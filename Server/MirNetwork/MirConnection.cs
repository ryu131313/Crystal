using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Net.Sockets;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Storage;
using Server.MirObjects;
using C = ClientPackets;
using S = ServerPackets;
using System.Text.RegularExpressions;
using Server.Utils;

namespace Server.MirNetwork
{
    public enum GameStage { None, Login, Select, Game, Observer, Disconnected }

    public class MirConnection
    {
        protected static Envir Envir
        {
            get { return Envir.Main; }
        }

        protected static MessageQueue MessageQueue
        {
            get { return MessageQueue.Instance; }
        }

        public readonly int SessionID;
        public readonly string IPAddress;

        public GameStage Stage;
        /// <summary>Index of the packet being (or last) handled; read by WorldErrors.Describe.</summary>
        public short LastPacketIndex = -1;

        /// <summary>X1-18: how long a connection may stay connected without logging in.</summary>
        private const long PreLoginLimitMs = 5 * 60 * 1000;

        /// <summary>X1-19: failed logins on this connection; Envir.Login closes it (and briefly blocks the IP) at the limit.</summary>
        public int FailedLogins;
        /// <summary>A password check or hash for this connection (login, new account, password change) is running off the game
        /// thread (roadmap P8); further such packets are ignored until it completes.</summary>
        public volatile bool LoginPending;
        public const int MaxFailedLogins = 5;

        /// <summary>X1-12: early movement/attack/magic packets are retried every pass; cap the backlog per connection.</summary>
        private const int MaxRetryQueue = 64;
        private void RetryLater(Packet p)
        {
            if (_retryList.Count < MaxRetryQueue) _retryList.Enqueue(p); // beyond the cap the packet is dropped, as a slow client would lose it
        }

        private TcpClient _client;
        private readonly System.Net.Sockets.Socket _socket;
        private ConcurrentQueue<Packet> _receiveList;
        private ConcurrentQueue<Packet> _sendList; 
        private Queue<Packet> _retryList;

        private bool _disconnecting;
        public bool Connected;
        public bool Disconnecting
        {
            get { return _disconnecting; }
            set
            {
                if (_disconnecting == value) return;
                _disconnecting = value;
                TimeOutTime = Envir.Time + 500;
            }
        }
        public readonly long TimeConnected;
        public long TimeDisconnected, TimeOutTime;

        byte[] _rawData = new byte[0];
        byte[] _rawBytes = new byte[8 * 1024];

        public AccountInfo Account;
        public PlayerObject Player;

        public List<MirConnection> Observers = new List<MirConnection>();
        public MirConnection Observing;

        public List<ItemInfo> SentItemInfo = new List<ItemInfo>();
        public List<MonsterInfo> SentMonsterInfo = new List<MonsterInfo>();
        public List<NPCInfo> SentNPCInfo = new List<NPCInfo>();
        public List<QuestInfo> SentQuestInfo = new List<QuestInfo>();
        public List<RecipeInfo> SentRecipeInfo = new List<RecipeInfo>();
        public List<UserItem> SentChatItem = new List<UserItem>(); //TODO - Add Expiry time
        public List<MapInfo> SentMapInfo = new List<MapInfo>();
        public List<ulong> SentHeroInfo = new List<ulong>();
        public bool WorldMapSetupSent;
        public bool StorageSent;
        public bool HeroStorageSent;
        public Dictionary<long, DateTime> SentRankings = new Dictionary<long, DateTime>();

        private DateTime _dataCounterReset;
        private int _dataCounter;
        private FixedSizedQueue<Packet> _lastPackets;

        public MirConnection(int sessionID, TcpClient client)
        {
            SessionID = sessionID;
            IPAddress = Envir.EndpointIp(client.Client.RemoteEndPoint); // X1-30

            Envir.UpdateIPBlock(IPAddress, TimeSpan.FromSeconds(Settings.IPBlockSeconds));

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.IPAddressConnected), IPAddress));

            _client = client;
            _socket = client.Client; // kept after Disconnect nulls _client, so pending receives can still be completed
            _client.NoDelay = true;

            TimeConnected = Envir.Time;
            TimeOutTime = TimeConnected + Settings.TimeOut;

            _lastPackets = new FixedSizedQueue<Packet>(10);

            _receiveList = new ConcurrentQueue<Packet>();
            _sendList = new ConcurrentQueue<Packet>();
            _sendList.Enqueue(new S.Connected());
            _retryList = new Queue<Packet>();

            Connected = true;
            BeginReceive();
        }

        public void AddObserver(MirConnection c)
        {
            Observers.Add(c);

            if (c.Observing != null)
                c.Observing.Observers.Remove(c);
            c.Observing = this;

            c.Stage = GameStage.Observer;
        }

        private void BeginReceive()
        {
            if (!Connected) return;

            try
            {
                _client.Client.BeginReceive(_rawBytes, 0, _rawBytes.Length, SocketFlags.None, ReceiveData, _rawBytes);
            }
            catch
            {
                Disconnecting = true;
            }
        }

        private void ReceiveData(IAsyncResult result)
        {
            int dataRead;

            // Always complete the receive first, even when already disconnected: an aborted receive whose EndReceive is
            // never called surfaces later as an unobserved task exception (SocketException 995) and a crash file.
            try
            {
                dataRead = _socket.EndReceive(result);
            }
            catch
            {
                Disconnecting = true;
                return;
            }

            if (!Connected) return;

            if (dataRead == 0)
            {
                Disconnecting = true;
                return;
            }

            if (_dataCounterReset < Envir.Now)
            {
                _dataCounterReset = Envir.Now.AddSeconds(5);
                _dataCounter = 0;
            }

            _dataCounter++;

            // Disconnect (game thread) nulls _receiveList/_rawData while this IO thread may still be here: work on locals and
            // stop quietly once they are gone. Previously the resulting NullReferenceException reached the catch below and
            // IP-blocked a player for 24 h for disconnecting (roadmap P5 known defect).
            var receiveList = _receiveList;
            var temp = _rawData;
            if (receiveList == null || temp == null) return;

            try
            {
                byte[] rawBytes = result.AsyncState as byte[];

                var raw = new byte[dataRead + temp.Length];
                Buffer.BlockCopy(temp, 0, raw, 0, temp.Length);
                Buffer.BlockCopy(rawBytes, 0, raw, temp.Length, dataRead);

                Packet p;

                while ((p = Packet.ReceivePacket(raw, out raw)) != null)
                    receiveList.Enqueue(p);

                _rawData = raw;
                Envir.WakeLoop(); // a parked game loop picks the packets up within ~1 ms at most (roadmap P6)
            }
            catch
            {
                Envir.UpdateIPBlock(IPAddress, TimeSpan.FromHours(24));

                MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.IPAddressDisconnectedInvalidPacket), IPAddress));

                Disconnecting = true;
                return;
            }

            var maxPacket = Settings.LoadTestLoopbackMaxPacket > 0 && Envir.IsLoadTestExempt(IPAddress) ? Settings.LoadTestLoopbackMaxPacket : Settings.MaxPacket;
            if (_dataCounter > maxPacket)
            {
                Envir.UpdateIPBlock(IPAddress, TimeSpan.FromHours(24));

                List<string> packetList = new List<string>();

                while (_lastPackets.Count > 0)
                {
                    _lastPackets.TryDequeue(out Packet pkt);

                    Enum.TryParse<ClientPacketIds>((pkt?.Index ?? 0).ToString(), out ClientPacketIds cPacket);

                    packetList.Add(cPacket.ToString());
                }

                MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.IPAddressDisconnectedLargePackets), IPAddress, String.Join(",", packetList.Distinct())));

                Disconnecting = true;
                return;
            }

            BeginReceive();
        }
        private void BeginSend(byte[] data)
        {
            if (!Connected || data.Length == 0) return;

            // P9-09: hand the array to a sender thread. Arrays still queued there keep later sends on that path after the
            // switch is turned off, so a direct send can never overtake them.
            if (Settings.SendThreads > 0 || Volatile.Read(ref _queuedArrays) > 0)
            {
                QueueSend(data);
                return;
            }

            //Interlocked.Add(ref Network.Sent, data.Length);

            try
            {
                _client.Client.BeginSend(data, 0, data.Length, SocketFlags.None, SendData, Disconnecting);
            }
            catch
            {
                Disconnecting = true;
            }
        }
        private void SendData(IAsyncResult result)
        {
            try
            {
                _client.Client.EndSend(result);
            }
            catch
            { }
        }

        #region Sender thread path (P9-09, [Optimisation] SendThreads)

        /// <summary>Queued-but-unsent bytes above which the client counts as too slow and is disconnected.</summary>
        private const long MaxQueuedBytes = 4 * 1024 * 1024;
        private const int MaxArraysPerSend = 64;

        // Written by any thread that sends (game thread; a WinForms kick), read by the sender side.
        private readonly ConcurrentQueue<byte[]> _outbox = new ConcurrentQueue<byte[]>();
        private int _queuedArrays;          // queued + in flight (Interlocked)
        private long _queuedBytes;          // queued + in flight (Interlocked)
        private volatile bool _sendFaulted; // a send failed or the client fell too far behind; the game thread disconnects
        private int _sendState;             // 0 idle, 1 owned by a sender thread or a send completion (Interlocked)
        private int _closed;                // 1 once the sender side has disposed the socket
        private long _closeDeadline;        // Environment.TickCount64; written by Disconnect before CloseWhenDrained

        // Owned by whoever holds _sendState.
        private SocketAsyncEventArgs _sendArgs;
        private readonly List<ArraySegment<byte>> _sendSegments = new List<ArraySegment<byte>>();
        private int _inFlightArrays, _inFlightBytes;

        /// <summary>Queues one flush's bytes for a sender thread, in order. Any thread; no locks.</summary>
        private void QueueSend(byte[] data)
        {
            if (_sendFaulted) return;

            if (Interlocked.Add(ref _queuedBytes, data.Length) > MaxQueuedBytes)
            {
                // Dropping a part of the stream corrupts it, so nothing more is sent: the game thread disconnects (Process).
                Interlocked.Add(ref _queuedBytes, -data.Length);
                _sendFaulted = true;
                MessageQueue.Enqueue($"[Optimisation] SendThreads: {IPAddress} has more than {MaxQueuedBytes / 1024} KB waiting to be sent; disconnecting a client that is too slow.");
                return;
            }

            Interlocked.Increment(ref _queuedArrays);
            _outbox.Enqueue(data);
            if (Interlocked.CompareExchange(ref _sendState, 1, 0) == 0)
                SocketSender.Schedule(this);
        }

        /// <summary>
        /// Sends what the outbox holds, one send in flight at a time, until it is empty or a send is pending (its completion
        /// continues). Called by the holder of <see cref="_sendState"/> only: a sender thread, or a send completion.
        /// </summary>
        internal void PumpSends()
        {
            while (true)
            {
                if (_outbox.IsEmpty)
                {
                    // Full fence (not Volatile.Write): the release must be visible before the re-check below, or an array
                    // queued meanwhile could see 1, skip scheduling, and be stranded. A queued array that found 1: take it back.
                    Interlocked.Exchange(ref _sendState, 0);
                    if (_outbox.IsEmpty || Interlocked.CompareExchange(ref _sendState, 1, 0) != 0) return;
                    continue;
                }
                if (!StartSend()) return;
            }
        }

        /// <summary>Starts one send of up to <see cref="MaxArraysPerSend"/> queued arrays. True when it finished at once.</summary>
        private bool StartSend()
        {
            byte[] first = null;
            var bytes = 0;
            _sendSegments.Clear();
            while (_sendSegments.Count < MaxArraysPerSend && _outbox.TryDequeue(out var a))
            {
                first ??= a;
                _sendSegments.Add(new ArraySegment<byte>(a));
                bytes += a.Length;
            }
            _inFlightArrays = _sendSegments.Count;
            _inFlightBytes = bytes;

            if (_sendFaulted || Volatile.Read(ref _closed) == 1)
            {
                CompleteSend(true); // dropped: the stream is already broken or closed
                return true;
            }

            try
            {
                if (_sendArgs == null)
                {
                    _sendArgs = new SocketAsyncEventArgs();
                    _sendArgs.Completed += SendCompleted;
                }
                if (_sendSegments.Count == 1)
                {
                    _sendArgs.BufferList = null;
                    _sendArgs.SetBuffer(first, 0, first.Length);
                }
                else
                {
                    _sendArgs.SetBuffer(null, 0, 0);
                    _sendArgs.BufferList = _sendSegments;
                }
                SocketSender.CountSend(_inFlightArrays);
                if (_socket.SendAsync(_sendArgs)) return false;
            }
            catch (ObjectDisposedException)
            {
                CompleteSend(false);
                return true;
            }
            catch (Exception ex)
            {
                MessageQueue.Enqueue(ex);
                CompleteSend(false);
                return true;
            }

            CompleteSend(_sendArgs.SocketError == SocketError.Success && _sendArgs.BytesTransferred == bytes);
            return true;
        }

        private void SendCompleted(object sender, SocketAsyncEventArgs args)
        {
            // IO completion thread: nothing may escape (an unhandled exception here ends the process).
            try
            {
                CompleteSend(args.SocketError == SocketError.Success && args.BytesTransferred == _inFlightBytes);
                PumpSends();
            }
            catch (Exception ex)
            {
                _sendFaulted = true;
                MessageQueue.Enqueue(ex);
            }
        }

        private void CompleteSend(bool ok)
        {
            if (!ok) _sendFaulted = true;
            Interlocked.Add(ref _queuedBytes, -_inFlightBytes);
            Interlocked.Add(ref _queuedArrays, -_inFlightArrays);
            _inFlightArrays = 0;
            _inFlightBytes = 0;
            _sendSegments.Clear();
            // Let go of the sent arrays (a login burst can be MBs), so an idle connection holds none.
            if (_sendArgs != null)
            {
                if (_sendArgs.BufferList != null) _sendArgs.BufferList = null;
                else _sendArgs.SetBuffer(null, 0, 0);
            }
        }

        /// <summary>
        /// Sender thread, for a connection that <see cref="Disconnect"/> handed over: disposes the socket once every queued
        /// send has finished, or at the deadline. True when done.
        /// </summary>
        internal bool TryFinishClose(long now)
        {
            if (Volatile.Read(ref _queuedArrays) > 0 && now < Volatile.Read(ref _closeDeadline)) return false;
            Volatile.Write(ref _closed, 1);
            try
            {
                _socket.Dispose();
            }
            catch (Exception ex)
            {
                MessageQueue.Enqueue(ex);
            }
            return true;
        }

        #endregion

        public void Enqueue(Packet p)
        {
            if (p == null) return;
            if (_sendList != null && p != null)
                _sendList.Enqueue(p);

            if (!p.Observable) return;
            foreach (MirConnection c in Observers)
                c.Enqueue(p);
        }

        /// <summary>
        /// Encode-once cache (roadmap P9): bytes of packets already encoded during the current connection pass, keyed by
        /// instance. Game thread only: filled lazily in <see cref="Process"/> (never in <see cref="Enqueue"/>, which mob
        /// threads call) and cleared by <see cref="EndEncodePass"/> after each pass. Safe per Future Ideas/Packet-Mutation-Audit.md:
        /// no server code mutates a packet after its first send.
        /// </summary>
        private static readonly Dictionary<Packet, byte[]> EncodedThisPass = new Dictionary<Packet, byte[]>(ReferenceEqualityComparer.Instance);

        /// <summary>Drops the encode-once cache. Called by the game loop after every connection pass.</summary>
        internal static void EndEncodePass()
        {
            if (EncodedThisPass.Count == 0) return;
            var burst = EncodedThisPass.Count > EncodeCacheTrimAbove;
            EncodedThisPass.Clear();
            // Clear costs O(capacity) and capacity never shrinks, so give back the table after a burst (e.g. a server-wide broadcast).
            if (burst) EncodedThisPass.TrimExcess(EncodeCacheKeep);
        }

        private const int EncodeCacheTrimAbove = 8192;
        private const int EncodeCacheKeep = 1024;

        internal static byte[] Encode(Packet p)
        {
            if (!Settings.EncodeOnce)
            {
                LoopProfiler.CountEncode(false);
                return ToBytes(p);
            }
            if (!EncodedThisPass.TryGetValue(p, out var bytes))
            {
                bytes = ToBytes(p);
                EncodedThisPass[p] = bytes;
                LoopProfiler.CountEncode(false);
            }
            else
            {
                LoopProfiler.CountEncode(true);
                if (++_encodeHits % EncodeCheckEvery == 0)
                    CheckNotMutated(p, bytes);
            }
            return bytes;
        }

        // Guard for the audit's "no mutate-after-send" condition (a frozen flag on Packet would be a Shared change, so it waits
        // for roadmap C5): one cache hit in EncodeCheckEvery is re-encoded and compared; a mismatch is logged once per packet type.
        private const int EncodeCheckEvery = 256;
        private static long _encodeHits;
        private static readonly HashSet<short> MutatedPacketsLogged = new HashSet<short>();

        /// <summary>Returns false (and logs once per packet type) when the packet no longer encodes to the cached bytes.</summary>
        internal static bool CheckNotMutated(Packet p, byte[] cached)
        {
            if (ToBytes(p).AsSpan().SequenceEqual(cached)) return true;
            if (MutatedPacketsLogged.Add(p.Index))
                MessageQueue.Enqueue($"[Optimisation] EncodeOnce: {p.GetType().Name} changed after its first send in a pass; later recipients got the first bytes. Fix the sender, or set [Optimisation] EncodeOnce=False.");
            return false;
        }

        private static byte[] ToBytes(Packet p)
        {
            var e = p.GetPacketBytes();
            return e as byte[] ?? e.ToArray();
        }

        public void Process()
        {
            if (_client == null || !_client.Connected)
            {
                Disconnect(20);
                return;
            }

            if (_sendFaulted) Disconnecting = true; // P9-09: a sender-thread send failed or the client fell too far behind

            var handled = false;
            while (!_receiveList.IsEmpty && !Disconnecting)
            {
                Packet p;
                if (!_receiveList.TryDequeue(out p)) continue;

                _lastPackets.Enqueue(p);

                TimeOutTime = Envir.Time + Settings.TimeOut;
                LastPacketIndex = p.Index; // identity for isolated-error logs (roadmap P5)
                LoopProfiler.CountPacketIn();
                ProcessPacket(p);
                handled = true;

                if (_receiveList == null)
                    return;
            }

            // P9-08: a handler may have started a timer or set a flag Process acts on; with the idle cap the player could
            // otherwise sleep up to HumanIdleCapMs.
            if (handled && Settings.HumanIdleCapMs > 0 && Player != null) Player.OperateTime = 0;

            while (_retryList.Count > 0)
                _receiveList.Enqueue(_retryList.Dequeue());

            if (Envir.Time > TimeOutTime)
            {
                Disconnect(21);
                return;
            }

            // X1-18: a connection that never logs in used to stay forever on KeepAlives, so a few IPs could fill MaxUser.
            if ((Stage == GameStage.None || Stage == GameStage.Login) && Envir.Time > TimeConnected + PreLoginLimitMs)
            {
                Disconnect(21);
                return;
            }

            // P9-06: in game, world packets go out at the end of the iteration (FlushEndOfTick), after monsters, objects and maps
            // ran. Replies to packets this client just sent go out here, as before, so no reply waits for the world phase;
            // with a shared send window only a KeepAlive reply does.
            if (!Settings.FlushAtEnd || Stage != GameStage.Game || _flushNow || (handled && Settings.SendFlushMs <= 0)) Flush(true);
        }

        /// <summary>
        /// End-of-iteration send (P9-06, [Optimisation] FlushAtEnd). <paramref name="tick"/> is the shared SendFlushMs tick: off
        /// it, packets wait in the pending list unless a KeepAlive reply or a full list forces the send. Game thread only.
        /// </summary>
        internal void FlushEndOfTick(bool tick)
        {
            if (Stage == GameStage.Game) Flush(tick);
        }

        /// <summary>Pending packets above which a connection sends even off the shared tick.</summary>
        private const int FlushAtPending = 256;
        private const int PendingTrimAbove = 1024;
        private const int PendingKeep = 64;

        /// <summary>
        /// Moves queued packets into the pending list (dropping superseded state packets when SendCoalesce is on) and, when
        /// <paramref name="due"/>, sends the pending list as one array. Game thread only: encodes through the per-pass cache.
        /// </summary>
        private void Flush(bool due, bool useEncodeCache = true)
        {
            var queue = _sendList;
            if (queue == null) return;

            while (queue.TryDequeue(out var p))
            {
                if (p == null) continue;
                if (Settings.SendCoalesce) Supersede(p);
                _pending.Add(p);
            }

            if (_pending.Count == 0) return;
            if (!due && !_flushNow && _pending.Count < FlushAtPending) return;
            _flushNow = false;

            // Encode first, then copy into one exact-size array: no List<byte> growth and no ToArray copy.
            // The array is handed to the socket, so it can't be reused; the scratch list only holds references.
            byte[] data;
            var encodeStart = Stopwatch.GetTimestamp();
            try
            {
                var total = 0;
                var batchMoves = Settings.MoveBatch && Stage != GameStage.None && Stage != GameStage.Login && Stage != GameStage.Select;
                for (int i = 0; i < _pending.Count; i++)
                {
                    var p = _pending[i];
                    if (p == null) continue; // superseded (SendCoalesce)
                    byte[] bytes;
                    if (batchMoves && TryBatchMoves(ref i))
                        bytes = MoveBatchScratch.ToFrame(); // per connection, so never through the shared encode cache
                    else
                        bytes = useEncodeCache ? Encode(p) : ToBytes(p);
                    SendScratch.Add(bytes);
                    total += bytes.Length;
                }

                data = new byte[total];
                var offset = 0;
                for (int i = 0; i < SendScratch.Count; i++)
                {
                    var bytes = SendScratch[i];
                    Buffer.BlockCopy(bytes, 0, data, offset, bytes.Length);
                    offset += bytes.Length;
                }

                LoopProfiler.CountPacketsOut(SendScratch.Count, total);
            }
            finally
            {
                // Always cleared, so an isolated encode failure can't leak this connection's bytes into the next flush.
                SendScratch.Clear();
                _pending.Clear();
                // Give back the capacity a burst (login object list, mass broadcast) left behind, as EndEncodePass does.
                if (_pending.Capacity > PendingTrimAbove) _pending.Capacity = PendingKeep;
                if (_coalesceIndex != null && _coalesceIndex.Count > 0)
                {
                    if (_coalesceIndex.Count > PendingTrimAbove) _coalesceIndex = null;
                    else _coalesceIndex.Clear();
                }
            }

            var sendStart = Stopwatch.GetTimestamp();
            BeginSend(data);
            LoopProfiler.CountSendTiming(sendStart - encodeStart, Stopwatch.GetTimestamp() - sendStart);
        }

        /// <summary>
        /// MoveBatch (P9-08): when <see cref="_pending"/>[<paramref name="i"/>] starts a run of two or more turn/walk/run
        /// packets (superseded nulls inside the run are skipped), fills <see cref="MoveBatchScratch"/> with the run, moves
        /// <paramref name="i"/> to its last packet and returns true. The batch takes the run's place, so the order of every
        /// packet the client receives is unchanged. Game thread only.
        /// </summary>
        private bool TryBatchMoves(ref int i)
        {
            if (!AsMove(_pending[i], out _)) return false;

            var moves = MoveBatchScratch.Moves;
            moves.Clear();
            var j = i;
            for (; j < _pending.Count && moves.Count < S.ObjectMoveBatch.MaxMoves; j++)
            {
                var p = _pending[j];
                if (p == null) continue;
                if (!AsMove(p, out var move)) break;
                moves.Add(move);
            }

            if (moves.Count < 2) return false;
            LoopProfiler.CountBatchedMoves(moves.Count);
            i = j - 1;
            return true;
        }

        private static bool AsMove(Packet p, out S.ObjectMoveBatch.Move move)
        {
            uint id; Point location; MirDirection direction; byte kind;
            switch (p.Index)
            {
                case (short)ServerPacketIds.ObjectTurn: { var t = (S.ObjectTurn)p; id = t.ObjectID; location = t.Location; direction = t.Direction; kind = S.ObjectMoveBatch.Turn; break; }
                case (short)ServerPacketIds.ObjectWalk: { var w = (S.ObjectWalk)p; id = w.ObjectID; location = w.Location; direction = w.Direction; kind = S.ObjectMoveBatch.Walk; break; }
                case (short)ServerPacketIds.ObjectRun: { var r = (S.ObjectRun)p; id = r.ObjectID; location = r.Location; direction = r.Direction; kind = S.ObjectMoveBatch.Run; break; }
                default: move = default; return false;
            }

            // The batch carries coordinates as ushort and the direction in 3 bits; anything else goes as the single packet.
            if (location.X < 0 || location.Y < 0 || location.X > ushort.MaxValue || location.Y > ushort.MaxValue || (byte)direction > 7)
            {
                move = default;
                return false;
            }

            move = new S.ObjectMoveBatch.Move { Kind = kind, ObjectID = id, Location = location, Direction = direction };
            return true;
        }

        /// <summary>The batch being built by <see cref="TryBatchMoves"/>; encoded at once, so one instance serves every flush (game thread).</summary>
        private static readonly S.ObjectMoveBatch MoveBatchScratch = new S.ObjectMoveBatch();

        /// <summary>
        /// SendCoalesce (P9-06): when <paramref name="p"/> is a "latest state" packet and an older one for the same object is
        /// still pending, the older one is nulled in place. The newer one keeps its later position, so nothing moves earlier.
        /// Only packets whose client handlers just overwrite state (GameScene.ProcessPacket and the web client) are listed.
        /// </summary>
        private void Supersede(Packet p)
        {
            uint objectId;
            switch (p.Index)
            {
                case (short)ServerPacketIds.ObjectHealth: objectId = ((S.ObjectHealth)p).ObjectID; break;
                case (short)ServerPacketIds.ObjectColourChanged: objectId = ((S.ObjectColourChanged)p).ObjectID; break;
                case (short)ServerPacketIds.ObjectPoisoned: objectId = ((S.ObjectPoisoned)p).ObjectID; break;
                case (short)ServerPacketIds.ObjectName: objectId = ((S.ObjectName)p).ObjectID; break;
                default: return;
            }

            var key = ((long)(ushort)p.Index << 32) | objectId;
            _coalesceIndex ??= new Dictionary<long, int>();
            if (_coalesceIndex.TryGetValue(key, out var at))
            {
                _pending[at] = null;
                LoopProfiler.CountCoalesced();
            }
            _coalesceIndex[key] = _pending.Count; // the index p is about to get
        }

        /// <summary>Packets drained from the send queue and not yet sent (game thread only; see <see cref="Flush"/>).</summary>
        private readonly List<Packet> _pending = new List<Packet>();
        /// <summary>SendCoalesce: (packet id, object id) → index in <see cref="_pending"/>; cleared after every send.</summary>
        private Dictionary<long, int> _coalesceIndex;
        /// <summary>Send at the next flush even off the shared tick (a KeepAlive reply). Game thread only.</summary>
        private bool _flushNow;

        /// <summary>Encoded packets of the connection being flushed. Game thread only (Process); cleared after every flush.</summary>
        private static readonly List<byte[]> SendScratch = new List<byte[]>();
        private void ProcessPacket(Packet p)
        {
            if (p == null || Disconnecting) return;

            switch (p.Index)
            {
                case (short)ClientPacketIds.ClientVersion:
                    ClientVersion((C.ClientVersion) p);
                    break;
                case (short)ClientPacketIds.Disconnect:
                    Disconnect(22);
                    break;
                case (short)ClientPacketIds.KeepAlive: // Keep Alive
                    ClientKeepAlive((C.KeepAlive)p);
                    break;
                case (short)ClientPacketIds.NewAccount:
                    NewAccount((C.NewAccount) p);
                    break;
                case (short)ClientPacketIds.ChangePassword:
                    ChangePassword((C.ChangePassword) p);
                    break;
                case (short)ClientPacketIds.UnlockStorage:
                    UnlockStorage((C.UnlockStorage)p);
                    break;
                case (short)ClientPacketIds.SetStoragePassword:
                    SetStoragePassword((C.SetStoragePassword)p);
                    break;
                case (short)ClientPacketIds.RemoveStoragePassword:
                    RemoveStoragePassword((C.RemoveStoragePassword)p);
                    break;
                case (short)ClientPacketIds.Login:
                    Login((C.Login) p);
                    break;
                case (short)ClientPacketIds.NewCharacter:
                    NewCharacter((C.NewCharacter) p);
                    break;
                case (short)ClientPacketIds.DeleteCharacter:
                    DeleteCharacter((C.DeleteCharacter) p);
                    break;
                case (short)ClientPacketIds.StartGame:
                    StartGame((C.StartGame) p);
                    break;
                case (short)ClientPacketIds.LogOut:
                    LogOut();
                    break;
                case (short)ClientPacketIds.Turn:
                    Turn((C.Turn) p);
                    break;
                case (short)ClientPacketIds.Walk:
                    Walk((C.Walk) p);
                    break;
                case (short)ClientPacketIds.Run:
                    Run((C.Run) p);
                    break;
                case (short)ClientPacketIds.Chat:
                    Chat((C.Chat) p);
                    break;
                case (short)ClientPacketIds.MoveItem:
                    MoveItem((C.MoveItem) p);
                    break;
                case (short)ClientPacketIds.StoreItem:
                    StoreItem((C.StoreItem) p);
                    break;
                case (short)ClientPacketIds.DepositRefineItem:
                    DepositRefineItem((C.DepositRefineItem)p);
                    break;
                case (short)ClientPacketIds.RetrieveRefineItem:
                    RetrieveRefineItem((C.RetrieveRefineItem)p);
                    break;
                case (short)ClientPacketIds.RefineCancel:
                    RefineCancel((C.RefineCancel)p);
                    break;
                case (short)ClientPacketIds.RefineItem:
                    RefineItem((C.RefineItem)p);
                    break;
                case (short)ClientPacketIds.CheckRefine:
                    CheckRefine((C.CheckRefine)p);
                    break;
                case (short)ClientPacketIds.ReplaceWedRing:
                    ReplaceWedRing((C.ReplaceWedRing)p);
                    break;
                case (short)ClientPacketIds.DepositTradeItem:
                    DepositTradeItem((C.DepositTradeItem)p);
                    break;
                case (short)ClientPacketIds.RetrieveTradeItem:
                    RetrieveTradeItem((C.RetrieveTradeItem)p);
                    break;
                case (short)ClientPacketIds.TakeBackItem:
                    TakeBackItem((C.TakeBackItem) p);
                    break;
                case (short)ClientPacketIds.MergeItem:
                    MergeItem((C.MergeItem) p);
                    break;
                case (short)ClientPacketIds.EquipItem:
                    EquipItem((C.EquipItem) p);
                    break;
                case (short)ClientPacketIds.RemoveItem:
                    RemoveItem((C.RemoveItem) p);
                    break;
                case (short)ClientPacketIds.RemoveSlotItem:
                    RemoveSlotItem((C.RemoveSlotItem)p);
                    break;
                case (short)ClientPacketIds.SplitItem:
                    SplitItem((C.SplitItem) p);
                    break;
                case (short)ClientPacketIds.UseItem:
                    UseItem((C.UseItem) p);
                    break;
                case (short)ClientPacketIds.DropItem:
                    DropItem((C.DropItem) p);
                    break;
                case (short)ClientPacketIds.TakeBackHeroItem:
                    TakeBackHeroItem((C.TakeBackHeroItem)p);
                    break;
                case (short)ClientPacketIds.TransferHeroItem:
                    TransferHeroItem((C.TransferHeroItem)p);
                    break;
                case (short)ClientPacketIds.DropGold:
                    DropGold((C.DropGold) p);
                    break;
                case (short)ClientPacketIds.PickUp:
                    PickUp();
                    break;
                case (short)ClientPacketIds.RequestMapInfo:
                    RequestMapInfo((C.RequestMapInfo)p);
                    break;
                case (short)ClientPacketIds.RequestMonsterInfo:
                    RequestMonsterInfo((C.RequestMonsterInfo)p);
                    break;
                case (short)ClientPacketIds.RequestNPCInfo:
                    RequestNPCInfo((C.RequestNPCInfo)p);
                    break;
                case (short)ClientPacketIds.RequestItemInfo:
                    RequestItemInfo((C.RequestItemInfo)p);
                    break;
                case (short)ClientPacketIds.TeleportToNPC:
                    TeleportToNPC((C.TeleportToNPC)p);
                    break;
                case (short)ClientPacketIds.SearchMap:
                    SearchMap((C.SearchMap)p);
                    break;
                case (short)ClientPacketIds.Inspect:
                    Inspect((C.Inspect)p);
                    break;
                case (short)ClientPacketIds.Observe:
                    Observe((C.Observe)p);
                    break;
                case (short)ClientPacketIds.ChangeAMode:
                    ChangeAMode((C.ChangeAMode)p);
                    break;
                case (short)ClientPacketIds.ChangePMode:
                    ChangePMode((C.ChangePMode)p);
                    break;
                case (short)ClientPacketIds.ChangeTrade:
                    ChangeTrade((C.ChangeTrade)p);
                    break;
                case (short)ClientPacketIds.Attack:
                    Attack((C.Attack)p);
                    break;
                case (short)ClientPacketIds.RangeAttack:
                    RangeAttack((C.RangeAttack)p);
                    break;
                case (short)ClientPacketIds.Harvest:
                    Harvest((C.Harvest)p);
                    break;
                case (short)ClientPacketIds.CallNPC:
                    CallNPC((C.CallNPC)p);
                    break;
                case (short)ClientPacketIds.BuyItem:
                    BuyItem((C.BuyItem)p);
                    break;
                case (short)ClientPacketIds.CraftItem:
                    CraftItem((C.CraftItem)p);
                    break;
                case (short)ClientPacketIds.SellItem:
                    SellItem((C.SellItem)p);
                    break;
                case (short)ClientPacketIds.RepairItem:
                    RepairItem((C.RepairItem)p);
                    break;
                case (short)ClientPacketIds.BuyItemBack:
                    BuyItemBack((C.BuyItemBack)p);
                    break;
                case (short)ClientPacketIds.SRepairItem:
                    SRepairItem((C.SRepairItem)p);
                    break;
                case (short)ClientPacketIds.MagicKey:
                    MagicKey((C.MagicKey)p);
                    break;
                case (short)ClientPacketIds.Magic:
                    Magic((C.Magic)p);
                    break;
                case (short)ClientPacketIds.SwitchGroup:
                    SwitchGroup((C.SwitchGroup)p);
                    return;
                case (short)ClientPacketIds.AddMember:
                    AddMember((C.AddMember)p);
                    return;
                case (short)ClientPacketIds.DellMember:
                    DelMember((C.DelMember)p);
                    return;
                case (short)ClientPacketIds.GroupInvite:
                    GroupInvite((C.GroupInvite)p);
                    return;
                case (short)ClientPacketIds.NewHero:
                    NewHero((C.NewHero)p);
                    break;
                case (short)ClientPacketIds.SetAutoPotValue:
                    SetAutoPotValue((C.SetAutoPotValue)p);
                    break;
                case (short)ClientPacketIds.SetAutoPotItem:
                    SetAutoPotItem((C.SetAutoPotItem)p);
                    break;
                case (short)ClientPacketIds.SetHeroBehaviour:
                    SetHeroBehaviour((C.SetHeroBehaviour)p);
                    break;
                case (short)ClientPacketIds.ChangeHero:
                    ChangeHero((C.ChangeHero)p);
                    break;
                case (short)ClientPacketIds.TownRevive:
                    TownRevive();
                    return;
                case (short)ClientPacketIds.SpellToggle:
                    SpellToggle((C.SpellToggle)p);
                    return;
                case (short)ClientPacketIds.ConsignItem:
                    ConsignItem((C.ConsignItem)p);
                    return;
                case (short)ClientPacketIds.MarketSearch:
                    MarketSearch((C.MarketSearch)p);
                    return;
                case (short)ClientPacketIds.MarketRefresh:
                    MarketRefresh();
                    return;
                case (short)ClientPacketIds.MarketPage:
                    MarketPage((C.MarketPage) p);
                    return;
                case (short)ClientPacketIds.MarketBuy:
                    MarketBuy((C.MarketBuy)p);
                    return;
                case (short)ClientPacketIds.MarketGetBack:
                    MarketGetBack((C.MarketGetBack)p);
                    return;
                case (short)ClientPacketIds.MarketSellNow:
                    MarketSellNow((C.MarketSellNow)p);
                    return;
                case (short)ClientPacketIds.RequestUserName:
                    RequestUserName((C.RequestUserName)p);
                    return;
                case (short)ClientPacketIds.RequestChatItem:
                    RequestChatItem((C.RequestChatItem)p);
                    return;
                case (short)ClientPacketIds.EditGuildMember:
                    EditGuildMember((C.EditGuildMember)p);
                    return;
                case (short)ClientPacketIds.EditGuildNotice:
                    EditGuildNotice((C.EditGuildNotice)p);
                    return;
                case (short)ClientPacketIds.GuildInvite:
                    GuildInvite((C.GuildInvite)p);
                    return;
                case (short)ClientPacketIds.RequestGuildInfo:
                    RequestGuildInfo((C.RequestGuildInfo)p);
                    return;
                case (short)ClientPacketIds.GuildNameReturn:
                    GuildNameReturn((C.GuildNameReturn)p);
                    return;
                case (short)ClientPacketIds.GuildStorageGoldChange:
                    GuildStorageGoldChange((C.GuildStorageGoldChange)p);
                    return;
                case (short)ClientPacketIds.GuildStorageItemChange:
                    GuildStorageItemChange((C.GuildStorageItemChange)p);
                    return;
                case (short)ClientPacketIds.GuildWarReturn:
                    GuildWarReturn((C.GuildWarReturn)p);
                    return;
                case (short)ClientPacketIds.MarriageRequest:
                    MarriageRequest((C.MarriageRequest)p);
                    return;
                case (short)ClientPacketIds.MarriageReply:
                    MarriageReply((C.MarriageReply)p);
                    return;
                case (short)ClientPacketIds.ChangeMarriage:
                    ChangeMarriage((C.ChangeMarriage)p);
                    return;
                case (short)ClientPacketIds.DivorceRequest:
                    DivorceRequest((C.DivorceRequest)p);
                    return;
                case (short)ClientPacketIds.DivorceReply:
                    DivorceReply((C.DivorceReply)p);
                    return;
                case (short)ClientPacketIds.AddMentor:
                    AddMentor((C.AddMentor)p);
                    return;
                case (short)ClientPacketIds.MentorReply:
                    MentorReply((C.MentorReply)p);
                    return;
                case (short)ClientPacketIds.AllowMentor:
                    AllowMentor((C.AllowMentor)p);
                    return;
                case (short)ClientPacketIds.CancelMentor:
                    CancelMentor((C.CancelMentor)p);
                    return;
                case (short)ClientPacketIds.TradeRequest:
                    TradeRequest((C.TradeRequest)p);
                    return;
                case (short)ClientPacketIds.TradeGold:
                    TradeGold((C.TradeGold)p);
                    return;
                case (short)ClientPacketIds.TradeReply:
                    TradeReply((C.TradeReply)p);
                    return;
                case (short)ClientPacketIds.TradeConfirm:
                    TradeConfirm((C.TradeConfirm)p);
                    return;
                case (short)ClientPacketIds.TradeCancel:
                    TradeCancel((C.TradeCancel)p);
                    return;
                case (short)ClientPacketIds.EquipSlotItem:
                    EquipSlotItem((C.EquipSlotItem)p);
                    break;
                case (short)ClientPacketIds.FishingCast:
                    FishingCast((C.FishingCast)p);
                    break;
                case (short)ClientPacketIds.FishingChangeAutocast:
                    FishingChangeAutocast((C.FishingChangeAutocast)p);
                    break;
                case (short)ClientPacketIds.AcceptQuest:
                    AcceptQuest((C.AcceptQuest)p);
                    break;
                case (short)ClientPacketIds.FinishQuest:
                    FinishQuest((C.FinishQuest)p);
                    break;
                case (short)ClientPacketIds.AbandonQuest:
                    AbandonQuest((C.AbandonQuest)p);
                    break;
                case (short)ClientPacketIds.ShareQuest:
                    ShareQuest((C.ShareQuest)p);
                    break;
                case (short)ClientPacketIds.AcceptReincarnation:
                    AcceptReincarnation();
                    break;
                case (short)ClientPacketIds.CancelReincarnation:
                     CancelReincarnation();
                    break;
                case (short)ClientPacketIds.CombineItem:
                    CombineItem((C.CombineItem)p);
                    break;
                case (short)ClientPacketIds.AwakeningNeedMaterials:
                    AwakeningNeedMaterials((C.AwakeningNeedMaterials)p);
                    break;
                case (short)ClientPacketIds.AwakeningLockedItem:
                    Enqueue(new S.AwakeningLockedItem { UniqueID = ((C.AwakeningLockedItem)p).UniqueID, Locked = ((C.AwakeningLockedItem)p).Locked });
                    break;
                case (short)ClientPacketIds.Awakening:
                    Awakening((C.Awakening)p);
                    break;
                case (short)ClientPacketIds.DisassembleItem:
                    DisassembleItem((C.DisassembleItem)p);
                    break;
                case (short)ClientPacketIds.DowngradeAwakening:
                    DowngradeAwakening((C.DowngradeAwakening)p);
                    break;
                case (short)ClientPacketIds.ResetAddedItem:
                    ResetAddedItem((C.ResetAddedItem)p);
                    break;
                case (short)ClientPacketIds.SendMail:
                    SendMail((C.SendMail)p);
                    break;
                case (short)ClientPacketIds.ReadMail:
                    ReadMail((C.ReadMail)p);
                    break;
                case (short)ClientPacketIds.CollectParcel:
                    CollectParcel((C.CollectParcel)p);
                    break;
                case (short)ClientPacketIds.DeleteMail:
                    DeleteMail((C.DeleteMail)p);
                    break;
                case (short)ClientPacketIds.LockMail:
                    LockMail((C.LockMail)p);
                    break;
                case (short)ClientPacketIds.MailLockedItem:
                    Enqueue(new S.MailLockedItem { UniqueID = ((C.MailLockedItem)p).UniqueID, Locked = ((C.MailLockedItem)p).Locked });
                    break;
                case (short)ClientPacketIds.MailCost:
                    MailCost((C.MailCost)p);
                    break;
                case (short)ClientPacketIds.RequestIntelligentCreatureUpdates:
                    RequestIntelligentCreatureUpdates((C.RequestIntelligentCreatureUpdates)p);
                    break;
                case (short)ClientPacketIds.UpdateIntelligentCreature:
                    UpdateIntelligentCreature((C.UpdateIntelligentCreature)p);
                    break;
                case (short)ClientPacketIds.IntelligentCreaturePickup:
                    IntelligentCreaturePickup((C.IntelligentCreaturePickup)p);
                    break;
                case (short)ClientPacketIds.AddFriend:
                    AddFriend((C.AddFriend)p);
                    break;
                case (short)ClientPacketIds.RemoveFriend:
                    RemoveFriend((C.RemoveFriend)p);
                    break;
                case (short)ClientPacketIds.RefreshFriends:
                    {
                        if (Stage != GameStage.Game) return;
                        Player.GetFriends();
                        break;
                    }
                case (short)ClientPacketIds.AddMemo:
                    AddMemo((C.AddMemo)p);
                    break;
                case (short)ClientPacketIds.GuildBuffUpdate:
                    GuildBuffUpdate((C.GuildBuffUpdate)p);
                    break;
                case (short)ClientPacketIds.GameshopBuy:
                    GameshopBuy((C.GameshopBuy)p);
                    return;
                case (short)ClientPacketIds.NPCConfirmInput:
                    NPCConfirmInput((C.NPCConfirmInput)p);
                    break;
                case (short)ClientPacketIds.ReportIssue:
                    ReportIssue((C.ReportIssue)p);
                    break;
                case (short)ClientPacketIds.GetRanking:
                    GetRanking((C.GetRanking)p);
                    break;
                case (short)ClientPacketIds.Opendoor:
                    Opendoor((C.Opendoor)p);
                    break;
                case (short)ClientPacketIds.GetRentedItems:
                    GetRentedItems();
                    break;
                case (short)ClientPacketIds.ItemRentalRequest:
                    ItemRentalRequest();
                    break;
                case (short)ClientPacketIds.ItemRentalFee:
                    ItemRentalFee((C.ItemRentalFee)p);
                    break;
                case (short)ClientPacketIds.ItemRentalPeriod:
                    ItemRentalPeriod((C.ItemRentalPeriod)p);
                    break;
                case (short)ClientPacketIds.DepositRentalItem:
                    DepositRentalItem((C.DepositRentalItem)p);
                    break;
                case (short)ClientPacketIds.RetrieveRentalItem:
                    RetrieveRentalItem((C.RetrieveRentalItem)p);
                    break;
                case (short)ClientPacketIds.CancelItemRental:
                    CancelItemRental();
                    break;
                case (short)ClientPacketIds.ItemRentalLockFee:
                    ItemRentalLockFee();
                    break;
                case (short)ClientPacketIds.ItemRentalLockItem:
                    ItemRentalLockItem();
                    break;
                case (short)ClientPacketIds.ConfirmItemRental:
                    ConfirmItemRental();
                    break;
                case (short)ClientPacketIds.GuildTerritoryPage:
                    GuildTerritoryPage((C.GuildTerritoryPage)p);
                    return;
                case (short)ClientPacketIds.PurchaseGuildTerritory:
                    PurchaseGuildTerritory((C.PurchaseGuildTerritory)p);
                    return;
                case (short)ClientPacketIds.DeleteItem:
                    DeleteItem((C.DeleteItem)p);
                    break;
                case (short)ClientPacketIds.SellItems:
                    SellItems((C.SellItems)p);
                    break;
                case (short)ClientPacketIds.RepairItems:
                    RepairItems((C.RepairItems)p);
                    break;
                case (short)ClientPacketIds.CollectAllParcels:
                    CollectAllParcels();
                    break;
                case (short)ClientPacketIds.ReadMails:
                    ReadMails((C.ReadMails)p);
                    break;
                case (short)ClientPacketIds.SortItems:
                    SortItems((C.SortItems)p);
                    break;
                case (short)ClientPacketIds.HeroSpellToggle:
                    HeroSpellToggle((C.HeroSpellToggle)p);
                    break;
                case (short)ClientPacketIds.RequestMapDetails:
                    RequestMapDetails((C.RequestMapDetails)p);
                    break;
                case (short)ClientPacketIds.RequestGuildMemberDetails:
                    RequestGuildMemberDetails();
                    break;
                case (short)ClientPacketIds.RequestGuildLog:
                    RequestGuildLog();
                    break;
                case (short)ClientPacketIds.GetGuildRanking:
                    GetGuildRanking((C.GetGuildRanking)p);
                    break;
                case (short)ClientPacketIds.FindMentor:
                    FindMentor((C.FindMentor)p);
                    break;
                case (short)ClientPacketIds.GuildTerritoryAction:
                    GuildTerritoryAction((C.GuildTerritoryAction)p);
                    break;
                case (short)ClientPacketIds.SetProfile:
                    SetProfile((C.SetProfile)p);
                    break;
                case (short)ClientPacketIds.GameshopGift:
                    GameshopGift((C.GameshopGift)p);
                    break;
                case (short)ClientPacketIds.MarketSearchEx:
                    MarketSearchEx((C.MarketSearchEx)p);
                    break;
                case (short)ClientPacketIds.MarketPriceHistory:
                    MarketPriceHistory((C.MarketPriceHistory)p);
                    break;
                case (short)ClientPacketIds.RequestSentMail:
                    RequestSentMail();
                    break;
                case (short)ClientPacketIds.HideSentMail:
                    HideSentMail((C.HideSentMail)p);
                    break;
                case (short)ClientPacketIds.FriendRequestReply:
                    FriendRequestReply((C.FriendRequestReply)p);
                    break;
                case (short)ClientPacketIds.SetAllowToggle:
                    SetAllowToggle((C.SetAllowToggle)p);
                    break;
                default:
                    MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.InvalidPacketReceived), p.Index));
                    break;
            }
        }

        public void SoftDisconnect(byte reason)
        {
            Stage = GameStage.Disconnected;
            TimeDisconnected = Envir.Time;
            
            lock (Envir.AccountLock)
            {
                if (Player != null)
                    Player.StopGame(reason);

                if (Account != null && Account.Connection == this)
                    Account.Connection = null;
            }

            Account = null;
        }
        public void Disconnect(byte reason)
        {
            if (!Connected) return;

            Connected = false;
            Stage = GameStage.Disconnected;
            TimeDisconnected = Envir.Time;

            lock (Envir.Connections)
                Envir.Connections.Remove(this);

            lock (Envir.AccountLock)
            {
                if (Player != null)
                    Player.StopGame(reason);

                if (Account != null && Account.Connection == this)
                    Account.Connection = null;
            }

            if (Observing != null)
                Observing.Observers.Remove(this);            

            Account = null;

            _sendList = null;
            _receiveList = null;
            _retryList = null;
            _rawData = null;

            if (_client != null)
            {
                // P9-09: sends still queued on a sender thread (S.Disconnect, a kick message) go out before the socket closes.
                if (Volatile.Read(ref _queuedArrays) > 0)
                {
                    Volatile.Write(ref _closeDeadline, Environment.TickCount64 + SocketSender.CloseDrainMs);
                    SocketSender.CloseWhenDrained(this);
                }
                else _client.Client.Dispose();
            }
            _client = null;
        }
        /// <summary>
        /// Last-resort cleanup after Disconnect threw part-way (roadmap P5 isolation, review M1): detaches the account so a
        /// new login is not blocked by this connection, and closes the socket. Never throws.
        /// </summary>
        public void ForceClose()
        {
            try
            {
                Connected = false;
                Stage = GameStage.Disconnected;
                lock (Envir.AccountLock)
                {
                    if (Account != null && Account.Connection == this)
                        Account.Connection = null;
                }
                Account = null;
                _client?.Client?.Dispose();
                _client = null;
            }
            catch (Exception ex)
            {
                MessageQueue.Enqueue(ex);
            }
        }

        public void SendDisconnect(byte reason)
        {
            if (!Connected)
            {
                Disconnecting = true;
                SoftDisconnect(reason);
                return;
            }
            
            Disconnecting = true;

            // P9-06: queued packets (a kick or ban message) go out before S.Disconnect. Off the game thread (a WinForms
            // editor, Known debt) the pending list can't be touched, so the old order stays there.
            // Encoded without the per-pass cache: this may run mid world phase (a GM kick, a login takeover), and cached
            // bytes would then outlive the pass they belong to.
            if (DesignStore.OnGameThread) Flush(true, useEncodeCache: false);
            BeginSend(ToBytes(new S.Disconnect { Reason = reason }));
            SoftDisconnect(reason);
        }
        public void CleanObservers()
        {
            foreach (MirConnection c in Observers)
            {
                c.Stage = GameStage.Login;
                c.Enqueue(new S.ReturnToLogin());
            }
        }

        private void ClientVersion(C.ClientVersion p)
        {
            if (Stage != GameStage.None) return;

            if (Settings.CheckVersion)
            {
                bool match = false;

                foreach (var hash in Settings.VersionHashes)
                {
                    if (Functions.CompareBytes(hash, p.VersionHash))
                    {
                        match = true;
                        break;
                    }
                }

                if (!match)
                {
                    Disconnecting = true;

                    Flush(true, useEncodeCache: false); // P9-06: anything queued goes out first (game thread: ProcessPacket)
                    BeginSend(ToBytes(new S.ClientVersion { Result = 0 }));
                    SoftDisconnect(10);
                    MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.PlayerDisconnectedWrongClientVersion), SessionID));
                    return;
                }
            }

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.ClientVersionMatched), SessionID, IPAddress));
            Enqueue(new S.ClientVersion { Result = 1 });

            Stage = GameStage.Login;
        }
        private void ClientKeepAlive(C.KeepAlive p)
        {
            Enqueue(new S.KeepAlive
            {
                Time = p.Time
            });
            _flushNow = true; // P9-06: the reply skips the shared send window, so the client's ping stays honest
        }
        private void NewAccount(C.NewAccount p)
        {
            if (Stage != GameStage.Login) return;

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.NewAccountBeingCreated), SessionID, IPAddress));
            Envir.NewAccount(p, this);
        }
        private void ChangePassword(C.ChangePassword p)
        {
            if (Stage != GameStage.Login) return;

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.PasswordBeingChanged), SessionID, IPAddress));
            Envir.ChangePassword(p, this);
        }
        private void UnlockStorage(C.UnlockStorage p)
        {
            if (Stage != GameStage.Game || Player == null || Account == null)
            {
                Enqueue(new S.StorageUnlockResult { Result = 3, HasPassword = Account != null && Account.HasStoragePassword });
                return;
            }

            if (!CanAccessStorageNpc())
            {
                Enqueue(new S.StorageUnlockResult { Result = 3, HasPassword = Account.HasStoragePassword });
                return;
            }

            if (!Account.HasStoragePassword)
            {
                Player.SetStorageUnlocked(true);
                Enqueue(new S.StorageUnlockResult { Result = 4, HasPassword = false });
                return;
            }

            if (!Envir.IsPasswordValid(p.Password))
            {
                Enqueue(new S.StorageUnlockResult { Result = 1, HasPassword = true });
                return;
            }

            if (!Account.ValidateStoragePassword(p.Password))
            {
                Enqueue(new S.StorageUnlockResult { Result = 2, HasPassword = true });
                return;
            }

            Player.SetStorageUnlocked(true);
            Enqueue(new S.StorageUnlockResult { Result = 0, HasPassword = true });
            Player.SendStorage();
        }
        private void SetStoragePassword(C.SetStoragePassword p)
        {
            if (Stage != GameStage.Game || Player == null || Account == null)
            {
                Enqueue(new S.StoragePasswordResult { Result = 0, Removing = false, HasPassword = Account != null && Account.HasStoragePassword, LastSetTime = Account?.StoragePasswordLastSet ?? DateTime.MinValue });
                return;
            }

            if (!CanAccessStorageNpc())
            {
                Enqueue(new S.StoragePasswordResult { Result = 0, Removing = false, HasPassword = Account.HasStoragePassword, LastSetTime = Account.StoragePasswordLastSet });
                return;
            }

            if (!Envir.IsPasswordValid(p.NewPassword))
            {
                Enqueue(new S.StoragePasswordResult { Result = 3, Removing = false, HasPassword = Account.HasStoragePassword, LastSetTime = Account.StoragePasswordLastSet });
                return;
            }

            if (Account.HasStoragePassword)
            {
                if (!Envir.IsPasswordValid(p.CurrentPassword))
                {
                    Enqueue(new S.StoragePasswordResult { Result = 1, Removing = false, HasPassword = true, LastSetTime = Account.StoragePasswordLastSet });
                    return;
                }

                if (!Account.ValidateStoragePassword(p.CurrentPassword))
                {
                    Enqueue(new S.StoragePasswordResult { Result = 2, Removing = false, HasPassword = true, LastSetTime = Account.StoragePasswordLastSet });
                    return;
                }
            }

            Account.StoragePassword = p.NewPassword;
            Account.StoragePasswordLastSet = Envir.Now;
            Player.SetStorageUnlocked(true);
            Enqueue(new S.StoragePasswordResult { Result = 4, Removing = false, HasPassword = true, LastSetTime = Account.StoragePasswordLastSet });
        }
        private void RemoveStoragePassword(C.RemoveStoragePassword p)
        {
            if (Stage != GameStage.Game || Player == null || Account == null)
            {
                Enqueue(new S.StoragePasswordResult { Result = 0, Removing = true, HasPassword = Account != null && Account.HasStoragePassword, LastSetTime = Account?.StoragePasswordLastSet ?? DateTime.MinValue });
                return;
            }

            if (!CanAccessStorageNpc())
            {
                Enqueue(new S.StoragePasswordResult { Result = 0, Removing = true, HasPassword = Account.HasStoragePassword, LastSetTime = Account.StoragePasswordLastSet });
                return;
            }

            if (!Account.HasStoragePassword)
            {
                Enqueue(new S.StoragePasswordResult { Result = 5, Removing = true, HasPassword = false, LastSetTime = DateTime.MinValue });
                return;
            }

            if (!Envir.IsPasswordValid(p.CurrentPassword))
            {
                Enqueue(new S.StoragePasswordResult { Result = 1, Removing = true, HasPassword = true, LastSetTime = Account.StoragePasswordLastSet });
                return;
            }

            if (!Account.ValidateStoragePassword(p.CurrentPassword))
            {
                Enqueue(new S.StoragePasswordResult { Result = 2, Removing = true, HasPassword = true, LastSetTime = Account.StoragePasswordLastSet });
                return;
            }

            Account.ClearStoragePassword();
            Player.SetStorageUnlocked(true);
            Enqueue(new S.StoragePasswordResult { Result = 4, Removing = true, HasPassword = false, LastSetTime = DateTime.MinValue });
        }
        private bool CanAccessStorageNpc()
        {
            if (Player == null) return false;

            if (Player.NPCPage == null || !String.Equals(Player.NPCPage.Key, NPCScript.StorageKey, StringComparison.CurrentCultureIgnoreCase))
                return false;

            NPCObject ob = null;
            for (int i = 0; i < Player.CurrentMap.NPCs.Count; i++)
            {
                if (Player.CurrentMap.NPCs[i].ObjectID != Player.NPCObjectID) continue;
                ob = Player.CurrentMap.NPCs[i];
                break;
            }

            return ob != null && Functions.InRange(ob.CurrentLocation, Player.CurrentLocation, Globals.DataRange);
        }
        private void Login(C.Login p)
        {
            if (Stage != GameStage.Login) return;

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.UserLoggingIn), SessionID, IPAddress));
            Envir.Login(p, this);
        }
        private void NewCharacter(C.NewCharacter p)
        {
            if (Stage != GameStage.Select) return;

            Envir.NewCharacter(p, this, Account.AdminAccount);
        }
        private void DeleteCharacter(C.DeleteCharacter p)
        {
            if (Stage != GameStage.Select) return;
            
            if (!Settings.AllowDeleteCharacter)
            {
                Enqueue(new S.DeleteCharacter { Result = 0 });
                return;
            }

            CharacterInfo temp = null;
            

            for (int i = 0; i < Account.Characters.Count; i++)
			{
			    if (Account.Characters[i].Index != p.CharacterIndex) continue;

			    temp = Account.Characters[i];
			    break;
			}

            if (temp == null)
            {
                Enqueue(new S.DeleteCharacter { Result = 1 });
                return;
            }

            temp.Deleted = true;
            temp.DeleteDate = Envir.Now;
            Envir.RemoveRank(temp);
            Enqueue(new S.DeleteCharacterSuccess { CharacterIndex = temp.Index });
        }
        private void StartGame(C.StartGame p)
        {
            if (Stage != GameStage.Select) return;

            if (!Settings.AllowStartGame && (Account == null || (Account != null && !Account.AdminAccount)))
            {
                Enqueue(new S.StartGame { Result = 0 });
                return;
            }

            if (Account == null)
            {
                Enqueue(new S.StartGame { Result = 1 });
                return;
            }


            CharacterInfo info = null;

            for (int i = 0; i < Account.Characters.Count; i++)
            {
                if (Account.Characters[i].Index != p.CharacterIndex) continue;

                info = Account.Characters[i];
                break;
            }
            if (info == null || info.Deleted) // X1-21: deleted characters could still be played until archived
            {
                Enqueue(new S.StartGame { Result = 2 });
                return;
            }

            if (info.Banned)
            {
                if (info.ExpiryDate > Envir.Now)
                {
                    Enqueue(new S.StartGameBanned { Reason = info.BanReason, ExpiryDate = info.ExpiryDate });
                    return;
                }
                info.Banned = false;
            }
            info.BanReason = string.Empty;
            info.ExpiryDate = DateTime.MinValue;

            long delay = (long) (Envir.Now - info.LastLogoutDate).TotalMilliseconds;


            //if (delay < Settings.RelogDelay)
            //{
            //    Enqueue(new S.StartGameDelay { Milliseconds = Settings.RelogDelay - delay });
            //    return;
            //}

            Player = new PlayerObject(info, this);
            Player.StartGame();
        }

        public void LogOut()
        {
            if (Stage == GameStage.Game)
            {
                if (Envir.Time < Player.LogTime)
                {
                    Enqueue(new S.LogOutFailed());
                    return;
                }

                Player.StopGame(23);

                Stage = GameStage.Select;
                Player = null;

                Enqueue(new S.LogOutSuccess { Characters = Account.GetSelectInfo() });
                Enqueue(Envir.SelectDetailsPacket(Account));
            }
            else if (Stage == GameStage.Observer)
            {
                if (Observing != null)
                    Observing.Observers.Remove(this);

                Observing = null;
                Stage = GameStage.Select;

                Enqueue(new S.LogOutSuccess { Characters = Account.GetSelectInfo() });
                Enqueue(Envir.SelectDetailsPacket(Account));
            }
        }

        private void Turn(C.Turn p)
        {
            if (Stage != GameStage.Game) return;
            if (p.Direction > MirDirection.UpLeft) return; // X1-27: never store or broadcast an invalid direction

            if (Player.ActionTime > Envir.Time)
                RetryLater(p);
            else
                Player.Turn(p.Direction);
        }
        private void Walk(C.Walk p)
        {
            if (Stage != GameStage.Game) return;
            if (p.Direction > MirDirection.UpLeft) return; // X1-27: never store or broadcast an invalid direction

            if (Player.ActionTime > Envir.Time)
                RetryLater(p);
            else
                Player.Walk(p.Direction);
        }
        private void Run(C.Run p)
        {
            if (Stage != GameStage.Game) return;
            if (p.Direction > MirDirection.UpLeft) return; // X1-27: never store or broadcast an invalid direction

            if (Player.ActionTime > Envir.Time)
                RetryLater(p);
            else
                Player.Run(p.Direction);
        }
        
        private void Chat(C.Chat p)
        {
            if (p.Message.Length > Globals.MaxChatLength)
            {
                SendDisconnect(2);
                return;
            }

            if (Stage == GameStage.Game)
            {
                Player.Chat(p.Message, p.LinkedItems);
            }
            else if (Stage == GameStage.Observer)
            {
                if (!p.Message.StartsWith("@")) return;

                string message = p.Message.Remove(0, 1);
                string[] parts = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return;

                if (string.Equals(parts[0], "OBSERVE", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length < 2) return;

                    PlayerObject player = Envir.GetPlayer(parts[1]);
                    if (player == null) return;
                    if ((!player.AllowObserve || !Settings.AllowObserve) &&
                        (Account == null || !Account.AdminAccount)) return;

                    player.AddObserver(this);
                }
            }
        }

        private void MoveItem(C.MoveItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.MoveItem(p.Grid, p.From, p.To);
        }
        private void StoreItem(C.StoreItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.StoreItem(p.From, p.To);
        }

        private void DepositRefineItem(C.DepositRefineItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.DepositRefineItem(p.From, p.To);
        }

        private void RetrieveRefineItem(C.RetrieveRefineItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.RetrieveRefineItem(p.From, p.To);
        }

        private void RefineCancel(C.RefineCancel p)
        {
            if (Stage != GameStage.Game) return;

            Player.RefineCancel();
        }

        private void RefineItem(C.RefineItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.RefineItem(p.UniqueID);
        }

        private void CheckRefine(C.CheckRefine p)
        {
            if (Stage != GameStage.Game) return;

            Player.CheckRefine(p.UniqueID);
        }

        private void ReplaceWedRing(C.ReplaceWedRing p)
        {
            if (Stage != GameStage.Game) return;

            Player.ReplaceWeddingRing(p.UniqueID);
        }

        private void DepositTradeItem(C.DepositTradeItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.DepositTradeItem(p.From, p.To);
        }
        
        private void RetrieveTradeItem(C.RetrieveTradeItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.RetrieveTradeItem(p.From, p.To);
        }
        private void TakeBackItem(C.TakeBackItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.TakeBackItem(p.From, p.To);
        }
        private void MergeItem(C.MergeItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.MergeItem(p.GridFrom, p.GridTo, p.IDFrom, p.IDTo);
        }
        private void EquipItem(C.EquipItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.EquipItem(p.Grid, p.UniqueID, p.To);
        }
        private void RemoveItem(C.RemoveItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.RemoveItem(p.Grid, p.UniqueID, p.To);
        }
        private void RemoveSlotItem(C.RemoveSlotItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.RemoveSlotItem(p.Grid, p.UniqueID, p.To, p.GridTo, p.FromUniqueID);
        }
        private void SplitItem(C.SplitItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.SplitItem(p.Grid, p.UniqueID, p.Count);
        }
        private void UseItem(C.UseItem p)
        {
            if (Stage != GameStage.Game) return;

            switch (p.Grid)
            {
                case MirGridType.Inventory:
                    Player.UseItem(p.UniqueID);
                    break;
                case MirGridType.HeroInventory:
                    Player.HeroUseItem(p.UniqueID);
                    break;
            }            
        }
        private void DropItem(C.DropItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.DropItem(p.UniqueID, p.Count, p.HeroInventory);
        }

        private void TakeBackHeroItem(C.TakeBackHeroItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.TakeBackHeroItem(p.From, p.To);
        }

        private void TransferHeroItem(C.TransferHeroItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.TransferHeroItem(p.From, p.To);
        }
        private void DropGold(C.DropGold p)
        {
            if (Stage != GameStage.Game) return;

            Player.DropGold(p.Amount);
        }
        private void PickUp()
        {
            if (Stage != GameStage.Game) return;

            Player.PickUp();
        }

        private void RequestMapInfo(C.RequestMapInfo p)
        {
            if (Stage != GameStage.Game) return;

            Player.RequestMapInfo(p.MapIndex);
        }

        private void RequestMonsterInfo(C.RequestMonsterInfo p)
        {
            if (Stage != GameStage.Game) return;

            Player.RequestMonsterInfo(p.MonsterIndex);
        }

        private void RequestNPCInfo(C.RequestNPCInfo p)
        {
            if (Stage != GameStage.Game) return;

            Player.RequestNPCInfo(p.NPCIndex);
        }

        private void RequestItemInfo(C.RequestItemInfo p)
        {
            if (Stage != GameStage.Game) return;

            Player.RequestItemInfo(p.ItemIndex);
        }

        private void TeleportToNPC(C.TeleportToNPC p)
        {
            if (Stage != GameStage.Game) return;

            Player.TeleportToNPC(p.ObjectID);
        }

        private void SearchMap(C.SearchMap p)
        {
            if (Stage != GameStage.Game) return;

            Player.SearchMap(p.Text);
        }
        private void Inspect(C.Inspect p)
        {
            if (Stage != GameStage.Game && Stage != GameStage.Observer) return;

            if (p.Ranking)
            {
                Envir.Inspect(this, (int)p.ObjectID);
            }
            else if (p.Hero)
            {
                Envir.InspectHero(this, (int)p.ObjectID);
            }
            else
            {
                Envir.Inspect(this, p.ObjectID);
            } 
        }
        private void Observe(C.Observe p)
        {
            if (Stage != GameStage.Game && Stage != GameStage.Observer) return;

            Envir.Observe(this, p.Name);
        }
        private void ChangeAMode(C.ChangeAMode p)
        {
            if (Stage != GameStage.Game) return;

            Player.AMode = p.Mode;

            Enqueue(new S.ChangeAMode {Mode = Player.AMode});
        }
        private void ChangePMode(C.ChangePMode p)
        {
            if (Stage != GameStage.Game) return;

            Player.PMode = p.Mode;

            Enqueue(new S.ChangePMode { Mode = Player.PMode });
        }
        private void ChangeTrade(C.ChangeTrade p)
        {
            if (Stage != GameStage.Game) return;

            Player.AllowTrade = p.AllowTrade;
            Player.SendAllowToggles();
        }
        private void Attack(C.Attack p)
        {
            if (Stage != GameStage.Game) return;
            if (p.Direction > MirDirection.UpLeft) return; // X1-27: never store or broadcast an invalid direction

            if (!Player.Dead && (Player.ActionTime > Envir.Time || Player.AttackTime > Envir.Time))
                RetryLater(p);
            else
                Player.Attack(p.Direction, p.Spell);
        }
        private void RangeAttack(C.RangeAttack p)
        {
            if (Stage != GameStage.Game) return;

            if (!Player.Dead && (Player.ActionTime > Envir.Time || Player.AttackTime > Envir.Time))
                RetryLater(p);
            else
                Player.RangeAttack(p.Direction, p.TargetLocation, p.TargetID);
        }
        private void Harvest(C.Harvest p)
        {
            if (Stage != GameStage.Game) return;

            if (!Player.Dead && Player.ActionTime > Envir.Time)
                RetryLater(p);
            else
                Player.Harvest(p.Direction);
        }

        private void CallNPC(C.CallNPC p)
        {
            if (Stage != GameStage.Game) return;

            if (p.Key.Length > 30) //No NPC Key should be that long.
            {
                SendDisconnect(2);
                return;
            }

            if (p.ObjectID == Envir.DefaultNPC.LoadedObjectID && Player.NPCObjectID == Envir.DefaultNPC.LoadedObjectID)
            {
                Player.CallDefaultNPC(p.Key);
                return;
            }

            if (p.ObjectID == uint.MaxValue)
            {
                Player.CallDefaultNPC(DefaultNPCType.Client, null);
                return;
            }

            Player.CallNPC(p.ObjectID, p.Key);
        }

        private void BuyItem(C.BuyItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.BuyItem(p.ItemIndex, p.Count, p.Type);
        }
        private void CraftItem(C.CraftItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.CraftItem(p.UniqueID, p.Count, p.Slots);
        }
        private void SellItem(C.SellItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.SellItem(p.UniqueID, p.Count);
        }
        private void RepairItem(C.RepairItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.RepairItem(p.UniqueID);
        }
        private void BuyItemBack(C.BuyItemBack p)
        {
            if (Stage != GameStage.Game) return;

           // Player.BuyItemBack(p.UniqueID, p.Count);
        }
        private void SRepairItem(C.SRepairItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.RepairItem(p.UniqueID, true);
        }
        private void MagicKey(C.MagicKey p)
        {
            if (Stage != GameStage.Game) return;

            HumanObject actor = Player;
            if (p.Key > 16 || p.OldKey > 16)
            {
                if (!Player.HeroSpawned || Player.Hero.Dead) return;
                actor = Player.Hero;
            }

            for (int i = 0; i < actor.Info.Magics.Count; i++)
            {
                UserMagic magic = actor.Info.Magics[i];
                if (magic.Spell != p.Spell)
                {
                    if (magic.Key == p.Key)
                        magic.Key = 0;
                    continue;
                }

                magic.Key = p.Key;
            }
        }
        private void Magic(C.Magic p)
        {
            if (Stage != GameStage.Game) return;
            if (p.Direction > MirDirection.UpLeft) return; // X1-27: never store or broadcast an invalid direction

            HumanObject actor = Player;
            if (Player.HeroSpawned && p.ObjectID == Player.Hero.ObjectID)
                actor = Player.Hero;

            if (actor.Dead) return;

            if (!actor.Dead && (actor.ActionTime > Envir.Time || actor.SpellTime > Envir.Time))
                RetryLater(p);
            else
                actor.BeginMagic(p.Spell, p.Direction, p.TargetID, p.Location, p.SpellTargetLock);
        }

        private void SwitchGroup(C.SwitchGroup p)
        {
            if (Stage != GameStage.Game) return;

            Player.SwitchGroup(p.AllowGroup);
        }
        private void AddMember(C.AddMember p)
        {
            if (Stage != GameStage.Game) return;

            Player.AddMember(p.Name);
        }
        private void DelMember(C.DelMember p)
        {
            if (Stage != GameStage.Game) return;

            Player.DelMember(p.Name);
        }
        private void GroupInvite(C.GroupInvite p)
        {
            if (Stage != GameStage.Game) return;

            Player.GroupInvite(p.AcceptInvite);
        }

        private void NewHero(C.NewHero p)
        {
            if (Stage != GameStage.Game) return;

            Player.NewHero(p);
        }

        private void SetAutoPotValue(C.SetAutoPotValue p)
        {
            if (Stage != GameStage.Game) return;

            Player.SetAutoPotValue(p.Stat, p.Value);
        }

        private void SetAutoPotItem(C.SetAutoPotItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.SetAutoPotItem(p.Grid, p.ItemIndex);
        }

        private void SetHeroBehaviour(C.SetHeroBehaviour p)
        {
            if (Stage != GameStage.Game) return;

            Player.SetHeroBehaviour(p.Behaviour);
        }

        private void ChangeHero(C.ChangeHero p)
        {
            if (Stage != GameStage.Game) return;

            Player.ChangeHero(p.ListIndex);
        }

        private void TownRevive()
        {
            if (Stage != GameStage.Game) return;

            Player.TownRevive();
        }

        private void SpellToggle(C.SpellToggle p)
        {
            if (Stage != GameStage.Game) return;

            if (p.canUse > SpellToggleState.None)
            {
                Player.SpellToggle(p.Spell, p.canUse);
                return;
            }
            if (Player.HeroSpawned)
                Player.Hero.SpellToggle(p.Spell, p.canUse);            
        }
        private void ConsignItem(C.ConsignItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.ConsignItem(p.UniqueID, p.Price, p.Type);
        }
        private void GuildTerritoryPage(C.GuildTerritoryPage p)
        {
            if (Stage != GameStage.Game) return;

            Player.GetGuildTerritories(p.Page);
        }

        private void PurchaseGuildTerritory(C.PurchaseGuildTerritory p)
        {
            if (Stage != GameStage.Game) return;

            Player.PurchaseGuildTerritory(p.Owner);
        }
        private void MarketSearch(C.MarketSearch p)
        {
            if (Stage != GameStage.Game) return;

            Player.UserMatch = p.Usermode;
            Player.MinShapes = p.MinShape;
            Player.MaxShapes = p.MaxShape;
            Player.MarketPanelType = p.MarketType;
            Player.ClearMarketFilterEx(); // a plain search drops the C.MarketSearchEx filter, so it behaves as before phase 2.6

            Player.MarketSearch(p.Match, p.Type);
        }
        private void MarketRefresh()
        {
            if (Stage != GameStage.Game) return;

            Player.MarketSearch(string.Empty, Player.MatchType);
        }

        private void MarketPage(C.MarketPage p)
        {
            if (Stage != GameStage.Game) return;

            Player.MarketPage(p.Page);
        }
        private void MarketBuy(C.MarketBuy p)
        {
            if (Stage != GameStage.Game) return;

            Player.MarketBuy(p.AuctionID, p.BidPrice);
        }
        private void MarketSellNow(C.MarketSellNow p)
        {
            if (Stage != GameStage.Game) return;

            Player.MarketSellNow(p.AuctionID);
        }

        private void MarketGetBack(C.MarketGetBack p)
        {
            if (Stage != GameStage.Game) return;

            Player.MarketGetBack(p.Mode, p.AuctionID);
        }
        private void RequestUserName(C.RequestUserName p)
        {
            if (Stage != GameStage.Game) return;

            Player.RequestUserName(p.UserID);
        }
        private void RequestChatItem(C.RequestChatItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.RequestChatItem(p.ChatItemID);
        }
        private void EditGuildMember(C.EditGuildMember p)
        {
            if (Stage != GameStage.Game) return;
            Player.EditGuildMember(p.Name,p.RankName,p.RankIndex,p.ChangeType);
        }
        private void EditGuildNotice(C.EditGuildNotice p)
        {
            if (Stage != GameStage.Game) return;
            Player.EditGuildNotice(p.notice);
        }
        private void GuildInvite(C.GuildInvite p)
        {
            if (Stage != GameStage.Game) return;

            Player.GuildInvite(p.AcceptInvite);
        }
        private void RequestGuildInfo(C.RequestGuildInfo p)
        {
            if (Stage != GameStage.Game) return;
            Player.RequestGuildInfo(p.Type);
        }
        private void GuildNameReturn(C.GuildNameReturn p)
        {
            if (Stage != GameStage.Game) return;
            Player.GuildNameReturn(p.Name);
        }
        private void GuildStorageGoldChange(C.GuildStorageGoldChange p)
        {
            if (Stage != GameStage.Game) return;
            Player.GuildStorageGoldChange(p.Type, p.Amount);
        }
        private void GuildStorageItemChange(C.GuildStorageItemChange p)
        {
            if (Stage != GameStage.Game) return;
            Player.GuildStorageItemChange(p.Type, p.From, p.To);
        }
        private void GuildWarReturn(C.GuildWarReturn p)
        {
            if (Stage != GameStage.Game) return;
            Player.GuildWarReturn(p.Name);
        }


        private void MarriageRequest(C.MarriageRequest p)
        {
            if (Stage != GameStage.Game) return;

            Player.MarriageRequest();
        }

        private void MarriageReply(C.MarriageReply p)
        {
            if (Stage != GameStage.Game) return;

            Player.MarriageReply(p.AcceptInvite);
        }

        private void ChangeMarriage(C.ChangeMarriage p)
        {
            if (Stage != GameStage.Game) return;

            if (Player.Info.Married == 0)
            {
                Player.AllowMarriage = !Player.AllowMarriage;
                if (Player.AllowMarriage)
                    Player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.YouAllowMarriageRequests), ChatType.Hint);
                else
                    Player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.YouBlockMarriageRequests), ChatType.Hint);
            }
            else
            {
                Player.AllowLoverRecall = !Player.AllowLoverRecall;
                if (Player.AllowLoverRecall)
                    Player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.YouAllowRecallFromLover), ChatType.Hint);
                else
                    Player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.YouBlockRecallFromLover), ChatType.Hint);
            }

            Player.SendAllowToggles();
        }

        private void DivorceRequest(C.DivorceRequest p)
        {
            if (Stage != GameStage.Game) return;

            Player.DivorceRequest();
        }

        private void DivorceReply(C.DivorceReply p)
        {
            if (Stage != GameStage.Game) return;

            Player.DivorceReply(p.AcceptInvite);
        }

        private void AddMentor(C.AddMentor p)
        {
            if (Stage != GameStage.Game) return;

            Player.AddMentor(p.Name);
        }

        private void MentorReply(C.MentorReply p)
        {
            if (Stage != GameStage.Game) return;

            Player.MentorReply(p.AcceptInvite);
        }

        private void AllowMentor(C.AllowMentor p)
        {
            if (Stage != GameStage.Game) return;

                Player.AllowMentor = !Player.AllowMentor;
                if (Player.AllowMentor)
                Player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.AllowingMentorRequests), ChatType.Hint);
                else
                    Player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.BlockingMentorRequests), ChatType.Hint);

            Player.SendAllowToggles();
        }

        private void CancelMentor(C.CancelMentor p)
        {
            if (Stage != GameStage.Game) return;

            Player.MentorBreak(true);
        }

        private void TradeRequest(C.TradeRequest p)
        {
            if (Stage != GameStage.Game) return;

            Player.TradeRequest();
        }
        private void TradeGold(C.TradeGold p)
        {
            if (Stage != GameStage.Game) return;

            Player.TradeGold(p.Amount);
        }
        private void TradeReply(C.TradeReply p)
        {
            if (Stage != GameStage.Game) return;

            Player.TradeReply(p.AcceptInvite);
        }
        private void TradeConfirm(C.TradeConfirm p)
        {
            if (Stage != GameStage.Game) return;

            Player.TradeConfirm(p.Locked);
        }
        private void TradeCancel(C.TradeCancel p)
        {
            if (Stage != GameStage.Game) return;

            Player.TradeCancel();
        }
        private void EquipSlotItem(C.EquipSlotItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.EquipSlotItem(p.Grid, p.UniqueID, p.To, p.GridTo, p.ToUniqueID);
        }

        private void FishingCast(C.FishingCast p)
        {
            if (Stage != GameStage.Game) return;

            Player.FishingCast(p.CastOut, true);
        }

        private void FishingChangeAutocast(C.FishingChangeAutocast p)
        {
            if (Stage != GameStage.Game) return;

            Player.FishingChangeAutocast(p.AutoCast);
        }

        private void AcceptQuest(C.AcceptQuest p)
        {
            if (Stage != GameStage.Game) return;

            Player.AcceptQuest(p.QuestIndex); //p.NPCIndex,
        }

        private void FinishQuest(C.FinishQuest p)
        {
            if (Stage != GameStage.Game) return;

            Player.FinishQuest(p.QuestIndex, p.SelectedItemIndex);
        }

        private void AbandonQuest(C.AbandonQuest p)
        {
            if (Stage != GameStage.Game) return;

            Player.AbandonQuest(p.QuestIndex);
        }

        private void ShareQuest(C.ShareQuest p)
        {
            if (Stage != GameStage.Game) return;

            Player.ShareQuest(p.QuestIndex);
        }

        private void AcceptReincarnation()
        {
            if (Stage != GameStage.Game) return;

            if (Player.ReincarnationHost != null && Player.ReincarnationHost.ReincarnationReady)
            {
                Player.Revive(Player.Stats[Stat.HP] / 2, true);
                Player.ReincarnationHost = null;
                return;
            }

            Player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.ReincarnationFailed), ChatType.System);
        }

        private void CancelReincarnation()
        {
            if (Stage != GameStage.Game) return;
            Player.ReincarnationExpireTime = Envir.Time;

        }

        private void CombineItem(C.CombineItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.CombineItem(p.Grid, p.IDFrom, p.IDTo);
        }

        private void Awakening(C.Awakening p)
        {
            if (Stage != GameStage.Game) return;

            Player.Awakening(p.UniqueID, p.Type);
        }

        private void AwakeningNeedMaterials(C.AwakeningNeedMaterials p)
        {
            if (Stage != GameStage.Game) return;

            Player.AwakeningNeedMaterials(p.UniqueID, p.Type);
        }

        private void DisassembleItem(C.DisassembleItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.DisassembleItem(p.UniqueID);
        }

        private void DowngradeAwakening(C.DowngradeAwakening p)
        {
            if (Stage != GameStage.Game) return;

            Player.DowngradeAwakening(p.UniqueID);
        }

        private void ResetAddedItem(C.ResetAddedItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.ResetAddedItem(p.UniqueID);
        }

        public void SendMail(C.SendMail p)
        {
            if (Stage != GameStage.Game) return;

            if (p.Gold > 0 || p.ItemsIdx.Length > 0)
            {
                Player.SendMail(p.Name, p.Message, p.Gold, p.ItemsIdx, p.Stamped);
            }
            else
            {
                Player.SendMail(p.Name, p.Message);
            }
        }

        public void ReadMail(C.ReadMail p)
        {
            if (Stage != GameStage.Game) return;

            Player.ReadMail(p.MailID);
        }

        public void CollectParcel(C.CollectParcel p)
        {
            if (Stage != GameStage.Game) return;

            Player.CollectMail(p.MailID);
        }

        public void DeleteMail(C.DeleteMail p)
        {
            if (Stage != GameStage.Game) return;

            Player.DeleteMail(p.MailID);
        }

        public void LockMail(C.LockMail p)
        {
            if (Stage != GameStage.Game) return;

            Player.LockMail(p.MailID, p.Lock);
        }

        public void MailCost(C.MailCost p)
        {
            if (Stage != GameStage.Game) return;

            uint cost = Player.GetMailCost(p.ItemsIdx, p.Gold, p.Stamped);

            Enqueue(new S.MailCost { Cost = cost });
        }

        private void RequestIntelligentCreatureUpdates(C.RequestIntelligentCreatureUpdates p)
        {
            if (Stage != GameStage.Game) return;

            Player.SendIntelligentCreatureUpdates = p.Update;
        }

        private void UpdateIntelligentCreature(C.UpdateIntelligentCreature p)
        {
            if (Stage != GameStage.Game) return;

            ClientIntelligentCreature petUpdate = p.Creature;
            if (petUpdate == null) return;

            if (p.ReleaseMe)
            {
                Player.ReleaseIntelligentCreature(petUpdate.PetType);
                return;
            }
            else if (p.SummonMe)
            {
                Player.SummonIntelligentCreature(petUpdate.PetType);
                return;
            }
            else if (p.UnSummonMe)
            {
                Player.UnSummonIntelligentCreature(petUpdate.PetType);
                return;
            }
            else
            {
                //Update the creature info
                for (int i = 0; i < Player.Info.IntelligentCreatures.Count; i++)
                {
                    if (Player.Info.IntelligentCreatures[i].PetType == petUpdate.PetType)
                    {
                        var reg = new Regex(@"^[A-Za-z0-9]{" + Globals.MinCharacterNameLength + "," + Globals.MaxCharacterNameLength + "}$");

                        if (reg.IsMatch(petUpdate.CustomName))
                        {
                            Player.Info.IntelligentCreatures[i].CustomName = petUpdate.CustomName;
                        }

                        Player.Info.IntelligentCreatures[i].SlotIndex = petUpdate.SlotIndex;
                        Player.Info.IntelligentCreatures[i].Filter = petUpdate.Filter;
                        Player.Info.IntelligentCreatures[i].petMode = petUpdate.petMode;
                    }
                    else continue;
                }

                if (Player.CreatureSummoned)
                {
                    if (Player.SummonedCreatureType == petUpdate.PetType)
                        Player.UpdateSummonedCreature(petUpdate.PetType);
                }
            }
        }

        private void IntelligentCreaturePickup(C.IntelligentCreaturePickup p)
        {
            if (Stage != GameStage.Game) return;

            Player.IntelligentCreaturePickup(p.MouseMode, p.Location);
        }

        private void AddFriend(C.AddFriend p)
        {
            if (Stage != GameStage.Game) return;

            Player.AddFriend(p.Name, p.Blocked);
        }

        private void RemoveFriend(C.RemoveFriend p)
        {
            if (Stage != GameStage.Game) return;

            Player.RemoveFriend(p.CharacterIndex);
        }

        private void AddMemo(C.AddMemo p)
        {
            if (Stage != GameStage.Game) return;

            Player.AddMemo(p.CharacterIndex, p.Memo);
        }
        private void GuildBuffUpdate(C.GuildBuffUpdate p)
        {
            if (Stage != GameStage.Game) return;
            Player.GuildBuffUpdate(p.Action,p.Id);
        }
        private void GameshopBuy(C.GameshopBuy p)
        {
            if (Stage != GameStage.Game) return;
            Player.GameshopBuy(p.GIndex, p.Quantity, p.PType);
        }

        private void NPCConfirmInput(C.NPCConfirmInput p)
        {
            if (Stage != GameStage.Game) return;

            Player.NPCData["NPCInputStr"] = p.Value;

            if (p.NPCID == Envir.DefaultNPC.LoadedObjectID && Player.NPCObjectID == Envir.DefaultNPC.LoadedObjectID)
            {
                Player.CallDefaultNPC(p.PageName);
                return;
            }

            Player.CallNPC(Player.NPCObjectID, p.PageName);
        }

        public List<byte[]> Image = new List<byte[]>();
        
        private void ReportIssue(C.ReportIssue p)
        {
            if (Stage != GameStage.Game) return;

            return;

            // Image.Add(p.Image);

            // if (p.ImageChunk >= p.ImageSize)
            // {
            //     System.Drawing.Image image = Functions.ByteArrayToImage(Functions.CombineArray(Image));
            //     image.Save("Reported-" + Player.Name + "-" + DateTime.Now.ToString("yyMMddHHmmss") + ".jpg");
            //     Image.Clear();
            // }
        }
        private void GetRanking(C.GetRanking p)
        {
            if (Stage != GameStage.Game && Stage != GameStage.Observer) return;
            Envir.GetRanking(this, p.RankType, p.RankIndex, p.OnlineOnly);
        }

        private void Opendoor(C.Opendoor p)
        {
            if (Stage != GameStage.Game) return;
            Player.Opendoor(p.DoorIndex);
        }

        private void GetRentedItems()
        {
            if (Stage != GameStage.Game)
                return;

            Player.GetRentedItems();
        }

        private void ItemRentalRequest()
        {
            if (Stage != GameStage.Game)
                return;

            Player.ItemRentalRequest();
        }

        private void ItemRentalFee(C.ItemRentalFee p)
        {
            if (Stage != GameStage.Game)
                return;

            Player.SetItemRentalFee(p.Amount);
        }

        private void ItemRentalPeriod(C.ItemRentalPeriod p)
        {
            if (Stage != GameStage.Game)
                return;

            Player.SetItemRentalPeriodLength(p.Days);
        }

        private void DepositRentalItem(C.DepositRentalItem p)
        {
            if (Stage != GameStage.Game)
                return;

            Player.DepositRentalItem(p.From, p.To);
        }

        private void RetrieveRentalItem(C.RetrieveRentalItem p)
        {
            if (Stage != GameStage.Game)
                return;

            Player.RetrieveRentalItem(p.From, p.To);
        }

        private void CancelItemRental()
        {
            if (Stage != GameStage.Game)
                return;

            Player.CancelItemRental();
        }

        private void ItemRentalLockFee()
        {
            if (Stage != GameStage.Game)
                return;

            Player.ItemRentalLockFee();
        }

        private void ItemRentalLockItem()
        {
            if (Stage != GameStage.Game)
                return;

            Player.ItemRentalLockItem();
        }

        private void ConfirmItemRental()
        {
            if (Stage != GameStage.Game)
                return;

            Player.ConfirmItemRental();
        }

        public void CheckItemInfo(ItemInfo info, bool dontLoop = false)
        {
            if ((dontLoop == false) && (info.ClassBased | info.LevelBased)) //send all potential data so client can display it
            {
                for (int i = 0; i < Envir.ItemInfoList.Count; i++)
                {
                    if ((Envir.ItemInfoList[i] != info) && (Envir.ItemInfoList[i].Name.StartsWith(info.Name)))
                        CheckItemInfo(Envir.ItemInfoList[i], true);
                }
            }

            foreach (MirConnection observer in Observers)
                observer.CheckItemInfo(info, dontLoop);

            if (SentItemInfo.Contains(info)) return;
            Enqueue(new S.NewItemInfo { Info = info });
            SentItemInfo.Add(info);
        }

        public void CheckMonsterInfo(int monsterIndex)
        {
            CheckMonsterInfo(Envir.GetMonsterInfo(monsterIndex));
        }

        public void CheckMonsterInfo(MonsterInfo info)
        {
            if (info == null) return;

            foreach (MirConnection observer in Observers)
                observer.CheckMonsterInfo(info);

            if (SentMonsterInfo.Contains(info)) return;

            Enqueue(new S.NewMonsterInfo { Info = info.ClientInformation });
            SentMonsterInfo.Add(info);
        }

        public void CheckNPCInfo(int npcIndex)
        {
            CheckNPCInfo(Envir.GetNPCInfo(npcIndex));
        }

        public void CheckNPCInfo(NPCInfo info)
        {
            if (info == null) return;

            foreach (MirConnection observer in Observers)
                observer.CheckNPCInfo(info);

            if (SentNPCInfo.Contains(info)) return;

            Enqueue(new S.NewNPCInfo { Info = info.ClientInformation });
            SentNPCInfo.Add(info);
        }
        public void CheckItem(UserItem item)
        {
            CheckItemInfo(item.Info);

            for (int i = 0; i < item.Slots.Length; i++)
            {
                if (item.Slots[i] == null) continue;

                CheckItemInfo(item.Slots[i].Info);
            }

            CheckHeroInfo(item);
        }
        private void CheckHeroInfo(UserItem item)
        {
            if (item.AddedStats[Stat.Hero] == 0) return;
            if (SentHeroInfo.Contains(item.UniqueID)) return;

            HeroInfo heroInfo = Envir.GetHeroInfo(item.AddedStats[Stat.Hero]);
            if (heroInfo == null) return;

            Enqueue(new S.NewHeroInfo { Info = heroInfo.ClientInformation });
            SentHeroInfo.Add(item.UniqueID);
        }

        private void DeleteItem(C.DeleteItem p)
        {
            if (Stage != GameStage.Game) return;

            Player.DeleteItem(p.UniqueID, p.Count);
        }

        private void SellItems(C.SellItems p)
        {
            if (Stage != GameStage.Game) return;

            Player.SellItems(p.Items);
        }

        private void RepairItems(C.RepairItems p)
        {
            if (Stage != GameStage.Game) return;

            Player.RepairItems(p.Special, p.UniqueIDs);
        }

        private void CollectAllParcels()
        {
            if (Stage != GameStage.Game) return;

            Player.CollectAllParcels();
        }

        private void ReadMails(C.ReadMails p)
        {
            if (Stage != GameStage.Game) return;

            Player.ReadMails(p.MailIDs);
        }

        private void SortItems(C.SortItems p)
        {
            if (Stage != GameStage.Game) return;

            Player.SortItems(p.Grid, p.Mode);
        }

        private void HeroSpellToggle(C.HeroSpellToggle p)
        {
            if (Stage != GameStage.Game) return;

            Player.HeroSpellToggle(p.Spell, p.AutoCast);
        }

        private void RequestMapDetails(C.RequestMapDetails p)
        {
            if (Stage != GameStage.Game) return;

            Player.RequestMapDetails(p.MapIndex);
        }

        private void RequestGuildMemberDetails()
        {
            if (Stage != GameStage.Game) return;

            Player.RequestGuildMemberDetails();
        }

        private void RequestGuildLog()
        {
            if (Stage != GameStage.Game) return;

            Player.RequestGuildLog();
        }

        private void GetGuildRanking(C.GetGuildRanking p)
        {
            if (Stage != GameStage.Game && Stage != GameStage.Observer) return;

            Envir.GetGuildRanking(this, p.Page);
        }

        private void FindMentor(C.FindMentor p)
        {
            if (Stage != GameStage.Game) return;

            Player.FindMentor(p.MinLevel, p.MaxLevel);
        }

        private void GuildTerritoryAction(C.GuildTerritoryAction p)
        {
            if (Stage != GameStage.Game) return;
            if (p.Kind > GuildTerritoryActionKind.Teleport) return;

            Player.GuildTerritoryAction(p.Kind, p.TerritoryIndex, p.Price);
        }

        private void SetProfile(C.SetProfile p)
        {
            if (Stage != GameStage.Game) return;

            Player.SetProfile(p.Text);
        }

        private void GameshopGift(C.GameshopGift p)
        {
            if (Stage != GameStage.Game) return;
            if (p.Quantity < 1 || p.Quantity > 99) return; // C.GameshopBuy sends a byte; the gift's int is narrowed only after this check
            if (string.IsNullOrWhiteSpace(p.Recipient) || p.Recipient.Length > Globals.MaxCharacterNameLength) return;

            Player.GameshopGift(p.GIndex, (byte)p.Quantity, p.PType, p.Recipient);
        }

        private void MarketSearchEx(C.MarketSearchEx p)
        {
            if (Stage != GameStage.Game) return;
            if (p.Sort > C.MarketSearchEx.SortEndingSoonest) return;

            // same player fields as MarketSearch, plus the extended filter that GetMarket applies until the next C.MarketSearch
            Player.UserMatch = p.Usermode;
            Player.MinShapes = p.MinShape;
            Player.MaxShapes = p.MaxShape;
            Player.MarketPanelType = p.MarketType;
            Player.SetMarketFilterEx(p.Sort, p.PriceMin, p.PriceMax, p.MinGrade);

            Player.MarketSearch(p.Match, p.Type);
        }

        private void MarketPriceHistory(C.MarketPriceHistory p)
        {
            if (Stage != GameStage.Game) return;

            Player.MarketPriceHistory(p.ItemIndex);
        }

        private void RequestSentMail()
        {
            if (Stage != GameStage.Game) return;

            Player.RequestSentMail();
        }

        private void HideSentMail(C.HideSentMail p)
        {
            if (Stage != GameStage.Game) return;

            Player.HideSentMail(p.MailID);
        }

        private void FriendRequestReply(C.FriendRequestReply p)
        {
            if (Stage != GameStage.Game) return;

            Player.FriendRequestReply(p.Name, p.Accept);
        }

        private void SetAllowToggle(C.SetAllowToggle p)
        {
            if (Stage != GameStage.Game) return;
            if (!Enum.IsDefined(typeof(AllowToggle), p.Toggle)) return;

            Player.SetAllowToggle(p.Toggle, p.Value);
        }
    }

    public class MirConnectionLog {
        public string IPAddress = "";
        public List<long> AccountsMade = new List<long>();
        public List<long> CharactersMade = new List<long>();
    }
}

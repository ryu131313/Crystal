using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using Client.MirControls;
using C = ClientPackets;


namespace Client.MirNetwork
{
    /// <summary>
    /// The connection to the game server. Two transports behind the same queues: a TcpClient to IPAddress:Port, or, when
    /// [Network] Relay is set, a WebSocket to a WebRelay (the web client's path: one Crystal frame per WebSocket message each
    /// way; the relay holds the TCP connection to the server). Both decode on a background thread and hand packets to the
    /// UI thread through _receiveList; Process (UI thread) handles them and queues sends.
    /// </summary>
    static class Network
    {
        private static TcpClient _client;
        private static ClientWebSocket _ws;
        private static readonly ConcurrentQueue<byte[]> _wsOut = new ConcurrentQueue<byte[]>();
        private static int _wsSending;

        private static bool UseRelay => Settings.Relay.Length > 0;

        /// <summary>The open connection of either transport.</summary>
        private static bool IsOpen => UseRelay ? _ws != null && _ws.State == WebSocketState.Open : _client != null && _client.Connected;
        public static int ConnectAttempt = 0;
        public static int MaxAttempts = 20;
        public static bool ErrorShown;
        public static bool Connected;
        public static long TimeOutTime, TimeConnected, RetryTime = CMain.Time + 5000;

        private static ConcurrentQueue<Packet> _receiveList;
        private static ConcurrentQueue<Packet> _sendList;

        static byte[] _rawData = new byte[0];
        static readonly byte[] _rawBytes = new byte[8 * 1024];

        public static void Connect()
        {
            if (_client != null || _ws != null)
                Disconnect();

            if (ConnectAttempt >= MaxAttempts)
            {
                if (ErrorShown)
                {
                    return;
                }

                ErrorShown = true;

                MirMessageBox errorBox = new(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ErrorConnectingToServer), MirMessageBoxButtons.Cancel);
                errorBox.CancelButton.Click += (o, e) => Program.Form.Close();
                errorBox.Label.Text = GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.MaximumConnectionAttemptsReached), MaxAttempts);
                errorBox.Show();
                return;
            }

            ConnectAttempt++;

            if (UseRelay)
            {
                ConnectRelay();
                return;
            }

            try
            {
                _client = new TcpClient { NoDelay = true };
                _client?.BeginConnect(Settings.IPAddress, Settings.Port, Connection, null);
            }
            catch (ObjectDisposedException ex)
            {
                if (Settings.LogErrors) CMain.SaveError(ex.ToString());
                Disconnect();
            }
        }

        private static void Connection(IAsyncResult result)
        {
            try
            {
                _client?.EndConnect(result);

                if ((_client != null &&
                    !_client.Connected) ||
                    _client == null)
                {
                    Connect();
                    return;
                }

                _receiveList = new ConcurrentQueue<Packet>();
                _sendList = new ConcurrentQueue<Packet>();
                _rawData = new byte[0];

                TimeOutTime = CMain.Time + Settings.TimeOut;
                TimeConnected = CMain.Time;

                BeginReceive();
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
                Connect();
            }
            catch (Exception ex)
            {
                if (Settings.LogErrors) CMain.SaveError(ex.ToString());
                Disconnect();
            }
        }

        private static void BeginReceive()
        {
            if (_client == null || !_client.Connected) return;

            try
            {
                _client.Client.BeginReceive(_rawBytes, 0, _rawBytes.Length, SocketFlags.None, ReceiveData, _rawBytes);
            }
            catch
            {
                Disconnect();
            }
        }
        private static void ReceiveData(IAsyncResult result)
        {
            if (_client == null || !_client.Connected) return;

            int dataRead;

            try
            {
                dataRead = _client.Client.EndReceive(result);
            }
            catch
            {
                Disconnect();
                return;
            }

            if (dataRead == 0)
            {
                Disconnect();
                return; // roadmap C1: it used to carry on with the queues Disconnect had just cleared
            }

            var receiveList = _receiveList;
            var temp = _rawData;
            if (receiveList == null || temp == null) return;

            byte[] rawBytes = result.AsyncState as byte[];

            var raw = new byte[dataRead + temp.Length];
            Buffer.BlockCopy(temp, 0, raw, 0, temp.Length);
            Buffer.BlockCopy(rawBytes, 0, raw, temp.Length, dataRead);

            Packet p;
            List<byte> data = new List<byte>();

            try
            {
                while ((p = Packet.ReceivePacket(raw, out raw)) != null)
                {
                    data.AddRange(p.GetPacketBytes());
                    receiveList.Enqueue(p);
                }
                _rawData = raw;
            }
            catch (Exception ex)
            {
                // roadmap C1: a malformed or newer-protocol packet threw InvalidDataException on this socket thread,
                // which is unhandled there and killed the whole client. Log it and drop the connection instead.
                CMain.SaveError("Network: undecodable packet from the server, disconnecting: " + ex);
                Disconnect();
                return;
            }

            CMain.BytesReceived += data.Count;

            BeginReceive();
        }

        private static void BeginSend(List<byte> data)
        {
            if (_client == null || !_client.Connected || data.Count == 0) return;
            
            try
            {
                _client.Client.BeginSend(data.ToArray(), 0, data.Count, SocketFlags.None, SendData, null);
            }
            catch
            {
                Disconnect();
            }
        }
        private static void SendData(IAsyncResult result)
        {
            try
            {
                _client.Client.EndSend(result);
            }
            catch
            { }
        }

        // — relay transport (WebSocket) —

        /// <summary>
        /// Opens the WebSocket on a background thread (the UI thread never waits on the network). The Origin header is the
        /// relay's own site, which a public relay requires (--origin). On success the queues are created as Connection does;
        /// a failed connect just leaves the socket closed, and Process retries every 5 s up to MaxAttempts.
        /// </summary>
        private static void ConnectRelay()
        {
            var ws = new ClientWebSocket();
            try
            {
                var uri = new Uri(Settings.Relay);
                string scheme = uri.Scheme == "wss" ? "https" : "http";
                ws.Options.SetRequestHeader("Origin", scheme + "://" + uri.Authority);
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                _ws = ws;
                var thread = new Thread(() => RelayLoop(ws, uri)) { IsBackground = true, Name = "RelaySocket" };
                thread.Start();
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException)
            {
                if (Settings.LogErrors) CMain.SaveError("[Network] bad [Network] Relay URL '" + Settings.Relay + "': " + ex.Message);
                ws.Dispose();
                _ws = null;
            }
        }

        /// <summary>Background thread: connect, then decode every message (one server frame each) into _receiveList.</summary>
        private static void RelayLoop(ClientWebSocket ws, Uri uri)
        {
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    ws.ConnectAsync(uri, timeout.Token).GetAwaiter().GetResult();
                if (ws != _ws) { ws.Dispose(); return; }

                _rawData = new byte[0];
                _receiveList = new ConcurrentQueue<Packet>();
                _sendList = new ConcurrentQueue<Packet>();
                while (_wsOut.TryDequeue(out _)) { }
                TimeOutTime = CMain.Time + Settings.TimeOut;
                TimeConnected = CMain.Time;

                var buffer = new byte[64 * 1024];
                while (ws == _ws && ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).GetAwaiter().GetResult();
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var receiveList = _receiveList;
                    var temp = _rawData;
                    if (receiveList == null || temp == null) break;

                    var raw = new byte[temp.Length + result.Count];
                    Buffer.BlockCopy(temp, 0, raw, 0, temp.Length);
                    Buffer.BlockCopy(buffer, 0, raw, temp.Length, result.Count);

                    Packet p;
                    try
                    {
                        while ((p = Packet.ReceivePacket(raw, out raw)) != null)
                            receiveList.Enqueue(p);
                        _rawData = raw;
                    }
                    catch (Exception ex)
                    {
                        CMain.SaveError("Network: undecodable packet from the relay, disconnecting: " + ex);
                        break;
                    }
                    CMain.BytesReceived += result.Count;
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // refused, lost or closed: the socket is no longer Open, so Process reports the lost connection or retries
            }
            finally
            {
                if (ws.State == WebSocketState.Open) ws.Abort();
            }
        }

        /// <summary>
        /// UI thread: one WebSocket message per packet (the relay closes a session whose message is not exactly one frame).
        /// A background loop sends them in order; ClientWebSocket allows one send at a time.
        /// </summary>
        private static void RelaySend(byte[] frame)
        {
            _wsOut.Enqueue(frame);
            if (Interlocked.CompareExchange(ref _wsSending, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ => RelaySendLoop());
        }

        private static void RelaySendLoop()
        {
            while (true)
            {
                var ws = _ws;
                while (_wsOut.TryDequeue(out byte[] frame))
                {
                    if (ws == null || ws.State != WebSocketState.Open) continue;
                    try
                    {
                        ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
                    {
                        // the receive loop sees the closed socket; Process reports it
                    }
                }
                Interlocked.Exchange(ref _wsSending, 0);
                if (_wsOut.IsEmpty || Interlocked.CompareExchange(ref _wsSending, 1, 0) != 0) return;
            }
        }

        public static void Disconnect()
        {
            if (_ws != null)
            {
                var ws = _ws;
                _ws = null;
                try { ws.Abort(); ws.Dispose(); } catch (Exception) { }
                TimeConnected = 0;
                Connected = false;
                _sendList = null;
                _receiveList = null;
                return;
            }

            if (_client == null) return;

            _client?.Close();

            TimeConnected = 0;
            Connected = false;
            _sendList = null;
            _client = null;

            _receiveList = null;
        }

        public static void Process()
        {
            if (!IsOpen)
            {
                if (Connected)
                {
                    while (_receiveList != null && !_receiveList.IsEmpty)
                    {
                        if (!_receiveList.TryDequeue(out Packet p) || p == null) continue;
                        if (!(p is ServerPackets.Disconnect) && !(p is ServerPackets.ClientVersion)) continue;

                        MirScene.ActiveScene.ProcessPacket(p);
                        _receiveList = null;
                        return;
                    }

                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LostConnectionWithServer), true);
                    Disconnect();
                    return;
                }
                else if (CMain.Time >= RetryTime)
                {
                    RetryTime = CMain.Time + 5000;
                    Connect();
                }
                return;
            }

            if (!Connected && TimeConnected > 0 && CMain.Time > TimeConnected + 5000)
            {
                Disconnect();
                Connect();
                return;
            }



            while (_receiveList != null && !_receiveList.IsEmpty)
            {
                if (!_receiveList.TryDequeue(out Packet p) || p == null) continue;
                MirScene.ActiveScene.ProcessPacket(p);
            }


            if (CMain.Time > TimeOutTime && _sendList != null && _sendList.IsEmpty)
                _sendList.Enqueue(new C.KeepAlive());

            if (_sendList == null || _sendList.IsEmpty) return;

            TimeOutTime = CMain.Time + Settings.TimeOut;

            if (UseRelay)
            {
                while (_sendList != null && _sendList.TryDequeue(out Packet p))
                {
                    byte[] frame = p.GetPacketBytes().ToArray();
                    CMain.BytesSent += frame.Length;
                    RelaySend(frame);
                }
                return;
            }

            List<byte> data = new List<byte>();
            while (!_sendList.IsEmpty)
            {
                if (!_sendList.TryDequeue(out Packet p)) continue;
                data.AddRange(p.GetPacketBytes());
            }

            CMain.BytesSent += data.Count;

            BeginSend(data);
        }
        
        public static void Enqueue(Packet p)
        {
            if (RenderTest.UiTestRunner.Enabled) { RenderTest.UiTestRunner.Sent(p); return; } // offline UI test: record, never send
            if (_sendList != null && p != null)
                _sendList.Enqueue(p);
        }
    }
}

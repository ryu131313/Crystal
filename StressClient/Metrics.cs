using System.Text;

namespace StressClient;

/// <summary>
/// Fleet-wide counters. Bots update them with Interlocked from worker threads; the reporter reads them once per
/// second and prints deltas (rates) for the monotonic counters and current values for gauges.
/// </summary>
internal sealed class Metrics
{
    // ---- gauges ----
    public int Connecting;   // TCP connect started, S.Connected not yet received
    public int Connected;    // S.Connected received (any stage)
    public int InGame;       // S.StartGame Result=4 + S.UserInformation received
    public int MaxInGame;

    // ---- monotonic counters ----
    public long PacketsSent, PacketsReceived, BytesSent, BytesReceived, Sends;
    public long ConnectAttempts, Refused, Disconnects, Reconnects, Errors, DecodeErrors, UnknownPackets, LoginTimeouts;
    public long AccountsCreated, CharactersCreated, Logins, GamesEntered, BotsReachedGame;
    public long Walks, Turns, Attacks, Blocked, MonstersSeen, Kills, Deaths, Revives, Throttled, Equips;
    public long MapsLoaded, MapsMissing, PathFound, PathFail, Repaths, Runs, DoorsOpened, MoveTimeouts, Directives;
    public long Potions, Casts, Pickups, Sold, Bought, Repairs, QuestsAccepted, QuestsFinished, Chats, Invites, GroupJoins;
    public long QuestFinishSent;
    public long NpcCalls, NpcPages, NpcGoods, NpcSellPages, SellSuccess, SellFailure;
    public long KeepAliveRttTotalMs, KeepAliveRttCount;

    /// <summary>S.MapChanged/MapInformation received, by map file name: shows how bots spread over maps.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> MapEntries = new();

    /// <summary>Loaded map files by format type (0..7, 100): shows whether the configured folders cover the server.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> MapTypes = new();

    public static void Inc(ref long field) => Interlocked.Increment(ref field);
    public static void Add(ref long field, long value) => Interlocked.Add(ref field, value);

    public void EnterGame()
    {
        int now = Interlocked.Increment(ref InGame);
        int max;
        while (now > (max = Volatile.Read(ref MaxInGame)) && Interlocked.CompareExchange(ref MaxInGame, now, max) != max) { }
    }

    public void LeaveGame() => Interlocked.Decrement(ref InGame);

    public Snapshot Take() => new()
    {
        Connecting = Volatile.Read(ref Connecting),
        Connected = Volatile.Read(ref Connected),
        InGame = Volatile.Read(ref InGame),
        PacketsSent = Interlocked.Read(ref PacketsSent),
        PacketsReceived = Interlocked.Read(ref PacketsReceived),
        BytesSent = Interlocked.Read(ref BytesSent),
        BytesReceived = Interlocked.Read(ref BytesReceived),
        Sends = Interlocked.Read(ref Sends),
        Refused = Interlocked.Read(ref Refused),
        Disconnects = Interlocked.Read(ref Disconnects),
        Errors = Interlocked.Read(ref Errors),
        DecodeErrors = Interlocked.Read(ref DecodeErrors),
        UnknownPackets = Interlocked.Read(ref UnknownPackets),
        LoginTimeouts = Interlocked.Read(ref LoginTimeouts),
        BotsReachedGame = Interlocked.Read(ref BotsReachedGame),
        Walks = Interlocked.Read(ref Walks),
        Turns = Interlocked.Read(ref Turns),
        Attacks = Interlocked.Read(ref Attacks),
        Blocked = Interlocked.Read(ref Blocked),
        Kills = Interlocked.Read(ref Kills),
        Deaths = Interlocked.Read(ref Deaths),
        Throttled = Interlocked.Read(ref Throttled),
        MapsLoaded = Interlocked.Read(ref MapsLoaded),
        MapsMissing = Interlocked.Read(ref MapsMissing),
        PathFound = Interlocked.Read(ref PathFound),
        PathFail = Interlocked.Read(ref PathFail),
        Repaths = Interlocked.Read(ref Repaths),
        Runs = Interlocked.Read(ref Runs),
        DoorsOpened = Interlocked.Read(ref DoorsOpened),
        MoveTimeouts = Interlocked.Read(ref MoveTimeouts),
        Directives = Interlocked.Read(ref Directives),
        Potions = Interlocked.Read(ref Potions),
        Casts = Interlocked.Read(ref Casts),
        Pickups = Interlocked.Read(ref Pickups),
        Sold = Interlocked.Read(ref Sold),
        Bought = Interlocked.Read(ref Bought),
        Repairs = Interlocked.Read(ref Repairs),
        QuestsAccepted = Interlocked.Read(ref QuestsAccepted),
        QuestsFinished = Interlocked.Read(ref QuestsFinished),
        Chats = Interlocked.Read(ref Chats),
        Invites = Interlocked.Read(ref Invites),
        GroupJoins = Interlocked.Read(ref GroupJoins),
        QuestFinishSent = Interlocked.Read(ref QuestFinishSent),
        NpcCalls = Interlocked.Read(ref NpcCalls),
        NpcPages = Interlocked.Read(ref NpcPages),
        NpcGoods = Interlocked.Read(ref NpcGoods),
        NpcSellPages = Interlocked.Read(ref NpcSellPages),
        SellSuccess = Interlocked.Read(ref SellSuccess),
        SellFailure = Interlocked.Read(ref SellFailure),
        RttTotal = Interlocked.Read(ref KeepAliveRttTotalMs),
        RttCount = Interlocked.Read(ref KeepAliveRttCount),
    };

    internal struct Snapshot
    {
        public int Connecting, Connected, InGame;
        public long PacketsSent, PacketsReceived, BytesSent, BytesReceived, Sends;
        public long Refused, Disconnects, Errors, DecodeErrors, UnknownPackets, LoginTimeouts, BotsReachedGame;
        public long Walks, Turns, Attacks, Blocked, Kills, Deaths, Throttled;
        public long MapsLoaded, MapsMissing, PathFound, PathFail, Repaths, Runs, DoorsOpened, MoveTimeouts, Directives;
        public long Potions, Casts, Pickups, Sold, Bought, Repairs, QuestsAccepted, QuestsFinished, Chats, Invites, GroupJoins;
        public long QuestFinishSent;
        public long NpcCalls, NpcPages, NpcGoods, NpcSellPages, SellSuccess, SellFailure;
        public long RttTotal, RttCount;
    }

    public const string CsvHeader =
        "t_s,connecting,connected,in_game,reached_game,sent_pps,recv_pps,sent_kbps,recv_kbps,sends_ps,walk_ps,attack_ps,blocked_ps,kills,deaths,rtt_ms,errors,decode_errors,unknown_packets,refused,disconnects,login_timeouts,throttled," +
        "maps_loaded,maps_missing,path_found,path_fail,repaths,runs,doors_opened,move_timeouts,directives";

    /// <summary>Formats one 1 Hz line (human) and the matching CSV row from two snapshots.</summary>
    public static (string line, string csv) Format(double tSeconds, Snapshot a, Snapshot b, double dt)
    {
        double R(long x, long y) => (y - x) / dt;
        long rttCount = b.RttCount - a.RttCount;
        double rtt = rttCount > 0 ? (double)(b.RttTotal - a.RttTotal) / rttCount : double.NaN;

        var line = new StringBuilder();
        line.Append($"[{tSeconds,6:0}s] conn {b.Connected,4} (+{b.Connecting} connecting) in-game {b.InGame,4} reached {b.BotsReachedGame,4}");
        line.Append($" | pkt/s out {R(a.PacketsSent, b.PacketsSent),6:0} in {R(a.PacketsReceived, b.PacketsReceived),7:0}");
        line.Append($" | KB/s out {R(a.BytesSent, b.BytesSent) / 1024,6:0.0} in {R(a.BytesReceived, b.BytesReceived) / 1024,7:0.0}");
        line.Append($" | walk/s {R(a.Walks, b.Walks),5:0} run/s {R(a.Runs, b.Runs),4:0} atk/s {R(a.Attacks, b.Attacks),4:0} blocked/s {R(a.Blocked, b.Blocked),4:0} kills {b.Kills} deaths {b.Deaths}");
        line.Append($" | path {b.PathFound} found/{b.PathFail} fail/{b.Runs} runs/{b.MapsMissing} maps missing");
        line.Append($" | rtt {(double.IsNaN(rtt) ? "  -" : rtt.ToString("0"))}ms");
        line.Append($" | err {b.Errors} decode {b.DecodeErrors} unk {b.UnknownPackets} refused {b.Refused} disc {b.Disconnects} timeout {b.LoginTimeouts}");

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string csv = string.Join(",",
            tSeconds.ToString("0.0", inv), b.Connecting, b.Connected, b.InGame, b.BotsReachedGame,
            R(a.PacketsSent, b.PacketsSent).ToString("0.0", inv), R(a.PacketsReceived, b.PacketsReceived).ToString("0.0", inv),
            (R(a.BytesSent, b.BytesSent) / 1024).ToString("0.0", inv), (R(a.BytesReceived, b.BytesReceived) / 1024).ToString("0.0", inv),
            R(a.Sends, b.Sends).ToString("0.0", inv), R(a.Walks, b.Walks).ToString("0.0", inv), R(a.Attacks, b.Attacks).ToString("0.0", inv),
            R(a.Blocked, b.Blocked).ToString("0.0", inv), b.Kills, b.Deaths, double.IsNaN(rtt) ? "" : rtt.ToString("0.0", inv),
            b.Errors, b.DecodeErrors, b.UnknownPackets, b.Refused, b.Disconnects, b.LoginTimeouts, b.Throttled,
            b.MapsLoaded, b.MapsMissing, b.PathFound, b.PathFail, b.Repaths, b.Runs, b.DoorsOpened, b.MoveTimeouts, b.Directives);
        return (line.ToString(), csv);
    }

    /// <summary>The same second as one JSON object, for the "@status" line a parent process reads (--status-json).</summary>
    public static string FormatJson(double tSeconds, Snapshot a, Snapshot b, double dt, int bots, int maxInGame, int silent)
    {
        double R(long x, long y) => Math.Round((y - x) / dt, 1);
        long rttCount = b.RttCount - a.RttCount;

        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("t", Math.Round(tSeconds, 1));
            w.WriteNumber("bots", bots);
            w.WriteNumber("connecting", b.Connecting);
            w.WriteNumber("connected", b.Connected);
            w.WriteNumber("inGame", b.InGame);
            w.WriteNumber("maxInGame", maxInGame);
            w.WriteNumber("reached", b.BotsReachedGame);
            w.WriteNumber("sentPps", R(a.PacketsSent, b.PacketsSent));
            w.WriteNumber("recvPps", R(a.PacketsReceived, b.PacketsReceived));
            w.WriteNumber("sentKBps", Math.Round(R(a.BytesSent, b.BytesSent) / 1024, 1));
            w.WriteNumber("recvKBps", Math.Round(R(a.BytesReceived, b.BytesReceived) / 1024, 1));
            w.WriteNumber("walkPs", R(a.Walks, b.Walks));
            w.WriteNumber("runPs", R(a.Runs, b.Runs));
            w.WriteNumber("attackPs", R(a.Attacks, b.Attacks));
            w.WriteNumber("blockedPs", R(a.Blocked, b.Blocked));
            if (rttCount > 0) w.WriteNumber("rttMs", Math.Round((double)(b.RttTotal - a.RttTotal) / rttCount, 1));
            w.WriteNumber("silent", silent);
            w.WriteNumber("kills", b.Kills);
            w.WriteNumber("deaths", b.Deaths);
            w.WriteNumber("potions", b.Potions);
            w.WriteNumber("pickups", b.Pickups);
            w.WriteNumber("sold", b.Sold);
            w.WriteNumber("bought", b.Bought);
            w.WriteNumber("repairs", b.Repairs);
            w.WriteNumber("questsAccepted", b.QuestsAccepted);
            w.WriteNumber("questsDone", b.QuestsFinished);
            w.WriteNumber("chats", b.Chats);
            w.WriteNumber("invites", b.Invites);
            w.WriteNumber("groups", b.GroupJoins);
            w.WriteNumber("pathFound", b.PathFound);
            w.WriteNumber("pathFail", b.PathFail);
            w.WriteNumber("mapsMissing", b.MapsMissing);
            w.WriteNumber("directives", b.Directives);
            w.WriteNumber("errors", b.Errors);
            w.WriteNumber("decodeErrors", b.DecodeErrors);
            w.WriteNumber("unknownPackets", b.UnknownPackets);
            w.WriteNumber("refused", b.Refused);
            w.WriteNumber("disconnects", b.Disconnects);
            w.WriteNumber("loginTimeouts", b.LoginTimeouts);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>Process-wide monotonic millisecond clock shared by bots and sockets (KeepAlive RTT uses it on both ends).</summary>
internal static class MonoClock
{
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    public static long Now => Clock.ElapsedMilliseconds;
}

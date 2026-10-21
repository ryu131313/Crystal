using System.Text.RegularExpressions;

namespace StressClient;

/// <summary>Command-line options. Every option has a safe default for a local scratch server.</summary>
internal sealed class Options
{
    public string Host = "127.0.0.1";
    public int Port = 17000;
    public int Bots = 10;
    public double RampPerSecond = 10;
    public int DurationSeconds = 60;
    public string Prefix = "sb";
    public string Password = "StressPw1";
    public int FirstIndex = 0;

    /// <summary>Concurrent bots allowed between TCP connect and entering the game (login-storm limiter).</summary>
    public int ConnectBudget = 25;

    /// <summary>The server's per-connection receive limit (MaxPacket, or [LoadTest] LoopbackMaxPacket) per 5 s window.
    /// Bots send at most <see cref="PacketBudgetFraction"/> of it.</summary>
    public int ServerMaxPacket = 50;
    public double PacketBudgetFraction = 0.6;

    public int TickMs = 50;
    public int MoveDelayMs = 650;      // server MoveDelay is 600 ms (HumanObject.MoveDelay)
    public int AttackDelayMs = 1500;   // server AttackSpeed is ~1400 ms at level 1 (HumanObject.RefreshStats)
    public int SeekRange = 8;
    public int LoginTimeoutSeconds = 30;
    public bool Reconnect = true;
    public int Seed = 12345;
    public string CsvPath;
    public bool Quiet;

    // ---- behaviour / movement (WoOOLcraft-style harness) ----

    /// <summary>Worker threads that sweep their partition of the bot population. I/O stays on per-session tasks.</summary>
    public int WorkerThreads = 4;

    /// <summary>Folders searched for the map files the server reports (read-only; the server's Maps folder is authoritative).</summary>
    public readonly List<string> MapFolders = new();

    /// <summary>--check-maps &lt;dir&gt;: parse every map in the folder and exit instead of running bots.</summary>
    public string CheckMapsFolder;

    /// <summary>The Director routes idle bots toward map respawn regions and, when known, map exits.</summary>
    public bool DirectorEnabled = true;
    public int DirectorIntervalSeconds = 3;

    /// <summary>A* expansion cap (nodes popped); a path that needs more is abandoned and the bot repaths later.</summary>
    public int PathNodes = 4000;

    public bool AllowRun = true;
    /// <summary>Share of eligible straight path segments the bot may run (0..100); C.Run moves 2 cells per action.</summary>
    public int RunPercent = 40;

    public int RoamRadius = 12;

    // ---- behaviour toggles (all on by default; the web admin Tools page sets them per run) ----

    public bool Hunt = true, Loot = true, Town = true, Quests = true, Potions = true, Equip = true, Chat = true, Party = true;

    /// <summary>Class for new characters; <c>null</c> = mixed (bot index cycles through the five classes).
    /// Existing characters keep their class.</summary>
    public MirClass? Class = MirClass.Warrior;

    /// <summary>--control-stdin: a "stop" line or end of input on stdin stops the run gracefully (the web admin's
    /// stop button, and the bots end when their parent process dies).</summary>
    public bool ControlStdin;

    /// <summary>--status-json: also print one "@status {json}" line per second for a parent process.</summary>
    public bool StatusJson;

    public static readonly Regex PrefixReg = new("^[A-Za-z0-9]{1,9}$");

    public static Options Parse(string[] args, out string error)
    {
        var o = new Options();
        error = null;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
                switch (a)
                {
                    case "--host": o.Host = Next(); break;
                    case "--port": o.Port = int.Parse(Next()); break;
                    case "--bots": o.Bots = int.Parse(Next()); break;
                    case "--ramp": o.RampPerSecond = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--duration": o.DurationSeconds = int.Parse(Next()); break;
                    case "--prefix": o.Prefix = Next(); break;
                    case "--password": o.Password = Next(); break;
                    case "--first-index": o.FirstIndex = int.Parse(Next()); break;
                    case "--connect-budget": o.ConnectBudget = int.Parse(Next()); break;
                    case "--max-packet": o.ServerMaxPacket = int.Parse(Next()); break;
                    case "--tick": o.TickMs = int.Parse(Next()); break;
                    case "--move-delay": o.MoveDelayMs = int.Parse(Next()); break;
                    case "--attack-delay": o.AttackDelayMs = int.Parse(Next()); break;
                    case "--seek-range": o.SeekRange = int.Parse(Next()); break;
                    case "--login-timeout": o.LoginTimeoutSeconds = int.Parse(Next()); break;
                    case "--no-reconnect": o.Reconnect = false; break;
                    case "--seed": o.Seed = int.Parse(Next()); break;
                    case "--csv": o.CsvPath = Next(); break;
                    case "--quiet": o.Quiet = true; break;

                    case "--workers": o.WorkerThreads = int.Parse(Next()); break;
                    case "--map-folder": o.MapFolders.Add(Next()); break;
                    case "--check-maps": o.CheckMapsFolder = Next(); break;
                    case "--no-director": o.DirectorEnabled = false; break;
                    case "--director-interval": o.DirectorIntervalSeconds = int.Parse(Next()); break;
                    case "--path-nodes": o.PathNodes = int.Parse(Next()); break;
                    case "--no-run": o.AllowRun = false; break;
                    case "--run-percent": o.RunPercent = int.Parse(Next()); break;
                    case "--roam-radius": o.RoamRadius = int.Parse(Next()); break;

                    case "--no-hunt": o.Hunt = false; break;
                    case "--no-loot": o.Loot = false; break;
                    case "--no-town": o.Town = false; break;
                    case "--no-quests": o.Quests = false; break;
                    case "--no-potions": o.Potions = false; break;
                    case "--no-equip": o.Equip = false; break;
                    case "--no-chat": o.Chat = false; break;
                    case "--no-party": o.Party = false; break;
                    case "--class": o.Class = ParseClass(Next()); break;
                    case "--control-stdin": o.ControlStdin = true; break;
                    case "--status-json": o.StatusJson = true; break;

                    case "-h":
                    case "--help":
                        error = "";
                        return null;
                    default:
                        throw new ArgumentException($"unknown option '{a}'");
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            error = ex.Message;
            return null;
        }

        if (!PrefixReg.IsMatch(o.Prefix)) error = "--prefix must be 1-9 letters/digits (account = prefix+5 digits, character = prefix+'c'+5 digits, max 15)";
        else if (o.Bots < 1 || o.Bots > 5000) error = "--bots must be 1..5000";
        else if (o.FirstIndex < 0 || o.FirstIndex + o.Bots > 100000) error = "--first-index + --bots must stay below 100000 (5-digit names)";
        else if (o.RampPerSecond <= 0) error = "--ramp must be > 0";
        else if (o.DurationSeconds < 0) error = "--duration must be >= 0 (0 = until stopped)";
        else if (o.DurationSeconds == 0 && !o.ControlStdin) error = "--duration 0 (until stopped) needs --control-stdin";
        else if (o.ConnectBudget < 1) error = "--connect-budget must be >= 1";
        else if (o.ServerMaxPacket < 5) error = "--max-packet must be >= 5";
        else if (o.TickMs < 10 || o.TickMs > 1000) error = "--tick must be 10..1000 ms";
        else if (o.MoveDelayMs < 600) error = "--move-delay below the server's 600 ms MoveDelay only fills its retry queue";
        else if (o.Password.Length < 5 || o.Password.Length > 15 || !Regex.IsMatch(o.Password, "^[A-Za-z0-9]+$")) error = "--password must be 5-15 letters/digits";
        else if (o.WorkerThreads < 1 || o.WorkerThreads > 64) error = "--workers must be 1..64";
        else if (o.DirectorIntervalSeconds < 1 || o.DirectorIntervalSeconds > 60) error = "--director-interval must be 1..60 s";
        else if (o.PathNodes < 100 || o.PathNodes > 200_000) error = "--path-nodes must be 100..200000";
        else if (o.RunPercent < 0 || o.RunPercent > 100) error = "--run-percent must be 0..100";
        else if (o.RoamRadius < 1 || o.RoamRadius > 100) error = "--roam-radius must be 1..100";

        if (error == null && o.MapFolders.Count == 0) o.MapFolders.Add("Maps");
        return error == null ? o : null;
    }

    private static MirClass? ParseClass(string value)
    {
        if (string.Equals(value, "mixed", StringComparison.OrdinalIgnoreCase)) return null;
        if (Enum.TryParse(value, true, out MirClass c) && Enum.IsDefined(c)) return c;
        throw new ArgumentException("--class must be warrior, wizard, taoist, assassin, archer or mixed");
    }

    /// <summary>The class a NEW character of bot <paramref name="index"/> gets.</summary>
    public MirClass ClassFor(int index) => Class ?? (MirClass)(index % 5);

    public const string Usage = """
        StressClient - headless Crystal bots for SCRATCH servers (never a live server).

          --host <ip>             server address (default 127.0.0.1)
          --port <n>              server port (default 17000)
          --bots <n>              number of bots (default 10)
          --ramp <n/s>            bots started per second (default 10)
          --duration <s>          run time in seconds after the first bot starts (default 60; 0 = until
                                    stopped, needs --control-stdin)
          --prefix <name>         account/character prefix, 1-9 alnum (default sb): accounts sb00000.., chars sbc00000..
          --first-index <n>       first bot index (default 0), to run several fleets with one prefix
          --password <pw>         password for all bot accounts (default StressPw1)
          --connect-budget <n>    bots allowed in connect/login at once (default 25)
          --max-packet <n>        the server's MaxPacket / [LoadTest] LoopbackMaxPacket (receives per 5 s;
                                    default 50); each bot sends at most 60 % of it
          --tick <ms>             bot think interval (default 50)
          --move-delay <ms>       min gap between walks (default 650; server MoveDelay = 600)
          --attack-delay <ms>     min gap between attacks (default 1500)
          --seek-range <cells>    chase monsters within this range (default 8)
          --login-timeout <s>     connect-to-in-game limit before a bot gives up and retries (default 30)
          --no-reconnect          do not reconnect bots that were disconnected
          --seed <n>              random seed (default 12345; bot i uses seed+i)
          --csv <file>            also write the 1 Hz metrics as CSV
          --quiet                 only print the summary

          --workers <n>           worker threads sweeping the bot population (default 4)
          --map-folder <dir>      folder with the server's .map files; repeatable (default .\Maps)
          --check-maps <dir>      parse every map in the folder, print coverage, exit (no bots)
          --no-director           disable the Director (bots roam around their start anchor)
          --director-interval <s> how often the Director reassigns idle bots (default 3)
          --path-nodes <n>        A* expansion cap per path (default 4000)
          --no-run                never send C.Run (walk only)
          --run-percent <0-100>   share of straight path segments that may run (default 40)
          --roam-radius <cells>   roam around the anchor within this radius (default 12)

          --no-hunt --no-loot --no-town --no-quests --no-potions --no-equip --no-chat --no-party
                                  turn one behaviour off (all on by default; --no-party also declines invites;
                                    --no-town keeps quest-only trips to the merchant unless --no-quests)
          --class <name>          class of new characters: warrior (default), wizard, taoist, assassin, archer,
                                    or mixed (cycles by bot index); existing characters keep theirs
          --control-stdin         stop gracefully on a "stop" line or end of input on stdin
          --status-json           also print one "@status {json}" line per second (for the web admin)

        Exit code: 0 when every bot reached the game and no decode errors happened, 1 otherwise, 2 bad arguments.
        """;
}

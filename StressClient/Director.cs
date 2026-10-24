using System.Collections.Concurrent;
using System.Drawing;

namespace StressClient;

internal enum DirectiveKind
{
    Roam,
    Grind,
    Travel,
}

/// <summary>An immutable routing instruction the Director hands to one bot (consumed on the bot's next tick).</summary>
internal sealed class Directive
{
    public DirectiveKind Kind;
    public Point Location;
    public int MapIndex;
    public string Reason = string.Empty;
}

/// <summary>A bot asking the Director where to go. Enqueued by the bot's worker, drained by the Director thread.</summary>
internal sealed class IdleRequest
{
    public VirtualPlayer Bot;
    public int MapIndex;
    public string MapFileName = string.Empty;
    public Point Location;
    /// <summary>Monster info indexes the bot needs for quest kills (an immutable snapshot); respawns of them are preferred.</summary>
    public int[] QuestMonsters = Array.Empty<int>();
    public long At;
}

/// <summary>
/// Central routing brain. It never touches bot internals: idle bots enqueue an <see cref="IdleRequest"/> carrying their
/// map and position, and the Director answers with an immutable <see cref="Directive"/>. Route data comes from what the
/// server sends every client: S.NewMapInfo (portal exits), S.MapDetails (respawn regions, safe zones) and the map files
/// the bot already loads for pathfinding.
/// </summary>
internal sealed class Director
{
    private readonly Options _o;
    private readonly WorldDirectory _directory;
    private readonly MapManager _maps;
    private readonly Metrics _m;
    private readonly Random _rng;
    private readonly ConcurrentQueue<IdleRequest> _queue = new();

    /// <summary>How far a quest-monster respawn may be for the Director to prefer it over the nearest ones.</summary>
    private const int QuestRespawnRange = 60;

    private Thread _thread;
    private volatile bool _running;

    public Director(Options options, WorldDirectory directory, MapManager maps, Metrics metrics)
    {
        _o = options;
        _directory = directory;
        _maps = maps;
        _m = metrics;
        _rng = new Random(options.Seed ^ 0x5EED);
    }

    public void Request(IdleRequest request) => _queue.Enqueue(request);

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "StressClient-Director" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(2000);
    }

    private void Loop()
    {
        long next = MonoClock.Now + _o.DirectorIntervalSeconds * 1000L;
        while (_running)
        {
            Thread.Sleep(100);
            long now = MonoClock.Now;
            if (now < next) continue;
            next = now + _o.DirectorIntervalSeconds * 1000L;

            while (_queue.TryDequeue(out var request))
                Route(request);
        }
    }

    private void Route(IdleRequest request)
    {
        if (request.Bot == null || !request.Bot.IsPlaying) return;

        Directive directive = null;
        var facts = _directory.Get(request.MapIndex);

        if (facts != null)
        {
            ClientMovementInfo[] movements;
            ClientMapRespawn[] respawns;
            ClientMapSafeZone[] safeZones;
            lock (facts)
            {
                movements = facts.Movements.ToArray();
                respawns = facts.Respawns.ToArray();
                safeZones = facts.SafeZones.ToArray();
            }

            var grid = _maps.Get(request.MapFileName);

            // Most idle bots go hunting; a few take a known exit to spread the fleet across maps.
            int roll = _rng.Next(100);
            if (movements.Length > 0 && roll < 15)
            {
                var near = movements
                    .Where(m => m.Destination != request.MapIndex && Functions.MaxDistance(m.Location, request.Location) <= 30)
                    .ToArray();

                if (near.Length > 0)
                {
                    var exit = near[_rng.Next(near.Length)];
                    var point = FindWalkable(grid, exit.Location, 6);
                    if (point != null)
                        directive = new Directive { Kind = DirectiveKind.Travel, Location = point.Value, MapIndex = exit.Destination, Reason = "exit" };
                }
            }

            if (directive == null && respawns.Length > 0)
            {
                // Prefer a respawn near the bot: a directive on the far side of the map turns a town run into a
                // minutes-long walk and starves the hunt loop. A bot hunting for a quest gets a respawn of its quest
                // monsters when one is within reach.
                var pool = respawns;
                if (request.QuestMonsters.Length > 0)
                {
                    var quest = respawns
                        .Where(r => Array.IndexOf(request.QuestMonsters, r.MonsterIndex) >= 0 &&
                                    Functions.MaxDistance(r.Location, request.Location) <= QuestRespawnRange)
                        .ToArray();
                    if (quest.Length > 0) pool = quest;
                }

                var candidates = pool
                    .OrderBy(r => Functions.MaxDistance(r.Location, request.Location))
                    .Take(Math.Min(3, pool.Length))
                    .ToArray();

                var respawn = candidates[_rng.Next(candidates.Length)];
                int spread = Math.Max(0, Math.Min((int)respawn.Spread, 20));
                var origin = new Point(
                    respawn.Location.X + (spread > 0 ? _rng.Next(-spread, spread + 1) : 0),
                    respawn.Location.Y + (spread > 0 ? _rng.Next(-spread, spread + 1) : 0));
                var point = FindWalkable(grid, origin, 4);
                if (point != null)
                    directive = new Directive { Kind = DirectiveKind.Grind, Location = point.Value, MapIndex = request.MapIndex, Reason = "respawn" };
            }

            if (directive == null && safeZones.Length > 0)
            {
                var zone = safeZones[_rng.Next(safeZones.Length)];
                var point = FindWalkable(grid, zone.Location, Math.Max(1, (int)zone.Size));
                if (point != null)
                    directive = new Directive { Kind = DirectiveKind.Roam, Location = point.Value, MapIndex = request.MapIndex, Reason = "safe zone" };
            }
        }

        directive ??= new Directive
        {
            Kind = DirectiveKind.Roam,
            Location = request.Location,
            MapIndex = request.MapIndex,
            Reason = "anchor",
        };

        request.Bot.AssignDirective(directive);
        Metrics.Inc(ref _m.Directives);
    }

    private static Point? FindWalkable(MapGrid grid, Point near, int radius)
    {
        if (grid == null) return near;
        radius = Math.Max(0, Math.Min(radius, 40));

        for (int r = 0; r <= radius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    int x = near.X + dx, y = near.Y + dy;
                    if (grid.InBounds(x, y) && !grid.IsBlocked(x, y)) return new Point(x, y);
                }
            }
        }

        return null;
    }
}

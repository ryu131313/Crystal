namespace Server.WebAdmin
{
    public sealed class PendingRebootItem
    {
        public string Area { get; set; }
        public string Description { get; set; }
        public string Admin { get; set; }
        public DateTime Utc { get; set; }
    }

    /// <summary>List of saved-but-not-yet-live changes. Same (area, description) is collapsed to its latest entry.</summary>
    public sealed class WebAdminPendingList
    {
        public const int MaxItems = 200;

        private readonly List<PendingRebootItem> _items = new List<PendingRebootItem>();
        private readonly object _gate = new object();
        private readonly Func<DateTime> _clock;

        public WebAdminPendingList(Func<DateTime> clock = null)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public int Count
        {
            get
            {
                lock (_gate) return _items.Count;
            }
        }

        public void Add(string area, string description, string admin)
        {
            area = string.IsNullOrWhiteSpace(area) ? "general" : area.Trim();
            description = description?.Trim() ?? string.Empty;

            lock (_gate)
            {
                _items.RemoveAll(i => string.Equals(i.Area, area, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(i.Description, description, StringComparison.Ordinal));

                _items.Add(new PendingRebootItem
                {
                    Area = area,
                    Description = description,
                    Admin = string.IsNullOrWhiteSpace(admin) ? "unknown" : admin,
                    Utc = _clock(),
                });

                if (_items.Count > MaxItems)
                    _items.RemoveRange(0, _items.Count - MaxItems);
            }
        }

        public void Clear()
        {
            lock (_gate) _items.Clear();
        }

        /// <summary>Copies, oldest first.</summary>
        public IReadOnlyList<PendingRebootItem> Snapshot()
        {
            lock (_gate)
            {
                return _items.Select(i => new PendingRebootItem { Area = i.Area, Description = i.Description, Admin = i.Admin, Utc = i.Utc }).ToList();
            }
        }
    }

    /// <summary>
    /// "Needs reboot" tracker for game-data edits saved on Envir.Edit. Shown on /api/admin/status until the world (re)starts:
    /// the list clears itself on <see cref="WebAdminHooks.WorldStarted"/>.
    /// </summary>
    public static class WebAdminPending
    {
        internal static WebAdminPendingList List { get; } = new WebAdminPendingList();

        private static int _hooked;

        static WebAdminPending()
        {
            EnsureHooks();
        }

        internal static void EnsureHooks()
        {
            if (Interlocked.Exchange(ref _hooked, 1) != 0) return;
            WebAdminHooks.WorldStarted += Clear;
        }

        public static void Add(string area, string description, string adminName) => List.Add(area, description, adminName);

        public static void Clear() => List.Clear();

        public static IReadOnlyList<PendingRebootItem> Snapshot() => List.Snapshot();

        public static int Count => List.Count;
    }
}

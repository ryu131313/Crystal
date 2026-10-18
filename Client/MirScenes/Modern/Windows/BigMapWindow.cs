// Ported from WebClient/src/ui/modern/windows/bigMap.ts (BigMapWindow): the new UI's big map, the desktop's BigMapDialog
// (MirScenes/Dialogs/BigMapDialog.cs) as a zoomable, draggable viewport with a sidebar. Registered as "bigmap"
// (KeybindOptions.Bigmap, the minimap's map button, the game menu).
//  - maps are keyed by MapIndex. The current map is MapControl (Index, Title, BigMap; its loaded size until its record
//    arrives, like BigMapDialog); any map is BigMapData.Map(index) (ClientMapInfo: Title, Width, Height, BigMap = MMap.Lib,
//    NPCs, Movements) asked with C.RequestMapInfo. The picture is MMap.Lib[BigMap];
//  - the world map (S.WorldMapSetup, only when Enabled): Prguse2 1360 + the additive clouds 1365, each WorldMapIcon as a pin at
//    the centre of its MapLinkIcon region (UseOffSet; the region lights up on hover like OnlyDrawWhenActive), a click views
//    that map;
//  - layers: NPCs (MapLinkIcon[Icon] when ShowOnBigMap), exits (Movements, grouped by screen distance; a click views the
//    destination), group members on your map (GroupState.MemberLocations / MemberMaps, matched on the map title like
//    BigMapViewPort), monster spawns and safe zones from S.MapDetails (Models.MapDetails, only while
//    ServerRules.MapDetailsEnabled; spawn tokens use ClientMonsterInfo Image / Level / AI), your waypoints (right-click;
//    per character and map, at most 9; kept for the session);
//  - the player arrow on your own map; a click there walks (MapControl.AutoPath, BigMapViewPort.OnMouseClick); other maps and
//    the world are view-only;
//  - NPC search: the box filters the lists; "Find" (or Enter with 3+ letters, 1 s apart) sends C.SearchMap, and
//    S.SearchMapResult (ModernEvents.SearchMap) views that map and selects the NPC, "Nothing found." otherwise;
//  - Teleport (C.TeleportToNPC) for NPCs with CanTeleportTo on your own map, after a popup with GameScene.TeleportToNPCCost and a
//    gold check (BigMapDialog.TeleportToNPC);
//  - the map picker lists only maps the client knows (world-map icons, exit destinations, search results, maps viewed).
// Desktop idiom: the viewport is a nested surface (its own render target: the scaled picture is clipped to it); markers are
// small painted controls a pan only moves; the arrow and group dots follow at 4 Hz while the window is open; data changes only
// mark it dirty (a closed window costs nothing).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirSounds;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Hud;
using C = ClientPackets;
using S = ServerPackets;
using MapDetailsStateEntry = Client.MirScenes.Models.MapDetailsState.Entry;

namespace Client.MirScenes.Modern.Windows
{
    public sealed partial class BigMapWindow : ModernWindow
    {
        public const string WindowKey = "bigmap";

        private const int World = BigMapData.World, WorldImage = 1360, WorldClouds = 1365, MaxWaypoints = 9, SearchMs = 1000, SearchMin = 3;
        /// <summary>Friendly town guards by monster AI (6 / 58 Guard, 57 TownArcher, 80 ConquestArcher, 113 ArcherGuard).</summary>
        private static readonly HashSet<int> GuardAi = new HashSet<int> { 6, 57, 58, 80, 113 };

        internal enum Layer { Npcs, Exits, Group, Monsters, Safe, Waypoints }
        internal enum ListTab { Npcs, Exits, Monsters }
        internal enum Difficulty { Easy, Even, Hard, Deadly, Guard }

        // prefs (per session): spawns / safe zones opt-in (busy maps)
        private static readonly HashSet<Layer> _layers = new HashSet<Layer> { Layer.Npcs, Layer.Exits, Layer.Group, Layer.Waypoints };
        private static ListTab _tab = ListTab.Npcs;
        /// <summary>Waypoints: character → MapIndex → cells (bigMap.ts 'crystal.ui.waypoints'; per session).</summary>
        private static readonly Dictionary<string, Dictionary<int, List<Point>>> _waypoints = new Dictionary<string, Dictionary<int, List<Point>>>();

        private readonly BigMapData _data;

        // view state
        internal int View = -1;
        internal ClientMapInfo Record;
        internal Size Dims = Size.Empty;      // the view's size in cells (world: picture px)
        internal Size Pic = Size.Empty;       // the picture's size in px
        internal MLibrary PicLib;
        internal int PicIndex = -1;
        internal float Z = 1, Ox, Oy, FitZ = 1;
        private bool _fitPending;
        private string _query = string.Empty;
        private string _focus = string.Empty, _focusAt = string.Empty;
        private uint _selNpc;
        private int _selMob;
        private (int Map, uint Id)? _pendingNpc;
        private long _nextSearch;
        private bool _dirty;
        private long _nextFollow, _nextMobPoll, _regroupAt = -1;
        private float _groupedZ;
        private int _ownIndex = int.MinValue;
        private string _meKey = string.Empty, _groupKey = string.Empty;
        private string _missing = string.Empty;

        // derived per view
        private List<ClientNPCInfo> _npcs = new List<ClientNPCInfo>();
        private List<ExitGroup> _exits = new List<ExitGroup>();
        private List<MobRow> _mobs = new List<MobRow>();

        // controls
        internal BigMapViewport Vp;
        private BmPill _crumb, _coords, _legend, _loading;
        private BmSelPill _selPill;
        private BmZoom _zoom;
        private ModernButton _picker, _mine, _world, _find;
        private ModernLabel _title, _sub1, _sub2, _wpLine;
        private Chips<Layer> _layerChips;
        private SearchBox _search;
        private Segmented<ListTab> _seg;
        private ScrollView _list;
        private ChatPopover _pickerPop;
        private int _vpW, _sideW, _bodyH;

        internal readonly List<BmMarker> Marks = new List<BmMarker>();
        private readonly List<BmMarker> _groupMarks = new List<BmMarker>();
        private BmMarker _me;

        internal sealed class ExitGroup
        {
            public int Dest;
            public string Title;
            public float X, Y;
            public readonly List<Point> At = new List<Point>();
        }

        internal sealed class MobRow
        {
            public int Index;
            public ClientMonsterInfo Info;
            public string Name;
            public int Level, Count;
            public readonly List<Point> Zones = new List<Point>();
            public int Next;
        }

        public BigMapWindow(BigMapData data) : base(WindowKey, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatMapTitle), 22, 20)
        {
            _data = data;
            if (_data != null) _data.Changed += OnData;
            ModernEvents.SearchMap.On(OnSearchResult);
        }

        private static string T(ClientTextKeys k) => GameLanguage.ClientTextMap.GetLocalization(k);
        private static string T(ClientTextKeys k, params object[] a) => GameLanguage.ClientTextMap.GetLocalization(k, a);

        private bool Narrow => Settings.ScreenWidth / ModernRoot.K < 1100;

        // ------------------------------------------------------------------ build

        protected override void Build()
        {
            bool narrow = Narrow;
            ContentWidth = narrow ? 900 : 980;
            ContentHeight = (narrow ? 560 : 620) - TitleHeight;
            _sideW = narrow ? 240 : 280;
            _vpW = ContentWidth - _sideW;
            _bodyH = ContentHeight;
            int top = ContentTop;
            Marks.Clear();
            _groupMarks.Clear();
            _me = null;
            _fix = _vpFix = null;
            _layerChips = null;
            _seg = null;

            // — viewport —
            Vp = new BigMapViewport(this) { Parent = this, Location = new Point(S(1), top), Size = new Size(S(_vpW) - S(1), S(_bodyH) - S(1)) };
            _crumb = new BmPill(13, true, Color.White) { Parent = Vp, Location = new Point(S(10), S(10)) };
            _selPill = new BmSelPill(this) { Parent = Vp, Visible = false };
            _zoom = new BmZoom(this) { Parent = Vp };
            _zoom.Location = new Point(Vp.Size.Width - S(10) - _zoom.Size.Width, S(10));
            _coords = new BmPill(11, true, ModernTheme.Text, true) { Parent = Vp, NotControl = true };
            _coords.Text = "—";
            _coords.Location = new Point(S(10), Vp.Size.Height - S(10) - _coords.Size.Height);
            _legend = new BmPill(10, false, ModernTheme.Dim) { Parent = Vp, NotControl = true, Visible = false };
            _legend.Legend = true;
            _legend.Text = " ";
            _legend.Location = new Point(Vp.Size.Width - S(10) - _legend.Size.Width, Vp.Size.Height - S(10) - _legend.Size.Height);
            _loading = new BmPill(13, false, ModernTheme.Dim) { Parent = Vp, NotControl = true, Plain = true };

            _loading.Visible = false;
            _vpFix = new ChatAlphaFix { Parent = Vp, Location = Point.Empty, Size = Vp.Size };

            // — sidebar (placed by PlaceSide: a column like the web's flex layout) —
            int inner = _sideW - 24, btnW = 64;
            _picker = new ModernButton(T(ClientTextKeys.ModernChatMapKnown)) { Parent = this, Size = new Size(S(inner - btnW * 2 - 12), S(24)), Hint = T(ClientTextKeys.ModernChatMapKnownHint) };
            _picker.Click += (o, e) => PickerMenu();
            _mine = new ModernButton(T(ClientTextKeys.ModernChatMapMine)) { Parent = this, Size = new Size(S(btnW), S(24)), Hint = T(ClientTextKeys.ModernChatMapMineHint) };
            _mine.Click += (o, e) => SetView(GameScene.Scene?.MapControl?.Index ?? -1, true);
            _world = new ModernButton(T(ClientTextKeys.ModernChatMapWorldBtn)) { Parent = this, Size = new Size(S(btnW), S(24)), Hint = T(ClientTextKeys.ModernChatMapWorldHint) };
            _world.Click += (o, e) => SetView(World, true);
            _title = new ModernLabel { Parent = this, Size = new Size(S(inner), S(21)), FontSize = 16, Bold = true, Colour = Color.White };
            _sub1 = new ModernLabel { Parent = this, Size = new Size(S(inner), S(15)), FontSize = 11, Colour = ModernTheme.Dim };
            _sub2 = new ModernLabel { Parent = this, Size = new Size(S(inner), S(15)), FontSize = 11, Colour = ModernTheme.Dim };
            BuildChips();
            _search = new SearchBox(inner - 70, T(ClientTextKeys.ModernChatMapSearch)) { Parent = this };
            _search.MaxLength = Globals.MaxChatLength;
            _search.Text = _query;
            _search.Search += q => { _query = q ?? string.Empty; RenderList(); };
            _search.Submitted += _ => ServerSearch();
            _find = new ModernButton(T(ClientTextKeys.ModernChatMapFind)) { Parent = this, Size = new Size(S(64), S(24)) };
            _find.Click += (o, e) => ServerSearch();
            _seg = null;
            _list = new ScrollView(inner, 100) { Parent = this };
            _list.Background = Color.Transparent;
            _list.Framed = false;
            _wpLine = new ModernLabel { Parent = this, Size = new Size(S(inner), S(15)), FontSize = 11, Colour = ModernTheme.Dim };
            new BmDivider { Parent = this, Location = new Point(S(_vpW), top), Size = new Size(Math.Max(1, S(1)), S(_bodyH)) };
            _fix = new ChatAlphaFix { Parent = this, Location = new Point(S(2), top), Size = new Size(S(ContentWidth) - S(4), S(_bodyH) - S(8)) };
            PlaceSide(View != World);

            _meKey = _groupKey = string.Empty;
            if (View != -1) Refresh();
        }

        private int _segY;
        private bool _chipsDetails;
        private ChatAlphaFix _fix, _vpFix;

        /// <summary>Lays the sidebar out top to bottom (bigMap.ts .side: picker row, title, layer chips, search row, list
        /// segments, list, waypoint line), collapsing what is hidden.</summary>
        private void PlaceSide(bool hasSeg)
        {
            if (_picker == null) return;
            int top = ContentTop, x = S(_vpW + 12), inner = S(_sideW - 24), y = top + S(12);
            _picker.Location = new Point(x, y);
            _mine.Location = new Point(_picker.Location.X + _picker.Size.Width + S(6), y);
            _world.Location = new Point(_mine.Location.X + _mine.Size.Width + S(6), y);
            y += S(24 + 8);
            _title.Location = new Point(x, y);
            y += S(22);
            _sub1.Location = new Point(x, y);
            y += S(15);
            _sub2.Location = new Point(x, y);
            y += S(15 + 8);
            if (_layerChips != null && _layerChips.Visible)
            {
                _layerChips.Location = new Point(x, y);
                y += _layerChips.Size.Height + S(8);
            }
            _search.Location = new Point(x, y);
            _find.Location = new Point(x + inner - _find.Size.Width, y);
            y += S(24 + 8);
            _segY = y;
            if (hasSeg) y += S(24 + 8);
            int bottom = top + S(_bodyH) - S(24);
            _list.Location = new Point(x, y);
            _list.Size = new Size(_list.Size.Width, Math.Max(S(40), bottom - y - S(4)));
            _wpLine.Location = new Point(x, bottom);
        }

        private void BuildChips()
        {
            _layerChips?.Dispose();
            int inner = _sideW - 24;
            bool details = DetailsOn();
            _chipsDetails = details;
            var choices = new List<Choice<Layer>>
            {
                new Choice<Layer>(Layer.Npcs, T(ClientTextKeys.ModernChatMapNpcs)), new Choice<Layer>(Layer.Exits, T(ClientTextKeys.ModernChatMapExits)),
                new Choice<Layer>(Layer.Group, T(ClientTextKeys.ModernChatMapGroup)),
            };
            if (details)
            {
                choices.Add(new Choice<Layer>(Layer.Monsters, T(ClientTextKeys.ModernChatMapMonsters), null, T(ClientTextKeys.ModernChatMapSpawnsHint)));
                choices.Add(new Choice<Layer>(Layer.Safe, T(ClientTextKeys.ModernChatMapSafe)));
            }
            choices.Add(new Choice<Layer>(Layer.Waypoints, T(ClientTextKeys.ModernChatMapWaypoints), null, T(ClientTextKeys.ModernChatMapWaypointsHint)));
            _layerChips = new Chips<Layer>(choices, _layers, inner, true, true) { Parent = this };
            if (View == World) _layerChips.Visible = false;
            _layerChips.Changed += sel =>
            {
                // keep hidden detail layers as they were
                foreach (Layer l in new[] { Layer.Npcs, Layer.Exits, Layer.Group, Layer.Waypoints }) if (sel.Contains(l)) _layers.Add(l); else _layers.Remove(l);
                if (_chipsDetails) foreach (Layer l in new[] { Layer.Monsters, Layer.Safe }) if (sel.Contains(l)) _layers.Add(l); else _layers.Remove(l);
                DrawOverlay();
                RenderSide();
            };
            _fix?.BringToFront();
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            // BigMapDialog.Show: TargetMyLocation.
            _ownIndex = GameScene.Scene?.MapControl?.Index ?? -1;
            SetView(_ownIndex, true);
        }

        protected override void Closed()
        {
            Vp?.EndGestures();
            ClosePicker();
            ModernTooltip.HideAll();
        }

        private void OnData(int index)
        {
            if (index == World || index == View || index == (GameScene.Scene?.MapControl?.Index ?? -2) || index < 0) _dirty = true;
        }

        private void OnSearchResult(S.SearchMapResult p)
        {
            if (p == null || IsDisposed) return;
            if (p.MapIndex == -1 && p.NPCIndex == 0) // GameScene.SearchMapResult: nothing
            {
                ModernRoot.Instance?.Popups.Confirm(T(ClientTextKeys.NothingFound), PopupButtons.Ok, PopupKind.Info, null, false, T(ClientTextKeys.ModernChatMapSearchTitle));
                return;
            }
            _data?.Learn(p.MapIndex, _data.Map(p.MapIndex)?.Title ?? string.Empty);
            _pendingNpc = (p.MapIndex, p.NPCIndex);
            if (!IsOpen) Open();
            SetView(p.MapIndex, true);
        }

        private bool _mouseWasDown;

        internal void ClearMob() => SelectMob(0);

        public override void Process()
        {
            base.Process();
            if (!IsOpen) return;
            var details = GameScene.Models?.MapDetails;
            details?.Retry(GameScene.Models.ServerRules); // a details request the 1 s cooldown held back (cheap when none waits)
            long now = CMain.Time;
            bool down = Control.MouseButtons != MouseButtons.None;
            if (down && !_mouseWasDown) PickerOutsideClick(CMain.MPoint);
            _mouseWasDown = down;
            MapControl map = GameScene.Scene?.MapControl;
            int own = map?.Index ?? -1;
            if (own != _ownIndex)
            {
                // follow a map change while looking at the map you were on (teleport, exit, death)
                bool follow = View == _ownIndex || View < 0;
                if (own >= 0) _data?.Learn(own, map.Title);
                _ownIndex = own;
                if (follow) SetView(own, true);
                else _dirty = true;
            }
            if (_dirty)
            {
                _dirty = false;
                Refresh();
            }
            if (_regroupAt >= 0 && now >= _regroupAt)
            {
                _regroupAt = -1;
                if (Z != _groupedZ) DrawOverlay();
            }
            if (now >= _nextFollow)
            {
                _nextFollow = now + 250;
                Follow();
            }
            if (now >= _nextMobPoll)
            {
                _nextMobPoll = now + 500;
                // monster infos arrive without an event: re-render once the missing names are here
                for (int i = 0; i < _mobs.Count; i++)
                    if (_mobs[i].Info == null && MonsterInfo(_mobs[i].Index) != null) { _dirty = true; break; }
            }
        }

        public override bool HandleKey(KeyEventArgs e)
        {
            if (_pickerPop != null && e.KeyCode == Keys.Escape)
            {
                ClosePicker();
                return true;
            }
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_data != null) _data.Changed -= OnData;
                ModernEvents.SearchMap.Off(OnSearchResult);
                ClosePicker();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ data helpers

        private static bool DetailsOn() => GameScene.Models?.ServerRules.Rules?.MapDetailsEnabled ?? false;
        internal bool IsOwn => View >= 0 && View == (GameScene.Scene?.MapControl?.Index ?? -1);

        internal static ClientMonsterInfo MonsterInfo(int index)
        {
            List<ClientMonsterInfo> list = GameScene.MonsterInfoList;
            for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].Index == index) return list[i];
            return null;
        }

        internal static string MobName(int index, ClientMonsterInfo info) =>
            info != null ? ChatGfx.Pretty(string.IsNullOrEmpty(info.GameName) ? info.Name : info.GameName) : T(ClientTextKeys.ModernChatMapMonster, index);

        internal Difficulty DifficultyOf(ClientMonsterInfo info)
        {
            if (info == null) return Difficulty.Easy;
            if (GuardAi.Contains(info.AI)) return Difficulty.Guard;
            int d = info.Level - (GameScene.User?.Level ?? 0);
            return d < -8 ? Difficulty.Easy : d <= 3 ? Difficulty.Even : d <= 10 ? Difficulty.Hard : Difficulty.Deadly;
        }

        internal static bool IsGuard(ClientMonsterInfo info) => info != null && GuardAi.Contains(info.AI);

        internal static Color DifficultyColour(Difficulty d) => d switch
        {
            Difficulty.Even => ModernTheme.Accent,
            Difficulty.Hard => ModernTheme.Warn,
            Difficulty.Deadly => ModernTheme.Bad,
            Difficulty.Guard => ModernTheme.Ok,
            _ => ModernTheme.Hex("#aab3c2"),
        };

        private static List<ExitGroup> GroupExits(List<ClientMovementInfo> mv, float near)
        {
            var groups = new List<ExitGroup>();
            for (int i = 0; i < mv.Count; i++)
            {
                ClientMovementInfo m = mv[i];
                float x = m.Location.X, y = m.Location.Y;
                ExitGroup g = null;
                for (int j = 0; j < groups.Count; j++)
                {
                    ExitGroup q = groups[j];
                    float dx = q.X - x, dy = q.Y - y;
                    if (q.Dest == m.Destination && MathF.Sqrt(dx * dx + dy * dy) < near) { g = q; break; }
                }
                if (g != null)
                {
                    g.At.Add(m.Location);
                    g.X += (x - g.X) / g.At.Count;
                    g.Y += (y - g.Y) / g.At.Count;
                }
                else
                {
                    g = new ExitGroup { Dest = m.Destination, Title = m.Title ?? string.Empty, X = x, Y = y };
                    g.At.Add(m.Location);
                    groups.Add(g);
                }
            }
            return groups;
        }

        // ------------------------------------------------------------------ view

        /// <summary>BigMapDialog.SetTargetMap (World = OpenWorldMap).</summary>
        internal void SetView(int index, bool fit)
        {
            if (index < 0 && index != World) return;
            if (index != View) { _selMob = 0; _selNpc = 0; }
            View = index;
            _focus = string.Empty;
            _fitPending |= fit;
            if (index >= 0)
            {
                _data?.RequestMap(index);
                if (DetailsOn()) GameScene.Models?.MapDetails.Request(index, GameScene.Models.ServerRules);
            }
            ClosePicker();
            Refresh();
        }

        /// <summary>Picks up the viewed record / picture / details and redraws the overlay and the sidebar (bigMap.ts refresh).</summary>
        private void Refresh()
        {
            if (Vp == null || Vp.IsDisposed) return;
            MapControl map = GameScene.Scene?.MapControl;
            MLibrary lib = null;
            int index = -1;
            Size dims = Size.Empty;
            _missing = string.Empty;
            if (View == World)
            {
                Record = null;
                if (_data != null && _data.WorldMap.Enabled) { lib = Libraries.Prguse2; index = WorldImage; }
                else _missing = T(ClientTextKeys.ModernChatMapNoWorld);
            }
            else
            {
                ClientMapInfo rec = _data?.Map(View);
                Record = rec;
                bool own = IsOwn;
                int big = rec != null ? rec.BigMap : own && map != null ? map.BigMap : 0;
                if (rec != null) dims = new Size(rec.Width, rec.Height);
                else if (own && map != null && map.Width > 0) dims = new Size(map.Width, map.Height);
                if (rec != null) _data?.Learn(View, rec.Title);
                if (big > 0) { lib = Libraries.MiniMap; index = big; }
                else if (rec != null || own) _missing = T(ClientTextKeys.ModernChatMapNoBigMap);
                if (dims.Width <= 0 || dims.Height <= 0) dims = Size.Empty;
            }
            Size pic = lib != null && index >= 0 ? lib.GetSize(index) : Size.Empty;
            if (lib != null && (pic.Width <= 0 || pic.Height <= 0))
            {
                lib = null;
                index = -1;
                pic = Size.Empty;
                _missing = T(ClientTextKeys.ModernChatMapNoPicture);
            }
            bool picChanged = lib != PicLib || index != PicIndex;
            PicLib = lib;
            PicIndex = index;
            Pic = pic;
            Dims = View == World ? pic : dims;
            if (View >= 0 && DetailsOn() && GameScene.Models?.MapDetails.Get(View) == null) GameScene.Models?.MapDetails.Request(View, GameScene.Models.ServerRules);
            bool ready = !Pic.IsEmpty && !Dims.IsEmpty;
            if (!ready) ShowLoading(_missing.Length > 0 ? _missing : View >= 0 && Record == null && !IsOwn ? T(ClientTextKeys.ModernChatMapAsking) : T(ClientTextKeys.ModernChatMapLoading));
            else if (_loading.Visible) _loading.Visible = false;
            if (ready && (_fitPending || picChanged)) { _fitPending = false; Fit(); }
            // S.SearchMapResult: select its NPC once the record is here
            if (_pendingNpc.HasValue && Record != null && _pendingNpc.Value.Map == View)
            {
                uint id = _pendingNpc.Value.Id;
                _pendingNpc = null;
                for (int i = 0; i < Record.NPCs.Count; i++)
                {
                    if (Record.NPCs[i].ObjectID != id) continue;
                    _selNpc = id;
                    _tab = ListTab.Npcs;
                    if (ready) FocusOn(Record.NPCs[i].Location.X, Record.NPCs[i].Location.Y, "n:" + id, false);
                    break;
                }
            }
            if (DetailsOn() != _chipsDetails) BuildChips();
            DrawOverlay();
            ApplyView();
            RenderSide();
            Vp.Redraw();
        }

        private void ShowLoading(string text)
        {
            _loading.Text = text;
            _loading.Location = new Point((Vp.Size.Width - _loading.Size.Width) / 2, (Vp.Size.Height - _loading.Size.Height) / 2);
            if (!_loading.Visible) _loading.Visible = true;
        }

        internal void Fit()
        {
            if (Pic.IsEmpty) return;
            float w = Vp.Size.Width, h = Vp.Size.Height;
            FitZ = Math.Min(w / Pic.Width, h / Pic.Height) * 0.94f;
            Z = FitZ;
            Ox = (w - Pic.Width * Z) / 2;
            Oy = (h - Pic.Height * Z) / 2;
        }

        internal void ZoomBy(float f, float cx = float.NaN, float cy = float.NaN)
        {
            if (Pic.IsEmpty) return;
            if (float.IsNaN(cx)) cx = Vp.Size.Width / 2f;
            if (float.IsNaN(cy)) cy = Vp.Size.Height / 2f;
            float nz = Math.Min(Math.Max(Z * f, FitZ * 0.6f), Math.Max(4f * ModernRoot.K, FitZ * 8));
            Ox = cx - (cx - Ox) * (nz / Z);
            Oy = cy - (cy - Oy) * (nz / Z);
            Z = nz;
            ApplyView();
        }

        internal void PanBy(float dx, float dy)
        {
            Ox += dx;
            Oy += dy;
            ApplyView();
        }

        internal void FitAndApply()
        {
            Fit();
            ApplyView();
        }

        internal bool ZoomedOut => Z < FitZ * 1.6f;

        /// <summary>A pan moves the markers; a zoom also re-places them now and regroups exits / monsters once the wheel settles.</summary>
        internal void ApplyView()
        {
            if (_zoom != null) _zoom.Percent = (int)Math.Round(Z / (FitZ > 0 ? FitZ : 1) * 100);
            bool zoomedOut = ZoomedOut;
            for (int i = 0; i < Marks.Count; i++) Place(Marks[i], zoomedOut);
            for (int i = 0; i < _groupMarks.Count; i++) Place(_groupMarks[i], zoomedOut);
            if (_me != null) Place(_me, zoomedOut);
            if (Z != _groupedZ && View != World && (_layers.Contains(Layer.Exits) || (_layers.Contains(Layer.Monsters) && DetailsOn()))) _regroupAt = CMain.Time + 160;
            Vp?.Redraw();
        }

        /// <summary>Cell → pixel inside the viewport (the picture's origin at the current zoom; bigMap.ts toPx).</summary>
        internal PointF ToPx(float x, float y)
        {
            if (Dims.IsEmpty || Pic.IsEmpty) return PointF.Empty;
            return new PointF(Ox + (x + 0.5f) / Dims.Width * Pic.Width * Z, Oy + (y + 0.5f) / Dims.Height * Pic.Height * Z);
        }

        internal float CellPx => Dims.IsEmpty ? 1 : Pic.Width * Z / Dims.Width;

        internal bool CellAt(Point local, out Point cell)
        {
            cell = Point.Empty;
            if (Dims.IsEmpty || Pic.IsEmpty) return false;
            int x = (int)Math.Floor((local.X - Ox) / (Pic.Width * Z) * Dims.Width), y = (int)Math.Floor((local.Y - Oy) / (Pic.Height * Z) * Dims.Height);
            if (x < 0 || y < 0 || x >= Dims.Width || y >= Dims.Height) return false;
            cell = new Point(x, y);
            return true;
        }

        internal void SetHoverCell(Point? cell)
        {
            string key = cell.HasValue && View != World ? cell.Value.X + ", " + cell.Value.Y : "—";
            if (_coords.Text == key) return;
            _coords.Text = key;
        }

        /// <summary>A click on your own map walks there (BigMapViewPort.OnMouseClick).</summary>
        internal void ClickAt(Point local)
        {
            if (!IsOwn || !CellAt(local, out Point cell)) return;
            MinimapHud.WalkTo(cell);
        }

        internal void RightClickAt(Point local)
        {
            if (View < 0 || !CellAt(local, out Point cell)) return;
            AddWaypoint(cell);
        }

        // ------------------------------------------------------------------ markers

        private void Place(BmMarker k, bool zoomedOut)
        {
            if (Dims.IsEmpty || Pic.IsEmpty) return;
            float sx = Pic.Width * Z / Dims.Width, sy = Pic.Height * Z / Dims.Height;
            if (k.Box.HasValue)
            {
                Rectangle b = k.Box.Value;
                k.SetBounds(new Rectangle((int)Math.Round(Ox + b.X * sx), (int)Math.Round(Oy + b.Y * sy), Math.Max(1, (int)Math.Round(b.Width * sx)), Math.Max(1, (int)Math.Round(b.Height * sy))));
                return;
            }
            PointF p = ToPx(k.X, k.Y);
            if (k.Cells > 0)
            {
                int w = (int)Math.Round(Math.Max(S(k.Min), k.Cells * sx)), h = (int)Math.Round(Math.Max(S(k.Min), k.Cells * sy));
                k.SetBounds(new Rectangle((int)Math.Round(p.X - w / 2f), (int)Math.Round(p.Y - h / 2f), w, h));
                return;
            }
            k.ZoomedOut = zoomedOut;
            k.PlaceAt(p);
        }

        private BmMarker Add(BmMarker m, float x, float y)
        {
            m.X = x;
            m.Y = y;
            m.Parent = Vp.Layer;
            Marks.Add(m);
            return m;
        }

        /// <summary>Rebuilds the markers: on a map, layer, focus, details or waypoint change (pans and zooms only move them).</summary>
        internal void DrawOverlay()
        {
            if (Vp == null) return;
            for (int i = 0; i < Marks.Count; i++) Marks[i].Dispose();
            Marks.Clear();
            for (int i = 0; i < _groupMarks.Count; i++) _groupMarks[i].Dispose();
            _groupMarks.Clear();
            _me?.Dispose();
            _me = null;
            _meKey = _groupKey = string.Empty;
            if (_legend.Visible) _legend.Visible = false;
            if (Dims.IsEmpty || Pic.IsEmpty) { Vp.Redraw(); return; }
            _groupedZ = Z;
            if (View == World) DrawWorld();
            else DrawMap();
            bool zoomedOut = ZoomedOut;
            for (int i = 0; i < Marks.Count; i++) Place(Marks[i], zoomedOut);
            Follow();
            Vp.Redraw();
        }

        private void DrawWorld()
        {
            List<WorldMapIcon> icons = _data?.WorldMap.Icons;
            if (icons == null) return;
            for (int i = 0; i < icons.Count; i++)
            {
                WorldMapIcon icon = icons[i];
                Size s = Libraries.MapLinkIcon.GetSize(icon.ImageIndex);
                if (s.Width <= 0 || s.Height <= 0) continue;
                Point off = Libraries.MapLinkIcon.GetOffSet(icon.ImageIndex);
                string title = ChatGfx.Pretty(_data.Known.TryGetValue(icon.MapIndex, out string t) && !string.IsNullOrEmpty(t) ? t : icon.Title);
                var reg = new BmRegion(icon.ImageIndex) { Visible = false };
                reg.Box = new Rectangle(off.X, off.Y, s.Width, s.Height);
                Add(reg, 0, 0);
                var pin = new BmMarker(BmKind.WorldPin, title) { Hint = T(ClientTextKeys.ModernChatMapViewIt, title) };
                pin.Linked = reg;
                int mapIndex = icon.MapIndex;
                pin.Click += (o, e) => SetView(mapIndex, true);
                Add(pin, 0, 0);
                // world view: cells are picture pixels; the pin sits at the region's centre
                pin.X = off.X + s.Width / 2f - 0.5f;
                pin.Y = off.Y + s.Height / 2f - 0.5f;
            }
        }

        private void DrawMap()
        {
            ClientMapInfo rec = Record;
            float cellPx = CellPx;
            MapDetailsStateEntry details = DetailsOn() ? GameScene.Models?.MapDetails.Get(View) : null;
            if (details != null && _layers.Contains(Layer.Safe))
                for (int i = 0; i < details.SafeZones.Count; i++)
                {
                    ClientMapSafeZone s = details.SafeZones[i];
                    var el = new BmMarker(BmKind.Safe, null) { Start = s.StartPoint, Cells = s.Size * 2 + 1, Min = 10, Hint = T(s.StartPoint ? ClientTextKeys.ModernChatMapSafeStart : ClientTextKeys.ModernChatMapSafeZone) };
                    Add(el, s.Location.X, s.Location.Y);
                }
            if (details != null && _layers.Contains(Layer.Monsters))
            {
                DrawMonsters(details.Respawns, cellPx);
                _legend.Visible = true;
            }
            if (rec != null && _layers.Contains(Layer.Exits))
            {
                List<ExitGroup> groups = GroupExits(rec.Movements, S(40) / Math.Max(0.01f, cellPx));
                for (int i = 0; i < groups.Count; i++)
                {
                    ExitGroup g = groups[i];
                    string title = ChatGfx.Pretty(g.Title);
                    var e = new BmMarker(BmKind.Exit, title) { Badge = g.At.Count > 1 ? g.At.Count.ToString() : null };
                    e.Hint = g.At.Count > 1 ? T(ClientTextKeys.ModernChatMapExitsHere, title, g.At.Count) : T(ClientTextKeys.ModernChatMapExitTitle, title);
                    for (int j = 0; j < g.At.Count; j++) if (_focus == "e:" + g.At[j].X + "," + g.At[j].Y) e.Focus = true;
                    int dest = g.Dest;
                    string destTitle = g.Title;
                    e.Click += (o, a) => { _data?.Learn(dest, destTitle); SetView(dest, true); };
                    Add(e, g.X, g.Y);
                }
            }
            if (rec != null && _layers.Contains(Layer.Npcs))
                for (int i = 0; i < rec.NPCs.Count; i++)
                {
                    ClientNPCInfo npc = rec.NPCs[i];
                    var (name, role) = ChatGfx.NpcName(npc.Name);
                    bool icon = npc.ShowOnBigMap && npc.Icon > 0;
                    var n = new BmMarker(BmKind.Npc, name) { Icon = icon ? npc.Icon : -1, Hint = role.Length > 0 ? name + " (" + role + ")" : name };
                    if (_focus == "n:" + npc.ObjectID || _selNpc == npc.ObjectID) n.Focus = true;
                    n.Click += (o, a) => SelectNpc(npc);
                    Add(n, npc.Location.X, npc.Location.Y);
                }
            if (_layers.Contains(Layer.Waypoints))
            {
                List<Point> wps = Waypoints();
                for (int i = 0; i < wps.Count; i++)
                {
                    int at = i;
                    var w = new BmMarker(BmKind.Waypoint, null) { Badge = (i + 1).ToString(), Hint = T(ClientTextKeys.ModernChatMapWaypointTitle, i + 1, wps[i].X, wps[i].Y) };
                    w.Click += (o, a) => RemoveWaypoint(at);
                    Add(w, wps[i].X, wps[i].Y);
                }
            }
        }

        /// <summary>Monster tokens and their (hidden) spawn areas: spawns closer than ~46 px share a token at their centre
        /// (weighted by count); hovering shows its areas, the selected monster shows all of its areas and dims the others.</summary>
        private void DrawMonsters(List<ClientMapRespawn> respawns, float cellPx)
        {
            float near = S(46) / Math.Max(0.01f, cellPx);
            var groups = new List<(float X, float Y, int Count, Dictionary<int, (int Count, ClientMonsterInfo Info)> Kinds, List<BmMarker> Zones, HashSet<string> At)>();
            for (int i = 0; i < respawns.Count; i++)
            {
                ClientMapRespawn r = respawns[i];
                ClientMonsterInfo info = MonsterInfo(r.MonsterIndex);
                if (info == null) GameScene.RequestMonsterInfo(r.MonsterIndex);
                float x = r.Location.X, y = r.Location.Y;
                var zone = new BmMarker(BmKind.Zone, null) { Diff = DifficultyOf(info), Cells = r.Spread * 2 + 1, Min = 12 };
                zone.Keep = _selMob == r.MonsterIndex;
                zone.Visible = zone.Keep;
                Add(zone, x, y);
                int n = Math.Max(1, (int)r.Count);
                int gi = -1;
                for (int j = 0; j < groups.Count; j++)
                {
                    float dx = groups[j].X - x, dy = groups[j].Y - y;
                    if (MathF.Sqrt(dx * dx + dy * dy) < near) { gi = j; break; }
                }
                if (gi >= 0)
                {
                    var g = groups[gi];
                    g.X = (g.X * g.Count + x * n) / (g.Count + n);
                    g.Y = (g.Y * g.Count + y * n) / (g.Count + n);
                    g.Count += n;
                    groups[gi] = g;
                }
                else
                {
                    groups.Add((x, y, n, new Dictionary<int, (int, ClientMonsterInfo)>(), new List<BmMarker>(), new HashSet<string>()));
                    gi = groups.Count - 1;
                }
                var grp = groups[gi];
                grp.Kinds[r.MonsterIndex] = grp.Kinds.TryGetValue(r.MonsterIndex, out var k) ? (k.Count + n, info) : (n, info);
                grp.Zones.Add(zone);
                grp.At.Add(r.MonsterIndex + "@" + r.Location.X + "," + r.Location.Y);
            }
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                var kinds = new List<KeyValuePair<int, (int Count, ClientMonsterInfo Info)>>(g.Kinds);
                // the selected monster first, then hostile ones (most numerous first), guards last
                kinds.Sort((a, b) =>
                {
                    int c = (b.Key == _selMob ? 1 : 0) - (a.Key == _selMob ? 1 : 0);
                    if (c != 0) return c;
                    c = (IsGuard(a.Value.Info) ? 1 : 0) - (IsGuard(b.Value.Info) ? 1 : 0);
                    if (c != 0) return c;
                    return b.Value.Count - a.Value.Count;
                });
                int top = kinds[0].Key;
                ClientMonsterInfo hardest = null;
                bool anyGuard = false;
                for (int i = 0; i < kinds.Count; i++)
                {
                    ClientMonsterInfo info = kinds[i].Value.Info;
                    if (IsGuard(info)) { anyGuard = true; continue; }
                    if (info != null && (hardest == null || info.Level > hardest.Level)) hardest = info;
                }
                Difficulty ring = hardest != null ? DifficultyOf(hardest) : anyGuard ? Difficulty.Guard : Difficulty.Easy;
                bool mixed = kinds.Count > 1;
                string label = MobName(top, kinds[0].Value.Info) + (mixed ? " +" + (kinds.Count - 1) : string.Empty);
                var t = new BmMarker(BmKind.Mob, label)
                {
                    Diff = ring,
                    MobImage = kinds[0].Value.Info != null ? (int)kinds[0].Value.Info.Image : -1,
                    MobImage2 = mixed && kinds[1].Value.Info != null ? (int)kinds[1].Value.Info.Image : -1,
                    Mixed = mixed,
                    Badge = g.Count > 1 ? g.Count.ToString() : null,
                };
                var lines = new System.Text.StringBuilder();
                for (int i = 0; i < kinds.Count; i++)
                {
                    ClientMonsterInfo info = kinds[i].Value.Info;
                    if (i > 0) lines.Append('\n');
                    lines.Append(MobName(kinds[i].Key, info)).Append(" · ")
                        .Append(IsGuard(info) ? T(ClientTextKeys.ModernChatMapGuard).ToLowerInvariant() : info != null ? T(ClientTextKeys.ModernChatMapLevel, info.Level) : T(ClientTextKeys.ModernChatMapLevelUnknown))
                        .Append(" · ").Append(kinds[i].Value.Count);
                }
                t.Hint = mixed ? lines + "\n" + T(ClientTextKeys.ModernChatMapZoomInHint)
                    : lines + "\n" + T(_selMob == top ? ClientTextKeys.ModernChatMapHideSpawns : ClientTextKeys.ModernChatMapShowSpawns);
                if (_selMob != 0)
                {
                    if (g.Kinds.ContainsKey(_selMob)) t.Selected = true;
                    else t.Dimmed = true;
                }
                if (_focus.StartsWith("m:") && g.At.Contains(_focus.Substring(2) + "@" + _focusAt)) t.Focus = true;
                t.Zones.AddRange(g.Zones);
                float gx = g.X, gy = g.Y;
                t.Click += (o, a) =>
                {
                    if (mixed) ZoomInAt(gx, gy);
                    else SelectMob(_selMob == top ? 0 : top);
                };
                Add(t, g.X, g.Y);
            }
        }

        private void ZoomInAt(float x, float y)
        {
            if (Pic.IsEmpty || Dims.IsEmpty) return;
            PointF p = ToPx(x, y);
            Ox += Vp.Size.Width / 2f - p.X;
            Oy += Vp.Size.Height / 2f - p.Y;
            ZoomBy(2);
        }

        private void SelectMob(int index)
        {
            _selMob = index;
            DrawOverlay();
            RenderSide();
        }

        private void SelectNpc(ClientNPCInfo npc)
        {
            _selNpc = _selNpc == npc.ObjectID ? 0 : npc.ObjectID;
            if (_selNpc != 0)
            {
                _tab = ListTab.Npcs;
                FocusOn(npc.Location.X, npc.Location.Y, "n:" + npc.ObjectID);
            }
            else DrawOverlay();
            RenderSide();
        }

        /// <summary>Centre the view on a cell (zooming in when far out) and ring that marker (bigMap.ts focusOn).</summary>
        private void FocusOn(float x, float y, string key, bool redraw = true)
        {
            if (Pic.IsEmpty || Dims.IsEmpty) return;
            if (Z < FitZ * 2.2f)
            {
                float cx = Vp.Size.Width / 2f, cy = Vp.Size.Height / 2f, nz = FitZ * 2.2f;
                Ox = cx - (cx - Ox) * (nz / Z);
                Oy = cy - (cy - Oy) * (nz / Z);
                Z = nz;
            }
            PointF p = ToPx(x, y);
            Ox += Vp.Size.Width / 2f - p.X;
            Oy += Vp.Size.Height / 2f - p.Y;
            _focus = key;
            _focusAt = (int)x + "," + (int)y;
            if (!redraw) return;
            DrawOverlay();
            ApplyView();
        }

        /// <summary>The arrow and the group members on your own map (4 Hz while open, and after a rebuild): changes only when the
        /// cell / direction / roster changed (bigMap.ts follow + syncGroup).</summary>
        private void Follow()
        {
            UserObject user = GameScene.User;
            if (user == null || !IsOwn || Dims.IsEmpty || Pic.IsEmpty)
            {
                if (_me != null) { _me.Dispose(); _me = null; }
                _meKey = string.Empty;
                if (_groupMarks.Count > 0)
                {
                    for (int i = 0; i < _groupMarks.Count; i++) _groupMarks[i].Dispose();
                    _groupMarks.Clear();
                    _groupKey = string.Empty;
                }
                return;
            }
            string key = user.CurrentLocation.X + "," + user.CurrentLocation.Y + "," + (int)user.Direction;
            if (key != _meKey || _me == null)
            {
                _meKey = key;
                if (_me == null) _me = new BmMarker(BmKind.Me, null) { Parent = Vp.Layer, NotControl = true };
                _me.Angle = ((int)user.Direction & 7) * 45;
                _me.X = user.CurrentLocation.X;
                _me.Y = user.CurrentLocation.Y;
                _me.Invalidate();
                Place(_me, ZoomedOut);
                _me.BringToFront();
                _sub2.Text = T(ClientTextKeys.ModernChatMapYouAt, user.CurrentLocation.X, user.CurrentLocation.Y);
            }
            SyncGroup(user.Name);
        }

        private void SyncGroup(string me)
        {
            Models.GroupState group = GameScene.Models?.Group;
            string title = GameScene.Scene?.MapControl?.Title ?? string.Empty;
            bool on = _layers.Contains(Layer.Group);
            var sb = new System.Text.StringBuilder();
            if (on && group != null)
                foreach (KeyValuePair<string, Point> kv in group.MemberLocations)
                    if (kv.Key != me && group.MemberMaps.TryGetValue(kv.Key, out string map) && map == title)
                        sb.Append(kv.Key).Append('@').Append(kv.Value.X).Append(',').Append(kv.Value.Y).Append(';');
            string key = sb.ToString();
            if (key == _groupKey) return;
            _groupKey = key;
            for (int i = 0; i < _groupMarks.Count; i++) _groupMarks[i].Dispose();
            _groupMarks.Clear();
            if (key.Length == 0 || group == null) return;
            foreach (KeyValuePair<string, Point> kv in group.MemberLocations)
            {
                if (kv.Key == me || !group.MemberMaps.TryGetValue(kv.Key, out string map) || map != title) continue;
                var m = new BmMarker(BmKind.Member, kv.Key) { Parent = Vp.Layer, X = kv.Value.X, Y = kv.Value.Y, Hint = kv.Key + " · " + kv.Value.X + ", " + kv.Value.Y };
                _groupMarks.Add(m);
                Place(m, ZoomedOut);
            }
            _me?.BringToFront();
        }

        // ------------------------------------------------------------------ sidebar

        private void RenderSide()
        {
            if (_title == null || _title.IsDisposed) return;
            MapControl map = GameScene.Scene?.MapControl;
            bool world = View == World, own = IsOwn;
            ClientMapInfo rec = Record;
            bool details = DetailsOn();
            string known = _data != null && _data.Known.TryGetValue(View, out string k) ? k : string.Empty;
            string title = world ? T(ClientTextKeys.ModernChatMapTitle) : ChatGfx.Pretty(rec?.Title ?? (own ? map?.Title : known) ?? string.Empty);
            if (title.Length == 0) title = "—";
            _crumb.Text = title;
            _crumb.Sub = world ? string.Empty : T(own ? ClientTextKeys.ModernChatMapYouHere : ClientTextKeys.ModernChatMapViewing);
            _crumb.MaxWidth = (int)(Vp.Size.Width * 0.55f);
            _title.Text = title;
            _sub1.Text = world ? T(ClientTextKeys.ModernChatMapClickRegion)
                : !Dims.IsEmpty ? T(ClientTextKeys.ModernChatMapCells, Dims.Width, Dims.Height) + (rec != null ? " · " + (rec.NPCs.Count == 1 ? T(ClientTextKeys.ModernChatMapNpcOne) : T(ClientTextKeys.ModernChatMapNpcMany, rec.NPCs.Count)) : string.Empty) : "…";
            UserObject user = GameScene.User;
            _sub2.Text = own && user != null ? T(ClientTextKeys.ModernChatMapYouAt, user.CurrentLocation.X, user.CurrentLocation.Y) : world ? string.Empty : T(ClientTextKeys.ModernChatMapClickExit);
            _picker.Text = world ? T(ClientTextKeys.ModernChatMapTitle) : View >= 0 && known.Length > 0 ? ChatGfx.Pretty(known) : T(ClientTextKeys.ModernChatMapKnown);
            _world.Visible = _data != null && _data.WorldMap.Enabled;
            _layerChips.Visible = !world;
            PlaceSide(!world);

            // derived lists
            _npcs = world || rec == null ? new List<ClientNPCInfo>() : rec.NPCs;
            _exits = world || rec == null ? new List<ExitGroup>() : GroupExits(rec.Movements, 12);
            var mobs = new Dictionary<int, MobRow>();
            MapDetailsStateEntry det = details && !world ? GameScene.Models?.MapDetails.Get(View) : null;
            if (det != null)
                for (int i = 0; i < det.Respawns.Count; i++)
                {
                    ClientMapRespawn r = det.Respawns[i];
                    if (!mobs.TryGetValue(r.MonsterIndex, out MobRow row))
                    {
                        ClientMonsterInfo info = MonsterInfo(r.MonsterIndex);
                        mobs[r.MonsterIndex] = row = new MobRow { Index = r.MonsterIndex, Info = info, Name = MobName(r.MonsterIndex, info), Level = info?.Level ?? 0 };
                    }
                    row.Count += r.Count;
                    row.Zones.Add(r.Location);
                }
            _mobs = new List<MobRow>(mobs.Values);
            _mobs.Sort((a, b) =>
            {
                int c = (IsGuard(a.Info) ? 1 : 0) - (IsGuard(b.Info) ? 1 : 0);
                return c != 0 ? c : a.Level - b.Level;
            });
            if (!details && _tab == ListTab.Monsters) _tab = ListTab.Npcs;
            RenderSelPill();

            _seg?.Dispose();
            _seg = null;
            if (!world)
            {
                var choices = new List<Choice<ListTab>>
                {
                    new Choice<ListTab>(ListTab.Npcs, T(ClientTextKeys.ModernChatMapNpcs) + "  " + _npcs.Count),
                    new Choice<ListTab>(ListTab.Exits, T(ClientTextKeys.ModernChatMapExits) + "  " + _exits.Count),
                };
                if (details) choices.Add(new Choice<ListTab>(ListTab.Monsters, T(ClientTextKeys.ModernChatMapMonsters) + "  " + (det == null ? "…" : _mobs.Count.ToString())));
                _seg = new Segmented<ListTab>(choices, _tab, _sideW - 24) { Parent = this, Location = new Point(S(_vpW + 12), _segY) };
                _seg.Changed += t => { _tab = t; RenderList(); };
                _fix?.BringToFront();
            }
            List<Point> wps = world ? new List<Point>() : Waypoints();
            _wpLine.Text = world ? string.Empty : wps.Count > 0
                ? T(ClientTextKeys.ModernChatMapWaypointLine, wps.Count == 1 ? T(ClientTextKeys.ModernChatMapWaypointOne) : T(ClientTextKeys.ModernChatMapWaypointMany, wps.Count))
                : T(ClientTextKeys.ModernChatMapWaypointNone);
            RenderList();
        }

        private void RenderSelPill()
        {
            string name = string.Empty;
            if (_selMob != 0)
            {
                for (int i = 0; i < _mobs.Count; i++) if (_mobs[i].Index == _selMob) name = _mobs[i].Name;
                if (name.Length == 0) name = MobName(_selMob, MonsterInfo(_selMob));
            }
            bool show = name.Length > 0;
            if (show)
            {
                _selPill.Set(T(ClientTextKeys.ModernChatMapSpawnsOf, string.Empty), name);
                _selPill.Location = new Point(_crumb.Location.X + _crumb.Size.Width + S(8), S(10));
            }
            if (_selPill.Visible != show) _selPill.Visible = show;
        }

        private void RenderList()
        {
            if (_list == null || _list.IsDisposed) return;
            ScrollView.ScrollContent content = _list.Content;
            for (int i = content.Controls.Count - 1; i >= 0; i--) content.Controls[i].Dispose();
            string q = _query.Trim().ToLowerInvariant();
            bool own = IsOwn;
            _find.SetDisabled(q.Length < SearchMin, T(ClientTextKeys.ModernChatMapFindShort, SearchMin));
            _find.Tooltip = T(ClientTextKeys.ModernChatMapFindHint, SearchMin);
            int w = _list.InnerWidth, y = 0;
            void AddRow(BmRow row)
            {
                row.Parent = content;
                row.Location = new Point(0, y);
                row.Size = new Size(w, row.Size.Height);
                row.Layout();
                y += row.Size.Height;
            }
            if (View == World)
            {
                List<WorldMapIcon> icons = _data?.WorldMap.Icons ?? new List<WorldMapIcon>();
                for (int i = 0; i < icons.Count; i++)
                {
                    WorldMapIcon icon = icons[i];
                    string title = ChatGfx.Pretty(_data.Known.TryGetValue(icon.MapIndex, out string t) && !string.IsNullOrEmpty(t) ? t : icon.Title);
                    if (q.Length > 0 && !title.ToLowerInvariant().Contains(q)) continue;
                    int mapIndex = icon.MapIndex;
                    var row = new BmRow { Title = title, GlyphIcon = ModernIcons.Map };
                    row.Click += (o, e) => SetView(mapIndex, true);
                    AddRow(row);
                }
            }
            else if (_tab == ListTab.Npcs)
            {
                int cost = GameScene.TeleportToNPCCost;
                for (int i = 0; i < _npcs.Count; i++)
                {
                    ClientNPCInfo npc = _npcs[i];
                    var (name, role) = ChatGfx.NpcName(npc.Name);
                    if (q.Length > 0 && !(name + " " + role).ToLowerInvariant().Contains(q)) continue;
                    var row = new BmRow
                    {
                        Title = name,
                        Sub = (role.Length > 0 ? role + " · " : string.Empty) + npc.Location.X + ", " + npc.Location.Y,
                        LinkIcon = npc.Icon > 0 ? npc.Icon : -1,
                        Selected = _selNpc == npc.ObjectID,
                    };
                    if (own && npc.CanTeleportTo)
                    {
                        row.ButtonText = T(ClientTextKeys.ModernChatMapTeleport);
                        row.ButtonHint = T(ClientTextKeys.ModernChatMapTeleportHint, name, cost.ToString("N0"));
                        row.ButtonAct = () => Teleport(npc);
                    }
                    row.Click += (o, e) => SelectNpc(npc);
                    AddRow(row);
                }
            }
            else if (_tab == ListTab.Exits)
            {
                for (int i = 0; i < _exits.Count; i++)
                {
                    ExitGroup g = _exits[i];
                    string title = ChatGfx.Pretty(g.Title);
                    if (q.Length > 0 && !title.ToLowerInvariant().Contains(q) && !g.Title.ToLowerInvariant().Contains(q)) continue;
                    Point first = g.At[0];
                    var row = new BmRow
                    {
                        Title = title,
                        GlyphIcon = ModernIcons.ArrowRight,
                        Sub = g.At.Count > 1 ? T(ClientTextKeys.ModernChatMapExitsAround, g.At.Count, (int)Math.Round(g.X), (int)Math.Round(g.Y)) : T(ClientTextKeys.ModernChatMapExitAt, first.X, first.Y),
                        ButtonText = T(ClientTextKeys.ModernChatMapView),
                        ButtonHint = T(ClientTextKeys.ModernChatMapViewHint, title),
                    };
                    int dest = g.Dest;
                    string destTitle = g.Title;
                    row.ButtonAct = () => { _data?.Learn(dest, destTitle); SetView(dest, true); };
                    row.Click += (o, e) =>
                    {
                        if (!_layers.Contains(Layer.Exits)) { _layers.Add(Layer.Exits); _layerChips.Set(_layers); }
                        FocusOn(first.X, first.Y, "e:" + first.X + "," + first.Y);
                    };
                    AddRow(row);
                }
            }
            else
            {
                MapDetailsStateEntry det = GameScene.Models?.MapDetails.Get(View);
                for (int i = 0; i < _mobs.Count; i++)
                {
                    MobRow r = _mobs[i];
                    if (q.Length > 0 && !r.Name.ToLowerInvariant().Contains(q)) continue;
                    Difficulty d = DifficultyOf(r.Info);
                    bool sel = _selMob == r.Index;
                    string spawns = r.Zones.Count == 1 ? T(ClientTextKeys.ModernChatMapSpawnOne) : T(ClientTextKeys.ModernChatMapSpawnMany, r.Zones.Count);
                    var row = new BmRow
                    {
                        Title = r.Name,
                        Sub = T(ClientTextKeys.ModernChatMapMobLine, r.Count.ToString("N0"), spawns, T(sel ? ClientTextKeys.ModernChatMapClickNext : ClientTextKeys.ModernChatMapClickShow)),
                        MobImage = r.Info != null ? (int)r.Info.Image : -1,
                        Level = d == Difficulty.Guard ? T(ClientTextKeys.ModernChatMapGuard) : r.Info != null ? r.Level.ToString() : "?",
                        LevelColour = DifficultyColour(d),
                        Selected = sel,
                    };
                    MobRow mr = r;
                    row.Click += (o, e) =>
                    {
                        Point at = mr.Zones[mr.Next++ % mr.Zones.Count];
                        if (!_layers.Contains(Layer.Monsters)) { _layers.Add(Layer.Monsters); _layerChips.Set(_layers); }
                        bool changed = _selMob != mr.Index;
                        _selMob = mr.Index;
                        FocusOn(at.X, at.Y, "m:" + mr.Index);
                        if (changed) { RenderSelPill(); RenderList(); }
                    };
                    AddRow(row);
                }
                if (det == null && DetailsOn()) AddRow(new BmRow { Note = T(ClientTextKeys.ModernChatMapAskingMobs) });
            }
            if (content.Controls.Count == 0)
                AddRow(new BmRow { Note = q.Length > 0 ? T(ClientTextKeys.ModernChatMapNothingMatches) : T(ClientTextKeys.ModernNothingHere) });
            _list.ContentHeight = y;
            _list.Redraw();
        }

        private void PickerMenu()
        {
            if (_pickerPop != null) { ClosePicker(); return; }
            if (_data == null) return;
            var rows = new List<KeyValuePair<int, string>>(_data.Known);
            rows.Sort((a, b) => string.Compare(ChatGfx.Pretty(a.Value), ChatGfx.Pretty(b.Value), StringComparison.CurrentCultureIgnoreCase));
            var items = new List<ChatPopItem>();
            for (int i = 0; i < rows.Count && i < 30; i++)
            {
                int index = rows[i].Key;
                items.Add(new ChatPopItem { Label = ChatGfx.Pretty(rows[i].Value), Note = index == View ? "●" : null, Act = () => { ClosePicker(); SetView(index, true); } });
            }
            if (items.Count == 0) items.Add(new ChatPopItem { Label = T(ClientTextKeys.ModernNothingHere), Act = ClosePicker });
            Point at = new Point(_picker.DisplayLocation.X, _picker.DisplayLocation.Y + _picker.Size.Height + S(4));
            _pickerPop = new ChatPopover("picker", T(ClientTextKeys.ModernChatMapKnown), items, at);
            // open downwards (below the picker)
            _pickerPop.Location = new Point(_pickerPop.Location.X, Math.Min(at.Y, Settings.ScreenHeight - _pickerPop.Size.Height));
        }

        private void ClosePicker()
        {
            if (_pickerPop == null) return;
            if (!_pickerPop.IsDisposed) _pickerPop.Dispose();
            _pickerPop = null;
        }

        internal void PickerOutsideClick(Point p)
        {
            if (_pickerPop != null && !_pickerPop.DisplayRectangle.Contains(p) && !_picker.DisplayRectangle.Contains(p)) ClosePicker();
        }

        // ------------------------------------------------------------------ actions

        /// <summary>BigMapDialog.TeleportToNPC: the cost popup, then a gold check; the server moves you.</summary>
        private void Teleport(ClientNPCInfo npc)
        {
            int cost = GameScene.TeleportToNPCCost, mapIndex = View;
            Popups pop = ModernRoot.Instance?.Popups;
            if (pop == null) return;
            pop.Ask(T(ClientTextKeys.TeleportToNpcForGold, cost.ToString("N0")), yes =>
            {
                if (!yes || IsDisposed) return;
                if ((GameScene.Scene?.MapControl?.Index ?? -1) != mapIndex) return; // you left that map meanwhile
                if (GameScene.Gold < cost)
                {
                    ModernRoot.Instance?.Popups.Confirm(T(ClientTextKeys.NotEnoughGold), PopupButtons.Ok, PopupKind.Warning, null, false, T(ClientTextKeys.ModernChatMapTeleport));
                    return;
                }
                Network.Enqueue(new C.TeleportToNPC { ObjectID = npc.ObjectID });
            }, false, PopupKind.Question, T(ClientTextKeys.ModernChatMapTeleport));
        }

        /// <summary>BigMapDialog.Search: C.SearchMap (3+ characters, 1 s apart).</summary>
        private void ServerSearch()
        {
            string text = _search?.Query ?? string.Empty;
            if (text.Length < SearchMin) return;
            if (CMain.Time < _nextSearch) return;
            _nextSearch = CMain.Time + SearchMs;
            Network.Enqueue(new C.SearchMap { Text = text });
        }

        // ------------------------------------------------------------------ waypoints (per character, per session)

        private List<Point> Waypoints()
        {
            string who = GameScene.User?.Name ?? string.Empty;
            if (_waypoints.TryGetValue(who, out var maps) && maps.TryGetValue(View, out List<Point> list)) return list;
            return new List<Point>();
        }

        private void SaveWaypoints(List<Point> list)
        {
            string who = GameScene.User?.Name ?? string.Empty;
            if (!_waypoints.TryGetValue(who, out var maps)) _waypoints[who] = maps = new Dictionary<int, List<Point>>();
            if (list.Count > 0) maps[View] = list;
            else maps.Remove(View);
        }

        private void AddWaypoint(Point cell)
        {
            var list = new List<Point>(Waypoints());
            if (list.Count >= MaxWaypoints) list.RemoveAt(0);
            list.Add(cell);
            SaveWaypoints(list);
            if (!_layers.Contains(Layer.Waypoints)) { _layers.Add(Layer.Waypoints); _layerChips.Set(_layers); }
            DrawOverlay();
            RenderSide();
        }

        private void RemoveWaypoint(int i)
        {
            var list = new List<Point>(Waypoints());
            if (i < 0 || i >= list.Count) return;
            list.RemoveAt(i);
            SaveWaypoints(list);
            DrawOverlay();
            RenderSide();
        }

        // test hooks (UiTestRunner.Chat.cs)
        internal string TitleText => _title?.Text ?? string.Empty;
        internal int MarkerCount => Marks.Count;
        internal int ListRows => _list?.Content.Controls.Count ?? 0;
        internal string LoadingText => _loading != null && _loading.Visible ? _loading.Text : string.Empty;
        internal void TestSearch(string q) { _search?.SetQuery(q); ServerSearch(); }
        internal void TestTab(int t) { _tab = (ListTab)t; RenderSide(); }
        internal void TestLayer(int layer, bool on) { if (on) _layers.Add((Layer)layer); else _layers.Remove((Layer)layer); _layerChips?.Set(_layers); DrawOverlay(); RenderSide(); }
        internal void TestRow(int i) { if (_list != null && i >= 0 && i < _list.Content.Controls.Count) _list.Content.Controls[i].InvokeMouseClick(EventArgs.Empty); }
        internal void TestRightClickCentre() => RightClickAt(new Point(Vp.Size.Width / 2, Vp.Size.Height / 2));
        internal string TestMarks()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Marks.Count; i++) sb.Append(Marks[i].Kind).Append(Marks[i].Visible ? "" : "(h)").Append(' ');
            return sb.ToString();
        }
    }
}

// Ported from WebClient/src/game/npcMarkup.ts (the page parser), the NpcSession / NpcDrop parts of src/game/npcSession.ts the
// desktop seam does not already hold, NpcDialogState (npc.ts: the page sequence and the conversation log) and MerchantSession
// (merchant.ts: which merchant panels the server has open). One NpcState per modern UI (ModernUi.Npc), built before any window,
// so pages and panels that arrive before a window exists count. UI thread only.
//
// Rules kept from the classic NPCDialog / NPCDropDialog / GameScene handlers: the dialog opens on a page with something to show
// and hides on an empty one (ClassicNpcResponse); every new page hides the goods / drop / craft panels; NPCDialog.ButtonClicked
// ("@Exit" hides without a packet, otherwise C.CallNPC "[key]" gated by GameScene.NPCTime + 5 s); NPCDialog.Hide hides every
// NPC panel and the storage; goods / drop panels open only while the dialog is open; the drop target locks its bag cell until it
// leaves the panel (Sell / Repair / Consign keep the lock until the server's reply, the item services unlock on send).
using System.Text.RegularExpressions;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Windows
{
    public enum NpcSegKind
    {
        Text,
        Link,
        Colour,
        Url,
        Info,
    }

    /// <summary>One piece of a page row (npcMarkup.ts NpcSegment).</summary>
    public sealed class NpcSegment
    {
        public NpcSegKind Kind;
        public string Text = string.Empty;
        /// <summary>Link: "@Key"; Colour: the colour name; Url: the address; Info: "ITEM" / "MONSTER" / "NPC".</summary>
        public string Key = string.Empty;
        public int Index;
        /// <summary>Info: the name after "|" (null = the info's own name).</summary>
        public string Name;
    }

    public sealed class NpcBigButton
    {
        public string Text = string.Empty, Action = string.Empty, Colour = "RoyalBlue";
    }

    /// <summary>A parsed S.NPCResponse page (npcMarkup.ts NpcPage).</summary>
    public sealed class NpcPage
    {
        public static readonly NpcPage Empty = new NpcPage();
        public readonly List<string> Lines = new List<string>();
        public readonly List<List<NpcSegment>> Rows = new List<List<NpcSegment>>();
        public readonly List<NpcBigButton> BigButtons = new List<NpcBigButton>();

        public bool HasContent => Lines.Count > 0 || BigButtons.Count > 0;

        private static readonly Regex Big = new Regex(@"<<((.*?)\/(@.*?))>>", RegexOptions.Compiled);
        private static readonly Regex Inline = new Regex(
            @"<((?:[^<>]*?)\/(?:@[^<>]*?))>|\{(.*?)\/(.*?)\}|\((.*?)\/(.*?)\)|\[(MONSTER|NPC|ITEM):(\d+)(?:\|([^\]]+))?\]|<\$(MONSTER|NPC|ITEM):(\d+)>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>NPCDialog.NewText's tokens on one row, in order.</summary>
        public static List<NpcSegment> ParseLine(string line)
        {
            var output = new List<NpcSegment>();
            line ??= string.Empty;
            int at = 0;
            foreach (Match m in Inline.Matches(line))
            {
                if (m.Index > at) output.Add(new NpcSegment { Kind = NpcSegKind.Text, Text = line.Substring(at, m.Index - at) });
                at = m.Index + m.Length;
                if (m.Groups[1].Success)
                {
                    string v = m.Groups[1].Value;
                    int slash = v.IndexOf("/@", StringComparison.Ordinal);
                    output.Add(new NpcSegment { Kind = NpcSegKind.Link, Text = v.Substring(0, slash), Key = v.Substring(slash + 1) });
                }
                else if (m.Groups[2].Success)
                    output.Add(new NpcSegment { Kind = NpcSegKind.Colour, Text = m.Groups[2].Value, Key = m.Groups[3].Value });
                else if (m.Groups[4].Success)
                    output.Add(new NpcSegment { Kind = NpcSegKind.Url, Text = m.Groups[4].Value, Key = m.Groups[5].Value });
                else
                {
                    bool bracket = m.Groups[6].Success;
                    string kind = (bracket ? m.Groups[6].Value : m.Groups[9].Value).ToUpperInvariant();
                    int.TryParse(bracket ? m.Groups[7].Value : m.Groups[10].Value, out int idx);
                    output.Add(new NpcSegment { Kind = NpcSegKind.Info, Key = kind, Index = idx, Name = bracket && m.Groups[8].Success ? m.Groups[8].Value : null });
                }
            }
            if (at < line.Length) output.Add(new NpcSegment { Kind = NpcSegKind.Text, Text = line.Substring(at) });
            return output;
        }

        /// <summary>GameScene.NPCResponse: the BigButton pass (bottom up; a line holding only big buttons is dropped), then the rows.</summary>
        public static NpcPage Parse(IReadOnlyList<string> page)
        {
            var p = new NpcPage();
            if (page == null) return p;
            p.Lines.AddRange(page);
            for (int i = p.Lines.Count - 1; i >= 0; i--)
            {
                string line = p.Lines[i] ?? string.Empty;
                var found = new List<NpcBigButton>();
                foreach (Match m in Big.Matches(line))
                {
                    string[] action = m.Groups[3].Value.Split('/');
                    found.Add(new NpcBigButton { Text = m.Groups[2].Value, Action = action[0], Colour = action.Length > 1 && action[1].Length > 0 ? action[1] : "RoyalBlue" });
                }
                p.BigButtons.InsertRange(0, found);
                string rest = Big.Replace(line, string.Empty);
                if (rest != line)
                {
                    if (rest.Trim().Length == 0) p.Lines.RemoveAt(i);
                    else p.Lines[i] = rest;
                }
            }
            for (int i = 0; i < p.Lines.Count; i++) p.Rows.Add(ParseLine(p.Lines[i]));
            return p;
        }

        /// <summary>The plain text of a row (info links by their given name or kind + index).</summary>
        public static string RowText(List<NpcSegment> row)
        {
            var sb = new System.Text.StringBuilder();
            foreach (NpcSegment s in row) sb.Append(s.Kind == NpcSegKind.Info ? s.Name ?? s.Key + " " + s.Index : s.Text);
            return sb.ToString();
        }

        /// <summary>A row holding only page links (and blanks) gives its links as option cards (npc.ts linkOnly).</summary>
        public static List<NpcSegment> LinkOnly(List<NpcSegment> row)
        {
            var output = new List<NpcSegment>();
            foreach (NpcSegment s in row)
            {
                if (s.Kind == NpcSegKind.Link) output.Add(s);
                else if (s.Kind != NpcSegKind.Text || s.Text.Trim().Length > 0) return null;
            }
            return output.Count > 0 ? output : null;
        }
    }

    /// <summary>The merchant tab a panel belongs to (merchant.ts Tab).</summary>
    public enum MerchantTab
    {
        Buy,
        Sell,
        Repair,
        BuyBack,
    }

    /// <summary>The NPC session the modern windows share (see the file header).</summary>
    public sealed class NpcState
    {
        public const int LogMax = 30, CallGateMs = 5000;
        public const string BuyBackKey = "@BUYBACK";
        /// <summary>No panel (PanelType has no None member).</summary>
        public const PanelType NoPanel = (PanelType)255;

        private string _pendingChoice = string.Empty;
        private long _pendingAt;
        private long _merchantCloseAt;

        /// <summary>NPCDialog.Visible.</summary>
        public bool Open { get; private set; }
        public NpcPage Page { get; private set; } = NpcPage.Empty;
        /// <summary>Bumped on every page (a redraw of the same page keeps it).</summary>
        public int Seq { get; private set; }
        /// <summary>The Key of the last C.CallNPC this UI sent ("@Main" for a conversation opened from the world).</summary>
        public string LastPageKey { get; private set; } = "@Main";
        public string NpcRawName { get; private set; } = string.Empty;
        public readonly List<(string Who, string Text)> Log = new List<(string, string)>();
        public int LogVersion { get; private set; }

        // ---- the drop panel (NPCDropDialog) ----
        /// <summary>The panel type the server opened (Sell / Repair / SpecialRepair / Consign / item services); None = closed.</summary>
        public PanelType DropType { get; private set; } = NoPanel;
        private int _dropSlot = -1;

        // ---- merchant panels (MerchantSession) ----
        public readonly List<UserItem> Goods = new List<UserItem>();
        public PanelType GoodsType { get; private set; }
        public bool GoodsOpen { get; private set; }
        public bool UsePearls { get; private set; }
        public string GoodsKey { get; private set; } = string.Empty;
        public bool SellOpen { get; private set; }
        /// <summary>PanelType.Repair / SpecialRepair while a repair page is open, else None.</summary>
        public PanelType RepairType { get; private set; } = NoPanel;
        /// <summary>Bumps on every new page / close: requests queued from an older panel are dropped.</summary>
        public int Gen { get; private set; }
        public MerchantTab? Focus { get; private set; }
        public int FocusSeq { get; private set; }

        // ---- craft ----
        public readonly List<UserItem> CraftGoods = new List<UserItem>();
        public int CraftSeq { get; private set; }

        /// <summary>Anything shown changed (page, log, panels).</summary>
        public event Action Changed;
        /// <summary>NPCDialog.Hide: every NPC window closes.</summary>
        public event Action Closed;

        private static WindowManager Wm => ModernRoot.Instance?.Wm;

        public static uint NpcId => GameScene.NPCID;

        /// <summary>The NPC's name as the server sent it (the map object, else the last one known).</summary>
        public string RawName
        {
            get
            {
                if (MapControl.Objects != null && MapControl.Objects.TryGetValue(NpcId, out MapObject ob) && ob != null) NpcRawName = ob.Name ?? NpcRawName;
                return NpcRawName;
            }
        }

        public (string Name, string Place) Name => NpcFmt.Split(RawName.Length > 0 ? RawName : W.T(ClientTextKeys.ModernNpcFallbackName));

        private void Emit() => Changed?.Invoke();

        // ---------------------------------------------------------------- server

        /// <summary>S.NPCResponse (Models.Npc.Page is set).</summary>
        public void Response(List<string> lines)
        {
            NpcPage page = NpcPage.Parse(lines);
            if (!page.HasContent)
            {
                Close();
                return;
            }
            bool wasOpen = Open;
            if (!wasOpen)
            {
                Log.Clear();
                LogVersion++;
                if (CMain.Time > _pendingAt + CallGateMs) LastPageKey = "@Main";
            }
            Open = true;
            Page = page;
            Seq++;
            _ = RawName;
            NewPageLog(page);
            // GameScene.NPCResponse: the goods, drop and craft panels hide; the merchant window waits a moment for the next panel
            ClearPanels();
            if (Wm != null && Wm.IsOpen(MerchantWindow.WindowKey)) _merchantCloseAt = CMain.Time + 300;
            Wm?.Close(NpcBoxWindow.WindowKey);
            Wm?.Close(CraftWindow.WindowKey);
            Emit();
            Wm?.Open(NpcWindow.WindowKey);
        }

        /// <summary>S.NPCGoods / S.NPCPearlGoods (GameScene.NPCPanelType is set; only while the dialog is open).</summary>
        public void GoodsArrived(List<UserItem> list, bool pearls)
        {
            if (!Open) return;
            PanelType type = pearls ? PanelType.Buy : GameScene.NPCPanelType;
            if (type == PanelType.Craft && !pearls)
            {
                CraftGoods.Clear();
                if (list != null) CraftGoods.AddRange(list);
                CraftSeq++;
                Emit();
                Wm?.Open(CraftWindow.WindowKey);
                return;
            }
            if (type != PanelType.Buy && type != PanelType.BuySub) return;
            Goods.Clear();
            if (list != null) Goods.AddRange(list);
            GoodsType = type;
            UsePearls = pearls;
            GoodsOpen = true;
            GoodsKey = NormKey(LastPageKey);
            Opened(GoodsKey == BuyBackKey ? MerchantTab.BuyBack : MerchantTab.Buy);
        }

        /// <summary>The server opened NPCDropDialog with a type (only while the dialog is open).</summary>
        public void DropPanel(PanelType type)
        {
            if (!Open) return;
            ReleaseDrop();
            switch (type)
            {
                case PanelType.Sell:
                    SellOpen = true;
                    DropType = type;
                    Opened(MerchantTab.Sell);
                    return;
                case PanelType.Repair:
                case PanelType.SpecialRepair:
                    RepairType = type;
                    DropType = type;
                    Opened(MerchantTab.Repair);
                    return;
            }
            if (!NpcBoxWindow.Serves(type)) return;
            DropType = type;
            Emit();
            Wm?.Open(NpcBoxWindow.WindowKey);
        }

        private void Opened(MerchantTab tab)
        {
            _merchantCloseAt = 0;
            if (Focus == null) { Focus = tab; FocusSeq++; }
            Emit();
            Wm?.Open(MerchantWindow.WindowKey);
        }

        private bool MerchantActive => GoodsOpen || SellOpen || RepairType != NoPanel;

        public bool MerchantLive(MerchantTab tab)
        {
            switch (tab)
            {
                case MerchantTab.Buy: return GoodsOpen && GoodsKey != BuyBackKey;
                case MerchantTab.BuyBack: return GoodsOpen && GoodsKey == BuyBackKey;
                case MerchantTab.Sell: return SellOpen;
                default: return RepairType != NoPanel;
            }
        }

        /// <summary>IGameUi.IsNpcDropOpen: a sell / repair / item-service panel is open (Alt + click quick sell).</summary>
        public bool DropOpen => Open && DropType != NoPanel;

        /// <summary>Per frame (ModernUi.Process via the NPC window manager hook): the merchant window's close grace.</summary>
        public void Process()
        {
            if (_merchantCloseAt > 0 && CMain.Time >= _merchantCloseAt)
            {
                _merchantCloseAt = 0;
                if (!MerchantActive) Wm?.Close(MerchantWindow.WindowKey);
            }
        }

        // ---------------------------------------------------------------- calls

        /// <summary>NPCDialog.ButtonClicked: "@Exit" hides the dialog (no packet); otherwise C.CallNPC "[key]" behind NPCTime.</summary>
        public bool Call(string key, string choiceText = null)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (string.Equals(key, "@Exit", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                return true;
            }
            if (CMain.Time <= GameScene.NPCTime) return false;
            GameScene.NPCTime = CMain.Time + CallGateMs;
            LastPageKey = key;
            if (!string.IsNullOrWhiteSpace(choiceText))
            {
                _pendingChoice = choiceText.Trim();
                _pendingAt = CMain.Time;
            }
            Network.Enqueue(new C.CallNPC { ObjectID = NpcId, Key = "[" + key + "]" });
            return true;
        }

        private void NewPageLog(NpcPage page)
        {
            if (_pendingChoice.Length > 0) Push(W.T(ClientTextKeys.ModernNpcYou), _pendingChoice);
            _pendingChoice = string.Empty;
            var said = new List<string>();
            foreach (List<NpcSegment> row in page.Rows)
            {
                if (NpcPage.LinkOnly(row) != null) continue;
                var sb = new System.Text.StringBuilder();
                foreach (NpcSegment seg in row) sb.Append(seg.Kind == NpcSegKind.Info ? NpcWindow.LinkName(seg.Key, seg.Index, seg.Name) : seg.Text);
                string t = sb.ToString().Trim();
                if (t.Length > 0) said.Add(t);
                if (said.Count >= 2) break;
            }
            if (said.Count > 0) Push(Name.Name, string.Join(" ", said));
        }

        private void Push(string who, string text)
        {
            Log.Add((who, text));
            if (Log.Count > LogMax) Log.RemoveRange(0, Log.Count - LogMax);
            LogVersion++;
        }

        /// <summary>NPCDialog.Hide: every NPC panel (and the storage) hides with it; no packet.</summary>
        public void Close()
        {
            bool was = Open;
            Open = false;
            _pendingChoice = string.Empty;
            ClearPanels();
            _merchantCloseAt = 0;
            WindowManager wm = Wm;
            if (wm != null)
            {
                wm.Close(MerchantWindow.WindowKey);
                wm.Close(NpcBoxWindow.WindowKey);
                wm.Close(CraftWindow.WindowKey);
                // classic NPCDialog.Hide also closed the refine, awaken and trust merchant windows (their own close paths cancel)
                wm.Close(RefineWindow.WindowKey);
                wm.Close(AwakenWindow.WindowKey);
                wm.Close(TrustWindow.WindowKey);
                wm.Close(NpcWindow.WindowKey);
            }
            NpcFmt.Ui?.Storage?.End();
            if (was) Closed?.Invoke();
            Emit();
        }

        private void ClearPanels()
        {
            ReleaseDrop();
            DropType = NoPanel;
            Goods.Clear();
            GoodsOpen = false;
            SellOpen = false;
            RepairType = NoPanel;
            Focus = null;
            CraftGoods.Clear();
            Gen++;
        }

        public static string NormKey(string k)
        {
            if (string.IsNullOrEmpty(k)) return string.Empty;
            k = k.Trim();
            if (k.StartsWith("[")) k = k.Substring(1);
            if (k.EndsWith("]")) k = k.Substring(0, k.Length - 1);
            return k.ToUpperInvariant();
        }

        /// <summary>The page's links by normalised key (merchant.ts pageLinks).</summary>
        public Dictionary<string, (string Key, string Text)> PageLinks()
        {
            var output = new Dictionary<string, (string, string)>();
            foreach (List<NpcSegment> row in Page.Rows)
                foreach (NpcSegment s in row)
                    if (s.Kind == NpcSegKind.Link) output[NormKey(s.Key)] = (s.Key, s.Text);
            foreach (NpcBigButton b in Page.BigButtons) output[NormKey(b.Action)] = (b.Action, b.Text);
            return output;
        }

        // ---------------------------------------------------------------- the drop target (NPCDropDialog.TargetItem / OldCell)

        public UserItem DropItem => GameScene.Models.Npc.DropTarget;
        public int DropSlot => _dropSlot;

        /// <summary>NPCDropDialog.ItemCell_Click with a bag item: it sits in the panel and its bag cell locks.</summary>
        public bool TakeDrop(int bagSlot)
        {
            UserItem item = ItemMoves.GetItem(MirGridType.Inventory, bagSlot);
            if (item == null || DropType == NoPanel || ItemMoves.IsLocked(MirGridType.Inventory, bagSlot)) return false;
            ReleaseDrop();
            _dropSlot = bagSlot;
            ItemMoves.SetLocked(MirGridType.Inventory, bagSlot, true);
            ItemMoves.SetItem(MirGridType.DropPanel, 0, item);
            Emit();
            return true;
        }

        /// <summary>The target leaves the panel: its bag cell unlocks.</summary>
        public void ReleaseDrop()
        {
            if (_dropSlot >= 0) ItemMoves.SetLocked(MirGridType.Inventory, _dropSlot, false);
            _dropSlot = -1;
            if (GameScene.Models.Npc.DropTarget != null) ItemMoves.SetItem(MirGridType.DropPanel, 0, null);
        }

        /// <summary>After a send: the panel empties; <paramref name="keepLock"/> leaves the bag cell locked until the server's reply.</summary>
        public void DropSent(bool keepLock)
        {
            if (!keepLock && _dropSlot >= 0) ItemMoves.SetLocked(MirGridType.Inventory, _dropSlot, false);
            _dropSlot = -1;
            if (GameScene.Models.Npc.DropTarget != null) ItemMoves.SetItem(MirGridType.DropPanel, 0, null);
            Emit();
        }

        /// <summary>A box window closed by hand: the panel hides (NPCDropDialog.Hide).</summary>
        public void HideDrop()
        {
            ReleaseDrop();
            if (NpcBoxWindow.Serves(DropType)) DropType = NoPanel;
            Emit();
        }

        /// <summary>
        /// The merchant window closed (by hand or with the session): its goods / sell / repair panels hide like NPCGoodsDialog /
        /// NPCDropDialog.Hide, so IsNpcDropOpen stops answering for them, and Gen bumps (pacer buys queued from them drop out).
        /// </summary>
        public void HideMerchant()
        {
            bool merchantDrop = DropType == PanelType.Sell || DropType == PanelType.Repair || DropType == PanelType.SpecialRepair;
            if (!MerchantActive && !merchantDrop) return;
            if (merchantDrop) DropType = NoPanel;
            Goods.Clear();
            GoodsOpen = false;
            SellOpen = false;
            RepairType = NoPanel;
            Focus = null;
            _merchantCloseAt = 0;
            Gen++;
            Emit();
        }

        public void Dispose()
        {
            ReleaseDrop();
            Changed = null;
            Closed = null;
        }
    }
}

using Client.MirNetwork;
using Client.MirScenes.Modern.Hud;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;
using C = ClientPackets;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// Phase 4 area "guild" (worker A): the guild window (+ member details, log, war list), territories (C.GuildTerritoryAction),
    /// ranking (+ S.RankingsDetail, the guild board), and the HUD timer / compass / roll / notice. State: Models.Guild,
    /// Models.Rankings (GameScene writes both before these members run). Window keys "guild", "territories", "ranking" (the
    /// game menu shows them once registered).
    /// </summary>
    public sealed partial class ModernUi
    {
        /// <summary>The guild buff countdowns (from login on), the territory page book and the HUD extras; null before Build.</summary>
        internal GuildBuffClock GuildClock { get; private set; }
        internal TerritoryBook Territories { get; private set; }
        internal HudExtras HudExtras { get; private set; }

        partial void RegisterGuildWindows()
        {
            GuildClock?.Dispose();
            GuildClock = new GuildBuffClock(GameScene.Models?.Guild);
            Territories = new TerritoryBook();
            GuildBuffClock clock = GuildClock;
            TerritoryBook book = Territories;
            Root.Wm.Register(GuildWindow.WindowKey, () => new GuildWindow(clock));
            Root.Wm.Register(TerritoriesWindow.WindowKey, () => new TerritoriesWindow(book));
            Root.Wm.Register(RankingWindow.WindowKey, () => new RankingWindow());
            Root.Wm.Register(NoticeWindow.WindowKey, () => new NoticeWindow());
            HudExtras = new HudExtras(Root.Hud); // below the bottom HUD (BuildHud runs after the window catalog)
        }

        partial void DisposeGuild()
        {
            GuildClock?.Dispose();
            GuildClock = null;
            Territories = null;
            HudExtras = null; // a child of the root: disposed with it
        }

        /// <summary>KeybindOptions.Guilds / Ranking (toggle their windows).</summary>
        private partial bool GuildKeyBind(KeybindOptions function)
        {
            switch (function)
            {
                case KeybindOptions.Guilds:
                    Root.Wm.Toggle(GuildWindow.WindowKey);
                    return true;
                case KeybindOptions.Ranking:
                    Root.Wm.Toggle(RankingWindow.WindowKey);
                    return true;
            }
            return false;
        }

        /// <summary>The built and open guild window, or null (events for a closed window are read from the model on open).</summary>
        private GuildWindow OpenGuild => Root != null && Root.Wm.IsOpen(GuildWindow.WindowKey) ? Root.Wm.Get(GuildWindow.WindowKey) as GuildWindow : null;

        /// <summary>The active guild buffs changed (Models.Guild.BuffsChanged already fired).</summary>
        public void GuildActiveStatsChanged() => OpenGuild?.Refresh();
        /// <summary>S.GuildStatus, before the model update (joined = no guild before); the model's request flags are already set.</summary>
        public void GuildStatusChanging(S.GuildStatus p, bool joined) => OpenGuild?.StatusChanging(p);
        public void GuildStatusChanged(S.GuildStatus p) => OpenGuild?.Refresh();
        public void GuildMembersChanged() => OpenGuild?.Refresh();
        /// <summary>S.GuildNoticeChange: Models.Guild.Notice / NoticeStale already updated (NoticeChanged fired for new lines).</summary>
        public void GuildNoticeChange(S.GuildNoticeChange p) => OpenGuild?.NoticeChange(p);
        /// <summary>S.GuildMemberChange: Models.Guild already edited; Models.Guild.MembersChanged fires right after this.</summary>
        public void GuildMemberChange(S.GuildMemberChange p, Models.GuildMemberEdit edit) => OpenGuild?.MemberChange(p);

        /// <summary>S.GuildTerritoryPage: filed in the page book; an unasked page (an NPC) opens the window (GameScene.GuildTerritoryPage).</summary>
        public void GuildTerritoryPage(S.GuildTerritoryPage p)
        {
            if (Root == null || Root.IsDisposed || Territories == null || p == null) return;
            Territories.File(p);
            if (!Territories.LastUnasked || Root.Wm.IsOpen(TerritoriesWindow.WindowKey)) return; // replies to the window's own requests never reopen it
            Territories.ServerOpened = true;
            Root.Wm.Open(TerritoriesWindow.WindowKey);
        }

        /// <summary>S.Rankings: Models.Rankings already holds the page and fired Changed (the open ranking window follows it).</summary>
        public void RankingsReceived(List<RankCharacterInfo> listings, S.Rankings p)
        {
        }

        public void SetTimer(S.SetTimer p) => HudExtras?.SetTimer(p);
        public void ExpireTimer(string key) => HudExtras?.ExpireTimer(key);
        public void SetCompass(Point location) => HudExtras?.SetCompass(location);
        public void Roll(S.Roll p) => HudExtras?.Roll(p);

        /// <summary>S.UpdateNotice: the notice window (NoticeDialog.Update; an empty message is ignored).</summary>
        public void ShowNotice(S.UpdateNotice p)
        {
            if (Root == null || Root.IsDisposed || p?.Notice == null || string.IsNullOrWhiteSpace(p.Notice.Message)) return;
            (Root.Wm.Get(NoticeWindow.WindowKey) as NoticeWindow)?.Update(p.Notice.Title, p.Notice.Message);
        }

        // ---- prompts (classic: MirMessageBox / MirInputBox in Classic\GameScene.ClassicPrompts.cs)

        public void GuildInviteRequest(S.GuildInvite p)
        {
            if (Root == null || Root.IsDisposed || p == null) return;
            Root.Popups.Request(p.Name, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.JoinGuildPrompt, p.Name), 30,
                () => Network.Enqueue(new C.GuildInvite { AcceptInvite = true }),
                () => { if (!_ending) Network.Enqueue(new C.GuildInvite { AcceptInvite = false }); });
        }

        /// <summary>S.GuildNameRequest: letters and digits only (classic blocks other keys while typing; a backslash is refused).</summary>
        public void GuildNameRequest()
        {
            if (Root == null || Root.IsDisposed) return;
            Root.Popups.Input(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.EnterGuildNameLengthLimit), string.Empty, 20, text =>
            {
                if (text == null) return;
                var sb = new System.Text.StringBuilder(text.Length);
                for (int i = 0; i < text.Length; i++)
                    if (text[i] < 128 && char.IsLetterOrDigit(text[i])) sb.Append(text[i]);
                Network.Enqueue(new C.GuildNameReturn { Name = sb.ToString() });
            });
        }

        public void GuildWarRequest()
        {
            if (Root == null || Root.IsDisposed) return;
            Root.Popups.Input(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.EnterGuildToWarWith), string.Empty, 20, text =>
            {
                if (text != null) Network.Enqueue(new C.GuildWarReturn { Name = text });
            });
        }
    }
}

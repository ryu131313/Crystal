using Client.MirControls;
using Client.MirScenes;
using Client.MirScenes.Modern;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Hud;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.RenderTest
{
    /// <summary>
    /// Phase 4 area "guild" steps (modern only): the guild window (S.GuildStatus / the member list / the notice / buffs / wars / log
    /// feed it; overview, notice edit + save + echo, members with details, promote and the server's answer, the rank picker, the
    /// ranks page and an option switch, buffs and obtain, storage with the list and a deposit, wars, log), the territories window
    /// (opened by an unasked S.GuildTerritoryPage, the second page asked and filed, the gated rent, list-for-sale on the guild's own
    /// territory), the ranking window (the board, details, pick, next page, a class tab, the guild board) and the HUD extras (two
    /// timers and an expiry, the compass, the die roll and its NPC call, a frozen poison with tints, the notice board). Starts from
    /// a fresh game and closes everything at the end.
    /// </summary>
    internal static partial class UiTestRunner
    {
        static partial void AddGuildSteps(List<Step> steps)
        {
            GuStep(steps, "gu_logout", () => { Root?.Popups.CloseAll(); Feed(new S.LogOutSuccess { Characters = new List<SelectInfo>() }); }, 800);
            GuStep(steps, "gu_enter", () =>
            {
                EnterGame();
                SetScale(1f, false);
                Feed(new S.ServerRules { BuyGTGold = 5000000, ExtendGT = 1000000, GTDays = 7 });
            }, 1500);

            // ---- guild window
            GuStep(steps, "gu_guild_data", () =>
            {
                Feed(new S.GuildStatus
                {
                    GuildName = "Dragon Lords", GuildRankName = "Leader", Level = 5, Experience = 1200, MaxExperience = 5000, Gold = 250000, SparePoints = 3,
                    MemberCount = 5, MaxMembers = 50, MyOptions = (GuildRankOptions)255, MyRankId = 0,
                });
                Feed(new S.GuildMemberChange { Status = 255, Ranks = GuRanks(false) });
                Feed(new S.GuildNoticeChange { update = 1, notice = new List<string> { "Welcome to the Dragon Lords!", "", "Raid tonight at 20:00 in the Bug Cave.", "Bring potions." } });
                Feed(new S.GuildBuffList { GuildBuffs = GuBuffInfos(), ActiveBuffs = new List<GuildBuff> { new GuildBuff { Id = 1, Active = true, ActiveTimeRemaining = 42 } } });
                Feed(new S.GuildWarList { Wars = new List<ClientGuildWar> { new ClientGuildWar { GuildName = "Shadow Clan", MillisecondsLeft = 3 * 3600000L + 120000 } } });
                long now = CMain.Now.ToBinary(), hour = CMain.Now.AddHours(-2).ToBinary();
                Feed(new S.GuildLog { Entries = new List<ClientGuildLogEntry>
                {
                    new ClientGuildLogEntry { Time = hour, Kind = 0, Text = "Carol joined the guild." },
                    new ClientGuildLogEntry { Time = hour, Kind = 8, Text = "Bob donated 50,000 gold." },
                    new ClientGuildLogEntry { Time = now, Kind = 6, Text = "War declared on Shadow Clan." },
                    new ClientGuildLogEntry { Time = now, Kind = 4, Text = "UiTester changed the notice." },
                } });
            }, 500);
            GuStep(steps, "gu_open", () => PressBind(KeybindOptions.Guilds), 900);
            GuStep(steps, "gu_details", () => Feed(new S.GuildMemberDetails { Part = 0, PartCount = 1, Members = new List<ClientGuildMemberDetail>
            {
                new ClientGuildMemberDetail { Name = "UiTester", Class = MirClass.Warrior, Level = 30, Online = true, RankIndex = 0 },
                new ClientGuildMemberDetail { Name = "Alice", Class = MirClass.Wizard, Level = 41, Online = true, RankIndex = 0 },
                new ClientGuildMemberDetail { Name = "Bob", Class = MirClass.Taoist, Level = 35, LastLogin = CMain.Now.AddDays(-3).ToBinary(), RankIndex = 1 },
                new ClientGuildMemberDetail { Name = "Carol", Class = MirClass.Archer, Level = 22, Online = true, RankIndex = 2 },
            } }), 700);
            GuStep(steps, "gu_notice_edit", () => ClickAt(Centre(GuWin?.NoticeButton)), 600);
            GuStep(steps, "gu_notice_type", () => { if (GuWin?.NoticeField != null) GuWin.NoticeField.Text = "Welcome to the Dragon Lords!\r\nRaid moved to 21:00."; }, 600);
            GuStep(steps, "gu_notice_save", () => ClickAt(Centre(GuWin?.NoticeButton)), 600);
            GuStep(steps, "gu_notice_echo", () => Feed(new S.GuildNoticeChange { update = 1, notice = new List<string> { "Welcome to the Dragon Lords!", "Raid moved to 21:00." } }), 700);
            GuStep(steps, "gu_members", () => ClickAt(Centre(GuWin?.NavRow(GuildWindow.Page.Members))), 800);
            GuStep(steps, "gu_member_pick", () => ClickAt(Centre(GuRow(GuWin?.MemberRows, "Bob"))), 700);
            GuStep(steps, "gu_promote", () => ClickAt(Centre(FindButton(GuWin, W(ClientTextKeys.ModernGuildPromote)))), 600);
            GuStep(steps, "gu_promote_yes", () => Key(Keys.Enter), 600);
            GuStep(steps, "gu_promote_reply", () => Feed(new S.GuildMemberChange { Status = 255, Ranks = GuRanks(true) }), 700);
            GuStep(steps, "gu_rank_picker", () => ClickAt(Centre(FindButton(GuWin, W(ClientTextKeys.ModernGuildSetRank)))), 700);
            GuStep(steps, "gu_rank_picker_cancel", () => ClickAt(Centre(FindButton(GuWin, W(ClientTextKeys.ModernCancel)))), 500);
            GuStep(steps, "gu_ranks", () => ClickAt(Centre(GuWin?.NavRow(GuildWindow.Page.Ranks))), 800);
            GuStep(steps, "gu_rank_toggle", () => ClickAt(Centre(GuSwitch(GuWin?.RankRows, 2, 1))), 600);
            GuStep(steps, "gu_rank_toggle_reply", () =>
            {
                GuildRank member = GuRanks(true)[2];
                member.Options = GuildRankOptions.CanRecruit | GuildRankOptions.CanStoreItem;
                Feed(new S.GuildMemberChange { Status = 7, Ranks = new List<GuildRank> { member } });
            }, 700);
            GuStep(steps, "gu_buffs", () => ClickAt(Centre(GuWin?.NavRow(GuildWindow.Page.Buffs))), 800);
            GuStep(steps, "gu_buff_obtain", () => ClickAt(Centre(FindButton(GuWin?.BuffGrid, W(ClientTextKeys.ModernGuildObtain)))), 600);
            GuStep(steps, "gu_buff_reply", () => Feed(new S.GuildBuffList { ActiveBuffs = new List<GuildBuff> { new GuildBuff { Id = 2, Active = false } } }), 700);
            GuStep(steps, "gu_storage", () => ClickAt(Centre(GuWin?.NavRow(GuildWindow.Page.Storage))), 700);
            GuStep(steps, "gu_storage_list", () =>
            {
                var items = new GuildStorageItem[112];
                items[0] = new GuildStorageItem { Item = Item("UiTestSword") };
                items[1] = new GuildStorageItem { Item = Item("UiTestPotion", 50) };
                items[15] = new GuildStorageItem { Item = Item("UiTestRing") };
                Feed(new S.GuildStorageList { Items = items });
            }, 800);
            GuStep(steps, "gu_deposit", () => ClickAt(Centre(GuWin?.DepositButton)), 600);
            GuStep(steps, "gu_deposit_ok", () => Key(Keys.Enter), 600);
            GuStep(steps, "gu_wars", () => ClickAt(Centre(GuWin?.NavRow(GuildWindow.Page.Wars))), 800);
            GuStep(steps, "gu_log", () => ClickAt(Centre(GuWin?.NavRow(GuildWindow.Page.Log))), 800);
            GuStep(steps, "gu_overview_again", () => ClickAt(Centre(GuWin?.NavRow(GuildWindow.Page.Overview))), 800);
            GuStep(steps, "gu_close", () => PressBind(KeybindOptions.Guilds), 500);

            // ---- territories
            GuStep(steps, "gt_npc_page", () => Feed(new S.GuildTerritoryPage { length = 9, Listings = GuTerritories(0) }), 900);
            GuStep(steps, "gt_page2", () => Feed(new S.GuildTerritoryPage { length = 9, Listings = GuTerritories(1) }), 800);
            GuStep(steps, "gt_pick_vacant", () => ClickAt(Centre(GuGtRow(1))), 700);
            GuStep(steps, "gt_pick_own", () => ClickAt(Centre(GuGtRow(0))), 700);
            GuStep(steps, "gt_list_for_sale", () => ClickAt(Centre(FindButton(GuGt?.Detail, W(ClientTextKeys.ModernGuildGtListForSale)))), 600);
            GuStep(steps, "gt_sale_price", () =>
            {
                ModernTextField f = Find<ModernTextField>(Root?.Popups);
                if (f != null) f.Text = "3m";
                Key(Keys.Enter);
            }, 700);
            GuStep(steps, "gt_close", () => Root?.Wm.Close(TerritoriesWindow.WindowKey), 500);

            // ---- ranking
            GuStep(steps, "rk_open", () => PressBind(KeybindOptions.Ranking), 800);
            GuStep(steps, "rk_reply", () => { Feed(GuRankings(0, 0)); Feed(GuRankingDetails(0)); }, 900);
            GuStep(steps, "rk_pick", () => ClickAt(Centre(GuRk?.Row(1))), 700);
            GuStep(steps, "rk_next", () => ClickAt(Centre(GuRk?.NextButton)), 900);
            GuStep(steps, "rk_next_reply", () => Feed(GuRankings(0, 20)), 800);
            GuStep(steps, "rk_tab_wizard", () => GuTab(2), 800);
            GuStep(steps, "rk_tab_wizard_reply", () => Feed(GuRankings(2, 0)), 800);
            GuStep(steps, "rk_guilds", () => GuTab(6), 700);
            GuStep(steps, "rk_guilds_reply", () => Feed(new S.GuildRankings { Page = 0, Total = 3, Guilds = new List<ClientGuildRanking>
            {
                new ClientGuildRanking { Name = "Shadow Clan", Level = 9, ExpPerTenThousand = 6400, Members = 42, Leader = "Nyx" },
                new ClientGuildRanking { Name = "Dragon Lords", Level = 5, ExpPerTenThousand = 2400, Members = 5, Leader = "UiTester" },
                new ClientGuildRanking { Name = "Lone Wolves", Level = 2, ExpPerTenThousand = 900, Members = 1, Leader = "Wolf" },
            } }), 800);
            GuStep(steps, "rk_close", () => PressBind(KeybindOptions.Ranking), 500);

            // ---- HUD extras
            GuStep(steps, "hx_timers", () =>
            {
                Feed(new S.SetTimer { Key = "event", Type = 1, Seconds = 75 });
                Feed(new S.SetTimer { Key = "boss", Type = 0, Seconds = 8 });
            }, 1200);
            GuStep(steps, "hx_expire", () => Feed(new S.ExpireTimer { Key = "boss" }), 2200);
            GuStep(steps, "hx_compass", () =>
            {
                Point u = GameScene.User.CurrentLocation;
                Feed(new S.SetCompass { Location = new Point(u.X + 10, u.Y - 6) });
            }, 700);
            GuStep(steps, "hx_roll", () => Feed(new S.Roll { Type = 0, Page = "@ROLLED", Result = 4, AutoRoll = false }), 700);
            GuStep(steps, "hx_roll_click", () => ClickAt(Centre(GuHx?.RollCard)), 1200);
            GuStep(steps, "hx_roll_result", () => { }, 2000);
            GuStep(steps, "hx_roll_close", () => ClickAt(Centre(GuHx?.RollCard)), 500);
            GuStep(steps, "hx_poison", () =>
            {
                Feed(new S.PoisonStatus { Poisons = new List<ClientPoisonStatus> { new ClientPoisonStatus { Type = PoisonType.Frozen, TimeLeft = 6000, Value = 0 } } });
                Feed(new S.Poisoned { Poison = PoisonType.Frozen | PoisonType.Green });
            }, 900);
            GuStep(steps, "hx_poison_end", () => Feed(new S.Poisoned { Poison = PoisonType.None }), 600);
            GuStep(steps, "hx_notice", () => Feed(new S.UpdateNotice { Notice = new Notice
            {
                Title = "Server news",
                Message = "Welcome back!\r\n{Double EXP/Yellow} all weekend.\r\nPatch notes: (forum/https://example.com/news)\r\n\r\nHave fun.",
            } }), 800);
            GuStep(steps, "hx_cleanup", () =>
            {
                Root?.Wm.Close(NoticeWindow.WindowKey);
                Feed(new S.ExpireTimer { Key = "event" });
                Feed(new S.SetCompass { Location = Point.Empty });
                Root?.Popups.CloseAll();
                Root?.Wm.CloseAll();
            }, 1500);
            GuStep(steps, "gu_end", () => { }, 300);
        }

        /// <summary>A modern-only step; it first logs the guild-area state the previous step left (after its delay), so the lines sit
        /// under the previous step's header.</summary>
        private static void GuStep(List<Step> steps, string name, Action act, int delayMs = 600) =>
            steps.Add(new Step { Name = name, Act = () => { GuDump(); act(); }, DelayMs = delayMs, ModernOnly = true });

        private static GuildWindow GuWin => Root?.Wm.IsOpen(GuildWindow.WindowKey) == true ? Root.Wm.Get(GuildWindow.WindowKey) as GuildWindow : null;
        private static TerritoriesWindow GuGt => Root?.Wm.IsOpen(TerritoriesWindow.WindowKey) == true ? Root.Wm.Get(TerritoriesWindow.WindowKey) as TerritoriesWindow : null;
        private static RankingWindow GuRk => Root?.Wm.IsOpen(RankingWindow.WindowKey) == true ? Root.Wm.Get(RankingWindow.WindowKey) as RankingWindow : null;
        private static HudExtras GuHx => (GameScene.Ui as ModernUi)?.HudExtras;

        /// <summary>The guild's ranks: Leader (UiTester, Alice), Officer (Bob, or empty once promoted), Member (Carol, Dave; Bob after).</summary>
        private static List<GuildRank> GuRanks(bool bobPromoted)
        {
            var leader = new GuildRank { Name = "Leader", Index = 0, Options = (GuildRankOptions)255 };
            leader.Members.Add(new GuildMember { Name = "UiTester", Online = true, LastLogin = CMain.Now });
            leader.Members.Add(new GuildMember { Name = "Alice", Online = true, LastLogin = CMain.Now });
            var officer = new GuildRank { Name = "Officer", Index = 1, Options = GuildRankOptions.CanRecruit | GuildRankOptions.CanKick | GuildRankOptions.CanStoreItem | GuildRankOptions.CanRetrieveItem };
            var member = new GuildRank { Name = "Member", Index = 2, Options = GuildRankOptions.CanStoreItem };
            var bob = new GuildMember { Name = "Bob", Online = false, LastLogin = CMain.Now.AddDays(-3) };
            if (bobPromoted) leader.Members.Add(bob);
            else officer.Members.Add(bob);
            member.Members.Add(new GuildMember { Name = "Carol", Online = true, LastLogin = CMain.Now });
            member.Members.Add(new GuildMember { Name = "Dave", Online = false, LastLogin = CMain.Now.AddHours(-5) });
            return new List<GuildRank> { leader, officer, member };
        }

        private static List<GuildBuffInfo> GuBuffInfos()
        {
            var a = new GuildBuffInfo { Id = 1, Icon = 0, Name = "Iron Skin", LevelRequirement = 1, PointsRequirement = 1, TimeLimit = 60, ActivationCost = 10000 };
            a.Stats[Stat.MaxAC] = 5;
            a.Stats[Stat.HP] = 100;
            var b = new GuildBuffInfo { Id = 2, Icon = 3, Name = "Battle Cry", LevelRequirement = 3, PointsRequirement = 2, TimeLimit = 30, ActivationCost = 50000 };
            b.Stats[Stat.MaxDC] = 4;
            var c = new GuildBuffInfo { Id = 3, Icon = 6, Name = "Gold Rush", LevelRequirement = 8, PointsRequirement = 3, TimeLimit = 0, ActivationCost = 0 };
            c.Stats[Stat.GoldDropRatePercent] = 10;
            return new List<GuildBuffInfo> { a, b, c };
        }

        private static List<ClientGTMap> GuTerritories(int page)
        {
            var list = new List<ClientGTMap>();
            if (page == 0)
            {
                list.Add(new ClientGTMap { index = 0, Name = "Bichon Keep", Owner = "Dragon Lords", Leader = "UiTester", Leader2 = "", price = 0, days = -4, begin = 0 });
                for (int i = 1; i < 7; i++)
                    list.Add(i % 3 == 0
                        ? new ClientGTMap { index = i, Name = "Hall " + i, Owner = "Shadow Clan", Leader = "Nyx", Leader2 = "Vex", price = i == 3 ? 8000000 : 0, days = -2, begin = 0 }
                        : new ClientGTMap { index = i, Name = "Hall " + i, Owner = "None", Leader = "None", Leader2 = "", price = 0, days = 0, begin = 0 });
            }
            else
            {
                list.Add(new ClientGTMap { index = 7, Name = "Mongchon Tower", Owner = "Lone Wolves", Leader = "Wolf", Leader2 = "", price = 0, days = -6, begin = 1 });
                list.Add(new ClientGTMap { index = 8, Name = "Serpent Hold", Owner = "None", Leader = "None", Leader2 = "", price = 0, days = 0, begin = 0 });
            }
            return list;
        }

        private static S.Rankings GuRankings(byte type, int from)
        {
            var p = new S.Rankings { RankType = type, Count = type == 0 ? 45 : 12, MyRank = type == 0 ? 5 : 0 };
            int n = Math.Min(20, p.Count - from);
            for (int i = 0; i < n; i++)
            {
                long id = 1000 + type * 100 + from + i;
                bool me = type == 0 && from + i == 4;
                MirClass cls = type == 0 ? (MirClass)(i % 5) : (MirClass)(type - 1);
                p.ListingDetails.Add(new RankCharacterInfo { PlayerId = me ? 1 : id, Name = me ? "UiTester" : "Hero" + (from + i + 1), Class = me ? MirClass.Warrior : cls, level = 60 - (from + i) });
                p.Listings.Add(me ? 1 : id);
            }
            return p;
        }

        private static S.RankingsDetail GuRankingDetails(byte type)
        {
            var p = new S.RankingsDetail { RankType = type };
            for (int i = 0; i < 20; i++)
            {
                long id = i == 4 ? 1 : 1000 + type * 100 + i;
                p.Rows.Add(new ClientRankingDetail { PlayerId = id, GuildName = i % 3 == 0 ? "Shadow Clan" : i % 3 == 1 ? "Dragon Lords" : string.Empty, Online = i % 2 == 0, ExpPerTenThousand = (ushort)(i * 450 % 10000) });
            }
            return p;
        }

        private static MirControl GuRow(ScrollView view, string name)
        {
            if (view == null) return null;
            foreach (MirControl c in view.Content.Controls)
                if (c is PaintBox b && b.Tag2 == name) return b;
            return null;
        }

        /// <summary>The <paramref name="col"/>-th option switch of rank row <paramref name="row"/> on the ranks page.</summary>
        private static MirControl GuSwitch(ScrollView view, int row, int col)
        {
            if (view == null) return null;
            int r = -1, c = 0;
            foreach (MirControl ctl in view.Content.Controls)
            {
                if (ctl is PaintBox) { r++; c = 0; continue; }
                if (ctl is ToggleSwitch t)
                {
                    if (r == row && c == col) return t;
                    c++;
                }
            }
            return null;
        }

        private static MirControl GuGtRow(int i)
        {
            ScrollView v = GuGt?.RowsView;
            if (v == null) return null;
            int n = 0;
            foreach (MirControl c in v.Content.Controls)
                if (c is PaintBox && n++ == i) return c;
            return null;
        }

        private static void GuTab(int i)
        {
            Tabs t = GuRk?.TabsControl;
            if (t == null) return;
            Rectangle r = t.TabBounds(i);
            ClickAt(new Point(t.DisplayLocation.X + r.X + r.Width / 2, t.DisplayLocation.Y + r.Y + r.Height / 2));
        }

        private static void GuDump()
        {
            GuildWindow g = GuWin;
            if (g != null)
            {
                var gs = GameScene.Models.Guild;
                _log.Add($"   guild page={g.PageShown} sel={g.Selected} editing={g.Editing} saving={g.NoticeSaving} rows={g.MemberRows?.Content.Controls.Count ?? -1} acts={g.CardActions.Count} notes=\"{g.Notes}\" notice=[{string.Join(" / ", gs.Notice)}] stale={gs.NoticeStale}/{gs.MembersStale} details={gs.MemberDetails.Count} buffs={gs.EnabledBuffs.Count}/{gs.BuffInfos.Count} wars={gs.Wars.Count} log={gs.Log.Count}");
            }
            TerritoriesWindow t = GuGt;
            if (t != null) _log.Add($"   territories sel={t.SelectedIndex} why=\"{t.Why}\" rows={t.RowsView?.Content.Controls.Count ?? -1}");
            RankingWindow r = GuRk;
            if (r != null) _log.Add($"   ranking board={r.Board} offset={r.Offset} picked={r.Picked} side={r.Side?.Controls.Count ?? -1} count={GameScene.Models.Rankings.Count}");
            HudExtras h = GuHx;
            if (h != null) _log.Add($"   extras timer={h.TimerCounter} compass={h.CompassShown} \"{h.CompassText}\" roll={(h.RollCard?.Visible == true ? (h.RollCard.Rolled ? "rolled" : h.RollCard.Rolling ? "rolling" : "set") : "none")} poison=\"{h.PoisonText}\" tint={h.TintShown}");
            if (Root != null && Root.Wm.IsOpen(NoticeWindow.WindowKey) && Root.Wm.Get(NoticeWindow.WindowKey) is NoticeWindow n)
                _log.Add($"   notice title=\"{n.Title}\" lines={n.LineCount} l1=\"{n.LineText(1)}\" l2=\"{n.LineText(2)}\"");
        }
    }
}

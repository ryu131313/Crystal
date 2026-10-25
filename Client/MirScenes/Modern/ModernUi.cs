using Client.MirScenes.Modern.Core;
using S = ServerPackets;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// The new UI (Desktop-Modern-UI-Plan). Phase 1a: an empty shell behind Settings.UiStyle = Modern (ini only, not selectable
    /// yet), so the seam can be exercised with no classic dialog constructed. Phases 1b-4 fill it window by window.
    /// Every IGameUi method not ported yet calls <see cref="NotPorted"/>: a no-op that logs once per method.
    /// </summary>
    public sealed partial class ModernUi : IGameUi
    {
        private readonly GameScene _scene;
        private static readonly HashSet<string> _notPortedLogged = new HashSet<string>();

        public ModernUi(GameScene scene)
        {
            _scene = scene;
        }

        public UiStyle Style => UiStyle.Modern;

        /// <summary>Logs "[ModernUi] not ported: name" the first time per method and process; never throws.</summary>
        public static void NotPorted(string name)
        {
            if (!_notPortedLogged.Add(name)) return;
            if (Settings.LogErrors) CMain.SaveError("[ModernUi] not ported: " + name);
        }

        /// <summary>The kit's root (windows, popups, …); null before Build and after Dispose.</summary>
        public ModernRoot Root { get; private set; }

        public void Build()
        {
            ModernTheme.ApplySettings();
            Models.QuestText.NpcNameFormat = n => Hud.ChatGfx.NpcName(n).Name; // quest text names NPCs as the new UI does
            Root = new ModernRoot(_scene);
            RegisterWindows();
            BuildHud();          // ModernUi.Hud.cs: the bottom HUD, party / hero frames, game menu
            BuildChatHud();      // ModernUi.Chat.cs: chat, minimap, quest tracker (+ the big map window)
            // over everything until the player continues; not in the offline UI test (its steps drive keys and take screenshots)
            if (!RenderTest.UiTestRunner.Enabled) Loading = new Hud.LoadingOverlay(MapData) { Parent = Root.Menu };
        }

        /// <summary>The loading screen (Hud\LoadingOverlay.cs) until the player continues; null after.</summary>
        internal Hud.LoadingOverlay Loading { get; private set; }

        /// <summary>The window catalog (window keys → factories; a window is built on its first open).</summary>
        private void RegisterWindows()
        {
            Root.Wm.Register(KitTestWindow.WindowKey, () => new KitTestWindow());
            RegisterChatWindows();  // ModernUi.Chat.cs
            RegisterCoreWindows();  // ModernUi.Windows.cs: inventory, character, storage, inspect, skills, options, keybinds, help
            RegisterNpcWindows();      // ModernUi.Npc.cs (phase 3)
            RegisterUpgradeWindows();  // ModernUi.Upgrade.cs (phase 3)
            RegisterEconomyWindows();  // ModernUi.Economy.cs (phase 3)
            RegisterGuildWindows();     // ModernUi.Guild.cs (phase 4): guild, territories, ranking (+ the HUD timer / compass / roll / notice)
            RegisterSocialWindows();    // ModernUi.Social.cs (phase 4): group, trade, friends, mentor, relationship, quests
            RegisterCompanionWindows(); // ModernUi.Companion.cs (phase 4): hero, creatures, mount, fishing
        }

        // Area hooks, one partial file per area (phase 2): each builds its part and answers its own keybinds.
        partial void BuildHud();
        partial void BuildChatHud();
        partial void RegisterChatWindows();
        partial void RegisterCoreWindows();
        partial void RegisterNpcWindows();
        partial void RegisterUpgradeWindows();
        partial void RegisterEconomyWindows();
        partial void RegisterGuildWindows();
        partial void RegisterSocialWindows();
        partial void RegisterCompanionWindows();
        /// <summary>Phase 4 areas: called from <see cref="Dispose"/> before ModernEvents.Clear (drop model subscriptions, sessions).</summary>
        partial void DisposeGuild();
        partial void DisposeSocial();
        partial void DisposeCompanion();
        /// <summary>Keybinds the area handles (true = consumed). Called by <see cref="KeyBind"/> before the shared cases.</summary>
        private partial bool HudKeyBind(KeybindOptions function);
        private partial bool ChatKeyBind(KeybindOptions function);
        private partial bool WindowKeyBind(KeybindOptions function);
        private partial bool NpcKeyBind(KeybindOptions function);
        private partial bool UpgradeKeyBind(KeybindOptions function);
        private partial bool EconomyKeyBind(KeybindOptions function);
        private partial bool GuildKeyBind(KeybindOptions function);
        private partial bool SocialKeyBind(KeybindOptions function);
        private partial bool CompanionKeyBind(KeybindOptions function);

        /// <summary>Spaces the modern UI's bulk sends (item moves, sorts, sell junk, …) under the server's MaxPacket.</summary>
        public readonly SendPacer Pacer = new SendPacer();

        public void Process()
        {
            if (Root == null || Root.IsDisposed) return;
            Root.KeepOnTop();
            if (Loading != null)
            {
                if (Loading.IsDisposed) Loading = null;
                else Loading.Process();
            }
            Root.Process();
            Root.Wm.Process();
            Pacer.Process();
        }

        /// <summary>From GameScene.DrawControl after the scene texture: the windows, each from its own render target.</summary>
        public void DrawOverlay() => Root?.DrawOverlay();

        public void KeyDown(KeyEventArgs e)
        {
        }

        /// <summary>Set when the UI is disposed (log-out / quit): request toasts closed by Popups.CloseAll then send no decline.</summary>
        private bool _ending;

        public void Dispose()
        {
            _ending = true;
            Pacer.Dispose();
            Storage?.Dispose();
            DisposeUpgrade();   // ModernUi.Upgrade.cs: refine / awaken / rental sessions
            DisposeGuild();
            DisposeSocial();
            DisposeCompanion();
            MapData?.Dispose(); // drops its Models.MapDetails subscription (ModernEvents.Clear covers the rest)
            ModernEvents.Clear();
            Root = null; // a child of the scene: disposed with it
        }

        public void BuffAdded(bool hero, ClientBuff buff) { } // Models.Buffs / HeroBuffs.Changed already fired
        public void BuffRemoved(bool hero, int index) { } // the model emits after the removal

        // ---- phase 4 prompts that belong to no window (IGameUi.Social.cs; classic: MirMessageBox)
        public void ConfirmQuit()
        {
            if (Root == null || Root.IsDisposed) return;
            Root.Popups.Ask(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ExitTip), yes => { if (yes) Program.Form.Close(); });
        }

        public void ConfirmLogOut()
        {
            if (Root == null || Root.IsDisposed) return;
            Root.Popups.Ask(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LogOutTip), yes => { if (yes) GameScene.Scene?.LogOut(false); });
        }

        public void MapChanged(int mapIndex) => ModernEvents.MapChanged.Emit(mapIndex);
        public void PatchStatusChanged() => DownloadStrip?.Refresh();

        public void PatchUpdateReady()
        {
            ModernRoot.Instance?.Popups.Confirm(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchUpdateReadyPrompt), PopupButtons.YesNo, PopupKind.Question, (a, _) =>
            {
                if (a == PopupAnswer.Yes) Client.Patching.PatchUpdate.RestartNow();
            }, false, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchRestartToUpdate));
        }

        public void ReincarnationRequest()
        {
            if (Root == null || Root.IsDisposed) return;
            Root.Popups.Ask(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.WouldYouLikeToBeRevived), yes =>
            {
                if (yes) MirNetwork.Network.Enqueue(new ClientPackets.AcceptReincarnation());
            });
        }

        // ---- seam step 4 (sweep) BEGIN: generated, keep in this order ----
        /// <summary>Per frame: a silent no-op until ported (NotPorted is not called per spin).</summary>
        public void DrawOutputLines()
        {
        }
        public void HasHeroChanged(bool value) => ModernEvents.HasHero.Emit(value);
        /// <summary>Esc closes the front window, Shift+Esc all; the front window's own keys first (wm.ts handleKey).</summary>
        public bool CapturesKey(KeyEventArgs e)
        {
            if (Loading != null && !Loading.IsDisposed && Root != null && !Root.Popups.IsOpen && Loading.HandleKey(e))
            {
                e.SuppressKeyPress = true;
                return true;
            }
            if (Root == null || Root.IsDisposed || !Root.Wm.HandleKey(e)) return false;
            e.SuppressKeyPress = true; // consumed: its KeyPress must not reach the chat box (Enter / Space open the input)
            return true;
        }
        /// <summary>The hero belt keys follow the HUD cells' rules: a summoned, living hero and potions only (Hud\Belt.cs).</summary>
        private static void UseHeroBelt(int slot)
        {
            if (GameScene.Hero == null || Hud.Belt.HeroOff()) return;
            UserItem item = ItemMoves.GetItem(MirGridType.HeroInventory, slot);
            if (item?.Info == null || item.Info.Type != ItemType.Potion) return;
            ItemMoves.Use(MirGridType.HeroInventory, slot);
        }

        /// <summary>
        /// The keybinds GameScene leaves to the UI (window toggles, belt 7-8, the hero belt, Close all): the areas first
        /// (HUD, chat / map, windows), then the shared cases. Gameplay keys (skills, belt 1-6, modes, pick up, …) never get
        /// here: GameScene handles them for both UIs.
        /// </summary>
        public void KeyBind(KeybindOptions function)
        {
            if (Root == null || Root.IsDisposed) return;
            if (HudKeyBind(function) || ChatKeyBind(function) || WindowKeyBind(function) || NpcKeyBind(function) || UpgradeKeyBind(function) ||
                EconomyKeyBind(function) || GuildKeyBind(function) || SocialKeyBind(function) || CompanionKeyBind(function)) return;

            switch (function)
            {
                case KeybindOptions.Belt7:
                case KeybindOptions.Belt7Alt:
                    ItemMoves.Use(MirGridType.Inventory, 6); // the modern belt: player cells 7-8 (classic: hero belt)
                    return;
                case KeybindOptions.Belt8:
                case KeybindOptions.Belt8Alt:
                    ItemMoves.Use(MirGridType.Inventory, 7);
                    return;
                case KeybindOptions.HeroBelt1:
                case KeybindOptions.HeroBelt1Alt:
                    UseHeroBelt(0);
                    return;
                case KeybindOptions.HeroBelt2:
                case KeybindOptions.HeroBelt2Alt:
                    UseHeroBelt(1);
                    return;
                case KeybindOptions.Closeall:
                    Root.Wm.CloseAll();
                    return;
            }
            NotPorted("KeyBind." + function);
        }
        public void SkillModeChanged(bool ctrl) => ModernEvents.SkillMode.Emit(ctrl);
        public void ObjectPositionsChanged() => ModernEvents.ObjectPositions.Emit(); // per object move: listeners repaint at most once per frame
        /// <summary>Per frame: a silent no-op until ported (NotPorted is not called per spin).</summary>
        public void ProcessBeforeMap()
        {
        }
        /// <summary>Per frame: a silent no-op until ported (NotPorted is not called per spin).</summary>
        public void DialogProcess()
        {
        }
        /// <summary>Per frame: a silent no-op until ported (NotPorted is not called per spin).</summary>
        public void ProcessOutput()
        {
        }
        public void GameDataRefreshed(bool magicsChanged, bool shopChanged, bool hoverChanged) => ModernEvents.GameData.Emit((magicsChanged, shopChanged, hoverChanged));
        public void MapInfoInvalidated(int mapIndex) => ModernEvents.MapInfo.Emit(mapIndex);
        public void WorldMapSetup(WorldMapSetup setup) => ModernEvents.WorldMap.Emit(setup);
        public void MapInfoReceived(S.NewMapInfo info) => ModernEvents.NewMapInfo.Emit(info);
        public void SearchMapResult(S.SearchMapResult info) => ModernEvents.SearchMap.Emit(info);
        public void UserLoaded() => ModernEvents.UserLoaded.Emit();
        public void HeroBehaviourChanged(HeroBehaviour behaviour) => ModernEvents.HeroBehaviour.Emit(behaviour);
        public void UserInformationReceived() => ModernEvents.UserInformation.Emit();
        public void MountChanged() => ModernEvents.Mount.Emit();
        public void QuestAccepted(int quest, bool track) => ModernEvents.QuestAdded.Emit((quest, track));
        public void QuestsChanged() => ModernEvents.Quests.Emit();
        public void LightsChanged() => ModernEvents.Lights.Emit();
        public void UserItemDuraChanged(UserItem item) => ModernEvents.Durability.Emit(item);
        public void HeroInfoChanged() => ModernEvents.HeroInfo.Emit();
        public void SkillBarsChanged() => ModernEvents.SkillBars.Emit();
        /// <summary>A group invite as a request toast (classic: a Yes / No box; Yes also opened the group window).</summary>
        public void GroupInvite(S.GroupInvite p)
        {
            if (Root == null) return;
            string text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DoYouWantGroupWithPlayer, p.Name);
            Root.Popups.Request(p.Name, text, 30,
                () => MirNetwork.Network.Enqueue(new ClientPackets.GroupInvite { AcceptInvite = true }),
                () => MirNetwork.Network.Enqueue(new ClientPackets.GroupInvite { AcceptInvite = false }));
        }
        public void ItemInfoChanged(UserItem item) => ModernEvents.ItemInfo.Emit(item);
        public void HeroAutoPotChanged() => ModernEvents.HeroAutoPot.Emit();
        public void HeroInformationReceived() => ModernEvents.HeroInformation.Emit();
        public void HeroSpawnStateChanged(HeroSpawnState state) => ModernEvents.HeroSpawnState.Emit(state);
        public void InventoryResized() => ModernEvents.InventoryResized.Emit();
        public void StorageResized() => ModernEvents.StorageResized.Emit();
        // ---- seam step 4 (sweep) END ----
    }
}

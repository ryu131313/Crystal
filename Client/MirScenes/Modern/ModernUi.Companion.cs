using Client.MirNetwork;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Windows;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// The companion windows' state that must live while their windows are closed or not built yet (the web's registry "watch"
    /// hooks): the hero's behaviour (ModernEvents.HeroBehaviour since the game started), the standing S.HeroCreateRequest, the
    /// creature rename grant, and the fishing session (the last S.FishingUpdate, the status switches, the cast tracking).
    /// Reset when the modern UI is built and disposed. UI thread only.
    /// </summary>
    internal static class CompanionState
    {
        public static HeroBehaviour Behaviour = HeroBehaviour.Attack;
        /// <summary>S.HeroCreateRequest.CanCreateClass while it stands (null = none); cleared by an S.NewHero that ends it.</summary>
        public static bool[] CreateRequest;
        /// <summary>The request whose New hero panel was closed with Cancel (it stays hidden until a new request).</summary>
        public static bool[] DismissedRequest;
        /// <summary>GameScene.IntelligentCreatureEnableRename: the Rename button shows until it is used.</summary>
        public static bool CreatureRenameEnabled;
        public static FishingSession Fishing = new FishingSession();

        public static void Reset()
        {
            Behaviour = HeroBehaviour.Attack;
            CreateRequest = DismissedRequest = null;
            CreatureRenameEnabled = false;
            Fishing = new FishingSession();
        }
    }

    /// <summary>
    /// Phase 4 area "companion" (worker C): hero (create / manage / change; GameScene.HeroInfoList, HeroStorage, CurrentHeroInfo,
    /// MaximumHeroCount), intelligent creatures (User.IntelligentCreatures, CreatureSummoned, …), mount, fishing (+ the fishing
    /// scene). Window keys "hero", "creatures", "mount", "fishing". Windows live in Client\MirScenes\Modern\Windows\ (Hero*,
    /// Creatures*, Mount*, Fishing*).
    /// </summary>
    public sealed partial class ModernUi
    {
        private Action<HeroBehaviour> _compBehaviour;
        private Action<Models.ChatLine> _compChat;

        partial void RegisterCompanionWindows()
        {
            CompanionState.Reset();
            Root.Wm.Register(HeroWindow.WindowKey, () => new HeroWindow());
            Root.Wm.Register(CreaturesWindow.WindowKey, () => new CreaturesWindow());
            Root.Wm.Register(MountWindow.WindowKey, () => new MountWindow());
            Root.Wm.Register(FishingWindow.WindowKey, () => new FishingWindow());
            _compBehaviour = b => CompanionState.Behaviour = b;
            ModernEvents.HeroBehaviour.On(_compBehaviour);
            _compChat = line => { if (line != null && line.Type == ChatType.System) CompanionState.Fishing.Chat(line.Text); };
            GameScene.Models?.Chat.Added.On(_compChat);
        }

        partial void DisposeCompanion()
        {
            if (_compBehaviour != null) ModernEvents.HeroBehaviour.Off(_compBehaviour);
            if (_compChat != null) GameScene.Models?.Chat.Added.Off(_compChat);
            _compBehaviour = null;
            _compChat = null;
            CompanionState.Reset();
        }

        private T OpenComp<T>(string key) where T : ModernWindow => Root != null && !Root.IsDisposed && Root.Wm.IsOpen(key) ? Root.Wm.Get(key) as T : null;

        /// <summary>KeybindOptions.Creature / MountWindow / Fishing toggle their windows; HeroInventory / HeroEquipment / HeroSkills open
        /// the hero window on its Bag / Stats / Skills tab (a second press on the shown tab closes it); Mount rides (MountDialog.CanRide,
        /// the classic keybind path).</summary>
        private partial bool CompanionKeyBind(KeybindOptions function)
        {
            switch (function)
            {
                case KeybindOptions.Creature:
                    Root.Wm.Toggle(CreaturesWindow.WindowKey);
                    return true;
                case KeybindOptions.MountWindow:
                    Root.Wm.Toggle(MountWindow.WindowKey);
                    return true;
                case KeybindOptions.Fishing:
                    Root.Wm.Toggle(FishingWindow.WindowKey);
                    return true;
                case KeybindOptions.Mount:
                    if (MountWindow.CanRide()) MountWindow.Ride();
                    return true;
                case KeybindOptions.HeroInventory:
                case KeybindOptions.HeroEquipment:
                case KeybindOptions.HeroSkills:
                    int tab = function == KeybindOptions.HeroInventory ? 1 : function == KeybindOptions.HeroSkills ? 2 : 3;
                    (Root.Wm.Get(HeroWindow.WindowKey) as HeroWindow)?.ToggleTab(tab);
                    return true;
            }
            return false;
        }

        /// <summary>S.HeroCreateRequest: the hero window opens with the New hero panel (NewHeroDialog.Show with the allowed classes).</summary>
        public void HeroCreateRequest(S.HeroCreateRequest p)
        {
            if (Root == null || Root.IsDisposed || p?.CanCreateClass == null) return;
            CompanionState.CreateRequest = (bool[])p.CanCreateClass.Clone();
            CompanionState.DismissedRequest = null;
            Root.Wm.Open(HeroWindow.WindowKey);
            OpenComp<HeroWindow>(HeroWindow.WindowKey)?.Poke();
        }

        /// <summary>S.ManageHeroes (HeroInfoList / HeroStorage / MaximumHeroCount / CurrentHeroInfo already set): HeroManageDialog.Show.</summary>
        public void ManageHeroes(ClientHeroInformation currentHero)
        {
            if (Root == null || Root.IsDisposed) return;
            Root.Wm.Open(HeroWindow.WindowKey);
            OpenComp<HeroWindow>(HeroWindow.WindowKey)?.Poke();
        }

        /// <summary>S.ChangeHero: GameScene.CurrentHeroInfo and HeroStorage are already swapped.</summary>
        public void ChangeHero(S.ChangeHero p) => OpenComp<HeroWindow>(HeroWindow.WindowKey)?.ChangeAnswered();

        /// <summary>S.NewHero: the classic result boxes (GameScene.ClassicNewHeroResult); 0 / 4 / 6 / 10 end the request.</summary>
        public void NewHeroResult(S.NewHero p)
        {
            if (Root == null || Root.IsDisposed || p == null) return;
            ClientTextKeys? text = null;
            bool end = false;
            switch (p.Result)
            {
                case 0: text = ClientTextKeys.CreatingNewHeroesDisabled; end = true; break;
                case 1: text = ClientTextKeys.YourHeroNameNotAcceptable; break;
                case 2: text = ClientTextKeys.SelectedGenderNotExist; break;
                case 3: text = ClientTextKeys.SelectedClassNotExist; break;
                case 4: text = ClientTextKeys.CannotMakeMoreHeroes; end = true; break;
                case 5: text = ClientTextKeys.CharacterNameAlreadyExists; break;
                case 6: text = ClientTextKeys.NoBagSpace; end = true; break;
                case 10: text = ClientTextKeys.HeroCreatedSuccessfully; end = true; break;
            }
            if (end) CompanionState.CreateRequest = CompanionState.DismissedRequest = null;
            OpenComp<HeroWindow>(HeroWindow.WindowKey)?.NewHeroAnswered();
            if (text != null)
                Root.Popups.Confirm(GameLanguage.ClientTextMap.GetLocalization(text.Value), PopupButtons.Ok, p.Result == 10 ? PopupKind.Info : PopupKind.Warning, null, false,
                    GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernCompNewHero));
        }

        /// <summary>S.NewIntelligentCreature (added to User.IntelligentCreatures): name it (a popup input; Cancel keeps the given name).
        /// The classic box sends C.UpdateIntelligentCreature with the typed name; this one checks the name rule first.</summary>
        public void NewIntelligentCreature()
        {
            if (Root == null || Root.IsDisposed) return;
            List<ClientIntelligentCreature> list = GameScene.User?.IntelligentCreatures;
            if (list == null || list.Count == 0) return;
            ClientIntelligentCreature c = list[list.Count - 1];
            IntelligentCreatureType type = c.PetType;
            OpenComp<CreaturesWindow>(CreaturesWindow.WindowKey)?.ListChanged(false);
            Root.Popups.Input(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernCompNewCreature), c.CustomName, Globals.MaxCharacterNameLength, v =>
            {
                if (v == null || Root == null || Root.IsDisposed) return;
                if (!CreaturesWindow.ValidName(v))
                {
                    Root.Popups.Confirm(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.CreatureNameLengthRange, Globals.MinCharacterNameLength, Globals.MaxCharacterNameLength),
                        PopupButtons.Ok, PopupKind.Warning, null);
                    return;
                }
                List<ClientIntelligentCreature> now = GameScene.User?.IntelligentCreatures;
                if (now == null) return;
                for (int i = 0; i < now.Count; i++)
                {
                    if (now[i].PetType != type) continue;
                    now[i].CustomName = v; // no answer for a plain update
                    Network.Enqueue(new C.UpdateIntelligentCreature { Creature = now[i] });
                    OpenComp<CreaturesWindow>(CreaturesWindow.WindowKey)?.ListChanged(false);
                    return;
                }
            }, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PleaseGiveYourCreatureName));
        }

        /// <summary>S.UpdateIntelligentCreatureList applied: any list answers a pending Summon / Dismiss / Release.</summary>
        public void IntelligentCreaturesChanged(bool listReplaced) => OpenComp<CreaturesWindow>(CreaturesWindow.WindowKey)?.ListChanged(true);

        public void IntelligentCreatureEnableRename()
        {
            CompanionState.CreatureRenameEnabled = true;
            OpenComp<CreaturesWindow>(CreaturesWindow.WindowKey)?.ListChanged(false);
        }

        /// <summary>S.FishingUpdate for the user: the session tracks the cast; the window opens by itself when a cast starts or a fish
        /// bites (FishingStatusDialog shows while the user fishes), once per transition.</summary>
        public void FishingUpdated(S.FishingUpdate p)
        {
            if (p == null) return;
            FishingSession f = CompanionState.Fishing;
            bool start = p.Fishing && !f.Fishing, bite = p.Fishing && p.FoundFish && !f.FoundFish;
            f.Update(new ClientPackets_FishingView { Fishing = p.Fishing, FoundFish = p.FoundFish, Chance = p.ChancePercent, Progress = p.ProgressPercent });
            if (Root == null || Root.IsDisposed) return;
            if ((start || bite) && !Root.Wm.IsOpen(FishingWindow.WindowKey) && GameScene.User != null && GameScene.User.HasFishingRod)
                Root.Wm.Open(FishingWindow.WindowKey);
        }

        public void CloseMountWindow() => Root?.Wm.Close(MountWindow.WindowKey);

        public void CloseFishingWindow() => Root?.Wm.Close(FishingWindow.WindowKey);

        /// <summary>PlayerObject frame 1 of FishingCast: the classic fish button hides. The modern window follows S.FishingUpdate.</summary>
        public void FishingCast() { }

        /// <summary>PlayerObject frame 1 of FishingWait: the classic fish button shows on a bite. The modern window follows S.FishingUpdate.</summary>
        public void FishingWait(bool foundFish) { }
    }
}

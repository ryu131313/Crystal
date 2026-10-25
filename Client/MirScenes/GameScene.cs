using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirSounds;
using Client.MirGraphics.Rendering;
using SlimDX;
using Font = System.Drawing.Font;
using S = ServerPackets;
using C = ClientPackets;
using Effect = Client.MirObjects.Effect;
using Client.MirScenes.Dialogs;
using Client.Utils;
using Client.MirGraphics.Particles;
using Client.MirScenes.Classic;
using Client.MirScenes.Models;
using Client.MirScenes.Modern;
using Client.Patching;

namespace Client.MirScenes
{
    public sealed partial class GameScene : MirScene
    {
        public static GameScene Scene;
        /// <summary>The model layer of this game (seam step 1); built before the UI, nulled in Dispose.</summary>
        public static GameModels Models;
        /// <summary>The UI of this game: ClassicUi or ModernUi (seam step 2); everything classic goes through it.</summary>
        public static IGameUi Ui;
        /// <summary>This scene's own Models / Ui, so Dispose clears the statics only while they are still this scene's.</summary>
        private GameModels _models;
        private IGameUi _ui;
        public static bool Observing;
        public static bool AllowObserve;

        public static UserObject User
        {
            get { return MapObject.User; }
            set { MapObject.User = value; }
        }

        public static UserHeroObject Hero
        {
            get { return MapObject.Hero; }
            set { MapObject.Hero = value; }
        }
        public static HeroObject HeroObject
        {
            get { return MapObject.HeroObject; }
            set { MapObject.HeroObject = value; }
        }

        public static long MoveTime, AttackTime, NextRunTime, LogTime, LastRunTime, ChangePModeTime, ChangeAModeTime, HeroSpellTime, IntelligentCreaturePickupTime;
        public static bool CanMove, CanRun;

        private bool hasHero;
        public bool HasHero
        {
            get { return hasHero; }
            set
            {
                if (hasHero == value) return;

                hasHero = value;
                Ui.HasHeroChanged(value);
            }
        }
        public HeroSpawnState HeroSpawnState;
        public List<ParticleEngine> ParticleEngines = new List<ParticleEngine>();
        public MapControl MapControl;


        public static List<ItemInfo> ItemInfoList = new List<ItemInfo>();
        public static List<UserId> UserIdList = new List<UserId>();
        public static List<UserItem> ChatItemList = new List<UserItem>();
        public static List<GameShopItem> GameShopInfoList = new List<GameShopItem>();
        public static List<ClientRecipeInfo> RecipeInfoList = new List<ClientRecipeInfo>();
        public static List<ClientHeroInformation> HeroInfoList = new List<ClientHeroInformation>();
        public static ClientHeroInformation[] HeroStorage = new ClientHeroInformation[8];
        /// <summary>The hero S.ManageHeroes named current (was HeroManageDialog.CurrentAvatar.Info); S.ChangeHero swaps it with a storage slot.</summary>
        public static ClientHeroInformation CurrentHeroInfo;
        public static Dictionary<long, RankCharacterInfo> RankingList = new Dictionary<long, RankCharacterInfo>();
        public static int TeleportToNPCCost;
        public static int MaximumHeroCount;

        public static List<ClientMonsterInfo> MonsterInfoList = new List<ClientMonsterInfo>();
        public static List<ClientNPCInfo> NPCInfoList = new List<ClientNPCInfo>();
        private static readonly HashSet<int> RequestedItemInfo = new HashSet<int>();
        private static readonly HashSet<int> RequestedMonsterInfo = new HashSet<int>();
        private static readonly HashSet<int> RequestedNPCInfo = new HashSet<int>();

        public static void RequestItemInfo(int index)
        {
            if (index <= 0 || HasItemInfo(index) || !RequestedItemInfo.Add(index)) return;
            if (!Network.Connected) return;

            Network.Enqueue(new C.RequestItemInfo { ItemIndex = index });
        }

        public static void RequestMonsterInfo(int index)
        {
            if (index <= 0 || HasMonsterInfo(index) || !RequestedMonsterInfo.Add(index)) return;
            if (!Network.Connected) return;

            Network.Enqueue(new C.RequestMonsterInfo { MonsterIndex = index });
        }

        public static void RequestNPCInfo(int index)
        {
            if (index <= 0 || HasNPCInfo(index) || !RequestedNPCInfo.Add(index)) return;
            if (!Network.Connected) return;

            Network.Enqueue(new C.RequestNPCInfo { NPCIndex = index });
        }

        public static void OnItemInfoReceived(int index)
        {
            RequestedItemInfo.Remove(index);
        }

        public static void OnMonsterInfoReceived(int index)
        {
            RequestedMonsterInfo.Remove(index);
        }

        public static void OnNPCInfoReceived(int index)
        {
            RequestedNPCInfo.Remove(index);
        }

        private static bool HasItemInfo(int index)
        {
            for (int i = 0; i < ItemInfoList.Count; i++)
            {
                if (ItemInfoList[i].Index == index)
                    return true;
            }

            return false;
        }

        private static bool HasMonsterInfo(int index)
        {
            for (int i = 0; i < MonsterInfoList.Count; i++)
            {
                if (MonsterInfoList[i].Index == index)
                    return true;
            }

            return false;
        }

        private static bool HasNPCInfo(int index)
        {
            for (int i = 0; i < NPCInfoList.Count; i++)
            {
                if (NPCInfoList[i].Index == index)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Adds the item, or copies it onto the ItemInfo already held with the same Index (in place, so every UserItem.Info that points
        /// at it sees the new values). Returns the held object. Used by S.NewItemInfo (F7) and the LiveDb push.
        /// </summary>
        public static ItemInfo UpsertItemInfo(ItemInfo info)
        {
            if (info == null) return null;
            for (int i = 0; i < ItemInfoList.Count; i++)
            {
                if (ItemInfoList[i].Index != info.Index) continue;
                ItemInfoList[i].CopyFrom(info);
                return ItemInfoList[i];
            }

            ItemInfoList.Add(info);
            return info;
        }

        /// <summary>Replaces the monster info with the same Index (or adds it).</summary>
        public static void UpsertMonsterInfo(ClientMonsterInfo info)
        {
            if (info == null) return;
            OnMonsterInfoReceived(info.Index);
            for (int i = 0; i < MonsterInfoList.Count; i++)
            {
                if (MonsterInfoList[i].Index != info.Index) continue;
                MonsterInfoList[i] = info;
                return;
            }

            MonsterInfoList.Add(info);
        }

        /// <summary>Replaces the NPC info with the same Index (or adds it).</summary>
        public static void UpsertNPCInfo(ClientNPCInfo info)
        {
            if (info == null) return;
            OnNPCInfoReceived(info.Index);
            for (int i = 0; i < NPCInfoList.Count; i++)
            {
                if (NPCInfoList[i].Index != info.Index) continue;
                NPCInfoList[i] = info;
                return;
            }

            NPCInfoList.Add(info);
        }

        public static UserItem[] Storage = new UserItem[80];
        public static UserItem[] GuildStorage = new UserItem[112];
        public static UserItem[] Refine = new UserItem[16];
        public static UserItem HoverItem, SelectedItem;

        public static bool PickedUpGold;
        public static long UseItemTime, PickUpTime, DropViewTime, TargetDeadTime;
        public static uint Gold, Credit;
        public static long InspectTime;
        public bool ShowReviveMessage;


        public bool NewMail;
        public int NewMailCounter = 0;


        public AttackMode AMode;
        public PetMode PMode;
        public LightSetting Lights;

        public static long NPCTime;
        public static uint NPCID;
        public static float NPCRate;
        public static PanelType NPCPanelType;
        public static uint DefaultNPCID;
        /// <summary>The NPC the player last clicked (C.CallNPC "[@Main]"). After a quest accept / turn-in the server's default-NPC call
        /// (OnAcceptQuest / OnFinishQuest) sends S.NPCUpdate, which moves NPCID to the default NPC; the quest views keep this one.</summary>
        public static uint TalkedNPCID;
        /// <summary>The NPC whose quests the NPC dialog shows: the clicked NPC while NPCID is it, the default NPC or none and it is still on
        /// screen, else NPCID.</summary>
        public static uint QuestNPCID =>
            TalkedNPCID != 0 && (NPCID == 0 || NPCID == TalkedNPCID || NPCID == DefaultNPCID) && MapControl.Objects != null && MapControl.Objects.ContainsKey(TalkedNPCID)
                ? TalkedNPCID : NPCID;
        public static bool HideAddedStoreStats;

        public long ToggleTime;
        public static long SpellTime;

        public List<OutPutMessage> OutputMessages = new List<OutPutMessage>();

        public long OutputDelay;

        public GameScene()
        {
            MapControl.AutoRun = false;
            MapControl.AutoHit = false;

            Scene = this;
            RequestedItemInfo.Clear();
            RequestedMonsterInfo.Clear();
            RequestedNPCInfo.Clear();
            BackColour = Color.Transparent;
            MoveTime = CMain.Time;

            KeyDown += GameScene_KeyDown;

            ItemMoves.Reset();
            Models = _models = new GameModels();
            Ui = _ui = UiStyles.IsModern ? new ModernUi(this) : new ClassicUi(this);
            Ui.Build();
        }

        private void UpdateMouseCursor()
        {
            if (!Settings.UseMouseCursors) return;

            if (GameScene.HoverItem != null)
            {
                if (ItemMoves.Selected != null && ItemMoves.Selected.Item != null && ItemMoves.Selected.Item.Info.Type == ItemType.Gem && CMain.Ctrl)
                {
                    CMain.SetMouseCursor(MouseCursor.Upgrade);
                }
                else
                {
                    CMain.SetMouseCursor(MouseCursor.Default);
                }
            }
            else if (MapObject.MouseObject != null)
            {
                switch (MapObject.MouseObject.Race)
                {
                    case ObjectType.Monster:
                        CMain.SetMouseCursor(MouseCursor.Attack);
                        break;
                    case ObjectType.Merchant:
                        CMain.SetMouseCursor(MouseCursor.NPCTalk);
                        break;
                    case ObjectType.Player:
                        if (CMain.Shift)
                        {
                            CMain.SetMouseCursor(MouseCursor.AttackRed);
                        }
                        else
                        {
                            CMain.SetMouseCursor(MouseCursor.Default);
                        }
                        break;
                    default:
                        CMain.SetMouseCursor(MouseCursor.Default);
                        break;
                }
            }
            else
            {
                CMain.SetMouseCursor(MouseCursor.Default);
            }

        }

        public void OutputMessage(string message, OutputMessageType type = OutputMessageType.Normal)
        {
            OutputMessages.Add(new OutPutMessage { Message = message, ExpireTime = CMain.Time + 5000, Type = type });
            if (OutputMessages.Count > 10)
                OutputMessages.RemoveAt(0);
        }

        private void ProcessOuput()
        {
            for (int i = 0; i < OutputMessages.Count; i++)
            {
                if (CMain.Time >= OutputMessages[i].ExpireTime)
                    OutputMessages.RemoveAt(i);
            }

            Ui.ProcessOutput();
        }
        private void GameScene_KeyDown(object sender, KeyEventArgs e)
        {
            if (Ui.CapturesKey(e))
                return;

            foreach (KeyBind KeyCheck in CMain.InputKeys.Keylist)
            {
                if (KeyCheck.Key == Keys.None)
                    continue;
                if (KeyCheck.Key != e.KeyCode)
                    continue;
                if (KeyCheck.Key == Keys.Insert && e.KeyCode == Keys.Insert && e.Modifiers == Keys.Control)
                {
                    //When Ctrl is held down, KeyData=Insert | Control, the system may mistakenly recognize it as Keys.Insert, requiring special judgment to be added.
                    continue;
                }
                if ((KeyCheck.RequireAlt != 2) && (KeyCheck.RequireAlt != (CMain.Alt ? 1 : 0)))
                    continue;
                if ((KeyCheck.RequireShift != 2) && (KeyCheck.RequireShift != (CMain.Shift ? 1 : 0)))
                    continue;
                if ((KeyCheck.RequireCtrl != 2) && (KeyCheck.RequireCtrl != (CMain.Ctrl ? 1 : 0)))
                    continue;
                if ((KeyCheck.RequireTilde != 2) && (KeyCheck.RequireTilde != (CMain.Tilde ? 1 : 0)))
                    continue;
                //now run the real code
                switch (KeyCheck.function)
                {
                    case KeybindOptions.Bar1Skill1: UseSpell(1); break;
                    case KeybindOptions.Bar1Skill2: UseSpell(2); break;
                    case KeybindOptions.Bar1Skill3: UseSpell(3); break;
                    case KeybindOptions.Bar1Skill4: UseSpell(4); break;
                    case KeybindOptions.Bar1Skill5: UseSpell(5); break;
                    case KeybindOptions.Bar1Skill6: UseSpell(6); break;
                    case KeybindOptions.Bar1Skill7: UseSpell(7); break;
                    case KeybindOptions.Bar1Skill8: UseSpell(8); break;
                    case KeybindOptions.Bar2Skill1: UseSpell(9); break;
                    case KeybindOptions.Bar2Skill2: UseSpell(10); break;
                    case KeybindOptions.Bar2Skill3: UseSpell(11); break;
                    case KeybindOptions.Bar2Skill4: UseSpell(12); break;
                    case KeybindOptions.Bar2Skill5: UseSpell(13); break;
                    case KeybindOptions.Bar2Skill6: UseSpell(14); break;
                    case KeybindOptions.Bar2Skill7: UseSpell(15); break;
                    case KeybindOptions.Bar2Skill8: UseSpell(16); break;
                    case KeybindOptions.HeroSkill1: UseSpell(17); break;
                    case KeybindOptions.HeroSkill2: UseSpell(18); break;
                    case KeybindOptions.HeroSkill3: UseSpell(19); break;
                    case KeybindOptions.HeroSkill4: UseSpell(20); break;
                    case KeybindOptions.HeroSkill5: UseSpell(21); break;
                    case KeybindOptions.HeroSkill6: UseSpell(22); break;
                    case KeybindOptions.HeroSkill7: UseSpell(23); break;
                    case KeybindOptions.HeroSkill8: UseSpell(24); break;
                    case KeybindOptions.Exit:
                        QuitGame();
                        return;

                    case KeybindOptions.Pickup:
                        if (CMain.Time > PickUpTime)
                        {
                            PickUpTime = CMain.Time + 200;
                            Network.Enqueue(new C.PickUp());
                        }
                        break;
                    case KeybindOptions.Belt1:
                    case KeybindOptions.Belt1Alt:
                        ItemMoves.Use(MirGridType.Inventory, 0);
                        break;
                    case KeybindOptions.Belt2:
                    case KeybindOptions.Belt2Alt:
                        ItemMoves.Use(MirGridType.Inventory, 1);
                        break;
                    case KeybindOptions.Belt3:
                    case KeybindOptions.Belt3Alt:
                        ItemMoves.Use(MirGridType.Inventory, 2);
                        break;
                    case KeybindOptions.Belt4:
                    case KeybindOptions.Belt4Alt:
                        ItemMoves.Use(MirGridType.Inventory, 3);
                        break;
                    case KeybindOptions.Belt5:
                    case KeybindOptions.Belt5Alt:
                        ItemMoves.Use(MirGridType.Inventory, 4);
                        break;
                    case KeybindOptions.Belt6:
                    case KeybindOptions.Belt6Alt:
                        ItemMoves.Use(MirGridType.Inventory, 5);
                        break;
                    case KeybindOptions.Logout:
                        LogOut();
                        break;
                    case KeybindOptions.Trade:
                        Network.Enqueue(new C.TradeRequest());
                        break;
                    case KeybindOptions.ChangePetmode:
                        ChangePetMode();
                        break;
                    case KeybindOptions.PetmodeBoth:
                        Network.Enqueue(new C.ChangePMode { Mode = PetMode.Both });
                        return;
                    case KeybindOptions.PetmodeMoveonly:
                        Network.Enqueue(new C.ChangePMode { Mode = PetMode.MoveOnly });
                        return;
                    case KeybindOptions.PetmodeAttackonly:
                        Network.Enqueue(new C.ChangePMode { Mode = PetMode.AttackOnly });
                        return;
                    case KeybindOptions.PetmodeNone:
                        Network.Enqueue(new C.ChangePMode { Mode = PetMode.None });
                        return;
                    case KeybindOptions.PetmodeFocusMasterTarget:
                        Network.Enqueue(new C.ChangePMode { Mode = PetMode.FocusMasterTarget });
                        return;
                    case KeybindOptions.CreatureAutoPickup://semiauto!
                        if (CMain.Time > IntelligentCreaturePickupTime)
                        {
                            IntelligentCreaturePickupTime = CMain.Time + 200;
                            Network.Enqueue(new C.IntelligentCreaturePickup { MouseMode = false, Location = MapControl.MapLocation });
                        }
                        break;
                    case KeybindOptions.CreaturePickup:
                        if (CMain.Time > IntelligentCreaturePickupTime)
                        {
                            IntelligentCreaturePickupTime = CMain.Time + 200;
                            Network.Enqueue(new C.IntelligentCreaturePickup { MouseMode = true, Location = MapControl.MapLocation });
                        }
                        break;
                    case KeybindOptions.ChangeAttackmode:
                        ChangeAttackMode();
                        break;
                    case KeybindOptions.AttackmodePeace:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.Peace });
                        return;
                    case KeybindOptions.AttackmodeGroup:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.Group });
                        return;
                    case KeybindOptions.AttackmodeGuild:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.Guild });
                        return;
                    case KeybindOptions.AttackmodeEnemyguild:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.EnemyGuild });
                        return;
                    case KeybindOptions.AttackmodeRedbrown:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.RedBrown });
                        return;
                    case KeybindOptions.AttackmodeAll:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.All });
                        return;

                    case KeybindOptions.Autorun:
                        MapControl.AutoRun = !MapControl.AutoRun;
                        break;
                    case KeybindOptions.DropView:
                        if (CMain.Time > DropViewTime)
                            DropViewTime = CMain.Time + 5000;
                            MapControl.TextureValid = false; // item names
                        break;
                    case KeybindOptions.TargetDead:
                        if (CMain.Time > TargetDeadTime)
                            TargetDeadTime = CMain.Time + 5000;
                        break;
                    default:
                        Ui.KeyBind(KeyCheck.function);
                        break;
                }
            }
        }

        public void ChangeSkillMode(bool? ctrl)
        {
            if (Settings.SkillMode || ctrl == true)
            {
                Settings.SkillMode = false;
                GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.SkillModeCtrl), ChatType.Hint);
                Ui.SkillModeChanged(true);
            }
            else if (!Settings.SkillMode || ctrl == false)
            {
                Settings.SkillMode = true;
                GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.SkillModeTilde), ChatType.Hint);
                Ui.SkillModeChanged(false);
            }
        }

        public void ChangePetMode()
        {
            if (CMain.Time > ChangePModeTime)
            {
                ChangePModeTime = CMain.Time + 500;

                switch (PMode)
                {
                    case PetMode.Both:
                        Network.Enqueue(new C.ChangePMode { Mode = PetMode.MoveOnly });
                        return;
                    case PetMode.MoveOnly:
                        Network.Enqueue(new C.ChangePMode { Mode = PetMode.AttackOnly });
                        return;
                    case PetMode.AttackOnly:
                        Network.Enqueue(new C.ChangePMode { Mode = PetMode.None });
                        return;
                    case PetMode.None:
                        Network.Enqueue(new C.ChangePMode { Mode = PetMode.FocusMasterTarget });
                        return;
                    case PetMode.FocusMasterTarget:
                        Network.Enqueue(new C.ChangePMode { Mode = PetMode.Both });
                        return;
                }
            }
        }

        public void ChangeAttackMode()
        {
            if (CMain.Time > ChangeAModeTime)
            {
                ChangeAModeTime = CMain.Time + 300;

                switch (AMode)
                {
                    case AttackMode.Peace:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.Group });
                        return;
                    case AttackMode.Group:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.Guild });
                        return;
                    case AttackMode.Guild:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.EnemyGuild });
                        return;
                    case AttackMode.EnemyGuild:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.RedBrown });
                        return;
                    case AttackMode.RedBrown:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.All });
                        return;
                    case AttackMode.All:
                        Network.Enqueue(new C.ChangeAMode { Mode = AttackMode.Peace });
                        return;
                }
            }
        }

        public void UseSpell(int key)
        {
            UserObject actor = User;
            if (key > 16)
            {
                if (HeroObject == null || CMain.Time < HeroSpellTime) return;

                actor = Hero;
                HeroSpellTime = CMain.Time + 200;
            }

            if (actor.Dead || actor.RidingMount || actor.Fishing) return;

            if (!actor.HasClassWeapon && actor.Weapon >= 0)
            {
                Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.MustWearSuitableWeapon), ChatType.System);
                return;
            }

            if (CMain.Time < actor.BlizzardStopTime || CMain.Time < actor.ReincarnationStopTime) return;

            ClientMagic magic = null;

            for (int i = 0; i < actor.Magics.Count; i++)
            {
                if (actor.Magics[i].Key != key) continue;
                magic = actor.Magics[i];
                break;
            }

            if (magic == null) return;

            switch (magic.Spell)
            {
                case Spell.CounterAttack:
                    if ((CMain.Time < magic.CastTime + magic.Delay))
                    {
                        if (CMain.Time >= OutputDelay)
                        {
                            OutputDelay = CMain.Time + 1000;
                            Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.CannotCastForSeconds), magic.Name,
                                ((magic.CastTime + magic.Delay) - CMain.Time - 1) / 1000 + 1));
                        }

                        return;
                    }
                    magic.CastTime = CMain.Time;
                    break;
            }

            int cost;
            string prefix = actor == Hero ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.Hero) : string.Empty;
            switch (magic.Spell)
            {
                case Spell.Fencing:
                case Spell.FatalSword:
                case Spell.MPEater:
                case Spell.Hemorrhage:
                case Spell.SpiritSword:
                case Spell.Slaying:
                case Spell.Focus:
                case Spell.Meditation:
                    return;
                case Spell.Thrusting:
                    if (CMain.Time < ToggleTime) return;
                    actor.Thrusting = !actor.Thrusting;
                    Models.Chat.Add(
                        prefix + (actor.Thrusting ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UseThrusting) : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DoNotUseThrusting)),
                        ChatType.Hint);
                    ToggleTime = CMain.Time + 1000;
                    SendSpellToggle(actor, magic.Spell, actor.Thrusting);
                    break;
                case Spell.HalfMoon:
                    if (CMain.Time < ToggleTime) return;
                    actor.HalfMoon = !actor.HalfMoon;
                    Models.Chat.Add(
                        prefix + (actor.HalfMoon ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UseHalfMoon) : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DoNotUseHalfMoon)),
                        ChatType.Hint);
                    ToggleTime = CMain.Time + 1000;
                    SendSpellToggle(actor, magic.Spell, actor.HalfMoon);
                    break;
                case Spell.CrossHalfMoon:
                    if (CMain.Time < ToggleTime) return;
                    actor.CrossHalfMoon = !actor.CrossHalfMoon;
                    Models.Chat.Add(
                        prefix + (actor.CrossHalfMoon ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UseCrossHalfMoon) : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DoNotUseCrossHalfMoon)),
                        ChatType.Hint);
                    ToggleTime = CMain.Time + 1000;
                    SendSpellToggle(actor, magic.Spell, actor.CrossHalfMoon);
                    break;
                case Spell.DoubleSlash:
                    if (CMain.Time < ToggleTime) return;
                    actor.DoubleSlash = !actor.DoubleSlash;
                    Models.Chat.Add(
                        prefix + (actor.DoubleSlash ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UseDoubleSlash) : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DoNotUseDoubleSlash)),
                        ChatType.Hint);
                    ToggleTime = CMain.Time + 1000;
                    SendSpellToggle(actor, magic.Spell, actor.DoubleSlash);
                    break;
                case Spell.TwinDrakeBlade:
                    if (CMain.Time < ToggleTime) return;
                    ToggleTime = CMain.Time + 500;

                    cost = magic.Level * magic.LevelCost + magic.BaseCost;
                    if (cost > actor.MP)
                    {
                        Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowMana));
                        return;
                    }
                    actor.TwinDrakeBlade = true;
                    SendSpellToggle(actor, magic.Spell, true);
                    if (actor == Hero)
                        HeroObject?.Effects.Add(new Effect(Libraries.Magic2, 210, 6, 500, HeroObject));
                    else
                        actor.Effects.Add(new Effect(Libraries.Magic2, 210, 6, 500, actor));
                    break;
                case Spell.FlamingSword:
                    if (CMain.Time < ToggleTime) return;
                    ToggleTime = CMain.Time + 500;

                    cost = magic.Level * magic.LevelCost + magic.BaseCost;
                    if (cost > actor.MP)
                    {
                        Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowMana));
                        return;
                    }
                    SendSpellToggle(actor, magic.Spell, true);
                    break;
                case Spell.CounterAttack:
                    cost = magic.Level * magic.LevelCost + magic.BaseCost;
                    if (cost > actor.MP)
                    {
                        Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowMana));
                        return;
                    }

                    SoundManager.PlaySound(20000 + (ushort)Spell.CounterAttack * 10);
                    SendSpellToggle(actor, magic.Spell, true);
                    break;
                case Spell.MentalState:
                    if (CMain.Time < ToggleTime) return;
                    ToggleTime = CMain.Time + 500;
                    SendSpellToggle(actor, magic.Spell, true);
                    break;
                default:
                    actor.NextMagic = magic;
                    actor.NextMagicLocation = MapControl.MapLocation;
                    actor.NextMagicObject = MapObject.MouseObject;
                    actor.NextMagicDirection = MapControl.MouseDirection();

                    if (actor == Hero)
                        MapControl.UseMagic(Hero.NextMagic, Hero);
                    break;
            }
        }
        private void SendSpellToggle(UserObject Actor, Spell Spell, bool CanUse)
        {
            if (Actor == User)
                Network.Enqueue(new C.SpellToggle { Spell = Spell, CanUse = CanUse });
            else
                Network.Enqueue(new C.SpellToggle { Spell = Spell });
        }
        public void QuitGame()
        {
            if (CMain.Time >= LogTime)
            {
                //If Last Combat < 10 CANCEL
                Ui.ConfirmQuit();
            }
            else
            {
                Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.CannotLeaveGame), (LogTime - CMain.Time) / 1000), ChatType.System);
            }
        }
        /// <param name="confirm">False when the caller already asked (the interface style switch).</param>
        public void LogOut(bool confirm = true)
        {
            if (CMain.Time >= LogTime)
            {
                if (!confirm)
                {
                    Network.Enqueue(new C.LogOut());
                    Enabled = false;
                    return;
                }

                //If Last Combat < 10 CANCEL
                Ui.ConfirmLogOut();
            }
            else
            {
                Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.CannotLeaveGame), (LogTime - CMain.Time) / 1000), ChatType.System);
            }
        }

        protected internal override void DrawControl()
        {
            if (MapControl != null && !MapControl.IsDisposed)
                MapControl.DrawControl();
            base.DrawControl();
            Ui?.DrawOverlay();


            if (PickedUpGold || (ItemMoves.Selected != null && ItemMoves.Selected.Item != null))
            {
                int image = PickedUpGold ? 116 : ItemMoves.Selected.Item.Image;
                Size imgSize = Libraries.Items.GetTrueSize(image);
                Point p = CMain.MPoint.Add(-imgSize.Width / 2, -imgSize.Height / 2);

                if (p.X + imgSize.Width >= Settings.ScreenWidth)
                    p.X = Settings.ScreenWidth - imgSize.Width;

                if (p.Y + imgSize.Height >= Settings.ScreenHeight)
                    p.Y = Settings.ScreenHeight - imgSize.Height;

                Libraries.Items.Draw(image, p.X, p.Y);
            }

            Ui?.DrawOutputLines();
        }
        /// <summary>
        /// An object moved or died. Its dot on the minimap and the big map moves; nothing else in the UI depends on it. With
        /// Settings.RebuildOptimisations only those dialogs are redrawn (the minimap is a live overlay, so only it re-renders)
        /// instead of the whole UI scene texture.
        /// </summary>
        public void ObjectPositionsChanged()
        {
            if (!Settings.RebuildOptimisations)
            {
                Redraw();
                return;
            }

            Ui.ObjectPositionsChanged();
        }

        public override void Process()
        {
            if (MapControl == null || User == null)
                return;

            if (CMain.Time >= MoveTime)
            {
                MoveTime = CMain.Time + 100; //Move Speed
                CanMove = true;
                MapControl.AnimationCount++;
                if (!Settings.RebuildOptimisations || MapControl.NeedsTimedRebuild())
                    MapControl.TextureValid = false;
            }
            else
                CanMove = false;

            if (CMain.Time >= CMain.NextPing)
            {
                CMain.NextPing = CMain.Time + 60000;
                Network.Enqueue(new C.KeepAlive() { Time = CMain.Time });
            }

            Ui.ProcessBeforeMap();

            MapControl.Process();
            Ui.Process();

            foreach (ParticleEngine pe in ParticleEngines)
                pe.Process();

            DialogProcess();

            ProcessOuput();

            UpdateMouseCursor();

            SoundManager.ProcessDelayedSounds();
        }

        public void DialogProcess()
        {
            Ui.DialogProcess();
        }

        public override void ProcessPacket(Packet p)
        {
            switch (p.Index)
            {
                case (short)ServerPacketIds.KeepAlive:
                    KeepAlive((S.KeepAlive)p);
                    break;
                case (short)ServerPacketIds.MapInformation: //MapInfo
                    MapInformation((S.MapInformation)p);
                    break;
                case (short)ServerPacketIds.NewMapInfo:
                    NewMapInfo((S.NewMapInfo)p);
                    break;
                case (short)ServerPacketIds.WorldMapSetup:
                    WorldMapSetup((S.WorldMapSetupInfo)p);
                    break;
                case (short)ServerPacketIds.SearchMapResult:
                    SearchMapResult((S.SearchMapResult)p);
                    break;
                case (short)ServerPacketIds.UserInformation:
                    UserInformation((S.UserInformation)p);
                    break;
                case (short)ServerPacketIds.UserSlotsRefresh:
                    UserSlotsRefresh((S.UserSlotsRefresh)p);
                    break;
                case (short)ServerPacketIds.UserLocation:
                    UserLocation((S.UserLocation)p);
                    break;
                case (short)ServerPacketIds.ObjectPlayer:
                    ObjectPlayer((S.ObjectPlayer)p);
                    break;
                case (short)ServerPacketIds.ObjectHero:
                    ObjectHero((S.ObjectHero)p);
                    break;
                case (short)ServerPacketIds.ObjectRemove:
                    ObjectRemove((S.ObjectRemove)p);
                    break;
                case (short)ServerPacketIds.ObjectTurn:
                    ObjectTurn((S.ObjectTurn)p);
                    break;
                case (short)ServerPacketIds.ObjectWalk:
                    ObjectWalk((S.ObjectWalk)p);
                    break;
                case (short)ServerPacketIds.ObjectRun:
                    ObjectRun((S.ObjectRun)p);
                    break;
                case (short)ServerPacketIds.ObjectMoveBatch:
                    ObjectMoveBatch((S.ObjectMoveBatch)p);
                    break;
                case (short)ServerPacketIds.Chat:
                    ReceiveChat((S.Chat)p);
                    break;
                case (short)ServerPacketIds.ObjectChat:
                    ObjectChat((S.ObjectChat)p);
                    break;
                case (short)ServerPacketIds.MoveItem:
                    MoveItem((S.MoveItem)p);
                    break;
                case (short)ServerPacketIds.EquipItem:
                    EquipItem((S.EquipItem)p);
                    break;
                case (short)ServerPacketIds.MergeItem:
                    MergeItem((S.MergeItem)p);
                    break;
                case (short)ServerPacketIds.RemoveItem:
                    RemoveItem((S.RemoveItem)p);
                    break;
                case (short)ServerPacketIds.RemoveSlotItem:
                    RemoveSlotItem((S.RemoveSlotItem)p);
                    break;
                case (short)ServerPacketIds.TakeBackItem:
                    TakeBackItem((S.TakeBackItem)p);
                    break;
                case (short)ServerPacketIds.StoreItem:
                    StoreItem((S.StoreItem)p);
                    break;
                case (short)ServerPacketIds.DepositRefineItem:
                    DepositRefineItem((S.DepositRefineItem)p);
                    break;
                case (short)ServerPacketIds.RetrieveRefineItem:
                    RetrieveRefineItem((S.RetrieveRefineItem)p);
                    break;
                case (short)ServerPacketIds.RefineCancel:
                    RefineCancel((S.RefineCancel)p);
                    break;
                case (short)ServerPacketIds.RefineItem:
                    RefineItem((S.RefineItem)p);
                    break;
                case (short)ServerPacketIds.DepositTradeItem:
                    DepositTradeItem((S.DepositTradeItem)p);
                    break;
                case (short)ServerPacketIds.RetrieveTradeItem:
                    RetrieveTradeItem((S.RetrieveTradeItem)p);
                    break;
                case (short)ServerPacketIds.SplitItem:
                    SplitItem((S.SplitItem)p);
                    break;
                case (short)ServerPacketIds.SplitItem1:
                    SplitItem1((S.SplitItem1)p);
                    break;
                case (short)ServerPacketIds.UseItem:
                    UseItem((S.UseItem)p);
                    break;
                case (short)ServerPacketIds.DropItem:
                    DropItem((S.DropItem)p);
                    break;
                case (short)ServerPacketIds.TakeBackHeroItem:
                    TakeBackHeroItem((S.TakeBackHeroItem)p);
                    break;
                case (short)ServerPacketIds.TransferHeroItem:
                    TransferHeroItem((S.TransferHeroItem)p);
                    break;
                case (short)ServerPacketIds.PlayerUpdate:
                    PlayerUpdate((S.PlayerUpdate)p);
                    break;
                case (short)ServerPacketIds.PlayerInspect:
                    PlayerInspect((S.PlayerInspect)p);
                    break;
                case (short)ServerPacketIds.LogOutSuccess:
                    LogOutSuccess((S.LogOutSuccess)p);
                    break;
                case (short)ServerPacketIds.LogOutFailed:
                    LogOutFailed((S.LogOutFailed)p);
                    break;
                case (short)ServerPacketIds.ReturnToLogin:
                    ReturnToLogin((S.ReturnToLogin)p);
                    break;
                case (short)ServerPacketIds.TimeOfDay:
                    TimeOfDay((S.TimeOfDay)p);
                    break;
                case (short)ServerPacketIds.ChangeAMode:
                    ChangeAMode((S.ChangeAMode)p);
                    break;
                case (short)ServerPacketIds.ChangePMode:
                    ChangePMode((S.ChangePMode)p);
                    break;
                case (short)ServerPacketIds.ObjectItem:
                    ObjectItem((S.ObjectItem)p);
                    break;
                case (short)ServerPacketIds.ObjectGold:
                    ObjectGold((S.ObjectGold)p);
                    break;
                case (short)ServerPacketIds.GainedItem:
                    GainedItem((S.GainedItem)p);
                    break;
                case (short)ServerPacketIds.GainedGold:
                    GainedGold((S.GainedGold)p);
                    break;
                case (short)ServerPacketIds.LoseGold:
                    LoseGold((S.LoseGold)p);
                    break;
                case (short)ServerPacketIds.GainedCredit:
                    GainedCredit((S.GainedCredit)p);
                    break;
                case (short)ServerPacketIds.LoseCredit:
                    LoseCredit((S.LoseCredit)p);
                    break;
                case (short)ServerPacketIds.ObjectMonster:
                    ObjectMonster((S.ObjectMonster)p);
                    break;
                case (short)ServerPacketIds.ObjectAttack:
                    ObjectAttack((S.ObjectAttack)p);
                    break;
                case (short)ServerPacketIds.Struck:
                    Struck((S.Struck)p);
                    break;
                case (short)ServerPacketIds.DamageIndicator:
                    DamageIndicator((S.DamageIndicator)p);
                    break;
                case (short)ServerPacketIds.ObjectStruck:
                    ObjectStruck((S.ObjectStruck)p);
                    break;
                case (short)ServerPacketIds.DuraChanged:
                    DuraChanged((S.DuraChanged)p);
                    break;
                case (short)ServerPacketIds.HealthChanged:
                    HealthChanged((S.HealthChanged)p);
                    break;
                case (short)ServerPacketIds.HeroHealthChanged:
                    HeroHealthChanged((S.HeroHealthChanged)p);
                    break;
                case (short)ServerPacketIds.DeleteItem:
                    DeleteItem((S.DeleteItem)p);
                    break;
                case (short)ServerPacketIds.Death:
                    Death((S.Death)p);
                    break;
                case (short)ServerPacketIds.ObjectDied:
                    ObjectDied((S.ObjectDied)p);
                    break;
                case (short)ServerPacketIds.ColourChanged:
                    ColourChanged((S.ColourChanged)p);
                    break;
                case (short)ServerPacketIds.ObjectColourChanged:
                    ObjectColourChanged((S.ObjectColourChanged)p);
                    break;
                case (short)ServerPacketIds.ObjectGuildNameChanged:
                    ObjectGuildNameChanged((S.ObjectGuildNameChanged)p);
                    break;
                case (short)ServerPacketIds.GainExperience:
                    GainExperience((S.GainExperience)p);
                    break;
                case (short)ServerPacketIds.GainHeroExperience:
                    GainHeroExperience((S.GainHeroExperience)p);
                    break;
                case (short)ServerPacketIds.LevelChanged:
                    LevelChanged((S.LevelChanged)p);
                    break;
                case (short)ServerPacketIds.HeroLevelChanged:
                    HeroLevelChanged((S.HeroLevelChanged)p);
                    break;
                case (short)ServerPacketIds.ObjectLeveled:
                    ObjectLeveled((S.ObjectLeveled)p);
                    break;
                case (short)ServerPacketIds.ObjectHarvest:
                    ObjectHarvest((S.ObjectHarvest)p);
                    break;
                case (short)ServerPacketIds.ObjectHarvested:
                    ObjectHarvested((S.ObjectHarvested)p);
                    break;
                case (short)ServerPacketIds.ObjectNpc:
                    ObjectNPC((S.ObjectNPC)p);
                    break;
                case (short)ServerPacketIds.NPCResponse:
                    NPCResponse((S.NPCResponse)p);
                    break;
                case (short)ServerPacketIds.ObjectHide:
                    ObjectHide((S.ObjectHide)p);
                    break;
                case (short)ServerPacketIds.ObjectShow:
                    ObjectShow((S.ObjectShow)p);
                    break;
                case (short)ServerPacketIds.Poisoned:
                    Poisoned((S.Poisoned)p);
                    break;
                case (short)ServerPacketIds.ObjectPoisoned:
                    ObjectPoisoned((S.ObjectPoisoned)p);
                    break;
                case (short)ServerPacketIds.MapChanged:
                    MapChanged((S.MapChanged)p);
                    break;
                case (short)ServerPacketIds.DungeonMap:
                    GeneratedMaps.Add((S.DungeonMap)p);
                    break;
                case (short)ServerPacketIds.ObjectTeleportOut:
                    ObjectTeleportOut((S.ObjectTeleportOut)p);
                    break;
                case (short)ServerPacketIds.ObjectTeleportIn:
                    ObjectTeleportIn((S.ObjectTeleportIn)p);
                    break;
                case (short)ServerPacketIds.TeleportIn:
                    TeleportIn();
                    break;
                case (short)ServerPacketIds.NPCGoods:
                    NPCGoods((S.NPCGoods)p);
                    break;
                case (short)ServerPacketIds.NPCSell:
                    NPCSell();
                    break;
                case (short)ServerPacketIds.NPCRepair:
                    NPCRepair((S.NPCRepair)p);
                    break;
                case (short)ServerPacketIds.NPCSRepair:
                    NPCSRepair((S.NPCSRepair)p);
                    break;
                case (short)ServerPacketIds.NPCRefine:
                    NPCRefine((S.NPCRefine)p);
                    break;
                case (short)ServerPacketIds.NPCCheckRefine:
                    NPCCheckRefine((S.NPCCheckRefine)p);
                    break;
                case (short)ServerPacketIds.NPCCollectRefine:
                    NPCCollectRefine((S.NPCCollectRefine)p);
                    break;
                case (short)ServerPacketIds.NPCReplaceWedRing:
                    NPCReplaceWedRing((S.NPCReplaceWedRing)p);
                    break;
                case (short)ServerPacketIds.NPCStorage:
                    NPCStorage();
                    break;
                case (short)ServerPacketIds.StorageUnlockResult:
                    StorageUnlockResult((S.StorageUnlockResult)p);
                    break;
                case (short)ServerPacketIds.StoragePasswordResult:
                    StoragePasswordResult((S.StoragePasswordResult)p);
                    break;
                case (short)ServerPacketIds.NPCRequestInput:
                    NPCRequestInput((S.NPCRequestInput)p);
                    break;
                case (short)ServerPacketIds.SellItem:
                    SellItem((S.SellItem)p);
                    break;
                case (short)ServerPacketIds.CraftItem:
                    CraftItem((S.CraftItem)p);
                    break;
                case (short)ServerPacketIds.RepairItem:
                    RepairItem((S.RepairItem)p);
                    break;
                case (short)ServerPacketIds.ItemRepaired:
                    ItemRepaired((S.ItemRepaired)p);
                    break;
                case (short)ServerPacketIds.ItemSlotSizeChanged:
                    ItemSlotSizeChanged((S.ItemSlotSizeChanged)p);
                    break;
                case (short)ServerPacketIds.ItemSealChanged:
                    ItemSealChanged((S.ItemSealChanged)p);
                    break;
                case (short)ServerPacketIds.NewMagic:
                    NewMagic((S.NewMagic)p);
                    break;
                case (short)ServerPacketIds.MagicLeveled:
                    MagicLeveled((S.MagicLeveled)p);
                    break;
                case (short)ServerPacketIds.Magic:
                    Magic((S.Magic)p);
                    break;
                case (short)ServerPacketIds.MagicDelay:
                    MagicDelay((S.MagicDelay)p);
                    break;
                case (short)ServerPacketIds.MagicCast:
                    MagicCast((S.MagicCast)p);
                    break;
                case (short)ServerPacketIds.ObjectMagic:
                    ObjectMagic((S.ObjectMagic)p);
                    break;
                case (short)ServerPacketIds.ObjectProjectile:
                    ObjectProjectile((S.ObjectProjectile)p);
                    break;
                case (short)ServerPacketIds.ObjectEffect:
                    ObjectEffect((S.ObjectEffect)p);
                    break;
                case (short)ServerPacketIds.RangeAttack:
                    RangeAttack((S.RangeAttack)p);
                    break;
                case (short)ServerPacketIds.Pushed:
                    Pushed((S.Pushed)p);
                    break;
                case (short)ServerPacketIds.ObjectPushed:
                    ObjectPushed((S.ObjectPushed)p);
                    break;
                case (short)ServerPacketIds.ObjectName:
                    ObjectName((S.ObjectName)p);
                    break;
                case (short)ServerPacketIds.UserStorage:
                    UserStorage((S.UserStorage)p);
                    break;
                case (short)ServerPacketIds.SwitchGroup:
                    SwitchGroup((S.SwitchGroup)p);
                    break;
                case (short)ServerPacketIds.DeleteGroup:
                    DeleteGroup();
                    break;
                case (short)ServerPacketIds.DeleteMember:
                    DeleteMember((S.DeleteMember)p);
                    break;
                case (short)ServerPacketIds.GroupInvite:
                    GroupInvite((S.GroupInvite)p);
                    break;
                case (short)ServerPacketIds.AddMember:
                    AddMember((S.AddMember)p);
                    break;
                case (short)ServerPacketIds.GroupMembersMap:
                    GroupMembersMap((S.GroupMembersMap)p);
                    break;
                case (short)ServerPacketIds.SendMemberLocation:
                    SendMemberLocation((S.SendMemberLocation)p);
                    break;
                case (short)ServerPacketIds.Revived:
                    Revived();
                    break;
                case (short)ServerPacketIds.ObjectRevived:
                    ObjectRevived((S.ObjectRevived)p);
                    break;
                case (short)ServerPacketIds.SpellToggle:
                    SpellToggle((S.SpellToggle)p);
                    break;
                case (short)ServerPacketIds.ObjectHealth:
                    ObjectHealth((S.ObjectHealth)p);
                    break;
                case (short)ServerPacketIds.ObjectMana:
                    ObjectMana((S.ObjectMana)p);
                    break;
                case (short)ServerPacketIds.MapEffect:
                    MapEffect((S.MapEffect)p);
                    break;
                case (short)ServerPacketIds.AllowObserve:
                    AllowObserve = ((S.AllowObserve)p).Allow;
                    break;
                case (short)ServerPacketIds.ServerRules:
                    Models.ServerRules.Set((S.ServerRules)p); // costs and limits for the modern windows (phase 1b)
                    break;
                case (short)ServerPacketIds.AllowToggles:
                    Models.AllowToggles.Set(((S.AllowToggles)p).Flags);
                    break;
                // Quest V2 (wave 2 kick-off): handlers in GameScene.QuestsV2.cs, filled by lanes C / D
                case (short)ServerPacketIds.QuestDefinition:
                    QuestDefinition((S.QuestDefinition)p);
                    break;
                case (short)ServerPacketIds.QuestStates:
                    QuestStates((S.QuestStates)p);
                    break;
                case (short)ServerPacketIds.QuestProgress:
                    QuestProgress((S.QuestProgress)p);
                    break;
                case (short)ServerPacketIds.QuestHistory:
                    QuestHistory((S.QuestHistory)p);
                    break;
                case (short)ServerPacketIds.QuestAvailability:
                    QuestAvailability((S.QuestAvailability)p);
                    break;
                case (short)ServerPacketIds.QuestRefused:
                    QuestRefused((S.QuestRefused)p);
                    break;
                case (short)ServerPacketIds.QuestOffer:
                    QuestOffer((S.QuestOffer)p);
                    break;
                case (short)ServerPacketIds.PlayerProfile:
                    {
                        var profile = (S.PlayerProfile)p; // the modern inspect window's profile (phase 2)
                        Models.Inspect.ProfileName = profile.Name ?? string.Empty;
                        Models.Inspect.ProfileText = profile.Text ?? string.Empty;
                        Models.Inspect.ProfileChanged.Emit();
                    }
                    break;
                // Phase 4: model-only packets (the classic windows do not read them)
                case (short)ServerPacketIds.PoisonStatus:
                    Models.Poisons.Apply(((S.PoisonStatus)p).Poisons);
                    break;
                case (short)ServerPacketIds.GroupMemberStatus:
                    GroupMemberStatus((S.GroupMemberStatus)p);
                    break;
                case (short)ServerPacketIds.GuildMemberDetails:
                    {
                        var details = (S.GuildMemberDetails)p;
                        Models.Guild.ApplyMemberDetails(details.Part, details.PartCount, details.Members);
                    }
                    break;
                case (short)ServerPacketIds.GuildWarList:
                    Models.Guild.ApplyWars(((S.GuildWarList)p).Wars);
                    break;
                case (short)ServerPacketIds.GuildLog:
                    Models.Guild.ApplyLog(((S.GuildLog)p).Entries);
                    break;
                case (short)ServerPacketIds.RankingsDetail:
                    Models.Rankings.ApplyDetails((S.RankingsDetail)p);
                    break;
                case (short)ServerPacketIds.GuildRankings:
                    Models.Rankings.ApplyGuildPage((S.GuildRankings)p);
                    break;
                case (short)ServerPacketIds.MentorDetails:
                    Models.Relationship.ApplyMentorDetails((S.MentorDetails)p);
                    break;
                case (short)ServerPacketIds.MentorCandidates:
                    Models.Relationship.ApplyCandidates((S.MentorCandidates)p);
                    break;
                case (short)ServerPacketIds.FriendRequest:
                    Models.Friends.AddRequest((S.FriendRequest)p);
                    break;
                case (short)ServerPacketIds.FriendRequestResult:
                    Models.Friends.ResultReceived.Emit((S.FriendRequestResult)p);
                    break;
                case (short)ServerPacketIds.MarketPriceHistory:
                    {
                        var history = (S.MarketPriceHistory)p; // the modern trust merchant's price history (phase 3)
                        Models.Market.PriceHistory[history.ItemIndex] = history.Sales ?? new List<ClientMarketSale>();
                        Models.Market.PriceHistoryChanged.Emit(history.ItemIndex);
                    }
                    break;
                case (short)ServerPacketIds.SentMail:
                    {
                        var sent = (S.SentMail)p; // the modern mail window's sent tab (phase 3)
                        Models.Mail.Sent.Clear();
                        if (sent.Mails != null) Models.Mail.Sent.AddRange(sent.Mails);
                        Models.Mail.SentKnown = true;
                        Models.Mail.SentChanged.Emit();
                    }
                    break;
                case (short)ServerPacketIds.HeroAutoCast:
                    Models.HeroAutoCast.Set(((S.HeroAutoCast)p).Disabled); // the modern skills window's hero auto-cast switches
                    break;
                case (short)ServerPacketIds.MapDetails:
                    Models.MapDetails.Apply((S.MapDetails)p); // the modern big map's respawns / safe zones (phase 2)
                    break;
                case (short)ServerPacketIds.ObjectRangeAttack:
                    ObjectRangeAttack((S.ObjectRangeAttack)p);
                    break;
                case (short)ServerPacketIds.AddBuff:
                    AddBuff((S.AddBuff)p);
                    break;
                case (short)ServerPacketIds.RemoveBuff:
                    RemoveBuff((S.RemoveBuff)p);
                    break;
                case (short)ServerPacketIds.PauseBuff:
                    PauseBuff((S.PauseBuff)p);
                    break;
                case (short)ServerPacketIds.ObjectHidden:
                    ObjectHidden((S.ObjectHidden)p);
                    break;
                case (short)ServerPacketIds.RefreshItem:
                    RefreshItem((S.RefreshItem)p);
                    break;
                case (short)ServerPacketIds.ObjectSpell:
                    ObjectSpell((S.ObjectSpell)p);
                    break;
                case (short)ServerPacketIds.UserDash:
                    UserDash((S.UserDash)p);
                    break;
                case (short)ServerPacketIds.ObjectDash:
                    ObjectDash((S.ObjectDash)p);
                    break;
                case (short)ServerPacketIds.UserDashFail:
                    UserDashFail((S.UserDashFail)p);
                    break;
                case (short)ServerPacketIds.ObjectDashFail:
                    ObjectDashFail((S.ObjectDashFail)p);
                    break;
                case (short)ServerPacketIds.NPCConsign:
                    NPCConsign();
                    break;
                case (short)ServerPacketIds.NPCMarket:
                    NPCMarket((S.NPCMarket)p);
                    break;
                case (short)ServerPacketIds.NPCMarketPage:
                    NPCMarketPage((S.NPCMarketPage)p);
                    break;
                case (short)ServerPacketIds.ConsignItem:
                    ConsignItem((S.ConsignItem)p);
                    break;
                case (short)ServerPacketIds.MarketFail:
                    MarketFail((S.MarketFail)p);
                    break;
                case (short)ServerPacketIds.MarketSuccess:
                    MarketSuccess((S.MarketSuccess)p);
                    break;
                case (short)ServerPacketIds.ObjectSitDown:
                    ObjectSitDown((S.ObjectSitDown)p);
                    break;
                case (short)ServerPacketIds.InTrapRock:
                    S.InTrapRock packetdata = (S.InTrapRock)p;
                    User.InTrapRock = packetdata.Trapped;
                    break;
                case (short)ServerPacketIds.RemoveMagic:
                    RemoveMagic((S.RemoveMagic)p);
                    break;
                case (short)ServerPacketIds.BaseStatsInfo:
                    BaseStatsInfo((S.BaseStatsInfo)p);
                    break;
                case (short)ServerPacketIds.HeroBaseStatsInfo:
                    HeroBaseStatsInfo((S.HeroBaseStatsInfo)p);
                    break;
                case (short)ServerPacketIds.UserName:
                    UserName((S.UserName)p);
                    break;
                case (short)ServerPacketIds.ChatItemStats:
                    ChatItemStats((S.ChatItemStats)p);
                    break;
                case (short)ServerPacketIds.GuildInvite:
                    GuildInvite((S.GuildInvite)p);
                    break;
                case (short)ServerPacketIds.GuildMemberChange:
                    GuildMemberChange((S.GuildMemberChange)p);
                    break;
                case (short)ServerPacketIds.GuildNoticeChange:
                    GuildNoticeChange((S.GuildNoticeChange)p);
                    break;
                case (short)ServerPacketIds.GuildStatus:
                    GuildStatus((S.GuildStatus)p);
                    break;
                case (short)ServerPacketIds.GuildExpGain:
                    GuildExpGain((S.GuildExpGain)p);
                    break;
                case (short)ServerPacketIds.GuildNameRequest:
                    GuildNameRequest((S.GuildNameRequest)p);
                    break;
                case (short)ServerPacketIds.GuildStorageGoldChange:
                    GuildStorageGoldChange((S.GuildStorageGoldChange)p);
                    break;
                case (short)ServerPacketIds.GuildStorageItemChange:
                    GuildStorageItemChange((S.GuildStorageItemChange)p);
                    break;
                case (short)ServerPacketIds.GuildStorageList:
                    GuildStorageList((S.GuildStorageList)p);
                    break;
                case (short)ServerPacketIds.GuildRequestWar:
                    GuildRequestWar((S.GuildRequestWar)p);
                    break;
                case (short)ServerPacketIds.HeroCreateRequest:
                    HeroCreateRequest((S.HeroCreateRequest)p);
                    break;
                case (short)ServerPacketIds.NewHero:
                    NewHero((S.NewHero)p);
                    break;
                case (short)ServerPacketIds.HeroInformation:
                    HeroInformation((S.HeroInformation)p);
                    break;
                case (short)ServerPacketIds.UpdateHeroSpawnState:
                    UpdateHeroSpawnState((S.UpdateHeroSpawnState)p);
                    break;
                case (short)ServerPacketIds.UnlockHeroAutoPot:
                    UnlockHeroAutoPot(true);
                    break;
                case (short)ServerPacketIds.SetAutoPotValue:
                    SetAutoPotValue((S.SetAutoPotValue)p);
                    break;
                case (short)ServerPacketIds.SetHeroBehaviour:
                    SetHeroBehaviour((S.SetHeroBehaviour)p);
                    break;
                case (short)ServerPacketIds.SetAutoPotItem:
                    SetAutoPotItem((S.SetAutoPotItem)p);
                    break;
                case (short)ServerPacketIds.ManageHeroes:
                    ManageHeroes((S.ManageHeroes)p);
                    break;
                case (short)ServerPacketIds.ChangeHero:
                    ChangeHero((S.ChangeHero)p);
                    break;
                case (short)ServerPacketIds.DefaultNPC:
                    DefaultNPC((S.DefaultNPC)p);
                    break;
                case (short)ServerPacketIds.NPCUpdate:
                    NPCUpdate((S.NPCUpdate)p);
                    break;
                case (short)ServerPacketIds.NPCImageUpdate:
                    NPCImageUpdate((S.NPCImageUpdate)p);
                    break;
                case (short)ServerPacketIds.MarriageRequest:
                    MarriageRequest((S.MarriageRequest)p);
                    break;
                case (short)ServerPacketIds.DivorceRequest:
                    DivorceRequest((S.DivorceRequest)p);
                    break;
                case (short)ServerPacketIds.MentorRequest:
                    MentorRequest((S.MentorRequest)p);
                    break;
                case (short)ServerPacketIds.TradeRequest:
                    TradeRequest((S.TradeRequest)p);
                    break;
                case (short)ServerPacketIds.TradeAccept:
                    TradeAccept((S.TradeAccept)p);
                    break;
                case (short)ServerPacketIds.TradeGold:
                    TradeGold((S.TradeGold)p);
                    break;
                case (short)ServerPacketIds.TradeItem:
                    TradeItem((S.TradeItem)p);
                    break;
                case (short)ServerPacketIds.TradeConfirm:
                    TradeConfirm();
                    break;
                case (short)ServerPacketIds.TradeCancel:
                    TradeCancel((S.TradeCancel)p);
                    break;
                case (short)ServerPacketIds.MountUpdate:
                    MountUpdate((S.MountUpdate)p);
                    break;
                case (short)ServerPacketIds.TransformUpdate:
                    TransformUpdate((S.TransformUpdate)p);
                    break;
                case (short)ServerPacketIds.EquipSlotItem:
                    EquipSlotItem((S.EquipSlotItem)p);
                    break;
                case (short)ServerPacketIds.FishingUpdate:
                    FishingUpdate((S.FishingUpdate)p);
                    break;
                case (short)ServerPacketIds.GainedQuestItem:
                    GainedQuestItem((S.GainedQuestItem)p);
                    break;
                case (short)ServerPacketIds.DeleteQuestItem:
                    DeleteQuestItem((S.DeleteQuestItem)p);
                    break;
                case (short)ServerPacketIds.CancelReincarnation:
                    User.ReincarnationStopTime = 0;
                    break;
                case (short)ServerPacketIds.RequestReincarnation:
                    if (!User.Dead) return;
                    RequestReincarnation();
                    break;
                case (short)ServerPacketIds.UserBackStep:
                    UserBackStep((S.UserBackStep)p);
                    break;
                case (short)ServerPacketIds.ObjectBackStep:
                    ObjectBackStep((S.ObjectBackStep)p);
                    break;
                case (short)ServerPacketIds.UserDashAttack:
                    UserDashAttack((S.UserDashAttack)p);
                    break;
                case (short)ServerPacketIds.ObjectDashAttack:
                    ObjectDashAttack((S.ObjectDashAttack)p);
                    break;
                case (short)ServerPacketIds.UserAttackMove://Warrior Skill - SlashingBurst
                    UserAttackMove((S.UserAttackMove)p);
                    break;
                case (short)ServerPacketIds.CombineItem:
                    CombineItem((S.CombineItem)p);
                    break;
                case (short)ServerPacketIds.ItemUpgraded:
                    ItemUpgraded((S.ItemUpgraded)p);
                    break;
                case (short)ServerPacketIds.SetConcentration:
                    SetConcentration((S.SetConcentration)p);
                    break;
                case (short)ServerPacketIds.SetElemental:
                    SetElemental((S.SetElemental)p);
                    break;
                case (short)ServerPacketIds.RemoveDelayedExplosion:
                    RemoveDelayedExplosion((S.RemoveDelayedExplosion)p);
                    break;
                case (short)ServerPacketIds.ObjectDeco:
                    ObjectDeco((S.ObjectDeco)p);
                    break;
                case (short)ServerPacketIds.ObjectSneaking:
                    ObjectSneaking((S.ObjectSneaking)p);
                    break;
                case (short)ServerPacketIds.ObjectLevelEffects:
                    ObjectLevelEffects((S.ObjectLevelEffects)p);
                    break;
                case (short)ServerPacketIds.SetBindingShot:
                    SetBindingShot((S.SetBindingShot)p);
                    break;
                case (short)ServerPacketIds.SendOutputMessage:
                    SendOutputMessage((S.SendOutputMessage)p);
                    break;
                case (short)ServerPacketIds.NPCAwakening:
                    NPCAwakening();
                    break;
                case (short)ServerPacketIds.NPCDisassemble:
                    NPCDisassemble();
                    break;
                case (short)ServerPacketIds.NPCDowngrade:
                    NPCDowngrade();
                    break;
                case (short)ServerPacketIds.NPCReset:
                    NPCReset();
                    break;
                case (short)ServerPacketIds.AwakeningNeedMaterials:
                    AwakeningNeedMaterials((S.AwakeningNeedMaterials)p);
                    break;
                case (short)ServerPacketIds.AwakeningLockedItem:
                    AwakeningLockedItem((S.AwakeningLockedItem)p);
                    break;
                case (short)ServerPacketIds.Awakening:
                    Awakening((S.Awakening)p);
                    break;
                case (short)ServerPacketIds.ReceiveMail:
                    ReceiveMail((S.ReceiveMail)p);
                    break;
                case (short)ServerPacketIds.MailLockedItem:
                    MailLockedItem((S.MailLockedItem)p);
                    break;
                case (short)ServerPacketIds.MailSent:
                    MailSent((S.MailSent)p);
                    break;
                case (short)ServerPacketIds.MailSendRequest:
                    MailSendRequest((S.MailSendRequest)p);
                    break;
                case (short)ServerPacketIds.ParcelCollected:
                    ParcelCollected((S.ParcelCollected)p);
                    break;
                case (short)ServerPacketIds.MailCost:
                    MailCost((S.MailCost)p);
                    break;
                case (short)ServerPacketIds.ResizeInventory:
                    ResizeInventory((S.ResizeInventory)p);
                    break;
                case (short)ServerPacketIds.ResizeStorage:
                    ResizeStorage((S.ResizeStorage)p);
                    break;
                case (short)ServerPacketIds.NewIntelligentCreature:
                    NewIntelligentCreature((S.NewIntelligentCreature)p);
                    break;
                case (short)ServerPacketIds.UpdateIntelligentCreatureList:
                    UpdateIntelligentCreatureList((S.UpdateIntelligentCreatureList)p);
                    break;
                case (short)ServerPacketIds.IntelligentCreatureEnableRename:
                    IntelligentCreatureEnableRename((S.IntelligentCreatureEnableRename)p);
                    break;
                case (short)ServerPacketIds.IntelligentCreaturePickup:
                    IntelligentCreaturePickup((S.IntelligentCreaturePickup)p);
                    break;
                case (short)ServerPacketIds.NPCPearlGoods:
                    NPCPearlGoods((S.NPCPearlGoods)p);
                    break;
                case (short)ServerPacketIds.FriendUpdate:
                    FriendUpdate((S.FriendUpdate)p);
                    break;
                case (short)ServerPacketIds.LoverUpdate:
                    LoverUpdate((S.LoverUpdate)p);
                    break;
                case (short)ServerPacketIds.MentorUpdate:
                    MentorUpdate((S.MentorUpdate)p);
                    break;
                case (short)ServerPacketIds.GuildBuffList:
                    GuildBuffList((S.GuildBuffList)p);
                    break;
                case (short)ServerPacketIds.GameShopInfo:
                    GameShopUpdate((S.GameShopInfo)p);
                    break;
                case (short)ServerPacketIds.GameShopStock:
                    GameShopStock((S.GameShopStock)p);
                    break;
                case (short)ServerPacketIds.Rankings:
                    Rankings((S.Rankings)p);
                    break;
                case (short)ServerPacketIds.Opendoor:
                    Opendoor((S.Opendoor)p);
                    break;
                case (short)ServerPacketIds.GetRentedItems:
                    RentedItems((S.GetRentedItems)p);
                    break;
                case (short)ServerPacketIds.ItemRentalRequest:
                    ItemRentalRequest((S.ItemRentalRequest)p);
                    break;
                case (short)ServerPacketIds.ItemRentalFee:
                    ItemRentalFee((S.ItemRentalFee)p);
                    break;
                case (short)ServerPacketIds.ItemRentalPeriod:
                    ItemRentalPeriod((S.ItemRentalPeriod)p);
                    break;
                case (short)ServerPacketIds.DepositRentalItem:
                    DepositRentalItem((S.DepositRentalItem)p);
                    break;
                case (short)ServerPacketIds.RetrieveRentalItem:
                    RetrieveRentalItem((S.RetrieveRentalItem)p);
                    break;
                case (short)ServerPacketIds.UpdateRentalItem:
                    UpdateRentalItem((S.UpdateRentalItem)p);
                    break;
                case (short)ServerPacketIds.CancelItemRental:
                    CancelItemRental((S.CancelItemRental)p);
                    break;
                case (short)ServerPacketIds.ItemRentalLock:
                    ItemRentalLock((S.ItemRentalLock)p);
                    break;
                case (short)ServerPacketIds.ItemRentalPartnerLock:
                    ItemRentalPartnerLock((S.ItemRentalPartnerLock)p);
                    break;
                case (short)ServerPacketIds.CanConfirmItemRental:
                    CanConfirmItemRental((S.CanConfirmItemRental)p);
                    break;
                case (short)ServerPacketIds.ConfirmItemRental:
                    ConfirmItemRental((S.ConfirmItemRental)p);
                    break;
                case (short)ServerPacketIds.OpenBrowser:
                    OpenBrowser((S.OpenBrowser)p);
                    break;
                case (short)ServerPacketIds.PlaySound:
                    PlaySound((S.PlaySound)p);
                    break;
                case (short)ServerPacketIds.SetTimer:
                    SetTimer((S.SetTimer)p);
                    break;
                case (short)ServerPacketIds.ExpireTimer:
                    ExpireTimer((S.ExpireTimer)p);
                    break;
                case (short)ServerPacketIds.UpdateNotice:
                    ShowNotice((S.UpdateNotice)p);
                    break;
                case (short)ServerPacketIds.Roll:
                    Roll((S.Roll)p);
                    break;
                case (short)ServerPacketIds.SetCompass:
                    SetCompass((S.SetCompass)p);
                    break;
                case (short)ServerPacketIds.GuildTerritoryPage:
                    GuildTerritoryPage((S.GuildTerritoryPage)p);
                    break;
                case (short)ServerPacketIds.NewMonsterInfo:
                    NewMonsterInfo((S.NewMonsterInfo)p);
                    break;
                case (short)ServerPacketIds.NewNPCInfo:
                    NewNPCInfo((S.NewNPCInfo)p);
                    break;
                case (short)ServerPacketIds.GameDataChanged:
                    GameDataChanged((S.GameDataChanged)p);
                    break;
                case (short)ServerPacketIds.MapAtmosphere:
                    MapAtmosphere((S.MapAtmosphere)p);
                    break;
                default:
                    base.ProcessPacket(p);
                    break;
            }
        }

        #region LiveDb push (S.GameDataChanged)

        // Accumulated over the chunks of one push; applied and reset when the chunk marked Last arrives.
        private bool _gameDataMagicsChanged, _gameDataHoverChanged, _gameDataShopChanged;

        /// <summary>
        /// LiveDb push: the server changed system data this client holds. Items are updated in place (UserItem.Info references stay
        /// valid), monsters / NPCs are upserted (quests: no section since quest V2), spells are updated by Spell on the user and the hero (only the server's
        /// values: name, costs, icon, levels, needs, range, delay; the player's own level / key / cooldown stay), game-shop rows are
        /// removed by GIndex and re-added, and an invalidated map drops its cached big-map record so it is requested again. Stats,
        /// skill bars and the hovered item's label are refreshed once, on the last chunk. Runs once per packet (no per-frame work).
        /// </summary>
        private void GameDataChanged(S.GameDataChanged p)
        {
            try
            {
                for (int i = 0; i < p.Items.Count; i++)
                {
                    ItemInfo held = UpsertItemInfo(p.Items[i]);
                    OnItemInfoReceived(p.Items[i].Index);
                    if (HoverItem != null && HoverItem.Info == held) _gameDataHoverChanged = true;
                }

                for (int i = 0; i < p.Monsters.Count; i++) UpsertMonsterInfo(p.Monsters[i]);
                for (int i = 0; i < p.Npcs.Count; i++) UpsertNPCInfo(p.Npcs[i]);

                if (User != null)
                    for (int i = 0; i < p.Magics.Count; i++)
                        if (CopyMagicInfo(User, p.Magics[i])) _gameDataMagicsChanged = true;

                if (Hero != null)
                    for (int i = 0; i < p.HeroMagics.Count; i++)
                        if (CopyMagicInfo(Hero, p.HeroMagics[i])) _gameDataMagicsChanged = true;

                for (int i = 0; i < p.GameShopRemoved.Count; i++)
                    if (RemoveGameShopItem(p.GameShopRemoved[i])) _gameDataShopChanged = true;

                for (int i = 0; i < p.GameShop.Count; i++)
                {
                    S.GameDataChanged.ShopEntry entry = p.GameShop[i];
                    if (entry?.Item == null) continue;
                    RemoveGameShopItem(entry.Item.GIndex);
                    entry.Item.Stock = entry.StockLevel;
                    GameShopInfoList.Add(entry.Item);
                    _gameDataShopChanged = true;
                }

                for (int i = 0; i < p.MapInfoInvalidated.Count; i++) Ui.MapInfoInvalidated(p.MapInfoInvalidated[i]);
            }
            catch (Exception ex)
            {
                if (Settings.LogErrors) CMain.SaveError("[GameDataChanged] " + ex);
            }

            if (!p.Last) return;

            try
            {
                if (User != null) User.RefreshStats();
                if (Hero != null) Hero.RefreshStats();

                Ui.GameDataRefreshed(_gameDataMagicsChanged, _gameDataShopChanged, _gameDataHoverChanged);
            }
            catch (Exception ex)
            {
                if (Settings.LogErrors) CMain.SaveError("[GameDataChanged] refresh: " + ex);
            }
            finally
            {
                _gameDataMagicsChanged = false;
                _gameDataHoverChanged = false;
                _gameDataShopChanged = false;
            }
        }

        /// <summary>Copies the server-owned fields of a pushed spell onto the actor's own ClientMagic with the same Spell.</summary>
        private static bool CopyMagicInfo(UserObject actor, ClientMagic pushed)
        {
            if (actor?.Magics == null || pushed == null) return false;
            for (int i = 0; i < actor.Magics.Count; i++)
            {
                ClientMagic magic = actor.Magics[i];
                if (magic == null || magic.Spell != pushed.Spell) continue;

                magic.Name = pushed.Name;
                magic.BaseCost = pushed.BaseCost;
                magic.LevelCost = pushed.LevelCost;
                magic.Icon = pushed.Icon;
                magic.Level1 = pushed.Level1;
                magic.Level2 = pushed.Level2;
                magic.Level3 = pushed.Level3;
                magic.Need1 = pushed.Need1;
                magic.Need2 = pushed.Need2;
                magic.Need3 = pushed.Need3;
                magic.Range = pushed.Range;
                magic.Delay = pushed.Delay;
                return true;
            }

            return false;
        }

        private static bool RemoveGameShopItem(int gIndex)
        {
            bool removed = false;
            for (int i = GameShopInfoList.Count - 1; i >= 0; i--)
            {
                if (GameShopInfoList[i].GIndex != gIndex) continue;
                GameShopInfoList.RemoveAt(i);
                removed = true;
            }

            return removed;
        }

        #endregion

        private void KeepAlive(S.KeepAlive p)
        {
            if (p.Time == 0) return;
            CMain.PingTime = (CMain.Time - p.Time);
        }
        private void MapInformation(S.MapInformation p)
        {
            if (MapControl != null && !MapControl.IsDisposed)
                MapControl.Dispose();
            MapControl = new MapControl
            {
                Index = p.MapIndex,
                FileName = Path.Combine(Settings.MapPath, p.FileName + ".map"),
                Title = p.Title,
                MiniMap = p.MiniMap,
                BigMap = p.BigMap,
                Lights = p.Lights,
                Lightning = p.Lightning,
                Fire = p.Fire,
                MapDarkLight = p.MapDarkLight,
                Music = p.Music
            };
            MapControl.Weather = p.WeatherParticles;
            MapControl.LoadMap();
            InsertControl(0, MapControl);
            Ui.MapChanged(p.MapIndex);
        }

        private void WorldMapSetup(S.WorldMapSetupInfo info)
        {
            Ui.WorldMapSetup(info.Setup);
            TeleportToNPCCost = info.TeleportToNPCCost;
        }

        private void NewMapInfo(S.NewMapInfo info)
        {
            Ui.MapInfoReceived(info);
        }

        private void SearchMapResult(S.SearchMapResult info)
        {
            Ui.SearchMapResult(info);
        }
        private void UserInformation(S.UserInformation p)
        {
            User = new UserObject(p.ObjectID);
            User.Load(p);
            Ui.UserLoaded();
            HasHero = p.HasHero;
            Ui.HeroBehaviourChanged(p.HeroBehaviour);
            Gold = p.Gold;
            Credit = p.Credit;

            Ui.UserInformationReceived();
            AllowObserve = p.AllowObserve;
            Observing = p.Observer;
        }
        private void UserSlotsRefresh(S.UserSlotsRefresh p)
        {
            User.SetSlots(p);
        }

        private void UserLocation(S.UserLocation p)
        {
            MapControl.NextAction = 0;
            if (User.CurrentLocation == p.Location && User.Direction == p.Direction) return;

            MapControl.RemoveObject(User);
            User.CurrentLocation = p.Location;
            User.MapLocation = p.Location;
            MapControl.AddObject(User);

            MapControl.FloorValid = false;
            MapControl.TextureValid = false;
            MapControl.InputDelay = CMain.Time + 400;

            if (User.Dead) return;

            User.ClearMagic();
            User.QueuedAction = null;

            for (int i = User.ActionFeed.Count - 1; i >= 0; i--)
            {
                if (User.ActionFeed[i].Action == MirAction.Pushed) continue;
                User.ActionFeed.RemoveAt(i);
            }

            User.SetAction();
        }
        private void ReceiveChat(S.Chat p)
        {
            Models.Chat.Add(p.Message, p.Type);
        }
        private void ObjectPlayer(S.ObjectPlayer p)
        {
            PlayerObject player = new PlayerObject(p.ObjectID);
            player.Load(p);
        }

        private void ObjectHero(S.ObjectHero p)
        {
            HeroObject hero = new HeroObject(p.ObjectID);
            hero.Load(p);

            if (p.ObjectID == Hero?.ObjectID)
                HeroObject = hero;
        }

        private void ObjectRemove(S.ObjectRemove p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out MapObject ob))
                ob.Remove();
        }
        /// <summary>P9-08: a server-side batch of turn/walk/run updates; each is applied in order exactly as its single packet.</summary>
        private void ObjectMoveBatch(S.ObjectMoveBatch p)
        {
            for (int i = 0; i < p.Moves.Count; i++)
            {
                var m = p.Moves[i];
                switch (m.Kind)
                {
                    case S.ObjectMoveBatch.Turn:
                        ObjectTurn(new S.ObjectTurn { ObjectID = m.ObjectID, Location = m.Location, Direction = m.Direction });
                        break;
                    case S.ObjectMoveBatch.Walk:
                        ObjectWalk(new S.ObjectWalk { ObjectID = m.ObjectID, Location = m.Location, Direction = m.Direction });
                        break;
                    case S.ObjectMoveBatch.Run:
                        ObjectRun(new S.ObjectRun { ObjectID = m.ObjectID, Location = m.Location, Direction = m.Direction });
                        break;
                }
            }
        }

        private void ObjectTurn(S.ObjectTurn p)
        {
            if (p.ObjectID == User.ObjectID && !Observing) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out MapObject ob))
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Standing, Direction = p.Direction, Location = p.Location });
        }

        private void ObjectWalk(S.ObjectWalk p)
        {
            if (p.ObjectID == User.ObjectID && !Observing) return;

            if (p.ObjectID == Hero?.ObjectID)
                Hero.CurrentLocation = p.Location;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out MapObject ob))
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Walking, Direction = p.Direction, Location = p.Location });
        }

        private void ObjectRun(S.ObjectRun p)
        {
            if (p.ObjectID == User.ObjectID && !Observing) return;

            if (p.ObjectID == Hero?.ObjectID)
                Hero.CurrentLocation = p.Location;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out MapObject ob))
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Running, Direction = p.Direction, Location = p.Location });
        }

        private void ObjectChat(S.ObjectChat p)
        {
            Models.Chat.Add(p.Text, p.Type);

            if (MapControl.Objects.TryGetValue(p.ObjectID, out MapObject ob))
                ob.Chat(RegexFunctions.CleanChatString(p.Text));
        }

        private void NewMonsterInfo(S.NewMonsterInfo info)
        {
            GameScene.MonsterInfoList.RemoveAll(x => x.Index == info.Info.Index);
            GameScene.MonsterInfoList.Add(info.Info);
            GameScene.OnMonsterInfoReceived(info.Info.Index);
        }

        private void NewNPCInfo(S.NewNPCInfo info)
        {
            GameScene.NPCInfoList.RemoveAll(x => x.Index == info.Info.Index);
            GameScene.NPCInfoList.Add(info.Info);
            GameScene.OnNPCInfoReceived(info.Info.Index);
        }

        private void MoveItem(S.MoveItem p)
        {
            ItemSlotRef toCell, fromCell;

            switch (p.Grid)
            {
                case MirGridType.Inventory:
                case MirGridType.Storage:
                case MirGridType.Trade:
                case MirGridType.Refine:
                case MirGridType.HeroInventory:
                    fromCell = ItemMoves.Slot(p.Grid, p.From);
                    toCell = ItemMoves.Slot(p.Grid, p.To);
                    break;
                default:
                    return;
            }

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (p.Grid == MirGridType.Trade)
                Ui.ItemTradeUnlocked();

            if (!p.Success) return;

            UserItem i = fromCell.Item;
            fromCell.Item = toCell.Item;
            toCell.Item = i;

            User.RefreshStats();
            Ui.ItemDuraRefresh();
        }
        private void EquipItem(S.EquipItem p)
        {
            ItemSlotRef fromCell, toCell;

            switch (p.Grid)
            {
                case MirGridType.HeroInventory:
                    toCell = ItemMoves.Slot(MirGridType.HeroEquipment, p.To);
                    break;
                default:
                    toCell = ItemMoves.Slot(MirGridType.Equipment, p.To);
                    break;
            }

            switch (p.Grid)
            {
                case MirGridType.Inventory:
                    fromCell = ItemMoves.Find(MirGridType.Inventory, p.UniqueID);
                    break;
                case MirGridType.Storage:
                    fromCell = ItemMoves.Find(MirGridType.Storage, p.UniqueID) ?? ItemMoves.FindBelt(p.UniqueID);
                    break;
                case MirGridType.HeroInventory:
                    fromCell = ItemMoves.Find(MirGridType.HeroInventory, p.UniqueID);
                    break;
                default:
                    return;
            }

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success) return;

            UserItem i = fromCell.Item;
            fromCell.Item = toCell.Item;
            toCell.Item = i;
            Ui.ItemDuraUpdate(i);
            if (p.Grid == MirGridType.HeroInventory)
                Hero.RefreshStats();
            else
                User.RefreshStats();
        }
        private void EquipSlotItem(S.EquipSlotItem p)
        {
            ItemSlotRef fromCell;
            ItemSlotRef toCell;

            switch (p.GridTo)
            {
                case MirGridType.Socket:
                case MirGridType.Mount:
                case MirGridType.Fishing:
                    // No range check: the classic cells were a fixed grid, so a reply for a slot past the current item's
                    // array (the socketed item changed meanwhile) still found a cell; SetItem ignores the out-of-range write.
                    toCell = new ItemSlotRef(p.GridTo, p.To);
                    break;
                default:
                    return;
            }

            switch (p.Grid)
            {
                case MirGridType.Inventory:
                    fromCell = ItemMoves.Find(MirGridType.Inventory, p.UniqueID);
                    break;
                case MirGridType.Storage:
                    fromCell = ItemMoves.Find(MirGridType.Storage, p.UniqueID) ?? ItemMoves.FindBelt(p.UniqueID);
                    break;
                default:
                    return;
            }

            //if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success) return;

            UserItem i = fromCell.Item;
            fromCell.Item = null;
            toCell.Item = i;
            User.RefreshStats();
        }

        private void CombineItem(S.CombineItem p)
        {
            ItemSlotRef fromCell = null;
            ItemSlotRef toCell = null;
            switch (p.Grid)
            {
                case MirGridType.Inventory:
                case MirGridType.HeroInventory:
                    fromCell = ItemMoves.Find(p.Grid, p.IDFrom);
                    toCell = ItemMoves.Find(p.Grid, p.IDTo);
                    break;
            }

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (p.Destroy) toCell.Item = null;

            if (!p.Success) return;

            if (fromCell.Item.Count > 1)
                fromCell.Item.Count--;
            else
                fromCell.Item = null;

            switch (p.Grid)
            {
                case MirGridType.Inventory:
                    User.RefreshStats();
                    break;
                case MirGridType.HeroInventory:
                    Hero.RefreshStats();
                    break;
            }
        }

        private void MergeItem(S.MergeItem p)
        {
            ItemSlotRef toCell, fromCell;

            switch (p.GridFrom)
            {
                case MirGridType.Inventory:
                case MirGridType.Storage:
                case MirGridType.Equipment:
                case MirGridType.Trade:
                case MirGridType.Fishing:
                case MirGridType.HeroEquipment:
                case MirGridType.HeroInventory:
                    fromCell = ItemMoves.Find(p.GridFrom, p.IDFrom);
                    break;
                default:
                    return;
            }

            switch (p.GridTo)
            {
                case MirGridType.Inventory:
                case MirGridType.Storage:
                case MirGridType.Equipment:
                case MirGridType.Trade:
                case MirGridType.Fishing:
                case MirGridType.HeroEquipment:
                case MirGridType.HeroInventory:
                    toCell = ItemMoves.Find(p.GridTo, p.IDTo);
                    break;
                default:
                    return;
            }

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (p.GridFrom == MirGridType.Trade || p.GridTo == MirGridType.Trade)
                Ui.ItemTradeUnlocked();

            if (!p.Success) return;
            if (fromCell.Item.Count <= toCell.Item.Info.StackSize - toCell.Item.Count)
            {
                toCell.Item.Count += fromCell.Item.Count;
                fromCell.Item = null;
            }
            else
            {
                fromCell.Item.Count -= (ushort)(toCell.Item.Info.StackSize - toCell.Item.Count);
                toCell.Item.Count = toCell.Item.Info.StackSize;
            }

            User.RefreshStats();
        }
        private void RemoveItem(S.RemoveItem p)
        {
            ItemSlotRef toCell;

            int index = -1;
            ItemSlotRef fromCell = null;
            for (int i = 0; i < MapObject.User.Equipment.Length; i++)
            {
                if (MapObject.User.Equipment[i] == null || MapObject.User.Equipment[i].UniqueID != p.UniqueID) continue;
                index = i;
                fromCell = ItemMoves.Slot(MirGridType.Equipment, index);
                break;
            }

            if (index == -1 && Hero != null)
            {
                for (int i = 0; i < MapObject.Hero.Equipment.Length; i++)
                {
                    if (MapObject.Hero.Equipment[i] == null || MapObject.Hero.Equipment[i].UniqueID != p.UniqueID) continue;
                    index = i;
                    fromCell = ItemMoves.Slot(MirGridType.HeroEquipment, index);
                    break;
                }
            }

            switch (p.Grid)
            {
                case MirGridType.Inventory:
                case MirGridType.Storage:
                case MirGridType.HeroInventory:
                    toCell = ItemMoves.Slot(p.Grid, p.To);
                    break;
                default:
                    return;
            }

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success) return;
            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            Ui.ItemDuraRefresh();
            if (p.Grid == MirGridType.HeroInventory)
                Hero.RefreshStats();
            else
                User.RefreshStats();

        }
        private void RemoveSlotItem(S.RemoveSlotItem p)
        {
            ItemSlotRef fromCell;
            ItemSlotRef toCell;

            switch (p.Grid)
            {
                case MirGridType.Socket:
                case MirGridType.Mount:
                case MirGridType.Fishing:
                    fromCell = ItemMoves.Find(p.Grid, p.UniqueID);
                    break;
                default:
                    return;
            }

            switch (p.GridTo)
            {
                case MirGridType.Inventory:
                case MirGridType.Storage:
                    toCell = ItemMoves.Slot(p.GridTo, p.To);
                    break;
                default:
                    return;
            }

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success) return;
            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            Ui.ItemDuraRefresh();
            User.RefreshStats();
        }
        private void TakeBackItem(S.TakeBackItem p)
        {
            ItemSlotRef fromCell = ItemMoves.Slot(MirGridType.Storage, p.From);

            ItemSlotRef toCell = ItemMoves.Slot(MirGridType.Inventory, p.To);

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success) return;
            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            User.RefreshStats();
            Ui.ItemDuraRefresh();
        }
        private void StoreItem(S.StoreItem p)
        {
            ItemSlotRef fromCell = ItemMoves.Slot(MirGridType.Inventory, p.From);

            ItemSlotRef toCell = ItemMoves.Slot(MirGridType.Storage, p.To);

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success) return;
            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            User.RefreshStats();
        }
        private void DepositRefineItem(S.DepositRefineItem p)
        {
            ItemSlotRef fromCell = ItemMoves.Slot(MirGridType.Inventory, p.From);

            ItemSlotRef toCell = ItemMoves.Slot(MirGridType.Refine, p.To);

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success) return;
            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            User.RefreshStats();
        }

        private void RetrieveRefineItem(S.RetrieveRefineItem p)
        {
            ItemSlotRef fromCell = ItemMoves.Slot(MirGridType.Refine, p.From);
            ItemSlotRef toCell = ItemMoves.Slot(MirGridType.Inventory, p.To);

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success) return;
            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            User.RefreshStats();
        }

        private void RefineCancel(S.RefineCancel p)
        {
            RefineReset();
        }

        private void RefineItem(S.RefineItem p)
        {
            RefineReset();
            for (int i = 0; i < User.Inventory.Length; i++)
            {
                if (User.Inventory[i] != null && User.Inventory[i].UniqueID == p.UniqueID)
                {
                    User.Inventory[i] = null;
                    break;
                }
            }
            Ui.CloseNpcDialog();
        }

        /// <summary>Empties the refine slots (was RefineDialog.RefineReset: its 4 x 4 grid shows GameScene.Refine slot for slot).</summary>
        private static void RefineReset()
        {
            for (int i = 0; i < Refine.Length; i++)
                ItemMoves.SetItem(MirGridType.Refine, i, null);
        }
        private void DepositTradeItem(S.DepositTradeItem p)
        {
            ItemSlotRef fromCell = ItemMoves.Slot(MirGridType.Inventory, p.From);

            ItemSlotRef toCell = ItemMoves.Slot(MirGridType.Trade, p.To);

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;
            Ui.ItemTradeUnlocked();

            if (!p.Success) return;
            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            User.RefreshStats();
        }
        private void RetrieveTradeItem(S.RetrieveTradeItem p)
        {
            ItemSlotRef fromCell = ItemMoves.Slot(MirGridType.Trade, p.From);
            ItemSlotRef toCell = ItemMoves.Slot(MirGridType.Inventory, p.To);

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;
            Ui.ItemTradeUnlocked();

            if (!p.Success) return;
            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            User.RefreshStats();
        }
        private void SplitItem(S.SplitItem p)
        {
            Bind(p.Item);

            UserItem[] array;
            switch (p.Grid)
            {
                case MirGridType.Inventory:
                    array = MapObject.User.Inventory;
                    break;
                case MirGridType.Storage:
                    array = Storage;
                    break;
                default:
                    return;
            }

            if (p.Grid == MirGridType.Inventory && (p.Item.Info.Type == ItemType.Potion || p.Item.Info.Type == ItemType.Scroll || p.Item.Info.Type == ItemType.Amulet || (p.Item.Info.Type == ItemType.Script && p.Item.Info.Effect == 1)))
            {
                if (p.Item.Info.Type == ItemType.Potion || p.Item.Info.Type == ItemType.Scroll || (p.Item.Info.Type == ItemType.Script && p.Item.Info.Effect == 1))
                {
                    for (int i = 0; i < UserObject.PotionBeltMaximum; i++)
                    {
                        if (array[i] != null) continue;
                        array[i] = p.Item;
                        User.RefreshStats();
                        return;
                    }
                }
                else if (p.Item.Info.Type == ItemType.Amulet)
                {
                    for (int i = UserObject.AmuletBeltMinimum; i < UserObject.AmuletBeltMaximum; i++)
                    {
                        if (array[i] != null) continue;
                        array[i] = p.Item;
                        User.RefreshStats();
                        return;
                    }
                }
            }

            for (int i = GameScene.User.BeltIdx; i < array.Length; i++)
            {
                if (array[i] != null) continue;
                array[i] = p.Item;
                User.RefreshStats();
                return;
            }

            for (int i = 0; i < GameScene.User.BeltIdx; i++)
            {
                if (array[i] != null) continue;
                array[i] = p.Item;
                User.RefreshStats();
                return;
            }
        }

        private void SplitItem1(S.SplitItem1 p)
        {
            ItemSlotRef cell;

            switch (p.Grid)
            {
                case MirGridType.Inventory:
                case MirGridType.Storage:
                    cell = ItemMoves.Find(p.Grid, p.UniqueID);
                    break;
                default:
                    return;
            }

            if (cell == null) return;

            cell.Locked = false;

            if (!p.Success) return;
            cell.Item.Count -= p.Count;
            User.RefreshStats();
        }
        private void UseItem(S.UseItem p)
        {
            ItemSlotRef cell = null;
            bool hero = false;

            switch (p.Grid)
            {
                case MirGridType.Inventory:
                    cell = ItemMoves.Find(MirGridType.Inventory, p.UniqueID);
                    break;
                case MirGridType.HeroInventory:
                    cell = ItemMoves.Find(MirGridType.HeroInventory, p.UniqueID);
                    hero = true;
                    break;
            }

            if (cell == null) return;

            cell.Locked = false;

            if (!p.Success) return;
            if (cell.Item.Count > 1) cell.Item.Count--;
            else cell.Item = null;
            if (hero)
                Hero.RefreshStats();
            else
                User.RefreshStats();
        }
        private void DropItem(S.DropItem p)
        {
            ItemSlotRef cell;
            if (p.HeroItem)
            {
                cell = ItemMoves.Find(MirGridType.HeroInventory, p.UniqueID);
            }
            else
            {
                cell = ItemMoves.Find(MirGridType.Inventory, p.UniqueID);
            }


            if (cell == null) return;

            cell.Locked = false;

            if (!p.Success) return;

            if (p.Count == cell.Item.Count)
                cell.Item = null;
            else
                cell.Item.Count -= p.Count;

            if (p.HeroItem)
            {
                Hero.RefreshStats();
            }
            else
            {
                User.RefreshStats();
            }

        }

        private void TakeBackHeroItem(S.TakeBackHeroItem p)
        {
            ItemSlotRef fromCell = ItemMoves.Slot(MirGridType.HeroInventory, p.From);

            ItemSlotRef toCell = ItemMoves.Slot(MirGridType.Inventory, p.To);

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success) return;
            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            User.RefreshStats();
            Hero.RefreshStats();
            Ui.ItemDuraRefresh();
        }

        private void TransferHeroItem(S.TransferHeroItem p)
        {
            ItemSlotRef fromCell = ItemMoves.Slot(MirGridType.Inventory, p.From);

            ItemSlotRef toCell = ItemMoves.Slot(MirGridType.HeroInventory, p.To);

            if (toCell == null || fromCell == null) return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success) return;
            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            User.RefreshStats();
            Hero.RefreshStats();
            Ui.ItemDuraRefresh();
        }

        private void MountUpdate(S.MountUpdate p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out MapObject ob))
            {
                if (ob is PlayerObject player)
                    player.MountUpdate(p);
            }

            if (p.ObjectID != User.ObjectID) return;

            CanRun = false;

            User.RefreshStats();

            Ui.MountChanged();
            Redraw();
        }

        private void TransformUpdate(S.TransformUpdate p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out MapObject ob))
                if (ob is PlayerObject player)
                    player.TransformType = p.TransformType;
        }

        private void FishingUpdate(S.FishingUpdate p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob) && ob is PlayerObject player)
                player.FishingUpdate(p);

            if (p.ObjectID != User.ObjectID) return;

            Ui.FishingUpdated(p);

            Redraw();
        }

        private void PlayerUpdate(S.PlayerUpdate p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob) && ob is PlayerObject player)
                player.Update(p);
        }

        private void PlayerInspect(S.PlayerInspect p)
        {
            Models.Inspect.Items = p.Equipment;

            Models.Inspect.Name = p.Name;
            Models.Inspect.GuildName = p.GuildName;
            Models.Inspect.GuildRank = p.GuildRank;
            Models.Inspect.Class = p.Class;
            Models.Inspect.Gender = p.Gender;
            Models.Inspect.Hair = p.Hair;
            Models.Inspect.Level = p.Level;
            Models.Inspect.LoverName = p.LoverName;
            Models.Inspect.AllowObserve = p.AllowObserve;
            Models.Inspect.Changed.Emit();

            Ui.PlayerInspected(p.IsHero);
        }
        private void LogOutSuccess(S.LogOutSuccess p)
        {
            for (int i = 0; i <= 3; i++)//Fix for orbs sound
                SoundManager.StopSound(20000 + 126 * 10 + 5 + i);

            User = null;
            if (Settings.Resolution != 1024)
            {
                CMain.SetResolution(1024, 768);
            }

            ActiveScene = Modern.Screens.AccountScenes.Select(p.Characters);

            Dispose();
        }
        private void LogOutFailed(S.LogOutFailed p)
        {
            Enabled = true;
        }

        private void ReturnToLogin(S.ReturnToLogin p)
        {
            User = null;
            if (Settings.Resolution != 1024)
                CMain.SetResolution(1024, 768);

            ActiveScene = Modern.Screens.AccountScenes.Login();
            Dispose();
            Modern.Screens.AccountScenes.Notice(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ObservedPlayerLoggedOff));
        }

        private void TimeOfDay(S.TimeOfDay p)
        {
            Lights = p.Lights;
            Ui.LightsChanged();
        }
        private void ChangeAMode(S.ChangeAMode p)
        {
            AMode = p.Mode;

            switch (p.Mode)
            {
                case AttackMode.Peace:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AttackMode_Peace), ChatType.Hint);
                    break;
                case AttackMode.Group:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AttackMode_Group), ChatType.Hint);
                    break;
                case AttackMode.Guild:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AttackMode_Guild), ChatType.Hint);
                    break;
                case AttackMode.EnemyGuild:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AttackMode_EnemyGuild), ChatType.Hint);
                    break;
                case AttackMode.RedBrown:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AttackMode_RedBrown), ChatType.Hint);
                    break;
                case AttackMode.All:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AttackMode_All), ChatType.Hint);
                    break;
            }
        }
        private void ChangePMode(S.ChangePMode p)
        {
            PMode = p.Mode;
            switch (p.Mode)
            {
                case PetMode.Both:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PetMode_Both), ChatType.Hint);
                    break;
                case PetMode.MoveOnly:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PetMode_MoveOnly), ChatType.Hint);
                    break;
                case PetMode.AttackOnly:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PetMode_AttackOnly), ChatType.Hint);
                    break;
                case PetMode.None:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PetMode_None), ChatType.Hint);
                    break;
                case PetMode.FocusMasterTarget:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PetMode_FocusMasterTarget), ChatType.Hint);
                    break;
            }
        }

        private void ObjectItem(S.ObjectItem p)
        {
            ItemObject ob = new ItemObject(p.ObjectID);
            ob.Load(p);
            /*
            string[] Warnings = new string[] {"HeroNecklace","AdamantineNecklace","8TrigramWheel","HangMaWheel","BaekTaGlove","SpiritReformer","BokMaWheel","BoundlessRing","ThunderRing","TaeGukRing","OmaSpiritRing","NobleRing"};
            if (Warnings.Contains(p.Name))
            {
                Models.Chat.Add(string.Format("{0} at {1}", p.Name, p.Location), ChatType.Hint);
            }
            */
        }
        private void ObjectGold(S.ObjectGold p)
        {
            ItemObject ob = new ItemObject(p.ObjectID);
            ob.Load(p);
        }
        private void GainedItem(S.GainedItem p)
        {
            Bind(p.Item);
            AddItem(p.Item);
            User.RefreshStats();

            OutputMessage(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.YouGainedItem), p.Item.FriendlyName));
        }
        private void GainedQuestItem(S.GainedQuestItem p)
        {
            Bind(p.Item);
            AddQuestItem(p.Item);
        }

        private void GainedGold(S.GainedGold p)
        {
            if (p.Gold == 0) return;

            Gold += p.Gold;
            SoundManager.PlaySound(SoundList.Gold);
            OutputMessage(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.YouGainedGold), p.Gold, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.Gold)));
        }
        private void LoseGold(S.LoseGold p)
        {
            Gold -= p.Gold;
            SoundManager.PlaySound(SoundList.Gold);
        }
        private void GainedCredit(S.GainedCredit p)
        {
            if (p.Credit == 0) return;

            Credit += p.Credit;
            SoundManager.PlaySound(SoundList.Gold);
            OutputMessage(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.YouGainedGold), p.Credit, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.Credit)));
        }
        private void LoseCredit(S.LoseCredit p)
        {
            Credit -= p.Credit;
            SoundManager.PlaySound(SoundList.Gold);
        }
        private void ObjectMonster(S.ObjectMonster p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob) && ob is MonsterObject mob)
            {
                mob.Load(p, true);
                return;
            }

            mob = new MonsterObject(p.ObjectID);
            mob.Load(p, false);
        }

        private void ObjectAttack(S.ObjectAttack p)
        {
            if (p.ObjectID == User.ObjectID && !Observing) return;

            QueuedAction action = null;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                if (ob.Race == ObjectType.Player)
                {
                    action = new QueuedAction { Action = MirAction.Attack1, Direction = p.Direction, Location = p.Location, Params = new List<object>() };
                }
                else
                {
                    switch (p.Type)
                    {
                        default:
                            {
                                action = new QueuedAction { Action = MirAction.Attack1, Direction = p.Direction, Location = p.Location, Params = new List<object>() };
                                break;
                            }
                        case 1:
                            {
                                action = new QueuedAction { Action = MirAction.Attack2, Direction = p.Direction, Location = p.Location, Params = new List<object>() };
                                break;
                            }
                        case 2:
                            {
                                action = new QueuedAction { Action = MirAction.Attack3, Direction = p.Direction, Location = p.Location, Params = new List<object>() };
                                break;
                            }
                        case 3:
                            {
                                action = new QueuedAction { Action = MirAction.Attack4, Direction = p.Direction, Location = p.Location, Params = new List<object>() };
                                break;
                            }
                        case 4:
                            {
                                action = new QueuedAction { Action = MirAction.Attack5, Direction = p.Direction, Location = p.Location, Params = new List<object>() };
                                break;
                            }
                    }
                }
                action.Params.Add(p.Spell);
                action.Params.Add(p.Level);
                ob.ActionFeed.Add(action);
            }
        }
        private void Struck(S.Struck p)
        {
            LogTime = CMain.Time + Globals.LogDelay;

            NextRunTime = CMain.Time + 2500;
            User.BlizzardStopTime = 0;
            User.ClearMagic();
            if (User.ReincarnationStopTime > CMain.Time)
                Network.Enqueue(new C.CancelReincarnation { });

            MirDirection dir = User.Direction;
            Point location = User.CurrentLocation;

            for (int i = 0; i < User.ActionFeed.Count; i++)
                if (User.ActionFeed[i].Action == MirAction.Struck) return;


            if (User.ActionFeed.Count > 0)
            {
                dir = User.ActionFeed[User.ActionFeed.Count - 1].Direction;
                location = User.ActionFeed[User.ActionFeed.Count - 1].Location;
            }

            if (User.Buffs.Any(a => a == BuffType.EnergyShield))
            {
                for (int j = 0; j < User.Effects.Count; j++)
                {
                    BuffEffect effect = null;
                    effect = User.Effects[j] as BuffEffect;

                    if (effect != null && effect.BuffType == BuffType.EnergyShield)
                    {
                        effect.Clear();
                        effect.Remove();

                        User.Effects.Add(effect = new BuffEffect(Libraries.Magic2, 1890, 6, 600, User, true, BuffType.EnergyShield) { Repeat = false });
                        SoundManager.PlaySound(20000 + (ushort)Spell.EnergyShield * 10 + 1);

                        effect.Complete += (o, e) =>
                        {
                            User.Effects.Add(new BuffEffect(Libraries.Magic2, 1900, 2, 800, User, true, BuffType.EnergyShield) { Repeat = true });
                        };


                        break;
                    }
                }
            }

            QueuedAction action = new QueuedAction
            {
                Action = MirAction.Struck,
                Direction = dir,
                Location = location,
                Params = new List<object>()
            };
            action.Params.Add(p.AttackerID);

            int weapon = -2;
            if (MapControl.Objects.TryGetValue(p.AttackerID, out MapObject attacker) &&
                attacker.Race == ObjectType.Player)
            {
                PlayerObject player = (PlayerObject)attacker;
                weapon = player.Weapon;
                if (player.Class == MirClass.Assassin && weapon > -1)
                    weapon = 1;
            }
            action.Params.Add(weapon);
            User.ActionFeed.Add(action);

        }
        private void ObjectStruck(S.ObjectStruck p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                if (ob.SkipFrames) return;
                if (ob.ActionFeed.Count > 0 && ob.ActionFeed[ob.ActionFeed.Count - 1].Action == MirAction.Struck) return;

                if (ob.Race == ObjectType.Player)
                    ((PlayerObject)ob).BlizzardStopTime = 0;
                QueuedAction action = new QueuedAction
                {
                    Action = MirAction.Struck,
                    Direction = p.Direction,
                    Location = p.Location,
                    Params = new List<object>()
                };
                action.Params.Add(p.AttackerID);

                int weapon = -2;
                if (MapControl.Objects.TryGetValue(p.AttackerID, out MapObject attacker) &&
                    attacker.Race == ObjectType.Player)
                {
                    PlayerObject player = (PlayerObject)attacker;
                    weapon = player.Weapon;
                    if (player.Class == MirClass.Assassin && weapon > -1)
                        weapon = 1;
                }
                action.Params.Add(weapon);
                ob.ActionFeed.Add(action);

                if (ob.Buffs.Any(a => a == BuffType.EnergyShield))
                {
                    for (int j = 0; j < ob.Effects.Count; j++)
                    {
                        BuffEffect effect = null;
                        effect = ob.Effects[j] as BuffEffect;

                        if (effect != null && effect.BuffType == BuffType.EnergyShield)
                        {
                            effect.Clear();
                            effect.Remove();

                            ob.Effects.Add(effect = new BuffEffect(Libraries.Magic2, 1890, 6, 600, ob, true, BuffType.EnergyShield) { Repeat = false });
                            SoundManager.PlaySound(20000 + (ushort)Spell.EnergyShield * 10 + 1);

                            effect.Complete += (o, e) =>
                            {
                                ob.Effects.Add(new BuffEffect(Libraries.Magic2, 1900, 2, 800, ob, true, BuffType.EnergyShield) { Repeat = true });
                            };

                            break;
                        }
                    }
                }
            }
        }

        private void DamageIndicator(S.DamageIndicator p)
        {
            if (Settings.DisplayDamage)
            {
                if (MapControl.Objects.TryGetValue(p.ObjectID, out var obj))
                {
                    if (obj.Damages.Count >= 10) return;

                    switch (p.Type)
                    {
                        case DamageType.Hit: //add damage level colours
                            obj.Damages.Add(new Damage(p.Damage.ToString("#,##0"), 1000, obj.Race == ObjectType.Player ? Color.Red : Color.White, 50));
                            break;
                        case DamageType.Miss:
                            obj.Damages.Add(new Damage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.Miss), 1200, obj.Race == ObjectType.Player ? Color.LightCoral : Color.LightGray, 50));
                            break;
                        case DamageType.Critical:
                            obj.Damages.Add(
                                new Damage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.Crit), 1000, obj.Race == ObjectType.Player ? Color.DarkRed : Color.DarkRed, 50) { Offset = 15 });
                            break;
                    }
                    obj.InvalidateMap();
                }
            }
        }

        private void DuraChanged(S.DuraChanged p)
        {
            UserItem item = null;
            for (int i = 0; i < User.Inventory.Length; i++)
                if (User.Inventory[i] != null && User.Inventory[i].UniqueID == p.UniqueID)
                {
                    item = User.Inventory[i];
                    break;
                }


            if (item == null)
                for (int i = 0; i < User.Equipment.Length; i++)
                {
                    if (User.Equipment[i] != null && User.Equipment[i].UniqueID == p.UniqueID)
                    {
                        item = User.Equipment[i];
                        break;
                    }
                    if (User.Equipment[i] != null && User.Equipment[i].Slots != null)
                    {
                        for (int j = 0; j < User.Equipment[i].Slots.Length; j++)
                        {
                            if (User.Equipment[i].Slots[j] != null && User.Equipment[i].Slots[j].UniqueID == p.UniqueID)
                            {
                                item = User.Equipment[i].Slots[j];
                                break;
                            }
                        }

                        if (item != null) break;
                    }
                }

            if (item == null) return;

            item.CurrentDura = p.CurrentDura;

            if (item.CurrentDura == 0)
            {
                User.RefreshStats();
                switch (item.Info.Type)
                {
                    case ItemType.Mount:
                        Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.FriendlyNoLongerLoyal), item.Info.FriendlyName), ChatType.System);
                        break;
                    default:
                        Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.FriendlyDuraDroppedToZero), item.Info.FriendlyName), ChatType.System);
                        break;
                }

            }

            Ui.UserItemDuraChanged(item);
        }
        private void HealthChanged(S.HealthChanged p)
        {
            User.HP = p.HP;
            User.MP = p.MP;

            User.PercentHealth = (byte)(User.HP / (float)User.Stats[Stat.HP] * 100);
        }
        private void HeroHealthChanged(S.HeroHealthChanged p)
        {
            Hero.HP = p.HP;
            Hero.MP = p.MP;

            Hero.PercentHealth = (byte)(Hero.HP / (float)Hero.Stats[Stat.HP] * 100);
            Hero.PercentMana = (byte)(Hero.MP / (float)Hero.Stats[Stat.MP] * 100);
        }

        private void DeleteQuestItem(S.DeleteQuestItem p)
        {
            for (int i = 0; i < User.QuestInventory.Length; i++)
            {
                UserItem item = User.QuestInventory[i];

                if (item == null || item.UniqueID != p.UniqueID) continue;

                if (item.Count == p.Count)
                    User.QuestInventory[i] = null;
                else
                    item.Count -= p.Count;
                break;
            }
        }

        private void DeleteItem(S.DeleteItem p)
        {
            UserObject actor = null;
            for (int i = 0; i < User.Inventory.Length; i++)
            {
                if (actor != null) break;
                UserItem item = User.Inventory[i];

                if (item != null && item.Slots.Length > 0)
                {
                    for (int j = 0; j < item.Slots.Length; j++)
                    {
                        UserItem slotItem = item.Slots[j];

                        if (slotItem == null || slotItem.UniqueID != p.UniqueID) continue;

                        if (slotItem.Count == p.Count)
                            item.Slots[j] = null;
                        else
                            slotItem.Count -= p.Count;
                        actor = User;
                        break;
                    }
                }

                if (item == null || item.UniqueID != p.UniqueID) continue;

                if (item.Count == p.Count)
                    User.Inventory[i] = null;
                else
                    item.Count -= p.Count;
                actor = User;
            }

            if (actor == null)
            {
                for (int i = 0; i < User.Equipment.Length; i++)
                {
                    if (actor != null) break;
                    UserItem item = User.Equipment[i];

                    if (item != null && item.Slots.Length > 0)
                    {
                        for (int j = 0; j < item.Slots.Length; j++)
                        {
                            UserItem slotItem = item.Slots[j];

                            if (slotItem == null || slotItem.UniqueID != p.UniqueID) continue;

                            if (slotItem.Count == p.Count)
                                item.Slots[j] = null;
                            else
                                slotItem.Count -= p.Count;
                            actor = User;
                            break;
                        }
                    }

                    if (item == null || item.UniqueID != p.UniqueID) continue;

                    if (item.Count == p.Count)
                        User.Equipment[i] = null;
                    else
                        item.Count -= p.Count;
                    actor = User;
                }
            }

            if (Hero != null && actor == null)
            {
                for (int i = 0; i < Hero.Inventory.Length; i++)
                {
                    if (actor != null) break;
                    UserItem item = Hero.Inventory[i];

                    if (item != null && item.Slots.Length > 0)
                    {
                        for (int j = 0; j < item.Slots.Length; j++)
                        {
                            UserItem slotItem = item.Slots[j];

                            if (slotItem == null || slotItem.UniqueID != p.UniqueID) continue;

                            if (slotItem.Count == p.Count)
                                item.Slots[j] = null;
                            else
                                slotItem.Count -= p.Count;
                            actor = Hero;
                            break;
                        }
                    }

                    if (item == null || item.UniqueID != p.UniqueID) continue;

                    if (item.Count == p.Count)
                        User.Inventory[i] = null;
                    else
                        item.Count -= p.Count;
                    actor = Hero;
                }

                if (actor == null)
                {
                    for (int i = 0; i < Hero.Equipment.Length; i++)
                    {
                        if (actor != null) break;
                        UserItem item = Hero.Equipment[i];

                        if (item != null && item.Slots.Length > 0)
                        {
                            for (int j = 0; j < item.Slots.Length; j++)
                            {
                                UserItem slotItem = item.Slots[j];

                                if (slotItem == null || slotItem.UniqueID != p.UniqueID) continue;

                                if (slotItem.Count == p.Count)
                                    item.Slots[j] = null;
                                else
                                    slotItem.Count -= p.Count;
                                actor = Hero;
                                break;
                            }
                        }

                        if (item == null || item.UniqueID != p.UniqueID) continue;

                        if (item.Count == p.Count)
                            User.Equipment[i] = null;
                        else
                            item.Count -= p.Count;
                        actor = Hero;
                    }
                }
            }

            if (actor == null)
            {
                for (int i = 0; i < Storage.Length; i++)
                {
                    var item = Storage[i];
                    if (item == null || item.UniqueID != p.UniqueID) continue;

                    if (item.Count == p.Count)
                        Storage[i] = null;
                    else
                        item.Count -= p.Count;
                    break;
                }
            }
            actor?.RefreshStats();
        }
        private void Death(S.Death p)
        {
            User.Dead = true;

            User.ActionFeed.Add(new QueuedAction { Action = MirAction.Die, Direction = p.Direction, Location = p.Location });
            ShowReviveMessage = true;

            LogTime = 0;
        }
        private void ObjectDied(S.ObjectDied p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                switch (p.Type)
                {
                    default:
                        ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Die, Direction = p.Direction, Location = p.Location });
                        ob.Dead = true;
                        break;
                    case 1:
                        MapControl.Effects.Add(new Effect(Libraries.Magic2, 690, 10, 1000, ob.CurrentLocation));
                        ob.Remove();
                        break;
                    case 2:
                        SoundManager.PlaySound(20000 + (ushort)Spell.DarkBody * 10 + 1);
                        MapControl.Effects.Add(new Effect(Libraries.Magic2, 2600, 10, 1200, ob.CurrentLocation));
                        ob.Remove();
                        break;
                }
            }
        }
        private void ColourChanged(S.ColourChanged p)
        {
            User.NameColour = p.NameColour;
        }
        private void ObjectColourChanged(S.ObjectColourChanged p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                ob.NameColour = p.NameColour;
                ob.InvalidateMap();
            }
        }

        private void ObjectGuildNameChanged(S.ObjectGuildNameChanged p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob) && ob is PlayerObject obPlayer)
            {
                obPlayer.GuildName = p.GuildName;
                obPlayer.InvalidateMap();
            }
        }

        private void GainExperience(S.GainExperience p)
        {
            OutputMessage(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.ExperienceGained), p.Amount));
            MapObject.User.Experience += p.Amount;
        }

        private void GainHeroExperience(S.GainHeroExperience p)
        {
            OutputMessage(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.HeroExperienceGained), p.Amount));
            MapObject.Hero.Experience += p.Amount;
        }
        private void LevelChanged(S.LevelChanged p)
        {
            User.Level = p.Level;
            User.Experience = p.Experience;
            User.MaxExperience = p.MaxExperience;
            User.RefreshStats();
            OutputMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LevelUp));
            User.Effects.Add(new Effect(Libraries.Magic2, 1200, 20, 2000, User));
            SoundManager.PlaySound(SoundList.LevelUp);
            Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LevelUp), ChatType.LevelUp);
        }
        private void HeroLevelChanged(S.HeroLevelChanged p)
        {
            Hero.Level = p.Level;
            Hero.Experience = p.Experience;
            Hero.MaxExperience = p.MaxExperience;
            Hero.RefreshStats();
            Hero.Effects.Add(new Effect(Libraries.Magic2, 1200, 20, 2000, User));
            SoundManager.PlaySound(SoundList.LevelUp);
            Ui.HeroInfoChanged();
        }
        private void ObjectLeveled(S.ObjectLeveled p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                ob.Effects.Add(new Effect(Libraries.Magic2, 1180, 16, 2500, ob));
                SoundManager.PlaySound(SoundList.LevelUp);
            }
        }
        private void ObjectHarvest(S.ObjectHarvest p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Harvest, Direction = ob.Direction, Location = ob.CurrentLocation });
        }

        private void ObjectHarvested(S.ObjectHarvested p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Skeleton, Direction = ob.Direction, Location = ob.CurrentLocation });
        }

        private void ObjectNPC(S.ObjectNPC p)
        {
            NPCObject ob = new NPCObject(p.ObjectID);
            ob.Load(p);
        }
        private void NPCResponse(S.NPCResponse p)
        {
            NPCTime = 0;
            Models.Npc.SetPage(p.Page);
            Ui.NpcResponse(p);
        }

        private void NPCUpdate(S.NPCUpdate p)
        {
            GameScene.NPCID = p.NPCID; //Updates the client with the correct NPC ID if it's manually called from the client
        }

        private void NPCImageUpdate(S.NPCImageUpdate p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob) && ob is NPCObject npc && ob.Race == ObjectType.Merchant)
            {
                npc.Image = p.Image;
                npc.Colour = p.Colour;
                npc.LoadLibrary();
            }
        }

        private void DefaultNPC(S.DefaultNPC p)
        {
            GameScene.DefaultNPCID = p.ObjectID; //Updates the client with the correct Default NPC ID
        }


        private void ObjectHide(S.ObjectHide p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Hide, Direction = ob.Direction, Location = ob.CurrentLocation });
        }

        private void ObjectShow(S.ObjectShow p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Show, Direction = ob.Direction, Location = ob.CurrentLocation });
        }

        private void Poisoned(S.Poisoned p)
        {
            var previousPoisons = User.Poison;

            User.Poison = p.Poison;
            Models.Poisons.Trim(p.Poison);
            if (p.Poison.HasFlag(PoisonType.Stun) || p.Poison.HasFlag(PoisonType.Dazed) || p.Poison.HasFlag(PoisonType.Frozen) || p.Poison.HasFlag(PoisonType.Paralysis) || p.Poison.HasFlag(PoisonType.LRParalysis))
            {
                User.ClearMagic();
            }

            if (previousPoisons.HasFlag(PoisonType.Blindness) && !User.Poison.HasFlag(PoisonType.Blindness))
            {
                User.BlindCount = 0;
            }
        }

        private void ObjectPoisoned(S.ObjectPoisoned p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                ob.Poison = p.Poison;
                ob.InvalidateMap(); // poison dots
            }
        }

        /// <summary>LiveDb: the current map's light / dark light / weather changed (no reload; the light pass reads them every frame).</summary>
        private void MapAtmosphere(S.MapAtmosphere p)
        {
            MapControl.Lights = p.Lights;
            MapControl.MapDarkLight = p.MapDarkLight;
            if (MapControl.Weather != p.Weather)
            {
                MapControl.Weather = p.Weather;
                MapControl.UpdateWeather();
            }
            MapControl.TextureValid = false;
        }

        private void MapChanged(S.MapChanged p)
        {
            var isCurrentMap = (MapControl.Index == p.MapIndex);

            if (isCurrentMap)
                MapControl.ResetMap();
            else
            {
                MapControl.Index = p.MapIndex;
                MapControl.FileName = Path.Combine(Settings.MapPath, p.FileName + ".map");
                MapControl.Title = p.Title;
                MapControl.MiniMap = p.MiniMap;
                MapControl.BigMap = p.BigMap;
                MapControl.Lights = p.Lights;
                MapControl.MapDarkLight = p.MapDarkLight;
                MapControl.Music = p.Music;
                MapControl.Weather = p.Weather;
                MapControl.LoadMap();
            }

            MapControl.NextAction = 0;
            Scene.MapControl.AutoPath = false;
            Models.Quests.ClearNpcs(); // the objects are gone; the server re-sends S.QuestAvailability for the new view
            User.CurrentLocation = p.Location;
            User.MapLocation = p.Location;
            MapControl.AddObject(User);

            User.Direction = p.Direction;

            User.QueuedAction = null;
            User.ActionFeed.Clear();
            User.ClearMagic();
            User.SetAction();

            GameScene.CanRun = false;

            MapControl.FloorValid = false;
            MapControl.TextureValid = false;
            MapControl.InputDelay = CMain.Time + 400;

            MapControl.UpdateWeather();
            Ui.MapChanged(p.MapIndex);
        }
        private void ObjectTeleportOut(S.ObjectTeleportOut p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                Effect effect = null;

                bool playDefaultSound = true;

                switch (p.Type)
                {
                    case 1: //Yimoogi
                        {
                            effect = new Effect(Libraries.Magic2, 1300, 10, 500, ob.CurrentLocation);
                            break;
                        }
                    case 2: //RedFoxman
                        {
                            effect = new Effect(Libraries.Monsters[(ushort)Monster.RedFoxman], 243, 10, 500, ob.CurrentLocation);
                            break;
                        }
                    case 4: //MutatedManWorm
                        {
                            effect = new Effect(Libraries.Monsters[(ushort)Monster.MutatedManworm], 272, 6, 500, ob.CurrentLocation);

                            SoundManager.PlaySound(((ushort)Monster.MutatedManworm) * 10 + 7);
                            playDefaultSound = false;
                            break;
                        }
                    case 5: //WitchDoctor
                        {
                            effect = new Effect(Libraries.Monsters[(ushort)Monster.WitchDoctor], 328, 20, 1000, ob.CurrentLocation);
                            SoundManager.PlaySound(((ushort)Monster.WitchDoctor) * 10 + 7);
                            playDefaultSound = false;
                            break;
                        }
                    case 6: //TurtleKing
                        {
                            effect = new Effect(Libraries.Monsters[(ushort)Monster.TurtleKing], 946, 10, 500, ob.CurrentLocation);
                            break;
                        }
                    case 7: //Mandrill
                        {
                            effect = new Effect(Libraries.Monsters[(ushort)Monster.Mandrill], 280, 10, 1000, ob.CurrentLocation);
                            SoundManager.PlaySound(((ushort)Monster.Mandrill) * 10 + 6);
                            playDefaultSound = false;
                            break;
                        }
                    case 8: //DarkCaptain
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.DarkCaptain], 1224, 10, 1000, ob.CurrentLocation));
                            SoundManager.PlaySound(((ushort)Monster.DarkCaptain) * 10 + 8);
                            playDefaultSound = false;
                            break;
                        }
                    case 9: //Doe
                        {
                            effect = new Effect(Libraries.Monsters[(ushort)Monster.Doe], 208, 10, 1000, ob.CurrentLocation);
                            SoundManager.PlaySound(((ushort)Monster.Doe) * 10 + 7);
                            playDefaultSound = false;
                            break;
                        }
                    case 10: //HornedCommander
                        {
                            MapControl.Effects.Add(effect = new Effect(Libraries.Monsters[(ushort)Monster.HornedCommander], 928, 10, 1000, ob.CurrentLocation));
                            SoundManager.PlaySound(8455);
                            playDefaultSound = false;
                            break;
                        }
                    case 11: //SnowWolfKing
                        {
                            MapControl.Effects.Add(effect = new Effect(Libraries.Monsters[(ushort)Monster.SnowWolfKing], 561, 10, 1000, ob.CurrentLocation));
                            SoundManager.PlaySound(8455);
                            playDefaultSound = false;
                            break;
                        }
                    default:
                        {
                            effect = new Effect(Libraries.Magic, 250, 10, 500, ob.CurrentLocation);
                            break;
                        }
                }

                MapControl.Effects.Add(effect);

                if (playDefaultSound)
                    SoundManager.PlaySound(SoundList.Teleport);
            }
        }
        private void ObjectTeleportIn(S.ObjectTeleportIn p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                bool playDefaultSound = true;

                switch (p.Type)
                {
                    case 1: //Yimoogi
                        {
                            ob.Effects.Add(new Effect(Libraries.Magic2, 1310, 10, 500, ob));
                            break;
                        }
                    case 2: //RedFoxman
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.RedFoxman], 253, 10, 500, ob));
                            break;
                        }
                    case 4: //MutatedManWorm
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.MutatedManworm], 278, 7, 500, ob));
                            SoundManager.PlaySound(((ushort)Monster.MutatedManworm) * 10 + 7);
                            playDefaultSound = false;
                            break;
                        }
                    case 5: //WitchDoctor
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.WitchDoctor], 348, 20, 1000, ob));
                            SoundManager.PlaySound(((ushort)Monster.WitchDoctor) * 10 + 7);
                            playDefaultSound = false;
                            break;
                        }
                    case 6: //TurtleKing
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.TurtleKing], 956, 10, 500, ob));
                            break;
                        }
                    case 7: //Mandrill
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.Mandrill], 290, 10, 1000, ob));
                            SoundManager.PlaySound(((ushort)Monster.Mandrill) * 10 + 6);
                            playDefaultSound = false;
                            break;
                        }
                    case 8: //DarkCaptain
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.DarkCaptain], 1224, 10, 1000, ob));
                            SoundManager.PlaySound(((ushort)Monster.DarkCaptain) * 10 + 9);
                            playDefaultSound = false;
                            break;
                        }
                    case 9: //Doe
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.Doe], 208, 10, 1000, ob));
                            SoundManager.PlaySound(((ushort)Monster.Doe) * 10 + 7);
                            playDefaultSound = false;
                            break;
                        }
                    case 10: //HornedCommander
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.HornedCommander], 928, 10, 1000, ob));
                            SoundManager.PlaySound(8455);
                            playDefaultSound = false;
                            break;
                        }
                    case 11: //SnowWolfKing
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.SnowWolfKing], 571, 10, 1000, ob));
                            SoundManager.PlaySound(8455);
                            playDefaultSound = false;
                            break;
                        }
                    default:
                        {
                            ob.Effects.Add(new Effect(Libraries.Magic, 260, 10, 500, ob));
                            break;
                        }
                }

                if (p.ObjectID == User.ObjectID)
                    User.TargetID = User.LastTargetObjectId;

                if (playDefaultSound)
                    SoundManager.PlaySound(SoundList.Teleport);
            }
        }

        private void TeleportIn()
        {
            User.Effects.Add(new Effect(Libraries.Magic, 260, 10, 500, User));
            SoundManager.PlaySound(SoundList.Teleport);
        }
        private void NPCGoods(S.NPCGoods p)
        {
            for (int i = 0; i < p.List.Count; i++)
            {
                p.List[i].Info = GetItemInfo(p.List[i].ItemIndex);
            }

            NPCRate = p.Rate;
            NPCPanelType = p.Type;
            HideAddedStoreStats = p.HideAddedStats;

            Ui.NpcGoods(p);
        }
        private void NPCPearlGoods(S.NPCPearlGoods p)
        {
            for (int i = 0; i < p.List.Count; i++)
            {
                p.List[i].Info = GetItemInfo(p.List[i].ItemIndex);
            }

            NPCRate = p.Rate;
            NPCPanelType = p.Type;

            Ui.NpcPearlGoods(p);
        }

        private void NPCSell()
        {
            Ui.NpcDropPanel(PanelType.Sell);
        }
        private void NPCRepair(S.NPCRepair p)
        {
            NPCRate = p.Rate;
            Ui.NpcDropPanel(PanelType.Repair);
        }
        private void NPCStorage()
        {
            Ui.NpcStorage();
        }
        private void StorageUnlockResult(S.StorageUnlockResult p)
        {
            Ui.StorageUnlockResult(p);
        }
        private void StoragePasswordResult(S.StoragePasswordResult p)
        {
            Ui.StoragePasswordResult(p);
        }
        private void NPCRequestInput(S.NPCRequestInput p)
        {
            Ui.NpcRequestInput(p);
        }

        private void NPCSRepair(S.NPCSRepair p)
        {
            NPCRate = p.Rate;
            Ui.NpcDropPanel(PanelType.SpecialRepair);
        }

        private void NPCRefine(S.NPCRefine p)
        {
            NPCRate = p.Rate;
            Ui.NpcRefine(p.Refining);
        }

        private void NPCCheckRefine(S.NPCCheckRefine p)
        {
            Ui.NpcDropPanel(PanelType.CheckRefine);
        }

        private void NPCCollectRefine(S.NPCCollectRefine p)
        {
            Ui.NpcCollectRefine();
        }

        private void NPCReplaceWedRing(S.NPCReplaceWedRing p)
        {
            if (!Ui.IsNpcDialogOpen()) return;
            NPCRate = p.Rate;
            Ui.NpcDropPanel(PanelType.ReplaceWedRing);
        }


        private void SellItem(S.SellItem p)
        {
            ItemSlotRef cell = ItemMoves.Find(MirGridType.Inventory, p.UniqueID);

            if (cell == null) return;

            cell.Locked = false;

            if (!p.Success) return;

            if (p.Count == cell.Item.Count)
                cell.Item = null;
            else
                cell.Item.Count -= p.Count;

            User.RefreshStats();
        }
        private void RepairItem(S.RepairItem p)
        {
            ItemSlotRef cell = ItemMoves.Find(MirGridType.Inventory, p.UniqueID);

            if (cell == null) return;

            cell.Locked = false;
        }
        private void CraftItem(S.CraftItem p)
        {
            if (!p.Success) return;

            Ui.CraftItemDone();
            User.RefreshStats();
        }
        private void ItemRepaired(S.ItemRepaired p)
        {
            UserItem item = null;
            for (int i = 0; i < User.Inventory.Length; i++)
            {
                if (User.Inventory[i] != null && User.Inventory[i].UniqueID == p.UniqueID)
                {
                    item = User.Inventory[i];
                    break;
                }
            }

            if (item == null)
            {
                for (int i = 0; i < User.Equipment.Length; i++)
                {
                    if (User.Equipment[i] != null && User.Equipment[i].UniqueID == p.UniqueID)
                    {
                        item = User.Equipment[i];
                        break;
                    }
                }
            }

            if (Hero != null)
            {
                if (item == null)
                {
                    for (int i = 0; i < Hero.Inventory.Length; i++)
                    {
                        if (Hero.Inventory[i] != null && Hero.Inventory[i].UniqueID == p.UniqueID)
                        {
                            item = Hero.Inventory[i];
                            break;
                        }
                    }
                }

                if (item == null)
                {
                    for (int i = 0; i < Hero.Equipment.Length; i++)
                    {
                        if (Hero.Equipment[i] != null && Hero.Equipment[i].UniqueID == p.UniqueID)
                        {
                            item = Hero.Equipment[i];
                            break;
                        }
                    }
                }
            }

            if (item == null) return;

            item.MaxDura = p.MaxDura;
            item.CurrentDura = p.CurrentDura;

            Ui.ItemInfoChanged(item);
        }

        private void ItemSlotSizeChanged(S.ItemSlotSizeChanged p)
        {
            UserItem item = null;
            for (int i = 0; i < User.Inventory.Length; i++)
            {
                if (User.Inventory[i] != null && User.Inventory[i].UniqueID == p.UniqueID)
                {
                    item = User.Inventory[i];
                    break;
                }
            }

            if (item == null)
            {
                for (int i = 0; i < User.Equipment.Length; i++)
                {
                    if (User.Equipment[i] != null && User.Equipment[i].UniqueID == p.UniqueID)
                    {
                        item = User.Equipment[i];
                        break;
                    }
                }
            }

            if (Hero != null)
            {
                if (item == null)
                {
                    for (int i = 0; i < Hero.Inventory.Length; i++)
                    {
                        if (Hero.Inventory[i] != null && Hero.Inventory[i].UniqueID == p.UniqueID)
                        {
                            item = Hero.Inventory[i];
                            break;
                        }
                    }
                }

                if (item == null)
                {
                    for (int i = 0; i < Hero.Equipment.Length; i++)
                    {
                        if (Hero.Equipment[i] != null && Hero.Equipment[i].UniqueID == p.UniqueID)
                        {
                            item = Hero.Equipment[i];
                            break;
                        }
                    }
                }
            }

            if (item == null) return;

            item.SetSlotSize(p.SlotSize);
        }

        private void ItemSealChanged(S.ItemSealChanged p)
        {
            UserItem item = null;
            for (int i = 0; i < User.Inventory.Length; i++)
            {
                if (User.Inventory[i] != null && User.Inventory[i].UniqueID == p.UniqueID)
                {
                    item = User.Inventory[i];
                    break;
                }
            }

            if (item == null)
            {
                for (int i = 0; i < User.Equipment.Length; i++)
                {
                    if (User.Equipment[i] != null && User.Equipment[i].UniqueID == p.UniqueID)
                    {
                        item = User.Equipment[i];
                        break;
                    }
                }
            }

            if (Hero != null)
            {
                if (item == null)
                {
                    for (int i = 0; i < Hero.Inventory.Length; i++)
                    {
                        if (Hero.Inventory[i] != null && Hero.Inventory[i].UniqueID == p.UniqueID)
                        {
                            item = Hero.Inventory[i];
                            break;
                        }
                    }
                }

                if (item == null)
                {
                    for (int i = 0; i < Hero.Equipment.Length; i++)
                    {
                        if (Hero.Equipment[i] != null && Hero.Equipment[i].UniqueID == p.UniqueID)
                        {
                            item = Hero.Equipment[i];
                            break;
                        }
                    }
                }
            }

            if (item == null) return;

            item.SealedInfo = new SealedInfo { ExpiryDate = p.ExpiryDate };

            Ui.ItemInfoChanged(item);
        }

        private void ItemUpgraded(S.ItemUpgraded p)
        {
            UserItem item = null;
            MirGridType grid = MirGridType.Inventory;
            for (int i = 0; i < User.Inventory.Length; i++)
            {
                if (User.Inventory[i] != null && User.Inventory[i].UniqueID == p.Item.UniqueID)
                {
                    item = User.Inventory[i];
                    break;
                }
            }

            if (item == null && Hero != null)
            {
                for (int i = 0; i < Hero.Inventory.Length; i++)
                {
                    if (Hero.Inventory[i] != null && Hero.Inventory[i].UniqueID == p.Item.UniqueID)
                    {
                        item = Hero.Inventory[i];
                        grid = MirGridType.HeroInventory;
                        break;
                    }
                }
            }

            if (item == null) return;

            item.AddedStats.Clear();
            item.AddedStats.Add(p.Item.AddedStats);

            item.MaxDura = p.Item.MaxDura;
            item.RefineAdded = p.Item.RefineAdded;

            Ui.ItemUpgraded(grid, item);
        }

        private void NewMagic(S.NewMagic p)
        {
            ClientMagic magic = p.Magic;

            UserObject actor = User;
            if (p.Hero)
                actor = Hero;

            actor.Magics.Add(magic);
            actor.RefreshStats();
            Ui.SkillBarsChanged();
        }

        private void RemoveMagic(S.RemoveMagic p)
        {
            User.Magics.RemoveAt(p.PlaceId);
            User.RefreshStats();
            Ui.SkillBarsChanged();
        }

        private void MagicLeveled(S.MagicLeveled p)
        {
            UserObject actor = p.ObjectID == Hero?.ObjectID ? Hero : User;

            for (int i = 0; i < actor.Magics.Count; i++)
            {
                ClientMagic magic = actor.Magics[i];
                if (magic.Spell != p.Spell) continue;

                if (magic.Level != p.Level)
                {
                    magic.Level = p.Level;
                    actor.RefreshStats();
                }

                magic.Experience = p.Experience;
                break;
            }
        }
        private void Magic(S.Magic p)
        {
            User.Spell = p.Spell;
            User.Cast = p.Cast;
            User.TargetID = p.TargetID;
            User.TargetPoint = p.Target;
            User.SpellLevel = p.Level;
            User.SecondaryTargetIDs = p.SecondaryTargetIDs;

            if (!p.Cast) return;

            ClientMagic magic = User.GetMagic(p.Spell);
            magic.CastTime = CMain.Time;
        }

        private void MagicDelay(S.MagicDelay p)
        {
            ClientMagic magic;
            if (p.ObjectID == Hero?.ObjectID)
                magic = Hero.GetMagic(p.Spell);
            else
                magic = User.GetMagic(p.Spell);
            magic.Delay = p.Delay;
        }

        private void MagicCast(S.MagicCast p)
        {
            ClientMagic magic = User.GetMagic(p.Spell);
            magic.CastTime = CMain.Time;
        }

        private void ObjectMagic(S.ObjectMagic p)
        {
            if (p.SelfBroadcast == false && p.ObjectID == User.ObjectID && !Observing) return;

            if (p.ObjectID == Hero?.ObjectID && p.Cast)
            {
                ClientMagic magic = Hero.GetMagic(p.Spell);
                magic.CastTime = CMain.Time;
            }

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                QueuedAction action = new QueuedAction { Action = MirAction.Spell, Direction = p.Direction, Location = p.Location, Params = new List<object>() };
                action.Params.Add(p.Spell);
                action.Params.Add(p.TargetID);
                action.Params.Add(p.Target);
                action.Params.Add(p.Cast);
                action.Params.Add(p.Level);
                action.Params.Add(p.SecondaryTargetIDs);

                ob.ActionFeed.Add(action);
            }
        }

        private void ObjectProjectile(S.ObjectProjectile p)
        {
            MapObject source = MapControl.GetObject(p.Source);

            if (source == null) return;

            switch (p.Spell)
            {
                case Spell.FireBounce:
                    {
                        SoundManager.PlaySound(20000 + (ushort)Spell.GreatFireBall * 10 + 1);

                        Missile missile = source.CreateProjectile(410, Libraries.Magic, true, 6, 30, 4, targetID: p.Destination);

                        if (missile.Target != null)
                        {
                            missile.Complete += (o, e) =>
                            {
                                var sender = (Missile)o;

                                if (sender.Target.CurrentAction == MirAction.Dead) return;
                                sender.Target.Effects.Add(new Effect(Libraries.Magic, 570, 10, 600, sender.Target));
                                SoundManager.PlaySound(20000 + (ushort)Spell.GreatFireBall * 10 + 2);
                            };
                        }
                    }
                    break;
            }
        }

        private void ObjectEffect(S.ObjectEffect p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                PlayerObject player;

                switch (p.Effect)
                {
                    case SpellEffect.FurbolgWarriorCritical:
                        ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.FurbolgWarrior], 400, 6, 600, ob));
                        SoundManager.PlaySound(20000 + (ushort)Spell.FatalSword * 10);
                        break;
                    case SpellEffect.FatalSword:
                        ob.Effects.Add(new Effect(Libraries.Magic2, 1940, 4, 400, ob));
                        SoundManager.PlaySound(20000 + (ushort)Spell.FatalSword * 10);
                        break;
                    case SpellEffect.StormEscape:
                        ob.Effects.Add(new Effect(Libraries.Magic3, 610, 10, 600, ob));
                        SoundManager.PlaySound(SoundList.Teleport);
                        break;
                    case SpellEffect.Teleport:
                        ob.Effects.Add(new Effect(Libraries.Magic, 1600, 10, 600, ob));
                        SoundManager.PlaySound(SoundList.Teleport);
                        break;
                    case SpellEffect.Healing:
                        SoundManager.PlaySound(20000 + (ushort)Spell.Healing * 10 + 1);
                        ob.Effects.Add(new Effect(Libraries.Magic, 370, 10, 800, ob));
                        break;
                    case SpellEffect.RedMoonEvil:
                        ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.RedMoonEvil], 32, 6, 400, ob) { Blend = false });
                        break;
                    case SpellEffect.TwinDrakeBlade:
                        ob.Effects.Add(new Effect(Libraries.Magic2, 380, 6, 800, ob));
                        break;
                    case SpellEffect.MPEater:
                        if (MapControl.Objects.TryGetValue(p.EffectType, out var ob2))
                            ob2.Effects.Add(new Effect(Libraries.Magic2, 2411, 19, 1900, ob2));

                        ob.Effects.Add(new Effect(Libraries.Magic2, 2400, 9, 900, ob));
                        SoundManager.PlaySound(20000 + (ushort)Spell.FatalSword * 10);
                        break;
                    case SpellEffect.Bleeding:
                        ob.Effects.Add(new Effect(Libraries.Magic3, 60, 3, 400, ob));
                        break;
                    case SpellEffect.Hemorrhage:
                        SoundManager.PlaySound(20000 + (ushort)Spell.Hemorrhage * 10);
                        ob.Effects.Add(new Effect(Libraries.Magic3, 0, 4, 400, ob));
                        ob.Effects.Add(new Effect(Libraries.Magic3, 28, 6, 600, ob));
                        ob.Effects.Add(new Effect(Libraries.Magic3, 46, 8, 800, ob));
                        break;
                    case SpellEffect.MagicShieldUp:
                        if (ob.Race != ObjectType.Player && ob.Race != ObjectType.Hero) return;
                        player = (PlayerObject)ob;
                        if (player.ShieldEffect != null)
                        {
                            player.ShieldEffect.Clear();
                            player.ShieldEffect.Remove();
                        }
                        player.MagicShield = true;
                        player.Effects.Add(player.ShieldEffect = new Effect(Libraries.Magic, 3890, 3, 600, ob) { Repeat = true });
                        break;
                    case SpellEffect.MagicShieldDown:
                        if (ob.Race != ObjectType.Player && ob.Race != ObjectType.Hero) return;
                        player = (PlayerObject)ob;
                        if (player.ShieldEffect != null)
                        {
                            player.ShieldEffect.Clear();
                            player.ShieldEffect.Remove();
                        }
                        player.ShieldEffect = null;
                        player.MagicShield = false;
                        break;
                    case SpellEffect.GreatFoxSpirit:
                        ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.GreatFoxSpirit], 375 + (CMain.Random.Next(3) * 20), 20, 1400, ob));
                        SoundManager.PlaySound(((ushort)Monster.GreatFoxSpirit * 10) + 5);
                        break;
                    case SpellEffect.Entrapment:
                        ob.Effects.Add(new Effect(Libraries.Magic2, 1010, 10, 1500, ob));
                        ob.Effects.Add(new Effect(Libraries.Magic2, 1020, 8, 1200, ob));
                        break;
                    case SpellEffect.Critical:
                        //ob.Effects.Add(new Effect(Libraries.CustomEffects, 0, 12, 60, ob));
                        break;
                    case SpellEffect.Reflect:
                        ob.Effects.Add(new Effect(Libraries.Effect, 580, 10, 70, ob));
                        break;
                    case SpellEffect.ElementalBarrierUp:
                        if (ob.Race != ObjectType.Player) return;
                        player = (PlayerObject)ob;
                        if (player.ElementalBarrierEffect != null)
                        {
                            player.ElementalBarrierEffect.Clear();
                            player.ElementalBarrierEffect.Remove();
                        }

                        player.ElementalBarrier = true;
                        player.Effects.Add(player.ElementalBarrierEffect = new Effect(Libraries.Magic3, 1890, 10, 2000, ob) { Repeat = true });
                        break;
                    case SpellEffect.ElementalBarrierDown:
                        if (ob.Race != ObjectType.Player) return;
                        player = (PlayerObject)ob;
                        if (player.ElementalBarrierEffect != null)
                        {
                            player.ElementalBarrierEffect.Clear();
                            player.ElementalBarrierEffect.Remove();
                        }
                        player.ElementalBarrierEffect = null;
                        player.ElementalBarrier = false;
                        player.Effects.Add(player.ElementalBarrierEffect = new Effect(Libraries.Magic3, 1910, 7, 1400, ob));
                        SoundManager.PlaySound(20000 + 131 * 10 + 5);
                        break;
                    case SpellEffect.DelayedExplosion:
                        int effectid = DelayedExplosionEffect.GetOwnerEffectID(ob.ObjectID);
                        if (effectid < 0)
                        {
                            ob.Effects.Add(new DelayedExplosionEffect(Libraries.Magic3, 1590, 8, 1200, ob, true, 0, 0));
                        }
                        else if (effectid >= 0)
                        {
                            if (DelayedExplosionEffect.effectlist[effectid].stage < p.EffectType)
                            {
                                DelayedExplosionEffect.effectlist[effectid].Remove();
                                ob.Effects.Add(new DelayedExplosionEffect(Libraries.Magic3, 1590 + ((int)p.EffectType * 10), 8, 1200, ob, true, (int)p.EffectType, 0));
                            }
                        }
                        break;
                    case SpellEffect.AwakeningSuccess:
                        {
                            Effect ef = new Effect(Libraries.Magic3, 900, 16, 1600, ob, CMain.Time + p.DelayTime);
                            ef.Played += (o, e) => SoundManager.PlaySound(50002);
                            ef.Complete += (o, e) => MapControl.AwakeningAction = false;
                            ob.Effects.Add(ef);
                            ob.Effects.Add(new Effect(Libraries.Magic3, 840, 16, 1600, ob, CMain.Time + p.DelayTime) { Blend = false });
                        }
                        break;
                    case SpellEffect.AwakeningFail:
                        {
                            Effect ef = new Effect(Libraries.Magic3, 920, 9, 900, ob, CMain.Time + p.DelayTime);
                            ef.Played += (o, e) => SoundManager.PlaySound(50003);
                            ef.Complete += (o, e) => MapControl.AwakeningAction = false;
                            ob.Effects.Add(ef);
                            ob.Effects.Add(new Effect(Libraries.Magic3, 860, 9, 900, ob, CMain.Time + p.DelayTime) { Blend = false });
                        }
                        break;
                    case SpellEffect.AwakeningHit:
                        {
                            Effect ef = new Effect(Libraries.Magic3, 880, 5, 500, ob, CMain.Time + p.DelayTime);
                            ef.Played += (o, e) => SoundManager.PlaySound(50001);
                            ob.Effects.Add(ef);
                            ob.Effects.Add(new Effect(Libraries.Magic3, 820, 5, 500, ob, CMain.Time + p.DelayTime) { Blend = false });
                        }
                        break;
                    case SpellEffect.AwakeningMiss:
                        {
                            Effect ef = new Effect(Libraries.Magic3, 890, 5, 500, ob, CMain.Time + p.DelayTime);
                            ef.Played += (o, e) => SoundManager.PlaySound(50000);
                            ob.Effects.Add(ef);
                            ob.Effects.Add(new Effect(Libraries.Magic3, 830, 5, 500, ob, CMain.Time + p.DelayTime) { Blend = false });
                        }
                        break;
                    case SpellEffect.TurtleKing:
                        {
                            Effect ef = new Effect(Libraries.Monsters[(ushort)Monster.TurtleKing], CMain.Random.Next(2) == 0 ? 922 : 934, 12, 1200, ob);
                            ef.Played += (o, e) => SoundManager.PlaySound(20000 + (ushort)Spell.HellFire * 10 + 1);
                            ob.Effects.Add(ef);
                        }
                        break;
                    case SpellEffect.Behemoth:
                        {
                            MapControl.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.Behemoth], 788, 10, 1500, ob.CurrentLocation));
                            MapControl.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.Behemoth], 778, 10, 1500, ob.CurrentLocation, 0, true) { Blend = false });
                        }
                        break;
                    case SpellEffect.Stunned:
                        ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.StoningStatue], 632, 10, 1000, ob)
                        {
                            Repeat = p.Time > 0,
                            RepeatUntil = p.Time > 0 ? CMain.Time + p.Time : 0
                        });
                        break;
                    case SpellEffect.IcePillar:
                        ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.IcePillar], 18, 8, 800, ob));
                        break;
                    case SpellEffect.KingGuard:
                        if (p.EffectType == 0)
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.KingGuard], 753, 10, 1000, ob) { Blend = false });
                        }
                        else
                        {
                            ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.KingGuard], 763, 10, 1000, ob) { Blend = false });
                        }
                        break;
                    case SpellEffect.FlamingMutantWeb:
                        ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.FlamingMutant], 330, 10, 1000, ob)
                        {
                            Repeat = p.Time > 0,
                            RepeatUntil = p.Time > 0 ? CMain.Time + p.Time : 0
                        });
                        break;
                    case SpellEffect.DeathCrawlerBreath:
                        ob.Effects.Add(new Effect(Libraries.Monsters[(ushort)Monster.DeathCrawler], 272 + ((int)ob.Direction * 4), 4, 400, ob) { Blend = true });
                        break;
                    case SpellEffect.MoonMist:
                        ob.Effects.Add(new Effect(Libraries.Magic3, 705, 10, 800, ob));
                        break;
                }
            }
        }

        private void RangeAttack(S.RangeAttack p)
        {
            User.TargetID = p.TargetID;
            User.TargetPoint = p.Target;
            User.Spell = p.Spell;
        }

        private void Pushed(S.Pushed p)
        {
            User.ActionFeed.Add(new QueuedAction { Action = MirAction.Pushed, Direction = p.Direction, Location = p.Location });
        }

        private void ObjectPushed(S.ObjectPushed p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Pushed, Direction = p.Direction, Location = p.Location });
        }

        private void ObjectName(S.ObjectName p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                ob.Name = p.Name;
                ob.InvalidateMap();
            }
        }

        private void UserStorage(S.UserStorage p)
        {
            if (Storage.Length != p.Storage.Length)
            {
                Array.Resize(ref Storage, p.Storage.Length);
            }

            Storage = p.Storage;

            for (int i = 0; i < Storage.Length; i++)
            {
                if (Storage[i] == null) continue;
                Bind(Storage[i]);
            }
        }
        private void SwitchGroup(S.SwitchGroup p)
        {
            Models.Group.AllowGroup = p.AllowGroup;
            Models.Group.Changed.Emit();

            if (!p.AllowGroup && Models.Group.Members.Count > 0)
                DeleteGroup();
        }

        private void DeleteGroup()
        {
            Models.Group.Members.Clear();
            Models.Group.MemberMaps.Clear();
            Models.Group.MemberLocations.Clear();
            Models.Group.MemberStatus.Clear();
            Models.Group.Changed.Emit();
            Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouHaveLeftGroup), ChatType.Group);
        }

        private void DeleteMember(S.DeleteMember p)
        {
            Models.Group.Members.Remove(p.Name);
            Models.Group.MemberMaps.Remove(p.Name);
            Models.Group.MemberLocations.Remove(p.Name);
            Models.Group.MemberStatus.Remove(p.Name);
            Models.Group.Changed.Emit();
            Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerHasLeftGroup), p.Name), ChatType.Group);
        }

        private void GroupInvite(S.GroupInvite p)
        {
            Ui.GroupInvite(p);
        }
        private void AddMember(S.AddMember p)
        {
            Models.Group.Members.Add(p.Name);
            Models.Group.Changed.Emit();
            Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerHasJoinedGroup), p.Name), ChatType.Group);
        }
        private void GroupMemberStatus(S.GroupMemberStatus p)
        {
            if (string.IsNullOrEmpty(p.Name)) return;
            Models.Group.MemberStatus[p.Name] = p;
            Models.Group.StatusChanged.Emit(p.Name);
        }
        private void GroupMembersMap(S.GroupMembersMap p)
        {
            Dictionary<string, string> memberMaps = Models.Group.MemberMaps;
            if (!memberMaps.ContainsKey(p.PlayerName))
                memberMaps.Add(p.PlayerName, p.PlayerMap);
            else
            {
                memberMaps.Remove(p.PlayerName);
                memberMaps.Add(p.PlayerName, p.PlayerMap);
            }
            Models.Group.Changed.Emit();
        }
        private void SendMemberLocation(S.SendMemberLocation p)
        {
            Dictionary<string, Point> memberLocations = Models.Group.MemberLocations;
            if (!memberLocations.ContainsKey(p.MemberName))
                memberLocations.Add(p.MemberName, p.MemberLocation);
            else
            {
                memberLocations.Remove(p.MemberName);
                memberLocations.Add(p.MemberName, p.MemberLocation);
            }
            Models.Group.Changed.Emit();
        }
        private void Revived()
        {
            User.SetAction();
            User.Dead = false;
            User.Effects.Add(new Effect(Libraries.Magic2, 1220, 20, 2000, User));
            SoundManager.PlaySound(SoundList.Revive);
        }
        private void ObjectRevived(S.ObjectRevived p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                if (p.Effect)
                {
                    ob.Effects.Add(new Effect(Libraries.Magic2, 1220, 20, 2000, ob));
                    SoundManager.PlaySound(SoundList.Revive);
                }
                ob.Dead = false;
                ob.ActionFeed.Clear();
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Revive, Direction = ob.Direction, Location = ob.CurrentLocation });
            }
        }
        private void SpellToggle(S.SpellToggle p)
        {
            UserObject actor = User;
            string prefix = string.Empty;

            if (p.ObjectID == Hero?.ObjectID)
            {
                actor = Hero;
                prefix = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.Hero);
            }

            switch (p.Spell)
            {
                //Warrior
                case Spell.Slaying:
                    actor.Slaying = p.CanUse;
                    break;
                case Spell.Thrusting:
                    actor.Thrusting = p.CanUse;
                    Models.Chat.Add(
                        prefix + (actor.Thrusting ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UseThrusting) : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DoNotUseThrusting)),
                        ChatType.Hint);
                    break;
                case Spell.HalfMoon:
                    actor.HalfMoon = p.CanUse;
                    Models.Chat.Add(
                        prefix + (actor.HalfMoon ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UseHalfMoon) : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DoNotUseHalfMoon)),
                        ChatType.Hint);
                    break;
                case Spell.CrossHalfMoon:
                    actor.CrossHalfMoon = p.CanUse;
                    Models.Chat.Add(
                        prefix + (actor.CrossHalfMoon ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UseCrossHalfMoon) : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DoNotUseCrossHalfMoon)),
                        ChatType.Hint);
                    break;
                case Spell.DoubleSlash:
                    actor.DoubleSlash = p.CanUse;
                    Models.Chat.Add(
                        prefix + (actor.DoubleSlash ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UseDoubleSlash) : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DoNotUseDoubleSlash)),
                        ChatType.Hint);
                    break;
                case Spell.FlamingSword:
                    actor.FlamingSword = p.CanUse;
                    if (actor.FlamingSword)
                        Models.Chat.Add(prefix + GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.WeaponSpiritFire), ChatType.Hint);
                    else
                        Models.Chat.Add(prefix + GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.SpiritsFireDisappeared), ChatType.System);
                    break;
            }
        }

        private void ObjectHealth(S.ObjectHealth p)
        {
            if (p.ObjectID == Hero?.ObjectID)
                Hero.PercentHealth = p.Percent;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                ob.PercentHealth = p.Percent;
                ob.HealthTime = CMain.Time + p.Expire * 1000;
                ob.InvalidateMap(); // health bar shown
            }
        }

        private void ObjectMana(S.ObjectMana p)
        {
            if (p.ObjectID == Hero?.ObjectID)
                Hero.PercentMana = p.Percent;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
                ob.PercentMana = p.Percent;
        }


        private void MapEffect(S.MapEffect p)
        {
            switch (p.Effect)
            {
                case SpellEffect.Mine:
                    SoundManager.PlaySound(10091);
                    Effect HitWall = new Effect(Libraries.Effect, 8 * p.Value, 3, 240, p.Location) { Light = 0 };
                    MapControl.Effects.Add(HitWall);
                    break;
                case SpellEffect.Tester:
                    Effect eff = new Effect(Libraries.Effect, 328, 10, 500, p.Location) { Light = 0 };
                    MapControl.Effects.Add(eff);
                    break;
            }
        }

        private void ObjectRangeAttack(S.ObjectRangeAttack p)
        {
            if (p.ObjectID == User.ObjectID && !Observing) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                QueuedAction action = null;
                if (ob.Race == ObjectType.Player)
                {
                    action = p.Type switch
                    {
                        _ => new QueuedAction { Action = MirAction.AttackRange1, Direction = p.Direction, Location = p.Location, Params = new List<object>() }
                    };
                }
                else
                {
                    action = p.Type switch
                    {
                        1 => new QueuedAction { Action = MirAction.AttackRange2, Direction = p.Direction, Location = p.Location, Params = new List<object>() },
                        2 => new QueuedAction { Action = MirAction.AttackRange3, Direction = p.Direction, Location = p.Location, Params = new List<object>() },
                        _ => new QueuedAction { Action = MirAction.AttackRange1, Direction = p.Direction, Location = p.Location, Params = new List<object>() }
                    };
                }

                action.Params.Add(p.TargetID);
                action.Params.Add(p.Target);
                action.Params.Add(p.Spell);
                action.Params.Add(new List<uint>());
                action.Params.Add(p.Level);

                ob.ActionFeed.Add(action);
            }
        }

        private void AddBuff(S.AddBuff p)
        {
            ClientBuff buff = p.Buff;

            if (!buff.Paused)
            {
                buff.ExpireTime += CMain.Time;
            }

            if (buff.ObjectID == User.ObjectID)
            {
                List<ClientBuff> buffs = Models.Buffs.Items;
                for (int i = 0; i < buffs.Count; i++)
                {
                    if (buffs[i].Type != buff.Type) continue;

                    buffs[i] = buff;
                    Models.Buffs.Changed.Emit();
                    User.RefreshStats();
                    return;
                }

                buffs.Add(buff);
                Models.Buffs.Changed.Emit();
                Ui.BuffAdded(false, buff);

                User.RefreshStats();
            }

            if (Hero != null && buff.ObjectID == Hero.ObjectID)
            {
                List<ClientBuff> buffs = Models.HeroBuffs.Items;
                for (int i = 0; i < buffs.Count; i++)
                {
                    if (buffs[i].Type != buff.Type) continue;

                    buffs[i] = buff;
                    Models.HeroBuffs.Changed.Emit();
                    Hero.RefreshStats();
                    return;
                }

                buffs.Add(buff);
                Models.HeroBuffs.Changed.Emit();
                Ui.BuffAdded(true, buff);

                Hero.RefreshStats();
            }

            if (!buff.Visible || buff.ObjectID <= 0) return;

            if (MapControl.Objects.TryGetValue(buff.ObjectID, out var ob) && (ob is PlayerObject || ob is MonsterObject))
            {
                if (!ob.Buffs.Contains(buff.Type))
                    ob.Buffs.Add(buff.Type);
                ob.AddBuffEffect(buff.Type);
            }
        }

        private void RemoveBuff(S.RemoveBuff p)
        {
            if (User.ObjectID == p.ObjectID)
            {
                List<ClientBuff> buffs = Models.Buffs.Items;
                for (int i = 0; i < buffs.Count; i++)
                {
                    if (buffs[i].Type != p.Type) continue;

                    switch (buffs[i].Type)
                    {
                        case BuffType.SwiftFeet:
                            User.Sprint = false;
                            break;
                        case BuffType.Transform:
                            User.TransformType = -1;
                            break;
                    }

                    Ui.BuffRemoved(false, i);
                    buffs.RemoveAt(i);
                    Models.Buffs.Changed.Emit();
                }
                User.RefreshStats();
            }

            if (Hero != null && Hero.ObjectID == p.ObjectID)
            {
                List<ClientBuff> buffs = Models.HeroBuffs.Items;
                for (int i = 0; i < buffs.Count; i++)
                {
                    if (buffs[i].Type != p.Type) continue;

                    switch (buffs[i].Type)
                    {
                        case BuffType.SwiftFeet:
                            Hero.Sprint = false;
                            break;
                        case BuffType.Transform:
                            Hero.TransformType = -1;
                            break;
                    }

                    Ui.BuffRemoved(true, i);
                    buffs.RemoveAt(i);
                    Models.HeroBuffs.Changed.Emit();
                }
                Hero.RefreshStats();
            }

            if (p.ObjectID <= 0) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                ob.Buffs.Remove(p.Type);
                ob.RemoveBuffEffect(p.Type);
            }
        }

        private void PauseBuff(S.PauseBuff p)
        {
            if (User.ObjectID == p.ObjectID)
            {
                List<ClientBuff> buffs = Models.Buffs.Items;
                for (int i = 0; i < buffs.Count; i++)
                {
                    if (buffs[i].Type != p.Type) continue;

                    User.RefreshStats();

                    if (buffs[i].Paused == p.Paused) return;

                    buffs[i].Paused = p.Paused;

                    if (p.Paused)
                    {
                        buffs[i].ExpireTime -= CMain.Time;
                    }
                    else
                    {
                        buffs[i].ExpireTime += CMain.Time;
                    }
                    Models.Buffs.Changed.Emit();
                }
            }

            if (Hero != null && Hero.ObjectID == p.ObjectID)
            {
                List<ClientBuff> buffs = Models.HeroBuffs.Items;
                for (int i = 0; i < buffs.Count; i++)
                {
                    if (buffs[i].Type != p.Type) continue;

                    Hero.RefreshStats();

                    if (buffs[i].Paused == p.Paused) return;

                    buffs[i].Paused = p.Paused;

                    if (p.Paused)
                    {
                        buffs[i].ExpireTime -= CMain.Time;
                    }
                    else
                    {
                        buffs[i].ExpireTime += CMain.Time;
                    }
                    Models.HeroBuffs.Changed.Emit();
                }
            }
        }

        private void ObjectHidden(S.ObjectHidden p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                ob.Hidden = p.Hidden;
                ob.InvalidateMap();
            }
        }

        private void ObjectSneaking(S.ObjectSneaking p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                // ob.SneakingActive = p.SneakingActive;
            }
        }

        private void ObjectLevelEffects(S.ObjectLevelEffects p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob) && ob is PlayerObject player)
            {
                player.LevelEffects = p.LevelEffects;
                player.SetEffects();
            }
        }

        private void RefreshItem(S.RefreshItem p)
        {
            Bind(p.Item);

            if (ItemMoves.Selected != null && ItemMoves.Selected.Item.UniqueID == p.Item.UniqueID)
                ItemMoves.Selected = null;

            if (HoverItem != null && HoverItem.UniqueID == p.Item.UniqueID)
            {
                DisposeItemLabel();
                CreateItemLabel(p.Item);
            }

            for (int i = 0; i < User.Inventory.Length; i++)
            {
                if (User.Inventory[i] != null && User.Inventory[i].UniqueID == p.Item.UniqueID)
                {
                    User.Inventory[i] = p.Item;
                    User.RefreshStats();
                    return;
                }
            }

            for (int i = 0; i < User.Equipment.Length; i++)
            {
                if (User.Equipment[i] != null && User.Equipment[i].UniqueID == p.Item.UniqueID)
                {
                    User.Equipment[i] = p.Item;
                    User.RefreshStats();
                    return;
                }
            }

            if (Hero != null)
            {
                for (int i = 0; i < Hero.Inventory.Length; i++)
                {
                    if (Hero.Inventory[i] != null && Hero.Inventory[i].UniqueID == p.Item.UniqueID)
                    {
                        Hero.Inventory[i] = p.Item;
                        Hero.RefreshStats();
                        return;
                    }
                }

                for (int i = 0; i < Hero.Equipment.Length; i++)
                {
                    if (Hero.Equipment[i] != null && Hero.Equipment[i].UniqueID == p.Item.UniqueID)
                    {
                        Hero.Equipment[i] = p.Item;
                        Hero.RefreshStats();
                        return;
                    }
                }
            }
        }

        private void ObjectSpell(S.ObjectSpell p)
        {
            SpellObject ob = new SpellObject(p.ObjectID);
            ob.Load(p);
        }

        private void ObjectDeco(S.ObjectDeco p)
        {
            DecoObject ob = new DecoObject(p.ObjectID);
            ob.Load(p);
        }

        private void UserDash(S.UserDash p)
        {
            if (User.Direction == p.Direction && User.CurrentLocation == p.Location)
            {
                MapControl.NextAction = 0;
                return;
            }
            MirAction action = User.CurrentAction == MirAction.DashL ? MirAction.DashR : MirAction.DashL;
            for (int i = User.ActionFeed.Count - 1; i >= 0; i--)
            {
                if (User.ActionFeed[i].Action == MirAction.DashR)
                {
                    action = MirAction.DashL;
                    break;
                }
                if (User.ActionFeed[i].Action == MirAction.DashL)
                {
                    action = MirAction.DashR;
                    break;
                }
            }

            User.ActionFeed.Add(new QueuedAction { Action = action, Direction = p.Direction, Location = p.Location });
        }

        private void UserDashFail(S.UserDashFail p)
        {
            MapControl.NextAction = 0;
            User.ActionFeed.Add(new QueuedAction { Action = MirAction.DashFail, Direction = p.Direction, Location = p.Location });
        }

        private void ObjectDash(S.ObjectDash p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                var action = ob.ActionFeed.Count > 0 && ob.ActionFeed[^1].Action == MirAction.DashL ? MirAction.DashR : MirAction.DashL;
                ob.ActionFeed.Add(new QueuedAction { Action = action, Direction = p.Direction, Location = p.Location });
            }

        }

        private void ObjectDashFail(S.ObjectDashFail p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.DashFail, Direction = p.Direction, Location = p.Location });
        }

        private void UserBackStep(S.UserBackStep p)
        {
            if (User.Direction == p.Direction && User.CurrentLocation == p.Location)
            {
                MapControl.NextAction = 0;
                return;
            }
            User.ActionFeed.Add(new QueuedAction { Action = MirAction.Jump, Direction = p.Direction, Location = p.Location });
        }

        private void ObjectBackStep(S.ObjectBackStep p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                ob.JumpDistance = p.Distance;
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Jump, Direction = p.Direction, Location = p.Location });
            }
        }

        private void UserDashAttack(S.UserDashAttack p)
        {
            if (User.Direction == p.Direction && User.CurrentLocation == p.Location)
            {
                MapControl.NextAction = 0;
                return;
            }
            //User.JumpDistance = p.Distance;
            User.ActionFeed.Add(new QueuedAction { Action = MirAction.DashAttack, Direction = p.Direction, Location = p.Location });
        }

        private void ObjectDashAttack(S.ObjectDashAttack p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob))
            {
                ob.JumpDistance = p.Distance;
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.DashAttack, Direction = p.Direction, Location = p.Location });
            }
        }

        private void UserAttackMove(S.UserAttackMove p)//Warrior Skill - SlashingBurst
        {
            MapControl.NextAction = 0;
            if (User.CurrentLocation == p.Location && User.Direction == p.Direction) return;


            MapControl.RemoveObject(User);
            User.CurrentLocation = p.Location;
            User.MapLocation = p.Location;
            MapControl.AddObject(User);


            MapControl.FloorValid = false;
            MapControl.TextureValid = false;
            MapControl.InputDelay = CMain.Time + 400;


            if (User.Dead) return;


            User.ClearMagic();
            User.QueuedAction = null;


            for (int i = User.ActionFeed.Count - 1; i >= 0; i--)
            {
                if (User.ActionFeed[i].Action == MirAction.Pushed) continue;
                User.ActionFeed.RemoveAt(i);
            }


            User.SetAction();

            User.ActionFeed.Add(new QueuedAction { Action = MirAction.Standing, Direction = p.Direction, Location = p.Location });
        }

        private void SetConcentration(S.SetConcentration p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob) && (ob.Race == ObjectType.Player || ob.Race == ObjectType.Hero))
            {
                var player = (PlayerObject)ob;
                player.Concentrating = p.Enabled;
                player.ConcentrateInterrupted = p.Interrupted;

                if (p.Enabled && !p.Interrupted && InterruptionEffect.GetOwnerEffectID(player.ObjectID) < 0)
                {
                    player.Effects.Add(new InterruptionEffect(Libraries.Magic3, 1860, 8, 8 * 100, player, true));
                    SoundManager.PlaySound(20000 + 129 * 10);
                }
            }
        }

        private void SetElemental(S.SetElemental p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out var ob) && (ob.Race == ObjectType.Player || ob.Race == ObjectType.Hero))
            {
                var player = (PlayerObject)ob;
                player.HasElements = p.Enabled;
                player.ElementCasted = p.Casted && User.ObjectID != p.ObjectID;
                player.ElementsLevel = (int)p.Value;

                if (p.Enabled && p.ElementType > 0)
                {
                    int elementType = (int)p.ElementType;
                    int maxExp = (int)p.ExpLast;
                    player.Effects.Add(new ElementsEffect(Libraries.Magic3, 1630 + ((elementType - 1) * 10), 10, 10 * 100, player, true, 1 + (elementType - 1), maxExp, User.ObjectID == p.ObjectID && (elementType == 4 || elementType == 3)));
                }
            }
        }

        private void RemoveDelayedExplosion(S.RemoveDelayedExplosion p)
        {
            //if (p.ObjectID == User.ObjectID) return;

            int effectid = DelayedExplosionEffect.GetOwnerEffectID(p.ObjectID);
            if (effectid >= 0)
                DelayedExplosionEffect.effectlist[effectid].Remove();
        }
        private void SetBindingShot(S.SetBindingShot p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out MapObject ob) && ob.Race == ObjectType.Monster)
            {
                var NetCast = new TrackableEffect(new Effect(Libraries.MagicC, 0, 8, 700, ob)) { EffectName = "BindingShotDrop" };
                var NetDropped = new TrackableEffect(new Effect(Libraries.MagicC, 7, 1, 1000, ob) { Repeat = true, RepeatUntil = CMain.Time + (p.Value - 1500) }) { EffectName = "BindingShotDown" };
                var NetFall = new TrackableEffect(new Effect(Libraries.MagicC, 8, 8, 700, ob)) { EffectName = "BindingShotFall" };

                NetDropped.Complete += (o1, e1) =>
                {
                    SoundManager.PlaySound(20000 + 130 * 10 + 6); // sound M130-6
                    ob.Effects.Add(NetFall);
                };

                NetCast.Complete += (o, e) =>
                {
                    SoundManager.PlaySound(20000 + 130 * 10 + 5); // sound M130-5
                    ob.Effects.Add(NetDropped);
                };

                ob.Effects.Add(NetCast);
            }
        }

        private void SendOutputMessage(S.SendOutputMessage p)
        {
            OutputMessage(p.Message, p.Type);
        }

        private void NPCConsign()
        {
            Ui.NpcDropPanel(PanelType.Consign);
        }
        private void NPCMarket(S.NPCMarket p)
        {
            for (int i = 0; i < p.Listings.Count; i++)
                Bind(p.Listings[i].Item);

            Ui.MarketOpening();
            Models.Market.UserMode = p.UserMode;
            Models.Market.Listings = p.Listings;
            Models.Market.Changed.Emit();
            Ui.MarketListed(p.Pages);
        }
        private void NPCMarketPage(S.NPCMarketPage p)
        {
            if (!Ui.IsMarketOpen()) return;

            for (int i = 0; i < p.Listings.Count; i++)
                Bind(p.Listings[i].Item);

            Models.Market.Listings.AddRange(p.Listings);
            Models.Market.Changed.Emit();
            Ui.MarketPageAdded();
        }
        private void ConsignItem(S.ConsignItem p)
        {
            ItemSlotRef cell = ItemMoves.Find(MirGridType.Inventory, p.UniqueID);

            if (cell == null) return;

            cell.Locked = false;

            if (!p.Success) return;

            cell.Item = null;

            User.RefreshStats();
        }
        private void MarketFail(S.MarketFail p)
        {
            Models.Market.MarketTime = 0;
            string text = null;
            switch (p.Reason)
            {
                case 0:
                    text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouCannotUseTrustMerchantWhenDead);
                    break;
                case 1:
                    text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouCannotBuyFromTrustMerchantWithoutUsing);
                    break;
                case 2:
                    text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ItemHasBeenSold);
                    break;
                case 3:
                    text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ItemExpiredCannotBeBought);
                    break;
                case 4:
                    text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowGold);
                    break;
                case 5:
                    text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.NotEnoughWeightOrSpaceToBuyItem);
                    break;
                case 6:
                    text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.CannotBuyOwnItems);
                    break;
                case 7:
                    text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TooFarFromTrustMerchant);
                    break;
                case 8:
                    text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.CannotHoldGoldForSale);
                    break;
                case 9:
                    text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ItemNotMetMinimumBid);
                    break;
                case 10:
                    text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AuctionAlreadyEndedForItem);
                    break;
            }
            if (text != null) Ui.MarketMessage(text, true);
        }
        private void MarketSuccess(S.MarketSuccess p)
        {
            Models.Market.MarketTime = 0;
            Ui.MarketMessage(p.Message, false);
        }
        private void ObjectSitDown(S.ObjectSitDown p)
        {
            if (p.ObjectID == User.ObjectID) return;

            if (MapControl.Objects.TryGetValue(p.ObjectID, out MapObject ob) && ob.Race == ObjectType.Monster)
            {
                ob.SitDown = p.Sitting;
                ob.ActionFeed.Add(new QueuedAction { Action = MirAction.SitDown, Direction = p.Direction, Location = p.Location });
            }
        }

        private void BaseStatsInfo(S.BaseStatsInfo p)
        {
            User.CoreStats = p.Stats;
            User.RefreshStats();
        }

        private void HeroBaseStatsInfo(S.HeroBaseStatsInfo p)
        {
            if (Hero == null) return;

            Hero.CoreStats = p.Stats;
            Hero.RefreshStats();
        }

        private void UserName(S.UserName p)
        {
            for (int i = 0; i < UserIdList.Count; i++)
                if (UserIdList[i].Id == p.Id)
                {
                    UserIdList[i].UserName = p.Name;
                    break;
                }
            DisposeItemLabel();
            HoverItem = null;
        }

        private void ChatItemStats(S.ChatItemStats p)
        {
            //for (int i = 0; i < ChatItemList.Count; i++)
            //    if (ChatItemList[i].ID == p.ChatItemId)
            //    {
            //        ChatItemList[i].ItemStats = p.Stats;
            //        ChatItemList[i].RecievedTick = CMain.Time;
            //    }
        }

        private void GuildInvite(S.GuildInvite p)
        {
            Ui.GuildInviteRequest(p);
        }

        private void GuildNameRequest(S.GuildNameRequest p)
        {
            Ui.GuildNameRequest();
        }

        private void GuildRequestWar(S.GuildRequestWar p)
        {
            Ui.GuildWarRequest();
        }

        private void GuildNoticeChange(S.GuildNoticeChange p)
        {
            if (p.update == -1)
                Models.Guild.NoticeStale = true;
            else
                Models.Guild.SetNotice(p.notice);
            Ui.GuildNoticeChange(p);
        }
        private void GuildMemberChange(S.GuildMemberChange p)
        {
            switch (p.Status)
            {
                case 1: // logged on
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerLoggedOn), p.Name), ChatType.Guild);
                    break;
                case 2: //new member
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerJoinedGuild), p.Name), ChatType.Guild);
                    Models.Guild.MemberCount++;
                    Models.Guild.Changed.Emit();
                    break;
                case 3: //kicked member
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerRemovedFromGuild), p.Name), ChatType.Guild);
                    Models.Guild.MemberCount = Math.Max(0, Models.Guild.MemberCount - 1);
                    Models.Guild.Changed.Emit();
                    break;
                case 4: //member left
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerLeftGuild), p.Name), ChatType.Guild);
                    Models.Guild.MemberCount = Math.Max(0, Models.Guild.MemberCount - 1);
                    Models.Guild.Changed.Emit();
                    break;
            }

            // The member-list half (was GuildDialog's: classic only refreshes its controls in Ui.GuildMemberChange)
            GuildState guild = Models.Guild;
            GuildMemberEdit edit = GuildMemberEdit.None;
            bool membersEdited = true;
            switch (p.Status)
            {
                case 0: // logged off
                    guild.SetMemberOnline(p.Name, false);
                    break;
                case 1: // logged on
                    guild.SetMemberOnline(p.Name, true);
                    break;
                case 2: // new member
                case 3: // kicked member
                case 4: // member left
                case 5: // rank change (name or different rank)
                    guild.MembersStale = true;
                    membersEdited = false;
                    break;
                case 6: // new rank
                    if (p.Ranks.Count > 0) guild.AddRank(p.Ranks[0]);
                    break;
                case 7: // rank option changed
                    if (p.Ranks.Count > 0)
                    {
                        guild.ChangeRank(p.Ranks[0], out bool mine, out bool renamed);
                        if (mine) edit |= GuildMemberEdit.Mine;
                        if (renamed) edit |= GuildMemberEdit.Renamed;
                    }
                    break;
                case 8: // my rank changed
                    if (p.Ranks.Count > 0 && guild.ChangeMyRank(p.Ranks[0])) edit |= GuildMemberEdit.Moved;
                    break;
                case 255:
                    guild.ReplaceMembers(p.Ranks);
                    break;
                default:
                    membersEdited = false;
                    break;
            }

            Ui.GuildMemberChange(p, edit);
            if (membersEdited) guild.MembersChanged.Emit();
        }

        private void GuildStatus(S.GuildStatus p)
        {
            Ui.GuildStatusChanging(p, (User.GuildName == "") && (p.GuildName != ""));

            GuildState guild = Models.Guild;
            bool GuildChange = User.GuildName != p.GuildName;
            if (User.GuildName == "" && p.GuildName != "")
            {
                guild.NoticeStale = true;
                guild.MembersStale = true;
            }
            if (GuildChange && User.GuildName != "")
                guild.ClearExtras(); // the member details, wars and log belong to the old guild
            User.GuildName = p.GuildName;
            User.GuildRankName = p.GuildRankName;
            guild.Level = p.Level;
            guild.Experience = p.Experience;
            guild.MaxExperience = p.MaxExperience;
            guild.Gold = p.Gold;
            guild.SparePoints = p.SparePoints;
            guild.MemberCount = p.MemberCount;
            guild.MaxMembers = p.MaxMembers;
            guild.Voting = p.Voting;
            guild.ItemCount = p.ItemCount;
            guild.BuffCount = p.BuffCount;
            guild.MyOptions = p.MyOptions;
            Ui.GuildStatusChanged(p);
            guild.MyRankId = p.MyRankId;
            guild.Changed.Emit();
            Ui.GuildMembersChanged();
            //reset guildbuffs
            if (GuildChange)
            {
                guild.EnabledBuffs.Clear();
                guild.BuffsChanged.Emit();
                Ui.GuildActiveStatsChanged();
                RemoveBuff(new S.RemoveBuff { ObjectID = User.ObjectID, Type = BuffType.Guild });
                User.RefreshStats();
            }
        }

        private void GuildExpGain(S.GuildExpGain p)
        {
            //OutputMessage(string.Format("Guild Experience Gained {0}.", p.Amount));
            Models.Guild.Experience += p.Amount;
            Models.Guild.Changed.Emit();
        }

        private void GuildStorageGoldChange(S.GuildStorageGoldChange p)
        {
            switch (p.Type)
            {
                case 0:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerDonatedGoldToGuildFunds), p.Name, p.Amount), ChatType.Guild);
                    Models.Guild.Gold += p.Amount;
                    break;
                case 1:
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerRetrievedGoldFromGuildFunds), p.Name, p.Amount), ChatType.Guild);
                    if (Models.Guild.Gold > p.Amount)
                        Models.Guild.Gold -= p.Amount;
                    else
                        Models.Guild.Gold = 0;
                    break;
                case 2:
                    if (Models.Guild.Gold > p.Amount)
                        Models.Guild.Gold -= p.Amount;
                    else
                        Models.Guild.Gold = 0;
                    break;
                case 3:
                    Models.Guild.Gold += p.Amount;
                    break;
            }
            Models.Guild.Changed.Emit();
        }

        private void GuildStorageItemChange(S.GuildStorageItemChange p)
        {
            ItemSlotRef fromCell = null;
            ItemSlotRef toCell = null;
            switch (p.Type)
            {
                case 0://store
                    toCell = ItemMoves.Slot(MirGridType.GuildStorage, p.To);

                    if (toCell == null) return;

                    toCell.Locked = false;
                    toCell.Item = p.Item.Item;
                    Bind(toCell.Item);
                    if (p.User != User.Id) return;
                    fromCell = ItemMoves.Slot(MirGridType.Inventory, p.From);
                    fromCell.Locked = false;
                    if (fromCell != null)
                        fromCell.Item = null;
                    User.RefreshStats();
                    break;
                case 1://retrieve
                    fromCell = ItemMoves.Slot(MirGridType.GuildStorage, p.From);

                    if (fromCell == null) return;
                    fromCell.Locked = false;

                    if (p.User != User.Id)
                    {
                        fromCell.Item = null;
                        return;
                    }
                    toCell = ItemMoves.Slot(MirGridType.Inventory, p.To);
                    if (toCell == null) return;
                    toCell.Locked = false;
                    toCell.Item = fromCell.Item;
                    fromCell.Item = null;
                    break;

                case 2:
                    toCell = ItemMoves.Slot(MirGridType.GuildStorage, p.To);
                    fromCell = ItemMoves.Slot(MirGridType.GuildStorage, p.From);

                    if (toCell == null || fromCell == null) return;

                    toCell.Locked = false;
                    fromCell.Locked = false;
                    fromCell.Item = toCell.Item;
                    toCell.Item = p.Item.Item;

                    Bind(toCell.Item);
                    if (fromCell.Item != null)
                        Bind(fromCell.Item);
                    break;
                case 3://failstore
                    fromCell = ItemMoves.Slot(MirGridType.Inventory, p.From);
                    toCell = ItemMoves.Slot(MirGridType.GuildStorage, p.To);

                    if (toCell == null || fromCell == null) return;

                    toCell.Locked = false;
                    fromCell.Locked = false;
                    break;
                case 4://failretrieve
                    toCell = ItemMoves.Slot(MirGridType.Inventory, p.To);
                    fromCell = ItemMoves.Slot(MirGridType.GuildStorage, p.From);

                    if (toCell == null || fromCell == null) return;

                    toCell.Locked = false;
                    fromCell.Locked = false;
                    break;
                case 5://failmove
                    fromCell = ItemMoves.Slot(MirGridType.GuildStorage, p.To);
                    toCell = ItemMoves.Slot(MirGridType.GuildStorage, p.From);

                    if (toCell == null || fromCell == null) return;

                    ItemMoves.SetLocked(MirGridType.GuildStorage, p.From, false);
                    ItemMoves.SetLocked(MirGridType.GuildStorage, p.To, false);
                    break;
            }
        }
        private void GuildStorageList(S.GuildStorageList p)
        {
            // The guild storage grid shows GuildStorage slot for slot (was GuildDialog.StorageGrid[i].Item, 8 x 14 = GuildStorage.Length).
            for (int i = 0; i < p.Items.Length; i++)
            {
                if (i >= GuildStorage.Length) break;
                if (p.Items[i] == null)
                {
                    ItemMoves.SetItem(MirGridType.GuildStorage, i, null);
                    continue;
                }
                ItemMoves.SetItem(MirGridType.GuildStorage, i, p.Items[i].Item);
                Bind(ItemMoves.GetItem(MirGridType.GuildStorage, i));
            }
        }
        private void GuildTerritoryPage(S.GuildTerritoryPage p)
        {
            Ui.GuildTerritoryPage(p);
        }

        private void HeroCreateRequest(S.HeroCreateRequest p)
        {
            Ui.HeroCreateRequest(p);
        }

        private void ManageHeroes(S.ManageHeroes p)
        {
            if (p.Heroes != null)
            {
                for (int i = 0; i < p.Heroes.Length; i++)
                    AddHeroInformation(p.Heroes[i], i);
            }

            MaximumHeroCount = p.MaximumCount;
            CurrentHeroInfo = p.CurrentHero;
            Ui.ManageHeroes(p.CurrentHero);
        }

        private void ChangeHero(S.ChangeHero p)
        {
            if (p.FromIndex < 0 || p.FromIndex >= HeroStorage.Length) return;
            ClientHeroInformation temp = HeroStorage[p.FromIndex];
            HeroStorage[p.FromIndex] = CurrentHeroInfo;
            CurrentHeroInfo = temp;
            Ui.ChangeHero(p);
        }

        public int HeroAvatar(MirClass job, MirGender gender) => 1400 + (byte)job + 10 * (byte)gender;

        private void UnlockHeroAutoPot(bool value)
        {
            if (Hero == null) return;

            Hero.AutoPot = value;
            Ui.HeroAutoPotChanged();
        }

        private void SetAutoPotValue(S.SetAutoPotValue p)
        {
            if (Hero == null) return;

            if (p.Stat == Stat.HP)
                Hero.AutoHPPercent = p.Value;
            else
                Hero.AutoMPPercent = p.Value;

            Ui.HeroAutoPotChanged();
        }

        private void SetAutoPotItem(S.SetAutoPotItem p)
        {
            if (Hero == null) return;

            if (p.Grid == MirGridType.HeroHPItem)
                Hero.HPItem[0] = p.ItemIndex > 0 ? new UserItem(GetItemInfo(p.ItemIndex)) : null;
            else
                Hero.MPItem[0] = p.ItemIndex > 0 ? new UserItem(GetItemInfo(p.ItemIndex)) : null;
        }

        private void SetHeroBehaviour(S.SetHeroBehaviour p)
        {
            if (Hero == null) return;
            Ui.HeroBehaviourChanged(p.Behaviour);
        }

        private void NewHero(S.NewHero p)
        {
            Ui.NewHeroResult(p);
        }

        private void HeroInformation(S.HeroInformation p)
        {
            Hero = new UserHeroObject(p.ObjectID);
            Hero.Load(p);

            Hero.AutoPot = p.AutoPot;
            Hero.AutoHPPercent = p.AutoHPPercent;
            Hero.AutoMPPercent = p.AutoMPPercent;

            if (p.HPItemIndex > 0)
                Hero.HPItem[0] = new UserItem(GetItemInfo(p.HPItemIndex));
            if (p.MPItemIndex > 0)
                Hero.MPItem[0] = new UserItem(GetItemInfo(p.MPItemIndex));

            // A new hero starts with no buffs and no slot locks (the old code got a fresh list with the new HeroBuffsDialog and
            // fresh unlocked cells with the new hero dialogs).
            Models.HeroBuffs.Items.Clear();
            Models.HeroBuffs.Changed.Emit();
            ItemMoves.ClearHeroLocks();
            Ui.HeroInformationReceived();

            Hero.RefreshStats();
        }

        private void UpdateHeroSpawnState(S.UpdateHeroSpawnState p)
        {
            HeroSpawnState = p.State;

            HasHero = p.State > HeroSpawnState.None;
            if (p.State < HeroSpawnState.Summoned)
                ItemMoves.ClearHeroLocks(); // the classic hero cells (and their locks) were disposed here
            Ui.HeroSpawnStateChanged(p.State);
        }

        private void MarriageRequest(S.MarriageRequest p)
        {
            Ui.MarriageRequest(p);
        }

        private void DivorceRequest(S.DivorceRequest p)
        {
            Ui.DivorceRequest(p);
        }

        private void MentorRequest(S.MentorRequest p)
        {
            Ui.MentorRequest(p);
        }

        private bool UpdateGuildBuff(GuildBuff buff, bool Remove = false)
        {
            List<GuildBuff> enabledBuffs = Models.Guild.EnabledBuffs;
            for (int i = 0; i < enabledBuffs.Count; i++)
            {
                if (enabledBuffs[i].Id == buff.Id)
                {
                    if (Remove)
                    {
                        enabledBuffs.RemoveAt(i);
                    }
                    else
                        enabledBuffs[i] = buff;
                    return true;
                }
            }
            return false;
        }

        private void GuildBuffList(S.GuildBuffList p)
        {
            //getting the list of all guildbuffs on server?
            GuildState guild = Models.Guild;
            if (p.GuildBuffs.Count > 0)
                guild.BuffInfos.Clear();
            for (int i = 0; i < p.GuildBuffs.Count; i++)
            {
                guild.BuffInfos.Add(p.GuildBuffs[i]);
            }
            //getting the list of all active/removedbuffs?
            for (int i = 0; i < p.ActiveBuffs.Count; i++)
            {
                //if (p.ActiveBuffs[i].ActiveTimeRemaining > 0)
                //    p.ActiveBuffs[i].ActiveTimeRemaining = Convert.ToInt32(CMain.Time / 1000) + (p.ActiveBuffs[i].ActiveTimeRemaining * 60);
                if (UpdateGuildBuff(p.ActiveBuffs[i], p.Remove == 1)) continue;
                if (!(p.Remove == 1))
                {
                    guild.EnabledBuffs.Add(p.ActiveBuffs[i]);
                    //CreateGuildBuff(p.ActiveBuffs[i]);
                }
            }

            for (int i = 0; i < guild.EnabledBuffs.Count; i++)
            {
                if (guild.EnabledBuffs[i].Info == null)
                {
                    guild.EnabledBuffs[i].Info = guild.FindBuffInfo(guild.EnabledBuffs[i].Id);
                }
            }
            guild.BuffsChanged.Emit();

            ClientBuff buff = Models.Buffs.Items.FirstOrDefault(e => e.Type == BuffType.Guild);

            if (guild.EnabledBuffs.Any(e => e.Active))
            {
                if (buff == null)
                {
                    buff = new ClientBuff { Type = BuffType.Guild, ObjectID = User.ObjectID, Caster = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.Guild), Infinite = true, Values = new int[0] };

                    Models.Buffs.Items.Add(buff);
                    Models.Buffs.Changed.Emit();
                    Ui.BuffAdded(false, buff);
                }

                Ui.GuildActiveStatsChanged();
            }
            else
            {
                RemoveBuff(new S.RemoveBuff { ObjectID = User.ObjectID, Type = BuffType.Guild });
            }

            User.RefreshStats();
        }

        private void TradeRequest(S.TradeRequest p)
        {
            Ui.TradeRequest(p);
        }
        private void TradeAccept(S.TradeAccept p)
        {
            Models.Trade.GuestName = p.Name;
            Models.Trade.Changed.Emit();
            Ui.TradeAccepted();
        }
        private void TradeGold(S.TradeGold p)
        {
            Models.Trade.GuestGold = p.Amount;
            User.TradeLocked = false; // the guest changed the offer: the user's side unlocks (was TradeDialog.ChangeLockState)
            Models.Trade.Changed.Emit();
            Ui.TradeGuestChanged();
        }
        private void TradeItem(S.TradeItem p)
        {
            Models.Trade.GuestItems = p.TradeItems;
            User.TradeLocked = false;
            Models.Trade.Changed.Emit();
            Ui.TradeGuestChanged();
        }
        private void TradeConfirm()
        {
            Models.Trade.Reset(User);
            Ui.TradeConfirmed();
        }
        private void TradeCancel(S.TradeCancel p)
        {
            if (p.Unlock)
            {
                User.TradeLocked = false;
                Models.Trade.Changed.Emit();
            }
            else
                Models.Trade.Reset(User);
            Ui.TradeCancelled(p.Unlock);
        }
        private void NPCAwakening()
        {
            Ui.NpcAwakening();
        }
        private void NPCDisassemble()
        {
            Ui.NpcDropPanel(PanelType.Disassemble);
        }
        private void NPCDowngrade()
        {
            Ui.NpcDropPanel(PanelType.Downgrade);
        }
        private void NPCReset()
        {
            Ui.NpcDropPanel(PanelType.Reset);
        }
        private void AwakeningNeedMaterials(S.AwakeningNeedMaterials p)
        {
            Ui.AwakeningNeedMaterials(p);
        }
        private void AwakeningLockedItem(S.AwakeningLockedItem p)
        {
            ItemSlotRef cell = ItemMoves.FindBag(p.UniqueID);
            if (cell != null)
                cell.Locked = p.Locked;
        }
        private void Awakening(S.Awakening p)
        {
            Ui.ItemAwakeningClosing();

            ItemSlotRef cell = ItemMoves.FindBag((ulong)p.removeID);
            if (cell != null)
            {
                cell.Locked = false;
                cell.Item = null;
            }

            //The bag slots (was InventoryDialog.Grid: Inventory[BeltSize..]).
            for (int i = UserObject.BeltSize; i < User.Inventory.Length; i++)
            {
                if (ItemMoves.IsLocked(MirGridType.Inventory, i) == true)
                {
                    ItemMoves.SetLocked(MirGridType.Inventory, i, false);

                    //if (InventoryDialog.Grid[i].Item.UniqueID == (ulong)p.removeID)
                    //{
                    //    InventoryDialog.Grid[i].Item = null;
                    //}
                }
            }

            for (int i = 0; i < Models.Npc.AwakeItemsIdx.Length; i++)
            {
                Models.Npc.AwakeItemsIdx[i] = 0;
            }

            switch (p.result)
            {
                case -4:
                case -3:
                case -2:
                case -1:
                    MapControl.AwakeningAction = false;
                    break;
            }

            Ui.ItemAwakeningResult(p.result);
        }

        // Mail of the S.ReceiveMail parts received so far (Part 0 starts it, the last part completes the box).
        private readonly List<ClientMail> _mailParts = new List<ClientMail>();

        private void ReceiveMail(S.ReceiveMail p)
        {
            if (p.Part == 0) _mailParts.Clear();
            _mailParts.AddRange(p.Mail);
            if (p.Part + 1 < p.PartCount) return;

            NewMail = false;
            NewMailCounter = 0;
            User.Mail.Clear();

            User.Mail = _mailParts.OrderByDescending(e => !e.Locked).ThenByDescending(e => e.DateSent).ToList();
            _mailParts.Clear();

            foreach (ClientMail mail in User.Mail)
            {
                foreach (UserItem itm in mail.Items)
                {
                    Bind(itm);
                }
            }

            //display new mail received
            if (User.Mail.Any(e => e.Opened == false))
            {
                NewMail = true;
            }

            Ui.MailReceived();
        }

        private void MailLockedItem(S.MailLockedItem p)
        {
            ItemSlotRef cell = ItemMoves.FindBag(p.UniqueID);
            if (cell != null)
                cell.Locked = p.Locked;
        }

        private void MailSendRequest(S.MailSendRequest p)
        {
            Ui.MailSendRequest();
        }

        private void MailSent(S.MailSent p)
        {
            //The bag slots, then the belt slots (were InventoryDialog.Grid and BeltDialog.Grid).
            for (int i = UserObject.BeltSize; i < User.Inventory.Length; i++)
            {
                if (ItemMoves.IsLocked(MirGridType.Inventory, i) == true)
                {
                    ItemMoves.SetLocked(MirGridType.Inventory, i, false);
                }
            }

            for (int i = 0; i < UserObject.BeltSize; i++)
            {
                if (ItemMoves.IsLocked(MirGridType.Inventory, i) == true)
                {
                    ItemMoves.SetLocked(MirGridType.Inventory, i, false);
                }
            }

            Ui.ItemMailSent(p.Result);
        }

        private void ParcelCollected(S.ParcelCollected p)
        {
            Ui.ParcelCollected(p.Result);
        }

        private void ResizeInventory(S.ResizeInventory p)
        {
            Array.Resize(ref User.Inventory, p.Size);
            Ui.InventoryResized();
        }

        private void ResizeStorage(S.ResizeStorage p)
        {
            Array.Resize(ref Storage, p.Size);
            User.HasExpandedStorage = p.HasExpandedStorage;
            User.ExpandedStorageExpiryTime = p.ExpiryTime;

            Ui.StorageResized();
        }

        private void MailCost(S.MailCost p)
        {
            Ui.MailCost(p.Cost);
        }

        public void AddQuestItem(UserItem item)
        {
            Redraw();

            if (item.Info.StackSize > 1) //Stackable
            {
                for (int i = 0; i < User.QuestInventory.Length; i++)
                {
                    UserItem temp = User.QuestInventory[i];
                    if (temp == null || item.Info != temp.Info || temp.Count >= temp.Info.StackSize) continue;

                    if (item.Count + temp.Count <= temp.Info.StackSize)
                    {
                        temp.Count += item.Count;
                        return;
                    }
                    item.Count -= (ushort)(temp.Info.StackSize - temp.Count);
                    temp.Count = temp.Info.StackSize;
                }
            }

            for (int i = 0; i < User.QuestInventory.Length; i++)
            {
                if (User.QuestInventory[i] != null) continue;
                User.QuestInventory[i] = item;
                return;
            }
        }

        private void RequestReincarnation()
        {
            if (CMain.Time > User.DeadTime && User.CurrentAction == MirAction.Dead)
            {
                Ui.ReincarnationRequest();
            }
        }

        private void NewIntelligentCreature(S.NewIntelligentCreature p)
        {
            User.IntelligentCreatures.Add(p.Creature);

            Ui.NewIntelligentCreature();
        }

        private void UpdateIntelligentCreatureList(S.UpdateIntelligentCreatureList p)
        {
            User.CreatureSummoned = p.CreatureSummoned;
            User.SummonedCreatureType = p.SummonedCreatureType;
            User.PearlCount = p.PearlCount;
            if (p.CreatureList.Count != User.IntelligentCreatures.Count)
            {
                User.IntelligentCreatures.Clear();
                for (int i = 0; i < p.CreatureList.Count; i++)
                    User.IntelligentCreatures.Add(p.CreatureList[i]);

                Ui.IntelligentCreaturesChanged(true);
            }
            else
            {
                for (int i = 0; i < p.CreatureList.Count; i++)
                    User.IntelligentCreatures[i] = p.CreatureList[i];
                Ui.IntelligentCreaturesChanged(false);
            }
        }

        private void IntelligentCreatureEnableRename(S.IntelligentCreatureEnableRename p)
        {
            Ui.IntelligentCreatureEnableRename();
        }

        private void IntelligentCreaturePickup(S.IntelligentCreaturePickup p)
        {
            if (MapControl.Objects.TryGetValue(p.ObjectID, out MapObject ob) && ob is MonsterObject monOb)
                monOb.PlayPickupSound();
        }


        private void FriendUpdate(S.FriendUpdate p)
        {
            Models.Friends.Set(p.Friends);

            Ui.FriendsChanged();
        }

        private void LoverUpdate(S.LoverUpdate p)
        {
            Models.Relationship.ApplyLover(p);
            Ui.LoverUpdate(p);
        }

        private void MentorUpdate(S.MentorUpdate p)
        {
            Models.Relationship.ApplyMentor(p);
            Ui.MentorUpdate(p);
        }

        private void GameShopUpdate(S.GameShopInfo p)
        {
            p.Item.Stock = p.StockLevel;
            GameShopInfoList.Add(p.Item);
            if (p.Item.Date > CMain.Now.AddDays(-7)) Ui.GameShopNewItems();
        }

        private void GameShopStock(S.GameShopStock p)
        {
            for (int i = 0; i < GameShopInfoList.Count; i++)
            {
                if (GameShopInfoList[i].GIndex == p.GIndex)
                {
                    if (p.StockLevel == 0) GameShopInfoList.Remove(GameShopInfoList[i]);
                    else GameShopInfoList[i].Stock = p.StockLevel;

                    Ui.GameShopStockChanged();
                }
            }
        }
        public void AddItem(UserItem item)
        {
            Redraw();

            if (item.Info.StackSize > 1) //Stackable
            {
                for (int i = 0; i < User.Inventory.Length; i++)
                {
                    UserItem temp = User.Inventory[i];
                    if (temp == null || item.Info != temp.Info || temp.Count >= temp.Info.StackSize) continue;

                    if (item.Count + temp.Count <= temp.Info.StackSize)
                    {
                        temp.Count += item.Count;
                        return;
                    }
                    item.Count -= (ushort)(temp.Info.StackSize - temp.Count);
                    temp.Count = temp.Info.StackSize;
                }
            }

            if (item.Info.Type == ItemType.Potion || item.Info.Type == ItemType.Scroll || (item.Info.Type == ItemType.Script && item.Info.Effect == 1))
            {
                for (int i = 0; i < UserObject.PotionBeltMaximum; i++)
                {
                    if (User.Inventory[i] != null) continue;
                    User.Inventory[i] = item;
                    return;
                }
            }
            else if (item.Info.Type == ItemType.Amulet)
            {
                for (int i = UserObject.AmuletBeltMinimum; i < UserObject.AmuletBeltMaximum; i++)
                {
                    if (User.Inventory[i] != null) continue;
                    User.Inventory[i] = item;
                    return;
                }
            }
            else
            {
                for (int i = User.BeltIdx; i < User.Inventory.Length; i++)
                {
                    if (User.Inventory[i] != null) continue;
                    User.Inventory[i] = item;
                    return;
                }
            }

            for (int i = 0; i < User.Inventory.Length; i++)
            {
                if (User.Inventory[i] != null) continue;
                User.Inventory[i] = item;
                return;
            }
        }
        public static void Bind(UserItem item)
        {
            for (int i = 0; i < ItemInfoList.Count; i++)
            {
                if (ItemInfoList[i].Index != item.ItemIndex) continue;

                item.Info = ItemInfoList[i];

                for (int s = 0; s < item.Slots.Length; s++)
                {
                    if (item.Slots[s] == null) continue;

                    Bind(item.Slots[s]);
                }

                return;
            }
        }

        public ItemInfo GetItemInfo(int index)
        {
            return ItemInfoList.Find(x => x.Index == index);
        }

        public Color GradeNameColor(ItemGrade grade)
        {
            switch (grade)
            {
                case ItemGrade.Common:
                    return Color.Yellow;
                case ItemGrade.Rare:
                    return Color.DeepSkyBlue;
                case ItemGrade.Legendary:
                    return Color.DarkOrange;
                case ItemGrade.Mythical:
                    return Color.Plum;
                case ItemGrade.Heroic:
                    return Color.Red;
                default:
                    return Color.Yellow;
            }
        }

        public string GetUserName(uint id)
        {
            for (int i = 0; i < UserIdList.Count; i++)
            {
                UserId who = UserIdList[i];
                if (id == who.Id)
                    return who.UserName;
            }
            Network.Enqueue(new C.RequestUserName { UserID = id });
            UserIdList.Add(new UserId() { Id = id, UserName = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UserNameUnknown) });
            return String.Empty;
        }

        public class UserId
        {
            public long Id = 0;
            public string UserName = String.Empty;
        }

        public class OutPutMessage
        {
            public string Message;
            public long ExpireTime;
            public OutputMessageType Type;
        }

        public void Rankings(S.Rankings p)
        {
            foreach (RankCharacterInfo info in p.ListingDetails)
            {
                if (RankingList.ContainsKey(info.PlayerId))
                    RankingList[info.PlayerId] = info;
                else
                    RankingList.Add(info.PlayerId, info);
            }
            List<RankCharacterInfo> listings = new List<RankCharacterInfo>();
            foreach (long id in p.Listings)
                listings.Add(RankingList[id]);

            Models.Rankings.ApplyRankings(p, listings);
            Ui.RankingsReceived(listings, p);
        }

        public void Opendoor(S.Opendoor p)
        {
            MapControl.OpenDoor(p.DoorIndex, p.Close);
        }

        private void RentedItems(S.GetRentedItems p)
        {
            Ui.RentedItems(p);
        }

        private void ItemRentalRequest(S.ItemRentalRequest p)
        {
            Ui.ItemRentalRequest(p);
        }

        private void ItemRentalFee(S.ItemRentalFee p)
        {
            Ui.ItemRentalFee(p);
        }

        private void ItemRentalPeriod(S.ItemRentalPeriod p)
        {
            Ui.ItemRentalPeriod(p);
        }

        private void DepositRentalItem(S.DepositRentalItem p)
        {
            var fromCell = ItemMoves.Slot(MirGridType.Inventory, p.From);
            var toCell = new ItemSlotRef(MirGridType.Renting, 0);

            if (toCell == null || fromCell == null)
                return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success)
                return;

            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            User.RefreshStats();

            Ui.ItemRentalDeposited();
        }

        private void RetrieveRentalItem(S.RetrieveRentalItem p)
        {
            var fromCell = new ItemSlotRef(MirGridType.Renting, 0);
            var toCell = ItemMoves.Slot(MirGridType.Inventory, p.To);

            if (toCell == null || fromCell == null)
                return;

            toCell.Locked = false;
            fromCell.Locked = false;

            if (!p.Success)
                return;

            toCell.Item = fromCell.Item;
            fromCell.Item = null;
            User.RefreshStats();
        }

        private void UpdateRentalItem(S.UpdateRentalItem p)
        {
            Models.Rental.GuestLoanItem = p.LoanItem;
            Models.Rental.Changed.Emit();
            Ui.RentalGuestItemChanged();
        }

        private void CancelItemRental(S.CancelItemRental p)
        {
            User.RentalGoldLocked = false;
            User.RentalItemLocked = false;

            Ui.ItemRentalReset(true);
        }

        private void ItemRentalLock(S.ItemRentalLock p)
        {
            if (!p.Success)
                return;

            User.RentalGoldLocked = p.GoldLocked;
            User.RentalItemLocked = p.ItemLocked;

            Ui.ItemRentalLocked();
        }

        private void ItemRentalPartnerLock(S.ItemRentalPartnerLock p)
        {
            Ui.ItemRentalPartnerLock(p);
        }

        private void CanConfirmItemRental(S.CanConfirmItemRental p)
        {
            Ui.CanConfirmItemRental();
        }

        private void ConfirmItemRental(S.ConfirmItemRental p)
        {
            User.RentalGoldLocked = false;
            User.RentalItemLocked = false;

            Ui.ItemRentalReset(false);
        }

        private void OpenBrowser(S.OpenBrowser p)
        {
            BrowserHelper.OpenDefaultBrowser(p.Url);
        }

        public void PlaySound(S.PlaySound p)
        {
            SoundManager.PlaySound(p.Sound, false);
        }
        private void SetTimer(S.SetTimer p)
        {
            Ui.SetTimer(p);
        }

        private void ExpireTimer(S.ExpireTimer p)
        {
            Ui.ExpireTimer(p.Key);
        }

        private void SetCompass(S.SetCompass p)
        {
            Ui.SetCompass(p.Location);
        }

        private void Roll(S.Roll p)
        {
            Ui.Roll(p);
        }

        public void ShowNotice(S.UpdateNotice p)
        {
            Ui.ShowNotice(p);
        }

        #region Disposable

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Scene = null;
                User = null;

                MoveTime = 0;
                AttackTime = 0;
                NextRunTime = 0;
                LastRunTime = 0;
                CanMove = false;
                CanRun = false;

                MapControl = null;
                _ui?.Dispose();

                HoverItem = null;
                ItemMoves.Reset();
                PickedUpGold = false;

                UseItemTime = 0;
                PickUpTime = 0;
                InspectTime = 0;

                DisposeItemLabel();

                AMode = 0;
                PMode = 0;
                Lights = 0;

                NPCTime = 0;
                NPCID = 0;
                TalkedNPCID = 0;
                DefaultNPCID = 0;

                OutputMessages.Clear();
                OutputMessages = null;
            }

            base.Dispose(disposing);

            if (disposing)
            {
                // Only this scene's own UI and models: a newer GameScene may already have set the statics.
                if (Ui == _ui) Ui = null;
                if (Models == _models) Models = null;
                _ui = null;
                _models = null;
            }
        }

        #endregion

    }

    public sealed partial class MapControl : MirControl
    {
        public static UserObject User
        {
            get { return MapObject.User; }
            set { MapObject.User = value; }
        }

        public static UserHeroObject Hero
        {
            get { return MapObject.Hero; }
            set { MapObject.Hero = value; }
        }

        public static Dictionary<uint, MapObject> Objects = new Dictionary<uint, MapObject>();
        public static List<MapObject> ObjectsList = new List<MapObject>();

        public const int CellWidth = 48;
        public const int CellHeight = 32;

        public static int OffSetX;
        public static int OffSetY;

        public static int ViewRangeX;
        public static int ViewRangeY;

        private bool _autoPath;
        public bool AutoPath
        {
            get
            {
                return _autoPath;
            }
            set
            {
                if (_autoPath == value) return;
                _autoPath = value;

                if (!_autoPath)
                    CurrentPath = null;
            }
        }

        public PathFinder PathFinder;
        public List<Node> CurrentPath = null;

        public static Point MapLocation
        {
            get { return GameScene.User == null ? Point.Empty : new Point(MouseLocation.X / CellWidth - OffSetX, MouseLocation.Y / CellHeight - OffSetY).Add(GameScene.User.CurrentLocation); }
        }

        public static Point ToMouseLocation(Point p)
        {
            return new Point((p.X - MapObject.User.Movement.X + OffSetX) * CellWidth, (p.Y - MapObject.User.Movement.Y + OffSetY) * CellHeight).Add(MapObject.User.OffSetMove);
        }

        public static MouseButtons MapButtons;
        public static Point MouseLocation;
        public static long InputDelay;

        private static long nextAction;
        public static long NextAction
        {
            get { return nextAction; }
            set
            {
                if (GameScene.Observing) return;
                nextAction = value;
            }
        }

        public CellInfo[,] M2CellInfo;
        public List<Door> Doors = new List<Door>();
        public int Width, Height;
        /// <summary>In-game patcher: the .map is downloading; the blank fallback map is shown and movement input ignored until <see cref="ReloadCells"/>.</summary>
        public bool WaitingForFile;

        public int Index;
        public string FileName = String.Empty;
        public string Title = String.Empty;
        public ushort MiniMap, BigMap, Music, SetMusic;
        public LightSetting Lights;
        public bool Lightning, Fire;
        public byte MapDarkLight;
        public long LightningTime, FireTime;
        public WeatherSetting Weather = WeatherSetting.None;
        public bool FloorValid, LightsValid;

        // Rebuild profiler shim: routes every map invalidation through FrameProfiler so it can count it and, in DebugMode,
        // record the code that caused it. The flag itself stays the base MirControl field.
        internal override bool DrawsInParentTexture => false; // Draw() is a no-op; the map is drawn by GameScene.DrawControl

        public new bool TextureValid
        {
            get { return base.TextureValid; }
            set
            {
                if (!value) FrameProfiler.NoteInvalidation(RebuildTarget.Map, base.TextureValid);
                base.TextureValid = value;
            }
        }

        #region Rebuild optimisation (Settings.RebuildOptimisations)

        // Cells beyond the DrawObjects range (ViewRangeX/Y, +25 rows below) in which a change can still reach the map texture:
        // sprite overhang for plain objects, light radius for anything with a light (largest light ~10x11 cells from its source).
        public const int ViewMargin = 2, LightViewMargin = 6;

        private bool _mouseMovedOverMap;
        private long _lastTimedTick;
        private int _settingsSignature;
        private CellInfo[,] _animatedScanMap;
        private Point _animatedScanCentre;

        /// <summary>True if something at <paramref name="location"/> can appear in the map texture (DrawObjects range + margin).</summary>
        public static bool IsNearView(Point location, int margin)
        {
            if (User == null) return true;
            int dx = location.X - User.Movement.X, dy = location.Y - User.Movement.Y;
            return dx >= -ViewRangeX - margin && dx <= ViewRangeX + margin && dy >= -ViewRangeY - margin && dy <= ViewRangeY + 25 + margin;
        }

        /// <summary>Invalidates the map texture for a change at <paramref name="location"/>; off-screen changes are skipped when optimising.</summary>
        public void InvalidateNear(Point location, int margin)
        {
            if (!Settings.RebuildOptimisations || IsNearView(location, margin))
                TextureValid = false;
        }

        /// <summary>
        /// Called on the 100 ms tick. Event-driven changes invalidate where they happen; this covers what is drawn from time
        /// alone: animated tiles, weather, damage numbers, hover names, chat/health/drop-view expiry, the blindness ramp, and
        /// settings or lighting changes made outside the map code.
        /// </summary>
        public bool NeedsTimedRebuild()
        {
            long now = CMain.Time, last = _lastTimedTick;
            _lastTimedTick = now;

            // Every reason that applies is counted (FrameProfiler "tick" line), so the report shows what keeps the tick busy.
            bool rebuild = false;

            if (_mouseMovedOverMap) rebuild = Reason("mouse"); // hover names and pixel hit-tests follow the mouse at tick granularity
            _mouseMovedOverMap = false;

            int signature = DrawSettingsSignature();
            if (signature != _settingsSignature)
            {
                _settingsSignature = signature;
                rebuild = Reason("settings");
            }

            if (User == null) return Reason("no-user");
            if (GameScene.Scene.ParticleEngines.Count > 0) rebuild = Reason("weather");
            if (GameScene.DropViewTime > last && GameScene.DropViewTime <= now) rebuild = Reason("drop-view");
            if (User.Poison.HasFlag(PoisonType.Blindness) && User.BlindCount < 25) rebuild = Reason("blindness"); // ramp advances in DrawLights
            if (AnimatedTileFrameChanged()) rebuild = Reason("tiles");
            if (EnhancedLightingNeedsRebuild(out string lightReason)) rebuild = Reason(lightReason); // flicker, day/night fade
            if (AtmosphereNeedsRebuild(out string wetReason)) rebuild = Reason(wetReason); // baked wet ground at 10 Hz

            bool damage = false, chat = false, health = false;
            for (int i = 0; i < ObjectsList.Count; i++)
            {
                MapObject ob = ObjectsList[i];
                if (!IsNearView(ob.CurrentLocation, ViewMargin)) continue;
                damage |= ob.Damages.Count > 0; // numbers drift with time and are removed while drawing
                chat |= ob.ChatLabel != null && ob.ChatTime >= last && ob.ChatTime < now; // DrawChat drops it after ChatTime
                health |= ob.HealthTime > last && ob.HealthTime <= now; // DrawHealth hides it from HealthTime
            }
            if (damage) rebuild = Reason("damage");
            if (chat) rebuild = Reason("chat-expiry");
            if (health) rebuild = Reason("health-expiry");

            if (!rebuild) FrameProfiler.NoteTickReason("skipped");
            return rebuild;
        }

        private static bool Reason(string reason)
        {
            FrameProfiler.NoteTickReason(reason);
            return true;
        }

        private int DrawSettingsSignature()
        {
            int s = (Settings.Effect ? 1 : 0) | (Settings.LevelEffect ? 2 : 0) | (Settings.DropView ? 4 : 0) | (Settings.NameView ? 8 : 0) |
                    (Settings.HighlightTarget ? 16 : 0) | (Settings.DisplayBodyName ? 32 : 0) | (Settings.SmoothMove ? 64 : 0) | (Settings.Atmosphere ? 128 : 0);
            s = s * 31 + (int)GameScene.Scene.Lights;
            s = s * 31 + (int)Lights;
            s = s * 31 + MapDarkLight;
            s = s * 31 + (UseEnhancedLighting ? 1 : 0);
            return s * 31 + (User != null ? (int)User.Poison : 0);
        }

        // Distinct (frames, tick) animation timings of the animated tiles in the DrawObjects range, packed as frames << 8 | tick;
        // rescanned when the map or the view centre changes.
        private readonly List<int> _animatedTimings = new List<int>();

        /// <summary>
        /// True if an animated tile in the DrawObjects range shows a different frame at this AnimationCount than at the previous
        /// one. DrawObjects picks the frame as (AnimationCount % (frames + frames * tick)) / (1 + tick) (the shanda tile layer is
        /// the tick = 0 case), so slow animations change only every (1 + tick) counts and one-frame "animations" never change.
        /// </summary>
        private bool AnimatedTileFrameChanged()
        {
            if (M2CellInfo == null) return false;

            if (!ReferenceEquals(_animatedScanMap, M2CellInfo) || _animatedScanCentre != User.Movement)
            {
                _animatedScanMap = M2CellInfo;
                _animatedScanCentre = User.Movement;
                _animatedTimings.Clear();

                for (int y = Math.Max(1, User.Movement.Y - ViewRangeY); y <= User.Movement.Y + ViewRangeY + 25 && y < Height; y++)
                    for (int x = Math.Max(0, User.Movement.X - ViewRangeX); x <= User.Movement.X + ViewRangeX && x < Width; x++)
                    {
                        CellInfo cell = M2CellInfo[x, y];

                        if (cell.TileAnimationImage > 0 && cell.TileAnimationFrames > 0)
                            AddAnimatedTiming(cell.TileAnimationFrames, 0);

                        if (cell.MiddleIndex >= 0 && cell.MiddleImage > 1 && cell.MiddleAnimationFrame > 0 && cell.MiddleAnimationFrame < 255)
                        {
                            int frames = cell.MiddleAnimationFrame;
                            if ((frames & 0x0F) > 0) frames &= 0x0F; // blended middle tiles keep the frame count in the low nibble
                            AddAnimatedTiming(frames, cell.MiddleAnimationTick);
                        }

                        if ((cell.FrontImage & 0x7FFF) > 0 && cell.FrontIndex != -1 && (cell.FrontAnimationFrame & 0x7F) > 0)
                            AddAnimatedTiming(cell.FrontAnimationFrame & 0x7F, cell.FrontAnimationTick);
                    }
            }

            for (int i = 0; i < _animatedTimings.Count; i++)
            {
                int frames = _animatedTimings[i] >> 8, tick = _animatedTimings[i] & 0xFF;
                int period = frames + frames * tick;
                if ((AnimationCount % period) / (1 + tick) != ((AnimationCount - 1) % period) / (1 + tick)) return true;
            }

            return false;
        }

        private void AddAnimatedTiming(int frames, int tick)
        {
            int key = frames << 8 | tick;
            if (!_animatedTimings.Contains(key)) _animatedTimings.Add(key);
        }

        #endregion

        public long OutputDelay;

        private static bool _awakeningAction;
        public static bool AwakeningAction
        {
            get { return _awakeningAction; }
            set
            {
                if (_awakeningAction == value) return;
                _awakeningAction = value;
            }
        }

        private static bool _autoRun;
        public static bool AutoRun
        {
            get { return _autoRun; }
            set
            {
                if (_autoRun == value) return;
                _autoRun = value;
                if (GameScene.Scene != null)
                    GameScene.Models.Chat.Add(value ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AutoRunOn) : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AutoRunOff),
                        ChatType.Hint);
            }

        }
        public static bool AutoHit;

        public int AnimationCount;

        public static List<Effect> Effects = new List<Effect>();

        public MapControl()
        {
            MapButtons = MouseButtons.None;

            OffSetX = Settings.ScreenWidth / 2 / CellWidth;
            OffSetY = Settings.ScreenHeight / 2 / CellHeight - 1;

            ViewRangeX = OffSetX + 6;
            ViewRangeY = OffSetY + 6;

            Size = new Size(Settings.ScreenWidth, Settings.ScreenHeight);
            DrawControlTexture = true;
            BackColour = Color.Black;

            MouseDown += OnMouseDown;
            MouseMove += (o, e) =>
            {
                MouseLocation = e.Location;
                _mouseMovedOverMap = true;
            };
            Click += OnMouseClick;
        }

        public void ResetMap()
        {
            GameScene.Ui.CloseNpcDialog();

            MapObject.MouseObjectID = 0;
            MapObject.TargetObjectID = 0;
            MapObject.MagicObjectID = 0;

            if (M2CellInfo != null)
                for (var i = ObjectsList.Count - 1; i >= 0; i--)
                    ObjectsList[i]?.Remove();

            Objects.Clear();
            ObjectsList.Clear();
            Effects.Clear();
            Doors.Clear();

            if (User != null)
            {
                Objects[User.ObjectID] = User;
                ObjectsList.Add(User);
            }
        }

        public void LoadMap()
        {
            ResetMap();

            MapObject.MouseObjectID = 0;
            MapObject.TargetObjectID = 0;
            MapObject.MagicObjectID = 0;

            // A generated dungeon arrives in packets before the map change (GeneratedMaps); it has no file to patch.
            bool generated = GeneratedMaps.TryGet(FileName, out var generatedBytes);

            // In-game patcher: a missing map keeps the blank fallback and is demanded (the file check only on a map change).
            WaitingForFile = !generated && PatchService.Active && !File.Exists(FileName) && PatchService.Demand(FileName);

            MapReader Map = generated ? new MapReader(FileName, generatedBytes) : new MapReader(FileName);
            M2CellInfo = Map.MapCells;
            Width = Map.Width;
            Height = Map.Height;

            PathFinder = new PathFinder(this);

            // A generated dungeon has no MMap.Lib art: its minimap / big map picture is drawn from the cells, once.
            if (generated) GeneratedMapPicture.Build(this);
            else GeneratedMapPicture.Clear();

            try
            {
                if (SetMusic != Music)
                {
                    SoundManager.Music?.Dispose();
                    SoundManager.PlayMusic(Music, true);
                }
            }
            catch (Exception)
            {
                // Do nothing. index was not valid.
            }

            SetMusic = Music;
            SoundList.Music = Music;

            UpdateWeather();

            // In-game patcher: ask for this map's tile / object libraries now, nearest first (not at their first draw).
            if (PatchService.Active) PatchPickup.PrefetchMap(this);
        }


        /// <summary>
        /// In-game patcher (UI thread, from <see cref="PatchPickup"/>): the current .map was installed. Swap the cells in
        /// place instead of LoadMap, whose ResetMap would drop every object: new cells, size and PathFinder, doors cleared
        /// as ResetMap does, the live objects re-added to their cells, and the floor, lights and map texture rebuilt. The
        /// lighting, atmosphere and animation caches key on ReferenceEquals(M2CellInfo), so they rebuild themselves.
        /// </summary>
        public void ReloadCells()
        {
            WaitingForFile = false; // first: a bad file must not leave movement blocked
            MapReader Map = new MapReader(FileName);
            M2CellInfo = Map.MapCells;
            Width = Map.Width;
            Height = Map.Height;
            PathFinder = new PathFinder(this);
            Doors.Clear();

            for (int i = 0; i < ObjectsList.Count; i++)
            {
                MapObject ob = ObjectsList[i];
                if (ob == null || ob.MapLocation.X < 0 || ob.MapLocation.Y < 0 || ob.MapLocation.X >= Width || ob.MapLocation.Y >= Height) continue;
                M2CellInfo[ob.MapLocation.X, ob.MapLocation.Y].AddObject(ob);
            }

            FloorValid = false;
            LightsValid = false;
            TextureValid = false;
            PatchPickup.PrefetchMap(this);
        }

        public void Process()
        {
            Processdoors();
            User.Process();
            for (int i = ObjectsList.Count - 1; i >= 0; i--)
            {
                if (ObjectsList[i] == User) continue;
                ObjectsList[i].Process();
            }

            for (int i = Effects.Count - 1; i >= 0; i--)
                Effects[i].Process();

            AtmosphereProcess();

            if (MapObject.TargetObject != null && MapObject.TargetObject is MonsterObject && MapObject.TargetObject.AI == 64)
                MapObject.TargetObjectID = 0;
            if (MapObject.MagicObject != null && MapObject.MagicObject is MonsterObject && MapObject.MagicObject.AI == 64)
                MapObject.MagicObjectID = 0;

            if (!WaitingForFile) CheckInput();

            MapObject bestmouseobject = null;
            for (int y = MapLocation.Y + 2; y >= MapLocation.Y - 2; y--)
            {
                if (y >= Height) continue;
                if (y < 0) break;
                for (int x = MapLocation.X + 2; x >= MapLocation.X - 2; x--)
                {
                    if (x >= Width) continue;
                    if (x < 0) break;
                    CellInfo cell = M2CellInfo[x, y];
                    if (cell.CellObjects == null) continue;

                    for (int i = cell.CellObjects.Count - 1; i >= 0; i--)
                    {
                        MapObject ob = cell.CellObjects[i];
                        if (ob == MapObject.User || !ob.MouseOver(CMain.MPoint)) continue;

                        if (MapObject.MouseObject != ob)
                        {
                            if (ob.Dead)
                            {
                                if (!Settings.TargetDead && GameScene.TargetDeadTime <= CMain.Time) continue;

                                bestmouseobject = ob;
                                //continue;
                            }
                            MapObject.MouseObjectID = ob.ObjectID;
                            if (!Settings.RebuildOptimisations) Redraw();
                        }
                        if (bestmouseobject != null && MapObject.MouseObject == null)
                        {
                            MapObject.MouseObjectID = bestmouseobject.ObjectID;
                            if (!Settings.RebuildOptimisations) Redraw();
                        }
                        return;
                    }
                }
            }

            if (MapObject.MouseObject != null)
            {
                MapObject.MouseObjectID = 0;
                if (!Settings.RebuildOptimisations) Redraw(); // the setter invalidates the map; nothing in the UI shows it
            }
        }

        public static MapObject GetObject(uint targetID)
        {
            Objects.TryGetValue(targetID, out var ob);
            return ob;
        }

        public override void Draw()
        {
            //Do nothing.
        }

        protected override void CreateTexture()
        {
            if (User == null) return;

            if (!FloorValid)
            {
                long floorStart = FrameProfiler.Now();
                int floorDraws = CMain.DPSCounter;
                DrawFloor();
                FrameProfiler.AddRebuild(RebuildTarget.Floor, floorStart, floorDraws);
            }


            if (Size != TextureSize)
                DisposeTexture();

            if (ControlTexture == null || ControlTexture.Disposed)
            {
                DXManager.ControlList.Add(this);
                ControlTexture = DXManager.CreateRenderTarget(Size.Width, Size.Height);
                TextureSize = Size;
            }

            RenderTexture oldSurface = DXManager.CurrentSurface;
            DXManager.SetSurface(ControlTexture);
            DXManager.Clear(BackColour);

            DrawBackground();

            if (FloorValid)
            {
                DXManager.Draw(DXManager.FloorTexture, new Rectangle(0, 0, Settings.ScreenWidth, Settings.ScreenHeight), Vector3.Zero, Color.White);
            }

            // Wet ground goes under the objects: puddles/ripples/splashes, footfall ripples, then the reflectivity alpha pass.
            DrawAtmosphereGround();

            DrawObjects();

            //render weather
            foreach (ParticleEngine engine in GameScene.Scene.ParticleEngines)
            {
                engine.Draw();
            }

            // The object/weather pixels have raised the scene alpha: snapshot it to tell wet floor from what stands on it.
            DrawAtmosphereSnapshot();

            //Render Death, 

            LightSetting setting = Lights == LightSetting.Normal ? GameScene.Scene.Lights : Lights;

            if (UseEnhancedLighting)
            {
                UpdateAmbient(setting);
                _atmosphereLightDrawn = setting != LightSetting.Day || AmbientFading || GameScene.User.Poison.HasFlag(PoisonType.Blindness);
                if (_atmosphereLightDrawn)
                    DrawEnhancedLights(setting);
            }
            else if (setting != LightSetting.Day || GameScene.User.Poison.HasFlag(PoisonType.Blindness))
            {
                DrawLights(setting);
            }

            if (Settings.DropView || GameScene.DropViewTime > CMain.Time)
            {
                foreach (var ob in Objects.Values.OfType<ItemObject>())
                {
                    if (!ob.MouseOver(MouseLocation))
                        ob.DrawName();
                }
            }

            if (MapObject.MouseObject != null && !(MapObject.MouseObject is ItemObject))
                MapObject.MouseObject.DrawName();

            int offSet = 0;

            if (Settings.DisplayBodyName)
            {
                foreach (var ob in Objects.Values.OfType<MonsterObject>())
                {
                    if (ob.MouseOver(MouseLocation))
                        ob.DrawName();
                }
            }

            foreach (var ob in Objects.Values.OfType<ItemObject>())
            {
                if (ob.MouseOver(MouseLocation))
                {
                    ob.DrawName(offSet);
                    offSet -= ob.NameLabel.Size.Height + (ob.NameLabel.Border ? 1 : 0);
                }
            }

            if (MapObject.User.MouseOver(MouseLocation))
                MapObject.User.DrawName();

            DXManager.SetSurface(oldSurface);
            TextureValid = true;

        }
        protected internal override void DrawControl()
        {
            if (!DrawControlTexture)
                return;

            if (!TextureValid)
            {
                long rebuildStart = FrameProfiler.Now();
                int rebuildDraws = CMain.DPSCounter;
                CreateTexture();
                FrameProfiler.AddRebuild(RebuildTarget.Map, rebuildStart, rebuildDraws);
            }

            if (ControlTexture == null || ControlTexture.Disposed)
                return;

            float oldOpacity = DXManager.Opacity;

            if (MapObject.User.Dead) DXManager.SetGrayscale(true);

            DXManager.DrawOpaque(ControlTexture, new Rectangle(0, 0, Settings.ScreenWidth, Settings.ScreenHeight), Vector3.Zero, Color.White, Opacity);

            if (MapObject.User.Dead) DXManager.SetGrayscale(false);

            // Enhanced weather: rain and the lightning strobe over the cached map, every frame (never baked into it).
            DrawAtmosphereOverlay();

            CleanTime = CMain.Time + Settings.CleanDelay;
        }

        private void DrawFloor()
        {
            if (DXManager.FloorTexture == null || DXManager.FloorTexture.Disposed)
            {
                DXManager.FloorTexture = DXManager.CreateRenderTarget(Settings.ScreenWidth, Settings.ScreenHeight);
            }

            RenderTexture oldSurface = DXManager.CurrentSurface;
            DXManager.SetSurface(DXManager.FloorTexture);
            DXManager.Clear(Color.Empty);


            int startX = User.Movement.X - ViewRangeX;
            int endX = User.Movement.X + ViewRangeX;
            int startY = User.Movement.Y - ViewRangeY;
            int endY = User.Movement.Y + ViewRangeY;
            int endYExtended = endY + 5;


            int[] drawXCache = new int[endX - startX + 1];
            for (int xi = startX; xi <= endX; xi++)
                drawXCache[xi - startX] = (xi - User.Movement.X + OffSetX) * CellWidth - OffSetX + User.OffSetMove.X;

            int[] drawYCache = new int[endYExtended - startY + 1];
            for (int yi = startY; yi <= endYExtended; yi++)
                drawYCache[yi - startY] = (yi - User.Movement.Y + OffSetY) * CellHeight + User.OffSetMove.Y;


            for (int y = startY; y <= endYExtended; y++)
            {
                if (y <= 0) continue;
                if (y >= Height) break;

                int drawY = drawYCache[y - startY];

                for (int x = startX; x <= endX; x++)
                {
                    if (x < 0) continue;
                    if (x >= Width) break;

                    int drawX = drawXCache[x - startX];
                    var cell = M2CellInfo[x, y];

                    // Back
                    if (y % 2 == 0 && x % 2 == 0 && y <= endY)
                    {
                        if (cell.BackImage != 0 && cell.BackIndex != -1)
                        {
                            int index = (cell.BackImage & 0x1FFFFFFF) - 1;
                            var lib = Libraries.MapLibs[cell.BackIndex];
                            lib.Draw(index, drawX, drawY);
                        }
                    }

                    // Middle
                    int midIndex = cell.MiddleImage - 1;
                    if (midIndex >= 0 && cell.MiddleIndex != -1)
                    {
                        var lib = Libraries.MapLibs[cell.MiddleIndex];
                        Size s = lib.GetSize(midIndex);
                        if ((s.Width == CellWidth && s.Height == CellHeight) ||
                            (s.Width == CellWidth * 2 && s.Height == CellHeight * 2))
                        {
                            lib.Draw(midIndex, drawX, drawY);
                        }
                    }

                    // Front
                    int frontIndex = (cell.FrontImage & 0x7FFF) - 1;
                    if (frontIndex != -1)
                    {
                        int fileIndex = cell.FrontIndex;
                        if (fileIndex != -1 && fileIndex != 200)
                        {
                            var lib = Libraries.MapLibs[fileIndex];
                            Size s = lib.GetSize(frontIndex);

                            // door
                            if (cell.DoorIndex > 0)
                            {
                                Door doorInfo = GetDoor(cell.DoorIndex);
                                if (doorInfo == null)
                                {
                                    doorInfo = new Door() { index = cell.DoorIndex, DoorState = 0, ImageIndex = 0, LastTick = CMain.Time };
                                    Doors.Add(doorInfo);
                                }
                                else if (doorInfo.DoorState != 0)
                                {
                                    frontIndex += (doorInfo.ImageIndex + 1) * cell.DoorOffset;
                                }
                            }

                            if (frontIndex >= 0 &&
                                ((s.Width == CellWidth && s.Height == CellHeight) ||
                                 (s.Width == CellWidth * 2 && s.Height == CellHeight * 2)))
                            {
                                lib.Draw(frontIndex, drawX, drawY);
                            }
                        }
                    }
                }
            }

            DXManager.SetSurface(oldSurface);
            FloorValid = true;
        }
        private void DrawBackground()
        {
            string cleanFilename = FileName.Replace(Settings.MapPath, "");

            if (cleanFilename.StartsWith("ID1") || cleanFilename.StartsWith("ID2"))
            {
                Libraries.Background.Draw(10, 0, 0); //mountains
            }
            else if (cleanFilename.StartsWith("ID3_013"))
            {
                Libraries.Background.Draw(22, 0, 0); //desert
            }
            else if (cleanFilename.StartsWith("ID3_015"))
            {
                Libraries.Background.Draw(23, 0, 0); //greatwall
            }
            else if (cleanFilename.StartsWith("ID3_023") || cleanFilename.StartsWith("ID3_025"))
            {
                Libraries.Background.Draw(21, 0, 0); //village entrance
            }
        }

        private void DrawObjects()
        {
            if (Settings.Effect)
            {
                for (int i = Effects.Count - 1; i >= 0; i--)
                {
                    if (!Effects[i].DrawBehind) continue;
                    Effects[i].Draw();
                }
            }

            for (int y = User.Movement.Y - ViewRangeY; y <= User.Movement.Y + ViewRangeY + 25; y++)
            {
                if (y <= 0) continue;
                if (y >= Height) break;
                for (int x = User.Movement.X - ViewRangeX; x <= User.Movement.X + ViewRangeX; x++)
                {
                    if (x < 0) continue;
                    if (x >= Width) break;
                    M2CellInfo[x, y].DrawDeadObjects();
                }
            }

            for (int y = User.Movement.Y - ViewRangeY; y <= User.Movement.Y + ViewRangeY + 25; y++)
            {
                if (y <= 0) continue;
                if (y >= Height) break;
                int drawY = (y - User.Movement.Y + OffSetY + 1) * CellHeight + User.OffSetMove.Y;

                for (int x = User.Movement.X - ViewRangeX; x <= User.Movement.X + ViewRangeX; x++)
                {
                    if (x < 0) continue;
                    if (x >= Width) break;
                    int drawX = (x - User.Movement.X + OffSetX) * CellWidth - OffSetX + User.OffSetMove.X;
                    int index;
                    byte animation;
                    bool blend;
                    Size s;
                    #region Draw shanda's tile animation layer
                    index = M2CellInfo[x, y].TileAnimationImage;
                    animation = M2CellInfo[x, y].TileAnimationFrames;
                    if ((index > 0) & (animation > 0))
                    {
                        index--;
                        int animationoffset = M2CellInfo[x, y].TileAnimationOffset ^ 0x2000;
                        index += animationoffset * (AnimationCount % animation);
                        Libraries.MapLibs[190].DrawUp(index, drawX, drawY);
                    }

                    #endregion

                    #region Draw mir3 middle layer
                    if ((M2CellInfo[x, y].MiddleIndex >= 0) && (M2CellInfo[x, y].MiddleIndex != -1))   //M2P '> 199' changed to '>= 0' to include mir2 libraries. Fixes middle layer tile strips draw. Also changed in 'DrawFloor' above.
                    {
                        index = M2CellInfo[x, y].MiddleImage - 1;
                        if (index > 0)
                        {
                            animation = M2CellInfo[x, y].MiddleAnimationFrame;
                            blend = false;
                            if ((animation > 0) && (animation < 255))
                            {
                                if ((animation & 0x0f) > 0)
                                {
                                    blend = true;
                                    animation &= 0x0f;
                                }
                                if (animation > 0)
                                {
                                    byte animationTick = M2CellInfo[x, y].MiddleAnimationTick;
                                    index += (AnimationCount % (animation + (animation * animationTick))) / (1 + animationTick);

                                    if (blend && (animation == 10 || animation == 8)) //diamond mines, abyss blends
                                    {
                                        Libraries.MapLibs[M2CellInfo[x, y].MiddleIndex].DrawUpBlend(index, new Point(drawX, drawY));
                                    }
                                    else
                                    {
                                        Libraries.MapLibs[M2CellInfo[x, y].MiddleIndex].DrawUp(index, drawX, drawY);
                                    }
                                }
                            }
                            s = Libraries.MapLibs[M2CellInfo[x, y].MiddleIndex].GetSize(index);
                            if ((s.Width != CellWidth || s.Height != CellHeight) && (s.Width != (CellWidth * 2) || s.Height != (CellHeight * 2)) && !blend)
                            {
                                Libraries.MapLibs[M2CellInfo[x, y].MiddleIndex].DrawUp(index, drawX, drawY);
                            }
                        }
                    }
                    #endregion

                    #region Draw front layer
                    index = (M2CellInfo[x, y].FrontImage & 0x7FFF) - 1;

                    if (index < 0) continue;

                    int fileIndex = M2CellInfo[x, y].FrontIndex;
                    if (fileIndex == -1) continue;
                    animation = M2CellInfo[x, y].FrontAnimationFrame;

                    if ((animation & 0x80) > 0)
                    {
                        blend = true;
                        animation &= 0x7F;
                    }
                    else
                        blend = false;


                    if (animation > 0)
                    {
                        byte animationTick = M2CellInfo[x, y].FrontAnimationTick;
                        index += (AnimationCount % (animation + (animation * animationTick))) / (1 + animationTick);
                    }


                    if (M2CellInfo[x, y].DoorIndex > 0)
                    {
                        Door DoorInfo = GetDoor(M2CellInfo[x, y].DoorIndex);
                        if (DoorInfo == null)
                        {
                            DoorInfo = new Door() { index = M2CellInfo[x, y].DoorIndex, DoorState = 0, ImageIndex = 0, LastTick = CMain.Time };
                            Doors.Add(DoorInfo);
                        }
                        else
                        {
                            if (DoorInfo.DoorState != 0)
                            {
                                index += (DoorInfo.ImageIndex + 1) * M2CellInfo[x, y].DoorOffset;//'bad' code if you want to use animation but it's gonna depend on the animation > has to be custom designed for the animtion
                            }
                        }
                    }

                    s = Libraries.MapLibs[fileIndex].GetSize(index);
                    if (s.Width == CellWidth && s.Height == CellHeight && animation == 0) continue;
                    if ((s.Width == CellWidth * 2) && (s.Height == CellHeight * 2) && (animation == 0)) continue;

                    if (blend)
                    {
                        if (fileIndex == 14 || fileIndex == 27 || (fileIndex > 99 & fileIndex < 199))
                            Libraries.MapLibs[fileIndex].DrawBlend(index, new Point(drawX, drawY - (3 * CellHeight)), Color.White, true);
                        else
                            Libraries.MapLibs[fileIndex].DrawBlend(index, new Point(drawX, drawY - s.Height), Color.White, (index >= 2723 && index <= 2732));
                    }
                    else
                    {
                        if (fileIndex == 28 && Libraries.MapLibs[fileIndex].GetOffSet(index) != Point.Empty)
                            Libraries.MapLibs[fileIndex].Draw(index, new Point(drawX, drawY - CellHeight), Color.White, true);
                        else
                            Libraries.MapLibs[fileIndex].Draw(index, drawX, drawY - s.Height);
                    }
                    #endregion
                }

                for (int x = User.Movement.X - ViewRangeX; x <= User.Movement.X + ViewRangeX; x++)
                {
                    if (x < 0) continue;
                    if (x >= Width) break;
                    M2CellInfo[x, y].DrawObjects();
                }
            }

            DXManager.Flush();
            float oldOpacity = DXManager.Opacity;
            DXManager.SetOpacity(0.4F);

            //MapObject.User.DrawMount();

            MapObject.User.DrawBody();

            if ((MapObject.User.Direction == MirDirection.Up) ||
                (MapObject.User.Direction == MirDirection.UpLeft) ||
                (MapObject.User.Direction == MirDirection.UpRight) ||
                (MapObject.User.Direction == MirDirection.Right) ||
                (MapObject.User.Direction == MirDirection.Left))
            {
                MapObject.User.DrawHead();
                MapObject.User.DrawWings();
            }
            else
            {
                MapObject.User.DrawWings();
                MapObject.User.DrawHead();
            }

            DXManager.SetOpacity(oldOpacity);

            if (Settings.HighlightTarget)
            {
                if (MapObject.MouseObject != null && !MapObject.MouseObject.Dead && MapObject.MouseObject != MapObject.TargetObject && MapObject.MouseObject.Blend)
                    MapObject.MouseObject.DrawBlend();

                if (MapObject.TargetObject != null)
                    MapObject.TargetObject.DrawBlend();
            }

            if (Settings.Effect)
            {
                for (int i = Effects.Count - 1; i >= 0; i--)
                {
                    if (Effects[i].DrawBehind) continue;
                    Effects[i].Draw();
                }
            }

            foreach (var ob in Objects.Values)
            {
                ob.DrawEffects(Settings.Effect);

                if (Settings.NameView && !(ob is ItemObject) && !ob.Dead)
                    ob.DrawName();

                ob.DrawChat();
                //ob.DrawHealth();
                ob.DrawPoison();
                ob.DrawDamages();
            }

            foreach (var ob in Objects.Values)
            {
                ob.DrawHealth();
            }
        }

        private Color GetBlindLight(Color light)
        {
            if (MapObject.User.BlindTime <= CMain.Time && MapObject.User.BlindCount < 25)
            {
                MapObject.User.BlindTime = CMain.Time + 100;
                MapObject.User.BlindCount++;
            }

            int count = MapObject.User.BlindCount;
            light = Color.FromArgb(255, Math.Max(20, light.R - (count * 10)), Math.Max(20, light.G - (count * 10)), Math.Max(20, light.B - (count * 10)));

            return light;
        }

        private void DrawLights(LightSetting setting)
        {
            if (DXManager.Lights == null || DXManager.Lights.Count == 0) return;

            if (DXManager.LightTexture == null || DXManager.LightTexture.Disposed)
            {
                DXManager.LightTexture = DXManager.CreateRenderTarget(Settings.ScreenWidth, Settings.ScreenHeight);
            }

            RenderTexture oldSurface = DXManager.CurrentSurface;
            DXManager.SetSurface(DXManager.LightTexture);

            #region Night Lights
            Color darkness;

            switch (setting)
            {
                case LightSetting.Night:
                    {
                        switch (MapDarkLight)
                        {
                            case 1:
                                darkness = Color.FromArgb(255, 20, 20, 20);
                                break;
                            case 2:
                                darkness = Color.LightSlateGray;
                                break;
                            case 3:
                                darkness = Color.SkyBlue;
                                break;
                            case 4:
                                darkness = Color.Goldenrod;
                                break;
                            default:
                                darkness = Color.Black;
                                break;
                        }
                    }
                    break;
                case LightSetting.Evening:
                case LightSetting.Dawn:
                    darkness = Color.FromArgb(255, 50, 50, 50);
                    break;
                default:
                case LightSetting.Day:
                    darkness = Color.FromArgb(255, 255, 255, 255);
                    break;
            }

            if (MapObject.User.Poison.HasFlag(PoisonType.Blindness))
            {
                darkness = GetBlindLight(darkness);
            }

            DXManager.Clear(darkness);

            #endregion

            int light;
            Point p;
            DXManager.SetBlend(true);
            DXManager.SetLightAdditiveBlend();

            #region Object Lights (Player/Mob/NPC)
            foreach (var ob in Objects.Values)
            {
                if (ob.Light > 0 && (!ob.Dead || ob == MapObject.User || ob.Race == ObjectType.Spell))
                {
                    light = ob.Light;

                    int lightRange = light % 15;
                    if (lightRange >= DXManager.Lights.Count)
                        lightRange = DXManager.Lights.Count - 1;

                    p = ob.DrawLocation;

                    Color lightColour = ob.LightColour;

                    if (ob.Race == ObjectType.Player)
                    {
                        switch (light / 15)
                        {
                            case 0://no light source
                                lightColour = Color.FromArgb(255, 60, 60, 60);
                                break;
                            case 1:
                                lightColour = Color.FromArgb(255, 120, 120, 120);
                                break;
                            case 2://Candle
                                lightColour = Color.FromArgb(255, 180, 180, 180);
                                break;
                            case 3://Torch
                                lightColour = Color.FromArgb(255, 240, 240, 240);
                                break;
                            default://Peddler Torch
                                lightColour = Color.FromArgb(255, 255, 255, 255);
                                break;
                        }
                    }
                    else if (ob.Race == ObjectType.Merchant)
                    {
                        lightColour = Color.FromArgb(255, 120, 120, 120);
                    }

                    if (MapObject.User.Poison.HasFlag(PoisonType.Blindness))
                    {
                        lightColour = GetBlindLight(lightColour);
                    }

                    if (DXManager.Lights[lightRange] != null && !DXManager.Lights[lightRange].Disposed)
                    {
                        p.Offset(-(DXManager.LightSizes[lightRange].X / 2) - (CellWidth / 2), -(DXManager.LightSizes[lightRange].Y / 2) - (CellHeight / 2) - 5);
                        DXManager.Draw(DXManager.Lights[lightRange], null, new Vector3((float)p.X, (float)p.Y, 0.0F), lightColour);
                    }
                }

                #region Object Effect Lights
                if (!Settings.Effect) continue;
                for (int e = 0; e < ob.Effects.Count; e++)
                {
                    Effect effect = ob.Effects[e];
                    if (!effect.Blend || CMain.Time < effect.Start || (!(effect is Missile) && effect.Light < ob.Light)) continue;

                    light = effect.Light;

                    p = effect.DrawLocation;

                    var lightColour = effect.LightColour;

                    if (MapObject.User.Poison.HasFlag(PoisonType.Blindness))
                    {
                        lightColour = GetBlindLight(lightColour);
                    }

                    if (DXManager.Lights[light] != null && !DXManager.Lights[light].Disposed)
                    {
                        p.Offset(-(DXManager.LightSizes[light].X / 2) - (CellWidth / 2), -(DXManager.LightSizes[light].Y / 2) - (CellHeight / 2) - 5);
                        DXManager.Draw(DXManager.Lights[light], null, new Vector3((float)p.X, (float)p.Y, 0.0F), lightColour);
                    }

                }
                #endregion
            }
            #endregion

            #region Map Effect Lights
            if (Settings.Effect)
            {
                for (int e = 0; e < Effects.Count; e++)
                {
                    Effect effect = Effects[e];
                    if (!effect.Blend || CMain.Time < effect.Start) continue;

                    light = effect.Light;
                    if (light == 0) continue;

                    p = effect.DrawLocation;

                    var lightColour = Color.White;

                    if (MapObject.User.Poison.HasFlag(PoisonType.Blindness))
                    {
                        lightColour = GetBlindLight(lightColour);
                    }

                    if (DXManager.Lights[light] != null && !DXManager.Lights[light].Disposed)
                    {
                        p.Offset(-(DXManager.LightSizes[light].X / 2) - (CellWidth / 2), -(DXManager.LightSizes[light].Y / 2) - (CellHeight / 2) - 5);
                        DXManager.Draw(DXManager.Lights[light], null, new Vector3((float)p.X, (float)p.Y, 0.0F), lightColour);
                    }
                }
            }
            #endregion

            #region Map Lights
            for (int y = MapObject.User.Movement.Y - ViewRangeY - 24; y <= MapObject.User.Movement.Y + ViewRangeY + 24; y++)
            {
                if (y < 0) continue;
                if (y >= Height) break;
                for (int x = MapObject.User.Movement.X - ViewRangeX - 24; x < MapObject.User.Movement.X + ViewRangeX + 24; x++)
                {
                    if (x < 0) continue;
                    if (x >= Width) break;
                    int imageIndex = (M2CellInfo[x, y].FrontImage & 0x7FFF) - 1;
                    if (imageIndex == -1) continue;
                    int fileIndex = M2CellInfo[x, y].FrontIndex;
                    if (fileIndex == -1) continue;
                    byte cellLight = CellLight(M2CellInfo[x, y], fileIndex, imageIndex);
                    if (cellLight <= 0 || cellLight >= 10) continue;

                    Color lightIntensity;

                    light = (cellLight % 10) * 3;

                    switch (cellLight / 10)
                    {
                        case 1:
                            lightIntensity = Color.FromArgb(255, 255, 255, 255);
                            break;
                        case 2:
                            lightIntensity = Color.FromArgb(255, 120, 180, 255);
                            break;
                        case 3:
                            lightIntensity = Color.FromArgb(255, 255, 180, 120);
                            break;
                        case 4:
                            lightIntensity = Color.FromArgb(255, 22, 160, 5);
                            break;
                        default:
                            lightIntensity = Color.FromArgb(255, 255, 255, 255);
                            break;
                    }
                    if (IsFirePit(fileIndex, imageIndex)) lightIntensity = FireCellLightColour;

                    if (MapObject.User.Poison.HasFlag(PoisonType.Blindness))
                    {
                        lightIntensity = GetBlindLight(lightIntensity);
                    }

                    p = new Point(
                        (x + OffSetX - MapObject.User.Movement.X) * CellWidth + MapObject.User.OffSetMove.X,
                        (y + OffSetY - MapObject.User.Movement.Y) * CellHeight + MapObject.User.OffSetMove.Y + 32);


                    if (M2CellInfo[x, y].FrontAnimationFrame > 0)
                        p.Offset(Libraries.MapLibs[fileIndex].GetOffSet(imageIndex));

                    if (light >= DXManager.Lights.Count)
                        light = DXManager.Lights.Count - 1;

                    if (DXManager.Lights[light] != null && !DXManager.Lights[light].Disposed)
                    {
                        p.Offset(-(DXManager.LightSizes[light].X / 2) - (CellWidth / 2) + 10, -(DXManager.LightSizes[light].Y / 2) - (CellHeight / 2) - 5);
                        DXManager.Draw(DXManager.Lights[light], null, new Vector3((float)p.X, (float)p.Y, 0.0F), lightIntensity);
                    }
                }
            }
            #endregion

            DXManager.SetBlend(false);
            DXManager.SetSurface(oldSurface);

            DXManager.SetLightMultiplyBlend();

            DXManager.Draw(DXManager.LightTexture, new Rectangle(0, 0, Settings.ScreenWidth, Settings.ScreenHeight), Vector3.Zero, Color.White);

            DXManager.RestartSpriteAlphaBlend();
        }

        private static void OnMouseClick(object sender, EventArgs e)
        {
            if (!(e is MouseEventArgs me)) return;

            if (AwakeningAction == true) return;
            switch (me.Button)
            {
                case MouseButtons.Left:
                    {
                        AutoRun = false;
                        GameScene.Scene.MapControl.AutoPath = false;
                        if (MapObject.MouseObject == null) return;
                        NPCObject npc = MapObject.MouseObject as NPCObject;
                        if (npc != null)
                        {
                            if (npc.ObjectID == GameScene.NPCID &&
                                (CMain.Time <= GameScene.NPCTime || GameScene.Ui.IsNpcDialogOpen()))
                            {
                                return;
                            }

                            //GameScene.Scene.NPCDialog.Hide();

                            GameScene.NPCTime = CMain.Time + 5000;
                            GameScene.NPCID = npc.ObjectID;
                            GameScene.TalkedNPCID = npc.ObjectID;
                            Network.Enqueue(new C.CallNPC { ObjectID = npc.ObjectID, Key = "[@Main]" });
                        }
                    }
                    break;
                case MouseButtons.Right:
                    {
                        AutoRun = false;
                        if (MapObject.MouseObject == null)
                        {
                            if (Settings.NewMove && MapLocation != MapObject.User.CurrentLocation && GameScene.Scene.MapControl.EmptyCell(MapLocation))
                            {
                                var path = GameScene.Scene.MapControl.PathFinder.FindPath(MapObject.User.CurrentLocation, MapLocation, 20);

                                if (path != null && path.Count > 0)
                                {
                                    GameScene.Scene.MapControl.CurrentPath = path;
                                    GameScene.Scene.MapControl.AutoPath = true;
                                    var offset = MouseLocation.Subtract(ToMouseLocation(MapLocation));
                                    Effects.Add(new Effect(Libraries.Magic3, 500, 10, 600, MapLocation) { DrawOffset = offset.Subtract(8, 15) });
                                }
                            }
                            return;
                        }

                        if (CMain.Ctrl)
                        {
                            HeroObject hero = MapObject.MouseObject as HeroObject;

                            if (hero != null &&
                                hero.ObjectID != (Hero is null ? 0 : Hero.ObjectID) &&
                                CMain.Time >= GameScene.InspectTime)
                            {
                                GameScene.InspectTime = CMain.Time + 500;
                                GameScene.Models.Inspect.InspectID = hero.ObjectID;
                                Network.Enqueue(new C.Inspect { ObjectID = hero.ObjectID, Hero = true });
                                return;
                            }

                            PlayerObject player = MapObject.MouseObject as PlayerObject;

                            if (player != null &&
                                player != User &&
                                CMain.Time >= GameScene.InspectTime)
                            {
                                GameScene.InspectTime = CMain.Time + 500;
                                GameScene.Models.Inspect.InspectID = player.ObjectID;
                                Network.Enqueue(new C.Inspect { ObjectID = player.ObjectID });
                                return;
                            }
                        }
                    }
                    break;
                case MouseButtons.Middle:
                    AutoRun = !AutoRun;
                    break;
            }
        }

        private static void OnMouseDown(object sender, MouseEventArgs e)
        {
            MapButtons |= e.Button;
            if (e.Button != MouseButtons.Right || !Settings.NewMove)
                GameScene.CanRun = false;

            if (AwakeningAction == true) return;

            if (e.Button != MouseButtons.Left) return;

            if (ItemMoves.Selected != null)
            {
                if (ItemMoves.Selected.GridType != MirGridType.Inventory && ItemMoves.Selected.GridType != MirGridType.HeroInventory)
                {
                    ItemMoves.Selected = null;
                    return;
                }

                ItemSlotRef cell = ItemMoves.Selected;
                if (cell.Item.Info.Bind.HasFlag(BindMode.DontDrop))
                {
                    GameScene.Ui.ItemMessage(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.YouCannotDrop), cell.Item.FriendlyName));
                    ItemMoves.Selected = null;
                    return;
                }
                if (cell.Item.Count == 1)
                {
                    GameScene.Ui.ItemAskYesNo(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.DropTip), cell.Item.FriendlyName), () =>
                    {
                        Network.Enqueue(new C.DropItem
                        {
                            UniqueID = cell.Item.UniqueID,
                            Count = 1,
                            HeroInventory = cell.GridType == MirGridType.HeroInventory
                        });

                        cell.Locked = true;
                    });
                }
                else
                {
                    GameScene.Ui.ItemAskAmount(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DropAmount), cell.Item.Info.Image, cell.Item.Count, amount =>
                    {
                        if (amount <= 0) return;
                        Network.Enqueue(new C.DropItem
                        {
                            UniqueID = cell.Item.UniqueID,
                            Count = (ushort)amount,
                            HeroInventory = cell.GridType == MirGridType.HeroInventory
                        });

                        cell.Locked = true;
                    });
                }
                ItemMoves.Selected = null;

                return;
            }

            if (GameScene.PickedUpGold)
            {
                GameScene.Ui.ItemAskAmount(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DropAmount), 116, GameScene.Gold, amount =>
                {
                    if (amount > 0)
                    {
                        Network.Enqueue(new C.DropGold { Amount = amount });
                    }
                });

                GameScene.PickedUpGold = false;
            }

            if (MapObject.MouseObject != null && !MapObject.MouseObject.Dead && !(MapObject.MouseObject is ItemObject) &&
                !(MapObject.MouseObject is NPCObject) && !(MapObject.MouseObject is MonsterObject && MapObject.MouseObject.AI == 64)
                 && !(MapObject.MouseObject is MonsterObject && MapObject.MouseObject.AI == 70))
            {
                MapObject.TargetObjectID = MapObject.MouseObject.ObjectID;
                if (MapObject.MouseObject is MonsterObject && MapObject.MouseObject.AI != 6)
                    MapObject.MagicObjectID = MapObject.TargetObject.ObjectID;
            }
            else
                MapObject.TargetObjectID = 0;
        }

        private void CheckInput()
        {
            if (AwakeningAction == true) return;

            if ((MouseControl == this) && (MapButtons != MouseButtons.None)) AutoHit = false;//mouse actions stop mining even when frozen!
            if (!CanRideAttack()) AutoHit = false;

            if (CMain.Time < InputDelay || User.Poison.HasFlag(PoisonType.Paralysis) || User.Poison.HasFlag(PoisonType.LRParalysis) || User.Poison.HasFlag(PoisonType.Frozen) || User.Fishing) return;

            if (User.NextMagic != null && !User.RidingMount)
            {
                UseMagic(User.NextMagic, User);
                return;
            }

            if (CMain.Time < User.BlizzardStopTime || CMain.Time < User.ReincarnationStopTime) return;

            if (MapObject.TargetObject != null && !MapObject.TargetObject.Dead)
            {
                if (((MapObject.TargetObject.Name.EndsWith(")") || MapObject.TargetObject is PlayerObject) && CMain.Shift) ||
                    (!MapObject.TargetObject.Name.EndsWith(")") && MapObject.TargetObject is MonsterObject))
                {
                    GameScene.LogTime = CMain.Time + Globals.LogDelay;

                    if (User.Class == MirClass.Archer && User.HasClassWeapon && !User.RidingMount && !User.Fishing)//ArcherTest - non aggressive targets (player / pets)
                    {
                        if (Functions.InRange(MapObject.TargetObject.CurrentLocation, User.CurrentLocation, Globals.MaxAttackRange))
                        {
                            if (CMain.Time > GameScene.AttackTime)
                            {
                                User.QueuedAction = new QueuedAction { Action = MirAction.AttackRange1, Direction = Functions.DirectionFromPoint(User.CurrentLocation, MapObject.TargetObject.CurrentLocation), Location = User.CurrentLocation, Params = new List<object>() };
                                User.QueuedAction.Params.Add(MapObject.TargetObject != null ? MapObject.TargetObject.ObjectID : (uint)0);
                                User.QueuedAction.Params.Add(MapObject.TargetObject.CurrentLocation);

                                // MapObject.TargetObject = null; //stop constant attack when close up
                            }
                        }
                        else
                        {
                            if (CMain.Time >= OutputDelay)
                            {
                                OutputDelay = CMain.Time + 1000;
                                GameScene.Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TargetTooFar));
                            }
                        }
                        //  return;
                    }

                    else if (Functions.InRange(MapObject.TargetObject.CurrentLocation, User.CurrentLocation, 1))
                    {
                        if (CMain.Time > GameScene.AttackTime && CanRideAttack() && !User.Poison.HasFlag(PoisonType.Dazed))
                        {
                            User.QueuedAction = new QueuedAction { Action = MirAction.Attack1, Direction = Functions.DirectionFromPoint(User.CurrentLocation, MapObject.TargetObject.CurrentLocation), Location = User.CurrentLocation };
                            return;
                        }
                    }
                }
            }
            if (AutoHit && !User.RidingMount)
            {
                if (CMain.Time > GameScene.AttackTime)
                {
                    User.QueuedAction = new QueuedAction { Action = MirAction.Mine, Direction = User.Direction, Location = User.CurrentLocation };
                    return;
                }
            }


            MirDirection direction;
            if (MouseControl == this)
            {
                direction = MouseDirection();
                if (AutoRun)
                {
                    if (GameScene.CanRun && CanRun(direction) && CMain.Time > GameScene.NextRunTime && User.HP >= 10 && (!User.Sneaking || (User.Sneaking && User.Sprint))) //slow remove
                    {
                        int distance = User.RidingMount || User.Sprint && !User.Sneaking ? 3 : 2;
                        bool fail = false;
                        for (int i = 1; i <= distance; i++)
                        {
                            if (!CheckDoorOpen(Functions.PointMove(User.CurrentLocation, direction, i)))
                                fail = true;
                        }
                        if (!fail)
                        {
                            User.QueuedAction = new QueuedAction { Action = MirAction.Running, Direction = direction, Location = Functions.PointMove(User.CurrentLocation, direction, distance) };
                            return;
                        }
                    }
                    if ((CanWalk(direction, out direction)) && (CheckDoorOpen(Functions.PointMove(User.CurrentLocation, direction, 1))))
                    {
                        User.QueuedAction = new QueuedAction { Action = MirAction.Walking, Direction = direction, Location = Functions.PointMove(User.CurrentLocation, direction, 1) };
                        return;
                    }
                    if (direction != User.Direction)
                    {
                        User.QueuedAction = new QueuedAction { Action = MirAction.Standing, Direction = direction, Location = User.CurrentLocation };
                        return;
                    }
                    return;
                }

                switch (MapButtons)
                {
                    case MouseButtons.Left:
                        if (MapObject.MouseObject is NPCObject || (MapObject.MouseObject is PlayerObject && MapObject.MouseObject != User)) break;
                        if (MapObject.MouseObject is MonsterObject && MapObject.MouseObject.AI == 70) break;

                        if (CMain.Alt && !User.RidingMount)
                        {
                            User.QueuedAction = new QueuedAction { Action = MirAction.Harvest, Direction = direction, Location = User.CurrentLocation };
                            return;
                        }

                        if (CMain.Shift)
                        {
                            if (CMain.Time > GameScene.AttackTime && CanRideAttack()) //ArcherTest - shift click
                            {
                                MapObject target = null;
                                if (MapObject.MouseObject is MonsterObject || MapObject.MouseObject is PlayerObject) target = MapObject.MouseObject;

                                if (User.Class == MirClass.Archer && User.HasClassWeapon && !User.RidingMount && !User.Poison.HasFlag(PoisonType.Dazed))
                                {
                                    if (target != null)
                                    {
                                        if (!Functions.InRange(MapObject.MouseObject.CurrentLocation, User.CurrentLocation, Globals.MaxAttackRange))
                                        {
                                            if (CMain.Time >= OutputDelay)
                                            {
                                                OutputDelay = CMain.Time + 1000;
                                                GameScene.Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TargetTooFar));
                                            }
                                            return;
                                        }
                                    }

                                    User.QueuedAction = new QueuedAction { Action = MirAction.AttackRange1, Direction = MouseDirection(), Location = User.CurrentLocation, Params = new List<object>() };
                                    User.QueuedAction.Params.Add(target != null ? target.ObjectID : (uint)0);
                                    User.QueuedAction.Params.Add(Functions.PointMove(User.CurrentLocation, MouseDirection(), 9));
                                    return;
                                }

                                //stops double slash from being used without empty hand or assassin weapon (otherwise bugs on second swing)
                                if (GameScene.User.DoubleSlash && (!User.HasClassWeapon && User.Weapon > -1)) return;
                                if (User.Poison.HasFlag(PoisonType.Dazed)) return;

                                User.QueuedAction = new QueuedAction { Action = MirAction.Attack1, Direction = direction, Location = User.CurrentLocation };
                            }
                            return;
                        }

                        if (MapObject.MouseObject is MonsterObject && User.Class == MirClass.Archer && MapObject.TargetObject != null && !MapObject.TargetObject.Dead && User.HasClassWeapon && !User.RidingMount) //ArcherTest - range attack
                        {
                            if (Functions.InRange(MapObject.MouseObject.CurrentLocation, User.CurrentLocation, Globals.MaxAttackRange))
                            {
                                if (CMain.Time > GameScene.AttackTime)
                                {
                                    User.QueuedAction = new QueuedAction { Action = MirAction.AttackRange1, Direction = direction, Location = User.CurrentLocation, Params = new List<object>() };
                                    User.QueuedAction.Params.Add(MapObject.TargetObject.ObjectID);
                                    User.QueuedAction.Params.Add(MapObject.TargetObject.CurrentLocation);
                                }
                            }
                            else
                            {
                                if (CMain.Time >= OutputDelay)
                                {
                                    OutputDelay = CMain.Time + 1000;
                                    GameScene.Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TargetTooFar));
                                }
                            }
                            return;
                        }

                        if (MapLocation == User.CurrentLocation)
                        {
                            if (CMain.Time > GameScene.PickUpTime)
                            {
                                GameScene.PickUpTime = CMain.Time + 200;
                                Network.Enqueue(new C.PickUp());
                            }
                            return;
                        }

                        //mine
                        if (!ValidPoint(Functions.PointMove(User.CurrentLocation, direction, 1)))
                        {
                            if ((MapObject.User.Equipment[(int)EquipmentSlot.Weapon] != null) && (MapObject.User.Equipment[(int)EquipmentSlot.Weapon].Info.CanMine))
                            {
                                if (direction != User.Direction)
                                {
                                    User.QueuedAction = new QueuedAction { Action = MirAction.Standing, Direction = direction, Location = User.CurrentLocation };
                                    return;
                                }
                                AutoHit = true;
                                return;
                            }
                        }
                        if ((CanWalk(direction, out direction)) && (CheckDoorOpen(Functions.PointMove(User.CurrentLocation, direction, 1))))
                        {

                            User.QueuedAction = new QueuedAction { Action = MirAction.Walking, Direction = direction, Location = Functions.PointMove(User.CurrentLocation, direction, 1) };
                            return;
                        }
                        if (direction != User.Direction)
                        {
                            User.QueuedAction = new QueuedAction { Action = MirAction.Standing, Direction = direction, Location = User.CurrentLocation };
                            return;
                        }

                        if (CanFish(direction))
                        {
                            User.FishingTime = CMain.Time;
                            Network.Enqueue(new C.FishingCast { CastOut = true });
                            return;
                        }

                        break;
                    case MouseButtons.Right:
                        if (MapObject.MouseObject is PlayerObject && MapObject.MouseObject != User && CMain.Ctrl) break;
                        if (Settings.NewMove) break;

                        if (Functions.InRange(MapLocation, User.CurrentLocation, 2))
                        {
                            if (direction != User.Direction)
                            {
                                User.QueuedAction = new QueuedAction { Action = MirAction.Standing, Direction = direction, Location = User.CurrentLocation };
                            }
                            return;
                        }

                        GameScene.CanRun = User.FastRun ? true : GameScene.CanRun;

                        if (GameScene.CanRun && CanRun(direction) && CMain.Time > GameScene.NextRunTime && User.HP >= 10 && (!User.Sneaking || (User.Sneaking && User.Sprint))) //slow removed
                        {
                            int distance = User.RidingMount || User.Sprint && !User.Sneaking ? 3 : 2;
                            bool fail = false;
                            for (int i = 0; i <= distance; i++)
                            {
                                if (!CheckDoorOpen(Functions.PointMove(User.CurrentLocation, direction, i)))
                                    fail = true;
                            }
                            if (!fail)
                            {
                                User.QueuedAction = new QueuedAction { Action = MirAction.Running, Direction = direction, Location = Functions.PointMove(User.CurrentLocation, direction, User.RidingMount || (User.Sprint && !User.Sneaking) ? 3 : 2) };
                                return;
                            }
                        }
                        if ((CanWalk(direction, out direction)) && (CheckDoorOpen(Functions.PointMove(User.CurrentLocation, direction, 1))))
                        {
                            User.QueuedAction = new QueuedAction { Action = MirAction.Walking, Direction = direction, Location = Functions.PointMove(User.CurrentLocation, direction, 1) };
                            return;
                        }
                        if (direction != User.Direction)
                        {
                            User.QueuedAction = new QueuedAction { Action = MirAction.Standing, Direction = direction, Location = User.CurrentLocation };
                            return;
                        }
                        break;
                }
            }

            if (AutoPath)
            {
                if (CurrentPath == null || CurrentPath.Count == 0)
                {
                    AutoPath = false;
                    return;
                }

                var path = GameScene.Scene.MapControl.PathFinder.FindPath(MapObject.User.CurrentLocation, CurrentPath.Last().Location);

                if (path != null && path.Count > 0)
                    GameScene.Scene.MapControl.CurrentPath = path;
                else
                {
                    AutoPath = false;
                    return;
                }

                Node currentNode = CurrentPath.SingleOrDefault(x => User.CurrentLocation == x.Location);
                if (currentNode != null)
                {
                    while (true)
                    {
                        Node first = CurrentPath.First();
                        CurrentPath.Remove(first);

                        if (first == currentNode)
                            break;
                    }
                }

                if (CurrentPath.Count > 0)
                {
                    MirDirection dir = Functions.DirectionFromPoint(User.CurrentLocation, CurrentPath.First().Location);

                    if (GameScene.CanRun && CanRun(dir) && CMain.Time > GameScene.NextRunTime && User.HP >= 10 && CurrentPath.Count > (User.RidingMount ? 2 : 1))
                    {
                        User.QueuedAction = new QueuedAction { Action = MirAction.Running, Direction = dir, Location = Functions.PointMove(User.CurrentLocation, dir, User.RidingMount ? 3 : 2) };
                        return;
                    }
                    if (CanWalk(dir))
                    {
                        User.QueuedAction = new QueuedAction { Action = MirAction.Walking, Direction = dir, Location = Functions.PointMove(User.CurrentLocation, dir, 1) };

                        return;
                    }
                }
            }

            if (MapObject.TargetObject == null || MapObject.TargetObject.Dead) return;
            if (((!MapObject.TargetObject.Name.EndsWith(")") && !(MapObject.TargetObject is PlayerObject)) || !CMain.Shift) &&
                (MapObject.TargetObject.Name.EndsWith(")") || !(MapObject.TargetObject is MonsterObject))) return;
            if (Functions.InRange(MapObject.TargetObject.CurrentLocation, User.CurrentLocation, 1)) return;
            if (User.Class == MirClass.Archer && User.HasClassWeapon && (MapObject.TargetObject is MonsterObject || MapObject.TargetObject is PlayerObject)) return; //ArcherTest - stop walking
            direction = Functions.DirectionFromPoint(User.CurrentLocation, MapObject.TargetObject.CurrentLocation);

            if (!CanWalk(direction, out direction)) return;

            User.QueuedAction = new QueuedAction { Action = MirAction.Walking, Direction = direction, Location = Functions.PointMove(User.CurrentLocation, direction, 1) };
        }

        public void UseMagic(ClientMagic magic, UserObject actor)
        {
            if (CMain.Time < GameScene.SpellTime || actor.Poison.HasFlag(PoisonType.Stun))
            {
                actor.ClearMagic();
                return;
            }

            if ((CMain.Time <= magic.CastTime + magic.Delay))
            {
                if (CMain.Time >= OutputDelay)
                {
                    OutputDelay = CMain.Time + 1000;
                    GameScene.Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.CannotCastSpellSeconds), magic.Name,
                        ((magic.CastTime + magic.Delay) - CMain.Time - 1) / 1000 + 1));
                }

                actor.ClearMagic();
                return;
            }

            int cost = magic.Level * magic.LevelCost + magic.BaseCost;

            if (magic.Spell == Spell.Teleport || magic.Spell == Spell.Blink || magic.Spell == Spell.StormEscape)
            {
                if (actor.Stats[Stat.TeleportManaPenaltyPercent] > 0)
                {
                    cost += (cost * actor.Stats[Stat.TeleportManaPenaltyPercent]) / 100;
                }
            }

            if (actor.Stats[Stat.ManaPenaltyPercent] > 0)
            {
                cost += (cost * actor.Stats[Stat.ManaPenaltyPercent]) / 100;
            }

            if (cost > actor.MP)
            {
                if (CMain.Time >= OutputDelay)
                {
                    OutputDelay = CMain.Time + 1000;
                    GameScene.Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowMana));
                }
                actor.ClearMagic();
                return;
            }

            //bool isTargetSpell = true;

            MapObject target = null;

            //Targeting
            switch (magic.Spell)
            {
                case Spell.FireBall:
                case Spell.GreatFireBall:
                case Spell.ElectricShock:
                case Spell.Poisoning:
                case Spell.ThunderBolt:
                case Spell.FlameDisruptor:
                case Spell.SoulFireBall:
                case Spell.TurnUndead:
                case Spell.FrostCrunch:
                case Spell.Vampirism:
                case Spell.Revelation:
                case Spell.Entrapment:
                case Spell.Hallucination:
                case Spell.DarkBody:
                case Spell.FireBounce:
                case Spell.MeteorShower:
                    if (actor.NextMagicObject != null)
                    {
                        if (!actor.NextMagicObject.Dead && actor.NextMagicObject.Race != ObjectType.Item && actor.NextMagicObject.Race != ObjectType.Merchant)
                            target = actor.NextMagicObject;
                    }

                    if (target == null) target = MapObject.MagicObject;

                    if (target != null && target.Race == ObjectType.Monster) MapObject.MagicObjectID = target.ObjectID;
                    break;
                case Spell.StraightShot:
                case Spell.DoubleShot:
                case Spell.ElementalShot:
                case Spell.DelayedExplosion:
                case Spell.BindingShot:
                case Spell.VampireShot:
                case Spell.PoisonShot:
                case Spell.CrippleShot:
                case Spell.NapalmShot:
                case Spell.SummonVampire:
                case Spell.SummonToad:
                case Spell.SummonSnakes:
                    if (!actor.HasClassWeapon)
                    {
                        GameScene.Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.MustWearBowForSkill));
                        actor.ClearMagic();
                        return;
                    }
                    if (actor.NextMagicObject != null)
                    {
                        if (!actor.NextMagicObject.Dead && actor.NextMagicObject.Race != ObjectType.Item && actor.NextMagicObject.Race != ObjectType.Merchant)
                            target = actor.NextMagicObject;
                    }

                    if (target == null) target = MapObject.MagicObject;

                    if (target != null && target.Race == ObjectType.Monster) MapObject.MagicObjectID = target.ObjectID;
                    break;
                case Spell.Stonetrap:
                    if (!User.HasClassWeapon)
                    {
                        GameScene.Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.MustWearBowForSkill));
                        User.ClearMagic();
                        return;
                    }
                    if (User.NextMagicObject != null)
                    {
                        if (!User.NextMagicObject.Dead && User.NextMagicObject.Race != ObjectType.Item && User.NextMagicObject.Race != ObjectType.Merchant)
                            target = User.NextMagicObject;
                    }

                    //if(magic.Spell == Spell.ElementalShot)
                    //{
                    //    isTargetSpell = User.HasElements;
                    //}

                    //switch(magic.Spell)
                    //{
                    //    case Spell.SummonVampire:
                    //    case Spell.SummonToad:
                    //    case Spell.SummonSnakes:
                    //        isTargetSpell = false;
                    //        break;
                    //}

                    break;
                case Spell.Purification:
                case Spell.Healing:
                case Spell.UltimateEnhancer:
                case Spell.EnergyShield:
                case Spell.PetEnhancer:
                    if (actor.NextMagicObject != null)
                    {
                        if (!actor.NextMagicObject.Dead && actor.NextMagicObject.Race != ObjectType.Item && actor.NextMagicObject.Race != ObjectType.Merchant && !(actor.NextMagicObject is MonsterObject monster && monster.MasterObjectId == 0))
                            target = actor.NextMagicObject;
                    }

                    if (target == null) target = User;
                    break;
                case Spell.FireBang:
                case Spell.MassHiding:
                case Spell.FireWall:
                case Spell.TrapHexagon:
                case Spell.HealingCircle:
                case Spell.CatTongue:
                    if (actor.NextMagicObject != null)
                    {
                        if (!actor.NextMagicObject.Dead && actor.NextMagicObject.Race != ObjectType.Item && actor.NextMagicObject.Race != ObjectType.Merchant)
                            target = actor.NextMagicObject;
                    }
                    break;
                case Spell.PoisonCloud:
                    if (actor.NextMagicObject != null)
                    {
                        if (!actor.NextMagicObject.Dead && actor.NextMagicObject.Race != ObjectType.Item && actor.NextMagicObject.Race != ObjectType.Merchant)
                            target = actor.NextMagicObject;
                    }
                    break;
                case Spell.Blizzard:
                case Spell.MeteorStrike:
                    if (actor.NextMagicObject != null)
                    {
                        if (!actor.NextMagicObject.Dead && actor.NextMagicObject.Race != ObjectType.Item && actor.NextMagicObject.Race != ObjectType.Merchant)
                            target = actor.NextMagicObject;
                    }
                    break;
                case Spell.Reincarnation:
                    if (actor == Hero && actor.NextMagicObject == null)
                        actor.NextMagicObject = User;
                    if (actor.NextMagicObject != null)
                    {
                        if (actor.NextMagicObject.Dead && actor.NextMagicObject.Race == ObjectType.Player)
                            target = actor.NextMagicObject;
                    }
                    break;
                case Spell.Trap:
                    if (actor.NextMagicObject != null)
                    {
                        if (!actor.NextMagicObject.Dead && User.NextMagicObject.Race != ObjectType.Item && actor.NextMagicObject.Race != ObjectType.Merchant)
                            target = actor.NextMagicObject;
                    }
                    break;
                case Spell.FlashDash:
                    if (actor.GetMagic(Spell.FlashDash).Level <= 1 && actor.IsDashAttack() == false)
                    {
                        actor.ClearMagic();
                        return;
                    }
                    //isTargetSpell = false;
                    break;
                default:
                    //isTargetSpell = false;
                    break;
            }

            MirDirection dir = (target == null || target == User) ? actor.NextMagicDirection : Functions.DirectionFromPoint(actor.CurrentLocation, target.CurrentLocation);

            Point location = target != null ? target.CurrentLocation : actor.NextMagicLocation;

            uint targetID = target != null ? target.ObjectID : 0;

            if (magic.Spell == Spell.FlashDash)
                dir = actor.Direction;

            if ((magic.Range != 0) && (!Functions.InRange(actor.CurrentLocation, location, magic.Range)))
            {
                if (CMain.Time >= OutputDelay)
                {
                    OutputDelay = CMain.Time + 1000;
                    GameScene.Scene.OutputMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TargetTooFar));
                }
                actor.ClearMagic();
                return;
            }

            GameScene.LogTime = CMain.Time + Globals.LogDelay;

            if (actor == User)
            {
                User.QueuedAction = new QueuedAction { Action = MirAction.Spell, Direction = dir, Location = User.CurrentLocation, Params = new List<object>() };
                User.QueuedAction.Params.Add(magic.Spell);
                User.QueuedAction.Params.Add(targetID);
                User.QueuedAction.Params.Add(location);
                User.QueuedAction.Params.Add(magic.Level);
            }
            else
            {
                Network.Enqueue(new C.Magic { ObjectID = actor.ObjectID, Spell = magic.Spell, Direction = dir, TargetID = targetID, Location = location, SpellTargetLock = CMain.SpellTargetLock });
            }
        }

        public static MirDirection MouseDirection(float ratio = 45F) //22.5 = 16
        {
            Point p = new Point(MouseLocation.X / CellWidth, MouseLocation.Y / CellHeight);
            if (Functions.InRange(new Point(OffSetX, OffSetY), p, 2))
                return Functions.DirectionFromPoint(new Point(OffSetX, OffSetY), p);

            PointF c = new PointF(OffSetX * CellWidth + CellWidth / 2F, OffSetY * CellHeight + CellHeight / 2F);
            PointF a = new PointF(c.X, 0);
            PointF b = MouseLocation;
            float bc = (float)Distance(c, b);
            float ac = bc;
            b.Y -= c.Y;
            c.Y += bc;
            b.Y += bc;
            float ab = (float)Distance(b, a);
            double x = (ac * ac + bc * bc - ab * ab) / (2 * ac * bc);
            double angle = Math.Acos(x);

            angle *= 180 / Math.PI;

            if (MouseLocation.X < c.X) angle = 360 - angle;
            angle += ratio / 2;
            if (angle > 360) angle -= 360;

            return (MirDirection)(angle / ratio);
        }

        public static int Direction16(Point source, Point destination)
        {
            PointF c = new PointF(source.X, source.Y);
            PointF a = new PointF(c.X, 0);
            PointF b = new PointF(destination.X, destination.Y);
            float bc = (float)Distance(c, b);
            float ac = bc;
            b.Y -= c.Y;
            c.Y += bc;
            b.Y += bc;
            float ab = (float)Distance(b, a);
            double x = (ac * ac + bc * bc - ab * ab) / (2 * ac * bc);
            double angle = Math.Acos(x);

            angle *= 180 / Math.PI;

            if (destination.X < c.X) angle = 360 - angle;
            angle += 11.25F;
            if (angle > 360) angle -= 360;

            return (int)(angle / 22.5F);
        }

        public static double Distance(PointF p1, PointF p2)
        {
            double x = p2.X - p1.X;
            double y = p2.Y - p1.Y;
            return Math.Sqrt(x * x + y * y);
        }

        public bool EmptyCell(Point p)
        {
            if ((M2CellInfo[p.X, p.Y].BackImage & 0x20000000) != 0 || (M2CellInfo[p.X, p.Y].FrontImage & 0x8000) != 0)
                return false;

            foreach (var ob in Objects.Values)
                if (ob.CurrentLocation == p && ob.Blocking)
                    return false;

            return true;
        }


        private bool CanWalk(MirDirection dir)
        {
            return EmptyCell(Functions.PointMove(User.CurrentLocation, dir, 1)) && !User.InTrapRock;
        }

        private bool CanWalk(MirDirection dir, out MirDirection outDir)
        {
            outDir = dir;
            if (User.InTrapRock) return false;

            if (EmptyCell(Functions.PointMove(User.CurrentLocation, dir, 1)))
                return true;

            dir = Functions.NextDir(outDir);
            if (EmptyCell(Functions.PointMove(User.CurrentLocation, dir, 1)))
            {
                outDir = dir;
                return true;
            }

            dir = Functions.PreviousDir(outDir);
            if (EmptyCell(Functions.PointMove(User.CurrentLocation, dir, 1)))
            {
                outDir = dir;
                return true;
            }

            return false;
        }

        private bool CheckDoorOpen(Point p)
        {
            if (M2CellInfo[p.X, p.Y].DoorIndex == 0) return true;
            Door DoorInfo = GetDoor(M2CellInfo[p.X, p.Y].DoorIndex);
            if (DoorInfo == null) return false;//if the door doesnt exist then it isnt even being shown on screen (and cant be open lol)
            if ((DoorInfo.DoorState == DoorState.Closed) || (DoorInfo.DoorState == DoorState.Closing))
            {
                if (CMain.Time > _doorTime)
                {
                    _doorTime = CMain.Time + 4000;
                    Network.Enqueue(new C.Opendoor() { DoorIndex = DoorInfo.index });
                }

                return false;
            }
            if ((DoorInfo.DoorState == DoorState.Open) && (DoorInfo.LastTick + 4000 > CMain.Time))
            {
                if (CMain.Time > _doorTime)
                {
                    _doorTime = CMain.Time + 4000;
                    Network.Enqueue(new C.Opendoor() { DoorIndex = DoorInfo.index });
                }
            }
            return true;
        }

        private long _doorTime = 0;


        private bool CanRun(MirDirection dir)
        {
            if (User.InTrapRock) return false;
            if (User.CurrentBagWeight > User.Stats[Stat.BagWeight]) return false;
            if (User.CurrentWearWeight > User.Stats[Stat.BagWeight]) return false;
            if (CanWalk(dir) && EmptyCell(Functions.PointMove(User.CurrentLocation, dir, 2)))
            {
                if (User.RidingMount || User.Sprint && !User.Sneaking)
                {
                    return EmptyCell(Functions.PointMove(User.CurrentLocation, dir, 3));
                }

                return true;
            }

            return false;
        }

        private bool CanRideAttack()
        {
            if (GameScene.User.RidingMount)
            {
                UserItem item = GameScene.User.Equipment[(int)EquipmentSlot.Mount];
                if (item == null || item.Slots.Length < 4 || item.Slots[(int)MountSlot.Bells] == null) return false;
            }

            return true;
        }

        public bool CanFish(MirDirection dir)
        {
            if (!GameScene.User.HasFishingRod || GameScene.User.FishingTime + 1000 > CMain.Time) return false;
            if (GameScene.User.CurrentAction != MirAction.Standing) return false;
            if (GameScene.User.Direction != dir) return false;
            if (GameScene.User.TransformType >= 6 && GameScene.User.TransformType <= 9) return false;

            Point point = Functions.PointMove(User.CurrentLocation, dir, 3);

            if (!M2CellInfo[point.X, point.Y].FishingCell) return false;

            return true;
        }

        public bool CanFly(Point target)
        {
            Point location = User.CurrentLocation;
            while (location != target)
            {
                MirDirection dir = Functions.DirectionFromPoint(location, target);

                location = Functions.PointMove(location, dir, 1);

                if (location.X < 0 || location.Y < 0 || location.X >= GameScene.Scene.MapControl.Width || location.Y >= GameScene.Scene.MapControl.Height) return false;

                if (!GameScene.Scene.MapControl.ValidPoint(location)) return false;
            }

            return true;
        }


        public bool ValidPoint(Point p)
        {
            //GameScene.Models.Chat.Add(string.Format("cell: {0}", (M2CellInfo[p.X, p.Y].BackImage & 0x20000000)), ChatType.Hint);
            return (M2CellInfo[p.X, p.Y].BackImage & 0x20000000) == 0;
        }
        public bool HasTarget(Point p)
        {
            foreach (var ob in Objects.Values)
                if (ob.CurrentLocation == p && ob.Blocking)
                    return true;
            return false;
        }

        public bool CanHalfMoon(Point p, MirDirection d)
        {
            d = Functions.PreviousDir(d);
            for (int i = 0; i < 4; i++)
            {
                if (HasTarget(Functions.PointMove(p, d, 1))) return true;
                d = Functions.NextDir(d);
            }
            return false;
        }
        public bool CanCrossHalfMoon(Point p)
        {
            MirDirection dir = MirDirection.Up;
            for (int i = 0; i < 8; i++)
            {
                if (HasTarget(Functions.PointMove(p, dir, 1))) return true;
                dir = Functions.NextDir(dir);
            }
            return false;
        }

        #region Disposable

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Objects.Clear();

                MapButtons = 0;
                MouseLocation = Point.Empty;
                InputDelay = 0;
                NextAction = 0;

                M2CellInfo = null;
                Width = 0;
                Height = 0;

                Index = 0;
                FileName = String.Empty;
                Title = String.Empty;
                MiniMap = 0;
                BigMap = 0;
                Lights = 0;
                FloorValid = false;
                LightsValid = false;
                MapDarkLight = 0;
                Music = 0;

                AnimationCount = 0;
                Effects.Clear();
            }

            base.Dispose(disposing);
        }

        #endregion

        public void UpdateWeather()
        {
            for (int i = GameScene.Scene.ParticleEngines.Count - 1; i > 0; i--)
                GameScene.Scene.ParticleEngines[i].Dispose();

            GameScene.Scene.ParticleEngines.Clear();
            List<ParticleImageInfo> textures = new List<ParticleImageInfo>();
            foreach (WeatherSetting itemWeather in Enum.GetValues(typeof(WeatherSetting)).Cast<object>().ToArray())
            {
                //if not enabled skip
                if ((Weather & itemWeather) != itemWeather)
                    continue;

                //foreach (WeatherSetting itemWeather in Weather)
                //{
                switch (itemWeather)
                {
                    case WeatherSetting.Leaves:
                        textures = new List<ParticleImageInfo>();
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 359, 170, 50));
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 531, 55, 50));
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 587, 200, 50));


                        ParticleEngine LeavesEngine2 = new ParticleEngine(textures, new Vector2(2f, 0), ParticleType.Leaves);
                        Vector2 lVelocity = new Vector2(0F, 0F);
                        for (int y = 512 * -1; y < Settings.ScreenHeight + 512; y += 512)
                            for (int x = 512 * -1; x < Settings.ScreenWidth + 512; x += 512)
                            {
                                Particle part = LeavesEngine2.GenerateNewParticle(ParticleType.Leaves);
                                part.Position = new Vector2(x, y);
                                part.Velocity = lVelocity;
                            }
                        LeavesEngine2.GenerateParticles = false;
                        GameScene.Scene.ParticleEngines.Add(LeavesEngine2);
                        break;
                    case WeatherSetting.FireyLeaves:
                        textures = new List<ParticleImageInfo>();
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 359, 170, 50));
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 531, 55, 50));
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 587, 200, 50));


                        ParticleEngine FLeavesEngine2 = new ParticleEngine(textures, new Vector2(2f, 0), ParticleType.FireyLeaves);
                        Vector2 FlVelocity = new Vector2(0F, 0F);
                        for (int y = 512 * -1; y < Settings.ScreenHeight + 512; y += 512)
                            for (int x = 512 * -1; x < Settings.ScreenWidth + 512; x += 512)
                            {
                                Particle part = FLeavesEngine2.GenerateNewParticle(ParticleType.FireyLeaves);
                                part.Position = new Vector2(x, y);
                                part.Velocity = FlVelocity;
                            }
                        FLeavesEngine2.GenerateParticles = false;
                        GameScene.Scene.ParticleEngines.Add(FLeavesEngine2);
                        break;
                    case WeatherSetting.Rain:
                        if (AtmosphereEnabled) break; // the shader rain replaces the Weather.Lib rain sprites
                        textures = new List<ParticleImageInfo>();
                        //Rain
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 164, 150, 50));


                        ParticleEngine RainEngine2 = new ParticleEngine(textures, new Vector2(2f, 0), ParticleType.Rain);
                        Vector2 rsevelocity = new Vector2(0F, 0F);
                        var xVar = 512;
                        var yVar = 512;
                        for (int y = yVar * -1; y < Settings.ScreenHeight + yVar; y += yVar)
                            for (int x = xVar * -1; x < Settings.ScreenWidth + xVar; x += xVar)
                            {
                                Particle part = RainEngine2.GenerateNewParticle(ParticleType.Rain);
                                part.Position = new Vector2(x, y);
                                part.Velocity = rsevelocity;
                            }
                        RainEngine2.GenerateParticles = false;
                        GameScene.Scene.ParticleEngines.Add(RainEngine2);
                        break;

                    case WeatherSetting.Snow:
                        textures = new List<ParticleImageInfo>();
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 43, 20, 50));

                        ParticleEngine RainEngine = new ParticleEngine(textures, new Vector2(0, 0), ParticleType.Snow);
                        Vector2 rsvelocity = new Vector2(1F, -1F);

                        for (int y = -400; y < Settings.ScreenHeight + 400; y += 400)
                            for (int x = -400; x < Settings.ScreenWidth + 400; x += 400)
                            {
                                Particle part = RainEngine.GenerateNewParticle(ParticleType.Snow);
                                part.Position = new Vector2(x, y);
                                part.Velocity = rsvelocity;
                            }
                        RainEngine.GenerateParticles = false;
                        GameScene.Scene.ParticleEngines.Add(RainEngine);

                        break;
                    case WeatherSetting.Fog:
                        List<ParticleImageInfo> ftextures = new List<ParticleImageInfo>();
                        ftextures.Add(new ParticleImageInfo(Libraries.Weather, 0));
                        ParticleEngine fengine = new ParticleEngine(ftextures, new Vector2(0, 0), ParticleType.Fog);
                        fengine.UpdateDelay = TimeSpan.FromMilliseconds(20);

                        Vector2 fvelocity = new Vector2(2F, -2F);
                        for (int y = -512; y < Settings.ScreenHeight + 512; y += 512)
                            for (int x = -512; x < Settings.ScreenWidth + 512; x += 512)
                            {
                                Particle part = fengine.GenerateNewParticle(ParticleType.Fog);
                                part.Position = new Vector2(x, y);
                                part.Velocity = fvelocity;
                            }


                        fengine.GenerateParticles = false;
                        GameScene.Scene.ParticleEngines.Add(fengine);
                        break;
                    case WeatherSetting.RedEmber:
                        var rtextures = new List<ParticleImageInfo>();
                        rtextures.Add(new ParticleImageInfo(Libraries.Weather, 1, 9, 150));

                        var rengine = new ParticleEngine(rtextures, new Vector2(0, 0), ParticleType.RedFogEmber);
                        GameScene.Scene.ParticleEngines.Add(rengine);
                        break;
                    case WeatherSetting.WhiteEmber:

                        textures = new List<ParticleImageInfo>();
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 1, 9, 150));
                        var whiteEmberEngine = new ParticleEngine(textures, new Vector2(0, 0), ParticleType.WhiteEmber);
                        GameScene.Scene.ParticleEngines.Add(whiteEmberEngine);
                        break;
                    case WeatherSetting.PurpleLeaves:

                        textures = new List<ParticleImageInfo>();
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 359, 170, 50));
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 531, 55, 50));
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 587, 200, 50));
                        //textures.Add(new ParticleImageInfo(Libraries.Weather, 10, 20, 50));

                        var pEmberEngine = new ParticleEngine(textures, new Vector2(0, 0), ParticleType.PurpleLeaves);

                        for (int y = 512 * -1; y < Settings.ScreenHeight + 512; y += 512)
                            for (int x = 512 * -1; x < Settings.ScreenWidth + 512; x += 512)
                            {
                                Particle part = pEmberEngine.GenerateNewParticle(ParticleType.PurpleLeaves);
                                part.Position = new Vector2(x, y);
                                part.Velocity = new Vector2(0, 0);
                            }
                        pEmberEngine.GenerateParticles = false;
                        GameScene.Scene.ParticleEngines.Add(pEmberEngine);
                        break;

                    case WeatherSetting.YellowEmber:

                        textures = new List<ParticleImageInfo>();
                        textures.Add(new ParticleImageInfo(Libraries.Weather, 1, 9, 100));

                        var yellowEmberEngine = new ParticleEngine(textures, new Vector2(0, 0), ParticleType.YellowEmber);
                        GameScene.Scene.ParticleEngines.Add(yellowEmberEngine);
                        break;
                    case WeatherSetting.FireParticle:

                        textures = new List<ParticleImageInfo>();
                        //textures.Add(new ParticleImageInfo(Libraries.StateEffect, 640)); << TODO - Win
                        //   textures.Add(new ParticleImageInfo(Libraries.Prguse4, 642));
                        var fEmberEngine = new ParticleEngine(textures, new Vector2(0, 0), ParticleType.Bird);
                        GameScene.Scene.ParticleEngines.Add(fEmberEngine);
                        break;


                }

            }

        }

        public void RemoveObject(MapObject ob)
        {
            M2CellInfo[ob.MapLocation.X, ob.MapLocation.Y].RemoveObject(ob);
            ob.InvalidateMap();
        }
        public void AddObject(MapObject ob)
        {
            M2CellInfo[ob.MapLocation.X, ob.MapLocation.Y].AddObject(ob);
            ob.InvalidateMap(); // new items, spells, decorations and spawns used to show only on the 100 ms tick
        }
        public MapObject FindObject(uint ObjectID, int x, int y)
        {
            return M2CellInfo[x, y].FindObject(ObjectID);
        }
        public void SortObject(MapObject ob)
        {
            M2CellInfo[ob.MapLocation.X, ob.MapLocation.Y].Sort();
        }

        public Door GetDoor(byte Index)
        {
            for (int i = 0; i < Doors.Count; i++)
            {
                if (Doors[i].index == Index)
                    return Doors[i];
            }
            return null;
        }
        public void Processdoors()
        {
            for (int i = 0; i < Doors.Count; i++)
            {
                if ((Doors[i].DoorState == DoorState.Opening) || (Doors[i].DoorState == DoorState.Closing))
                {
                    if (Doors[i].LastTick + 50 < CMain.Time)
                    {
                        Doors[i].LastTick = CMain.Time;
                        Doors[i].ImageIndex++;

                        if (Doors[i].ImageIndex == 1)//change the 1 if you want to animate doors opening/closing
                        {
                            Doors[i].ImageIndex = 0;
                            Doors[i].DoorState = (DoorState)Enum.ToObject(typeof(DoorState), ((byte)++Doors[i].DoorState % 4));
                        }

                        FloorValid = false;
                        TextureValid = false; // doors are drawn in the floor and the front layer
                    }
                }
                if (Doors[i].DoorState == DoorState.Open)
                {
                    if (Doors[i].LastTick + 5000 < CMain.Time)
                    {
                        Doors[i].LastTick = CMain.Time;
                        Doors[i].DoorState = DoorState.Closing;
                        FloorValid = false;
                        TextureValid = false;
                    }
                }
            }
        }
        public void OpenDoor(byte Index, bool closed)
        {
            Door Info = GetDoor(Index);
            if (Info == null) return;
            Info.DoorState = (closed ? DoorState.Closing : Info.DoorState == DoorState.Open ? DoorState.Open : DoorState.Opening);
            Info.ImageIndex = 0;
            Info.LastTick = CMain.Time;
            FloorValid = false;
            TextureValid = false;
        }
    }
}


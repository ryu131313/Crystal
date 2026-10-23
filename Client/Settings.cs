using Client.MirGraphics.Rendering;
using Client.MirScenes;
using Client.MirSounds;

namespace Client
{
    class Settings
    {
        public const long CleanDelay = 600000;

        public static int ScreenWidth = 1024, ScreenHeight = 768;
        private static InIReader Reader = new InIReader(@".\Mir2Config.ini");
        private static InIReader QuestTrackingReader = new InIReader(Path.Combine(UserDataPath, @".\QuestTracking.ini"));

        private static bool _useTestConfig;
        public static bool UseTestConfig
        {
            get
            {
                return _useTestConfig;
            }
            set
            {
                if (value == true)
                {
                    Reader = new InIReader(@".\Mir2Test.ini");
                }
                _useTestConfig = value;
            }
        }

        public const string DataPath = @".\Data\",
                            MapPath = @".\Map\",
                            SoundPath = @".\Sound\",
                            ExtraDataPath = @".\Data\Extra\",
                            ShadersPath = @".\Data\Shaders\",
                            MonsterPath = @".\Data\Monster\",
                            GatePath = @".\Data\Gate\",
                            FlagPath = @".\Data\Flag\",
                            SiegePath = @".\Data\Siege\",
                            NPCPath = @".\Data\NPC\",
                            CArmourPath = @".\Data\CArmour\",
                            CWeaponPath = @".\Data\CWeapon\",
                            CWeaponEffectPath = @".\Data\CWeaponEffect\",
                            CHairPath = @".\Data\CHair\",
                            AArmourPath = @".\Data\AArmour\",
                            AWeaponPath = @".\Data\AWeapon\",
                            AHairPath = @".\Data\AHair\",
                            ARArmourPath = @".\Data\ARArmour\",
                            ARWeaponPath = @".\Data\ARWeapon\",
                            ARHairPath = @".\Data\ARHair\",
                            CHumEffectPath = @".\Data\CHumEffect\",
                            AHumEffectPath = @".\Data\AHumEffect\",
                            ARHumEffectPath = @".\Data\ARHumEffect\",
                            MountPath = @".\Data\Mount\",
                            FishingPath = @".\Data\Fishing\",
                            PetsPath = @".\Data\Pet\",
                            TransformPath = @".\Data\Transform\",
                            TransformMountsPath = @".\Data\TransformRide2\",
                            TransformEffectPath = @".\Data\TransformEffect\",
                            TransformWeaponEffectPath = @".\Data\TransformWeaponEffect\",
                            MouseCursorPath = @".\Data\Cursors\",
                            UserDataPath = @".\Data\UserData\",
                            DbLanguageJsonPath = @".\DbLanguage.json";

        //Logs
        public static bool LogErrors = true;
        public static bool LogChat = true;
        public static int RemainingErrorLogs = 100;

        //Graphics
        public static bool FullScreen = false, Borderless = false, TopMost = true, MouseClip = false; // windowed by default (2026-10-20)
        public static string FontName = "Arial"; //"MS Sans Serif"
        public static float FontSize = 8F;
        public static bool UseMouseCursors = true;

        public static bool FPSCap = true;
        public static int MaxFPS = 100;
        public static RendererType Renderer = RendererType.D3D11; // [Graphics] Renderer: D3D11 (default since roadmap C4 QA, falls back to D3D9 if it cannot start) or D3D9
        // [Interface] UiStyle: Default / Modern / Classic (Desktop-Modern-UI-Plan D6). Default resolves to Modern (the new UI is
        // the default since 2026-10-20); a saved Classic always wins. Read once per GameScene (UiStyles.IsModern), so a change takes effect on the next log in.
        public static UiStyle UiStyle = UiStyle.Default;
        // [Interface] the modern UI (Desktop-Modern-UI-Plan phase 1b): theme preset name (ModernTheme.Presets, saved by name) and
        // custom overrides (a negative number / empty string = use the preset's), HUD scale (UiScale x screen height / 768 when
        // UiAutoScale), window opacity, stat plates, and window positions in HUD px ("key=x,y;key=x,y").
        public static string UiTheme = "Royal gold";
        public static float UiCustomHue = -1, UiCustomSat = -1, UiCustomLit = -1, UiCustomGlow = -1, UiCustomRadius = -1;
        public static string UiCustomAccent = "", UiCustomFrame = "", UiCustomText = "";
        public static float UiScale = 1F;
        public static bool UiAutoScale = true;
        public static float UiOpacity = 1F;
        public static bool UiPlates = true;
        /// <summary>The six stat plate picks ("AC,MAC,DC,Bag,Weight,Gold"; empty = defaults; HudPlateStore parses).</summary>
        public static string UiPlateStats = "";
        public static string UiWindows = "";
        /// <summary>Modern HUD prefs (HudPrefs): chat box "left,bottom,w,h,opacity,fade,font,time,tags,lock"; minimap "zoom,square,shut";
        /// big-map layers (names); waypoints "character|map|x.y,x.y;…"; read help topics (ids); quest tracker folded.</summary>
        public static string UiChat = "", UiMinimap = "", UiBigMap = "", UiWaypoints = "", UiHelpRead = "";
        public static bool UiTrackerFolded = false;
        public static int Resolution = 1920; // 1920x1080 by default; Program.CheckResolutionSetting falls back when the screen cannot show it
        public static bool DebugMode = false;
        // [Graphics] RebuildOptimisations: fewer UI/map render-target rebuilds (live skill-bar/minimap overlays, no blind 100 ms map
        // tick, off-screen frames skipped). False restores the old invalidation behaviour for A/B checks and rollback.
        public static bool RebuildOptimisations = true;
        // [Graphics] Lighting: Classic (default) or Enhanced (D3D11 only; D3D9 always draws Classic). In game: @light new / @light old.
        public static LightingMode Lighting = LightingMode.Enhanced; // enhanced lighting + weather by default (2026-10-20; D3D9 falls back)
        // [Graphics] Atmosphere: shader rain and lightning on weather maps (D3D11 + Enhanced lighting only). In game: @weather on / @weather off.
        public static bool Atmosphere = true;
        // [Graphics] Reflections: wet-ground reflections of objects and lamps (D3D11 + Enhanced lighting only; no in-game command).
        public static bool Reflections = true;

        //Network
        public static bool UseConfig = false;
        public static string IPAddress = "127.0.0.1";
        /// <summary>
        /// [Network] Relay: a WebRelay WebSocket URL (wss://host/mount/ws). When set the client reaches the game server through
        /// the relay, as the web client does (no open game port needed), and IPAddress / Port are not used.
        /// </summary>
        public static string Relay = string.Empty;
        public static int Port = 7000;
        public const int TimeOut = 5000;

        //Sound
        public static int SoundOverLap = 3;
        private static byte _volume = 100;
        public static int SoundCleanMinutes = 5;

        public static byte Volume
        {
            get { return _volume; }
            set
            {
                switch (value)
                {
                    case > 100:
                        _volume = (byte)100;
                        break;
                    case <= 0:
                        _volume = (byte)0;
                        break;
                    default:
                        _volume = value;
                        break;
                }

                SoundManager.Vol = Convert.ToInt32(_volume);
            }
        }

        private static byte _musicVolume = 100;
        public static byte MusicVolume
        {
            get { return _musicVolume; }
            set
            {
                switch (value)
                {
                    case > 100:
                        _musicVolume = (byte)100;
                        break;
                    case <= 0:
                        _musicVolume = (byte)0;
                        break;
                    default:
                        _musicVolume = value;
                        break;
                }

                SoundManager.MusicVol = Convert.ToInt32(_musicVolume);
            }
        }

        //Game
        public static string AccountID = "",
                             Password = "";

        public static bool
            SkillMode = false,
            SkillBar = true,
            //SkillSet = true,
            Effect = true,
            LevelEffect = true,
            DropView = true,
            NameView = true,
            HPView = true,
            TransparentChat = false,
            ModeView = false,
            DuraView = false,
            DisplayDamage = true,
            TargetDead = false,
            HighlightTarget = true,
            ExpandedBuffWindow = true,
            ExpandedHeroBuffWindow = true,
            DisplayBodyName = false,
            NewMove = false,
            // [Game] SmoothMove: move between cells in even steps between animation frames (default on, 2026-10-19). In game: @smooth on / @smooth off.
            SmoothMove = true;

        public static string Language = "English";

        public static int[,] SkillbarLocation = new int[2, 2] { { 0, 0 }, { 216, 0 } };

        //Quests
        public static int[] TrackedQuests = new int[5];

        //Chat
        public static bool
            ShowNormalChat = true,
            ShowYellChat = true,
            ShowWhisperChat = true,
            ShowLoverChat = true,
            ShowMentorChat = true,
            ShowGroupChat = true,
            ShowGuildChat = true;

        //Filters
        public static bool
            FilterNormalChat = false,
            FilterWhisperChat = false,
            FilterShoutChat = false,
            FilterSystemChat = false,
            FilterLoverChat = false,
            FilterMentorChat = false,
            FilterGroupChat = false,
            FilterGuildChat = false;


        //AutoPatcher
        public static bool P_Patcher = true;
        /// <summary>[Launcher] Host: the WebRelay patch host (--patch-root) URL including its mount, ending in '/'. Empty by
        /// default (no patching; a server ships Mir2Config.ini with its own). https only, except a loopback host
        /// (<see cref="Client.Patching.PatchPaths.IsAllowedHost"/>); the old default asked upstream's domain over plain http.</summary>
        public static string P_Host = string.Empty;
        public static string P_ServerName = string.Empty;
        public static string P_Client = Application.StartupPath + "\\";
        public static int P_Concurrency = 6; // [Launcher] ConcurrentDownloads: background download workers (2 more are kept for priority files)
        /// <summary>[Launcher] BackgroundDownloads: false pauses the in-game background queue (demanded files still download).</summary>
        public static bool P_BackgroundDownloads = true;
        /// <summary>[Launcher] RefreshMinutes: how often the in-game patcher re-reads /api/patch (1..1440, default 15).</summary>
        public static int P_RefreshMinutes = 15;

        public static void Load()
        {


            if (!Directory.Exists(DataPath)) Directory.CreateDirectory(DataPath);
            if (!Directory.Exists(MapPath)) Directory.CreateDirectory(MapPath);
            if (!Directory.Exists(SoundPath)) Directory.CreateDirectory(SoundPath);

            //Graphics
            FullScreen = Reader.ReadBoolean("Graphics", "FullScreen", FullScreen);
            Borderless = Reader.ReadBoolean("Graphics", "Borderless", Borderless);
            MouseClip = Reader.ReadBoolean("Graphics", "MouseClip", MouseClip);
            TopMost = Reader.ReadBoolean("Graphics", "AlwaysOnTop", TopMost);
            FPSCap = Reader.ReadBoolean("Graphics", "FPSCap", FPSCap);
            MaxFPS = Math.Max(10, Reader.ReadInt32("Graphics", "MaxFPS", MaxFPS)); // roadmap C1: was never read
            Renderer = Enum.TryParse(Reader.ReadString("Graphics", "Renderer", Renderer.ToString()), true, out RendererType renderer) ? renderer : RendererType.D3D11;
            Resolution = Reader.ReadInt32("Graphics", "Resolution", Resolution);
            DebugMode = Reader.ReadBoolean("Graphics", "DebugMode", DebugMode);
            RebuildOptimisations = Reader.ReadBoolean("Graphics", "RebuildOptimisations", RebuildOptimisations);
            Lighting = Enum.TryParse(Reader.ReadString("Graphics", "Lighting", Lighting.ToString()), true, out LightingMode lighting) ? lighting : LightingMode.Classic;
            Atmosphere = Reader.ReadBoolean("Graphics", "Atmosphere", Atmosphere);
            Reflections = Reader.ReadBoolean("Graphics", "Reflections", Reflections);
            UseMouseCursors = Reader.ReadBoolean("Graphics", "UseMouseCursors", UseMouseCursors);

            //Interface
            UiStyle = Enum.TryParse(Reader.ReadString("Interface", "UiStyle", UiStyle.ToString()), true, out UiStyle uiStyle) && Enum.IsDefined(uiStyle) ? uiStyle : UiStyle.Default;
            UiTheme = Reader.ReadString("Interface", "UiTheme", UiTheme);
            UiCustomHue = Reader.ReadSingle("Interface", "UiCustomHue", UiCustomHue);
            UiCustomSat = Reader.ReadSingle("Interface", "UiCustomSat", UiCustomSat);
            UiCustomLit = Reader.ReadSingle("Interface", "UiCustomLit", UiCustomLit);
            UiCustomGlow = Reader.ReadSingle("Interface", "UiCustomGlow", UiCustomGlow);
            UiCustomRadius = Reader.ReadSingle("Interface", "UiCustomRadius", UiCustomRadius);
            UiCustomAccent = Reader.ReadString("Interface", "UiCustomAccent", UiCustomAccent);
            UiCustomFrame = Reader.ReadString("Interface", "UiCustomFrame", UiCustomFrame);
            UiCustomText = Reader.ReadString("Interface", "UiCustomText", UiCustomText);
            UiScale = Reader.ReadSingle("Interface", "UiScale", UiScale);
            UiScale = float.IsFinite(UiScale) ? Math.Clamp(UiScale, 0.5F, 3F) : 1F;
            UiAutoScale = Reader.ReadBoolean("Interface", "UiAutoScale", UiAutoScale);
            UiOpacity = Reader.ReadSingle("Interface", "UiOpacity", UiOpacity);
            UiOpacity = float.IsFinite(UiOpacity) ? Math.Clamp(UiOpacity, 0.3F, 1F) : 1F;
            UiPlates = Reader.ReadBoolean("Interface", "UiPlates", UiPlates);
            UiPlateStats = Reader.ReadString("Interface", "UiPlateStats", UiPlateStats);
            UiWindows = Reader.ReadString("Interface", "UiWindows", UiWindows);
            UiChat = Reader.ReadString("Interface", "UiChat", UiChat);
            UiMinimap = Reader.ReadString("Interface", "UiMinimap", UiMinimap);
            UiBigMap = Reader.ReadString("Interface", "UiBigMap", UiBigMap);
            UiWaypoints = Reader.ReadString("Interface", "UiWaypoints", UiWaypoints);
            UiHelpRead = Reader.ReadString("Interface", "UiHelpRead", UiHelpRead);
            UiTrackerFolded = Reader.ReadBoolean("Interface", "UiTrackerFolded", UiTrackerFolded);

            //Network
            UseConfig = Reader.ReadBoolean("Network", "UseConfig", UseConfig);
            Relay = Reader.ReadString("Network", "Relay", Relay).Trim();
            if (UseConfig)
            {
                IPAddress = Reader.ReadString("Network", "IPAddress", IPAddress);
                Port = Reader.ReadInt32("Network", "Port", Port);
            }

            //Logs
            LogErrors = Reader.ReadBoolean("Logs", "LogErrors", LogErrors);
            LogChat = Reader.ReadBoolean("Logs", "LogChat", LogChat);

            //Sound
            Volume = Reader.ReadByte("Sound", "Volume", Volume);
            SoundOverLap = Reader.ReadInt32("Sound", "SoundOverLap", SoundOverLap);
            MusicVolume = Reader.ReadByte("Sound", "Music", MusicVolume);
            var n = Reader.ReadInt32("Sound", "CleanMinutes", SoundCleanMinutes);
            if (n < 1 || n > 60 * 3) n = SoundCleanMinutes;
            SoundCleanMinutes = n;


            //Game
            AccountID = Reader.ReadString("Game", "AccountID", AccountID);
            Password = Reader.ReadString("Game", "Password", Password);

            SkillMode = Reader.ReadBoolean("Game", "SkillMode", SkillMode);
            SkillBar = Reader.ReadBoolean("Game", "SkillBar", SkillBar);
            //SkillSet = Reader.ReadBoolean("Game", "SkillSet", SkillSet);
            Effect = Reader.ReadBoolean("Game", "Effect", Effect);
            LevelEffect = Reader.ReadBoolean("Game", "LevelEffect", LevelEffect); // roadmap C1: defaulted to Effect
            DropView = Reader.ReadBoolean("Game", "DropView", DropView);
            NameView = Reader.ReadBoolean("Game", "NameView", NameView);
            HPView = Reader.ReadBoolean("Game", "HPMPView", HPView);
            ModeView = Reader.ReadBoolean("Game", "ModeView", ModeView);
            FontName = Reader.ReadString("Game", "FontName", FontName);
            TransparentChat = Reader.ReadBoolean("Game", "TransparentChat", TransparentChat);
            DisplayDamage = Reader.ReadBoolean("Game", "DisplayDamage", DisplayDamage);
            TargetDead = Reader.ReadBoolean("Game", "TargetDead", TargetDead);
            HighlightTarget = Reader.ReadBoolean("Game", "HighlightTarget", HighlightTarget);
            ExpandedBuffWindow = Reader.ReadBoolean("Game", "ExpandedBuffWindow", ExpandedBuffWindow);
            ExpandedHeroBuffWindow = Reader.ReadBoolean("Game", "ExpandedHeroBuffWindow", ExpandedHeroBuffWindow);
            DuraView = Reader.ReadBoolean("Game", "DuraWindow", DuraView);
            DisplayBodyName = Reader.ReadBoolean("Game", "DisplayBodyName", DisplayBodyName);
            NewMove = Reader.ReadBoolean("Game", "NewMove", NewMove);
            SmoothMove = Reader.ReadBoolean("Game", "SmoothMove", SmoothMove);
            Language = Reader.ReadString("Game", "Language", Language);

            for (int i = 0; i < SkillbarLocation.Length / 2; i++)
            {
                SkillbarLocation[i, 0] = Reader.ReadInt32("Game", "Skillbar" + i.ToString() + "X", SkillbarLocation[i, 0]);
                SkillbarLocation[i, 1] = Reader.ReadInt32("Game", "Skillbar" + i.ToString() + "Y", SkillbarLocation[i, 1]);
            }

            //Chat
            ShowNormalChat = Reader.ReadBoolean("Chat", "ShowNormalChat", ShowNormalChat);
            ShowYellChat = Reader.ReadBoolean("Chat", "ShowYellChat", ShowYellChat);
            ShowWhisperChat = Reader.ReadBoolean("Chat", "ShowWhisperChat", ShowWhisperChat);
            ShowLoverChat = Reader.ReadBoolean("Chat", "ShowLoverChat", ShowLoverChat);
            ShowMentorChat = Reader.ReadBoolean("Chat", "ShowMentorChat", ShowMentorChat);
            ShowGroupChat = Reader.ReadBoolean("Chat", "ShowGroupChat", ShowGroupChat);
            ShowGuildChat = Reader.ReadBoolean("Chat", "ShowGuildChat", ShowGuildChat);

            //Filters
            FilterNormalChat = Reader.ReadBoolean("Filter", "FilterNormalChat", FilterNormalChat);
            FilterWhisperChat = Reader.ReadBoolean("Filter", "FilterWhisperChat", FilterWhisperChat);
            FilterShoutChat = Reader.ReadBoolean("Filter", "FilterShoutChat", FilterShoutChat);
            FilterSystemChat = Reader.ReadBoolean("Filter", "FilterSystemChat", FilterSystemChat);
            FilterLoverChat = Reader.ReadBoolean("Filter", "FilterLoverChat", FilterLoverChat);
            FilterMentorChat = Reader.ReadBoolean("Filter", "FilterMentorChat", FilterMentorChat);
            FilterGroupChat = Reader.ReadBoolean("Filter", "FilterGroupChat", FilterGroupChat);
            FilterGuildChat = Reader.ReadBoolean("Filter", "FilterGuildChat", FilterGuildChat);

            //AutoPatcher
            P_Patcher = Reader.ReadBoolean("Launcher", "Enabled", P_Patcher);
            P_Host = Reader.ReadString("Launcher", "Host", P_Host);
            P_ServerName = Reader.ReadString("Launcher", "ServerName", P_ServerName);
            P_BackgroundDownloads = Reader.ReadBoolean("Launcher", "BackgroundDownloads", P_BackgroundDownloads);
            P_RefreshMinutes = Math.Clamp(Reader.ReadInt32("Launcher", "RefreshMinutes", P_RefreshMinutes), 1, 1440);
            P_Concurrency = Reader.ReadInt32("Launcher", "ConcurrentDownloads", P_Concurrency);


            P_Host = P_Host.Trim();
            // The old built-in default (upstream's plain-http host, no patch routes) was saved into every ini: drop it.
            if (P_Host.StartsWith("http://mirfiles.com/", StringComparison.OrdinalIgnoreCase)) P_Host = string.Empty;
            if (P_Host.Length > 0 && !P_Host.EndsWith("/")) P_Host += "/";
            if (P_Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) P_Host = P_Host.Insert(0, "https://");

            if (P_Concurrency < 1) P_Concurrency = 1;
            if (P_Concurrency > 100) P_Concurrency = 100;

            try
            {
                string languageDirectory = @".\Localization\";
                if (!Directory.Exists(languageDirectory))
                {
                    Directory.CreateDirectory(languageDirectory);
                }
                string settingLanguageFile = Path.Combine(languageDirectory, Language + ".json");
                GameLanguage.LoadClientLanguage(settingLanguageFile);
            }
            catch (Exception ex)
            {
                CMain.SaveError($"Load Client Language Error:{ex.Message}");
            }
            
        }

        public static void Save()
        {
            //Graphics
            Reader.Write("Graphics", "FullScreen", FullScreen);
            Reader.Write("Graphics", "Borderless", Borderless);
            Reader.Write("Graphics", "MouseClip", MouseClip);
            Reader.Write("Graphics", "AlwaysOnTop", TopMost);
            Reader.Write("Graphics", "FPSCap", FPSCap);
            Reader.Write("Graphics", "MaxFPS", MaxFPS);
            Reader.Write("Graphics", "Renderer", Renderer.ToString());
            Reader.Write("Interface", "UiStyle", UiStyle.ToString());
            Reader.Write("Interface", "UiTheme", UiTheme);
            Reader.Write("Interface", "UiCustomHue", UiCustomHue);
            Reader.Write("Interface", "UiCustomSat", UiCustomSat);
            Reader.Write("Interface", "UiCustomLit", UiCustomLit);
            Reader.Write("Interface", "UiCustomGlow", UiCustomGlow);
            Reader.Write("Interface", "UiCustomRadius", UiCustomRadius);
            Reader.Write("Interface", "UiCustomAccent", UiCustomAccent);
            Reader.Write("Interface", "UiCustomFrame", UiCustomFrame);
            Reader.Write("Interface", "UiCustomText", UiCustomText);
            Reader.Write("Interface", "UiScale", UiScale);
            Reader.Write("Interface", "UiAutoScale", UiAutoScale);
            Reader.Write("Interface", "UiOpacity", UiOpacity);
            Reader.Write("Interface", "UiPlates", UiPlates);
            Reader.Write("Interface", "UiPlateStats", UiPlateStats);
            Reader.Write("Interface", "UiWindows", UiWindows);
            Reader.Write("Interface", "UiChat", UiChat);
            Reader.Write("Interface", "UiMinimap", UiMinimap);
            Reader.Write("Interface", "UiBigMap", UiBigMap);
            Reader.Write("Interface", "UiWaypoints", UiWaypoints);
            Reader.Write("Interface", "UiHelpRead", UiHelpRead);
            Reader.Write("Interface", "UiTrackerFolded", UiTrackerFolded);
            Reader.Write("Logs", "LogErrors", LogErrors); // roadmap C1: [Logs] was load-only
            Reader.Write("Logs", "LogChat", LogChat);
            Reader.Write("Graphics", "Resolution", Resolution);
            Reader.Write("Graphics", "DebugMode", DebugMode);
            Reader.Write("Graphics", "RebuildOptimisations", RebuildOptimisations);
            Reader.Write("Graphics", "Lighting", Lighting.ToString());
            Reader.Write("Graphics", "Atmosphere", Atmosphere);
            Reader.Write("Graphics", "Reflections", Reflections);
            Reader.Write("Graphics", "UseMouseCursors", UseMouseCursors);

            //Sound
            Reader.Write("Sound", "Volume", Volume);
            Reader.Write("Sound", "SoundOverLap", SoundOverLap);
            Reader.Write("Sound", "Music", MusicVolume);
            Reader.Write("Sound", "CleanMinutes", SoundCleanMinutes);

            //Game
            Reader.Write("Game", "AccountID", AccountID);
            Reader.Write("Game", "Password", Password);
            Reader.Write("Game", "SkillMode", SkillMode);
            Reader.Write("Game", "SkillBar", SkillBar);
            //Reader.Write("Game", "SkillSet", SkillSet);
            Reader.Write("Game", "Effect", Effect);
            Reader.Write("Game", "LevelEffect", LevelEffect);
            Reader.Write("Game", "DropView", DropView);
            Reader.Write("Game", "NameView", NameView);
            Reader.Write("Game", "HPMPView", HPView);
            Reader.Write("Game", "ModeView", ModeView);
            Reader.Write("Game", "FontName", FontName);
            Reader.Write("Game", "TransparentChat", TransparentChat);
            Reader.Write("Game", "DisplayDamage", DisplayDamage);
            Reader.Write("Game", "TargetDead", TargetDead);
            Reader.Write("Game", "HighlightTarget", HighlightTarget);
            Reader.Write("Game", "ExpandedBuffWindow", ExpandedBuffWindow);
            Reader.Write("Game", "ExpandedHeroBuffWindow", ExpandedHeroBuffWindow); // roadmap C1: saved ExpandedBuffWindow
            Reader.Write("Game", "DuraWindow", DuraView);
            Reader.Write("Game", "DisplayBodyName", DisplayBodyName);
            Reader.Write("Game", "NewMove", NewMove);
            Reader.Write("Game", "SmoothMove", SmoothMove);
            Reader.Write("Game", "Language", Language);

            for (int i = 0; i < SkillbarLocation.Length / 2; i++)
            {

                Reader.Write("Game", "Skillbar" + i.ToString() + "X", SkillbarLocation[i, 0]);
                Reader.Write("Game", "Skillbar" + i.ToString() + "Y", SkillbarLocation[i, 1]);
            }

            //Chat
            Reader.Write("Chat", "ShowNormalChat", ShowNormalChat);
            Reader.Write("Chat", "ShowYellChat", ShowYellChat);
            Reader.Write("Chat", "ShowWhisperChat", ShowWhisperChat);
            Reader.Write("Chat", "ShowLoverChat", ShowLoverChat);
            Reader.Write("Chat", "ShowMentorChat", ShowMentorChat);
            Reader.Write("Chat", "ShowGroupChat", ShowGroupChat);
            Reader.Write("Chat", "ShowGuildChat", ShowGuildChat);

            //Filters
            Reader.Write("Filter", "FilterNormalChat", FilterNormalChat);
            Reader.Write("Filter", "FilterWhisperChat", FilterWhisperChat);
            Reader.Write("Filter", "FilterShoutChat", FilterShoutChat);
            Reader.Write("Filter", "FilterSystemChat", FilterSystemChat);
            Reader.Write("Filter", "FilterLoverChat", FilterLoverChat);
            Reader.Write("Filter", "FilterMentorChat", FilterMentorChat);
            Reader.Write("Filter", "FilterGroupChat", FilterGroupChat);
            Reader.Write("Filter", "FilterGuildChat", FilterGuildChat);

            //AutoPatcher
            Reader.Write("Launcher", "Enabled", P_Patcher);
            Reader.Write("Launcher", "Host", P_Host);
            Reader.Write("Launcher", "ServerName", P_ServerName);
            Reader.Write("Launcher", "ConcurrentDownloads", P_Concurrency);
            Reader.Write("Launcher", "BackgroundDownloads", P_BackgroundDownloads);
            Reader.Write("Launcher", "RefreshMinutes", P_RefreshMinutes);
            Reader.Write("Network", "Relay", Relay); // the only [Network] key Save writes ([Network] is otherwise load-only: Known debt)
        }

        public static void LoadTrackedQuests(string charName)
        {
            //Quests
            for (int i = 0; i < TrackedQuests.Length; i++)
            {
                TrackedQuests[i] = QuestTrackingReader.ReadInt32(charName, "Quest-" + i.ToString(), -1);
            }
        }

        public static void SaveTrackedQuests(string charName)
        {
            //Quests
            for (int i = 0; i < TrackedQuests.Length; i++)
            {
                QuestTrackingReader.Write(charName, "Quest-" + i.ToString(), TrackedQuests[i]);
            }
        }
    }


}

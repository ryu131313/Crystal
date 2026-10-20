using System.Diagnostics;
using Launcher;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Client.Resolution;
using Client.Patching;

namespace Client
{
    internal static class Program
    {
        public static CMain Form;
        public static AMain PForm;

        public static bool Restart;
        public static bool Launch;

        [STAThread]
        private static void Main(string[] args)
        {
            // In-game patcher: wait for a restarting parent and recover from a program update that never reached the game.
            // First, and in a method of its own, so it runs even when a new Client.dll and an old Shared.dll do not match.
            PatchApply.Startup(args);
            if (PatchApply.ExitNow) return;
            Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run(string[] args)
        {
            if (args.Length > 0)
            {
                foreach (var arg in args)
                {
                    if (arg.ToLower() == "-tc") Settings.UseTestConfig = true;
                }
            }

            #if DEBUG
                Settings.UseTestConfig = true;
            #endif

            // Roadmap C1: golden render tests. --rendercompare needs no window at all.
            bool renderTest = RenderTest.RenderTestRunner.ParseArgs(args);
            if (RenderTest.RenderTestRunner.CompareMode)
            {
                Environment.ExitCode = RenderTest.RenderTestRunner.Compare();
                return;
            }

            try
            {
                if (RuntimePolicyHelper.LegacyV2RuntimeEnabledSuccessfully == true) { }

                Packet.IsServer = false;

                // roadmap C1: record unhandled exceptions (e.g. on the socket thread) in Error.txt before the process dies.
                AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                    CMain.SaveError("Unhandled exception" + (e.IsTerminating ? " (terminating)" : "") + ": " + e.ExceptionObject);
                Settings.Load();
                if (PatchApply.PendingLog != null) { CMain.SaveError(PatchApply.PendingLog); PatchApply.PendingLog = null; }
                if (renderTest) RenderTest.RenderTestRunner.ApplySettings();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                if (!renderTest) CheckResolutionSetting();

                Launch = false;
                if (Settings.P_Patcher)
                    Application.Run(PForm = new AMain());
                else
                    Launch = true;

                // In-game patcher: background downloads run while the game does (client.md §3.2 exception).
                if (Launch) PatchService.StartBackground();

                if (Launch)
                    Application.Run(Form = new CMain());

                PatchService.Stop(); // saves State.bin / Staged.bin; patch.lock is kept until a staged update is applied

                if (renderTest) // test and smoke runs never write the user's ini
                {
                    PatchService.ReleaseLock();
                    return;
                }
                Settings.Save();

                // In-game patcher, program updates: a complete staged set is applied on the way out (Restart or a normal
                // exit), then, for Restart, the new build starts with --wait-pid.
                if (PatchService.TakeApplySet(out var update))
                {
                    bool restartAfter = Restart || PatchService.RestartRequested;
                    bool applied = PatchApply.Apply(update);
                    PatchService.ReleaseLock();
                    if (PatchApply.PendingLog != null) CMain.SaveError(PatchApply.PendingLog);
                    if (applied && restartAfter) PatchApply.Restart(args);
                    else if (!applied && restartAfter)
                        MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchUpdateFailed));
                    return;
                }
                PatchService.ReleaseLock();

                if (Restart)
                {
                    Application.Restart();
                }
            }
            catch (Exception ex)
            {
                CMain.SaveError(ex.ToString());
            }
        }

        public static class RuntimePolicyHelper
        {
            public static bool LegacyV2RuntimeEnabledSuccessfully { get; private set; }

            static RuntimePolicyHelper()
            {
                //ICLRRuntimeInfo clrRuntimeInfo =
                //    (ICLRRuntimeInfo)RuntimeEnvironment.GetRuntimeInterfaceAsObject(
                //        Guid.Empty,
                //        typeof(ICLRRuntimeInfo).GUID);

                //try
                //{
                //    clrRuntimeInfo.BindAsLegacyV2Runtime();
                //    LegacyV2RuntimeEnabledSuccessfully = true;
                //}
                //catch (COMException)
                //{
                //    // This occurs with an HRESULT meaning 
                //    // "A different runtime was already bound to the legacy CLR version 2 activation policy."
                //    LegacyV2RuntimeEnabledSuccessfully = false;
                //}
            }

            [ComImport]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            [Guid("BD39D1D2-BA2F-486A-89B0-B4B0CB466891")]
            private interface ICLRRuntimeInfo
            {
                void xGetVersionString();
                void xGetRuntimeDirectory();
                void xIsLoaded();
                void xIsLoadable();
                void xLoadErrorString();
                void xLoadLibrary();
                void xGetProcAddress();
                void xGetInterface();
                void xSetDefaultStartupFlags();
                void xGetDefaultStartupFlags();

                [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
                void BindAsLegacyV2Runtime();
            }
        }

        public static void CheckResolutionSetting()
        {
            var parsedOK = DisplayResolutions.GetDisplayResolutions();
            if (!parsedOK)
            {
                MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.CouldNotGetDisplayResolutions), GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.GetDisplayResolutionIssue), MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(0);
            }

            if (!DisplayResolutions.IsSupported(Settings.Resolution))
            {
                MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.ClientNotSupportSettingResolution), Settings.Resolution),
                    GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.InvalidClientResolution),
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);

                Settings.Resolution = (int)eSupportedResolution.w1024h768;
                Settings.Save();
            }
        }

    }
}
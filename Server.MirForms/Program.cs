using log4net;
using Server.WebAdmin;
using System.Reflection;

namespace Server.MirForms
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Packet.IsServer = true;

            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
            log4net.Config.XmlConfigurator.Configure(logRepository, new FileInfo("log4net.config"));

            CrashReporter.Install();

            try
            {
                Settings.Load();

                if (Settings.WebAdminEnabled)
                {
                    try
                    {
                        WebAdminHost.Start();
                    }
                    catch (Exception ex)
                    {
                        Logger.GetLogger().Error(ex);
                    }
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var mainForm = new SMain();

                // Web admin "Shut down Server.exe": close the main form on the UI thread (runs the normal FormClosing -> Envir.Stop path).
                Action shutdown = () =>
                {
                    try
                    {
                        if (!mainForm.IsDisposed && mainForm.IsHandleCreated)
                            mainForm.BeginInvoke(new Action(mainForm.Close));
                    }
                    catch (Exception ex)
                    {
                        Logger.GetLogger().Error(ex);
                    }
                };
                WebAdminHost.ShutdownRequested += shutdown;

                Application.Run(mainForm);

                WebAdminHost.ShutdownRequested -= shutdown;

                try
                {
                    WebAdminHost.Stop();
                }
                catch (Exception ex)
                {
                    Logger.GetLogger().Error(ex);
                }

                Settings.Save();
            }
            catch(Exception ex)
            {
                Logger.GetLogger().Error(ex);
            }
        }
    }
}

using System.Diagnostics;
using Client;
using Microsoft.Web.WebView2.Core;
using Client.Patching;

namespace Launcher
{
    public partial class AMain : Form
    {
        public bool Completed, LabelSwitch, ErrorFound;

        private Stopwatch _stopwatch = Stopwatch.StartNew();

        public Thread _workThread;

        private bool dragging = false;
        private Point dragCursorPoint;
        private Point dragFormPoint;

        private Config ConfigForm = new Config();

        private bool Restart = false;

        public AMain()
        {
            InitializeComponent();

            BackColor = Color.FromArgb(1, 0, 0);
            TransparencyKey = Color.FromArgb(1, 0, 0);
        }

        public static void SaveError(string ex)
        {
            try
            {
                if (Settings.RemainingErrorLogs-- > 0)
                {
                    File.AppendAllText(@".\Error.txt",
                                       string.Format("[{0}] {1}{2}", DateTime.Now, ex, Environment.NewLine));
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// The launcher's patch step (in-game patcher plan §4.2; the old PList.gz patcher was retired 2026-10-20): the relay's
        /// /api/patch ([Launcher] Host = WebRelay --patch-root), then program files + tiers 0-1
        /// through <see cref="PatchService"/> (streamed, hashed, resumable). Relay unreachable or another client patching
        /// this folder: launch with what is on disk. Everything else downloads in the game.
        /// </summary>
        private void StartOnDemand()
        {
            try
            {
                _stopwatch = Stopwatch.StartNew();
                if (PatchService.Start() && PatchService.Holder)
                {
                    if (PatchService.UpdateReady)
                    {
                        // A complete, still-current program set was staged before (last session, or a crash before applying):
                        // apply it first (Program.Main, on the way out) and download the rest with the new build.
                        Restart = true;
                    }
                    else
                    {
                        int failed = PatchService.DownloadCore(out bool programChanged);
                        if (programChanged) Restart = true;
                        if (failed > 0) ErrorFound = true;
                    }
                }
            }
            catch (Exception ex)
            {
                SaveError(ex.ToString());
            }
            Completed = true;
        }

        /// <summary>
        /// The Config form's "check files" button (it used to re-run the PList.gz check and clean-up): the launch check again,
        /// i.e. program files + tiers 0-1 that are not current are downloaded (or staged) once more.
        /// </summary>
        public void CheckFilesAgain()
        {
            if (!PatchService.Active || !PatchService.Holder || (_workThread != null && _workThread.IsAlive)) return;
            Completed = false;
            Launch_pb.Enabled = false;
            InterfaceTimer.Enabled = true;
            _workThread = new Thread(() =>
            {
                try
                {
                    int failed = PatchService.DownloadCore(out bool programChanged);
                    if (programChanged) Restart = true;
                    if (failed > 0) ErrorFound = true;
                }
                catch (Exception ex)
                {
                    SaveError(ex.ToString());
                }
                Completed = true;
            }) { IsBackground = true };
            _workThread.Start();
        }

        /// <summary>The launcher bars, from <see cref="PatchService.Progress"/>.</summary>
        private void ShowOnDemandProgress()
        {
            if (!PatchService.Active) return;
            var p = PatchService.Progress;
            long total = p.BytesDone + p.BytesLeft;

            ActionLabel.Visible = true;
            SpeedLabel.Visible = true;
            CurrentFile_label.Visible = true;
            CurrentPercent_label.Visible = true;
            TotalPercent_label.Visible = true;

            if (LabelSwitch) ActionLabel.Text = GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.FilesRemaining), p.FilesLeft);
            else ActionLabel.Text = GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.MBRemaining), p.BytesLeft / 1024 / 1024);
            CurrentFile_label.Text = p.Current;
            SpeedLabel.Text = ToSize(p.BytesDone / Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds));
            if (total > 0)
            {
                int percent = (int)(100 * p.BytesDone / total);
                TotalProg_pb.Width = (int)(5.5 * percent);
                ProgressCurrent_pb.Width = (int)(5.5 * percent);
                TotalPercent_label.Text = percent + "%";
                CurrentPercent_label.Text = percent + "%";
            }
        }

        private void AMain_Load(object sender, EventArgs e)
        {
            var envir = CoreWebView2Environment.CreateAsync(null, Settings.ResourcePath).Result;
            Main_browser.EnsureCoreWebView2Async(envir);

            if (Settings.P_BrowserAddress != "")
            {
                if (Uri.IsWellFormedUriString(Settings.P_BrowserAddress, UriKind.Absolute))
                {
                    Main_browser.NavigationCompleted += Main_browser_NavigationCompleted;
                    Main_browser.Source = new Uri(Settings.P_BrowserAddress);
                }
                else
                {
                    MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LauncherBrowserFormatError), GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.BadBrowserFormat));
                }
            }

            RepairOldFiles();

            Launch_pb.Enabled = false;
            ProgressCurrent_pb.Width = 5;
            TotalProg_pb.Width = 5;
            Version_label.Text = string.Format("Build: {0}.{1}.{2}", Globals.ProductCodename, Settings.UseTestConfig ? "Debug" : "Release", Application.ProductVersion);

            if (Settings.P_ServerName != String.Empty)
            {
                Name_label.Visible = true;
                Name_label.Text = Settings.P_ServerName;
            }

            _workThread = new Thread(StartOnDemand) { IsBackground = true };
            _workThread.Start();
        }

        private void Main_browser_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (Main_browser.Source.AbsolutePath != "blank") Main_browser.Visible = true;
        }

        private void Launch_pb_Click(object sender, EventArgs e)
        {
            Launch();
        }

        private void Launch()
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;

            Program.Launch = true;
            Close();
        }

        private void Close_pb_Click(object sender, EventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;
            Close();
        }

        private void Movement_panel_MouseClick(object sender, MouseEventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;
            dragging = true;
            dragCursorPoint = Cursor.Position;
            dragFormPoint = this.Location;
        }

        private void Movement_panel_MouseUp(object sender, MouseEventArgs e)
        {
            dragging = false;
        }

        private void Movement_panel_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                Point dif = Point.Subtract(Cursor.Position, new Size(dragCursorPoint));
                this.Location = Point.Add(dragFormPoint, new Size(dif));
            }
        }

        private void Launch_pb_MouseEnter(object sender, EventArgs e)
        {
            Launch_pb.Image = Client.Resources.Images.Launch_Hover;
        }

        private void Launch_pb_MouseLeave(object sender, EventArgs e)
        {
            Launch_pb.Image = Client.Resources.Images.Launch_Base1;
        }

        private void Close_pb_MouseEnter(object sender, EventArgs e)
        {
            Close_pb.Image = Client.Resources.Images.Cross_Hover;
        }

        private void Close_pb_MouseLeave(object sender, EventArgs e)
        {
            Close_pb.Image = Client.Resources.Images.Cross_Base;
        }

        private void Launch_pb_MouseDown(object sender, MouseEventArgs e)
        {
            Launch_pb.Image = Client.Resources.Images.Launch_Pressed;
        }

        private void Launch_pb_MouseUp(object sender, MouseEventArgs e)
        {
            Launch_pb.Image = Client.Resources.Images.Launch_Base1;
        }

        private void Close_pb_MouseDown(object sender, MouseEventArgs e)
        {
            Close_pb.Image = Client.Resources.Images.Cross_Pressed;
        }

        private void Close_pb_MouseUp(object sender, MouseEventArgs e)
        {
            Close_pb.Image = Client.Resources.Images.Cross_Base;
        }

        private void ProgressCurrent_pb_SizeChanged(object sender, EventArgs e)
        {
            ProgEnd_pb.Location = new Point((ProgressCurrent_pb.Location.X + ProgressCurrent_pb.Width), ProgressCurrent_pb.Location.Y);
            if (ProgressCurrent_pb.Width == 0) ProgEnd_pb.Visible = false;
            else ProgEnd_pb.Visible = true;
        }

        private void Config_pb_MouseDown(object sender, MouseEventArgs e)
        {
            Config_pb.Image = Client.Resources.Images.Config_Pressed;
        }

        private void Config_pb_MouseEnter(object sender, EventArgs e)
        {
            Config_pb.Image = Client.Resources.Images.Config_Hover;
        }

        private void Config_pb_MouseLeave(object sender, EventArgs e)
        {
            Config_pb.Image = Client.Resources.Images.Config_Base;
        }

        private void Config_pb_MouseUp(object sender, MouseEventArgs e)
        {
            Config_pb.Image = Client.Resources.Images.Config_Base;
        }

        private void Config_pb_Click(object sender, EventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Hide();
            else ConfigForm.Show(Program.PForm);
            ConfigForm.Location = new Point(Location.X + Config_pb.Location.X - 183, Location.Y + 36);
        }

        private void TotalProg_pb_SizeChanged(object sender, EventArgs e)
        {
            ProgTotalEnd_pb.Location = new Point((TotalProg_pb.Location.X + TotalProg_pb.Width), TotalProg_pb.Location.Y);
            if (TotalProg_pb.Width == 0) ProgTotalEnd_pb.Visible = false;
            else ProgTotalEnd_pb.Visible = true;
        }

        private void InterfaceTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (Completed)
                {
                    ActionLabel.Text = "";
                    CurrentFile_label.Text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UpToDate);
                    SpeedLabel.Text = "";
                    ProgressCurrent_pb.Width = 550;
                    TotalProg_pb.Width = 550;
                    CurrentFile_label.Visible = true;
                    CurrentPercent_label.Visible = true;
                    TotalPercent_label.Visible = true;
                    CurrentPercent_label.Text = "100%";
                    TotalPercent_label.Text = "100%";
                    InterfaceTimer.Enabled = false;
                    Launch_pb.Enabled = true;
                    if (ErrorFound) MessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.FilesDownloadFailed), GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DownloadFailed));
                    ErrorFound = false;

                    if (Restart)
                    {
                        Program.Restart = true;

                        Close();
                        // Restart into the new build first (AutoStart would launch the game on the old DLLs).
                        return;
                    }

                    if (Settings.P_AutoStart)
                    {
                        Launch();
                    }
                    return;
                }

                ShowOnDemandProgress();
            }
            catch
            {
                //to-do 
            }

        }

        private void AMain_Click(object sender, EventArgs e)
        {
            if (ConfigForm.Visible) ConfigForm.Visible = false;
        }

        private void ActionLabel_Click(object sender, EventArgs e)
        {
            LabelSwitch = !LabelSwitch;
        }

        private void Credit_label_Click(object sender, EventArgs e)
        {
            if (Credit_label.Text == "Powered by Crystal M2") Credit_label.Text = "Designed by Breezer";
            else Credit_label.Text = "Powered by Crystal M2";
        }

        private void AMain_FormClosed(object sender, FormClosedEventArgs e)
        {
            MoveOldFilesToCurrent();

            Launch_pb?.Dispose();
            Close_pb?.Dispose();
        }

        private static string[] suffixes = new[] { " B", " KB", " MB", " GB", " TB", " PB" };

        private string ToSize(double number, int precision = 2)
        {
            // unit's number of bytes
            const double unit = 1024;
            // suffix counter
            int i = 0;
            // as long as we're bigger than a unit, keep going
            while (number > unit)
            {
                number /= unit;
                i++;
            }
            // apply precision and current suffix
            return Math.Round(number, precision) + suffixes[i];
        }

        /// <summary>
        /// Left-over Old__ files: put back when the original is missing, else deleted; a locked file is left for the next start.
        /// Skipped while a program update is being proven: its Old__ files are the rollback copy (PatchApply).
        /// </summary>
        private void RepairOldFiles()
        {
            if (PatchApply.Applying) return;
            try
            {
                foreach (var oldFilename in Directory.GetFiles(Settings.P_Client, "Old__*", SearchOption.AllDirectories))
                {
                    try
                    {
                        string original = Path.Combine(Path.GetDirectoryName(oldFilename), Path.GetFileName(oldFilename).Substring("Old__".Length));
                        if (!File.Exists(original)) File.Move(oldFilename, original);
                        else File.Delete(oldFilename);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SaveError("[Patch] Old__ clean-up: " + ex.Message);
            }
        }

        private void MoveOldFilesToCurrent()
        {
            var files = Directory.GetFiles(Settings.P_Client, "*", SearchOption.AllDirectories).Where(x => Path.GetFileName(x).StartsWith("Old__"));

            foreach (var oldFilename in files)
            {
                string originalFilename = Path.Combine(Path.GetDirectoryName(oldFilename), (Path.GetFileName(oldFilename).Replace("Old__", "")));

                if (!File.Exists(originalFilename) && File.Exists(oldFilename))
                    File.Move(oldFilename, originalFilename);
            }
        }
    }
}

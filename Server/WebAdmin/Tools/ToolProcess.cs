using System.Diagnostics;

namespace Server.WebAdmin.Tools
{
    /// <summary>
    /// One child process started by the Tools page (stress bots, a build, a scratch-server script). Output lines go into a
    /// bounded ring the status route reads; <see cref="LineFilter"/> may consume a line instead (the bots' "@status" JSON).
    /// Events arrive on thread-pool threads; everything here is guarded by one lock. Never touches game state.
    /// </summary>
    internal sealed class ToolProcess
    {
        private readonly object _lock = new object();
        private readonly LinkedList<string> _lines = new LinkedList<string>();
        private readonly int _maxLines;
        private readonly Process _process;
        private readonly Func<string, bool> _lineFilter;
        private bool _exitHandled;

        public string Kind { get; }
        public DateTime StartedUtc { get; }
        public DateTime? ExitedUtc { get; private set; }
        public int? ExitCode { get; private set; }
        public int Pid { get; }

        /// <summary>Raised once after the process exited and its output was drained (thread-pool thread).</summary>
        public event Action<ToolProcess> Exited;

        private ToolProcess(string kind, Process process, Func<string, bool> lineFilter, int maxLines)
        {
            Kind = kind;
            _process = process;
            _lineFilter = lineFilter;
            _maxLines = maxLines;
            StartedUtc = DateTime.UtcNow;
            Pid = process.Id;
        }

        public bool Running => ExitedUtc == null;

        /// <summary>Starts <paramref name="psi"/> with redirected output (and stdin when asked), no window.</summary>
        public static ToolProcess Start(string kind, ProcessStartInfo psi, Func<string, bool> lineFilter = null, int maxLines = 400)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
            psi.StandardErrorEncoding = System.Text.Encoding.UTF8;

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("Process did not start: " + psi.FileName);

            var tool = new ToolProcess(kind, process, lineFilter, maxLines);
            process.OutputDataReceived += (_, e) => { if (e.Data != null) tool.Add(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) tool.Add("! " + e.Data); };
            process.Exited += (_, _) => tool.OnExited();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.HasExited) tool.OnExited();
            return tool;
        }

        private void Add(string line)
        {
            if (_lineFilter != null)
            {
                try { if (_lineFilter(line)) return; }
                catch (Exception ex) { WebAdminLog.Error(ex, "Tools: output filter (" + Kind + ")"); }
            }

            lock (_lock)
            {
                _lines.AddLast(line.Length > 600 ? line.Substring(0, 600) + "…" : line);
                while (_lines.Count > _maxLines) _lines.RemoveFirst();
            }
        }

        /// <summary>Adds a line of our own (e.g. "stop requested by admin") to the log ring.</summary>
        public void Note(string line) => Add("# " + line);

        private void OnExited()
        {
            lock (_lock)
            {
                if (_exitHandled) return;
                _exitHandled = true;
            }

            int? code = null;
            try
            {
                _process.WaitForExit(); // drains the async output readers
                code = _process.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }

            lock (_lock)
            {
                ExitCode = code;
                ExitedUtc = DateTime.UtcNow;
            }

            try { Exited?.Invoke(this); }
            catch (Exception ex) { WebAdminLog.Error(ex, "Tools: exit handler (" + Kind + ")"); }
            _process.Dispose();
        }

        public string[] Tail(int count)
        {
            lock (_lock)
            {
                var skip = Math.Max(0, _lines.Count - count);
                return _lines.Skip(skip).ToArray();
            }
        }

        /// <summary>Writes one line to stdin (only for processes started with RedirectStandardInput). False when gone.</summary>
        public bool WriteLine(string line)
        {
            try
            {
                if (!Running || !_process.StartInfo.RedirectStandardInput) return false;
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
        }

        public void CloseInput()
        {
            try { if (Running && _process.StartInfo.RedirectStandardInput) _process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
        }

        public void Kill()
        {
            try { if (Running) _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }
}

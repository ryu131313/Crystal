namespace Server.WebAdmin
{
    /// <summary>
    /// Internal diagnostics sink for the web admin. Defaults to the server's MessageQueue; tests may replace <see cref="Sink"/>
    /// so nothing touches log4net/Settings.
    /// </summary>
    internal static class WebAdminLog
    {
        internal static Action<string> Sink { get; set; } = DefaultSink;

        internal static Action<Exception> ErrorSink { get; set; } = DefaultErrorSink;

        public static void Info(string message)
        {
            try
            {
                Sink?.Invoke("[WebAdmin] " + message);
            }
            catch
            {
                // logging must never throw into callers
            }
        }

        public static void Error(Exception ex, string context = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(context))
                    Sink?.Invoke("[WebAdmin] " + context);

                ErrorSink?.Invoke(ex);
            }
            catch
            {
                // logging must never throw into callers
            }
        }

        private static void DefaultSink(string message)
        {
            MessageQueue.Instance.Enqueue(message);
        }

        private static void DefaultErrorSink(Exception ex)
        {
            MessageQueue.Instance.Enqueue(ex);
        }
    }
}

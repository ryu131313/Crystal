namespace Server.WebAdmin
{
    /// <summary>
    /// Throw from any route handler to answer with <c>{"ok":false,"message":...,"data":...}</c> and the given status code.
    /// The message is sent to the client verbatim, so never put exception text or secrets in it.
    /// </summary>
    public class WebAdminException : Exception
    {
        public int Status { get; }

        /// <summary>Extra payload sent as <c>data</c> in the error body (named Payload to avoid clashing with Exception.Data).</summary>
        public object Payload { get; }

        public WebAdminException(int status, string message, object data = null)
            : base(message)
        {
            Status = status;
            Payload = data;
        }
    }

    /// <summary>503: the world (Envir.Main game loop) is not running, so game-thread work cannot be done.</summary>
    public class WorldNotRunningException : WebAdminException
    {
        public WorldNotRunningException()
            : this("stopped")
        {
        }

        public WorldNotRunningException(string worldState)
            : base(503, "World is not running", new { world = string.IsNullOrEmpty(worldState) ? "stopped" : worldState })
        {
        }
    }

    /// <summary>504: the game thread did not run (or finish) the queued work in time.</summary>
    public class GameThreadTimeoutException : WebAdminException
    {
        /// <summary>True when the work had already started on the game thread (it may still complete; do not retry blindly).</summary>
        public bool Started { get; }

        public GameThreadTimeoutException(bool started)
            : base(504, started
                ? "The game thread is still completing this action. Do not retry; refresh to check the result."
                : "The game thread did not respond in time. The action was cancelled before it ran; it is safe to retry.",
                new { started })
        {
            Started = started;
        }
    }
}

using Server.MirDatabase;
using Server.MirObjects;

namespace Server.WebAdmin
{
    /// <summary>
    /// Static hooks invoked by Envir / PlayerObject (foundation) and subscribed to by web admin features.
    /// Every invocation is exception-safe: a throwing subscriber is logged and never breaks the game loop.
    /// Subscribers run on the calling (usually game) thread and must be cheap: enqueue, never block.
    /// </summary>
    public static class WebAdminHooks
    {
        /// <summary>Game-client login outcome: (accountId, ip, result). result: "ok", "bad-password", "banned", "no-account", "refused:&lt;reason&gt;".</summary>
        public static event Action<string, string, string> LoginAttempt;

        /// <summary>Optional game login gate (e.g. maintenance). Non-null/non-empty return = refusal reason; the client gets the standard "login disabled" result.</summary>
        public static Func<AccountInfo, string> LoginGate;

        /// <summary>A player finished StartGame successfully.</summary>
        public static event Action<PlayerObject> PlayerEntered;

        /// <summary>A player left the game (StopGame).</summary>
        public static event Action<PlayerObject> PlayerLeft;

        /// <summary>
        /// A player died (end of PlayerObject.Die): (victim, last hitter or null). May be raised on a mob thread (a monster killing a
        /// player during the mob pass): subscribers must be thread-safe and lock-free (enqueue only).
        /// </summary>
        public static event Action<PlayerObject, MapObject> PlayerDied;

        /// <summary>(channel, sender, message, target or null). Channels: normal, shout, shout-map, shout-server, whisper, group, guild, mentor, relationship, announcement.</summary>
        public static event Action<string, string, string, string> Chat;

        /// <summary>After LoadAccounts (StartNetwork).</summary>
        public static event Action AccountsLoaded;

        /// <summary>After accounts were serialized for saving (BeginSaveAccounts / SaveAccounts).</summary>
        public static event Action AccountsSaved;

        /// <summary>WorkLoop started successfully (after StartNetwork); the admin action queue is open.</summary>
        public static event Action WorldStarted;

        /// <summary>WorkLoop exited.</summary>
        public static event Action WorldStopped;

        internal static void RaiseLoginAttempt(string accountId, string ip, string result)
        {
            var handler = LoginAttempt;
            if (handler == null) return;

            foreach (var d in handler.GetInvocationList())
            {
                try
                {
                    ((Action<string, string, string>)d)(accountId, ip, result);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "LoginAttempt subscriber failed");
                }
            }
        }

        /// <summary>Returns a refusal reason when <see cref="LoginGate"/> refuses this account, otherwise null. Never throws.</summary>
        internal static string CheckLoginGate(AccountInfo account)
        {
            var gate = LoginGate;
            if (gate == null || account == null) return null;

            try
            {
                var result = gate(account);
                return string.IsNullOrWhiteSpace(result) ? null : result;
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "LoginGate failed; login allowed");
                return null;
            }
        }

        internal static void RaisePlayerEntered(PlayerObject player)
        {
            RaisePlayer(PlayerEntered, player, "PlayerEntered");
        }

        internal static void RaisePlayerLeft(PlayerObject player)
        {
            RaisePlayer(PlayerLeft, player, "PlayerLeft");
        }

        internal static void RaisePlayerDied(PlayerObject victim, MapObject killer)
        {
            var handler = PlayerDied;
            if (handler == null) return;

            foreach (var d in handler.GetInvocationList())
            {
                try
                {
                    ((Action<PlayerObject, MapObject>)d)(victim, killer);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "PlayerDied subscriber failed");
                }
            }
        }

        internal static void RaiseChat(string channel, string sender, string message, string target)
        {
            var handler = Chat;
            if (handler == null) return;

            foreach (var d in handler.GetInvocationList())
            {
                try
                {
                    ((Action<string, string, string, string>)d)(channel, sender, message, target);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Chat subscriber failed");
                }
            }
        }

        internal static void RaiseAccountsLoaded()
        {
            RaiseSimple(AccountsLoaded, "AccountsLoaded");
        }

        internal static void RaiseAccountsSaved()
        {
            RaiseSimple(AccountsSaved, "AccountsSaved");
        }

        internal static void RaiseWorldStarted()
        {
            RaiseSimple(WorldStarted, "WorldStarted");
        }

        internal static void RaiseWorldStopped()
        {
            RaiseSimple(WorldStopped, "WorldStopped");
        }

        private static void RaisePlayer(Action<PlayerObject> handler, PlayerObject player, string name)
        {
            if (handler == null) return;

            foreach (var d in handler.GetInvocationList())
            {
                try
                {
                    ((Action<PlayerObject>)d)(player);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, name + " subscriber failed");
                }
            }
        }

        private static void RaiseSimple(Action handler, string name)
        {
            if (handler == null) return;

            foreach (var d in handler.GetInvocationList())
            {
                try
                {
                    ((Action)d)();
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, name + " subscriber failed");
                }
            }
        }
    }
}

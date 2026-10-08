using System.Text.Json;

namespace Server.WebAdmin.Routes
{
    /// <summary>GET /api/admin/maintenance and POST /api/admin/maintenance {action: start|lift, message, kickNonAdmins, minutes}.</summary>
    public sealed class MaintenanceRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/maintenance", GetState);
            router.Post("/api/admin/maintenance", PostAction);
        }

        private static object Dto(MaintenanceSnapshot s) => new
        {
            active = s.Active,
            since = s.Since,
            by = s.By,
            message = s.Message,
            kickNonAdmins = s.KickNonAdmins,
            kickScheduledUtc = s.KickScheduledUtc,
            secondsUntilKick = s.SecondsUntilKick,
            kickedUtc = s.KickedUtc,
            kickedCount = s.KickedCount,
            liftedUtc = s.LiftedUtc,
            liftedBy = s.LiftedBy,
            refusedLogins = s.RefusedLogins,
            serverTimeUtc = s.ServerTimeUtc,
            world = WebAdminGame.WorldState,
            defaultMessage = MaintenanceController.DefaultMessage,
            maxMinutes = MaintenanceController.MaxMinutes,
        };

        private static Task GetState(WebAdminRequest req)
        {
            return req.Json(Dto(WebAdminMaintenance.Snapshot()));
        }

        private static async Task PostAction(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "Expected a JSON object body");

            var action = (body.GetString("action") ?? string.Empty).Trim().ToLowerInvariant();
            var announce = body.GetBool("announce", true);

            switch (action)
            {
                case "start":
                {
                    var message = body.GetString("message");
                    if (message != null && message.Length > 300) throw new WebAdminException(400, "message is too long (max 300 characters)");

                    var kick = body.GetBool("kickNonAdmins", body.GetBool("kick", false));
                    // A present but non-whole minutes value (2.5, "abc") must not silently become 0 = "disconnect now".
                    var minutes = 0;
                    if (body.TryGetPropertyIgnoreCase("minutes", out var minutesEl) && minutesEl.ValueKind != JsonValueKind.Null)
                    {
                        minutes = body.GetInt("minutes", int.MinValue);
                        if (minutes == int.MinValue) throw new WebAdminException(400, "minutes must be a whole number");
                    }
                    if (minutes < 0 || minutes > MaintenanceController.MaxMinutes)
                        throw new WebAdminException(400, "minutes must be between 0 and " + MaintenanceController.MaxMinutes);

                    MaintenanceSnapshot state;
                    try
                    {
                        state = WebAdminMaintenance.Start(req.AdminName, message, kick, kick ? minutes : 0, announce);
                    }
                    catch (InvalidOperationException)
                    {
                        throw new WebAdminException(409, "Maintenance is already active. Lift it first to change it.", Dto(WebAdminMaintenance.Snapshot()));
                    }

                    WebAdminAudit.Record(req, "maintenance.start", "logins", new
                    {
                        message = state.Message,
                        kickNonAdmins = kick,
                        minutes = kick ? minutes : 0,
                        announce,
                        world = WebAdminGame.WorldState,
                    });

                    string text;
                    if (!kick) text = "Maintenance started: non-admin logins are refused. Players already online stay connected.";
                    else if (minutes > 0) text = "Maintenance started: logins closed; non-admin players are disconnected in " + minutes + " minute" + (minutes == 1 ? string.Empty : "s") + ".";
                    else text = "Maintenance started: logins closed and non-admin players disconnected.";

                    if (!WebAdminGame.IsRunning) text += " (The world is not running; the gate applies when it starts.)";

                    await req.Ok(Dto(state), text).ConfigureAwait(false);
                    return;
                }

                case "lift":
                {
                    var before = WebAdminMaintenance.Snapshot();
                    var state = WebAdminMaintenance.Lift(req.AdminName, announce);

                    if (before.Active)
                        WebAdminAudit.Record(req, "maintenance.lift", "logins", new { since = before.Since, startedBy = before.By, refusedLogins = before.RefusedLogins, announce });

                    await req.Ok(Dto(state), before.Active ? "Maintenance lifted: logins are open." : "Maintenance was not active.").ConfigureAwait(false);
                    return;
                }

                default:
                    throw new WebAdminException(400, "action must be start or lift");
            }
        }
    }
}

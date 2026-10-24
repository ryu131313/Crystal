using System.Text.Json;
using Server.MirEnvir.Quests;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Quest system V2, phase 1 (UserData-and-Quests-Plan.md, Part B): the "Re-import legacy quests" action and the last validation
    /// report. The import rebuilds the V2 definition tables from the legacy quests on Envir.Edit and saves SystemData.db (ids kept stable);
    /// the running world is unchanged (legacy quests stay the runtime; the V2 tables are RestartOnly). The Quests editor arrives in phase 3.
    /// <para>Threads: HTTP only; the import takes EditLock itself (<see cref="QuestImportRunner"/>).</para>
    /// </summary>
    public sealed class QuestDefinitionRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Post("/api/admin/quests/reimport", PostReimport);
            router.Get("/api/admin/quests/validation", GetValidation);
        }

        private static async Task PostReimport(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var dryRun = body.ValueKind == JsonValueKind.Object && body.GetBool("dryRun");
            var reason = body.ValueKind == JsonValueKind.Object ? body.GetString("reason") : null;

            var outcome = await Task.Run(() => QuestImportRunner.Run("web admin re-import", onlyWhenEmpty: false, dryRun)).ConfigureAwait(false);
            if (outcome.Report == null)
                throw new WebAdminException(409, "The quest import did not run: " + outcome.Message + ".");

            var r = outcome.Report;
            var data = new
            {
                dryRun,
                quests = r.Quests,
                unparsable = r.Unparsed.Count,
                noGiver = r.NoGiver.Count,
                multiGiver = r.MultiGiver.Count,
                cutShort = r.CutShort.Count,
                errors = r.Errors,
                warnings = r.Warnings,
                report = r.ToMarkdown(),
            };

            if (!dryRun)
            {
                WebAdminAudit.Record(req, "quests.reimport", "SystemData.db", new { r.Quests, r.Errors, r.Warnings }, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
                WebAdminPending.Add("gamedata", "Quest V2 definitions re-imported from the legacy quests (loaded at the next world start; not used at runtime yet)", req.AdminName);
            }
            await req.Ok(data, dryRun ? "Dry run: " + r.Summary() : r.Summary() + " Saved; the running world still uses the legacy quests.").ConfigureAwait(false);
        }

        private static Task GetValidation(WebAdminRequest req)
        {
            var problems = QuestDefinitions.LastProblems;
            return req.Ok(new
            {
                errors = problems.Count(p => !p.Warning),
                warnings = problems.Count(p => p.Warning),
                problems = problems.Take(500).Select(p => new { quest = p.Quest, warning = p.Warning, message = p.Message }),
            });
        }
    }
}

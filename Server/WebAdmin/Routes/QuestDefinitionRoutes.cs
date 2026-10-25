using Server.MirEnvir.Quests;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Quest system V2 (UserData-and-Quests-Plan.md, Part B): the last validation report. The legacy "re-import legacy quests" action
    /// (<c>POST /api/admin/quests/reimport</c>) was removed with the importer in wave 4; quests are authored in the Quests page.
    /// <para>Threads: HTTP only; reads a copy of the last report.</para>
    /// </summary>
    public sealed class QuestDefinitionRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/quests/validation", GetValidation);
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

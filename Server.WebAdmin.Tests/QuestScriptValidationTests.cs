namespace Server.WebAdmin.Tests
{
    /// <summary>Quest V2 NPC script keywords in ScriptValidation (lane F): placement (#IF vs #ACT), argument count, numeric quest.</summary>
    public class QuestScriptValidationTests
    {
        private static List<ScriptDiagnostic> Npc(params string[] lines) =>
            ScriptValidation.Validate("npc", string.Join("\n", lines), new ScriptValidationContext());

        [Fact]
        public void Well_formed_quest_keywords_give_no_quest_warning()
        {
            var diags = Npc("[@MAIN]", "#IF", "CHECKQUESTSTEP 5 12", "CHECKQUESTVAR 5 kills >= 3", "CHECKQUESTOUTCOME 5 2", "CHECKCONDITION 40",
                "#ACT", "QUESTCOUNTER 5 77", "SETQUESTVAR 5 kills 1", "QUESTSTEP 5 13", "OFFERQUEST 6", "#SAY", "Hi");
            Assert.DoesNotContain(diags, d => d.Message.Contains("QUEST") || d.Message.Contains("CONDITION") || d.Message.Contains("arguments"));
        }

        [Fact]
        public void Misplaced_short_or_non_numeric_quest_keywords_are_reported()
        {
            var diags = Npc("[@MAIN]", "#IF", "QUESTSTEP 5 13", "CHECKQUESTVAR 5 kills", "#ACT", "CHECKCONDITION 4", "OFFERQUEST wolves", "#SAY", "Hi");
            Assert.Contains(diags, d => d.Line == 3 && d.Message.Contains("is an action"));
            Assert.Contains(diags, d => d.Line == 4 && d.Message.Contains("Too few arguments"));
            Assert.Contains(diags, d => d.Line == 6 && d.Message.Contains("is a check"));
            Assert.Contains(diags, d => d.Line == 7 && d.Message.Contains("number"));
        }
    }
}

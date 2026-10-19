using Client.MirControls;
using Client.MirScenes;
using S = ServerPackets;

namespace Client.RenderTest
{
    /// <summary>
    /// Classic-only prompt steps: S.FriendRequest (phase 2.7) shows a Yes / No box that answers through Models.Friends.Reply
    /// (accept, a repeat replacing the open box, decline with No, and a request settled by S.FriendUpdate closing its box
    /// without an answer). Each step checks the boxes, the pending requests and the C.FriendRequestReply it sent.
    /// </summary>
    internal static partial class UiTestRunner
    {
        static partial void AddClassicPromptSteps(List<Step> steps)
        {
            ClassicPromptStep(steps, "cfriend_close_all", () => { CloseAllPrompts(); PressBind(KeybindOptions.Closeall); });
            ClassicPromptStep(steps, "cfriend_request_in", () =>
            {
                Feed(new S.FriendRequest { Name = "Stranger", Class = MirClass.Taoist, Level = 27 });
                Expect(MessageBoxCount() == 1, "one friend request box");
            });
            ClassicPromptStep(steps, "cfriend_request_repeat", () =>
            {
                Feed(new S.FriendRequest { Name = "Stranger", Class = MirClass.Taoist, Level = 28 });
                Expect(MessageBoxCount() == 1, "the repeat to replace the open box");
                Expect(_sent.Count == 0, "no answer for the replaced box");
            });
            ClassicPromptStep(steps, "cfriend_accept", () =>
            {
                TopMessageBox()?.YesButton.InvokeMouseClick(null);
                Expect(MessageBoxCount() == 0, "the box to close");
                Expect(_sent.Count == 1 && _sent[0].Contains("Name=\"Stranger\"") && _sent[0].Contains("Accept=True"), "C.FriendRequestReply accept");
                Expect(GameScene.Models.Friends.Requests.Count == 0, "no pending request");
            });
            ClassicPromptStep(steps, "cfriend_decline", () =>
            {
                Feed(new S.FriendRequest { Name = "Rival", Class = MirClass.Warrior, Level = 40 });
                TopMessageBox()?.NoButton.InvokeMouseClick(null);
                Expect(MessageBoxCount() == 0, "the box to close");
                Expect(_sent.Count == 1 && _sent[0].Contains("Name=\"Rival\"") && _sent[0].Contains("Accept=False"), "C.FriendRequestReply decline");
            });
            ClassicPromptStep(steps, "cfriend_settled", () =>
            {
                Feed(new S.FriendRequest { Name = "UiAlly", Class = MirClass.Wizard, Level = 31 });
                Feed(new S.FriendUpdate { Friends = new List<ClientFriend> { new ClientFriend { Index = 2, Name = "UiAlly", Online = true } } });
                Expect(MessageBoxCount() == 0, "the settled request's box to close");
                Expect(_sent.Count == 0, "no answer for a settled request");
            });
        }

        private static void ClassicPromptStep(List<Step> steps, string name, Action act, int delayMs = 600) =>
            steps.Add(new Step { Name = name, Act = act, DelayMs = delayMs, ClassicOnly = true });

        private static void Expect(bool ok, string what)
        {
            if (!ok) throw new InvalidOperationException("expected " + what);
        }

        private static MirMessageBox TopMessageBox()
        {
            for (int i = Scene.Controls.Count - 1; i >= 0; i--)
                if (Scene.Controls[i] is MirMessageBox box && !box.IsDisposed) return box;
            return null;
        }

        private static int MessageBoxCount()
        {
            int n = 0;
            for (int i = 0; i < Scene.Controls.Count; i++)
                if (Scene.Controls[i] is MirMessageBox box && !box.IsDisposed) n++;
            return n;
        }
    }
}

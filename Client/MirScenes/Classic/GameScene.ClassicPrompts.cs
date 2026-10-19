using Client.MirControls;
using Client.MirNetwork;
using Client.MirScenes.Models;
using S = ServerPackets;
using C = ClientPackets;

namespace Client.MirScenes
{
    /// <summary>
    /// The classic prompts moved out of GameScene.cs unchanged (phase 4, IGameUi.Social.cs): GameScene keeps the checks before
    /// each prompt; the MirMessageBox / MirInputBox code is here in its original form.
    /// </summary>
    public sealed partial class GameScene
    {
        internal void ClassicConfirmQuit()
        {
            MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ExitTip), MirMessageBoxButtons.YesNo);
            messageBox.YesButton.Click += (o, e) => Program.Form.Close();
            messageBox.Show();
        }

        internal void ClassicConfirmLogOut()
        {
            MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LogOutTip), MirMessageBoxButtons.YesNo);
            messageBox.YesButton.Click += (o, e) =>
            {
                Network.Enqueue(new C.LogOut());
                Enabled = false;
            };
            messageBox.Show();
        }

        internal void ClassicQuestShareRequest(S.ShareQuest p, ClientQuestInfo quest)
        {
            MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerShareQuestRequest), p.SharerName), MirMessageBoxButtons.YesNo);

            messageBox.YesButton.Click += (o, e) => Network.Enqueue(new C.AcceptQuest { NPCIndex = 0, QuestIndex = quest.Index });

            messageBox.Show();
        }

        internal void ClassicGuildInviteRequest(S.GuildInvite p)
        {
            MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.JoinGuildPrompt), p.Name), MirMessageBoxButtons.YesNo);

            messageBox.YesButton.Click += (o, e) => Network.Enqueue(new C.GuildInvite { AcceptInvite = true });
            messageBox.NoButton.Click += (o, e) => Network.Enqueue(new C.GuildInvite { AcceptInvite = false });

            messageBox.Show();
        }

        internal void ClassicGuildNameRequest()
        {
            MirInputBox inputBox = new MirInputBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.EnterGuildNameLengthLimit));
            inputBox.InputTextBox.TextBox.KeyPress += (o, e) =>
            {
                string Allowed = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                if (!Allowed.Contains(e.KeyChar) && e.KeyChar != (char)Keys.Back)
                    e.Handled = true;
            };
            inputBox.OKButton.Click += (o, e) =>
            {
                if (inputBox.InputTextBox.Text.Contains('\\'))
                {
                    Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.CannotUseBackslashInGuildName), ChatType.System);
                    inputBox.InputTextBox.Text = "";
                }
                Network.Enqueue(new C.GuildNameReturn { Name = inputBox.InputTextBox.Text });
                inputBox.Dispose();
            };
            inputBox.Show();
        }

        internal void ClassicGuildWarRequest()
        {
            MirInputBox inputBox = new MirInputBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.EnterGuildToWarWith));

            inputBox.OKButton.Click += (o, e) =>
            {
                Network.Enqueue(new C.GuildWarReturn { Name = inputBox.InputTextBox.Text });
                inputBox.Dispose();
            };
            inputBox.Show();
        }

        internal void ClassicMarriageRequest(S.MarriageRequest p)
        {
            MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerAskedForMarriage), p.Name), MirMessageBoxButtons.YesNo);

            messageBox.YesButton.Click += (o, e) => Network.Enqueue(new C.MarriageReply { AcceptInvite = true });
            messageBox.NoButton.Click += (o, e) => { Network.Enqueue(new C.MarriageReply { AcceptInvite = false }); messageBox.Dispose(); };

            messageBox.Show();
        }

        internal void ClassicDivorceRequest(S.DivorceRequest p)
        {
            MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerRequestedDivorce), p.Name), MirMessageBoxButtons.YesNo);

            messageBox.YesButton.Click += (o, e) => Network.Enqueue(new C.DivorceReply { AcceptInvite = true });
            messageBox.NoButton.Click += (o, e) => { Network.Enqueue(new C.DivorceReply { AcceptInvite = false }); messageBox.Dispose(); };

            messageBox.Show();
        }

        internal void ClassicMentorRequest(S.MentorRequest p)
        {
            MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerRequestedTeachClass), p.Name, p.Level, GameScene.User.Class.ToString()),
                MirMessageBoxButtons.YesNo);

            messageBox.YesButton.Click += (o, e) => Network.Enqueue(new C.MentorReply { AcceptInvite = true });
            messageBox.NoButton.Click += (o, e) => { Network.Enqueue(new C.MentorReply { AcceptInvite = false }); messageBox.Dispose(); };

            messageBox.Show();
        }

        internal void ClassicTradeRequest(S.TradeRequest p)
        {
            MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.PlayerRequestedTrade), p.Name), MirMessageBoxButtons.YesNo);

            messageBox.YesButton.Click += (o, e) => Network.Enqueue(new C.TradeReply { AcceptInvite = true });
            messageBox.NoButton.Click += (o, e) => { Network.Enqueue(new C.TradeReply { AcceptInvite = false }); messageBox.Dispose(); };

            messageBox.Show();
        }

        internal void ClassicReincarnationRequest()
        {
            MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.WouldYouLikeToBeRevived), MirMessageBoxButtons.YesNo);

            messageBox.YesButton.Click += (o, e) => Network.Enqueue(new C.AcceptReincarnation());

            messageBox.Show();
        }

        /// <summary>The open friend request boxes by requester name (ClassicFriendRequest).</summary>
        private readonly Dictionary<string, MirMessageBox> _classicFriendBoxes = new Dictionary<string, MirMessageBox>(StringComparer.OrdinalIgnoreCase);

        /// <summary>S.FriendRequest (Models.Friends.RequestReceived; classic had no prompt before phase 2.7): Yes / No answers through
        /// Models.Friends.Reply. A repeat from the same player replaces the open box; the server judges expiry.</summary>
        internal void ClassicFriendRequest(FriendRequestInfo r)
        {
            if (r == null || MirScene.ActiveScene != this) return;
            string name = r.Name;
            ClassicCloseFriendBox(name);

            MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PlayerRequestedFriend, name, r.Level), MirMessageBoxButtons.YesNo);
            _classicFriendBoxes[name] = messageBox;

            messageBox.YesButton.Click += (o, e) => ClassicAnswerFriend(name, messageBox, true);
            messageBox.NoButton.Click += (o, e) => ClassicAnswerFriend(name, messageBox, false);

            messageBox.Show();
        }

        /// <summary>Models.Friends.RequestsChanged: a request settled by a friend list update closes its box without an answer.</summary>
        internal void ClassicFriendRequestsChanged()
        {
            if (_classicFriendBoxes.Count == 0) return;
            List<FriendRequestInfo> live = Models?.Friends.Requests;
            foreach (string name in new List<string>(_classicFriendBoxes.Keys))
            {
                bool still = false;
                if (live != null)
                    for (int i = 0; i < live.Count; i++)
                        if (string.Equals(live[i].Name, name, StringComparison.OrdinalIgnoreCase)) { still = true; break; }
                if (!still) ClassicCloseFriendBox(name);
            }
        }

        private void ClassicAnswerFriend(string name, MirMessageBox box, bool accept)
        {
            // the box disposed itself (its first Click handler); answer only if it is still the open box for this name
            if (!_classicFriendBoxes.TryGetValue(name, out MirMessageBox current) || current != box) return;
            _classicFriendBoxes.Remove(name);
            Models?.Friends.Reply(name, accept);
        }

        private void ClassicCloseFriendBox(string name)
        {
            if (!_classicFriendBoxes.TryGetValue(name, out MirMessageBox box)) return;
            _classicFriendBoxes.Remove(name);
            if (!box.IsDisposed) box.Dispose();
        }
    }
}

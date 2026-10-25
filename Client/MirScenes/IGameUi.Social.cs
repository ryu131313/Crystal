using S = ServerPackets;

namespace Client.MirScenes
{
    // Prompt seam members (Desktop-Modern-UI-Plan phase 4): the Yes / No and input prompts GameScene used to raise itself as
    // MirMessageBox / MirInputBox. ClassicUi keeps that code verbatim (Classic\GameScene.ClassicPrompts.cs); ModernUi answers with
    // request toasts and popups. Called on the UI thread only.
    public partial interface IGameUi
    {
        /// <summary>S.QuestOffer (a share, an item / area / auto start): ask whether to accept it (Yes sends C.QuestAccept { NpcObjectID = 0 }).</summary>
        void QuestOffer(S.QuestOffer p);
        /// <summary>S.QuestRefused (after Models.Quests.Refused fired): show why a quest request was refused (QuestText.Refusal).</summary>
        void QuestRefused(S.QuestRefused p);
        /// <summary>S.GuildInvite: ask whether to join p.Name's guild (C.GuildInvite accept / decline).</summary>
        void GuildInviteRequest(S.GuildInvite p);
        /// <summary>S.GuildNameRequest: ask for the new guild's name (letters and digits; C.GuildNameReturn).</summary>
        void GuildNameRequest();
        /// <summary>S.GuildRequestWar: ask for the guild to go to war with (C.GuildWarReturn).</summary>
        void GuildWarRequest();
        /// <summary>S.MarriageRequest: C.MarriageReply accept / decline.</summary>
        void MarriageRequest(S.MarriageRequest p);
        /// <summary>S.DivorceRequest: C.DivorceReply accept / decline.</summary>
        void DivorceRequest(S.DivorceRequest p);
        /// <summary>S.MentorRequest: C.MentorReply accept / decline.</summary>
        void MentorRequest(S.MentorRequest p);
        /// <summary>S.TradeRequest: C.TradeReply accept / decline.</summary>
        void TradeRequest(S.TradeRequest p);
        /// <summary>S.RequestReincarnation while the user is dead past DeadTime: Yes sends C.AcceptReincarnation.</summary>
        void ReincarnationRequest();
        /// <summary>GameScene.QuitGame outside combat: Yes closes the game (Program.Form.Close).</summary>
        void ConfirmQuit();
        /// <summary>GameScene.LogOut outside combat: Yes sends C.LogOut (GameScene.LogOut(false)).</summary>
        void ConfirmLogOut();

        /// <summary>S.MapInformation / S.MapChanged, after MapControl loaded the map and (S.MapChanged) the user was placed (classic: nothing).</summary>
        void MapChanged(int mapIndex);

        /// <summary>In-game patcher: Patching.PatchStatus changed (at most 4 times a second): show, update or hide the download strip.</summary>
        void PatchStatusChanged();

        /// <summary>In-game patcher: a program update is fully staged; ask "restart now?" once (Yes = PatchService.RequestRestart + close the game).</summary>
        void PatchUpdateReady();
    }
}

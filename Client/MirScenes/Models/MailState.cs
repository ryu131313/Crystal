namespace Client.MirScenes.Models
{
    /// <summary>
    /// Mail being composed (port of the WebClient/src/game/mail.ts subset). Was MailComposeParcelDialog.Items / ItemsIdx
    /// (statics): the parcel's attached item slots, the MirGridType.Mail grid. The mailbox itself stays on UserObject.Mail.
    /// </summary>
    public sealed class MailState
    {
        public const int ParcelSlots = 5;

        public readonly UserItem[] ParcelItems = new UserItem[ParcelSlots];
        public readonly ulong[] ParcelItemsIdx = new ulong[ParcelSlots];

        /// <summary>S.SentMail: the user's sent mail (C.RequestSentMail asks; C.HideSentMail hides one), newest first, at most MaxRows.</summary>
        public readonly List<ClientSentMail> Sent = new List<ClientSentMail>();
        public bool SentKnown;
        public readonly Emitter SentChanged = new Emitter();

        public readonly Emitter Changed = new Emitter();
    }
}

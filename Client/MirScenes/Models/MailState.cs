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

        public readonly Emitter Changed = new Emitter();
    }
}

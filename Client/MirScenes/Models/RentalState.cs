namespace Client.MirScenes.Models
{
    /// <summary>
    /// An item rental in progress (port of the WebClient/src/game/rental.ts subset). Was ItemRentingDialog.RentalItem and
    /// GuestItemRentingDialog.GuestLoanItem (statics): the MirGridType.Renting / GuestRenting slots.
    /// </summary>
    public sealed class RentalState
    {
        public UserItem RentalItem;
        public UserItem GuestLoanItem;

        public readonly Emitter Changed = new Emitter();
    }
}

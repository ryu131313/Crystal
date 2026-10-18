namespace Client.MirScenes.Models
{
    /// <summary>
    /// The game scene's model layer (Desktop-Modern-UI-Plan seam step 1; the desktop form of the model set in
    /// WebClient/src/ui/gameUi.ts). One instance per GameScene (GameScene.Models), built before the UI. GameScene.ProcessPacket
    /// writes game state here first, then hands the UI-only leftovers to IGameUi; the classic dialogs and the modern windows
    /// both read these, so nothing gameplay reads lives in a window.
    /// Other client state that is already window-free stays where it was: UserObject (inventory, equipment, magics, quests,
    /// mail), GameScene statics (Storage, GuildStorage, Refine, the info lists, NPCID / NPCRate) and ItemMoves (item locks and
    /// the picked-up item).
    /// </summary>
    public sealed class GameModels
    {
        public readonly ChatLog Chat = new ChatLog();
        public readonly BuffList Buffs = new BuffList();
        public readonly BuffList HeroBuffs = new BuffList();
        public readonly GroupState Group = new GroupState();
        public readonly GuildState Guild = new GuildState();
        public readonly TradeState Trade = new TradeState();
        public readonly MailState Mail = new MailState();
        public readonly NpcSession Npc = new NpcSession();
        public readonly MarketState Market = new MarketState();
        public readonly RentalState Rental = new RentalState();
        public readonly InspectState Inspect = new InspectState();
        public readonly FriendList Friends = new FriendList();
        public readonly ServerRulesState ServerRules = new ServerRulesState();
        public readonly AllowTogglesState AllowToggles = new AllowTogglesState();
        public readonly MapDetailsState MapDetails = new MapDetailsState();
        public readonly HeroAutoCastState HeroAutoCast = new HeroAutoCastState();
    }
}

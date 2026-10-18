namespace Client.MirScenes.Models
{
    /// <summary>
    /// The open NPC conversation and the item slots of the NPC-opened panels (port of the WebClient/src/game/npcSession.ts
    /// subset). Was NPCDialog's page lines, NPCDropDialog.TargetItem, NPCAwakeDialog.Items / ItemsIdx and CraftDialog.Slots /
    /// ShadowItems (statics). The NPC id, rate and panel type stay on GameScene (NPCID, NPCRate, NPCPanelType).
    /// </summary>
    public sealed class NpcSession
    {
        public const int AwakeSlots = 7;
        public const int CraftSlots = 9;

        /// <summary>The last S.NPCResponse page (raw markup lines).</summary>
        public readonly List<string> Page = new List<string>();
        public readonly Emitter PageChanged = new Emitter();

        /// <summary>The sell / repair / consign / … drop slot (MirGridType.DropPanel).</summary>
        public UserItem DropTarget;
        /// <summary>The awakening slots (MirGridType.AwakenItem) and the inventory index each came from.</summary>
        public readonly UserItem[] AwakeItems = new UserItem[AwakeSlots];
        public readonly int[] AwakeItemsIdx = new int[AwakeSlots];
        /// <summary>The craft slots (MirGridType.Craft) and the recipe's ghost items drawn under them.</summary>
        public readonly UserItem[] CraftItems = new UserItem[CraftSlots];
        public readonly UserItem[] CraftShadowItems = new UserItem[CraftSlots];

        public void SetPage(List<string> page)
        {
            Page.Clear();
            if (page != null) Page.AddRange(page);
            PageChanged.Emit();
        }
    }
}

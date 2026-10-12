// NPC dialog + shop (Client/MirScenes/Dialogs/NPCDialogs.cs, DOM edition).
//   NPCDialog:      S.NPCResponse page lines with the desktop markup: <text/@key> links (C.CallNPC "[@key]", @Exit closes),
//                   <<text/@key/Colour>> big buttons, {text/Colour} coloured text, (text/url) web links, [ITEM|MONSTER|NPC:idx(|name)]
//                   and <$ITEM:idx> name links. Talks are gated like GameScene.NPCTime (5 s until the server answers).
//                   MONSTER / NPC / ITEM links (NPCDialog.NewLink) show the hover tooltip (NPCDialog.ShowTooltip: image, title,
//                   GetMonsterInfo / GetNPCInfo / GetItemInfo lines) and request a missing info through GameInfo.
//   NPCGoodsDialog: S.NPCGoods list (Buy / BuySub); click to buy (C.BuyItem, an amount prompt for stackables).
//                   The buy-back list ([@BUYBACK], NPCScript) arrives as NPCGoods with PanelType.Buy and is bought with
//                   C.BuyItem by UniqueID (NPCScript.Buy looks in the BuyBack list); C.BuyItemBack is a server no-op.
//   NPCDropDialog:  S.NPCSell / NPCRepair / NPCSRepair; pick an inventory item, Confirm sends C.SellItem / RepairItem / SRepairItem.
//                   S.NPCConsign: Confirm asks the price and sends C.ConsignItem (npcConsign in marketWindow.ts; the market is there).
// Storage (StorageDialog) is ui/storageWindow.ts. Not ported: crafting (PanelType.Craft), pearls, refine, awakening, NPC images and sounds.
// Quests: the QuestButton lives here (hooks set by ui/questWindow.ts, which holds QuestListDialog).

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ItemTypeName, PanelType, RequiredClass, RequiredClassName, RequiredType, Stat } from "../protocol/enums.generated.ts";
import type { Session } from "../game/session.ts";
import type { Inventory } from "../game/inventory.ts";
import { type ItemInfos, itemPrice, repairPrice } from "../game/items.ts";
import type { InventoryWindow } from "./inventoryWindow.ts";
import { el } from "./screens.ts";
import { itemIcon } from "./inventoryWindow.ts";
import { libIcon } from "./buffBar.ts";
import { LibPath, monsterLibPath } from "../game/libraries.ts";
import { type GameInfo, type InfoKind, itemFriendlyName } from "../game/gameInfo.ts";
import { npcConsign } from "./marketWindow.ts";

const BIG = /<<((.*?)\/(@.*?))>>/g;
// One pass over every inline token, in index order like NPCDialog.NewText's sorted match list.
const INLINE = /<((?:[^<>]*?)\/(?:@[^<>]*?))>|\{(.*?)\/(.*?)\}|\((.*?)\/(.*?)\)|\[(MONSTER|NPC|ITEM):(\d+)(?:\|([^\]]+))?\]|<\$(MONSTER|NPC|ITEM):(\d+)>/gi;

type DropKind = "sell" | "repair" | "srepair" | "consign"; // consign: S.NPCConsign (marketWindow.ts npcConsign)
type LinkKind = "MONSTER" | "NPC" | "ITEM";
interface PageLink { span: HTMLElement; kind: LinkKind; idx: number; name: string | undefined }

/** RequiredClass.ToLocalizedString (the enum name; a combination of flags reads "Warrior, Wizard" like Enum.ToString). */
function requiredClassText(value: number): string {
  const named = RequiredClassName[value];
  if (named) return named;
  const parts: string[] = [];
  for (const [k, v] of Object.entries(RequiredClass)) if ((v & (v - 1)) === 0 && (value & v) !== 0) parts.push(k);
  return parts.join(", ") || String(value);
}

export class NpcWindow {
  private readonly session: Session;
  private readonly inv: Inventory;
  private readonly infos: ItemInfos;
  private readonly invWindow: InventoryWindow;
  private readonly now: () => number;
  private npcId = 0;
  private npcName = "";
  private npcTime = 0; // GameScene.NPCTime
  private rate = 1;    // GameScene.NPCRate

  private readonly dialog = el("div", { class: "window npc-window", hidden: true });
  private readonly title = el("span", {});
  private readonly body = el("div", { class: "npc-body" });
  private readonly bigButtons = el("div", { class: "npc-big" });
  private readonly goods = el("div", { class: "window npc-goods", hidden: true });
  private readonly drop = el("div", { class: "window npc-drop", hidden: true });
  private dropKind: DropKind = "sell";
  private dropIndex = -1;

  // Link tooltip (NPCDialog._tooltipPanel / _tooltipImage / _tooltipTitle / _tooltipContent)
  private readonly stage: HTMLElement;
  private readonly gameInfo: GameInfo;
  private readonly tooltip = el("div", { class: "npc-tooltip", hidden: true });
  private readonly tooltipImage = el("img", { class: "npc-tooltip-image", alt: "", hidden: true });
  private readonly tooltipTitle = el("div", { class: "npc-tooltip-title" });
  private readonly tooltipContent = el("div", { class: "npc-tooltip-content" });
  private tooltipLink: PageLink | null = null;
  private tooltipImageKey = "";
  private links: PageLink[] = [];
  private mapIndex = -1; // MapControl.Index / Title, for GetNPCInfo's location line
  private mapTitle = "";

  constructor(stage: HTMLElement, session: Session, inv: Inventory, infos: ItemInfos, invWindow: InventoryWindow, now: () => number, gameInfo: GameInfo) {
    this.stage = stage;
    this.gameInfo = gameInfo;
    this.tooltip.append(this.tooltipImage, el("div", { class: "npc-tooltip-text" }, this.tooltipTitle, this.tooltipContent));
    stage.append(this.tooltip);
    // A link's text and its open tooltip follow the info once it arrives (the desktop only fills them on the next page / hover).
    // GameInfo lives as long as the scene, like this window, so the listener needs no removal.
    gameInfo.onChange((kind, index) => this.infoArrived(kind, index));
    this.session = session;
    this.inv = inv;
    this.infos = infos;
    this.invWindow = invWindow;
    this.now = now;
    this.dialog.append(el("div", { class: "window-title" }, this.title, el("button", { class: "window-close", onclick: () => this.close() }, "×")), this.body, this.bigButtons, this.questButton);
    for (const w of [this.dialog, this.goods, this.drop]) {
      w.addEventListener("mousedown", (e) => e.stopPropagation());
      w.addEventListener("contextmenu", (e) => e.preventDefault());
      stage.append(w);
    }
  }

  get visible(): boolean {
    return !this.dialog.hidden;
  }

  // — Quests (NPCDialog.QuestButton / CheckQuestButtonDisplay; the quest list is src/ui/questWindow.ts, which sets the hooks) —
  /** NPCDialog.QuestButton: visible when the NPC has available quests; click toggles QuestListDialog. */
  private readonly questButton = el("button", { class: "npc-quest-button", hidden: true, onclick: () => this.onQuestButton(this.npcId) }, "Quests");
  /** NPCObject.GetAvailableQuests().Any() for the NPC (GameScene.NPCID). */
  questsAvailable: (npcId: number) => boolean = () => false;
  onQuestButton: (npcId: number) => void = () => {};
  /** GameScene.NPCID (the NPC the dialog talks to). */
  get currentNpcId(): number {
    return this.npcId;
  }
  /** NPCDialog.CheckQuestButtonDisplay. */
  refreshQuestButton(): void {
    this.questButton.hidden = !this.questsAvailable(this.npcId);
  }

  /** GameScene MapControl click on an NPC: C.CallNPC "[@Main]" (not again within 5 s, or while its dialog is open). */
  talk(id: number, name: string): void {
    const t = this.now();
    if (id === this.npcId && (t <= this.npcTime || this.visible)) return;
    this.npcTime = t + 5000;
    this.npcId = id;
    this.npcName = name;
    this.session.send(ClientPackets.CallNPC({ ObjectID: id, Key: "[@Main]" }));
  }

  /** RollDialog.ReturnResult: CallNPC "[page]" on the last NPC (GameScene.NPCID), gated by NPCTime like a dialog link. */
  callPage(page: string): void {
    const t = this.now();
    if (t <= this.npcTime) return;
    this.npcTime = t + 5000;
    this.session.send(ClientPackets.CallNPC({ ObjectID: this.npcId, Key: `[${page}]` }));
  }

  /** NPCDialog.ButtonClicked. */
  private action(key: string): void {
    if (key.toLowerCase() === "@exit") { this.close(); return; }
    const t = this.now();
    if (t <= this.npcTime) return;
    this.npcTime = t + 5000;
    this.session.send(ClientPackets.CallNPC({ ObjectID: this.npcId, Key: `[${key}]` }));
  }

  close(): void {
    this.hideTooltip();
    this.dialog.hidden = this.goods.hidden = true;
    this.closeDrop();
    this.onClose();
  }

  /** NPCDialog.Hide also hides the other NPC panels (refine, awakening, craft, ...): they chain onto this. */
  onClose: () => void = () => {};

  private closeDrop(): void {
    this.drop.hidden = true;
    this.dropIndex = -1;
    this.invWindow.itemClickHook = null;
  }

  handle(p: ServerPacket): void {
    switch (p.type) {
      case "MapInformation": case "MapChanged": this.mapIndex = p.MapIndex; this.mapTitle = p.Title; break;
      case "NPCResponse":
        this.npcTime = 0;
        this.goods.hidden = true;
        this.closeDrop();
        this.showPage(p.Page);
        break;
      case "NPCUpdate": this.npcId = p.NPCID; break;
      case "NPCGoods":
        this.rate = p.Rate;
        if (!this.visible) break;
        if (p.Type === PanelType.Buy || p.Type === PanelType.BuySub) this.showGoods(p.List, p.Type);
        break;
      case "NPCSell": if (this.visible) this.showDrop("sell"); break;
      case "NPCRepair": this.rate = p.Rate; if (this.visible) this.showDrop("repair"); break;
      case "NPCSRepair": this.rate = p.Rate; if (this.visible) this.showDrop("srepair"); break;
      case "NPCConsign": if (this.visible) this.showDrop("consign"); break; // GameScene.NPCConsign
      case "ConsignItem": if (!this.drop.hidden) this.renderDrop(); break;
      case "SellItem": case "RepairItem": case "GainedGold": case "LoseGold": case "ItemRepaired":
        if (!this.drop.hidden) this.renderDrop();
        break;
    }
  }

  // — Page —

  private showPage(lines: string[]): void {
    this.title.textContent = this.npcName || "NPC";
    this.hideTooltip();
    this.links = [];
    const big: HTMLElement[] = [];
    const kept: string[] = [];
    for (const line of lines) {
      let had = false;
      for (const m of line.matchAll(BIG)) {
        had = true;
        const [action, colour] = m[3]!.split("/");
        big.push(el("button", { class: "npc-big-button", style: colour ? `border-color:${colour}` : "", onclick: () => this.action(action!) }, m[2]!));
      }
      const rest = line.replace(BIG, "");
      if (!(had && rest.trim() === "")) kept.push(rest); // NewText drops lines that held only big buttons
    }
    this.body.replaceChildren(...kept.map((l) => this.renderLine(l)));
    this.bigButtons.replaceChildren(...big);
    this.bigButtons.hidden = big.length === 0;
    this.dialog.hidden = kept.length === 0 && big.length === 0;
    this.refreshQuestButton(); // NPCDialog.Show -> CheckQuestButtonDisplay
  }

  private renderLine(line: string): HTMLElement {
    const div = el("div", { class: "npc-line" });
    let at = 0;
    for (const m of line.matchAll(INLINE)) {
      if (m.index! > at) div.append(line.slice(at, m.index));
      at = m.index! + m[0].length;
      if (m[1] !== undefined) {
        const slash = m[1].indexOf("/@");
        const text = m[1].slice(0, slash), key = m[1].slice(slash + 1);
        div.append(el("span", { class: "npc-link", onclick: () => this.action(key) }, text));
      } else if (m[2] !== undefined) {
        div.append(el("span", { style: `color:${m[3]}` }, m[2]));
      } else if (m[4] !== undefined) {
        const url = m[5]!;
        div.append(/^https?:\/\//i.test(url)
          ? el("a", { class: "npc-link", href: url, target: "_blank", rel: "noopener noreferrer" }, m[4])
          : el("span", { class: "npc-link" }, m[4]));
      } else {
        // NPCDialog.NewLink: cyan, orange on hover, tooltip on enter, hidden on leave / click
        const kind = (m[6] ?? m[9])!.toUpperCase() as LinkKind, idx = Number(m[7] ?? m[10]), name = m[8];
        const span = el("span", { class: "npc-ref" }, this.linkName(kind, idx, name));
        const link: PageLink = { span, kind, idx, name };
        span.addEventListener("mouseenter", () => this.showTooltip(link));
        span.addEventListener("mouseleave", () => this.hideTooltip());
        span.addEventListener("click", () => this.hideTooltip());
        this.links.push(link);
        div.append(span);
      }
    }
    if (at < line.length) div.append(line.slice(at));
    if (!div.childNodes.length) div.append(" ");
    return div;
  }

  // — Info links (NPCDialog.GetDisplayNameForLink / ShowTooltip / HideTooltip) —

  /** NPCDialog.GetDisplayNameForLink (requests a missing info). */
  private linkName(kind: LinkKind, idx: number, provided: string | undefined): string {
    if (provided) return provided;
    switch (kind) {
      case "ITEM": {
        const item = this.gameInfo.item(idx);
        if (item) return itemFriendlyName(item) || item.Name || `Item ${idx}`;
        this.gameInfo.requestItem(idx);
        return `Item ${idx}`;
      }
      case "MONSTER": {
        const monster = this.gameInfo.monster(idx);
        if (monster) return monster.GameName || monster.Name;
        this.gameInfo.requestMonster(idx);
        return `Monster ${idx}`;
      }
      case "NPC": {
        const npc = this.gameInfo.npc(idx);
        if (npc) return npc.Name;
        this.gameInfo.requestNPC(idx);
        return `NPC ${idx}`;
      }
    }
  }

  private infoArrived(kind: InfoKind, index: number): void {
    if (kind === "map") { if (this.tooltipLink?.kind === "NPC") this.showTooltip(this.tooltipLink); return; }
    const want: LinkKind | null = kind === "monster" ? "MONSTER" : kind === "npc" ? "NPC" : kind === "item" ? "ITEM" : null;
    if (!want) return;
    for (const link of this.links) {
      if (link.kind !== want || link.idx !== index) continue;
      if (!link.name) link.span.textContent = this.linkName(link.kind, link.idx, undefined);
      if (link === this.tooltipLink) this.showTooltip(link);
    }
  }

  /** NPCDialog.ShowTooltip: image (monster frame 20, NPC frame 0, item Items_Tooltip_32bit else Items) + title + info lines,
   *  20 px right of and below the link. */
  private showTooltip(link: PageLink): void {
    this.tooltipLink = link;
    const { kind, idx } = link;
    let title = "", content = "";
    let image: { lib: string; index: number; fallback?: string } | null = null;
    switch (kind) {
      case "MONSTER": {
        const monster = this.gameInfo.monster(idx);
        if (monster) {
          title = monster.GameName || monster.Name;
          content = this.monsterInfo(idx);
          const body = monsterLibPath(monster.Image); // NPCDialog uses Monsters[Image]; the web helper also covers Pet / Gate / Siege / Dragon
          image = body === null ? null : { lib: body, index: 20 };
        } else {
          this.gameInfo.requestMonster(idx);
          title = `Monster Index: ${idx}`;
          content = "Monster info is being loaded...";
        }
        break;
      }
      case "NPC": {
        const npc = this.gameInfo.npc(idx);
        if (npc) {
          title = npc.Name;
          content = this.npcInfo(idx);
          image = { lib: LibPath.npc(npc.Image), index: 0 };
        } else {
          this.gameInfo.requestNPC(idx);
          title = `NPC Index: ${idx}`;
          content = "NPC info is being loaded...";
        }
        break;
      }
      case "ITEM": {
        const item = this.gameInfo.item(idx);
        if (item) {
          title = itemFriendlyName(item) || item.Name;
          content = this.itemInfo(idx);
          image = { lib: "Items_Tooltip_32bit", index: item.Image, fallback: "Items" };
        } else {
          this.gameInfo.requestItem(idx);
          title = `Item Index: ${idx}`;
          content = "Item info is being loaded...";
        }
        break;
      }
    }
    this.tooltipTitle.textContent = title;
    this.tooltipContent.textContent = content;
    const key = image ? `${image.lib}#${image.index}` : "";
    if (key !== this.tooltipImageKey) {
      this.tooltipImageKey = key;
      this.tooltipImage.hidden = true;
      this.tooltipImage.removeAttribute("src");
      if (image) {
        const { lib, index, fallback } = image;
        void libIcon(lib, index)
          .then((url) => url ?? (fallback ? libIcon(fallback, index) : null))
          .then((url) => {
            if (!url || this.tooltipImageKey !== key) return;
            this.tooltipImage.src = url;
            this.tooltipImage.hidden = false;
          });
      }
    }
    const a = link.span.getBoundingClientRect(), s = this.stage.getBoundingClientRect();
    this.tooltip.style.left = `${a.left - s.left + 20}px`;
    this.tooltip.style.top = `${a.top - s.top + 20}px`;
    this.tooltip.hidden = false;
  }

  /** NPCDialog.HideTooltip. */
  private hideTooltip(): void {
    this.tooltip.hidden = true;
    this.tooltipLink = null;
  }

  /** NPCDialog.GetMonsterInfo. */
  private monsterInfo(idx: number): string {
    const m = this.gameInfo.monster(idx);
    if (!m) { this.gameInfo.requestMonster(idx); return `Monster Index: ${idx}\n(Loading...)`; }
    const st = (s: number) => m.Stats[s] ?? 0;
    return [
      `Level: ${m.Level}`, `Health: ${st(Stat.HP)}`, `Experience: ${m.Experience}`,
      `DC: ${st(Stat.MinDC)}~${st(Stat.MaxDC)}`, `MC: ${st(Stat.MinMC)}~${st(Stat.MaxMC)}`,
      `AC: ${st(Stat.MinAC)}~${st(Stat.MaxAC)}`, `MAC: ${st(Stat.MinMAC)}~${st(Stat.MaxMAC)}`,
    ].join("\n");
  }

  /** NPCDialog.GetNPCInfo: the map title comes from the user's map or MapInfoList, else C.RequestMapInfo once. */
  private npcInfo(idx: number): string {
    const npc = this.gameInfo.npc(idx);
    if (!npc) { this.gameInfo.requestNPC(idx); return `NPC Index: ${idx}\n(Loading...)`; }
    const lines = [`Name: ${npc.Name}`];
    const mapName = this.mapIndex === npc.MapIndex ? this.mapTitle : npc.MapIndex > 0 ? this.gameInfo.map(npc.MapIndex)?.Title ?? "" : "";
    if (mapName.trim()) lines.push(`Location: ${mapName}`);
    else if (npc.MapIndex > 0) { this.gameInfo.requestMap(npc.MapIndex); lines.push("Location: Loading map..."); }
    else lines.push("Location: Unknown map");
    lines.push(`Coordinates: (${npc.Location.X}, ${npc.Location.Y})`);
    return lines.join("\n");
  }

  /** NPCDialog.GetItemInfo (ItemType / RequiredClass names stand in for ToLocalizedString). */
  private itemInfo(idx: number): string {
    const item = this.gameInfo.item(idx);
    if (!item) { this.gameInfo.requestItem(idx); return `Item Index: ${idx}\n(Loading...)`; }
    const lines: string[] = [];
    const type = ItemTypeName[item.Type];
    if (type) lines.push(`Type: ${type}`);
    if (item.RequiredType === RequiredType.Level && item.RequiredAmount > 0) lines.push(`Required Level : ${item.RequiredAmount}`);
    if (item.ClassBased) lines.push(`Class Required : ${requiredClassText(item.RequiredClass)}`);
    if (item.Weight > 0) lines.push(`Weight: ${item.Weight}`);
    return lines.join("\n");
  }

  // — Goods (NPCGoodsDialog) —

  private showGoods(list: UserItem[], type: number): void {
    const rows = list.map((item) => {
      const info = this.infos.get(item.ItemIndex);
      const unit = info ? Math.trunc(itemPrice({ ...item, Count: 1 }, info) * this.rate) : 0;
      const img = el("img", { class: "npc-goods-icon", alt: "" });
      if (info) void itemIcon(info.Image).then((url) => { if (url) img.src = url; });
      return el("div", { class: "npc-goods-row", title: info?.Name ?? "", onclick: () => this.buy(item, type) },
        img, el("span", { class: "npc-goods-name" }, info?.Name ?? `Item #${item.ItemIndex}`, item.Count > 1 ? ` (${item.Count})` : ""),
        el("span", { class: "npc-goods-price" }, `${unit.toLocaleString()} gold`));
    });
    this.goods.replaceChildren(el("div", { class: "window-title" }, el("span", {}, "Goods"), el("button", { class: "window-close", onclick: () => (this.goods.hidden = true) }, "×")),
      el("div", { class: "npc-goods-list" }, ...rows));
    this.goods.hidden = false;
  }

  /** NPCGoodsDialog.BuyItem: stackables ask for an amount (up to the stack size and what the gold covers). */
  private buy(item: UserItem, type: number): void {
    const info = this.infos.get(item.ItemIndex);
    if (!info) return;
    const unit = Math.max(1, Math.trunc(itemPrice({ ...item, Count: 1 }, info) * this.rate));
    let count = 1;
    if (info.StackSize > 1) {
      const max = Math.min(info.StackSize, Math.trunc(this.inv.gold / unit));
      if (max <= 0) return void alert("You do not have enough gold.");
      const answer = prompt(`Buy how many ${info.Name}? (1-${max})`, String(Math.min(item.Count, max)));
      if (answer === null) return;
      count = Math.trunc(Number(answer));
      if (!(count >= 1 && count <= max)) return;
    } else {
      if (unit > this.inv.gold) return void alert("You do not have enough gold.");
      if (!this.inv.inventory.some((i) => !i)) return void alert("You do not have enough bag space.");
    }
    this.session.send(ClientPackets.BuyItem({ ItemIndex: item.UniqueID, Count: count, Type: type }));
  }

  // — Sell / repair (NPCDropDialog) —

  private showDrop(kind: DropKind): void {
    this.dropKind = kind;
    this.dropIndex = -1;
    this.invWindow.toggle(true);
    this.invWindow.itemClickHook = (index) => {
      this.dropIndex = index;
      this.renderDrop();
      return true;
    };
    this.drop.hidden = false;
    this.renderDrop();
  }

  private dropPrice(item: UserItem): number {
    const info = this.infos.get(item.ItemIndex);
    switch (this.dropKind) {
      case "sell": return Math.trunc(itemPrice(item, info) / 2);
      case "repair": return Math.trunc(repairPrice(item, info) * this.rate);
      case "consign": return 0;
      default: return Math.trunc(repairPrice(item, info) * 3 * this.rate);
    }
  }

  private renderDrop(): void {
    const item = this.dropIndex >= 0 ? this.inv.inventory[this.dropIndex] ?? null : null;
    if (!item) this.dropIndex = -1;
    const label = { sell: "Sell", repair: "Repair", srepair: "Special repair", consign: "Consign" }[this.dropKind];
    const info = item ? this.infos.get(item.ItemIndex) : undefined;
    const cell = el("div", { class: "inv-cell npc-drop-cell" });
    if (info) {
      const img = el("img", { alt: info.Name });
      void itemIcon(info.Image).then((url) => { if (url) img.src = url; });
      cell.append(img);
    }
    const confirm = el("button", {
      class: "primary", disabled: !item || this.inv.isLocked(item),
      onclick: () => {
        if (this.dropIndex >= 0) {
          if (this.dropKind === "consign") npcConsign(this.inv, this.dropIndex, (t) => this.invWindow.notify(t));
          else this.inv.npcAction(this.dropIndex, this.dropKind);
        }
        this.dropIndex = -1; this.renderDrop();
      },
    }, label);
    this.drop.replaceChildren(
      el("div", { class: "window-title" }, el("span", {}, label), el("button", { class: "window-close", onclick: () => this.closeDrop() }, "×")),
      el("div", { class: "npc-drop-body" }, cell, el("div", {},
        el("div", {}, item ? `${info?.Name ?? "Item"}${item.Count > 1 ? ` x${item.Count}` : ""}` : "Click an item in your bag."),
        el("div", { class: "npc-goods-price" }, item && this.dropKind !== "consign" ? `${this.dropKind === "sell" ? "Receive" : "Cost"}: ${this.dropPrice(item).toLocaleString()} gold` : ""))),
      el("div", { class: "row" }, confirm),
    );
  }
}

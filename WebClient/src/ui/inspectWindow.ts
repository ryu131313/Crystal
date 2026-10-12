// Inspect + observe: InspectDialog (S.PlayerInspect; Ctrl+right-click on another player, or a RankingDialog row) and
// the AllowObserve flag (S.AllowObserve, OptionDialog.ToggleObserve = the "@ALLOWOBSERVE" chat command).
// Deliberate difference: the paperdoll (CharacterPage.AfterDraw: StateItems armour / weapon / helmet, hair) is not drawn;
// the equipment shows as the inventory window's EQUIP_LAYOUT grid with icons + tooltips, and the level is shown as text.
// Observe only sends C.Observe: the observer mode (UserInformation.Observer, GameScene.Observing) is not ported, and the
// server ends the observer's own game (StopGame) when the request succeeds. Mail is not ported (no Mail button).

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, EquipmentSlotName, MirClassName } from "../protocol/enums.generated.ts";
import type { GameInfo } from "../game/gameInfo.ts";
import { EQUIP_LAYOUT, type InventoryWindow } from "./inventoryWindow.ts";
import { el } from "./screens.ts";

const MAX_GROUP = 11; // Globals.MaxGroup
const INSPECT_DELAY = 500; // GameScene.InspectTime = CMain.Time + 500

export class InspectWindow {
  // InspectDialog fields
  items: (UserItem | null)[] = [];
  name = "";
  guildName = "";
  guildRank = "";
  cls = 0;
  gender = 0;
  hair = 0;
  level = 0;
  loverName = "";
  allowObserve = false;
  isHero = false;
  /** InspectDialog.InspectID. */
  inspectId = 0;
  /** GameScene.AllowObserve (the user's own flag); null until the server says (UserInformation's flag is not decoded). */
  userAllowObserve: boolean | null = null;
  private inspectTime = 0;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly prefillChat: (text: string) => void;
  private readonly groupList: () => string[];
  private readonly me: () => string;
  private readonly now: () => number;
  private readonly invWindow: InventoryWindow;
  private readonly gameInfo: GameInfo;
  private readonly root = el("div", { class: "window inspect-window", hidden: true });
  private readonly nameLabel = el("div", { class: "inspect-name", title: "Whisper" });
  private readonly guildLabel = el("div", { class: "magic-meta" });
  private readonly classLabel = el("div", {});
  private readonly loverLabel = el("div", { class: "magic-meta" });
  private readonly equipGrid = el("div", { class: "inv-equip" });
  private readonly playerButtons = el("div", { class: "row" });
  /** The user's own observe toggle (OptionDialog ObserveOn / ObserveOff), shown in this window's footer. */
  readonly observeToggle = el("button", { title: "Allow other players to observe you (@ALLOWOBSERVE)" });

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, prefillChat: (text: string) => void,
    groupList: () => string[], me: () => string, now: () => number, invWindow: InventoryWindow, gameInfo: GameInfo) {
    this.send = send;
    this.chat = chat;
    this.prefillChat = prefillChat;
    this.groupList = groupList;
    this.me = me;
    this.now = now;
    this.invWindow = invWindow;
    this.gameInfo = gameInfo;
    // InspectDialog.NameLabel.Click: "/Name " in the chat box
    this.nameLabel.onclick = () => this.prefillChat(`/${this.name} `);
    this.observeToggle.onclick = () => this.toggleObserve();
    this.playerButtons.append(
      el("button", { title: "Invite to Group", onclick: () => this.group() }, "Group"),
      el("button", { title: "Add to Friends List", onclick: () => this.send(ClientPackets.AddFriend({ Name: this.name, Blocked: false })) }, "Friend"),
      el("button", { title: "Trade (the player you face)", onclick: () => this.send(ClientPackets.TradeRequest()) }, "Trade"),
      // Hidden until observer mode is ported: a granted C.Observe ends the requester's own game (StopGame) and streams the target's view
      el("button", { title: "Observe", hidden: true, onclick: () => this.observe() }, "Observe"));
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Inspect"), el("button", { class: "window-close", onclick: () => this.toggle(false) }, "×")),
      this.nameLabel, this.guildLabel, this.classLabel, this.loverLabel,
      el("div", { class: "inv-label" }, "Equipment"), this.equipGrid,
      this.playerButtons,
      el("div", { class: "row inspect-footer" }, this.observeToggle),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    stage.append(this.root);
    // GameScene.Bind -> RequestItemInfo: redraw when a missing ItemInfo arrives
    gameInfo.onChange((kind, index) => {
      if (kind === "item" && !this.root.hidden && this.items.some((i) => i?.ItemIndex === index)) this.render();
    });
    this.renderObserveToggle();
  }

  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
    if (show) this.render();
  }

  /** MapControl.OnMouseClick (Right + Ctrl on another PlayerObject / HeroObject) and RankingRow.Inspect, with the shared
   *  GameScene.InspectTime gate. `objectId` is the player's ObjectID, or `(uint)PlayerId` with ranking = true. */
  inspect(objectId: number, ranking = false, hero = false): boolean {
    const t = this.now();
    if (t < this.inspectTime || (ranking && t <= this.inspectTime)) return false;
    this.inspectTime = t + INSPECT_DELAY;
    this.inspectId = objectId >>> 0;
    this.send(ClientPackets.Inspect({ ObjectID: objectId >>> 0, Ranking: ranking, Hero: hero }));
    return true;
  }

  /** Server packets; returns true when handled. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "PlayerInspect": // GameScene.PlayerInspect
        this.items = p.Equipment;
        this.name = p.Name;
        this.guildName = p.GuildName;
        this.guildRank = p.GuildRank;
        this.cls = p.Class;
        this.gender = p.Gender;
        this.hair = p.Hair;
        this.level = p.Level;
        this.loverName = p.LoverName;
        this.allowObserve = p.AllowObserve;
        this.isHero = p.IsHero;
        // InspectDialog.RefreshInferface: GameScene.Bind each item (asks for missing ItemInfo)
        for (const item of this.items) if (item) this.gameInfo.requestItem(item.ItemIndex);
        this.toggle(true);
        return true;
      case "AllowObserve": // GameScene.ProcessPacket: AllowObserve = p.Allow
        this.setUserAllowObserve(p.Allow);
        return true;
    }
    return false;
  }

  /** GameScene.UserInformation sets AllowObserve too; game.ts may call this if it decodes that flag. */
  setUserAllowObserve(allow: boolean): void {
    this.userAllowObserve = allow;
    this.renderObserveToggle();
  }

  /** InspectDialog.GroupButton.Click (the desktop still sends after the not-leader line; the server refuses). */
  private group(): void {
    const list = this.groupList();
    if (list.length >= MAX_GROUP) { this.chat("Your group already has the maximum number of members.", ChatType.System); return; }
    if (list.length > 0 && list[0] !== this.me()) this.chat("You are not the leader of your group.", ChatType.System);
    this.send(ClientPackets.AddMember({ Name: this.name }));
  }

  /** InspectDialog.ObserveButton.Click. */
  private observe(): void {
    if (this.allowObserve) this.send(ClientPackets.Observe({ Name: this.name }));
    else this.chat("That player has disabled observation.", ChatType.System);
  }

  /** OptionDialog.ToggleObserve: one toggle here (the desktop's On / Off buttons skip a no-op change). */
  private toggleObserve(): void {
    this.send(ClientPackets.Chat({ Message: "@ALLOWOBSERVE" }));
  }

  private renderObserveToggle(): void {
    this.observeToggle.textContent = `My observation: ${this.userAllowObserve === null ? "?" : this.userAllowObserve ? "Allowed" : "Blocked"}`;
  }

  private render(): void {
    if (this.root.hidden) return;
    this.nameLabel.textContent = this.name;
    this.guildLabel.textContent = `${this.guildName} ${this.guildRank}`.trim();
    this.classLabel.textContent = `Lv ${this.level} ${MirClassName[this.cls] ?? ""}`;
    this.loverLabel.textContent = this.loverName !== "" ? `Lover: ${this.loverName}` : ""; // LoverButton (hint = LoverName)
    this.loverLabel.hidden = this.loverName === "";
    this.equipGrid.replaceChildren(...EQUIP_LAYOUT.flat().map((slot) => {
      if (slot < 0) return el("div", { class: "inv-gap" });
      const item = this.items[slot] ?? null;
      const c = this.invWindow.viewCell(item);
      if (!item) { c.title = EquipmentSlotName[slot] ?? ""; c.append(el("span", { class: "inv-slot-label" }, EquipmentSlotName[slot] ?? "")); }
      return c;
    }));
    // RefreshInferface: Observe / Trade / Mail / Friend / Group hidden for a hero
    this.playerButtons.hidden = this.isHero;
    this.renderObserveToggle();
  }
}

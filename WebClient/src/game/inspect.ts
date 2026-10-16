// Inspect: InspectDialog's state (Items, Name, GuildName, GuildRank, Class, Gender, Hair, Level, LoverName, AllowObserve,
// IsHero, InspectID; Client/MirScenes/Dialogs/MainDialogs.cs), the shared GameScene.InspectTime gate (500 ms; per game:
// GameScene.Dispose resets it) for MapControl.OnMouseClick (Ctrl + right-click on another player / hero) and
// RankingRow.Inspect, GameScene.PlayerInspect, and the dialog's player buttons (Group / Friend / Trade / Observe).
// game.ts feeds handle(); the UI opens its inspect window on `received`.

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { MAX_GROUP, type GroupState } from "./group.ts";
import { Emitter } from "./emitter.ts";

/** GameScene.InspectTime = CMain.Time + 500. */
export const INSPECT_DELAY = 500;

const T = { // ClientTextKeys (English.json)
  GroupHasMaxMembers: "Your group already has the maximum number of members.", YouNotGroupLeader: "You are not the leader of your group.",
  PlayerDisabledObservation: "That player has disabled observation.",
};

export interface InspectDeps {
  send(f: Uint8Array): void;
  /** performance.now() (CMain.Time). */
  now(): number;
  chat(text: string, type: number): void;
  /** GameScene.Bind: C.RequestItem for a missing ItemInfo (GameInfo.requestItem). */
  requestItem(index: number): void;
  /** GroupDialog.GroupList (InspectDialog.GroupButton). */
  group: GroupState;
  /** The user's name (GameScene.User.Name). */
  me(): string;
}

export class Inspect {
  /** GameScene.PlayerInspect applied, ItemInfos requested: the dialog opens (InspectDialog.Show). */
  readonly received = new Emitter<void>();
  private _items: (UserItem | null)[] = [];
  private _name = "";
  private _guildName = "";
  private _guildRank = "";
  private _cls = 0;
  private _gender = 0;
  private _hair = 0;
  private _level = 0;
  private _loverName = "";
  private _allowObserve = false;
  private _isHero = false;
  private _inspectId = 0;
  private inspectTime = 0;

  constructor(private readonly deps: InspectDeps) {}

  /** InspectDialog.Items (indexed by EquipmentSlot). */
  get items(): readonly (UserItem | null)[] { return this._items; }
  get name(): string { return this._name; }
  get guildName(): string { return this._guildName; }
  get guildRank(): string { return this._guildRank; }
  /** InspectDialog.Class (MirClass). */
  get cls(): number { return this._cls; }
  get gender(): number { return this._gender; }
  get hair(): number { return this._hair; }
  get level(): number { return this._level; }
  get loverName(): string { return this._loverName; }
  /** The inspected player allows observation. */
  get allowObserve(): boolean { return this._allowObserve; }
  get isHero(): boolean { return this._isHero; }
  /** InspectDialog.InspectID (the last request's id). */
  get inspectId(): number { return this._inspectId; }

  /** MapControl.OnMouseClick (Right + Ctrl on another PlayerObject / HeroObject) and RankingRow.Inspect, with the shared
   *  GameScene.InspectTime gate. `objectId` is the player's ObjectID, or `(uint)PlayerId` with ranking = true. */
  inspect(objectId: number, ranking = false, hero = false): boolean {
    const t = this.deps.now();
    if (t < this.inspectTime || (ranking && t <= this.inspectTime)) return false;
    this.inspectTime = t + INSPECT_DELAY;
    this._inspectId = objectId >>> 0;
    this.deps.send(ClientPackets.Inspect({ ObjectID: objectId >>> 0, Ranking: ranking, Hero: hero }));
    return true;
  }

  /** InspectDialog.GroupButton.Click (the desktop still sends after the not-leader line; the server refuses). */
  addToGroup(): void {
    const list = this.deps.group.list;
    if (list.length >= MAX_GROUP) { this.deps.chat(T.GroupHasMaxMembers, ChatType.System); return; }
    if (list.length > 0 && list[0] !== this.deps.me()) this.deps.chat(T.YouNotGroupLeader, ChatType.System);
    this.deps.send(ClientPackets.AddMember({ Name: this._name }));
  }

  /** InspectDialog.FriendButton.Click. */
  addFriend(): void {
    this.deps.send(ClientPackets.AddFriend({ Name: this._name, Blocked: false }));
  }

  /** InspectDialog.TradeButton.Click. */
  tradeRequest(): void {
    this.deps.send(ClientPackets.TradeRequest());
  }

  /** InspectDialog.ObserveButton.Click (no UI shows it: observer mode is not ported). */
  observe(): void {
    if (this._allowObserve) this.deps.send(ClientPackets.Observe({ Name: this._name }));
    else this.deps.chat(T.PlayerDisabledObservation, ChatType.System);
  }

  /** Server packets; true = consumed (the classic chain returned on S.PlayerInspect). */
  handle(p: ServerPacket): boolean {
    if (p.type !== "PlayerInspect") return false;
    // GameScene.PlayerInspect
    this._items = p.Equipment;
    this._name = p.Name;
    this._guildName = p.GuildName;
    this._guildRank = p.GuildRank;
    this._cls = p.Class;
    this._gender = p.Gender;
    this._hair = p.Hair;
    this._level = p.Level;
    this._loverName = p.LoverName;
    this._allowObserve = p.AllowObserve;
    this._isHero = p.IsHero;
    // InspectDialog.RefreshInferface: GameScene.Bind each item (asks for missing ItemInfo)
    for (const item of this._items) if (item) this.deps.requestItem(item.ItemIndex);
    this.received.emit();
    return true;
  }
}

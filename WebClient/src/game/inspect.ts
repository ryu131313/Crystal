// Inspect: InspectDialog's state (Items, Name, GuildName, GuildRank, Class, Gender, Hair, Level, LoverName, AllowObserve,
// IsHero, InspectID; Client/MirScenes/Dialogs/MainDialogs.cs), the shared GameScene.InspectTime gate (500 ms; per game:
// GameScene.Dispose resets it) for MapControl.OnMouseClick (Ctrl + right-click on another player / hero) and
// RankingRow.Inspect, GameScene.PlayerInspect, and the dialog's player buttons (Group / Friend / Trade / Observe).
// game.ts feeds handle(); the UI opens its inspect window on `received`.
//
// Phase 2.5 (web-only): S.PlayerProfile follows S.PlayerInspect for a player (the server sends it even when the text is
// empty); `profileText` holds it for the inspected player and `profile` fires when it arrives. setProfile() sends
// C.SetProfile for the user's own text (the server trims, strips control characters, caps at 120 characters, applies a
// 10 s cooldown and answers with a chat line).

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

/** CharacterInfo.ProfileTextMaxLength (the server cuts longer text). */
export const PROFILE_MAX_LENGTH = 120;
/** The server's C.SetProfile cooldown. */
export const PROFILE_THROTTLE = 10000;

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
  private _profileText = "";
  private _hasProfile = false;
  private lastProfileSet = -Infinity;
  /** S.PlayerProfile for the inspected player applied (`profileText`). */
  readonly profile = new Emitter<void>();

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

  /** The inspected player's profile text (S.PlayerProfile; "" = none or not received). */
  get profileText(): string { return this._profileText; }
  /** S.PlayerProfile arrived for the inspected player (false for heroes and older servers). */
  get hasProfile(): boolean { return this._hasProfile; }

  /** C.SetProfile (web): the user's own profile text. False = inside the 10 s throttle (nothing sent); the text is trimmed
   *  and cut to 120 characters here too (the server does the same and answers with a chat line). */
  setProfile(text: string): boolean {
    const t = this.deps.now();
    if (t - this.lastProfileSet < PROFILE_THROTTLE) return false;
    this.lastProfileSet = t;
    this.deps.send(ClientPackets.SetProfile({ Text: text.trim().slice(0, PROFILE_MAX_LENGTH) }));
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
    if (p.type === "PlayerProfile") {
      // Only the profile of the player just inspected (S.PlayerInspect came first)
      if (p.Name !== this._name) return true;
      this._profileText = p.Text;
      this._hasProfile = true;
      this.profile.emit();
      return true;
    }
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
    this._profileText = ""; this._hasProfile = false; // S.PlayerProfile follows for a player
    // InspectDialog.RefreshInferface: GameScene.Bind each item (asks for missing ItemInfo)
    for (const item of this._items) if (item) this.deps.requestItem(item.ItemIndex);
    this.received.emit();
    return true;
  }
}

// Account flags of the user, without the DOM:
//   GameScene.AllowObserve      S.UserInformation.AllowObserve, then S.AllowObserve (OptionDialog shows it, InspectDialog
//                               checks it; OptionDialog.ToggleObserve sends C.Chat "@ALLOWOBSERVE")
//   UserObject storage flags    HasExpandedStorage / ExpandedStorageExpiryTime / HasStoragePassword /
//                               RequireStoragePassword / StoragePasswordLastSet from S.UserInformation, then
//                               S.ResizeStorage, S.StorageUnlockResult and S.StoragePasswordResult (StorageDialog reads
//                               them; its password prompts and the open / close with the NPC stay in the window).
// handle() consumes only S.AllowObserve (GameScene.ProcessPacket returns after it); the storage packets go on to the
// storage window (prompts) and the inventory (S.ResizeStorage resizes the grid).

// removeStoragePassword() sends C.RemoveStoragePassword (web; the desktop has no sender); S.StoragePasswordResult with
// Removing = true answers (Result 4 = removed).

// Social allow toggles (web-only, phase 2.8): S.AllowToggles { Flags } carries every toggle as bit (1 << AllowToggle.X);
// the server sends it at login and after any change from any path (C.SwitchGroup, C.ChangeTrade, C.ChangeMarriage,
// C.AllowMentor, NPC @ALLOW* commands, C.SetAllowToggle). S.SwitchGroup / S.AllowObserve also update the Group / Observe
// bits so the flags stay right if they arrive alone. setToggle() sends C.SetAllowToggle (an idempotent setter; the
// server answers with S.AllowToggles). The desktop shows none of this (OptionDialog keeps its own observe / group state).

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { allowToggleOn } from "../protocol/server.ts";
import { AllowToggle } from "../protocol/enums.generated.ts";
import { Emitter } from "./emitter.ts";

export class Account {
  /** A flag changed. */
  readonly changed = new Emitter<void>();
  /** The social allow toggles changed (S.AllowToggles, or the Group / Observe bit from S.SwitchGroup / S.AllowObserve). */
  readonly togglesChanged = new Emitter<void>();
  private _toggleFlags = 0;
  private _togglesKnown = false;
  private _allowObserve = false;
  private _hasExpandedStorage = false;
  private _expandedStorageExpiryTime = 0n;
  private _hasStoragePassword = false;
  private _hadStoragePassword = false;
  private _requireStoragePassword = false;
  private _storagePasswordLastSet = 0n;

  constructor(private readonly send: (f: Uint8Array) => void = () => {}) {}

  /** GameScene.AllowObserve. */
  get allowObserve(): boolean { return this._allowObserve; }
  /** UserObject.HasExpandedStorage (storage page 2). */
  get hasExpandedStorage(): boolean { return this._hasExpandedStorage; }
  /** UserObject.ExpandedStorageExpiryTime (DateTime.ToBinary). */
  get expandedStorageExpiryTime(): bigint { return this._expandedStorageExpiryTime; }
  /** UserObject.HasStoragePassword. */
  get hasStoragePassword(): boolean { return this._hasStoragePassword; }
  /** HasStoragePassword before the last S.StoragePasswordResult (StorageDialog.HandleStoragePasswordResult's "set" vs
   *  "changed" line). */
  get hadStoragePassword(): boolean { return this._hadStoragePassword; }
  /** UserObject.RequireStoragePassword (Settings.RequireStoragePassword on the server). */
  get requireStoragePassword(): boolean { return this._requireStoragePassword; }
  /** UserObject.StoragePasswordLastSet (DateTime.ToBinary; 0 = never). */
  get storagePasswordLastSet(): bigint { return this._storagePasswordLastSet; }

  /** S.AllowToggles.Flags (bit 1 << AllowToggle.X = on). */
  get toggleFlags(): number { return this._toggleFlags; }
  /** False until the first S.AllowToggles (an older server never sends it). */
  get togglesKnown(): boolean { return this._togglesKnown; }
  /** One toggle's state. */
  toggle(t: AllowToggle): boolean { return allowToggleOn(this._toggleFlags, t); }
  /** AllowToggle.Group (also echoed by S.SwitchGroup). */
  get allowGroup(): boolean { return this.toggle(AllowToggle.Group); }
  /** AllowToggle.Trade (PlayerObject.AllowTrade). */
  get allowTrade(): boolean { return this.toggle(AllowToggle.Trade); }
  /** AllowToggle.Observe (same value as allowObserve once S.AllowToggles arrived). */
  get allowObserveToggle(): boolean { return this.toggle(AllowToggle.Observe); }
  /** AllowToggle.GuildInvite (@ALLOWGUILD). */
  get allowGuildInvite(): boolean { return this.toggle(AllowToggle.GuildInvite); }
  /** AllowToggle.GroupRecall (@ENABLEGROUPRECALL). */
  get allowGroupRecall(): boolean { return this.toggle(AllowToggle.GroupRecall); }
  /** AllowToggle.Marriage (C.ChangeMarriage). */
  get allowMarriage(): boolean { return this.toggle(AllowToggle.Marriage); }
  /** AllowToggle.LoverRecall. */
  get allowLoverRecall(): boolean { return this.toggle(AllowToggle.LoverRecall); }
  /** AllowToggle.Mentor (C.AllowMentor). */
  get allowMentor(): boolean { return this.toggle(AllowToggle.Mentor); }
  /** AllowToggle.FriendRequests (CharacterInfo.AllowFriendRequests). */
  get allowFriendRequests(): boolean { return this.toggle(AllowToggle.FriendRequests); }

  /** C.SetAllowToggle: set one toggle explicitly (no flip). The server validates the value, runs the same side effects as
   *  the old toggle path (leaving the group, chat hint, ...) and answers with S.AllowToggles; the local flags change only
   *  then. False = unknown toggle, nothing sent. */
  setToggle(toggle: AllowToggle, value: boolean): boolean {
    if (!Number.isInteger(toggle) || toggle < AllowToggle.Group || toggle > AllowToggle.FriendRequests) return false;
    this.send(ClientPackets.SetAllowToggle({ Toggle: toggle, Value: value }));
    return true;
  }

  private setBit(t: AllowToggle, on: boolean): void {
    const next = on ? this._toggleFlags | (1 << t) : this._toggleFlags & ~(1 << t);
    if (next === this._toggleFlags) return;
    this._toggleFlags = next;
    this.togglesChanged.emit();
  }

  /** C.RemoveStoragePassword: the server checks the storage NPC page / range and the password (StoragePasswordResult
   *  Result 1 = invalid, 2 = wrong, 4 = removed, 5 = none set). False = no password set, nothing sent. */
  removeStoragePassword(currentPassword: string): boolean {
    if (!this._hasStoragePassword) return false;
    this.send(ClientPackets.RemoveStoragePassword({ CurrentPassword: currentPassword }));
    return true;
  }

  /** True = consumed (S.AllowObserve and S.AllowToggles; S.SwitchGroup goes on to the group model). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "UserInformation": // GameScene.UserInformation
        this._allowObserve = p.AllowObserve;
        this._hasExpandedStorage = p.HasExpandedStorage;
        this._hasStoragePassword = p.HasStoragePassword;
        this._requireStoragePassword = p.RequireStoragePassword;
        this._storagePasswordLastSet = p.StoragePasswordLastSet;
        this._expandedStorageExpiryTime = p.ExpandedStorageExpiryTime;
        this.changed.emit();
        return false;
      case "AllowObserve": // GameScene.ProcessPacket: AllowObserve = p.Allow
        this._allowObserve = p.Allow;
        this.setBit(AllowToggle.Observe, p.Allow);
        this.changed.emit();
        return true;
      case "SwitchGroup": // read only: the group model / windows handle it
        this.setBit(AllowToggle.Group, p.AllowGroup);
        return false;
      case "AllowToggles": // web-only; nothing else reads it
        this._togglesKnown = true;
        this._toggleFlags = p.Flags & 0xffff;
        this._allowObserve = allowToggleOn(p.Flags, AllowToggle.Observe);
        this.togglesChanged.emit();
        this.changed.emit();
        return true;
      case "ResizeStorage": // GameScene.ResizeStorage
        this._hasExpandedStorage = p.HasExpandedStorage;
        this._expandedStorageExpiryTime = p.ExpiryTime;
        this.changed.emit();
        return false;
      case "StorageUnlockResult": // StorageDialog.HandleStorageUnlockResult
        this._hasStoragePassword = p.HasPassword;
        this.changed.emit();
        return false;
      case "StoragePasswordResult": // StorageDialog.HandleStoragePasswordResult
        this._hadStoragePassword = this._hasStoragePassword;
        this._hasStoragePassword = p.HasPassword;
        this._storagePasswordLastSet = p.LastSetTime;
        this.changed.emit();
        return false;
    }
    return false;
  }
}

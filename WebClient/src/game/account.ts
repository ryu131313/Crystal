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

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { Emitter } from "./emitter.ts";

export class Account {
  /** A flag changed. */
  readonly changed = new Emitter<void>();
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

  /** C.RemoveStoragePassword: the server checks the storage NPC page / range and the password (StoragePasswordResult
   *  Result 1 = invalid, 2 = wrong, 4 = removed, 5 = none set). False = no password set, nothing sent. */
  removeStoragePassword(currentPassword: string): boolean {
    if (!this._hasStoragePassword) return false;
    this.send(ClientPackets.RemoveStoragePassword({ CurrentPassword: currentPassword }));
    return true;
  }

  /** True = consumed (S.AllowObserve only). */
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

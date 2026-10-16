// The user's friends and blacklist: FriendDialog.Friends (Client/MirScenes/Dialogs/FriendDialog.cs) + GameScene.FriendUpdate
// and the friend requests FriendDialog / MemoDialog send. game.ts feeds handle(); the UI follows `changed`.

import type { ServerPacket, ClientFriend } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { Emitter } from "./emitter.ts";

export class Friends {
  private _list: ClientFriend[] = [];
  /** GameScene.FriendUpdate: the list was replaced. */
  readonly changed = new Emitter<void>();

  constructor(private readonly send: (f: Uint8Array) => void) {}

  /** FriendDialog.Friends (both tabs: Blocked = the blacklist). */
  get list(): readonly ClientFriend[] { return this._list; }

  /** FriendDialog.Show: C.RefreshFriends (the server answers S.FriendUpdate). */
  refresh(): void {
    this.send(ClientPackets.RefreshFriends());
  }

  /** FriendDialog.AddButton's input box answer (`blocked` = the Blacklist tab). */
  add(name: string, blocked: boolean): void {
    this.send(ClientPackets.AddFriend({ Name: name, Blocked: blocked }));
  }

  /** FriendDialog.RemoveButton's Yes. */
  remove(characterIndex: number): void {
    this.send(ClientPackets.RemoveFriend({ CharacterIndex: characterIndex }));
  }

  /** MemoDialog.OKButton (the server ignores an empty memo or one over 200 characters). */
  setMemo(characterIndex: number, memo: string): void {
    this.send(ClientPackets.AddMemo({ CharacterIndex: characterIndex, Memo: memo }));
  }

  /** Server packets; true = consumed (the classic chain returned on S.FriendUpdate). */
  handle(p: ServerPacket): boolean {
    if (p.type !== "FriendUpdate") return false;
    // GameScene.FriendUpdate: replace the list
    this._list = p.Friends;
    this.changed.emit();
    return true;
  }
}

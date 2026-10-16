// The user's friends and blacklist: FriendDialog.Friends (Client/MirScenes/Dialogs/FriendDialog.cs) + GameScene.FriendUpdate
// and the friend requests FriendDialog / MemoDialog send. game.ts feeds handle(); the UI follows `changed`.
// Phase 2.7: friendships are mutual. C.AddFriend (not blocked) asks the target (S.FriendRequest, online only, kept 60 s in
// server memory); the target answers with reply() (C.FriendRequestReply) and the requester gets S.FriendRequestResult.
// The server also sends a chat line for every outcome, so a UI does not need to echo `result` into the chat.

import type { ServerPacket, ClientFriend } from "../protocol/server.ts";
import { FriendRequestResults } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { Emitter } from "./emitter.ts";

/** An incoming friend request (S.FriendRequest). `expires` is on the `now` clock (the server keeps it 60 s). */
export interface FriendRequest {
  name: string;
  /** MirClass. */
  cls: number;
  level: number;
  expires: number;
}

/** S.FriendRequestResult.Result (FriendRequestResults: 0 accepted, 1 declined, 2 expired, 3 offline, 4 list full, 5 not accepting). */
export type FriendRequestResult = (typeof FriendRequestResults)[keyof typeof FriendRequestResults];

/** How long an incoming request stays answerable locally (the server's expiry). */
export const FRIEND_REQUEST_MS = 60_000;

export class Friends {
  private _list: ClientFriend[] = [];
  private readonly _requests = new Map<string, FriendRequest>();
  /** GameScene.FriendUpdate: the list was replaced. */
  readonly changed = new Emitter<void>();
  /** S.FriendRequest: someone online asks to be the user's friend; answer with reply(). */
  readonly request = new Emitter<FriendRequest>();
  /** The pending incoming requests changed (added, answered, expired or settled by a list update). */
  readonly requestsChanged = new Emitter<void>();
  /** S.FriendRequestResult: what happened to a request the user sent (`name` = the player it went to). */
  readonly result = new Emitter<{ name: string; result: FriendRequestResult }>();

  constructor(private readonly send: (f: Uint8Array) => void, private readonly now: () => number = () => performance.now()) {}

  /** FriendDialog.Friends (both tabs: Blocked = the blacklist). */
  get list(): readonly ClientFriend[] { return this._list; }

  /** The pending incoming requests, oldest first; expired ones are dropped. */
  get requests(): readonly FriendRequest[] {
    this.prune();
    return [...this._requests.values()];
  }

  /** FriendDialog.Show: C.RefreshFriends (the server answers S.FriendUpdate). */
  refresh(): void {
    this.send(ClientPackets.RefreshFriends());
  }

  /** FriendDialog.AddButton's input box answer (`blocked` = the Blacklist tab). Not blocked = a friend request (the
   *  server answers with chat lines and S.FriendRequestResult); blocked = the instant, one-sided blacklist entry. */
  add(name: string, blocked: boolean): void {
    this.send(ClientPackets.AddFriend({ Name: name, Blocked: blocked }));
  }

  /** FriendDialog.RemoveButton's Yes (a friend: removed on both sides; a blacklist entry: one-sided). */
  remove(characterIndex: number): void {
    this.send(ClientPackets.RemoveFriend({ CharacterIndex: characterIndex }));
  }

  /** MemoDialog.OKButton (the server ignores an empty memo or one over 200 characters). */
  setMemo(characterIndex: number, memo: string): void {
    this.send(ClientPackets.AddMemo({ CharacterIndex: characterIndex, Memo: memo }));
  }

  /** Answers an incoming request (C.FriendRequestReply). The server is the judge of expiry, so the reply is sent even
   *  when the local timer already ran out (it answers "no friend request" then). */
  reply(name: string, accept: boolean): void {
    const key = name.toLowerCase();
    const r = this._requests.get(key);
    this.send(ClientPackets.FriendRequestReply({ Name: r?.name ?? name, Accept: accept }));
    if (r && this._requests.delete(key)) this.requestsChanged.emit();
  }

  /** Server packets; true = consumed (the classic chain returned on S.FriendUpdate). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "FriendUpdate": {
        // GameScene.FriendUpdate: replace the list
        this._list = p.Friends;
        // a request from someone who is now a friend was settled (both sides added each other)
        let settled = false;
        for (const f of p.Friends) if (!f.Blocked && this._requests.delete(f.Name.toLowerCase())) settled = true;
        this.changed.emit();
        if (settled) this.requestsChanged.emit();
        return true;
      }
      case "FriendRequest": {
        const r: FriendRequest = { name: p.Name, cls: p.Class, level: p.Level, expires: this.now() + FRIEND_REQUEST_MS };
        this._requests.delete(p.Name.toLowerCase()); // a repeat moves to the end with a fresh expiry
        this._requests.set(p.Name.toLowerCase(), r);
        this.request.emit(r);
        this.requestsChanged.emit();
        return true;
      }
      case "FriendRequestResult":
        this.result.emit({ name: p.Name, result: p.Result as FriendRequestResult });
        return true;
      default:
        return false;
    }
  }

  private prune(): void {
    const t = this.now();
    let removed = false;
    for (const [k, r] of this._requests) if (r.expires <= t) { this._requests.delete(k); removed = true; }
    if (removed) this.requestsChanged.emit();
  }
}

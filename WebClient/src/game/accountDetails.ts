// Account-screen details the desktop client never gets (web-only): S.LoginFailed (tries left / lock time, sent right
// after S.Login on a wrong password or unknown account) and S.SelectDetails (account gold + per-character guild, map
// and play time, sent after S.LoginSuccess, S.NewCharacterSuccess and S.LogOutSuccess). The classic login / select
// scenes keep their own messages and ignore this store; a later UI reads it.
//
// Like serverRules.ts the store lives per Session: the account screens (screens.ts) feed it, and it outlives the game
// scene that is rebuilt on every log-in over the same connection. Each packet replaces its part and fires `changed`.

import type { ClientSelectDetail, ServerPacket, PacketOf } from "../protocol/server.ts";
import type { Session } from "./session.ts";
import { Emitter } from "./emitter.ts";

export type LoginFailedInfo = Omit<PacketOf<"LoginFailed">, "type">;

/** DateTime.ToBinary ticks (100 ns since 0001-01-01; Kind bits in the top two) to Unix ms. Local-kind values are
 *  treated as UTC (the server and a lock message only need minutes). */
export function dateTimeBinaryToMs(binary: bigint): number {
  const ticks = BigInt.asUintN(64, binary) & 0x3fff_ffff_ffff_ffffn;
  return Number((ticks - 621_355_968_000_000_000n) / 10_000n);
}

export class AccountDetailsStore {
  /** "loginFailed" = a new S.LoginFailed; "select" = a new S.SelectDetails. */
  readonly changed = new Emitter<"loginFailed" | "select">();
  private _loginFailed: LoginFailedInfo | null = null;
  private _accountGold = 0;
  private readonly _characters = new Map<number, ClientSelectDetail>();

  /** The last failed login of this connection (cleared by S.LoginSuccess), or null. */
  get loginFailed(): LoginFailedInfo | null { return this._loginFailed; }
  /** Unix ms until which the account is locked, or 0 when it is not. */
  get lockedUntilMs(): number {
    const b = this._loginFailed?.LockedUntil ?? 0n;
    return b === 0n ? 0 : dateTimeBinaryToMs(b);
  }
  /** AccountInfo.Gold from the last S.SelectDetails. */
  get accountGold(): number { return this._accountGold; }
  /** The per-character details of the last S.SelectDetails, by SelectInfo.Index. */
  character(index: number): ClientSelectDetail | undefined { return this._characters.get(index); }
  get characters(): ReadonlyMap<number, ClientSelectDetail> { return this._characters; }

  /** Server packets; true = consumed (S.LoginFailed, S.SelectDetails). S.LoginSuccess is only read. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "LoginFailed": {
        const { type: _type, ...info } = p;
        this._loginFailed = info;
        this.changed.emit("loginFailed");
        return true;
      }
      case "LoginSuccess":
        this._loginFailed = null;
        return false;
      case "SelectDetails":
        this._accountGold = p.AccountGold;
        this._characters.clear();
        for (const c of p.Characters) this._characters.set(c.Index, c);
        this.changed.emit("select");
        return true;
      default:
        return false;
    }
  }
}

const stores = new WeakMap<Session, AccountDetailsStore>();

/** The account-details store for this connection (created on first use, dropped with the Session). */
export function accountDetails(session: Session): AccountDetailsStore {
  let store = stores.get(session);
  if (!store) { store = new AccountDetailsStore(); stores.set(session, store); }
  return store;
}

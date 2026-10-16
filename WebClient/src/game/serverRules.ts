// Server rules: the last S.ServerRules snapshot (refine / awakening odds, mentor / marriage / mail / territory costs,
// class gating, feature switches), so a UI shows the server's values instead of hard-coding them. The desktop client
// has no counterpart (it ignores the packet).
//
// The store lives per Session, not per game, like the rankings details cache (rankings.ts): the server sends the packet
// right after S.LoginSuccess (account stage: the select screen's class gating) and again on StartGame success, so it
// must outlive the game scene that is rebuilt on every log-in over the same connection. Screens (account stage) and
// game.ts (in game) both feed it; each packet replaces the snapshot and fires `changed`.

import type { ServerPacket, ServerRules } from "../protocol/server.ts";
import type { Session } from "./session.ts";
import { Emitter } from "./emitter.ts";

export type { ServerRules };

export class ServerRulesStore {
  /** A new S.ServerRules was applied (the whole snapshot is replaced). */
  readonly changed = new Emitter<ServerRules>();
  private _rules: ServerRules | null = null;

  /** The last snapshot, or null before the first S.ServerRules of this connection. */
  get rules(): ServerRules | null { return this._rules; }

  /** Server packets; true = consumed (S.ServerRules). */
  handle(p: ServerPacket): boolean {
    if (p.type !== "ServerRules") return false;
    const { type: _type, ...rules } = p;
    this._rules = rules;
    this.changed.emit(rules);
    return true;
  }
}

const stores = new WeakMap<Session, ServerRulesStore>();

/** The rules store for this connection (created on first use, dropped with the Session). */
export function serverRules(session: Session): ServerRulesStore {
  let store = stores.get(session);
  if (!store) { store = new ServerRulesStore(); stores.set(session, store); }
  return store;
}

/** The last S.ServerRules on this connection, or null before the first one. */
export function rulesFor(session: Session): ServerRules | null {
  return stores.get(session)?.rules ?? null;
}

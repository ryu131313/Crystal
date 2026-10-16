// Guild territories: GuildTerritoryDialog's state (GTMapList, Page, Lenght, selectedIndex; Client/MirScenes/Dialogs/
// GuildTerritoryDialog .cs) with GameScene.GuildTerritoryPage and the page buttons' C.GuildTerritoryPage. Opened by the
// server (an NPC script calls GetGuildTerritories(0)), so the UI shows its window on `received`.
// purchase() sends C.PurchaseGuildTerritory (GuildTerritoryDialog.BuyButton; the server decodes it since phase 2.1).
// action() sends C.GuildTerritoryAction (web, phase 2.5): rent / extend / list for sale / cancel the sale / teleport, with
// the same server checks as the NPC BuyGT / ExtendGT / GTSale / GTCancelSale / TeleportGT commands; replies are chat
// lines, and action() re-requests the shown page (S.GuildTerritoryPage carries no page number).

import type { ClientGTMap, ServerPacket } from "../protocol/server.ts";
import { ClientPackets, GuildTerritoryActionKinds } from "../protocol/client.ts";
import { Emitter } from "./emitter.ts";

/** GuildTerritoryDialog rows per page (the server's page size). */
export const TERRITORY_ROWS = 7;

/** C.GuildTerritoryAction kinds (GuildTerritoryActionKind: Rent 0, Extend 1, ListForSale 2, CancelSale 3, Teleport 4). */
export const TerritoryAction = GuildTerritoryActionKinds;
export type TerritoryActionKind = (typeof GuildTerritoryActionKinds)[keyof typeof GuildTerritoryActionKinds];

export class Territories {
  /** GameScene.GuildTerritoryPage applied (list and length set; the page is unchanged). */
  readonly received = new Emitter<void>();
  private _list: ClientGTMap[] = [];
  private _page = 0;
  private _length = 0;
  private _selected = -1;

  constructor(private readonly send: (f: Uint8Array) => void) {}

  /** GuildTerritoryDialog.GTMapList: the current page's territories. */
  get list(): readonly ClientGTMap[] { return this._list; }
  get page(): number { return this._page; }
  /** GuildTerritoryDialog.Lenght (the server's GTMapList.Count). */
  get length(): number { return this._length; }
  /** selectedIndex: the GT # (1-based over all pages) of the selected row; -1 = none. */
  get selected(): number { return this._selected; }

  /** GTRow.Click / GuildTerritoryDialog.Reset. */
  select(gt: number): void {
    this._selected = gt;
  }

  /** The window was opened by the server while hidden: page 0 (a web difference: the desktop keeps the old Page). */
  resetPage(): void {
    this._page = 0;
  }

  /** prevButton.Click: false at page 0; else C.GuildTerritoryPage for the previous page. */
  prev(): boolean {
    if (this._page - 1 < 0) return false;
    this._page--;
    this.send(ClientPackets.GuildTerritoryPage({ Page: this._page }));
    return true;
  }

  /** nextButton.Click: false on the last page; else C.GuildTerritoryPage for the next page. */
  next(): boolean {
    if ((this._page + 1) * TERRITORY_ROWS >= this._length) return false;
    this._page++;
    this.send(ClientPackets.GuildTerritoryPage({ Page: this._page }));
    return true;
  }

  /** GuildTerritoryDialog.BuyButton.Click: C.PurchaseGuildTerritory with the owning guild's name (the window checks the
   *  row first: selected, a leader, "For Sale"). The server answers with chat lines and a fresh page. */
  purchase(owner: string): void {
    if (owner === "") return;
    this.send(ClientPackets.PurchaseGuildTerritory({ Owner: owner }));
  }

  /** C.GuildTerritoryAction (web). `index` = ClientGTMap.index from S.GuildTerritoryPage, used by Rent only (< 0 = the
   *  first free territory, as BuyGT); the other kinds act on the guild's own territory. `price` (gold, u32) is used by
   *  ListForSale only (the server refuses above int.MaxValue, as GTSale). Rent / Extend / Teleport need the territory NPC
   *  page open on the server; ListForSale / CancelSale need the player on the guild's territory. Except for Teleport, a
   *  C.GuildTerritoryPage for the current page follows (handled after the action, so it shows the result). False = an
   *  invalid kind / index / price (nothing sent). */
  action(kind: TerritoryActionKind, index: number, price = 0): boolean {
    if (!Number.isInteger(kind) || kind < TerritoryAction.Rent || kind > TerritoryAction.Teleport) return false;
    if (!Number.isInteger(index) || index < -0x80000000 || index > 0x7fffffff) return false;
    if (!Number.isInteger(price) || price < 0 || price > 0xffffffff) return false;
    this.send(ClientPackets.GuildTerritoryAction({ Kind: kind, TerritoryIndex: index, Price: kind === TerritoryAction.ListForSale ? price : 0 }));
    if (kind !== TerritoryAction.Teleport) this.send(ClientPackets.GuildTerritoryPage({ Page: this._page }));
    return true;
  }

  /** Server packets; true = consumed (the classic chain returned on it). */
  handle(p: ServerPacket): boolean {
    if (p.type !== "GuildTerritoryPage") return false;
    // GameScene.GuildTerritoryPage
    this._list = p.Listings;
    this._length = p.length;
    this.received.emit();
    return true;
  }
}

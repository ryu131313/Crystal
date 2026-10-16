// Guild territories: GuildTerritoryDialog's state (GTMapList, Page, Lenght, selectedIndex; Client/MirScenes/Dialogs/
// GuildTerritoryDialog .cs) with GameScene.GuildTerritoryPage and the page buttons' C.GuildTerritoryPage. Opened by the
// server (an NPC script calls GetGuildTerritories(0)), so the UI shows its window on `received`.
// C.PurchaseGuildTerritory is never sent: it has no GetClientPacket case in Shared/Packet.cs (Client §3.5 Known defect).

import type { ClientGTMap, ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { Emitter } from "./emitter.ts";

/** GuildTerritoryDialog rows per page (the server's page size). */
export const TERRITORY_ROWS = 7;

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

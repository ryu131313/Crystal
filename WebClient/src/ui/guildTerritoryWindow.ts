// Guild territories: GuildTerritoryDialog + GTRow (Client/MirScenes/Dialogs/GuildTerritoryDialog .cs) with the desktop
// artwork (Prguse 680, Title 54) and GameScene.GuildTerritoryPage. Opened by the server (an NPC script calls
// GetGuildTerritories(0)), not by a key. 7 rows a page (GT #, owning guild, guild leaders, status, price), prev / next
// send C.GuildTerritoryPage, a row click selects it (lime border); Mail composes a letter to the selected owner's leader
// (MailComposeLetterDialog.ComposeMail).
// Purchase is inert: C.PurchaseGuildTerritory has no GetClientPacket case in Shared/Packet.cs, so the server would
// discard the connection's whole receive buffer (Client §3.5 Known defect); it is deliberately not in client.ts. The
// button shows like the desktop's (a selected "For Sale" row) but sends nothing.
// Deliberate differences: the page resets to 0 when the server opens the hidden window (the NPC always asks for page 0;
// the desktop keeps the old Page and mislabels the rows); the window opens centred (the desktop never sets a Location,
// so it opens at 0, 0).

import type { ClientGTMap, ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { MirWindow, MirImage, MirButton, MirLabel, MirControl, Lib } from "./mir/controls.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";

const ROWS = 7;
const NONE = "None"; // ClientTextKeys.None (the server sends "None" for an unowned territory)
const FOR_SALE = "For Sale"; // ClientTextKeys.ForSale
const GOLDENROD = "#daa520"; // Color.Goldenrod

/** GTRow: a 550 x 17 row of five labels; Border (lime) while selected. */
class GTRow {
  readonly control: MirControl;
  readonly idx: MirLabel;
  readonly guildOwner: MirLabel;
  readonly ownerName: MirLabel;
  readonly status: MirLabel;
  readonly price: MirLabel;
  owner1 = "";
  owner2 = "";

  constructor(parent: MirControl, location: [number, number], onClick: (row: GTRow) => void) {
    this.control = new MirControl({ parent, location, size: [550, 17], onClick: () => onClick(this) });
    const label = (x: number) => new MirLabel({ parent: this.control, location: [x, 0], autoSize: true, notControl: true });
    this.idx = label(15);
    this.guildOwner = label(45);
    this.ownerName = label(150);
    this.status = label(365);
    this.price = label(460);
  }

  set border(v: boolean) { this.control.el.style.outline = v ? "1px solid #00ff00" : ""; }
}

export class GuildTerritoryWindow {
  /** GuildTerritoryDialog.GTMapList. */
  private list: ClientGTMap[] = [];
  private page = 0;
  /** GuildTerritoryDialog.Lenght (the server's GTMapList.Count). */
  private length = 0;
  /** selectedIndex: the GT # of the selected row (-1 = none). */
  private selected = -1;
  private readonly send: (f: Uint8Array) => void;
  private readonly composeMail: (name: string) => void;
  private readonly win: MirWindow;
  private readonly rows: GTRow[] = [];
  private readonly buyButton: MirButton;

  constructor(_stage: HTMLElement, send: (f: Uint8Array) => void, composeMail: (name: string) => void) {
    this.send = send;
    this.composeMail = composeMail;
    // GuildTerritoryDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 680, movable: true, sort: true, location: "center" });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 361, hoverIndex: 362, pressedIndex: 363, location: [544, 8], hint: "Exit", onClick: () => this.toggle(false) });
    new MirButton({ parent: this.win, library: Lib.Prguse2, index: 240, hoverIndex: 241, pressedIndex: 242, location: [214, 213], sound: SoundList.ButtonA, hint: "Page Back", onClick: () => this.prev() });
    new MirButton({ parent: this.win, library: Lib.Prguse2, index: 243, hoverIndex: 244, pressedIndex: 245, location: [317, 213], sound: SoundList.ButtonA, hint: "Page Forward", onClick: () => this.next() });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 437, hoverIndex: 438, pressedIndex: 439, location: [262, 208], sound: SoundList.ButtonA, hint: "Mail a Guild Leader", onClick: () => this.mail() });
    // BuyButton: C.PurchaseGuildTerritory is not ported (see the header), so the click does nothing
    this.buyButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 437, hoverIndex: 438, pressedIndex: 439, location: [292, 208], sound: SoundList.ButtonA, hint: "Purchase", visible: false });
    new MirImage({ parent: this.win, library: Lib.Title, index: 54, location: [217, 11] });
    const header = (x: number, text: string) => new MirLabel({ parent: this.win, location: [x, 38], autoSize: true, notControl: true, foreColour: GOLDENROD, text });
    header(15, "GT #");
    header(60, "Owning Guild");
    header(230, "Guild Leaders");
    header(380, "GT Status");
    header(480, "GT Price");
    for (let i = 0; i < ROWS; i++) {
      const row = new GTRow(this.win, [5, 60 + 20 * i], (r) => this.rowClick(r));
      row.idx.text = String(i + 1);
      row.guildOwner.text = NONE;
      row.ownerName.text = NONE;
      row.status.text = "Available";
      row.price.text = "10,000,000";
      this.rows.push(row);
    }
    gameSignal().addEventListener("abort", () => this.win.dispose());
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** GuildTerritoryDialog.Show / Hide. */
  toggle(show = !this.win.visible): void {
    if (show) this.win.show(); else this.win.hide();
  }

  /** Server packets; returns true when handled. */
  handle(p: ServerPacket): boolean {
    if (p.type !== "GuildTerritoryPage") return false;
    // GameScene.GuildTerritoryPage
    if (!this.visible) { this.page = 0; this.reset(); this.toggle(true); }
    this.list = p.Listings;
    this.length = p.length;
    this.updateInterface();
    return true;
  }

  /** prevButton.Click. */
  private prev(): void {
    if (this.page - 1 < 0) return;
    this.page--;
    this.send(ClientPackets.GuildTerritoryPage({ Page: this.page }));
    this.reset();
  }

  /** nextButton.Click. */
  private next(): void {
    if ((this.page + 1) * ROWS >= this.length) return;
    this.page++;
    this.send(ClientPackets.GuildTerritoryPage({ Page: this.page }));
    this.reset();
  }

  /** GuildTerritoryDialog.Reset. */
  private reset(): void {
    for (const r of this.rows) r.border = false;
    this.selected = -1;
  }

  /** GTRow.Click. */
  private rowClick(row: GTRow): void {
    this.reset();
    if (row.guildOwner.text === "") return;
    this.selected = Number.parseInt(row.idx.text, 10);
    if (Number.isNaN(this.selected)) this.selected = 0; // int.TryParse
    row.border = true;
    this.buyButton.visible = row.status.text === FOR_SALE;
  }

  private selectedRow(): GTRow | undefined {
    return this.rows.find((r) => r.idx.text === String(this.selected));
  }

  /** mailButton.Click: ComposeMail(GT.Owner1) unless the leader reads None. */
  private mail(): void {
    const gt = this.selectedRow();
    if (!gt || gt.ownerName.text === NONE) return;
    this.composeMail(gt.owner1);
  }

  /** GuildTerritoryDialog.UpdateInterface. */
  private updateInterface(): void {
    for (let i = 0; i < this.rows.length; i++) {
      const row = this.rows[i]!;
      const gt = this.list[i];
      if (!gt) {
        row.idx.text = row.guildOwner.text = row.ownerName.text = row.status.text = row.price.text = "";
        continue;
      }
      row.idx.text = String(i + 1 + this.page * ROWS);
      row.guildOwner.text = gt.Owner;
      row.owner1 = gt.Leader;
      row.owner2 = gt.Leader2;
      row.ownerName.text = gt.Leader + (gt.Leader2 !== "" ? ` and ${gt.Leader2}` : ""); // AndPlaceholder
      if (gt.Owner !== NONE) row.status.text = gt.begin > 0 ? "Sale pending" : gt.price > 0 ? FOR_SALE : "Unavailable";
      else row.status.text = "Available";
      row.price.text = gt.price.toLocaleString("en-US");
    }
  }
}

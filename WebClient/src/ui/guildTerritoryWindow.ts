// Guild territories: GuildTerritoryDialog + GTRow (Client/MirScenes/Dialogs/GuildTerritoryDialog .cs, DOM edition) and
// GameScene.GuildTerritoryPage. Opened by the server (an NPC script calls GetGuildTerritories(0)), not by a key.
// 7 rows a page (GT #, owning guild, guild leaders, status, price), prev / next send C.GuildTerritoryPage, a row click
// selects it; "Mail" composes a letter to the selected owner's leader (MailComposeLetterDialog.ComposeMail).
// Purchase is shown disabled: C.PurchaseGuildTerritory has no GetClientPacket case in Shared/Packet.cs, so the server
// would discard the connection's whole receive buffer (Client §3.5 Known defect); it is deliberately not in client.ts.
// Deliberate difference: the page resets to 0 when the server opens the hidden window (the NPC always asks for page 0;
// the desktop keeps the old Page and mislabels the rows).

import type { ClientGTMap, ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { el } from "./screens.ts";

const ROWS = 7;
const NONE = "None"; // ClientTextKeys.None (the server sends "None" for an unowned territory)

export class GuildTerritoryWindow {
  /** GuildTerritoryDialog.GTMapList. */
  private list: ClientGTMap[] = [];
  private page = 0;
  /** GuildTerritoryDialog.Lenght (the server's GTMapList.Count). */
  private length = 0;
  /** selectedIndex: the GT # of the selected row (0 = none). */
  private selected = 0;
  private readonly send: (f: Uint8Array) => void;
  private readonly composeMail: (name: string) => void;
  private readonly root = el("div", { class: "window gt-window", hidden: true });
  private readonly rows = el("div", { class: "gt-rows" });
  private readonly pageLabel = el("span", { class: "magic-meta" });
  private readonly mailButton = el("button", { title: "Mail a Guild Leader", onclick: () => this.mail() }, "Mail") as HTMLButtonElement;
  private readonly buyButton = el("button", {
    title: "Purchase is unavailable in the web client: the server has no decoder for C.PurchaseGuildTerritory.", disabled: true,
  }, "Purchase") as HTMLButtonElement;

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, composeMail: (name: string) => void) {
    this.send = send;
    this.composeMail = composeMail;
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Guild Territories"), el("button", { class: "window-close", title: "Exit", onclick: () => this.toggle(false) }, "×")),
      el("div", { class: "gt-row gt-head" }, el("span", {}, "GT #"), el("span", {}, "Owning Guild"), el("span", {}, "Guild Leaders"), el("span", {}, "GT Status"), el("span", {}, "GT Price")),
      this.rows,
      el("div", { class: "row gt-foot" },
        el("button", { title: "Page back", onclick: () => this.prev() }, "◀"), this.pageLabel,
        el("button", { title: "Page forward", onclick: () => this.next() }, "▶"), this.mailButton, this.buyButton),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  /** GuildTerritoryDialog.Show / Hide. */
  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
  }

  /** Server packets; returns true when handled. */
  handle(p: ServerPacket): boolean {
    if (p.type !== "GuildTerritoryPage") return false;
    // GameScene.GuildTerritoryPage
    if (!this.visible) { this.page = 0; this.toggle(true); }
    this.list = p.Listings;
    this.length = p.length;
    this.selected = 0;
    this.render();
    return true;
  }

  /** prevButton.Click. */
  private prev(): void {
    if (this.page - 1 < 0) return;
    this.page--;
    this.send(ClientPackets.GuildTerritoryPage({ Page: this.page }));
    this.selected = 0; // Reset
    this.render();
  }

  /** nextButton.Click. */
  private next(): void {
    if ((this.page + 1) * ROWS >= this.length) return;
    this.page++;
    this.send(ClientPackets.GuildTerritoryPage({ Page: this.page }));
    this.selected = 0;
    this.render();
  }

  private selectedMap(): ClientGTMap | undefined {
    const i = this.selected - 1 - this.page * ROWS;
    return this.selected > 0 ? this.list[i] : undefined;
  }

  /** mailButton.Click: ComposeMail(GT.Owner1) unless the leader reads None. */
  private mail(): void {
    const gt = this.selectedMap();
    if (!gt || gt.Leader === NONE || gt.Leader === "") return;
    this.composeMail(gt.Leader);
  }

  /** GuildTerritoryDialog.UpdateInterface. */
  private render(): void {
    const rows: HTMLElement[] = [];
    for (let i = 0; i < ROWS && i < this.list.length; i++) {
      const gt = this.list[i]!;
      const idx = i + 1 + this.page * ROWS;
      let owners = gt.Leader;
      if (gt.Leader2 !== "") owners += ` and ${gt.Leader2}`; // AndPlaceholder
      let status: string;
      if (gt.Owner !== NONE) status = gt.begin > 0 ? "Sale pending" : gt.price > 0 ? "For Sale" : "Unavailable";
      else status = "Available";
      const row = el("div", { class: `gt-row${idx === this.selected ? " selected" : ""}` },
        el("span", {}, String(idx)), el("span", {}, gt.Owner), el("span", {}, owners), el("span", {}, status),
        el("span", {}, gt.price.toLocaleString("en-US")));
      // GTRow.Click
      row.onclick = () => { if (gt.Owner !== "") { this.selected = idx; this.render(); } };
      rows.push(row);
    }
    this.rows.replaceChildren(...rows);
    const pages = Math.max(1, Math.ceil(this.length / ROWS));
    this.pageLabel.textContent = `${this.page + 1} / ${pages}`;
    const gt = this.selectedMap();
    this.mailButton.disabled = !gt || gt.Leader === NONE || gt.Leader === "";
  }
}

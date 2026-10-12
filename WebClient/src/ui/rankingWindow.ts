// Rankings: RankingDialog (Ranking HUD button) + GameScene.Rankings. Six tabs (overall + one per class), 20 rows from
// RowOffset (prev / next / mouse wheel, re-requested 0.5 s after the last move), Online Only, the user's rank; a row
// click inspects that character (C.Inspect Ranking = true, ObjectID = (uint)PlayerId).
// The server sends each RankCharacterInfo once per connection (Envir.CheckListing), so the details are kept in
// rankingList (GameScene.RankingList) and Listings only carries ids. No key: the desktop's K opens the web magic window.

import type { ServerPacket, RankCharacterInfo } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { MirClassName } from "../protocol/enums.generated.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import type { InspectWindow } from "./inspectWindow.ts";

const ROWS = 20;
const TABS = ["Overall", "Warriors", "Wizards", "Taoists", "Assassins", "Archers"]; // RankType 0..5 (AllButton .. ArchButton)
const TAB_HINTS = ["Overall TOP 20", "TOP 20 Warriors", "TOP 20 Wizards", "TOP 20 Taoists", "TOP 20 Assasins", "TOP 20 Archers"];

export class RankingWindow {
  rankType = 0;
  private rowOffset = 0;
  rankCount = 0;
  private readonly rankList: RankCharacterInfo[][] = TABS.map(() => []);
  private readonly rank: number[] = TABS.map(() => 0);
  private onlineOnly = false;
  /** GameScene.RankingList: PlayerId -> details. */
  private readonly rankingList = new Map<bigint, RankCharacterInfo>();
  private nextRequest: ReturnType<typeof setTimeout> | null = null;
  /** This scene's scope (gameSignal() returns the next scene's after a log out). */
  private readonly signal = gameSignal();
  private readonly send: (f: Uint8Array) => void;
  private readonly me: () => string;
  private readonly inspect: InspectWindow;
  private readonly root = el("div", { class: "window ranking-window", hidden: true });
  private readonly tabButtons: HTMLButtonElement[];
  private readonly rows = el("div", { class: "ranking-rows" });
  private readonly myRank = el("span", { class: "ranking-my" });
  private readonly pageLabel = el("span", { class: "magic-meta" });
  private readonly onlineBox = el("input", { type: "checkbox" });

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, me: () => string, inspect: InspectWindow) {
    this.send = send;
    this.me = me;
    this.inspect = inspect;
    this.tabButtons = TABS.map((t, i) => el("button", { title: TAB_HINTS[i], onclick: () => this.selectRank(i) }, t));
    // OnlineOnlyButton.Click
    this.onlineBox.onchange = () => { this.onlineOnly = this.onlineBox.checked; this.rowOffset = 0; this.scheduleRequest(0); };
    this.rows.addEventListener("wheel", (e) => { e.preventDefault(); this.move(e.deltaY > 0 ? 1 : -1); }, { passive: false }); // Ranking_MouseWheel
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Ranking"), el("button", { class: "window-close", onclick: () => this.toggle(false) }, "×")),
      el("div", { class: "row ranking-tabs" }, ...this.tabButtons),
      el("div", { class: "ranking-row ranking-head" }, el("span", {}, "#"), el("span", {}, "Name"), el("span", {}, "Level"), el("span", {}, "Class")),
      this.rows,
      el("div", { class: "row ranking-foot" },
        el("button", { title: "Up", onclick: () => this.move(-1) }, "▲"), el("button", { title: "Down", onclick: () => this.move(1) }, "▼"), this.pageLabel,
        el("label", { class: "ranking-online" }, this.onlineBox, "Online Only"), this.myRank),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);
    this.signal.addEventListener("abort", () => { if (this.nextRequest) clearTimeout(this.nextRequest); });
    this.render();
  }

  /** RankingDialog.Toggle / Show (Show requests the current tab). */
  toggle(show = this.root.hidden): void {
    if (show === !this.root.hidden) return;
    this.root.hidden = !show;
    if (show) this.requestRanks(this.rankType);
  }

  /** RankingDialog.Move: one row at a time, then a request 0.5 s later (NextRequestTime). */
  private move(distance: number): void {
    if (distance > 0) { if (this.rowOffset < this.rankCount - ROWS) this.rowOffset++; }
    else if (this.rowOffset > 0) this.rowOffset--;
    this.scheduleRequest(500);
    this.renderPage();
  }

  /** RankingDialog.Process: NextRequestTime -> RequestRanks. */
  private scheduleRequest(ms: number): void {
    if (this.nextRequest) clearTimeout(this.nextRequest);
    this.nextRequest = setTimeout(() => {
      this.nextRequest = null;
      if (!this.signal.aborted) this.requestRanks(this.rankType);
    }, ms);
  }

  /** RankingDialog.RequestRanks. */
  private requestRanks(rankType: number): void {
    if (rankType > 6) return;
    this.send(ClientPackets.GetRanking({ RankType: rankType, RankIndex: this.rowOffset, OnlineOnly: this.onlineOnly }));
  }

  /** RankingDialog.SelectRank. */
  private selectRank(rankType: number): void {
    this.rankType = rankType;
    this.rows.replaceChildren();
    this.rowOffset = 0;
    this.requestRanks(rankType);
    this.render();
  }

  /** Server packets; returns true when handled. */
  handle(p: ServerPacket): boolean {
    if (p.type !== "Rankings") return false;
    // GameScene.Rankings
    for (const info of p.ListingDetails) this.rankingList.set(info.PlayerId, info);
    const listings: RankCharacterInfo[] = [];
    for (const id of p.Listings) {
      const info = this.rankingList.get(id);
      if (info) listings.push(info); // the desktop would throw on an unknown id
    }
    // RankingDialog.RecieveRanks
    if (p.RankType < this.rankList.length) {
      this.rankList[p.RankType] = listings;
      this.rank[p.RankType] = p.MyRank;
    }
    this.rankCount = p.Count;
    this.render();
    return true;
  }

  /** RankingDialog.UpdateRanks + RankingRow.Update (colours: 1 gold, 2 silver, 3 rosy brown, the user green, else white). */
  private render(): void {
    this.tabButtons.forEach((b, i) => b.classList.toggle("on", i === this.rankType));
    const list = this.rankList[this.rankType] ?? [];
    const me = this.me();
    const rows: HTMLElement[] = [];
    for (let i = 0; i < ROWS && i < this.rankCount && i < list.length; i++) {
      const listing = list[i]!;
      const rank = this.rowOffset + i + 1;
      let colour = "";
      if (rank === 1) colour = "gold";
      if (rank === 2) colour = "silver";
      if (rank === 3) colour = "rosybrown";
      else if (listing.Name === me) colour = "#3c3";
      else if (rank > 3) colour = "#fff";
      const row = el("div", { class: "ranking-row", title: "Inspect" },
        el("span", {}, String(rank)), el("span", {}, listing.Name), el("span", {}, String(listing.level)), el("span", {}, MirClassName[listing.Class] ?? ""));
      row.style.color = colour;
      // RankingRow.Inspect: InspectDialog.InspectID = (uint)Index
      row.onclick = () => this.inspect.inspect(Number(BigInt.asUintN(32, listing.PlayerId)), true);
      rows.push(row);
    }
    this.rows.replaceChildren(...rows);
    const r = this.rank[this.rankType] ?? 0;
    this.myRank.textContent = r === 0 ? "Not Listed" : `Ranked: ${r}`;
    this.renderPage();
  }

  private renderPage(): void {
    this.pageLabel.textContent = this.rankCount > 0 ? `${this.rowOffset + 1}-${Math.min(this.rowOffset + ROWS, this.rankCount)} of ${this.rankCount}` : "";
  }
}

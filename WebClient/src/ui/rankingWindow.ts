// Rankings: RankingDialog (Client/MirScenes/Dialogs/RankingDialog.cs, Title 728) + GameScene.Rankings. Six tab buttons
// (overall + one per class), 20 RankingRows from RowOffset (Prev / Next / scroll bar / mouse wheel, re-requested 0.5 s
// after the last move), the Online Only check box, the user's rank; a row click inspects that character
// (C.Inspect Ranking = true, ObjectID = (uint)PlayerId).
// The server sends each RankCharacterInfo once per connection (Envir.CheckListing), so the details are kept per Session
// (src/game/rankings.ts rankingDetails) and Listings only carries ids. Since phase 1c the lists, the user's rank and the
// requests live in the Rankings model; this window keeps the tab, the row offset, Online Only and the delayed request.
// Toggle with K (game.ts) or the Ranking button; Esc closes (GameScene Closeall).

import type { RankCharacterInfo } from "../protocol/server.ts";
import { MirClassName } from "../protocol/enums.generated.ts";
import { MirWindow, MirButton, MirCheckBox, MirControl, MirLabel, closeButton, dragY, Lib } from "./mir/controls.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import type { Rankings } from "../game/rankings.ts";
import type { Inspect } from "../game/inspect.ts";

const ROWS = 20;
// RankType 0..5 = AllButton, WarButton, WizButton, TaoButton, SinButton, ArchButton
const TAB_HINTS = ["Overall TOP 20", "TOP 20 Warriors", "TOP 20 Wizards", "TOP 20 Taoists", "TOP 20 Assasins", "TOP 20 Archers"];
// [rank type, Title index (Pressed = +1, Hover = +2), x] in the desktop's creation order
const TAB_BUTTONS: [number, number, number][] = [[0, 751, 10], [3, 760, 40], [1, 754, 60], [2, 763, 80], [4, 757, 100], [5, 766, 120]];
const PREV_Y = 100, NEXT_Y = 386; // PrevButton / NextButton Location.Y
const SCROLL_HEIGHT = NEXT_Y - PREV_Y - 32; // ScrollHeight


/** RankingDialog.RankingRow: rank / name / class / level labels; a click inspects. */
class RankingRow {
  readonly control: MirControl;
  private readonly rankLabel: MirLabel;
  private readonly nameLabel: MirLabel;
  private readonly classLabel: MirLabel;
  private readonly levelLabel: MirLabel;
  listing: RankCharacterInfo | null = null;

  constructor(parent: MirControl, location: [number, number], onClick: (row: RankingRow) => void) {
    this.control = new MirControl({ parent, location, size: [270, 15], visible: false, onClick: () => onClick(this) });
    const label = (x: number) => new MirLabel({ parent: this.control, location: [x, 0], autoSize: true, notControl: true });
    this.rankLabel = label(0);
    this.nameLabel = label(55);
    this.classLabel = label(150);
    this.levelLabel = label(220);
  }

  /** RankingRow.Clear. */
  clear(): void {
    this.control.visible = false;
    this.nameLabel.text = this.rankLabel.text = this.levelLabel.text = this.classLabel.text = "";
  }

  /** RankingRow.Update (colours: 1 Gold, 2 Silver, 3 RosyBrown, the user Green, else White; otherwise the last colour stays). */
  update(listing: RankCharacterInfo, rankIndex: number, me: string): void {
    this.listing = listing;
    this.rankLabel.text = String(rankIndex);
    this.levelLabel.text = String(listing.level);
    this.classLabel.text = MirClassName[listing.Class] ?? "";
    this.nameLabel.text = listing.Name;
    let colour = "";
    if (rankIndex === 1) colour = "#ffd700";
    if (rankIndex === 2) colour = "#c0c0c0";
    if (rankIndex === 3) colour = "#bc8f8f";
    else if (listing.Name === me) colour = "#008000";
    else if (rankIndex > 3) colour = "#ffffff";
    if (colour) for (const l of [this.rankLabel, this.nameLabel, this.levelLabel, this.classLabel]) l.foreColour = colour;
    this.control.visible = true;
  }
}

export class RankingWindow {
  rankType = 0;
  private rowOffset = 0;
  /** RankingDialog.RankCount (the model's). */
  get rankCount(): number { return this.model.count; }
  private onlineOnly = false;
  private gapPerRow = 0;
  private nextRequest: ReturnType<typeof setTimeout> | null = null;
  /** This scene's scope (gameSignal() returns the next scene's after a log out). */
  private readonly signal = gameSignal();
  private readonly model: Rankings;
  private readonly me: () => string;
  private readonly inspect: Inspect;
  private readonly win: MirWindow;
  private readonly rows: RankingRow[] = [];
  private readonly myRank: MirLabel;
  private readonly scrollBar: MirButton;

  constructor(_stage: HTMLElement, model: Rankings, me: () => string, inspect: Inspect) {
    this.model = model;
    this.me = me;
    this.inspect = inspect;
    // RankingDialog ctor
    this.win = new MirWindow({ library: Lib.Title, index: 728, movable: true, sort: true, location: "center" });
    closeButton(this.win, [300, 3], () => this.toggle(false));
    for (const [type, index, x] of TAB_BUTTONS) {
      new MirButton({ parent: this.win, library: Lib.Title, index, pressedIndex: index + 1, hoverIndex: index + 2, hint: TAB_HINTS[type], location: [x, 38],
        sound: SoundList.ButtonA, onClick: () => this.selectRank(type) });
    }
    new MirButton({ parent: this.win, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, location: [299, NEXT_Y], sound: SoundList.ButtonA, onClick: () => this.move(1) });
    new MirButton({ parent: this.win, library: Lib.Prguse2, index: 197, hoverIndex: 198, pressedIndex: 199, location: [299, PREV_Y], sound: SoundList.ButtonA, onClick: () => this.move(-1) });
    this.scrollBar = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 205, hoverIndex: 206, pressedIndex: 206, location: [299, PREV_Y + 13], sound: SoundList.ButtonA });
    // ScrollBar.OnMoving
    dragY(this.scrollBar, (y) => {
      y = Math.min(368, Math.max(110, y));
      this.scrollBar.location = [this.scrollBar.location[0], y];
      const row = Math.max(0, Math.min(this.rankCount - ROWS, this.gapPerRow > 0 ? (y - PREV_Y - 13) / this.gapPerRow : 0));
      this.setRowOffset(Math.trunc(row));
      this.scheduleRequest(500);
    });
    const online = new MirCheckBox({ parent: this.win, library: Lib.Prguse, index: 2086, unTickedIndex: 2086, tickedIndex: 2087, labelText: "Online Only",
      onChange: (checked) => { this.onlineOnly = checked; this.setRowOffset(0); this.scheduleRequest(0); } });
    this.win.ready.then((pic) => { if (pic) online.location = [190, pic.height - 20]; });
    this.myRank = new MirLabel({ parent: this.win, font: "bold 10pt Arial", foreColour: "#deb887", location: [229, 36], size: [82, 22], center: true });
    for (let i = 0; i < ROWS; i++) {
      const row = new RankingRow(this.win, [32, 98 + i * 15], (r) => { if (r.listing) this.inspect.inspect(Number(BigInt.asUintN(32, r.listing.PlayerId)), true); }); // RankingRow.Inspect
      row.control.el.addEventListener("wheel", (e) => { e.preventDefault(); this.move(e.deltaY > 0 ? 1 : -1); }, { passive: false }); // Ranking_MouseWheel
      this.rows.push(row);
    }
    window.addEventListener("keydown", (e) => {
      if (matches(e, KeybindOptions.Closeall) && this.win.visible) this.toggle(false); // GameScene Esc hides RankingDialog
    }, { signal: this.signal });
    // GameScene.Rankings -> RankingDialog.RecieveRanks: UpdateRanks, then the scroll gap
    const offChanged = model.changed.on(() => {
      this.render();
      const extraRows = model.count - ROWS;
      this.gapPerRow = extraRows > 0 ? SCROLL_HEIGHT / extraRows : 0; // the desktop divides by <= 0 here
    });
    this.signal.addEventListener("abort", () => { offChanged(); if (this.nextRequest) clearTimeout(this.nextRequest); this.win.dispose(); });
    this.render();
  }

  /** RankingDialog.Toggle / Show (Show requests the current tab) / Hide. */
  toggle(show = !this.win.visible): void {
    if (show === this.win.visible) { if (show) this.win.bringToFront(); return; }
    if (show) { this.win.show(); this.requestRanks(this.rankType); }
    else this.win.hide();
  }

  /** RankingDialog.RowOffset setter: the scroll bar follows. */
  private setRowOffset(offset: number): void {
    this.rowOffset = offset;
    this.scrollBar.location = [this.scrollBar.location[0], Math.trunc(PREV_Y + 13 + offset * this.gapPerRow)];
  }

  /** RankingDialog.Move: one row at a time, then a request 0.5 s later (NextRequestTime). */
  private move(distance: number): void {
    if (distance > 0) { if (this.rowOffset < this.rankCount - ROWS) this.setRowOffset(this.rowOffset + 1); }
    else if (this.rowOffset > 0) this.setRowOffset(this.rowOffset - 1);
    this.scheduleRequest(500);
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
    this.model.request(rankType, this.rowOffset, this.onlineOnly);
  }

  /** RankingDialog.SelectRank. */
  private selectRank(rankType: number): void {
    this.rankType = rankType;
    for (const row of this.rows) row.clear();
    this.setRowOffset(0);
    this.requestRanks(rankType);
  }

  /** RankingDialog.UpdateRanks. */
  private render(): void {
    const list = this.model.list(this.rankType);
    const me = this.me();
    for (let i = 0; i < this.rows.length; i++) {
      if (i >= this.rankCount || i >= list.length) this.rows[i]!.clear();
      else this.rows[i]!.update(list[i]!, this.rowOffset + i + 1, me);
    }
    const r = this.model.myRank(this.rankType);
    this.myRank.text = r === 0 ? "Not Listed" : `Ranked: ${r}`;
  }
}

// Trade: TradeDialog + GuestTradeDialog (Client/MirScenes/Dialogs/TradeDialogs.cs) with the desktop artwork (Prguse
// 389 / 390) + GameScene's trade handlers (TradeRequest / TradeAccept / TradeGold / TradeItem / TradeConfirm /
// TradeCancel). The user's grid, gold and lock live in Inventory (they move with the item packets); the guest side
// lives here. Request a trade with T (KeybindOptions.Trade) or the Trade button: the server pairs you with the player
// you face. The request is a MirMessageBox Yes/No, the gold a MirAmountBox, the cancel notice a MirMessageBox OK.

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { TRADE_SLOTS, type Inventory } from "../game/inventory.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";
import { MirWindow, MirButton, MirLabel, closeButton, Lib, uiSound, SCREEN_WIDTH, SCREEN_HEIGHT } from "./mir/controls.ts";
import { messageBox, amountBox } from "./messageBox.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";

/** TradeDialog / GuestTradeDialog Size. */
const DIALOG_SIZE: readonly [number, number] = [204, 152];

/** Grid[2 * x + y] Location (both dialogs). */
function cellLocation(slot: number): [number, number] {
  const x = Math.trunc(slot / 2), y = slot % 2;
  return [x * 36 + 10 + x, y * 32 + 39 + y];
}

export class TradeWindow {
  /** GuestTradeDialog.GuestName / GuestGold / GuestItems. */
  guestName = "";
  guestGold = 0;
  guestItems: (UserItem | null)[] = new Array<UserItem | null>(TRADE_SLOTS).fill(null);
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly send: (f: Uint8Array) => void;
  private readonly me: () => string;
  private readonly stage: HTMLElement;
  // TradeDialog
  private readonly win: MirWindow;
  private readonly nameLabel: MirLabel;
  private readonly goldLabel: MirLabel;
  private readonly confirmButton: MirButton;
  // GuestTradeDialog
  private readonly guestWin: MirWindow;
  private readonly guestNameLabel: MirLabel;
  private readonly guestGoldLabel: MirLabel;
  private guestCells: HTMLElement[] = [];
  private shownGuestItems: (UserItem | null)[] | null = null;

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, send: (f: Uint8Array) => void, _chat: (text: string, type: number) => void, me: () => string) {
    this.stage = stage;
    this.inv = inv;
    this.invWindow = invWindow;
    this.send = send;
    this.me = me;

    // TradeDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 389, movable: true, sort: true, size: DIALOG_SIZE,
      location: [Math.trunc(SCREEN_WIDTH / 2) - DIALOG_SIZE[0] - 10, SCREEN_HEIGHT - 350] });
    this.confirmButton = new MirButton({ parent: this.win, library: Lib.Title, index: 520, hoverIndex: 521, pressedIndex: 522, location: [135, 120], size: [48, 25], sound: SoundList.ButtonA,
      onClick: () => { inv.toggleTradeLock(); this.render(); } }); // ChangeLockState(!TradeLocked) + C.TradeConfirm
    closeButton(this.win, [DIALOG_SIZE[0] - 23, 3], () => this.cancel());
    this.nameLabel = new MirLabel({ parent: this.win, location: [20, 10], size: [150, 14], center: true, notControl: true });
    this.goldLabel = new MirLabel({ parent: this.win, location: [35, 123], size: [90, 15], center: true, onClick: () => this.addGold() });
    for (let slot = 0; slot < TRADE_SLOTS; slot++) placeCell(invWindow.boundCell("trade", slot), this.win, cellLocation(slot));

    // GuestTradeDialog ctor
    this.guestWin = new MirWindow({ library: Lib.Prguse, index: 390, movable: true, sort: true, size: DIALOG_SIZE,
      location: [Math.trunc(SCREEN_WIDTH / 2) + 10, SCREEN_HEIGHT - 350] });
    this.guestNameLabel = new MirLabel({ parent: this.guestWin, location: [0, 10], size: [204, 14], center: true, notControl: true });
    this.guestGoldLabel = new MirLabel({ parent: this.guestWin, location: [35, 123], size: [90, 15], center: true, notControl: true });

    this.win.el.addEventListener("contextmenu", (e) => e.preventDefault());
    this.guestWin.el.addEventListener("contextmenu", (e) => e.preventDefault());
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    gameSignal().addEventListener("abort", () => { this.win.dispose(); this.guestWin.dispose(); });
  }

  get open(): boolean {
    return this.win.visible;
  }

  /** KeybindOptions.Trade: ask the player you face. */
  request(): void {
    this.send(ClientPackets.TradeRequest());
  }

  /** TradeDialog.CloseButton: hide both dialogs and tell the server (it hands the items back, then S.TradeCancel). */
  private cancel(): void {
    this.win.hide();
    this.guestWin.hide();
    this.send(ClientPackets.TradeCancel());
  }

  /** TradeDialog.GoldLabel click (Sound = Gold): MirAmountBox (TradeAmount, Items 116) up to GameScene.Gold. */
  private addGold(): void {
    uiSound(SoundList.Gold);
    if (this.invWindow.pickedInventorySlot >= 0 || this.invWindow.heroPick || this.inv.gold <= 0) return; // SelectedCell == null
    amountBox("Trade Amount:", 116, this.inv.gold, (amount) => { if (amount > 0) this.inv.offerGold(amount); });
  }

  /** Server packets; returns true when handled. Inventory.handle has already updated the user's side. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "TradeRequest": {
        // GameScene.TradeRequest (MirMessageBox Yes/No)
        const answer = (yes: boolean) => this.send(ClientPackets.TradeReply({ AcceptInvite: yes }));
        messageBox(this.stage, `Player ${p.Name} has requested to trade with you.`, [{ label: "Yes", onClick: () => answer(true) }, { label: "No", onClick: () => answer(false) }]);
        break;
      }
      case "TradeAccept":
        // GameScene.TradeAccept -> TradeDialog.TradeAccept: show the inventory and both dialogs
        this.guestName = p.Name;
        // TradeDialog.TradeAccept: InventoryDialog to the top right
        this.invWindow.location = [SCREEN_WIDTH - this.invWindow.size[0], 0];
        if (!this.invWindow.visible) this.invWindow.toggle(true);
        this.win.show();
        this.guestWin.show();
        break;
      case "TradeGold": this.guestGold = p.Amount; break;
      case "TradeItem": this.guestItems = p.TradeItems.slice(0, TRADE_SLOTS); break;
      case "TradeConfirm": this.reset(); break;
      case "TradeCancel":
        if (p.Unlock) break;
        // GameScene.TradeCancel: TradeReset + MirMessageBox (DealCancelledMustFace)
        this.reset();
        messageBox(this.stage, "Deal cancelled.\nTo deal correctly you must face the other party.", [{ label: "OK" }]);
        break;
      default: return false;
    }
    this.render();
    return true;
  }

  /** TradeDialog.TradeReset -> GuestTradeDialog.TradeReset + hide both (the user's side is reset by Inventory). */
  private reset(): void {
    this.guestItems = new Array<UserItem | null>(TRADE_SLOTS).fill(null);
    this.guestName = "";
    this.guestGold = 0;
    this.win.hide();
    this.guestWin.hide();
  }

  /** TradeDialog.RefreshInterface + ChangeLockState (ConfirmButton.Index) + GuestTradeDialog.RefreshInterface. The
   *  user's cells are bound cells (refreshed by the inventory render); the guest's are read-only view cells. */
  render(): void {
    this.confirmButton.index = this.inv.tradeLocked ? 521 : 520;
    this.nameLabel.text = this.me();
    this.goldLabel.text = this.inv.tradeGold.toLocaleString("en-US");
    this.guestNameLabel.text = this.guestName;
    this.guestGoldLabel.text = this.guestGold.toLocaleString("en-US");
    if (this.shownGuestItems !== this.guestItems) {
      this.shownGuestItems = this.guestItems;
      for (const c of this.guestCells) c.remove();
      this.guestCells = Array.from({ length: TRADE_SLOTS }, (_, slot) =>
        placeCell(this.invWindow.viewCell(this.guestItems[slot] ?? null), this.guestWin, cellLocation(slot)));
    }
  }
}

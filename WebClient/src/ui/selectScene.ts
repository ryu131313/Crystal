// SelectScene (Client/MirScenes/SelectScene.cs): Prguse 65 background, Title 40, the server name, the four
// CharacterButtons (Prguse 44 empty / Title 660 + class, + 5 selected) with name / level / class labels, the animated
// ChrSel.Lib preview (+ its additive layer, index + 560), Last Online, and the Start / New / Delete / Credits / Exit
// buttons along the bottom. Handles S.NewCharacter(Success), S.DeleteCharacter(Success), S.StartGame(Banned / Delay).

import type { ServerPacket, SelectInfo } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { MirControl, MirImage, MirButton, MirLabel, Lib, SCREEN_WIDTH, SCREEN_HEIGHT } from "./mir/controls.ts";
import { MirAnimated } from "./animatedImage.ts";
import { NewCharacterDialog } from "./newCharacterDialog.ts";
import { Globals, Text, format, banText, chrSelIndex, wallMs, isMinValue, formatDateTime, liveMessageBox, type SceneHost } from "./accountCommon.ts";
import { SoundList } from "../game/audio.ts";
import { MirClassName } from "../protocol/enums.generated.ts";
import { warmCharacter, setPlayingCharacter } from "../game/mapFiles.ts";

type P<T extends ServerPacket["type"]> = Extract<ServerPacket, { type: T }>;

/** SelectScene.CharacterButton: Prguse 44 (empty) or Title 660 + class (+ 5 when selected), Name / Level / Class labels. */
class CharacterButton {
  readonly img: MirImage;
  selected = false;
  private readonly nameLabel: MirLabel;
  private readonly levelLabel: MirLabel;
  private readonly classLabel: MirLabel;

  constructor(parent: MirControl, location: [number, number], onClick: () => void) {
    this.img = new MirImage({ parent, library: Lib.Prguse, index: 44, location, onClick });
    this.img.el.dataset.sound = String(SoundList.ButtonA);
    this.nameLabel = new MirLabel({ parent: this.img, location: [107, 9], size: [170, 18], notControl: true });
    this.levelLabel = new MirLabel({ parent: this.img, location: [107, 28], size: [30, 18], notControl: true });
    this.classLabel = new MirLabel({ parent: this.img, location: [178, 28], size: [100, 18], notControl: true });
  }

  /** CharacterButton.Update. */
  update(info: SelectInfo | null): void {
    const labels = [this.nameLabel, this.levelLabel, this.classLabel];
    if (!info) {
      this.img.library = Lib.Prguse;
      this.img.index = 44;
      for (const l of labels) { l.text = ""; l.visible = false; }
      return;
    }
    this.img.library = Lib.Title;
    this.img.index = 660 + info.Class + (this.selected ? 5 : 0);
    this.nameLabel.text = info.Name;
    this.levelLabel.text = String(info.Level);
    this.classLabel.text = MirClassName[info.Class] ?? String(info.Class);
    for (const l of labels) l.visible = true;
  }
}

export class SelectScene {
  private readonly host: SceneHost;
  private readonly onStart: (c: SelectInfo) => void;
  private readonly root: MirControl;
  private readonly characters: SelectInfo[];
  private selected = 0;
  private character: NewCharacterDialog | null = null;
  private readonly startGameButton: MirButton;
  private readonly deleteCharacterButton: MirButton;
  private readonly characterDisplay: MirAnimated;
  private readonly glow: MirImage;
  private readonly characterButtons: CharacterButton[] = [];
  private readonly lastAccessLabel: MirLabel;
  private readonly lastAccessLabelLabel: MirLabel;
  private delayBox: { close(): void } | null = null;
  private delayTimer = 0;
  private readonly keys = new AbortController();

  constructor(parent: HTMLElement, host: SceneHost, characters: SelectInfo[], onStart: (c: SelectInfo) => void) {
    this.host = host;
    this.onStart = onStart;
    this.characters = characters;
    this.sortList();

    // SelectScene ctor
    this.root = new MirControl({ parent, location: [0, 0], size: [SCREEN_WIDTH, SCREEN_HEIGHT] });
    const background = new MirImage({ parent: this.root, library: Lib.Prguse, index: 65 });
    new MirLabel({ parent: background, location: [432, 60], size: [155, 17], center: true, text: Text.GameName });

    const xPoint = Math.trunc((SCREEN_WIDTH - 200) / 5);
    const bx = (i: number) => 100 + xPoint * i - Math.trunc(xPoint / 2) - 50;
    const by = SCREEN_HEIGHT - 32;
    this.startGameButton = new MirButton({ parent: background, library: Lib.Title, index: 340, hoverIndex: 341, pressedIndex: 342, location: [bx(1), by], enabled: false, onClick: () => this.startGame() });
    new MirButton({ parent: background, library: Lib.Title, index: 343, hoverIndex: 344, pressedIndex: 345, location: [bx(2), by], onClick: () => this.openNewCharacterDialog() });
    this.deleteCharacterButton = new MirButton({ parent: background, library: Lib.Title, index: 346, hoverIndex: 347, pressedIndex: 348, location: [bx(3), by], onClick: () => this.deleteCharacter() });
    new MirButton({ parent: background, library: Lib.Title, index: 349, hoverIndex: 350, pressedIndex: 351, location: [bx(4), by] }); // CreditsButton: no action on the desktop either
    // ExitGame (Program.Form.Close): the browser cannot close itself; log out and go back to the login screen
    new MirButton({ parent: background, library: Lib.Title, index: 352, hoverIndex: 353, pressedIndex: 354, location: [bx(5), by],
      onClick: () => { this.host.send(ClientPackets.LogOut()); location.reload(); } });

    this.characterDisplay = new MirAnimated({ parent: background, library: Lib.ChrSel, index: 220, location: [260, 420], useOffset: true, visible: false,
      animated: true, animationCount: 16, animationDelay: 250, fadeIn: true, fadeInDelay: 75, fadeInRate: 0.1 });
    // CharacterDisplay.AfterDraw: Libraries.ChrSel.DrawBlend(Index + 560, DisplayLocationWithoutOffSet, White, true)
    this.glow = new MirImage({ parent: background, library: Lib.ChrSel, index: 780, location: [260, 420], useOffset: true, notControl: true, visible: false });
    this.glow.el.style.mixBlendMode = "plus-lighter";
    this.characterDisplay.onFrame = (frame) => { this.glow.index = frame + 560; };
    this.characterDisplay.companion = { library: Lib.ChrSel, offset: 560 }; // the cycle waits for the glow frames too

    const ys = [194, 298, 402, 506];
    ys.forEach((y, i) => {
      this.characterButtons.push(new CharacterButton(background, [637, y], () => {
        if (this.characters.length <= i) return;
        this.selected = i;
        this.updateInterface();
      }));
    });
    // old web convenience kept: double-click a character to start
    this.characterButtons.forEach((b, i) => b.img.el.addEventListener("dblclick", () => { if (i === this.selected && this.startGameButton.enabled) this.startGame(); }));

    // LastAccessLabelLabel is a child of LastAccessLabel at (-65, 0) on the desktop; a sibling here (a label's text replaces its children)
    this.lastAccessLabel = new MirLabel({ parent: background, location: [265, 609], size: [180, 21], vCenter: true });
    this.lastAccessLabelLabel = new MirLabel({ parent: background, location: [265 - 65, 609], size: [100, 21], vCenter: true, text: Text.LastOnlineTitle });

    new MirImage({ parent: this.root, library: Lib.Title, index: 40, location: [468, 20] });

    // SelectScene_KeyPress: Enter starts the game
    window.addEventListener("keydown", (e) => {
      if (e.key !== "Enter" || e.defaultPrevented || document.activeElement instanceof HTMLInputElement) return;
      if (this.character && this.character.win.visible) return;
      if (this.startGameButton.enabled) this.startGame();
      e.preventDefault();
    }, { signal: this.keys.signal });

    this.updateInterface();
  }

  dispose(): void {
    this.keys.abort();
    clearInterval(this.delayTimer);
    this.delayBox?.close();
    this.character?.dispose();
    this.characterDisplay.dispose();
    this.root.dispose();
  }

  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "NewCharacter": this.newCharacter(p); return true;
      case "NewCharacterSuccess": this.newCharacterSuccess(p); return true;
      case "DeleteCharacter": this.deleteCharacterResult(p); return true;
      case "DeleteCharacterSuccess": this.deleteCharacterSuccess(p); return true;
      case "StartGame": this.startGameResult(p); return true;
      case "StartGameBanned": this.startGameButton.enabled = true; this.host.box(banText(p.Reason, p.ExpiryDate)); return true;
      case "StartGameDelay": this.startGameDelay(Number(p.Milliseconds)); return true;
      default: return false;
    }
  }

  /** SortList: most recently played first. */
  private sortList(): void {
    this.characters.sort((a, b) => wallMs(b.LastAccess) - wallMs(a.LastAccess));
  }

  private openNewCharacterDialog(): void {
    if (!this.character || this.character.isDisposed) {
      const dialog = new NewCharacterDialog(this.root.el);
      dialog.onCreate = () => this.host.send(ClientPackets.NewCharacter({ Name: dialog.nameTextBox.text, Class: dialog.cls, Gender: dialog.gender }));
      this.character = dialog;
    }
    this.character.show();
  }

  /** StartGame(): C.StartGame for the selected character (Libraries.Loaded has no web equivalent: pictures stream in). */
  private startGame(): void {
    const c = this.characters[this.selected];
    if (!c) return;
    this.startGameButton.enabled = false;
    this.host.send(ClientPackets.StartGame({ CharacterIndex: c.Index }));
  }

  private newCharacter(p: P<"NewCharacter">): void {
    const dialog = this.character;
    if (dialog && !dialog.isDisposed) dialog.okButton.enabled = true;
    switch (p.Result) {
      case 0: this.host.box(Text.CreatingNewCharactersDisabled); dialog?.dispose(); break;
      case 1: this.host.box(Text.YourCharacterNameNotAcceptable); dialog?.nameTextBox.focus(); break;
      case 2: this.host.box(Text.GenderNotExistContactGM); break;
      case 3: this.host.box(Text.ClassNotExistContactGM); break;
      case 4: this.host.box(format(Text.YouCannotMakeMoreCharacters, Globals.MaxCharacterCount)); dialog?.dispose(); break;
      case 5: this.host.box(Text.CharacterNameExists); dialog?.nameTextBox.focus(); break;
      default: this.host.box(`Character creation failed (${p.Result}).`); break;
    }
  }

  private newCharacterSuccess(p: P<"NewCharacterSuccess">): void {
    this.character?.dispose();
    this.host.box(Text.YourCharacterCreatedSuccessfully);
    this.characters.unshift(p.CharInfo);
    this.selected = 0;
    this.updateInterface();
  }

  /** DeleteCharacter(): Yes/No, then the name in a MirInputBox; a mismatch is "Incorrect Entry.". */
  private deleteCharacter(): void {
    const c = this.characters[this.selected];
    if (!c) return;
    const index = c.Index;
    this.host.box(format(Text.ConfirmDeleteCharacter, c.Name), [{ label: "Yes", onClick: () => {
      this.host.input(Text.PleaseEnterCharacterName, (name) => {
        if (name === this.characters[this.selected]?.Name) {
          this.deleteCharacterButton.enabled = false;
          this.host.send(ClientPackets.DeleteCharacter({ CharacterIndex: index }));
        } else this.host.box(Text.IncorrectEntry);
      });
    } }, { label: "No" }]);
  }

  private deleteCharacterResult(p: P<"DeleteCharacter">): void {
    this.deleteCharacterButton.enabled = true;
    if (p.Result === 0) this.host.box(Text.DeletingCharactersDisabled);
    else if (p.Result === 1) this.host.box(Text.SelectedCharacterNotExist);
  }

  private deleteCharacterSuccess(p: P<"DeleteCharacterSuccess">): void {
    this.deleteCharacterButton.enabled = true;
    this.host.box(Text.YourCharacterDeletedSuccessfully);
    const i = this.characters.findIndex((c) => c.Index === p.CharacterIndex);
    if (i >= 0) this.characters.splice(i, 1);
    this.updateInterface();
  }

  /** StartGame(S.StartGameDelay): a box counting down; when it runs out the box closes and StartGame() is sent again. */
  private startGameDelay(ms: number): void {
    this.startGameButton.enabled = true;
    const until = performance.now() + ms;
    const text = () => format(Text.CannotLoginCharacterSeconds, Math.max(0, Math.ceil((until - performance.now()) / 1000)));
    clearInterval(this.delayTimer);
    this.delayBox?.close();
    const box = liveMessageBox(text(), () => { clearInterval(this.delayTimer); this.delayBox = null; });
    this.delayBox = box;
    this.host.track(box.close);
    this.delayTimer = window.setInterval(() => {
      if (performance.now() <= until) { box.setText(text()); return; }
      clearInterval(this.delayTimer);
      box.close();
      this.delayBox = null;
      this.startGame();
    }, 100);
  }

  private startGameResult(p: P<"StartGame">): void {
    this.startGameButton.enabled = true;
    switch (p.Result) {
      case 0: this.host.box(Text.StartingGameDisabled); break;
      case 1: this.host.box(Text.YouNotLoggedIn); break;
      case 2: this.host.box(Text.YourCharacterNotFound); break;
      case 3: this.host.box(Text.NoActiveMapOrStartPointFound); break;
      case 4: {
        const c = this.characters[this.selected];
        if (c) setPlayingCharacter(c.Name);
        if (c) this.onStart(c);
        break;
      }
      default: this.host.box(`Start failed (${p.Result}).`); break;
    }
  }

  /** UpdateInterface. */
  private updateInterface(): void {
    this.characterButtons.forEach((b, i) => {
      b.selected = i === this.selected;
      b.update(this.characters[i] ?? null);
    });
    const c = this.characters[this.selected];
    if (c) {
      warmCharacter(c.Name);
      this.characterDisplay.visible = true;
      this.glow.visible = true;
      this.characterDisplay.index = chrSelIndex(c.Class, c.Gender);
      this.glow.index = this.characterDisplay.frame + 560;
      this.lastAccessLabel.text = isMinValue(c.LastAccess) ? Text.Never : formatDateTime(wallMs(c.LastAccess));
      this.lastAccessLabel.visible = this.lastAccessLabelLabel.visible = true;
      this.startGameButton.enabled = true;
    } else {
      this.characterDisplay.visible = this.glow.visible = false;
      this.lastAccessLabel.visible = this.lastAccessLabelLabel.visible = false;
      this.startGameButton.enabled = false;
    }
  }
}

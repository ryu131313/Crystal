// The game menu: port of MenuDialog (Client/MirScenes/Dialogs/MainDialogs.cs), opened by MainDialog.MenuButton. Title 567
// strip above the bottom bar's right end, Movable + Sort, with Exit / Log Out (Title 633 / 636) and the Prguse / Prguse2
// window buttons (Help, Keyboard, Ranking, Creatures, Mount, Fishing, Friends, Mentor, Relationship, Groups, Guild).
// The buttons keep MirButton's default sound (ButtonB) like the desktop. CraftingButton is hidden on the desktop too.

import { MirWindow, MirButton, Lib } from "./mir/controls.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { gameSignal } from "../game/scope.ts";
import { keyHint } from "./mainDialog.ts";

export interface MenuActions {
  exit(): void; logOut(): void; help(): void; keyboard(): void; ranking(): void; creatures(): void; mount(): void;
  fishing(): void; friends(): void; mentor(): void; relationship(): void; group(): void; guild(): void;
}

export class MenuDialog {
  readonly win: MirWindow;

  /** `mainY`: MainDialog.Location.Y. */
  constructor(screenW: number, mainY: number, act: MenuActions) {
    // MenuDialog ctor: Location = (ScreenWidth - Size.Width, MainDialog.Location.Y - Size.Height + 15); Title 567 is 36 x 282
    this.win = new MirWindow({ library: Lib.Title, index: 567, location: [screenW - 36, mainY - 282 + 15], movable: true, sort: true });
    const b = (library: string, index: number, y: number, hint: string, onClick: () => void, visible = true) =>
      new MirButton({ parent: this.win, library, index, hoverIndex: index + 1, pressedIndex: index + 2, location: [3, y], hint, onClick, visible });
    b(Lib.Title, 633, 12, keyHint("Exit ({0})", KeybindOptions.Exit), act.exit);
    b(Lib.Title, 636, 31, keyHint("Log Out ({0})", KeybindOptions.Logout), act.logOut);
    b(Lib.Prguse, 1970, 50, keyHint("Help ({0})", KeybindOptions.Help), act.help);
    b(Lib.Prguse, 1973, 69, keyHint("Keyboard ({0})", KeybindOptions.Keybind), act.keyboard);
    b(Lib.Prguse, 2000, 88, keyHint("Ranking ({0})", KeybindOptions.Ranking), act.ranking);
    b(Lib.Prguse, 2000, 107, "", () => {}, false); // CraftingButton (Visible = false)
    b(Lib.Prguse2, 431, 126, keyHint("Creatures ({0})", KeybindOptions.Creature), act.creatures);
    b(Lib.Prguse, 1976, 145, keyHint("Mount ({0})", KeybindOptions.MountWindow), act.mount);
    b(Lib.Prguse, 1979, 164, keyHint("Fishing ({0})", KeybindOptions.Fishing), act.fishing);
    b(Lib.Prguse, 1982, 183, keyHint("Friends ({0})", KeybindOptions.Friends), act.friends);
    b(Lib.Prguse, 1985, 202, keyHint("Mentor ({0})", KeybindOptions.Mentor), act.mentor);
    b(Lib.Prguse, 1988, 221, keyHint("Relationship ({0})", KeybindOptions.Relationship), act.relationship);
    b(Lib.Prguse, 1991, 240, keyHint("Groups ({0})", KeybindOptions.Group), act.group);
    b(Lib.Prguse, 1994, 259, keyHint("Guild ({0})", KeybindOptions.Guilds), act.guild);
    // GameScene: Escape (KeybindOptions.Closeall) hides the dialogs
    window.addEventListener("keydown", (e) => { if (matches(e, KeybindOptions.Closeall)) this.win.hide(); }, { signal: gameSignal() });
    gameSignal().addEventListener("abort", () => this.win.dispose());
  }

  toggle(): void { this.win.toggle(); }
}

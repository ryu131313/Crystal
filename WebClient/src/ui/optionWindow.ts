// Options: port of OptionDialog (Client/MirScenes/Dialogs/MainDialogs.cs) with the desktop artwork (Title 411, the
// Prguse2 450-467 / Title 848-853 On / Off pairs, the Prguse2 468 volume bars with the Prguse 20 knobs). Toggle with
// F12 / O (KeybindOptions.Options / Options2) or the Options button.
// Web extra (no desktop control): a "More" panel beside the dialog for the [Game] / [Graphics] values the desktop only
// changes elsewhere (chat commands @LEVELEFFECT / @TARGETDEAD / @LIGHT, ChatOptionDialog transparency, BuffDialog,
// Mir2Config.ini), drawn with the same Prguse2 On / Off buttons, so they stay reachable without an ini file.
// Values go through settings.ts setSetting (localStorage); volumes go to SoundManager (audio.ts persists them).
// Allow observe sends `C.Chat "@ALLOWOBSERVE"` like OptionDialog.ToggleObserve; the state (UserInformation.AllowObserve,
// S.AllowObserve) is in the Account model (src/game/account.ts).

import { KeybindOptions, matches } from "../game/keybinds.ts";
import type { Account } from "../game/account.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { SoundList, type SoundManager } from "../game/audio.ts";
import { gameSettings, setSetting, onSettingChange, type GameSettings } from "../game/settings.ts";
import { gameSignal } from "../game/scope.ts";
import { MirWindow, MirControl, MirImage, MirButton, MirLabel, closeButton, setPicture, libSize, Lib } from "./mir/controls.ts";
import { el } from "./screens.ts";

type BoolKey = { [K in keyof GameSettings]: GameSettings[K] extends boolean ? K : never }[keyof GameSettings];

/** One OptionDialog On / Off button pair: the indices OptionPanel_BeforeDraw swaps in. */
interface Pair {
  on: MirButton; off: MirButton; get: () => boolean;
  /** [On, Off] Index when the value is true, then when it is false. */
  whenOn: [number, number]; whenOff: [number, number];
}

/** A row of the web-only "More" panel. */
interface MoreRow { label: string; hint?: string; get: () => boolean; set: (v: boolean) => void }

export class OptionWindow {
  private readonly win: MirWindow;
  private readonly pairs: Pair[] = [];
  private readonly bars: (() => void)[] = [];
  private readonly more: MirControl;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly sound: Pick<SoundManager, "volume" | "musicVolume">;
  private readonly account: Account;

  constructor(_stage: HTMLElement, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, sound: Pick<SoundManager, "volume" | "musicVolume">,
    account: Account) {
    this.send = send;
    this.chat = chat;
    this.sound = sound;
    this.account = account;
    const hint = (text: string) => this.chat(text, ChatType.Hint);

    // OptionDialog ctor
    const win = this.win = new MirWindow({ library: Lib.Title, index: 411, movable: true, sort: true, location: "center", onShow: () => this.refresh() });
    const close = closeButton(win, [0, 5], () => this.toggle(false));
    void win.ready.then(() => { close.location = [win.size[0] - 26, 5]; });

    const pair = (library: string, y: number, pressed: [number, number], whenOn: [number, number], whenOff: [number, number],
      get: () => boolean, onOn: () => void, onOff: () => void) => {
      const on = new MirButton({ parent: win, library, location: [159, y], size: [36, 17], pressedIndex: pressed[0], sound: SoundList.ButtonA, onClick: () => { onOn(); this.refresh(); } });
      const off = new MirButton({ parent: win, library, location: [201, y], size: [36, 17], pressedIndex: pressed[1], sound: SoundList.ButtonA, onClick: () => { onOff(); this.refresh(); } });
      this.pairs.push({ on, off, get, whenOn, whenOff });
    };
    const flag = (key: BoolKey, y: number, after?: (v: boolean) => void) =>
      pair(Lib.Prguse2, y, [457, 460], [458, 459], [456, 461], () => gameSettings[key],
        () => { setSetting(key, true); after?.(true); }, () => { setSetting(key, false); after?.(false); });

    // SkillModeOn ("~", Settings.SkillMode = true) / SkillModeOff ("Ctrl"): GameScene.ChangeSkillMode
    const skillMode = (v: boolean) => { setSetting("SkillMode", v); hint(v ? "[Skill Mode: ~]" : "[Skill Mode: Ctrl]"); }; // SkillModeTilde / SkillModeCtrl
    pair(Lib.Prguse2, 68, [451, 454], [452, 453], [450, 455], () => gameSettings.SkillMode, () => skillMode(true), () => skillMode(false));
    flag("SkillBar", 93);
    flag("Effect", 118);
    flag("DropView", 143);
    flag("NameView", 168);
    pair(Lib.Prguse2, 193, [463, 466], [464, 465], [462, 467], () => gameSettings.HPView,
      () => { setSetting("HPView", true); hint("[HP/MP Mode 1]"); }, () => { setSetting("HPView", false); hint("[HP/MP Mode 2]"); }); // HpMpMode1 / 2
    // SoundBar / VolumeBar, MusicSoundBar / MusicVolumeBar
    this.volumeBar(225, 218, () => this.sound.volume, (v) => { this.sound.volume = v; });
    this.volumeBar(251, 244, () => this.sound.musicVolume, (v) => { this.sound.musicVolume = v; });
    pair(Lib.Title, 296, [853, 850], [853, 848], [851, 850], () => gameSettings.NewMove,
      () => { setSetting("NewMove", true); hint("[New Movement Style]"); }, () => { setSetting("NewMove", false); hint("[Old Movement Style]"); }); // NewMovementStyle / OldMovementStyle
    // ObserveOn / ObserveOff: OptionDialog.ToggleObserve (the server flips the flag and answers with S.AllowObserve)
    const observe = (allow: boolean) => { if (this.allowObserve !== allow) this.send(ClientPackets.Chat({ Message: "@ALLOWOBSERVE" })); };
    pair(Lib.Prguse2, 271, [457, 460], [458, 459], [456, 461], () => this.allowObserve, () => observe(true), () => observe(false));

    // Web-only "More" panel (see the header)
    const moreRows: MoreRow[] = [
      { label: "Level effects", hint: "Desktop: @LEVELEFFECT", ...this.boolRow("LevelEffect") },
      { label: "Transparent chat", hint: "Desktop: chat options", ...this.boolRow("TransparentChat") },
      { label: "Mode view", ...this.boolRow("ModeView") },
      { label: "Durability view", ...this.boolRow("DuraView") },
      { label: "Display damage", ...this.boolRow("DisplayDamage") },
      { label: "Target dead", hint: "Desktop: @TARGETDEAD", ...this.boolRow("TargetDead") },
      { label: "Highlight target", ...this.boolRow("HighlightTarget") },
      { label: "Body names", hint: "Show dead monsters' names under the mouse", ...this.boolRow("DisplayBodyName") },
      { label: "Smooth mode", hint: "Players and monsters glide between steps", ...this.boolRow("SmoothMove") },
      { label: "Smooth shadows", hint: "Solid shadows instead of the checkerboard dither", ...this.boolRow("SmoothShadows") },
      // ChatDialog.TryLightingCommand (@LIGHT NEW / OLD)
      { label: "Enhanced lighting", hint: "Desktop: @LIGHT NEW / OLD", get: () => gameSettings.Lighting === "Enhanced",
        set: (v) => { setSetting("Lighting", v ? "Enhanced" : "Classic"); hint(v ? "[Lighting: Enhanced]" : "[Lighting: Classic]"); } },
      { label: "Atmosphere effects", hint: "Glow, spell flashes, lit embers and heat haze (Enhanced lighting only)", ...this.boolRow("Atmosphere") },
      { label: "Cozy mode", hint: "Slightly richer colour and contrast", ...this.boolRow("CozyMode") },
    ];
    this.more = new MirControl({ parent: win, size: [180, 12 + moreRows.length * 25], backColour: "rgba(0, 0, 0, 0.8)", border: true, borderColour: "rgb(148, 146, 148)", visible: false });
    moreRows.forEach((r, i) => {
      const y = 8 + i * 25;
      new MirLabel({ parent: this.more, location: [10, y + 2], autoSize: true, text: r.label, hint: r.hint });
      const on = new MirButton({ parent: this.more, library: Lib.Prguse2, location: [100, y], size: [36, 17], pressedIndex: 457, sound: SoundList.ButtonA, onClick: () => { r.set(true); this.refresh(); } });
      const off = new MirButton({ parent: this.more, library: Lib.Prguse2, location: [142, y], size: [36, 17], pressedIndex: 460, sound: SoundList.ButtonA, onClick: () => { r.set(false); this.refresh(); } });
      this.pairs.push({ on, off, get: r.get, whenOn: [458, 459], whenOff: [456, 461] });
    });
    const moreButton = new MirButton({ parent: win, location: [0, 0], size: [40, 16], drawImage: false, className: "mir-text-button", text: "More", sound: SoundList.ButtonA, hint: "More options",
      onClick: () => { this.more.visible = !this.more.visible; } });
    void win.ready.then(() => {
      const [w, h] = win.size;
      moreButton.location = [w - 52, h - 24];
      this.more.location = [w + 2, 0];
    });

    const signal = gameSignal();
    const off = onSettingChange(() => this.refresh());
    const offAccount = account.changed.on(() => this.refresh()); // UserInformation / S.AllowObserve
    signal.addEventListener("abort", () => { off(); offAccount(); win.dispose(); });
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement || a instanceof HTMLSelectElement) return;
      if (matches(e, KeybindOptions.Options) || matches(e, KeybindOptions.Options2)) { e.preventDefault(); this.toggle(); } // F12 / O
      else if (matches(e, KeybindOptions.Closeall) && this.win.visible) this.toggle(false);
    }, { signal });
    this.refresh();
  }

  get visible(): boolean {
    return this.win.visible;
  }

  toggle(show = !this.win.visible): void {
    if (show) this.win.show(); else this.win.hide();
  }

  /** GameScene.AllowObserve (Account). */
  get allowObserve(): boolean {
    return this.account.allowObserve;
  }

  private boolRow(key: BoolKey): Pick<MoreRow, "get" | "set"> {
    return { get: () => gameSettings[key], set: (v) => setSetting(key, v) };
  }

  /** OptionPanel_BeforeDraw (+ the volume bars' BeforeDraw). */
  private refresh(): void {
    for (const p of this.pairs) {
      const [on, off] = p.get() ? p.whenOn : p.whenOff;
      p.on.index = on;
      p.off.index = off;
    }
    for (const b of this.bars) b();
  }

  /** SoundBar / MusicSoundBar (Prguse2 468, DrawImage = false, drawn cut to the volume in BeforeDraw) at (159, barY) and the
   *  VolumeBar knob (Prguse 20, NotControl) at (159 + filled width, knobY). SoundBar_MouseMove: a left press / drag sets
   *  the volume to p.X / Width * 100; the hint shows "N%". */
  private volumeBar(barY: number, knobY: number, get: () => number, set: (v: number) => void): void {
    const bar = new MirControl({ parent: this.win, location: [159, barY] });
    const clip = document.createElement("div"); // the drawn section: Rectangle((Width - 2) * percent, Height)
    clip.style.cssText = "position:absolute;left:0;top:0;height:100%;overflow:hidden;pointer-events:none";
    const pic = document.createElement("div");
    pic.className = "mir-pic";
    clip.append(pic);
    bar.el.append(clip);
    void setPicture(pic, Lib.Prguse2, 468);
    void libSize(Lib.Prguse2, 468).then((s) => { bar.size = s; draw(); });
    const knob = new MirImage({ parent: this.win, library: Lib.Prguse, index: 20, location: [155, knobY], notControl: true });

    const draw = () => {
      const volume = Math.round(get());
      bar.hint = `${volume}%`;
      const percent = Math.min(1, volume / 100);
      const width = percent > 0 ? Math.floor((bar.size[0] - 2) * percent) : 0;
      clip.style.width = width + "px";
      knob.location = [159 + width, knobY];
    };
    const fromMouse = (e: PointerEvent) => {
      const r = bar.el.getBoundingClientRect();
      const w = bar.size[0];
      if (!w || !r.width) return;
      const x = (e.clientX - r.left) / (r.width / w);
      set(Math.max(0, Math.min(100, Math.floor((x / w) * 100))));
      draw();
    };
    bar.el.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      bar.el.setPointerCapture(e.pointerId);
      fromMouse(e);
    });
    bar.el.addEventListener("pointermove", (e) => { if (bar.el.hasPointerCapture(e.pointerId)) fromMouse(e); });
    this.bars.push(draw);
  }
}

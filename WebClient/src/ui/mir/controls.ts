// Desktop-look UI controls as DOM: port of the Client/MirControls basics (MirControl, MirImageControl, MirButton,
// MirCheckBox, MirLabel, MirTextBox) so a web window can be laid out exactly like its desktop dialog: same .Lib
// artwork (Library + Index), same pixel Location / Size on the 1024 x 768 stage, same hover / pressed / disabled
// indices, same Arial 8pt outlined labels, same Movable / Sort windows and yellow hints.
//
// Usage (mirrors a desktop constructor):
//   const win = new MirWindow({ library: Lib.Prguse, index: 120, movable: true, sort: true, location: "center" });
//   new MirImage({ parent: win, library: Lib.Title, index: 5, location: [18, 8] });
//   new MirButton({ parent: win, library: Lib.Prguse2, index: 360, hoverIndex: 361, pressedIndex: 362,
//                   location: [206, 3], sound: SoundList.ButtonA, onClick: () => win.hide() });
//   const name = new MirLabel({ parent: win, location: [16, 33], autoSize: true, text: "..." });
//
// Pictures load asynchronously (one Range GET per image, see crystalLib.ts); a control draws nothing until its picture
// is there, like a texture still streaming in. AutoSize controls take the picture's size when it arrives.

import { libAsync } from "../../game/libraries.ts";
import { SoundList } from "../../game/audio.ts";

/** Libraries (Client/MirGraphics/MLibrary.cs) used by the interface, as /data/ paths. */
export const Lib = {
  ChrSel: "ChrSel", Prguse: "Prguse", Prguse2: "Prguse2", Prguse3: "Prguse3", BuffIcon: "BuffIcon", Help: "Help",
  MiniMap: "MMap", MapLinkIcon: "MapLinkIcon", Title: "Title", MagIcon: "MagIcon", MagIcon2: "MagIcon2",
  Items: "Items", StateItems: "StateItem", FloorItems: "DNItems", UI_32bit: "UI_32bit", GuildSkill: "GuildSkill",
} as const;

/** Settings.ScreenWidth / ScreenHeight: 1024 x 768, or the game resolution (1280 x 800, 1366 x 768, 1920 x 1080) in game (live
 *  bindings: always read them, never copy them at module load). */
export let SCREEN_WIDTH = 1024;
export let SCREEN_HEIGHT = 768;
export function setScreenSize(width: number, height: number): void {
  SCREEN_WIDTH = width;
  SCREEN_HEIGHT = height;
  window.dispatchEvent(new Event("resize")); // the page re-fits the stage
}

/** Settings.FontName 8F (MirLabel default) and 10F (MirTextBox default) as CSS fonts. */
export const FONT_8 = "8pt Arial";
export const FONT_9 = "9pt Arial";
export const FONT_10 = "10pt Arial";

export type Point = readonly [number, number];
export type Size = readonly [number, number];

// — Pictures —

export interface LibPicture { url: string; width: number; height: number; x: number; y: number }

const pictures = new Map<string, Promise<LibPicture | null>>();
const loaded = new Map<string, LibPicture | null>();

/** One .Lib image as an object URL (cached for the page's lifetime). null = no such image (or a 0 x 0 one). */
export function libPicture(library: string, index: number): Promise<LibPicture | null> {
  const key = library + "#" + index;
  let p = pictures.get(key);
  if (!p) {
    p = (async () => {
      if (index < 0) return null;
      const lib = await libAsync(library);
      const img = lib && (await lib.image(index));
      if (!img || !img.width || !img.height) return null;
      const canvas = document.createElement("canvas");
      canvas.width = img.width;
      canvas.height = img.height;
      canvas.getContext("2d")!.putImageData(new ImageData(new Uint8ClampedArray(img.rgba), img.width, img.height), 0, 0);
      const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve));
      if (!blob) return null;
      return { url: URL.createObjectURL(blob), width: img.width, height: img.height, x: img.x, y: img.y };
    })().catch(() => null);
    p.then((pic) => loaded.set(key, pic));
    pictures.set(key, p);
  }
  return p;
}

/** The picture if it has already loaded: undefined = still loading (or never asked for). */
export function libPictureNow(library: string, index: number): LibPicture | null | undefined {
  return loaded.get(library + "#" + index);
}

/** Library.GetTrueSize once loaded ([0, 0] while loading or missing). */
export async function libSize(library: string, index: number): Promise<Size> {
  const pic = await libPicture(library, index);
  return pic ? [pic.width, pic.height] : [0, 0];
}

/** Draws picture `index` of `library` into `target` (an absolutely positioned child), replacing what was there. Used
 *  by code that draws pictures outside the control classes (item icons in cells, the HUD bars). */
export function setPicture(target: HTMLElement, library: string | null, index: number, useOffset = false): Promise<LibPicture | null> {
  target.dataset.pic = library === null ? "" : library + "#" + index;
  if (library === null || index < 0) {
    target.style.backgroundImage = "";
    target.style.width = target.style.height = "0px";
    return Promise.resolve(null);
  }
  const want = target.dataset.pic;
  const apply = (pic: LibPicture | null) => {
    if (target.dataset.pic !== want) return; // superseded while loading
    target.style.backgroundImage = pic ? `url("${pic.url}")` : "";
    target.style.width = (pic?.width ?? 0) + "px";
    target.style.height = (pic?.height ?? 0) + "px";
    target.style.left = (useOffset && pic ? pic.x : 0) + "px";
    target.style.top = (useOffset && pic ? pic.y : 0) + "px";
  };
  const now = libPictureNow(library, index);
  if (now !== undefined) { apply(now); return Promise.resolve(now); }
  return libPicture(library, index).then((pic) => { apply(pic); return pic; });
}

// — Root, z-order, hint —

let uiRoot: HTMLElement | null = null;
let zTop = 10;
let playSound: ((id: number) => void) | null = null;

/** The stage element windows attach to (the 1024 x 768 #stage). Set once by the game / the account screens. */
export function setUiRoot(root: HTMLElement): void { uiRoot = root; }
export function getUiRoot(): HTMLElement {
  if (!uiRoot) uiRoot = document.getElementById("stage") ?? document.body;
  return uiRoot;
}
/** The SoundManager hook for MirButton.Sound (set by the game; the account screens have none). */
export function setUiSound(play: ((id: number) => void) | null): void { playSound = play; }
export function uiSound(id: number): void { playSound?.(id); }

let hintBox: HTMLDivElement | null = null;

/** CMain.CreateHint: black 50 % box, (144,144,0) border, yellow Arial 8pt text, at the mouse + 32 px down. */
export function showHint(text: string, clientX: number, clientY: number): void {
  const root = getUiRoot();
  if (!hintBox || !hintBox.isConnected) {
    hintBox = document.createElement("div");
    hintBox.className = "mir-hint";
    root.append(hintBox);
  }
  hintBox.textContent = text;
  hintBox.hidden = false;
  const r = root.getBoundingClientRect();
  const sx = r.width / root.offsetWidth || 1;
  let x = (clientX - r.left) / sx;
  let y = (clientY - r.top) / sx + 32;
  const w = hintBox.offsetWidth, h = hintBox.offsetHeight;
  if (x + w > root.offsetWidth) x = root.offsetWidth - w;
  if (y + h > root.offsetHeight) y = (clientY - r.top) / sx - h;
  hintBox.style.left = Math.max(0, x) + "px";
  hintBox.style.top = Math.max(0, y) + "px";
  hintBox.parentElement?.append(hintBox); // keep on top
}
export function hideHint(): void { if (hintBox) hintBox.hidden = true; }

// — Controls —

export interface ControlOptions {
  parent?: MirControl | HTMLElement;
  location?: Point;
  size?: Size;
  visible?: boolean;
  /** NotControl: never takes the mouse (clicks go to the control below). */
  notControl?: boolean;
  hint?: string;
  /** BackColour as a CSS colour (MirControl draws it under the image). */
  backColour?: string;
  border?: boolean;
  borderColour?: string;
  opacity?: number;
  className?: string;
  onClick?: (e: MouseEvent) => void;
}

/** MirControl: an absolutely positioned box at Location with Size, child of a parent control. */
export class MirControl {
  readonly el: HTMLElement;
  private _location: [number, number] = [0, 0];
  private _size: [number, number] = [0, 0];
  private _hint = "";
  /** Fires after the control's size changes (AutoSize pictures, labels). */
  onSizeChanged: (() => void) | null = null;

  constructor(opts: ControlOptions = {}, tag: keyof HTMLElementTagNameMap = "div") {
    this.el = document.createElement(tag);
    this.el.dataset.mir = "";
    this.el.classList.add("mir");
    if (opts.className) this.el.classList.add(...opts.className.split(/\s+/).filter(Boolean));
    if (opts.location) this.location = opts.location;
    if (opts.size) this.size = opts.size;
    if (opts.visible === false) this.visible = false;
    if (opts.notControl) this.notControl = true;
    if (opts.backColour) this.el.style.backgroundColor = opts.backColour;
    if (opts.border) this.el.style.outline = `1px solid ${opts.borderColour ?? "#000"}`;
    if (opts.opacity !== undefined) this.el.style.opacity = String(opts.opacity);
    if (opts.hint) this.hint = opts.hint;
    if (opts.onClick) this.onClick(opts.onClick);
    if (opts.parent) this.setParent(opts.parent);
  }

  setParent(parent: MirControl | HTMLElement): void {
    (parent instanceof MirControl ? parent.el : parent).append(this.el);
  }

  get location(): Point { return this._location; }
  set location(p: Point) {
    this._location = [p[0], p[1]];
    this.el.style.left = p[0] + "px";
    this.el.style.top = p[1] + "px";
  }

  get size(): Size { return this._size; }
  set size(s: Size) {
    if (s[0] === this._size[0] && s[1] === this._size[1]) return;
    this._size = [s[0], s[1]];
    this.el.style.width = s[0] + "px";
    this.el.style.height = s[1] + "px";
    this.onSizeChanged?.();
  }

  get visible(): boolean { return !this.el.hidden; }
  set visible(v: boolean) { this.el.hidden = !v; }

  set notControl(v: boolean) { this.el.style.pointerEvents = v ? "none" : ""; }

  get hint(): string { return this._hint; }
  set hint(text: string) {
    const had = this._hint !== "";
    this._hint = text;
    if (text) this.el.setAttribute("aria-label", text); else this.el.removeAttribute("aria-label");
    if (!had && text) {
      this.el.addEventListener("pointerenter", (e) => { if (this._hint) showHint(this._hint, e.clientX, e.clientY); });
      this.el.addEventListener("pointermove", (e) => { if (this._hint) showHint(this._hint, e.clientX, e.clientY); });
      this.el.addEventListener("pointerleave", hideHint);
    }
    if (!text) hideHint();
  }

  onClick(fn: (e: MouseEvent) => void): void {
    this.el.addEventListener("click", (e) => { e.stopPropagation(); fn(e); });
  }

  dispose(): void { this.el.remove(); }
}

export interface ImageOptions extends ControlOptions {
  library?: string | null;
  index?: number;
  /** UseOffSet: draw at Location + the image's X/Y offset. */
  useOffset?: boolean;
  /** AutoSize (default true): the control takes the picture's size. */
  autoSize?: boolean;
  grayScale?: boolean;
  /** DrawImage = false: keep the control but draw no picture. */
  drawImage?: boolean;
}

/** MirImageControl: draws Library[Index] at its Location (+ offset with UseOffSet). */
export class MirImage extends MirControl {
  protected readonly pic: HTMLDivElement;
  private _library: string | null;
  private _index: number;
  private _useOffset: boolean;
  private readonly autoSize: boolean;
  private _drawImage: boolean;
  /** Resolves after the current picture has loaded (or failed). */
  ready: Promise<LibPicture | null> = Promise.resolve(null);

  constructor(opts: ImageOptions = {}, tag: keyof HTMLElementTagNameMap = "div") {
    super(opts, tag);
    this.pic = document.createElement("div");
    this.pic.className = "mir-pic";
    this.el.prepend(this.pic);
    this._library = opts.library ?? null;
    this._index = opts.index ?? -1;
    this._useOffset = opts.useOffset ?? false;
    this.autoSize = opts.autoSize ?? opts.size === undefined;
    this._drawImage = opts.drawImage ?? true;
    if (opts.grayScale) this.grayScale = true;
    this.redraw();
  }

  get library(): string | null { return this._library; }
  set library(l: string | null) { if (l !== this._library) { this._library = l; this.redraw(); } }
  get index(): number { return this._index; }
  set index(i: number) { if (i !== this._index) { this._index = i; this.redraw(); } }
  set useOffset(v: boolean) { if (v !== this._useOffset) { this._useOffset = v; this.redraw(); } }
  set drawImage(v: boolean) { if (v !== this._drawImage) { this._drawImage = v; this.redraw(); } }
  set grayScale(v: boolean) { this.pic.style.filter = v ? "grayscale(1)" : ""; }
  /** ForeColour multiply for the picture (e.g. DimGray on a locked cell): CSS brightness approximation. */
  set dim(v: boolean) { this.pic.style.filter = v ? "brightness(0.41)" : ""; }
  set picOpacity(v: number) { this.pic.style.opacity = String(v); }

  /** The index the picture shows right now (MirButton overrides with hover / pressed / disabled). */
  protected displayIndex(): number { return this._index; }

  protected redraw(): void {
    const lib = this._drawImage ? this._library : null;
    const index = this.displayIndex();
    this.ready = setPicture(this.pic, lib, index, this._useOffset).then((pic) => {
      if (this.autoSize && pic && lib === this._library && index === this.displayIndex()) this.size = [pic.width, pic.height];
      return pic;
    });
  }
}

export interface ButtonOptions extends ImageOptions {
  hoverIndex?: number;
  pressedIndex?: number;
  disabledIndex?: number;
  /** SoundList id played on click (MirButton default ButtonB; most dialogs set ButtonA). 0 = none. */
  sound?: number;
  text?: string;
  centerText?: boolean;
  fontColour?: string;
  font?: string;
  enabled?: boolean;
}

/** MirButton: Index / HoverIndex / PressedIndex / DisabledIndex + an optional centred label. A real <button> so the
 *  keyboard and the click-sound listener see it; clicks on a disabled button do nothing. */
export class MirButton extends MirImage {
  private _hover = -1;
  private _pressed = -1;
  private _disabled = -1;
  private mouseOver = false;
  private mouseDown = false;
  private label: MirLabel | null = null;

  constructor(opts: ButtonOptions = {}) {
    super(opts, "button");
    const b = this.el as HTMLButtonElement;
    b.type = "button";
    b.classList.add("mir-button");
    b.dataset.sound = String(opts.sound ?? SoundList.ButtonB);
    if (opts.sound === 0) b.dataset.sound = "none";
    this._hover = opts.hoverIndex ?? -1;
    this._pressed = opts.pressedIndex ?? -1;
    this._disabled = opts.disabledIndex ?? -1;
    if (opts.enabled === false) b.disabled = true;
    b.addEventListener("pointerenter", () => { this.mouseOver = true; this.redraw(); });
    b.addEventListener("pointerleave", () => { this.mouseOver = false; this.mouseDown = false; this.redraw(); });
    b.addEventListener("pointerdown", (e) => { if (e.button === 0) { this.mouseDown = true; this.redraw(); } });
    b.addEventListener("pointerup", () => { if (this.mouseDown) { this.mouseDown = false; this.redraw(); } });
    if (opts.text !== undefined) this.text = opts.text;
    if (opts.centerText !== undefined || opts.fontColour || opts.font) {
      this.ensureLabel();
      if (opts.fontColour) this.label!.foreColour = opts.fontColour;
      if (opts.font) this.label!.font = opts.font;
    }
    this.redraw();
  }

  get enabled(): boolean { return !(this.el as HTMLButtonElement).disabled; }
  set enabled(v: boolean) { (this.el as HTMLButtonElement).disabled = !v; this.redraw(); }
  set hoverIndex(i: number) { this._hover = i; this.redraw(); }
  set pressedIndex(i: number) { this._pressed = i; this.redraw(); }
  set disabledIndex(i: number) { this._disabled = i; this.redraw(); }
  /** Sets Index / HoverIndex / PressedIndex together (BeforeDraw swaps in the desktop). */
  setIndices(index: number, hover = -1, pressed = -1): void {
    this._hover = hover; this._pressed = pressed; this.index = index; this.redraw();
  }

  set text(t: string) { this.ensureLabel().text = t; }
  get text(): string { return this.label?.text ?? ""; }
  set fontColour(c: string) { this.ensureLabel().foreColour = c; }

  private ensureLabel(): MirLabel {
    if (!this.label) {
      // MirButton._label: NotControl, sized to the button, centred (CenterText) in the desktop dialogs that use text
      this.label = new MirLabel({ parent: this, notControl: true, center: true, location: [0, 0] });
      this.label.el.style.inset = "0";
      this.label.el.style.width = this.label.el.style.height = "auto";
    }
    return this.label;
  }

  protected override displayIndex(): number {
    // MirButton.Index getter
    if (this._disabled >= 0 && this.el && (this.el as HTMLButtonElement).disabled) return this._disabled;
    if (this._pressed >= 0 && this.mouseDown && this.mouseOver) return this._pressed;
    if (this._hover >= 0 && this.mouseOver) return this._hover;
    return this.index;
  }

  protected override redraw(): void {
    if (this.el === undefined || this._hover === undefined) { super.redraw(); return; } // during super()
    super.redraw();
  }
}

export interface CheckBoxOptions extends ButtonOptions {
  tickedIndex: number;
  unTickedIndex: number;
  checked?: boolean;
  labelText?: string;
  onChange?: (checked: boolean) => void;
}

/** MirCheckBox: a button whose Index follows Checked; LabelText draws at (15, -2). */
export class MirCheckBox extends MirButton {
  private _checked = false;
  private readonly ticked: number;
  private readonly unticked: number;
  private readonly textLabel: MirLabel;
  onChange: ((checked: boolean) => void) | null;

  constructor(opts: CheckBoxOptions) {
    super({ ...opts, index: opts.checked ? opts.tickedIndex : opts.unTickedIndex });
    this.ticked = opts.tickedIndex;
    this.unticked = opts.unTickedIndex;
    this._checked = !!opts.checked;
    this.onChange = opts.onChange ?? null;
    this.textLabel = new MirLabel({ parent: this, notControl: true, autoSize: true, location: [15, -2], text: opts.labelText ?? "" });
    this.textLabel.visible = !!opts.labelText;
    this.el.addEventListener("click", () => {
      if (!this.enabled) return;
      this.checked = !this._checked;
      this.onChange?.(this._checked);
    });
  }

  get checked(): boolean { return this._checked; }
  set checked(v: boolean) { this._checked = v; this.index = v ? this.ticked : this.unticked; }
  set labelText(t: string) { this.textLabel.text = t; this.textLabel.visible = t !== ""; }
}

export interface LabelOptions extends ControlOptions {
  text?: string;
  /** AutoSize: size to the text (no wrapping). Default false when a size is given, else true. */
  autoSize?: boolean;
  /** CSS colour (ForeColour, default White). */
  foreColour?: string;
  font?: string;
  /** OutLine (default true) with OutLineColour (default Black). */
  outline?: boolean;
  outlineColour?: string;
  /** TextFormatFlags.HorizontalCenter | VerticalCenter. */
  center?: boolean;
  /** HorizontalCenter only / Right alignment. */
  align?: "left" | "center" | "right";
  /** VerticalCenter only. */
  vCenter?: boolean;
}

/** MirLabel: Arial 8pt, white, 1 px black outline, WordBreak inside Size (or AutoSize to the text). */
export class MirLabel extends MirControl {
  private _text = "";
  private readonly autoSize: boolean;

  constructor(opts: LabelOptions = {}) {
    super(opts);
    this.el.classList.add("mir-label");
    this.autoSize = opts.autoSize ?? opts.size === undefined;
    if (this.autoSize) this.el.classList.add("mir-autosize");
    if (opts.foreColour) this.foreColour = opts.foreColour;
    if (opts.font) this.font = opts.font;
    if (opts.outline === false) this.el.classList.add("mir-no-outline");
    else if (opts.outlineColour) this.el.style.setProperty("--mir-outline", opts.outlineColour);
    if (opts.center) this.el.classList.add("mir-center");
    if (opts.vCenter) this.el.classList.add("mir-vcenter");
    if (opts.align) this.el.style.textAlign = opts.align;
    if (opts.align === "center" && opts.vCenter) this.el.classList.add("mir-center");
    if (opts.text) this.text = opts.text;
  }

  get text(): string { return this._text; }
  set text(t: string) {
    if (t === this._text) return;
    this._text = t;
    this.el.textContent = t;
  }
  set foreColour(c: string) { this.el.style.color = c; }
  set font(f: string) { this.el.style.font = f; }
}

export interface TextBoxOptions extends ControlOptions {
  text?: string;
  maxLength?: number;
  password?: boolean;
  foreColour?: string;
  font?: string;
  /** Multiline TextBox (a <textarea>). */
  multiline?: boolean;
  readOnly?: boolean;
  onEnter?: (value: string) => void;
  onEscape?: () => void;
  onChange?: (value: string) => void;
}

/** MirTextBox: a borderless WinForms TextBox at Location / Size (Arial 10pt, white on black). Keys typed in it never
 *  reach the game's key handlers. */
export class MirTextBox extends MirControl {
  readonly input: HTMLInputElement | HTMLTextAreaElement;

  constructor(opts: TextBoxOptions = {}) {
    super(opts);
    this.el.classList.add("mir-textbox");
    const input = opts.multiline ? document.createElement("textarea") : document.createElement("input");
    if (input instanceof HTMLInputElement) input.type = opts.password ? "password" : "text";
    input.autocomplete = "off";
    input.spellcheck = false;
    if (opts.maxLength) input.maxLength = opts.maxLength;
    if (opts.text) input.value = opts.text;
    if (opts.foreColour) input.style.color = opts.foreColour;
    if (opts.font) input.style.font = opts.font;
    if (opts.readOnly) input.readOnly = true;
    if (opts.backColour) { input.style.backgroundColor = opts.backColour; this.el.style.backgroundColor = ""; }
    input.addEventListener("keydown", (e) => {
      e.stopPropagation();
      const k = e as KeyboardEvent;
      if (k.key === "Enter" && !opts.multiline && opts.onEnter) { k.preventDefault(); opts.onEnter(input.value); }
      else if (k.key === "Escape" && opts.onEscape) { k.preventDefault(); opts.onEscape(); }
    });
    input.addEventListener("keyup", (e) => e.stopPropagation());
    input.addEventListener("keypress", (e) => e.stopPropagation());
    if (opts.onChange) input.addEventListener("input", () => opts.onChange!(input.value));
    this.input = input;
    this.el.append(input);
  }

  get text(): string { return this.input.value; }
  set text(t: string) { this.input.value = t; }
  focus(): void { this.input.focus(); }
}

export interface WindowOptions extends Omit<ImageOptions, "location"> {
  /** Movable: drag by the window's own picture (not its child controls). */
  movable?: boolean;
  /** Sort: a mouse-down brings the window to the front. */
  sort?: boolean;
  /** Location = Center: centred on the screen once the picture's size is known. */
  location?: Point | "center";
  /** Modal: a transparent layer behind the window blocks the rest of the interface. */
  modal?: boolean;
  onShow?: () => void;
  onHide?: () => void;
}

/** A top-level dialog (MirImageControl with Movable / Sort / Show / Hide), attached to the UI root. Hidden until
 *  show() unless `visible: true`. */
export class MirWindow extends MirImage {
  private readonly movable: boolean;
  private readonly sortable: boolean;
  private modalLayer: HTMLDivElement | null = null;
  onShow: (() => void) | null;
  onHide: (() => void) | null;

  constructor(opts: WindowOptions = {}) {
    const center = opts.location === "center";
    super({ ...opts, location: center ? undefined : (opts.location as Point | undefined), parent: opts.parent ?? getUiRoot(), visible: opts.visible ?? false });
    this.el.classList.add("mir-window");
    this.movable = !!opts.movable;
    this.sortable = opts.sort ?? true;
    this.onShow = opts.onShow ?? null;
    this.onHide = opts.onHide ?? null;
    if (opts.modal) {
      this.modalLayer = document.createElement("div");
      this.modalLayer.className = "mir-modal";
      this.modalLayer.hidden = this.el.hidden;
      this.el.before(this.modalLayer);
    }
    if (center) {
      if (opts.size) this.center();
      else this.ready.then(() => this.center());
    }
    // the game canvas must not see clicks that land on a window
    for (const t of ["pointerdown", "mousedown", "contextmenu", "wheel"] as const) this.el.addEventListener(t, (e) => e.stopPropagation());
    this.el.addEventListener("pointerdown", (e) => {
      if (this.sortable) this.bringToFront();
      if (!this.movable || e.button !== 0) return;
      // MirControl: Moving starts only when the window itself is the MouseControl (not one of its child controls)
      const hit = (e.target as Element).closest("[data-mir]");
      if (hit !== this.el) return;
      e.preventDefault();
      const root = this.el.parentElement!.getBoundingClientRect();
      const scale = root.width / (this.el.parentElement!.offsetWidth || 1) || 1;
      const startX = e.clientX, startY = e.clientY;
      const [lx, ly] = this.location;
      const move = (m: PointerEvent) => { this.location = [Math.round(lx + (m.clientX - startX) / scale), Math.round(ly + (m.clientY - startY) / scale)]; };
      const up = () => { window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", up); };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
    });
  }

  /** Location = Center ((Settings.ScreenWidth - Size.Width) / 2, (Settings.ScreenHeight - Size.Height) / 2). */
  center(): void {
    const [w, h] = this.size;
    this.location = [Math.floor((SCREEN_WIDTH - w) / 2), Math.floor((SCREEN_HEIGHT - h) / 2)];
  }

  bringToFront(): void {
    this.el.style.zIndex = String(++zTop);
    if (this.modalLayer) this.modalLayer.style.zIndex = String(zTop - 0);
  }

  get isVisible(): boolean { return this.visible; }

  show(): void {
    if (this.visible) { this.bringToFront(); return; }
    this.visible = true;
    if (this.modalLayer) this.modalLayer.hidden = false;
    this.bringToFront();
    this.onShow?.();
  }

  hide(): void {
    if (!this.visible) return;
    this.visible = false;
    if (this.modalLayer) this.modalLayer.hidden = true;
    hideHint();
    this.onHide?.();
  }

  toggle(): void { if (this.visible) this.hide(); else this.show(); }

  override dispose(): void {
    this.modalLayer?.remove();
    super.dispose();
  }
}

/** Vertical drag of a scroll-bar button (PositionBar OnMoving): `onMove` gets the wanted Location.Y; the caller clamps. */
export function dragY(button: MirControl, onMove: (y: number) => void): void {
  button.el.addEventListener("pointerdown", (e) => {
    if (e.button !== 0) return;
    e.preventDefault();
    const root = getUiRoot();
    const scale = root.getBoundingClientRect().width / (root.offsetWidth || 1) || 1;
    const startY = e.clientY, y0 = button.location[1];
    const move = (m: PointerEvent) => onMove(Math.round(y0 + (m.clientY - startY) / scale));
    const up = () => { window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", up); };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  });
}

/** The common Prguse2 360/361/362 close button of the desktop dialogs. */
export function closeButton(parent: MirControl, location: Point, onClick: () => void): MirButton {
  return new MirButton({ parent, library: Lib.Prguse2, index: 360, hoverIndex: 361, pressedIndex: 362, location, sound: SoundList.ButtonA, onClick });
}

/** GameScene item label / tooltip look: black 80 % box with the (148,146,148) border (red when the item is broken). */
export function styleTooltip(box: HTMLElement, broken = false): void {
  box.classList.add("mir-tooltip");
  box.style.borderColor = broken ? "#ff0000" : "rgb(148,146,148)";
}

// Ported from play/hudMock.ts (the '.hudm' theme root and HudMock.set, ~150-160 / ~732-741) and src/ui/modern/core/layers.ts:
// the themed root of the new account screens (login.ts, select.ts, loading.ts). They run before the game exists, so
// there is no UiRuntime / Layers: this is a small standalone '.hudm' root with the theme variables from the saved Ui*
// settings (core/theme.ts applyTheme), a scene layer for the screen and a popups layer (core/popups.ts) usable without
// a game runtime.
//
// Mounted in #screen (1024 x 768 inside #stage; play/main.ts fit() scales the stage), it fills its parent, so a screen
// laid out at 1024 x 768 fits, and the same root works on a bigger parent (a loading overlay at the game resolution).
// Keys: the popups' keys come from one keydown listener on the root ELEMENT (element-level, capture, removed with the
// scope) - never a document / window listener: there is no input router before S.StartGame.
// One scope (AbortController) per root: dispose() aborts it (listeners, timers created with `signal`, open popups) and
// removes the element.

import { h, injectCss } from "../core/dom.ts";
import { installBaseStyles } from "../core/styles.ts";
import { applyTheme, type ThemeColors, type ThemeSettings } from "../core/theme.ts";
import { Popups } from "../core/popups.ts";
import { gameSettings, type GameSettings } from "../../../game/settings.ts";

const CSS = `
.hudm.acct { width: 100%; height: 100%; pointer-events: auto; background: #000; user-select: none; }
.hudm.acct input, .hudm.acct textarea { user-select: text; }
.hudm.acct > .acct-scene { position: absolute; inset: 0; z-index: 1; }
.hudm.acct > .acct-pops { position: absolute; inset: 0; z-index: 80; pointer-events: none; }
`;

/** The theme settings (preset + custom colours) from the saved Ui* settings (the same mapping modernUi.ts uses). */
export function accountThemeSettings(s: Readonly<GameSettings> = gameSettings): ThemeSettings {
  return {
    theme: s.UiTheme,
    custom: { accent: s.UiAccent, frame: s.UiFrame, text: s.UiText, hue: s.UiHue, sat: s.UiSat, lit: s.UiLit, glow: s.UiGlow, radius: s.UiRadius },
  };
}

export interface AccountRootOptions {
  /** Extra class(es) on the root (e.g. the screen's name). */
  cls?: string;
  /** Theme to apply (default: the saved Ui* settings). */
  theme?: ThemeSettings;
}

/** A standalone '.hudm' root for one account screen: theme variables, a scene layer, popups, one scope. */
export class AccountRoot {
  /** The '.hudm.acct' element (theme variables on it). */
  readonly root: HTMLElement;
  /** Where the screen puts its content (fills the root). */
  readonly scene: HTMLElement;
  /** The popups' layer (above the scene). */
  readonly pops: HTMLElement;
  /** Modal boxes and toasts (MirMessageBox / MirInputBox equivalents) for this screen; closed on dispose. */
  readonly popups: Popups;
  /** The resolved theme colours (canvas drawing). */
  readonly theme: ThemeColors;
  private readonly scope = new AbortController();

  constructor(parent: HTMLElement, opts: AccountRootOptions = {}) {
    installBaseStyles();
    injectCss("screens-account-root", CSS);
    this.scene = h("div", "acct-scene");
    this.pops = h("div", "acct-pops");
    this.root = h("div", "hudm acct" + (opts.cls ? " " + opts.cls : ""), this.scene, this.pops);
    this.theme = applyTheme(this.root, opts.theme ?? accountThemeSettings());
    this.popups = new Popups(this.pops, this.scope.signal);
    // While a popup shows, its keys (Enter / Esc / Y / N ...) run and nothing reaches the screen's own fields.
    this.root.addEventListener("keydown", (e) => { if (this.popups.handleKey(e)) e.stopPropagation(); }, { capture: true, signal: this.scope.signal });
    parent.append(this.root);
  }

  /** The screen's scope: pass it to listeners / timers (core/dom.ts later, windowInterval, debounce). */
  get signal(): AbortSignal { return this.scope.signal; }

  get disposed(): boolean { return this.scope.signal.aborted; }

  /** Closes the popups, stops everything tied to `signal` and removes the root. */
  dispose(): void {
    if (this.scope.signal.aborted) return;
    this.scope.abort();
    this.root.remove();
  }
}

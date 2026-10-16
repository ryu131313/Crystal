// Keyboard router for the game scene: one window "keydown" listener that hands each key to layered handlers, highest
// layer first. The desktop has one key path too: CMain_KeyDown (Client/Forms/CMain.cs) -> MirScene.OnKeyDown ->
// the active control / dialogs (MirControl.OnKeyDown, a modal MirMessageBox first) -> GameScene.GameScene_KeyDown
// (Client/MirScenes/GameScene.cs, the KeybindOptions loop). The browser has no focus chain, so the layers stand in for it.
//
// A handler returns true when it consumed the key; lower handlers are then skipped. The router never calls
// preventDefault / stopPropagation itself: the handler decides, so the classic listeners keep their exact behaviour.
// Bind matching stays in keybinds.ts (`matches(e, KeybindOptions.*)`); the router knows nothing about bindings.
//
// One router per game scene (createInputRouter(gameSignal())): aborting the scene's signal removes the listeners and
// drops every handler. No module state.

/** Router layers, highest priority first. */
export type InputLayer = "modal" | "scene" | "menu" | "window" | "hotkeys" | "gameplay";

/** Dispatch order (`InputLayer` from highest to lowest priority). */
export const INPUT_LAYERS: readonly InputLayer[] = ["modal", "scene", "menu", "window", "hotkeys", "gameplay"];

/** A layered key handler: true = consumed (lower handlers are skipped). */
export type KeyHandler = (e: KeyboardEvent) => boolean | void;
/** A key watcher: sees every event, cannot consume it. */
export type KeyWatcher = (e: KeyboardEvent) => void;

export interface InputRouter {
  /** Adds a keydown handler to a layer (after the layer's earlier handlers). Returns its removal. */
  register(layer: InputLayer, handler: KeyHandler): () => void;
  /** Adds a keydown watcher that runs before every layer, whatever a handler then consumes (held-key state such as
   *  game.ts's Shift / Alt flags, which today sit in their own always-run listener). Returns its removal. */
  watch(handler: KeyWatcher): () => void;
  /** Adds a keyup watcher (held-key release). Returns its removal. */
  onKeyUp(handler: KeyWatcher): () => void;
  /** Adds a window blur watcher (CMain_Deactivate: held keys are released). Returns its removal. */
  onBlur(handler: () => void): () => void;
}

/** True when the key is aimed at a text field: an input, textarea, select or contenteditable element has the focus
 *  (or is the event target). Matches the classic checks (`document.activeElement instanceof HTMLInputElement /
 *  HTMLTextAreaElement / HTMLSelectElement` in game.ts, chatDialog.ts, helpWindow.ts, optionWindow.ts); windows that
 *  only check input / textarea differ only while a select has the focus. */
export function isTextTarget(e: KeyboardEvent): boolean {
  return isTextElement(e.target) || isTextElement(typeof document !== "undefined" ? document.activeElement : null);
}

function isTextElement(el: EventTarget | null): boolean {
  if (typeof HTMLElement === "undefined" || !(el instanceof HTMLElement)) return false;
  return el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement || el.isContentEditable;
}

/** Installs the scene's key listeners (window keydown + keyup + blur, bubble phase, removed when `signal` aborts). */
export function createInputRouter(signal: AbortSignal): InputRouter {
  const layers = new Map<InputLayer, KeyHandler[]>();
  for (const l of INPUT_LAYERS) layers.set(l, []);
  const watchers: KeyWatcher[] = [];
  const upWatchers: KeyWatcher[] = [];
  const blurWatchers: (() => void)[] = [];

  const add = <T>(list: T[], fn: T): (() => void) => {
    if (signal.aborted) return () => {};
    list.push(fn);
    return () => {
      const i = list.indexOf(fn);
      if (i >= 0) list.splice(i, 1);
    };
  };
  // A throwing handler must not stop the rest: today every window has its own listener, so one failure stays local.
  const safely = (fn: () => boolean | void): boolean => {
    try { return fn() === true; } catch (err) { console.error(err); return false; }
  };

  if (typeof window !== "undefined" && !signal.aborted) {
    window.addEventListener("keydown", (e) => {
      // Snapshots: a handler may register or remove handlers (a window opening / closing) while the key is routed.
      for (const w of watchers.slice()) safely(() => w(e));
      for (const l of INPUT_LAYERS) {
        for (const h of layers.get(l)!.slice()) if (safely(() => h(e))) return;
      }
    }, { signal });
    window.addEventListener("keyup", (e) => { for (const w of upWatchers.slice()) safely(() => w(e)); }, { signal });
    window.addEventListener("blur", () => { for (const w of blurWatchers.slice()) safely(() => w()); }, { signal });
  }
  signal.addEventListener("abort", () => {
    for (const list of layers.values()) list.length = 0;
    watchers.length = upWatchers.length = blurWatchers.length = 0;
  }, { once: true });

  return {
    register: (layer, handler) => add(layers.get(layer)!, handler),
    watch: (handler) => add(watchers, handler),
    onKeyUp: (handler) => add(upWatchers, handler),
    onBlur: (handler) => add(blurWatchers, handler),
  };
}

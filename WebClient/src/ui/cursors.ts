// Mouse cursors: CMain.LoadMouseCursors / SetMouseCursor with the client's own Data\Cursors\*.CUR files (served
// read-only by WebRelay under /data/), applied as CSS cursors. The desktop sets the form's cursor; here the stage carries
// Default (play.css, inherited by the UI) and the game canvas carries GameScene.UpdateMouseCursor's choice. Text boxes use
// TextPrompt (MirTextBox) via play.css. Not ported: Upgrade (a gem picked + Ctrl over an item) and Trash (unused).

const FILES = {
  Default: "Cursor_Default.CUR",
  Attack: "Cursor_Normal_Atk.CUR",
  AttackRed: "Cursor_Compulsion_Atk.CUR",
  NPCTalk: "Cursor_Npc.cur",
  TextPrompt: "Cursor_TextPrompt.CUR",
} as const;

export type MouseCursor = keyof typeof FILES;

/** The CSS cursor value (the .CUR file carries its hot spot; the system cursor is the fallback). */
export function cursorCss(cursor: MouseCursor): string {
  return `url("/data/Cursors/${FILES[cursor]}"), ${cursor === "TextPrompt" ? "text" : "default"}`;
}

const current = new WeakMap<HTMLElement, MouseCursor>();

/** CMain.SetMouseCursor for one element: writes the style only when the cursor changes. */
export function setMouseCursor(el: HTMLElement, cursor: MouseCursor): void {
  if (current.get(el) === cursor) return;
  current.set(el, cursor);
  el.style.cursor = cursorCss(cursor);
}

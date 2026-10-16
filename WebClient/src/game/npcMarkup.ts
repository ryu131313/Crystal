// The NPC page markup (NPCDialog.NewText / NewButton / NewColour / NewLink and GameScene.NPCResponse's BigButton
// pass, Client/MirScenes/Dialogs/NPCDialogs.cs) as a pure parser: both UIs render the structured page it returns.
//   <<text/@key>> or <<text/@key/Colour>>  a BigButton (a line holding only big buttons is dropped);
//   <text/@key>                            a link that calls the page (C.CallNPC "[@key]"; @Exit closes the dialog);
//   {text/Colour}                          coloured text (Color.FromName);
//   (text/url)                             a web link;
//   [ITEM|MONSTER|NPC:idx(|name)], <$ITEM|MONSTER|NPC:idx>   an info link (the name, or the info's name once known).

const BIG = /<<((.*?)\/(@.*?))>>/g;
// One pass over every inline token, in index order like NPCDialog.NewText's sorted match list.
const INLINE = /<((?:[^<>]*?)\/(?:@[^<>]*?))>|\{(.*?)\/(.*?)\}|\((.*?)\/(.*?)\)|\[(MONSTER|NPC|ITEM):(\d+)(?:\|([^\]]+))?\]|<\$(MONSTER|NPC|ITEM):(\d+)>/gi;

export type NpcLinkKind = "MONSTER" | "NPC" | "ITEM";

/** One piece of a page row. */
export type NpcSegment =
  /** Plain text between the tokens. */
  | { readonly kind: "text"; readonly text: string }
  /** NewButton: `key` is "@Name" (sent as "[@Name]"; "@exit", any case, closes the dialog). */
  | { readonly kind: "link"; readonly text: string; readonly key: string }
  /** NewColour: `colour` is the raw Color.FromName text (an unknown name draws transparent on the desktop). */
  | { readonly kind: "colour"; readonly text: string; readonly colour: string }
  /** NewButton with an address (the desktop opens it with Process.Start). */
  | { readonly kind: "url"; readonly text: string; readonly url: string }
  /** NewLink: an item / monster / NPC info link; `name` = the text after "|" (else the info's name). */
  | { readonly kind: "info"; readonly link: NpcLinkKind; readonly index: number; readonly name: string | undefined };

/** A BigButton (GameScene.NPCResponse: text, the "@key" action, ForeColour, RoyalBlue by default). */
export interface NpcBigButton { readonly text: string; readonly action: string; readonly colour: string }

/** A parsed S.NPCResponse page. */
export interface NpcPage {
  /** NPCDialog.CurrentLines: the page lines minus those that held only big buttons (the scroll counts these). */
  readonly lines: readonly string[];
  /** `lines` parsed into segments (rows[i] is lines[i]). */
  readonly rows: readonly (readonly NpcSegment[])[];
  /** GameScene.BigButtons, in page order. */
  readonly bigButtons: readonly NpcBigButton[];
}

export const EMPTY_PAGE: NpcPage = { lines: [], rows: [], bigButtons: [] };

/** NPCDialog.NewText's tokens on one row, in order (text between the tokens kept as text segments). */
export function parseLine(line: string): NpcSegment[] {
  const out: NpcSegment[] = [];
  let at = 0;
  for (const m of line.matchAll(INLINE)) {
    if (m.index! > at) out.push({ kind: "text", text: line.slice(at, m.index) });
    at = m.index! + m[0].length;
    if (m[1] !== undefined) {
      const slash = m[1].indexOf("/@");
      out.push({ kind: "link", text: m[1].slice(0, slash), key: m[1].slice(slash + 1) });
    } else if (m[2] !== undefined) {
      out.push({ kind: "colour", text: m[2], colour: m[3]! });
    } else if (m[4] !== undefined) {
      out.push({ kind: "url", text: m[4], url: m[5]! });
    } else {
      out.push({ kind: "info", link: (m[6] ?? m[9])!.toUpperCase() as NpcLinkKind, index: Number(m[7] ?? m[10]), name: m[8] });
    }
  }
  if (at < line.length) out.push({ kind: "text", text: line.slice(at) });
  return out;
}

/** GameScene.NPCResponse: the BigButton pass (bottom up), then the rows. */
export function parsePage(page: readonly string[]): NpcPage {
  const lines = [...page];
  const big: NpcBigButton[] = [];
  for (let i = lines.length - 1; i >= 0; i--) {
    const line = lines[i]!;
    const found: NpcBigButton[] = [];
    for (const m of line.matchAll(BIG)) {
      const [action, colour] = m[3]!.split("/");
      found.push({ text: m[2]!, action: action!, colour: colour || "RoyalBlue" });
    }
    big.unshift(...found);
    const rest = line.replace(BIG, "");
    if (rest.trim() === "" && rest !== line) lines.splice(i, 1);
  }
  return { lines, rows: lines.map(parseLine), bigButtons: big };
}

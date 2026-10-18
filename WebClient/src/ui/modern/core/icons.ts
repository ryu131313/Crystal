// The new UI's shared icons (webclient.md §3.6: no local svg helpers or icon tables). Merged from the scene-lab mocks:
// the 24 x 24 stroke icons of play/hudMock.ts (~105-130) and the icon tables of hudChat, hudFriends, hudGameMenu,
// hudHelp, hudMail, hudMentor, hudMinimap, hudQuests and hudRelationship; and the illustrated 32 x 32 menu emblems of
// play/hudMenuIcons.ts (ART, ICON_DEFS, ICON_CSS copied verbatim; the window emblems are now pictures, PICTURES).
// Icons are constant markup / path data written in code, never data. Need a new icon? Add it to ICONS here, not a local table.

import { webUrl } from "../../../assets/root.ts";
import { injectCss } from "./dom.ts";

const NS = "http://www.w3.org/2000/svg";

/** 24 x 24 stroke paths (stroke: currentColor, no fill; CSS class "ic" in styles.ts). */
export const ICONS = {
  // HUD menu strip (hudMock)
  guild: "M6 21V3M6 4h12l-3 4.5L18 13H6",
  // Backpack: carry loop, top straps, domed body, side pockets, front pocket with a zip.
  bag: "M6.5 11a5.5 5.5 0 0111 0v9a1.5 1.5 0 01-1.5 1.5h-8A1.5 1.5 0 016.5 20zM9.5 5.9V4a1.5 1.5 0 011.5-1.5h2A1.5 1.5 0 0114.5 4v1.9M7.6 8.4V5.6a.8.8 0 011.6 0v.9M16.4 8.4V5.6a.8.8 0 00-1.6 0v.9M6.5 13.5H5a1 1 0 00-1 1v3.5a1 1 0 001 1h1.5M17.5 13.5H19a1 1 0 011 1v3.5a1 1 0 01-1 1h-1.5M9 14.5h6a1 1 0 011 1v2.5a1 1 0 01-1 1H9a1 1 0 01-1-1v-2.5a1 1 0 011-1zM10.2 16.4h3.6",
  // Character: a large round head over closed rounded shoulders.
  person: "M12 2.5a4.6 4.6 0 110 9.2 4.6 4.6 0 010-9.2zM4.5 20.5c0-3.6 2.9-6.5 6.5-6.5h2c3.6 0 6.5 2.9 6.5 6.5 0 .6-.4 1-1 1h-13c-.6 0-1-.4-1-1z",
  star: "M12 2l2.8 6.2L21 9l-4.7 4.4 1.3 6.6L12 16.8 6.4 20l1.3-6.6L3 9l6.2-.8z",
  scroll: "M6 3h10l2 2v16H8l-2-2zM9 8h6M9 12h6M9 16h4",
  map: "M2 5l6-2 8 3 6-2v15l-6 2-8-3-6 2zM8 3v15M16 6v15",
  back: "M9 14L4 9l5-5M4 9h10a6 6 0 010 12h-3",
  cross: "M6 6l12 12M18 6L6 18",
  group: "M12 3l8 3v5c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10V6zM9 10a2 2 0 104 0 2 2 0 00-4 0M7.5 16c.5-2 2-3 4.5-3s4 1 4.5 3",
  shop: "M6 3h12l3 6-9 12L3 9zM3 9h18M9 3l3 6 3-6M12 9v12",
  mail: "M3 6h18v12H3zM3 6l9 7 9-7",
  friends: "M9 11a3 3 0 100-6 3 3 0 000 6zM17 11a2.5 2.5 0 100-5 2.5 2.5 0 000 5zM3 20c0-3.5 2.7-6 6-6s6 2.5 6 6M15 14.5c3 0 6 2 6 5",
  menu: "M4 6h16M4 12h16M4 18h16",
  ranking: "M4 20V10h4v10M10 20V4h4v16M16 20v-7h4v7",
  craft: "M14 4l6 6-3 3-6-6zM11 7l-8 8 3 3 8-8M4 20l2-2",
  creature: "M7 8a2 2 0 100-4 2 2 0 000 4zM17 8a2 2 0 100-4 2 2 0 000 4zM4.5 13a2 2 0 100-4 2 2 0 000 4zM19.5 13a2 2 0 100-4 2 2 0 000 4zM12 11c3 0 5.5 3.5 5.5 6.5 0 2-1.5 3-3 3-1 0-1.5-.6-2.5-.6s-1.5.6-2.5.6c-1.5 0-3-1-3-3C6.5 14.5 9 11 12 11z",
  mount: "M4 15c1-4 4-6 8-6h3l3-4 1 4 2 2-2 2h-3l-1 7M7 15v5M11 15l1 5M4 15l-1 3",
  fishing: "M18 3v11a4 4 0 01-8 0v-1M18 3l-3 3M10 13l-2 2M3 21c2-2 4-2 6 0s4 2 6 0 4-2 6 0",
  mentor: "M12 4l10 5-10 5L2 9zM6 11v5c3 2 9 2 12 0v-5M22 9v6",
  heart: "M12 20s-7.5-4.5-7.5-10A4.5 4.5 0 0112 7a4.5 4.5 0 017.5 3c0 5.5-7.5 10-7.5 10z",
  hero: "M12 3l2.5 5 5.5.8-4 3.9 1 5.5-5-2.6-5 2.6 1-5.5-4-3.9 5.5-.8zM12 8v5",
  gear: "M12 8.5a3.5 3.5 0 110 7 3.5 3.5 0 010-7zM12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1",
  // Chat (hudChat)
  search: "M11 4a7 7 0 110 14 7 7 0 010-14zM20 20l-4-4",
  lock: "M6 11h12v9H6zM8 11V8a4 4 0 018 0v3",
  unlock: "M6 11h12v9H6zM8 11V8a4 4 0 017.5-2",
  link: "M10 14a4 4 0 005.7 0l3-3a4 4 0 00-5.7-5.7l-1 1M14 10a4 4 0 00-5.7 0l-3 3a4 4 0 005.7 5.7l1-1",
  send: "M4 12l16-8-6 16-2-7z",
  plus: "M12 5v14M5 12h14",
  minus: "M5 12h14",
  // Friends / mail / mentor / relationship
  heartFull: "M12 21s-7.5-4.6-9.5-9.3C1 8 3.4 4.5 7 4.5c2 0 3.6 1.1 5 3 1.4-1.9 3-3 5-3 3.6 0 6 3.5 4.5 7.2C19.5 16.4 12 21 12 21z",
  book: "M4 5a2 2 0 012-2h14v16H6a2 2 0 00-2 2zM4 19V5",
  bookOpen: "M4 4h6a2 2 0 012 2v14a2 2 0 00-2-2H4zM20 4h-6a2 2 0 00-2 2v14a2 2 0 012-2h6z",
  memo: "M5 3h10l4 4v14H5zM15 3v4h4M8 11h8M8 15h6",
  chat: "M4 5h16v11H9l-5 4z",
  inbox: "M3 13l3-8h12l3 8v6H3zM3 13h5l1 3h6l1-3h5",
  clip: "M8 12l6-6a3 3 0 014 4l-8 8a5 5 0 01-7-7l7-7",
  compose: "M4 20h4L19 9l-4-4L4 16zM13 7l4 4",
  coin: "M12 3a9 9 0 110 18 9 9 0 010-18zM12 7a5 5 0 110 10 5 5 0 010-10z",
  coinGold: "M12 4a8 8 0 110 16 8 8 0 010-16zM12 8v8M9.5 10.5c0-1 1-1.5 2.5-1.5s2.5.5 2.5 1.5-1 1.5-2.5 1.5-2.5.5-2.5 1.5 1 1.5 2.5 1.5 2.5-.5 2.5-1.5",
  trash: "M5 7h14M10 7V4h4v3M6 7l1 13h10l1-13",
  reply: "M10 8L4 13l6 5M4 13h11a5 5 0 015 5",
  stamp: "M5 4h14v16H5zM8 8h8v6H8zM5 4l1 1M19 4l-1 1M5 20l1-1M19 20l-1-1",
  sword: "M5 19L19 5M14 5h5v5M5 14l5 5",
  cap: "M2 9l10-5 10 5-10 5zM6 11v5c3 2 9 2 12 0v-5M22 9v6",
  bank: "M3 10l9-6 9 6M5 10v8M9 10v8M15 10v8M19 10v8M3 20h18",
  info: "M12 3a9 9 0 110 18 9 9 0 010-18zM12 11v5M12 8v.01",
  ring: "M12 8a6 6 0 110 12 6 6 0 010-12zM9 4h6l-1.5 3h-3z",
  recall: "M12 3v4M12 17v4M3 12h4M17 12h4M12 8a4 4 0 110 8 4 4 0 010-8z",
  calendar: "M4 6h16v14H4zM4 10h16M8 3v4M16 3v4",
  // Minimap (hudMinimap)
  sun: "M12 7a5 5 0 110 10 5 5 0 010-10zM12 1v3M12 20v3M1 12h3M20 12h3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1",
  moon: "M20 14A8 8 0 1110 4a6.5 6.5 0 0010 10z",
  dusk: "M3 18h18M6 18a6 6 0 0112 0M12 5v3M5 9l2 2M19 9l-2 2",
  rain: "M7 15a5 5 0 01.5-10 6 6 0 0111 2 4 4 0 01-.5 8zM8 18l-1 3M12 18l-1 3M16 18l-1 3",
  snow: "M12 2v20M3 7l18 10M3 17L21 7",
  fog: "M3 9h18M5 13h14M3 17h18",
  pin: "M12 21s-7-7-7-12a7 7 0 0114 0c0 5-7 12-7 12zM12 7a2 2 0 110 4 2 2 0 010-4z",
  square: "M4 4h16v16H4z",
  // Quests (hudQuests)
  repeat: "M4 12a8 8 0 0114-5.3L20 9M20 4v5h-5M20 12a8 8 0 01-14 5.3L4 15M4 20v-5h5",
  kill: "M3 3l12 12M21 3L9 15M13 17l4 4M17 17l-4 4M7 17l4 4M11 17l-4 4",
  pouch: "M6 10a5 5 0 0111 0v9a1.5 1.5 0 01-1.5 1.5h-8A1.5 1.5 0 016 19z",
  talk: "M4 5h16v10H9l-5 4z",
  tack: "M9 3h6l-1 6 4 4H6l4-4zM12 13v8",
  check: "M5 12l4 4 10-10",
  // Help topics (hudHelp)
  key: "M3 7h18v10H3zM7 11h.01M11 11h.01M15 11h.01M7 14h10",
  walk: "M13 4a2 2 0 110 4 2 2 0 010-4M10 21l2-7 3 3v4M8 12l3-3 4 1 2 3",
  drop: "M12 3s6 7 6 11a6 6 0 01-12 0c0-4 6-11 6-11z",
  shield: "M12 3l8 3v6c0 5-4 8-8 9-4-1-8-4-8-9V6z",
  tag: "M3 12V4h8l10 10-8 8zM7 8h.01",
  wrench: "M14 6a4 4 0 015 5l-9 9-4-4 9-9zM5 19l-1 1",
  swap: "M4 8h14l-3-3M20 16H6l3 3",
  eye: "M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12zM12 9a3 3 0 110 6 3 3 0 010-6",
  chart: "M4 20V10M10 20V4M16 20v-7M22 20H2",
  horse: "M5 20v-6l3-5 2-5 3 2 5 1 1 3-3 1-1 4v5M9 14h7",
  fish: "M3 12c3-5 9-6 14-2l4-3v10l-4-3c-5 4-11 3-14-2zM15 11h.01",
  gem: "M6 3h12l3 6-9 12L3 9zM3 9h18M9 3l3 6 3-6",
  flag: "M5 21V4M5 4h12l-2 4 2 4H5",
  spark: "M12 2v6M12 16v6M2 12h6M16 12h6M5 5l4 4M15 15l4 4M5 19l4-4M15 9l4-4",
  home: "M3 11l9-8 9 8M5 9v11h14V9",
  // Game menu actions (hudGameMenu)
  play: "M8 5v14l11-7z",
  keyboard: "M3 7h18v11H3zM6 10h1M9 10h1M12 10h1M15 10h1M18 10h0M7 14h10",
  help: "M12 3a9 9 0 110 18 9 9 0 010-18zM9.5 9.5a2.5 2.5 0 015 0c0 1.8-2.5 2-2.5 4M12 17v.01",
  logout: "M10 4H5v16h5M14 8l4 4-4 4M18 12H9",
  power: "M12 3v8M6.3 6.3a8 8 0 1011.4 0",
  // Arrows / misc
  chevronDown: "M6 9l6 6 6-6",
  chevronUp: "M6 15l6-6 6 6",
  chevronLeft: "M15 6l-6 6 6 6",
  chevronRight: "M9 6l6 6-6 6",
  arrowRight: "M5 12h12M13 7l5 5-5 5",
  refresh: "M20 11a8 8 0 10-2.3 5.7M20 4v7h-7",
  sort: "M7 4v16M3 8l4-4 4 4M17 20V4M13 16l4 4 4-4",
  filter: "M3 5h18l-7 8v6l-4 2v-8z",
} as const;

export type IconName = keyof typeof ICONS;

/** One SVG element (namespaced) with attributes, for drawn widgets (rings, cooldown pies, charts). */
export function svgNode<K extends keyof SVGElementTagNameMap>(tag: K, attrs: Record<string, string | number> = {}): SVGElementTagNameMap[K] {
  const n = document.createElementNS(NS, tag);
  for (const k in attrs) n.setAttribute(k, String(attrs[k]));
  return n;
}

/** A stroke icon: an ICONS name or raw path data (constant, written in code). `cls` defaults to "ic" (16 px,
 *  currentColor, styles.ts); pass "" to style it yourself. */
export function svg(icon: IconName | (string & {}), cls = "ic", viewBox = "0 0 24 24"): SVGSVGElement {
  const s = svgNode("svg", { viewBox, "aria-hidden": "true" });
  if (cls) s.setAttribute("class", cls);
  s.append(svgNode("path", { d: (ICONS as Record<string, string>)[icon] ?? icon }));
  return s;
}

// — Menu emblems —
// The windows' emblems are painted pictures: play/menu-icons/<file>.png, 72 x 72 RGBA (cut from a black background, the
// black keyed out, 2026-10-17), shown at 22-34 px. Keys: the menu's window names (MENU_WINDOWS) and help.
const PICTURES: Record<string, string> = {
  Bag: "bag", Character: "character", Skills: "skills", Quests: "quests", Guild: "guild", Group: "group",
  Friends: "friends", Mail: "mail", "Game shop": "game-shop", "World map": "world-map", Options: "options",
  Hero: "hero", Ranking: "ranking", Territories: "territories", Creatures: "creatures", Mount: "mount",
  Fishing: "fishing", Mentor: "mentor", Relationship: "relationship", "Key bindings": "key-bindings",
  help: "help",
};
PICTURES.gear = PICTURES.Options!;
PICTURES.keys = PICTURES["Key bindings"]!;

// The action emblems (copied from play/hudMenuIcons.ts, 2026-10-16): small illustrated emblems on a 32 x 32 grid built
// from filled shapes with gradients (metal, gold, wood, paper, red and the theme's accent, so they follow the HUD
// colour), a dark outline and a light edge highlight. The gradients live once in a hidden <svg> (ICON_DEFS,
// installIconDefs) inside the UI root; the icons reference them by id. Keys: resume / logout / exit.

/** Shape helpers: class = fill (ac accent, mt metal, gd gold, wd wood, pp paper, rd red, dk dark) + "o" for the outline. */
const P = (d: string, c: string) => `<path class="${c}" d="${d}"/>`;
const C = (x: number, y: number, r: number, c: string) => `<circle class="${c}" cx="${x}" cy="${y}" r="${r}"/>`;
const E = (x: number, y: number, rx: number, ry: number, rot: number, c: string) => `<ellipse class="${c}" cx="${x}" cy="${y}" rx="${rx}" ry="${ry}" transform="rotate(${rot} ${x} ${y})"/>`;

const ART: Record<string, string> = {
  Crafting:
    P("M3.5 15h17.5c0 3 3 4.4 7 4.4v1.8H21l-1 3h3v4.3H9v-4.3h3l-1-3c-4.4 0-7.5-2.6-7.5-6.2z", "mt o") + P("M5 16.5h15", "hl") +
    P("M21 5.3l1.8 1.8-9.4 9.4-1.8-1.8z", "wd o") + P("M20.2 1.4l6.4 6.4-3.1 3.1-6.4-6.4z", "mt o") +
    C(24.5, 13.4, 1, "spark") + C(27.6, 11.4, .8, "spark") + C(22.4, 10.8, .7, "spark"),
  // Actions
  resume: C(16, 16, 12.5, "ac o") + P("M8.6 10.4a9 9 0 0 1 6-4.6", "hl") + P("M13 10.2l9.4 5.8-9.4 5.8z", "wh o"),
  logout:
    P("M5 3.5h14v25H5z", "wd o") + P("M7.6 6h8.8v20H7.6z", "sh") + C(15, 16.6, 1.1, "gd") +
    P("M17 16h11M24.2 11.8L28.4 16l-4.2 4.2", "arrow-o") + P("M17 16h11M24.2 11.8L28.4 16l-4.2 4.2", "arrow"),
  exit: P("M10.2 8.3a10.2 10.2 0 1 0 11.6 0M16 3.6v11.2", "pw-o") + P("M10.2 8.3a10.2 10.2 0 1 0 11.6 0M16 3.6v11.2", "pw"),
};

/** Names with an emblem. */
export const MENU_ICON_KEYS: readonly string[] = [...Object.keys(PICTURES), ...Object.keys(ART)];

/** The emblem for a window name or action key as an element (class "gmi"), or null (the caller falls back to a stroke
 *  icon). A picture is an <img> of a fixed file; ART markup is constant, so innerHTML is fine there. ART needs
 *  installIconDefs() in the same root. */
export function menuIcon(key: string): Element | null {
  const pic = PICTURES[key];
  if (pic) {
    const img = document.createElement("img");
    img.className = "gmi";
    img.src = webUrl(`/play/menu-icons/${pic}.png`);
    img.alt = "";
    img.draggable = false;
    return img;
  }
  const art = ART[key];
  if (!art) return null;
  const s = svgNode("svg", { class: "gmi", viewBox: "0 0 32 32", "aria-hidden": "true" });
  s.innerHTML = art; // constant markup from ART, never data
  return s;
}

/** The shared gradients (put in the root once, hidden). */
export const ICON_DEFS = `<svg width="0" height="0" style="position: absolute" aria-hidden="true"><defs>
<linearGradient id="gmi-ac" x1="0" y1="0" x2="0" y2="1"><stop offset="0" style="stop-color: color-mix(in srgb, var(--ac) 45%, #fff)"/><stop offset=".55" style="stop-color: var(--ac)"/><stop offset="1" style="stop-color: color-mix(in srgb, var(--ac) 55%, #000)"/></linearGradient>
<linearGradient id="gmi-dkac" x1="0" y1="0" x2="0" y2="1"><stop offset="0" style="stop-color: color-mix(in srgb, var(--ac) 45%, #000)"/><stop offset="1" style="stop-color: color-mix(in srgb, var(--ac) 20%, #000)"/></linearGradient>
<linearGradient id="gmi-mt" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#f6f9fd"/><stop offset=".5" stop-color="#c3ccd9"/><stop offset="1" stop-color="#7b879a"/></linearGradient>
<linearGradient id="gmi-gd" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff0b0"/><stop offset=".5" stop-color="#f2c14c"/><stop offset="1" stop-color="#a86e12"/></linearGradient>
<linearGradient id="gmi-wd" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#d9995c"/><stop offset="1" stop-color="#6e3f1c"/></linearGradient>
<linearGradient id="gmi-pp" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fbf3dc"/><stop offset="1" stop-color="#cdb684"/></linearGradient>
<linearGradient id="gmi-rd" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ff9a8a"/><stop offset=".5" stop-color="#e0453a"/><stop offset="1" stop-color="#8f1c16"/></linearGradient>
</defs></svg>`;

/** Icon styles (scoped to .gmi). */
export const ICON_CSS = `
.hudm img.gmi { object-fit: contain; user-select: none; pointer-events: none; }
.hudm svg.gmi { fill: none; stroke: none; overflow: visible; filter: drop-shadow(0 1px 1px rgba(0,0,0,.7)); }
.hudm svg.gmi .o { stroke: #05080e; stroke-width: 1.3; stroke-linejoin: round; stroke-linecap: round; }
.hudm svg.gmi .ac { fill: url(#gmi-ac); } .hudm svg.gmi .dkac { fill: url(#gmi-dkac); } .hudm svg.gmi .mt { fill: url(#gmi-mt); }
.hudm svg.gmi .gd { fill: url(#gmi-gd); } .hudm svg.gmi .wd { fill: url(#gmi-wd); } .hudm svg.gmi .pp { fill: url(#gmi-pp); }
.hudm svg.gmi .rd { fill: url(#gmi-rd); } .hudm svg.gmi .dk { fill: #0b111c; } .hudm svg.gmi .wh { fill: #fff; }
.hudm svg.gmi .sh { fill: rgba(0,0,0,.16); } .hudm svg.gmi .lt { fill: rgba(255,255,255,.14); }
.hudm svg.gmi .kt { fill: rgba(255,255,255,.28); }
.hudm svg.gmi .hl { fill: none; stroke: #fff; stroke-opacity: .7; stroke-width: 1.1; stroke-linecap: round; }
.hudm svg.gmi .ln { fill: none; stroke: #05080e; stroke-width: 1.2; stroke-linecap: round; }
.hudm svg.gmi .ln.f { stroke-opacity: .45; }
.hudm svg.gmi .rim { fill: none; stroke: #fff; stroke-opacity: .35; stroke-width: 1; }
.hudm svg.gmi .route { fill: none; stroke: #b3261e; stroke-width: 1.5; stroke-dasharray: 2.2 1.8; stroke-linecap: round; }
.hudm svg.gmi .mark { fill: none; stroke: #b3261e; stroke-width: 1.8; stroke-linecap: round; }
.hudm svg.gmi .mane { fill: none; stroke: #3a1f0c; stroke-width: 2.2; stroke-linecap: round; }
.hudm svg.gmi .tassel { fill: none; stroke: #f2c14c; stroke-width: 1.5; }
.hudm svg.gmi .ring-o { fill: none; stroke: #05080e; stroke-width: 4.8; }
.hudm svg.gmi .ring { fill: none; stroke: url(#gmi-gd); stroke-width: 2.6; }
.hudm svg.gmi .handle-o { fill: none; stroke: #05080e; stroke-width: 3.6; stroke-linecap: round; }
.hudm svg.gmi .handle { fill: none; stroke: url(#gmi-gd); stroke-width: 1.8; stroke-linecap: round; }
.hudm svg.gmi .star { fill: #fff; fill-opacity: .9; }
.hudm svg.gmi .spark { fill: #ffd76a; filter: drop-shadow(0 0 1.5px #ffb020); }
.hudm svg.gmi .bub { fill: none; stroke: #fff; stroke-opacity: .7; stroke-width: .9; }
.hudm svg.gmi .arrow-o { fill: none; stroke: #05080e; stroke-width: 4.6; stroke-linecap: round; stroke-linejoin: round; }
.hudm svg.gmi .arrow { fill: none; stroke: url(#gmi-ac); stroke-width: 2.6; stroke-linecap: round; stroke-linejoin: round; }
.hudm svg.gmi .pw-o { fill: none; stroke: #05080e; stroke-width: 5.4; stroke-linecap: round; }
.hudm svg.gmi .pw { fill: none; stroke: url(#gmi-rd); stroke-width: 3.2; stroke-linecap: round; }
`;

/** Puts the emblem gradients and styles into a UI root (Layers does it once per runtime). */
export function installIconDefs(root: HTMLElement): void {
  injectCss("core-icons", ICON_CSS);
  if (root.querySelector(":scope > .gmi-defs")) return;
  const holder = document.createElement("div");
  holder.className = "gmi-defs";
  holder.setAttribute("aria-hidden", "true");
  holder.style.cssText = "position: absolute; width: 0; height: 0; overflow: hidden";
  holder.innerHTML = ICON_DEFS; // constant markup
  root.append(holder);
}

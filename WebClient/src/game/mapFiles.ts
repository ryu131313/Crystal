// Map files: fetch + parse once (versioned and compressed by the relay: a 12.7 MB map crosses the wire as ~0.6 MB), and
// warm the selected character's last map on the select screen (after WoOOLcraft's select-screen warm, load-time item
// LT6): the file, its parse and the index of every library its cells use, so S.StartGame finds them ready.
// The last map per character name lives in localStorage ("crystal.lastMap.<name>"); the desktop has no equivalent.

import { readMap, type MapCells } from "../map/mapReader.ts";
import { assetUrl, fetchAsset } from "../assets/manifest.ts";
import { MAP_LIBS, libAsync } from "./libraries.ts";

const KEEP = 2;
const parsed = new Map<string, Promise<MapCells>>();
let playing = "";

/** The parsed map (cached: the last KEEP maps). Rejects when the file is missing or unreadable. */
export function mapCells(fileName: string): Promise<MapCells> {
  let p = parsed.get(fileName);
  if (p) {
    parsed.delete(fileName); // most recent last
    parsed.set(fileName, p);
    return p;
  }
  p = (async () => {
    const url = await assetUrl("map", `${fileName}.map`);
    if (!url) throw new Error("not in the client folder");
    const res = await fetchAsset(url);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return readMap(new Uint8Array(await res.arrayBuffer()));
  })();
  p.catch(() => { if (parsed.get(fileName) === p) parsed.delete(fileName); });
  parsed.set(fileName, p);
  while (parsed.size > KEEP) parsed.delete(parsed.keys().next().value!);
  return p;
}

/** SelectScene: the character about to play (its map is remembered under this name). */
export function setPlayingCharacter(name: string): void {
  playing = name;
}

/** GameScene map load: remember it for the next select screen. */
export function rememberMap(fileName: string): void {
  if (!playing) return;
  try { localStorage.setItem(`crystal.lastMap.${playing}`, fileName); } catch { /* storage blocked */ }
}

/** Select screen: fetch + parse the character's last map and open (index only) every library its cells use. */
export function warmCharacter(name: string): void {
  let fileName: string | null = null;
  try { fileName = localStorage.getItem(`crystal.lastMap.${name}`); } catch { /* storage blocked */ }
  if (!fileName) return;
  mapCells(fileName).then((m) => {
    const used = new Set<number>();
    for (const column of [m.backIndex, m.middleIndex, m.frontIndex]) for (const i of column) used.add(i);
    for (const i of used) {
      const path = i >= 0 ? MAP_LIBS[i] : null;
      if (path) void libAsync(path);
    }
  }).catch(() => { /* the game reports it when it loads the map */ });
}

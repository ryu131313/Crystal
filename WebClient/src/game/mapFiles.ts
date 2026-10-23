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

// Generated dungeon maps (S.DungeonMap) have no file on the relay: their parts arrive before the map change, and the
// inflated bytes are served from here (desktop: GeneratedMaps in MapCode.cs).
const generatedParts = new Map<string, (Uint8Array | undefined)[]>();
const generated = new Map<string, Promise<Uint8Array>>();

/** S.DungeonMap: keep the part; when every part is in, inflate the map (async; mapCells waits for it). */
export function addGeneratedPart(p: { FileName: string; Part: number; Parts: number; Data: Uint8Array }): void {
  // The name becomes part of the map's cache key and nothing else; still, keep to what the server makes.
  if (!/^[A-Za-z0-9]{1,64}$/.test(p.FileName) || p.Parts < 1 || p.Parts > 64 || p.Part >= p.Parts) return;
  let parts = generatedParts.get(p.FileName);
  if (!parts || parts.length !== p.Parts) generatedParts.set(p.FileName, parts = new Array(p.Parts).fill(undefined));
  parts[p.Part] = p.Data;
  if (parts.some((x) => !x)) return;
  generatedParts.delete(p.FileName);
  const gzipped = new Blob(parts.map((x) => new Uint8Array(x!)));
  generated.set(p.FileName, new Response(gzipped.stream().pipeThrough(new DecompressionStream("gzip"))).arrayBuffer()
    .then((b) => new Uint8Array(b)));
  parsed.delete(p.FileName); // a new map under this name replaces any older parse
  while (generated.size > KEEP) generated.delete(generated.keys().next().value!);
}

/** The parsed map (cached: the last KEEP maps). Rejects when the file is missing or unreadable. */
export function mapCells(fileName: string): Promise<MapCells> {
  let p = parsed.get(fileName);
  if (p) {
    parsed.delete(fileName); // most recent last
    parsed.set(fileName, p);
    return p;
  }
  p = (async () => {
    const own = generated.get(fileName);
    if (own) return readMap(await own);
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
  if (!playing || generated.has(fileName)) return; // a dungeon is gone by the next select screen
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

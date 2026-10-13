// Library registry + GPU sprite cache. Port of the Client/MirGraphics/MLibrary.cs `Libraries` table (paths under
// /data/, served read-only by WebRelay from the client folder). Drawing is synchronous (the frame loop), loading is
// not: `sprite()` returns undefined while an image is still loading (the frame skips it, like a texture still
// streaming in), null when it does not exist, else the GPU texture + placement.

import { CrystalLib, type LibImage } from "../assets/crystalLib.ts";
import type { WebGl2Renderer, GLTextureRef } from "../render/backend.ts";
import { Monster } from "../protocol/enums.generated.ts";
import { webUrl } from "../assets/root.ts";
import { loadManifest, assetVersion } from "../assets/manifest.ts";

export interface Sprite {
  tex: GLTextureRef | null; // null for a 0-size image (still has a size, e.g. a placeholder frame)
  width: number; height: number; x: number; y: number; shadowX: number; shadowY: number;
  /** One bit per pixel, set where alpha > 0 (MImage.Data's alpha, kept for MImage.VisiblePixel); only for the body
   *  libraries the mouse hit test reads (`hitMaskPath`), else null. */
  mask: Uint8Array | null;
}

/** The libraries MapObject.MouseOver tests (monster / NPC / player bodies): they keep a hit mask. */
const HIT_MASK_LIBS = /^(Monster|NPC|Pet|Gate|Siege|Flag|Dragon|CArmour|AArmour|ARArmour|Transform|TransformRide2)(\/|#)/;
const hitMaskPath = (key: string) => HIT_MASK_LIBS.test(key);

/** MImage.VisiblePixel: the pixel at (x, y) of the image is not fully transparent. */
export function visiblePixel(s: Sprite, x: number, y: number): boolean {
  if (!s.mask || x < 0 || y < 0 || x >= s.width || y >= s.height) return false;
  const i = y * s.width + x;
  return (s.mask[i >> 3]! & (1 << (i & 7))) !== 0;
}

// — Paths (Settings.cs + Libraries static ctor) —

export const MAP_LIBS: (string | null)[] = (() => {
  const p: (string | null)[] = new Array(400).fill(null);
  p[0] = "Map/WemadeMir2/Tiles";
  p[1] = "Map/WemadeMir2/Smtiles";
  p[2] = "Map/WemadeMir2/Objects";
  for (let i = 2; i < 28; i++) p[i + 1] = "Map/WemadeMir2/Objects" + i;
  p[90] = "Map/WemadeMir2/Objects_32bit";
  p[100] = "Map/ShandaMir2/Tiles";
  for (let i = 1; i < 10; i++) p[100 + i] = "Map/ShandaMir2/Tiles" + (i + 1);
  p[110] = "Map/ShandaMir2/SmTiles";
  for (let i = 1; i < 10; i++) p[110 + i] = "Map/ShandaMir2/SmTiles" + (i + 1);
  p[120] = "Map/ShandaMir2/Objects";
  for (let i = 1; i < 31; i++) p[120 + i] = "Map/ShandaMir2/Objects" + (i + 1);
  p[190] = "Map/ShandaMir2/AniTiles1";
  const mir3 = ["Tilesc", "Tiles30c", "Tiles5c", "Smtilesc", "Housesc", "Cliffsc", "Dungeonsc", "Innersc", "Furnituresc", "Wallsc", "smObjectsc", "Animationsc", "Object1c", "Object2c"];
  const state = ["", "wood/", "sand/", "snow/", "forest/"];
  for (let i = 0; i < state.length; i++) for (let j = 0; j < mir3.length; j++) p[200 + i * 15 + j] = "Map/WemadeMir3/" + state[i] + mir3[j];
  const state3 = ["", "wood", "sand", "snow", "forest"];
  for (let i = 0; i < state3.length; i++) for (let j = 0; j < mir3.length; j++) p[300 + i * 15 + j] = "Map/ShandaMir3/" + mir3[j] + state3[i];
  return p;
})();

const pad = (n: number, width: number) => String(n).padStart(width, "0");
export const LibPath = {
  map: (index: number) => MAP_LIBS[index] ?? null,
  cArmour: (n: number) => `CArmour/${pad(n, 2)}`,
  cHair: (n: number) => `CHair/${pad(n, 2)}`,
  cWeapon: (n: number) => `CWeapon/${pad(n, 2)}`,
  aArmour: (n: number) => `AArmour/${pad(n, 2)}`,
  aHair: (n: number) => `AHair/${pad(n, 2)}`,
  aWeaponR: (n: number) => `AWeapon/${pad(n, 2)} R`,
  aWeaponL: (n: number) => `AWeapon/${pad(n, 2)} L`,
  arArmour: (n: number) => `ARArmour/${pad(n, 2)}`,
  arHair: (n: number) => `ARHair/${pad(n, 2)}`,
  arWeapon: (n: number) => `ARWeapon/${pad(n, 2)}`,
  arWeaponS: (n: number) => `ARWeapon/${pad(n, 2)} S`,
  monster: (n: number) => `Monster/${pad(n, 3)}`,
  // Libraries.Mounts / Fishing / Transform / TransformMounts / TransformEffect (Settings.MountPath .. TransformEffectPath)
  mount: (n: number) => `Mount/${pad(n, 2)}`,
  fishing: (n: number) => `Fishing/${pad(n, 2)}`,
  transform: (n: number) => `Transform/${pad(n, 2)}`,
  transformMount: (n: number) => `TransformRide2/${pad(n, 2)}`,
  transformEffect: (n: number) => `TransformEffect/${pad(n, 2)}`,
  npc: (n: number) => `NPC/${pad(n, 2)}`,
  // Libraries.Pets / Gates / Siege / Flags (Settings.PetsPath .. FlagPath, InitLibrary "00") and Libraries.Dragon
  pet: (n: number) => `Pet/${pad(n, 2)}`,
  gate: (n: number) => `Gate/${pad(n, 2)}`,
  siege: (n: number) => `Siege/${pad(n, 2)}`,
  flag: (n: number) => `Flag/${pad(n, 2)}`,
  dragon: "Dragon",
  floorItems: "DNItems", // Libraries.FloorItems
  prguse: "Prguse",
  chrSel: "ChrSel",
} as const;

/** MonsterObject.Load's "Library" switch (repeated for the creatures at the top of SetAction): the BodyLibrary of a
 *  Monster image. null = no body (EvilMirBody; MonsterObject.Draw returns early). Use this for every monster picture. */
export function monsterLibPath(image: number): string | null {
  switch (image) {
    case Monster.EvilMir: case Monster.DragonStatue: return LibPath.dragon;
    case Monster.EvilMirBody: return null;
    case Monster.HellBomb1: case Monster.HellBomb2: case Monster.HellBomb3: return LibPath.monster(Monster.HellLord);
  }
  if (image >= Monster.Catapult && image <= Monster.CanonTrebuchet) return LibPath.siege(image - 940); // Catapult .. CanonTrebuchet
  if (image >= Monster.SabukGate && image <= Monster.FrozenDoor) return LibPath.gate(image - 950);     // SabukGate .. FrozenDoor
  if (image >= Monster.BabyPig && image <= Monster.MedicalRat) return LibPath.pet(image - 10000);      // BabyPig .. MedicalRat (IntelligentCreature)
  return LibPath.monster(image);
}

/** MonsterObject's IntelligentCreature cases (BabyPig .. MedicalRat = Monster 10000 .. 10014). */
export function isIntelligentCreature(image: number): boolean {
  return image >= Monster.BabyPig && image <= Monster.MedicalRat;
}

/** NPCObject.LoadLibrary: NPCs[Image], or Flags[Image - 1000] for 1000 .. 1099 (the desktop's "Image < NPCs.Length"
 *  is the folder size; here every image below 1000 is an NPC library and a missing file draws nothing). */
export function npcLibPath(image: number): string | null {
  if (image >= 1000 && image < 1100) return LibPath.flag(image - 1000);
  return image < 1000 ? LibPath.npc(image) : null;
}

// — Registry —

const libs = new Map<string, Promise<CrystalLib | null>>();
const opened = new Map<string, CrystalLib | null>();

function dataUrl(path: string): string {
  return webUrl("/data/") + path.split("/").map(encodeURIComponent).join("/") + ".Lib";
}

/** Opens through the asset manifest: one /libindex/ request with a version, nothing for a library it does not list. */
async function openLib(path: string): Promise<CrystalLib | null> {
  await loadManifest();
  const indexUrl = webUrl("/libindex/") + path.split("/").map(encodeURIComponent).join("/") + ".Lib";
  return CrystalLib.open(dataUrl(path), assetVersion("data", path + ".Lib"), indexUrl);
}

/** Opens (once) and returns the library, or undefined while its header is still loading. */
export function lib(path: string): CrystalLib | null | undefined {
  if (opened.has(path)) return opened.get(path);
  if (!libs.has(path)) {
    const p = openLib(path).catch((e) => {
      console.warn(`[lib] ${path}:`, e);
      return null;
    });
    libs.set(path, p);
    p.then((l) => opened.set(path, l));
  }
  return undefined;
}

export function libAsync(path: string): Promise<CrystalLib | null> {
  lib(path);
  return libs.get(path)!;
}

// — Sprite cache (GPU) —

let renderer: WebGl2Renderer | null = null;
const sprites = new Map<string, Sprite | null>();
const pending = new Set<string>();

/** A queued load. Draw-time misses go in `high` (one image each); warm-ups (`prefetch`) go in `low` (one batched block
 *  read each) and are capped at MAX_LOW_INFLIGHT, so a draw always finds a free slot. A draw that asks for an image an
 *  unstarted low job holds promotes that job to the front of `high`. */
interface Job { run: () => Promise<unknown>; started: boolean; keys: string[] }
const high: Job[] = [];
const low: Job[] = [];
/** Keys held by unstarted low jobs. */
const waiting = new Map<string, Job>();
let inflight = 0;
let lowInflight = 0;
const MAX_INFLIGHT = 24;
const MAX_LOW_INFLIGHT = 8;

export function attachRenderer(r: WebGl2Renderer): void {
  renderer = r;
}

function upload(img: LibImage, keepMask: boolean): Sprite {
  const tex = img.width > 0 && img.height > 0 && img.rgba.length ? renderer!.createRgbaTexture(img.rgba, img.width, img.height) : null;
  let mask: Uint8Array | null = null;
  if (keepMask && tex) {
    const n = img.width * img.height;
    mask = new Uint8Array((n + 7) >> 3);
    for (let i = 0; i < n; i++) if (img.rgba[i * 4 + 3]! > 0) mask[i >> 3]! |= 1 << (i & 7);
  }
  return { tex, width: img.width, height: img.height, x: img.x, y: img.y, shadowX: img.shadowX, shadowY: img.shadowY, mask };
}

function start(job: Job, fromLow: boolean): void {
  job.started = true;
  for (const k of job.keys) waiting.delete(k);
  inflight++;
  if (fromLow) lowInflight++;
  job.run().finally(() => {
    inflight--;
    if (fromLow) lowInflight--;
    pump();
  });
}

function pump(): void {
  while (inflight < MAX_INFLIGHT) {
    const h = high.shift();
    if (h) { if (!h.started) start(h, false); continue; }
    if (lowInflight >= MAX_LOW_INFLIGHT) return;
    const l = low.shift();
    if (!l) return;
    if (!l.started) start(l, true);
  }
}

/** Failed reads per sprite key: a failure is retried (the next sprite() asks again) up to MAX_FAILS, then the image
 *  counts as absent. Caching the first failure made a monster vanish on that frame for the whole session. */
const failures = new Map<string, number>();
const MAX_FAILS = 3;

/** Decode `index` (already fetched or fetching) and keep it on the GPU. */
function settle(l: CrystalLib, key: string, index: number): Promise<void> {
  return l.image(index).then((img) => {
    sprites.set(key, img && renderer ? upload(img, hitMaskPath(key)) : null);
    l.evict(index); // pixels live on the GPU now
  }, () => {
    const n = (failures.get(key) ?? 0) + 1;
    failures.set(key, n);
    if (n >= MAX_FAILS) sprites.set(key, null);
  }).finally(() => pending.delete(key));
}

/** The sprite, undefined while loading (kicks the load), null when absent. Call from the frame loop. */
export function sprite(path: string | null, index: number): Sprite | null | undefined {
  if (path === null || index < 0) return null;
  const key = path + "#" + index;
  const hit = sprites.get(key);
  if (hit !== undefined) return hit;
  if (pending.has(key)) {
    const w = waiting.get(key);
    if (w) { high.unshift(w); pump(); } // a warm-up holds it: start that block now
    return undefined;
  }
  const l = lib(path);
  if (l === null) { sprites.set(key, null); return null; }
  if (l === undefined) return undefined; // header loading; ask again next frame
  if (index >= l.count) { sprites.set(key, null); return null; }
  pending.add(key);
  high.push({ run: () => settle(l, key, index), started: false, keys: [] });
  pump();
  return undefined;
}

/** Warm-up (the WoOOLcraft web client's art warm): load these images of `path` at low priority, in one batched read
 *  per block (CrystalLib.preload), so they are resident before the frame loop asks. Already resident or loading
 *  images are skipped; safe to call repeatedly. */
export function prefetch(path: string | null, indices: readonly number[]): void {
  if (path === null || !indices.length) return;
  void libAsync(path).then((l) => {
    if (!l || !renderer) return;
    const want: number[] = [];
    const keys: string[] = [];
    for (const i of indices) {
      if (i < 0 || i >= l.count) continue;
      const key = path + "#" + i;
      if (sprites.has(key) || pending.has(key)) continue;
      pending.add(key);
      want.push(i);
      keys.push(key);
    }
    if (!want.length) return;
    const job: Job = {
      run: () => l.preload(want).then(() => Promise.all(want.map((i, n) => settle(l, keys[n]!, i)))),
      started: false,
      keys,
    };
    for (const k of keys) waiting.set(k, job);
    low.push(job);
    pump();
  });
}

/** Number of sprite loads still queued or in flight (loading indicator). */
export function loadingCount(): number {
  return pending.size;
}

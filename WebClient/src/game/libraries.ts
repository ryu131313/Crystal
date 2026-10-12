// Library registry + GPU sprite cache. Port of the Client/MirGraphics/MLibrary.cs `Libraries` table (paths under
// /data/, served read-only by WebRelay from the client folder). Drawing is synchronous (the frame loop), loading is
// not: `sprite()` returns undefined while an image is still loading (the frame skips it, like a texture still
// streaming in), null when it does not exist, else the GPU texture + placement.

import { CrystalLib, type LibImage } from "../assets/crystalLib.ts";
import type { WebGl2Renderer, GLTextureRef } from "../render/backend.ts";
import { Monster } from "../protocol/enums.generated.ts";

export interface Sprite {
  tex: GLTextureRef | null; // null for a 0-size image (still has a size, e.g. a placeholder frame)
  width: number; height: number; x: number; y: number; shadowX: number; shadowY: number;
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
  return "/data/" + path.split("/").map(encodeURIComponent).join("/") + ".Lib";
}

/** Opens (once) and returns the library, or undefined while its header is still loading. */
export function lib(path: string): CrystalLib | null | undefined {
  if (opened.has(path)) return opened.get(path);
  if (!libs.has(path)) {
    const p = CrystalLib.open(dataUrl(path)).catch((e) => {
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
let inflight = 0;
const queue: (() => void)[] = [];
const MAX_INFLIGHT = 24;

export function attachRenderer(r: WebGl2Renderer): void {
  renderer = r;
}

function upload(img: LibImage): Sprite {
  const tex = img.width > 0 && img.height > 0 && img.rgba.length ? renderer!.createRgbaTexture(img.rgba, img.width, img.height) : null;
  return { tex, width: img.width, height: img.height, x: img.x, y: img.y, shadowX: img.shadowX, shadowY: img.shadowY };
}

function pump(): void {
  while (inflight < MAX_INFLIGHT && queue.length) {
    inflight++;
    queue.shift()!();
  }
}

/** The sprite, undefined while loading (kicks the load), null when absent. Call from the frame loop. */
export function sprite(path: string | null, index: number): Sprite | null | undefined {
  if (path === null || index < 0) return null;
  const key = path + "#" + index;
  const hit = sprites.get(key);
  if (hit !== undefined) return hit;
  if (pending.has(key)) return undefined;
  const l = lib(path);
  if (l === null) { sprites.set(key, null); return null; }
  if (l === undefined) return undefined; // header loading; ask again next frame
  if (index >= l.count) { sprites.set(key, null); return null; }
  pending.add(key);
  queue.push(() => {
    l.image(index).then((img) => {
      sprites.set(key, img && renderer ? upload(img) : null);
      l.evict(index); // pixels live on the GPU now
    }).finally(() => {
      pending.delete(key);
      inflight--;
      pump();
    });
  });
  pump();
  return undefined;
}

/** Number of sprite loads still queued or in flight (loading indicator). */
export function loadingCount(): number {
  return pending.size;
}

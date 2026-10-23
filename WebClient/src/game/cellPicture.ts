// The minimap / big map picture of a generated dungeon (S.DungeonMap; desktop: Client/MirObjects/GeneratedMapPicture.cs).
// MMap.Lib has no art for a map made at run time (the server sends MiniMap = BigMap = 0), so the client draws one from the
// cells, `cellPixels` px a cell (1-3: the larger side stays within 2048 px):
//  - colours from the map's own art: each floor cell takes the average colour of its floor tile (Back, else Middle), the
//    walls the average of the wall objects (Front) beside the floor, the void a dark shade of that; lifted together when
//    the dungeon's art is too dark to read on a minimap;
//  - smooth edges: a floor field (field(): the walkable grid blurred wide, corridors and pillars put back), sampled
//    bilinearly per pixel and cut at FLOOR_EDGE (floor) and WALL_EDGE (the wall band, lighter towards the floor), each cut
//    anti-aliased over about two pixels. Floor colours are blended between cells.
// It stands in for the MMap image in the minimap and big map windows (classic and modern). The picture object is returned
// at once (cached on the parsed cells) and its canvas is painted when the tile colours are read (`ready`); the object URL
// (for <img>) is made after that and revoked when the map is dropped.

import type { MapCells } from "../map/mapReader.ts";
import { isGeneratedCells, isGeneratedMap, mapCells } from "./mapFiles.ts";
import { MAP_LIBS, libAsync } from "./libraries.ts";

export interface CellPicture {
  readonly canvas: HTMLCanvasElement;
  readonly width: number;
  readonly height: number;
  readonly cellPixels: number;
  /** Resolves once the canvas is painted. */
  readonly ready: Promise<void>;
}

type Rgb = [number, number, number];

const MAX_SIDE = 2048, MAX_CELL_PIXELS = 3;
const BLUR = 3, RESCUE = 1.8, CORRIDOR_LENGTH = 8, FLOOR_EDGE = 0.4, WALL_EDGE = 0.12, TARGET_LUMA = 140, MAX_LIFT = 2.5;
const DEFAULT_FLOOR: Rgb = [150, 138, 112], DEFAULT_WALL: Rgb = [72, 64, 54]; // a missing library; also the old flat look

const pictures = new WeakMap<MapCells, CellPicture | null>();
const urls: { canvas: HTMLCanvasElement; url: Promise<string | null> }[] = [];

const walkableAt = (m: MapCells, i: number) => (m.backImage[i]! & 0x20000000) === 0 && (m.frontImage[i]! & 0x8000) === 0;
const luma = (c: Rgb) => 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2];
const smoothStep = (e0: number, e1: number, x: number) => {
  const t = Math.min(1, Math.max(0, (x - e0) / (e1 - e0)));
  return t * t * (3 - 2 * t);
};

/** The average colour of a map tile's opaque pixels; null when unreadable. */
async function average(library: number, index: number): Promise<Rgb | null> {
  const path = library >= 0 ? MAP_LIBS[library] : null;
  if (!path || index < 0) return null;
  const img = await (await libAsync(path))?.image(index).catch(() => null);
  if (!img || !img.rgba.length) return null;
  const p = img.rgba;
  let r = 0, g = 0, b = 0, n = 0;
  for (let i = 0; i + 3 < p.length; i += 4) {
    if (p[i + 3] === 0) continue; // RGBA
    r += p[i]!; g += p[i + 1]!; b += p[i + 2]!; n++;
  }
  return n ? [r / n, g / n, b / n] : null;
}

function besideFloor(walk: Uint8Array, w: number, h: number, x: number, y: number): boolean {
  for (let dx = -1; dx <= 1; dx++)
    for (let dy = -1; dy <= 1; dy++) {
      const nx = x + dx, ny = y + dy;
      if ((dx || dy) && nx >= 0 && ny >= 0 && nx < w && ny < h && walk[nx * h + ny]) return true;
    }
  return false;
}

/** Per-cell floor colours (rgb triples, x * h + y; blocked cells: the average floor) and the wall colour, from the tiles
 *  the map draws, lifted together towards TARGET_LUMA. */
async function palette(m: MapCells, walk: Uint8Array): Promise<{ floor: Float32Array; wall: Rgb }> {
  const w = m.width, h = m.height;
  // Pass 1: which tile each floor cell / wall cell shows (-1 = none), as library * 2^24 + index keys.
  const key = (library: number, index: number) => (library < 0 || index < 0 ? -1 : library * 0x1000000 + index);
  const backKey = new Float64Array(w * h).fill(-1), midKey = new Float64Array(w * h).fill(-1);
  const wallKeys: number[] = [];
  const wanted = new Set<number>();
  for (let x = 0; x < w; x++)
    for (let y = 0; y < h; y++) {
      const i = x * h + y;
      if (walk[i]) {
        // MapControl.DrawFloor: a back tile is drawn at even cells and covers 2 x 2; else the cell's middle tile.
        const bi = (x & ~1) * h + (y & ~1);
        const back = m.backImage[bi]!;
        if (back !== 0 && m.backIndex[bi] !== -1) wanted.add(backKey[i] = key(m.backIndex[bi]!, (back & 0x1fffffff) - 1));
        if (m.middleIndex[i] !== -1) wanted.add(midKey[i] = key(m.middleIndex[i]!, m.middleImage[i]! - 1));
      } else if (besideFloor(walk, w, h, x, y) && m.frontIndex[i] !== -1 && m.frontIndex[i] !== 200) {
        const k = key(m.frontIndex[i]!, (m.frontImage[i]! & 0x7fff) - 1);
        if (k >= 0) { wallKeys.push(k); wanted.add(k); }
      }
    }
  wanted.delete(-1);
  // Pass 2: one read per distinct tile.
  const colours = new Map<number, Rgb | null>();
  await Promise.all([...wanted].map(async (k) => {
    colours.set(k, await average(Math.floor(k / 0x1000000), k % 0x1000000).catch(() => null));
  }));
  const floors: (Rgb | null)[] = new Array(w * h).fill(null);
  for (let i = 0; i < w * h; i++) if (walk[i]) floors[i] = colours.get(backKey[i]!) ?? colours.get(midKey[i]!) ?? null;

  const sum: Rgb = [0, 0, 0];
  let count = 0;
  for (const c of floors) if (c) { sum[0] += c[0]; sum[1] += c[1]; sum[2] += c[2]; count++; }
  const floorAverage: Rgb = count ? [sum[0] / count, sum[1] / count, sum[2] / count] : [...DEFAULT_FLOOR];
  const wsum: Rgb = [0, 0, 0];
  let wcount = 0;
  for (const k of wallKeys) { const c = colours.get(k); if (!c) continue; wsum[0] += c[0]; wsum[1] += c[1]; wsum[2] += c[2]; wcount++; }
  let wall: Rgb = wcount ? [wsum[0] / wcount, wsum[1] / wcount, wsum[2] / wcount] : [...DEFAULT_WALL];

  // Lift a dark dungeon so the minimap reads; keep the walls clearly darker than the floor.
  const lift = Math.min(MAX_LIFT, Math.max(1, TARGET_LUMA / Math.max(1, luma(floorAverage))));
  for (let k = 0; k < 3; k++) { floorAverage[k] *= lift; wall[k] *= lift; }
  if (luma(wall) > luma(floorAverage) * 0.6) {
    const s = (luma(floorAverage) * 0.55) / luma(wall);
    wall = [wall[0] * s, wall[1] * s, wall[2] * s];
  }

  const floor = new Float32Array(w * h * 3);
  for (let i = 0; i < w * h; i++) {
    const c = floors[i];
    for (let k = 0; k < 3; k++) floor[i * 3 + k] = c ? c[k]! * lift : floorAverage[k]!;
  }
  return { floor, wall };
}

/** A separable Gaussian blur (sigma in cells; outside the map counts as 0), x * h + y layout. */
function gauss(src: Float32Array, w: number, h: number, sigma: number): Float32Array {
  const r = Math.ceil(sigma * 3);
  const kernel = new Float32Array(r * 2 + 1);
  let sum = 0;
  for (let i = -r; i <= r; i++) sum += kernel[i + r] = Math.exp(-(i * i) / (2 * sigma * sigma));
  for (let i = 0; i < kernel.length; i++) kernel[i]! /= sum;
  const across = new Float32Array(w * h), out = new Float32Array(w * h);
  for (let x = 0; x < w; x++)
    for (let y = 0; y < h; y++) {
      let a = 0;
      for (let i = -r; i <= r; i++) if (x + i >= 0 && x + i < w) a += src[(x + i) * h + y]! * kernel[i + r]!;
      across[x * h + y] = a;
    }
  for (let x = 0; x < w; x++)
    for (let y = 0; y < h; y++) {
      let a = 0;
      for (let i = -r; i <= r; i++) if (y + i >= 0 && y + i < h) a += across[x * h + y + i]! * kernel[i + r]!;
      out[x * h + y] = a;
    }
  return out;
}

/** A mask of the 8-connected groups of set cells whose bounds (x0, y0, x1, y1) `keep` accepts. */
function components(set: Uint8Array, w: number, h: number, keep: (x0: number, y0: number, x1: number, y1: number) => boolean): Float32Array {
  const mask = new Float32Array(w * h), seen = new Uint8Array(w * h);
  const stack: number[] = [], group: number[] = [];
  for (let s = 0; s < w * h; s++) {
    if (!set[s] || seen[s]) continue;
    group.length = 0;
    seen[s] = 1;
    stack.push(s);
    let x0 = Infinity, y0 = Infinity, x1 = -1, y1 = -1;
    while (stack.length) {
      const c = stack.pop()!, x = Math.floor(c / h), y = c % h;
      group.push(c);
      x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y);
      for (let dx = -1; dx <= 1; dx++)
        for (let dy = -1; dy <= 1; dy++) {
          const nx = x + dx, ny = y + dy, n = nx * h + ny;
          if (nx < 0 || ny < 0 || nx >= w || ny >= h || !set[n] || seen[n]) continue;
          seen[n] = 1;
          stack.push(n);
        }
    }
    if (keep(x0, y0, x1, y1)) for (const c of group) mask[c] = 1;
  }
  return mask;
}

/** The floor field the picture is cut from (1 = floor, 0 = deep in the wall). The walkable grid is blurred wide (BLUR
 *  cells), which smooths away the saw-teeth that wall pieces leave on diagonal walls (a tooth every 8 cells, up to 7
 *  wide). A wide blur also fades what is thinner than it, so two kinds of shape are put back sharp (blurred 1 cell):
 *  corridors, i.e. walkable cells the wide blur dropped that run at least CORRIDOR_LENGTH cells (the cut-off tips of
 *  teeth are shorter), and pillars, i.e. blocked cells that do not reach the map's edge. */
function field(walk: Uint8Array, w: number, h: number): Float32Array {
  const f = gauss(Float32Array.from(walk), w, h, BLUR);
  const lost = new Uint8Array(w * h), blocked = new Uint8Array(w * h);
  for (let i = 0; i < w * h; i++) { lost[i] = walk[i] && f[i]! < FLOOR_EDGE ? 1 : 0; blocked[i] = walk[i] ? 0 : 1; }
  const corridors = gauss(components(lost, w, h, (x0, y0, x1, y1) => Math.max(x1 - x0, y1 - y0) + 1 >= CORRIDOR_LENGTH), w, h, 1);
  const pillars = gauss(components(blocked, w, h, (x0, y0, x1, y1) => x0 > 0 && y0 > 0 && x1 < w - 1 && y1 < h - 1), w, h, 1);
  for (let i = 0; i < w * h; i++)
    f[i] = Math.min(Math.max(f[i]!, Math.min(1, corridors[i]! * RESCUE)), 1 - Math.min(1, pillars[i]! * RESCUE));
  return f;
}

async function paint(m: MapCells, canvas: HTMLCanvasElement, px: number): Promise<void> {
  const w = m.width, h = m.height;
  const walk = new Uint8Array(w * h);
  for (let i = 0; i < w * h; i++) walk[i] = walkableAt(m, i) ? 1 : 0;
  const fld = field(walk, w, h);
  const { floor, wall } = await palette(m, walk);
  const voidC: Rgb = [wall[0] * 0.2, wall[1] * 0.2, wall[2] * 0.2];

  const W = w * px, H = h * px;
  const image = new ImageData(W, H);
  const d = image.data;
  const F = (x: number, y: number) => (x < 0 || y < 0 || x >= w || y >= h ? 0 : fld[x * h + y]!);
  const C = (x: number, y: number, k: number) => floor[(Math.min(w - 1, Math.max(0, x)) * h + Math.min(h - 1, Math.max(0, y))) * 3 + k]!;
  const aa = 0.15 / px; // about a pixel either side of each cut at the wide blur's slope
  for (let py = 0; py < H; py++) {
    const v = (py + 0.5) / px - 0.5, y0 = Math.floor(v), fy = v - y0;
    for (let pxl = 0; pxl < W; pxl++) {
      const u = (pxl + 0.5) / px - 0.5, x0 = Math.floor(u), fx = u - x0;
      const f = (F(x0, y0) * (1 - fx) + F(x0 + 1, y0) * fx) * (1 - fy) + (F(x0, y0 + 1) * (1 - fx) + F(x0 + 1, y0 + 1) * fx) * fy;
      const wallT = smoothStep(WALL_EDGE - aa, WALL_EDGE + aa, f);
      const floorT = smoothStep(FLOOR_EDGE - aa, FLOOR_EDGE + aa, f);
      const shade = 0.75 + 0.25 * Math.min(1, Math.max(0, (f - WALL_EDGE) / (FLOOR_EDGE - WALL_EDGE)));
      const o = (py * W + pxl) * 4;
      for (let k = 0; k < 3; k++) {
        let c = voidC[k]! + (wall[k]! * shade - voidC[k]!) * wallT;
        if (floorT > 0) {
          const fc = (C(x0, y0, k) * (1 - fx) + C(x0 + 1, y0, k) * fx) * (1 - fy) + (C(x0, y0 + 1, k) * (1 - fx) + C(x0 + 1, y0 + 1, k) * fx) * fy;
          c += (fc - c) * floorT;
        }
        d[o + k] = c; // Uint8ClampedArray rounds and clamps
      }
      d[o + 3] = 255;
    }
  }
  canvas.getContext("2d")!.putImageData(image, 0, 0);
}

function create(m: MapCells): CellPicture | null {
  const w = m.width, h = m.height;
  if (w <= 0 || h <= 0) return null;
  const px = Math.min(MAX_CELL_PIXELS, Math.max(1, Math.floor(MAX_SIDE / Math.max(w, h))));
  if (w * px > MAX_SIDE || h * px > MAX_SIDE) return null; // larger than any generator profile makes
  const canvas = document.createElement("canvas");
  canvas.width = w * px;
  canvas.height = h * px;
  const ready = paint(m, canvas, px).catch((e) => console.warn("[cellPicture]", e));
  return { canvas, width: canvas.width, height: canvas.height, cellPixels: px, ready };
}

/** The picture of these cells when they are a generated dungeon's, else null (a map with MMap art, or still loading).
 *  Its canvas is blank until `ready`. */
export function cellPicture(map: MapCells): CellPicture | null {
  if (!isGeneratedCells(map)) return null;
  let p = pictures.get(map);
  if (p === undefined) pictures.set(map, p = create(map));
  return p;
}

/** The painted picture of a generated map as an object URL (for an <img>), once its cells are parsed; null for other maps. */
export async function cellPictureUrl(fileName: string): Promise<{ url: string; width: number; height: number } | null> {
  if (!isGeneratedMap(fileName)) return null;
  const cells = await mapCells(fileName).catch(() => null);
  const pic = cells && cellPicture(cells);
  if (!pic) return null;
  await pic.ready;
  let entry = urls.find((u) => u.canvas === pic.canvas);
  if (!entry) {
    entry = { canvas: pic.canvas, url: new Promise((resolve) => pic.canvas.toBlob((b) => resolve(b ? URL.createObjectURL(b) : null))) };
    urls.push(entry);
    while (urls.length > 2) void urls.shift()!.url.then((u) => { if (u) URL.revokeObjectURL(u); }); // mapFiles keeps 2 maps
  }
  const url = await entry.url;
  return url ? { url, width: pic.width, height: pic.height } : null;
}

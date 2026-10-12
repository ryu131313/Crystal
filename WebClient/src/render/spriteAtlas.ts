// Lifted from WoOOLcraft src/WebClient/src/render/spriteAtlas.ts (2026-10-12) for the Crystal web client; the D3D11Backend it mirrors descends from Crystal's own.
// Sprite atlas allocator: where a sprite goes on a shared page. Consecutive sprites on one page are one batcher run (one draw
// call), where a texture per frame cost one bind+draw per quad (measured 1,600 quads: 801 fps as separate calls, 7,679 as one).
//
// A page is a square cut into horizontal SHELVES; a sprite takes the shelf it fits with the least wasted height, or opens a new
// one. Per-format geometry (`atlasGeometryFor`):
//   - BC1 / BC3: origin and size multiples of 4 (`compressedTexSubImage2D` writes whole blocks), 4-texel gutter, 4096 pages
//     where the GPU allows (2048 otherwise): compressed pages are cheap and fewer pages mean fewer draws;
//   - RGBA8: no alignment, 2-texel gutter, 1024 pages (decoded art is 4-8x the bytes); a sprite over half a page keeps its own texture.
// Growing pages (1024 -> 4096) and 2048 BC pages were measured and rejected (more draws). Extra memory is bounded by about one
// part-filled page per pool.
// The gutter is transparent so a sample rounding past an edge reads nothing. Freed space is not reused: a page is deleted when
// its last sprite is released.
//
// PURE: no GL, no DOM, no clock. `tools/atlas-selftest.ts` drives it; the GL half is `WebGl2Renderer`'s atlas methods.

/** One format's page shape. */
export interface AtlasGeometry {
  /** A page's side in texels. */
  readonly pageSize: number;
  /** Origin and size granularity. */
  readonly align: number;
  /** Transparent texels kept free after each sprite, to its right and below (a multiple of `align`). */
  readonly gutter: number;
  /** The largest side a sprite may have and still go on a page. */
  readonly maxSprite: number;
}

/** WebGL2's guaranteed minimum `MAX_TEXTURE_SIZE`: every GPU can hold a page this size. */
export const ATLAS_MIN_PAGE = 2048;

/** The page shape for a format, given the GPU's `MAX_TEXTURE_SIZE`. */
export function atlasGeometryFor(format: "RGBA8" | "BC1" | "BC3", maxTextureSize: number): AtlasGeometry {
  if (format === "RGBA8") return { pageSize: 1024, align: 1, gutter: 2, maxSprite: 512 };
  const pageSize = maxTextureSize >= 4096 ? 4096 : ATLAS_MIN_PAGE;
  return { pageSize, align: 4, gutter: 4, maxSprite: 1024 };
}

/** The cell a sprite of this allocation size takes on a page: aligned up, plus the gutter. */
export function atlasCell(g: AtlasGeometry, width: number, height: number): { w: number; h: number } {
  const up = (n: number): number => Math.ceil(n / g.align) * g.align;
  return { w: up(width) + g.gutter, h: up(height) + g.gutter };
}

/** Whether a sprite of this allocation size goes on a page of this geometry at all. */
export function atlasEligible(g: AtlasGeometry, width: number, height: number): boolean {
  return Number.isInteger(width) && Number.isInteger(height) && width > 0 && height > 0 && width <= g.maxSprite && height <= g.maxSprite;
}

interface Shelf { readonly y: number; readonly h: number; x: number }

/** One page's shelves. `place` returns the top-left of a free cell, or null when the page cannot hold it. */
export class ShelfPage {
  readonly size: number;
  private readonly shelves: Shelf[] = [];
  private top = 0;
  /** Sprites placed and not yet released. */
  live = 0;
  /** Texels covered by placed cells (the page's fill, for the census). */
  used = 0;

  constructor(size: number) {
    this.size = size;
  }

  /** Place a cell `w` x `h` (already aligned and guttered — `atlasCell`). Best fit: the open shelf whose height wastes least,
   *  never one more than a quarter (at least 8 texels) taller than the cell; otherwise a new shelf under the last. */
  place(w: number, h: number): { x: number; y: number } | null {
    if (w > this.size || h > this.size) return null;
    let best: Shelf | null = null;
    for (const s of this.shelves) {
      if (s.h < h || s.h - h > Math.max(8, h >> 2) || s.x + w > this.size) continue;
      if (best === null || s.h < best.h) best = s;
    }
    if (best === null) {
      if (this.top + h > this.size) return null;
      best = { y: this.top, h, x: 0 };
      this.shelves.push(best);
      this.top += h;
    }
    const at = { x: best.x, y: best.y };
    best.x += w;
    this.live++;
    this.used += w * h;
    return at;
  }

  /** One placed sprite released. True when the page holds none any more (the caller deletes it). */
  release(): boolean {
    this.live = Math.max(0, this.live - 1);
    return this.live === 0;
  }
}

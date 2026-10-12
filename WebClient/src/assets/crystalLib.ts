// Crystal .Lib reader (port of Client/MirGraphics/MLibrary.cs MLibrary.Initialize + MImage). Never loads a whole file:
//   header   i32 version (>= 2), i32 count, [v3: i32 frameSeek], count x i32 absolute image offsets
//   image    i16 w, h, x, y, shadowX, shadowY; u8 shadow (bit 7 = has mask); i32 length; GZIP'd BGRA32 (w*h*4)
//            [mask: i16 w, h, x, y; i32 maskLength; GZIP'd BGRA32 at the MAIN w x h, as MImage.CreateTexture]
//   frames   (v3) at frameSeek: i32 n, n x (u8 MirAction + Frame: 8 x i32 + 2 x bool)
// An image spans [offset[i], next higher offset) so one Range GET fetches header + payload (+ mask). The span end is
// the next offset in sorted order, else frameSeek (v3), else the file length.
// Only CrystalLib is needed (WoOOLcraft's mirLibrary.ts also reads Wemade/Shanda formats Crystal never ships as .Lib).

export interface LibImage {
  width: number; height: number; x: number; y: number; shadowX: number; shadowY: number; shadow: number;
  /** RGBA8, w*h*4 (converted from BGRA). Empty for a 0x0 image. */
  rgba: Uint8Array;
  mask: { width: number; height: number; x: number; y: number; rgba: Uint8Array } | null;
}

export interface LibFrame {
  start: number; count: number; skip: number; interval: number;
  effectStart: number; effectCount: number; effectSkip: number; effectInterval: number;
  reverse: boolean; blend: boolean;
}

export class LibFormatError extends Error {}

const IMAGE_HEADER = 17;
/** CrystalLib.preload: a run of images is split at a byte gap over PRELOAD_GAP or a size over PRELOAD_MAX. */
const PRELOAD_GAP = 64 * 1024;
const PRELOAD_MAX = 4 * 1024 * 1024;

import { gunzip } from "../protocol/binaryCodec.ts";
export { gunzip };

function bgraToRgbaInPlace(px: Uint8Array): Uint8Array {
  for (let i = 0; i < px.length; i += 4) {
    const b = px[i];
    px[i] = px[i + 2];
    px[i + 2] = b;
  }
  return px;
}

/** The bytes [start, endExclusive). Checked: the HTTP cache can answer a range request from a partial entry with a
 *  short 200 (seen live: a monster frame that then never drew), so a wrong length retries once past the cache. */
async function rangeGet(url: string, start: number, endExclusive: number, signal?: AbortSignal): Promise<Uint8Array> {
  for (let attempt = 0; ; attempt++) {
    const res = await fetch(url, { headers: { Range: `bytes=${start}-${endExclusive - 1}` }, signal, cache: attempt === 0 ? "default" : "no-store" });
    if (res.status !== 206 && res.status !== 200) throw new Error(`GET ${url} [${start}..${endExclusive}) -> ${res.status}`);
    const body = new Uint8Array(await res.arrayBuffer());
    // 200 = Range ignored: the whole file, sliced here
    const got = res.status === 200 ? (body.length >= endExclusive ? body.subarray(start, endExclusive) : null) : body.length === endExclusive - start ? body : null;
    if (got) return got;
    if (attempt > 0) throw new Error(`GET ${url} [${start}..${endExclusive}) -> ${res.status} with ${body.length} bytes`);
  }
}

export class CrystalLib {
  readonly url: string;
  readonly version: number;
  readonly count: number;
  readonly frames: ReadonlyMap<number, LibFrame> | null;
  private readonly offsets: Int32Array;
  private readonly spanEnd: Int32Array;
  private readonly images = new Map<number, Promise<LibImage | null>>();

  private constructor(url: string, version: number, offsets: Int32Array, spanEnd: Int32Array, frames: Map<number, LibFrame> | null) {
    this.url = url;
    this.version = version;
    this.count = offsets.length;
    this.offsets = offsets;
    this.spanEnd = spanEnd;
    this.frames = frames;
  }

  /** Opens a library: HEAD for the length, one Range for the header + offset table, one for the v3 frame table. Null when the file is absent (404), like a missing MLibrary. */
  static async open(url: string): Promise<CrystalLib | null> {
    const head = await fetch(url, { method: "HEAD" });
    if (head.status === 404) return null;
    if (!head.ok) throw new Error(`HEAD ${url} -> ${head.status}`);
    const length = Number(head.headers.get("content-length"));
    if (!(length >= 12)) throw new LibFormatError(`${url}: length ${length}`);

    let first = await rangeGet(url, 0, Math.min(length, 16));
    let dv = new DataView(first.buffer, first.byteOffset, first.byteLength);
    const version = dv.getInt32(0, true);
    const count = dv.getInt32(4, true);
    if (version < 2 || count < 0 || count > 1_000_000) throw new LibFormatError(`${url}: version ${version}, count ${count}`);
    const tableAt = version >= 3 ? 12 : 8;
    const frameSeek = version >= 3 ? dv.getInt32(8, true) : 0;
    const tableEnd = tableAt + count * 4;
    if (tableEnd > length) throw new LibFormatError(`${url}: offset table past EOF`);

    const table = await rangeGet(url, 0, tableEnd);
    dv = new DataView(table.buffer, table.byteOffset, table.byteLength);
    const offsets = new Int32Array(count);
    for (let i = 0; i < count; i++) offsets[i] = dv.getInt32(tableAt + i * 4, true);

    // Span end of each image = next greater offset (images are written in order, but sort to be safe).
    const order = Array.from(offsets.keys()).filter((i) => offsets[i] > 0).sort((a, b) => offsets[a] - offsets[b]);
    const spanEnd = new Int32Array(count);
    const tail = version >= 3 && frameSeek > 0 ? frameSeek : length;
    for (let k = 0; k < order.length; k++) {
      const i = order[k];
      let end = tail;
      for (let j = k + 1; j < order.length; j++) {
        if (offsets[order[j]] > offsets[i]) { end = offsets[order[j]]; break; }
      }
      spanEnd[i] = end;
    }

    let frames: Map<number, LibFrame> | null = null;
    if (version >= 3 && frameSeek > 0 && frameSeek + 4 <= length) {
      const ft = await rangeGet(url, frameSeek, length);
      const fv = new DataView(ft.buffer, ft.byteOffset, ft.byteLength);
      const n = fv.getInt32(0, true);
      if (n > 0 && 4 + n * 35 <= ft.length) {
        frames = new Map();
        let p = 4;
        for (let i = 0; i < n; i++) {
          const action = fv.getUint8(p); p += 1;
          const ints: number[] = [];
          for (let k = 0; k < 8; k++) { ints.push(fv.getInt32(p, true)); p += 4; }
          const reverse = fv.getUint8(p) !== 0, blend = fv.getUint8(p + 1) !== 0; p += 2;
          // FrameSet.Add keeps the first entry for an action (Dictionary.Add would throw; the editor never writes duplicates).
          if (!frames.has(action)) {
            frames.set(action, {
              start: ints[0], count: ints[1], skip: ints[2], interval: ints[3],
              effectStart: ints[4], effectCount: ints[5], effectSkip: ints[6], effectInterval: ints[7], reverse, blend,
            });
          }
        }
      }
    }
    return new CrystalLib(url, version, offsets, spanEnd, frames);
  }

  /** Decoded image (cached promise); null for an out-of-range, empty or zero-size entry. Rejects on a failed read or
   *  decode, and forgets it, so a later call tries again. */
  image(index: number): Promise<LibImage | null> {
    let p = this.images.get(index);
    if (!p) {
      p = this.load(index);
      this.track(index, p);
    }
    return p;
  }

  /** Cache `p` for `index`; a rejection drops the entry (the caller decides whether to retry). */
  private track(index: number, p: Promise<LibImage | null>): void {
    this.images.set(index, p);
    p.catch((e) => {
      console.warn(`[lib] ${this.url}#${index}:`, e);
      if (this.images.get(index) === p) this.images.delete(index);
    });
  }

  /** Forget a decoded image (its pixels; the GPU texture is owned by the caller). */
  evict(index: number): void {
    this.images.delete(index);
  }

  /** Whether image `index` has bytes to fetch (a 0 offset or a span shorter than the header is an absent image). */
  private hasSpan(index: number): boolean {
    return index >= 0 && index < this.count && this.offsets[index] > 0 && this.spanEnd[index] - this.offsets[index] >= IMAGE_HEADER;
  }

  /** Batched read for a warm-up: ONE Range GET per run of nearby images (a frame block: every direction of an action)
   *  instead of one per image. Seeds the image cache, so `image(i)` shares the fetch. A run closes at a byte gap over
   *  PRELOAD_GAP or a size over PRELOAD_MAX. Resolves when every run has settled (failures are logged per image). */
  preload(indices: Iterable<number>): Promise<void> {
    const want: number[] = [];
    for (const i of indices) if (!this.images.has(i) && this.hasSpan(i)) want.push(i);
    if (!want.length) return Promise.resolve();
    want.sort((a, b) => this.offsets[a]! - this.offsets[b]!);
    const runs: number[][] = [];
    let run: number[] = [], runAt = 0, runEnd = 0;
    for (const i of want) {
      const at = this.offsets[i]!, end = this.spanEnd[i]!;
      if (run.length && (at - runEnd > PRELOAD_GAP || Math.max(end, runEnd) - runAt > PRELOAD_MAX)) { runs.push(run); run = []; }
      if (!run.length) { runAt = at; runEnd = end; }
      run.push(i);
      runEnd = Math.max(runEnd, end);
    }
    runs.push(run);
    const all: Promise<unknown>[] = [];
    for (const r of runs) {
      let at = Infinity, end = 0;
      for (const i of r) { at = Math.min(at, this.offsets[i]!); end = Math.max(end, this.spanEnd[i]!); }
      const bytes = rangeGet(this.url, at, end);
      all.push(bytes.catch(() => undefined));
      for (const i of r) this.track(i, bytes.then((b) => this.parse(i, b.subarray(this.offsets[i]! - at, this.spanEnd[i]! - at))));
    }
    return Promise.all(all).then(() => undefined);
  }

  private async load(index: number): Promise<LibImage | null> {
    if (!this.hasSpan(index)) return null;
    return this.parse(index, await rangeGet(this.url, this.offsets[index]!, this.spanEnd[index]!));
  }

  /** MImage: one image from its span bytes (header + payload + mask). */
  private async parse(index: number, span: Uint8Array): Promise<LibImage | null> {
    const dv = new DataView(span.buffer, span.byteOffset, span.byteLength);
    const width = dv.getInt16(0, true), height = dv.getInt16(2, true);
    const x = dv.getInt16(4, true), y = dv.getInt16(6, true);
    const shadowX = dv.getInt16(8, true), shadowY = dv.getInt16(10, true);
    const shadow = dv.getUint8(12);
    const length = dv.getInt32(13, true);
    if (width <= 0 || height <= 0 || length <= 0) return { width: Math.max(0, width), height: Math.max(0, height), x, y, shadowX, shadowY, shadow, rgba: new Uint8Array(0), mask: null };
    if (IMAGE_HEADER + length > span.length) throw new LibFormatError(`image ${index}: payload ${length} past span ${span.length}`);
    const rgba = bgraToRgbaInPlace(await gunzip(span.subarray(IMAGE_HEADER, IMAGE_HEADER + length)));
    if (rgba.length < width * height * 4) throw new LibFormatError(`image ${index}: inflated ${rgba.length} < ${width * height * 4}`);

    let mask: LibImage["mask"] = null;
    if (shadow >> 7 === 1) {
      const m = IMAGE_HEADER + length;
      if (m + 12 <= span.length) {
        const mw = dv.getInt16(m, true), mh = dv.getInt16(m + 2, true), mx = dv.getInt16(m + 4, true), my = dv.getInt16(m + 6, true);
        const mLen = dv.getInt32(m + 8, true);
        if (mLen > 0 && m + 12 + mLen <= span.length) {
          // MImage.CreateTexture decodes the mask into a MAIN-size (Width x Height) texture.
          const px = bgraToRgbaInPlace(await gunzip(span.subarray(m + 12, m + 12 + mLen)));
          mask = { width: mw, height: mh, x: mx, y: my, rgba: px };
        }
      }
    }
    return { width, height, x, y, shadowX, shadowY, shadow, rgba: rgba.length === width * height * 4 ? rgba : rgba.subarray(0, width * height * 4), mask };
  }
}

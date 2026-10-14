// Weapon posing for the scene lab's Weapon tester: port of tools/weaponpose (weaponpose.py + pose.py). Measures the
// reference weapon's line (pommel -> tip) in every frame of a CWeapon library and bends one upright picture onto each
// line, pommel pinned, foreshortened with the line, flipped across its axis in the mirrored directions. The result is
// written as a v2 .Lib in memory (CrystalLib.fromBytes), so it can be tested and downloaded. Lab-only.

import type { CrystalLib, LibImage } from "../src/assets/crystalLib.ts";

/** Client/MirObjects/Frames.cs FrameSet.Player: the actions inside one 416-frame gender block (start, count). */
const ACTIONS: [number, number][] = [[0, 4], [32, 6], [80, 6], [128, 1], [136, 6], [184, 6], [232, 8], [296, 6], [344, 2], [360, 3], [384, 4]];
export const DIRS = ["Up", "UpRight", "Right", "DownRight", "Down", "DownLeft", "Left", "UpLeft"];
const GENDER_BLOCK = 416;
const BINS = 12;
const SS = 4;            // supersamples per axis per output pixel
const ALPHA_CUT = 0.4;   // coverage for an opaque output pixel (the client uses hard alpha)
const KEY_TOL = 14;      // max per-channel difference from the background colour that still counts as background

/** weaponpose.py is_glow: a dark, strongly coloured pixel (a glow's halo on black); dark greys (handles) are not. */
function isGlow(r: number, g: number, b: number, glow: number): boolean {
  const hi = Math.max(r, g, b);
  return hi > 0 && hi < glow && (hi - Math.min(r, g, b)) / hi > 0.5;
}

type P = [number, number];
type Rgb = [number, number, number];
interface Img { w: number; h: number; x: number; y: number; rgba: Uint8Array }
interface Measure { cx: number; cy: number; ux: number; uy: number; t0: number; t1: number; length: number; prof: (Rgb | null)[]; guard: number }
interface Pose { pommel: P; tip: P; conf: number }
export interface Reference { count: number; frames: (Img | null)[]; genders: { poses: Map<number, Pose>; Lm: number }[] }
export interface PoseOptions { length: number; width: number; mirror: ReadonlySet<number> }

/** pose.py measure: principal axis of the opaque pixels, colour profile along it, where the grey guard sits. */
function measure(im: Img): Measure | null {
  const xs: number[] = [], ys: number[] = [], cs: Rgb[] = [];
  for (let j = 0; j < im.h; j++) for (let i = 0; i < im.w; i++) {
    const o = (j * im.w + i) * 4;
    if (im.rgba[o + 3]! > 0) { xs.push(i + 0.5); ys.push(j + 0.5); cs.push([im.rgba[o]!, im.rgba[o + 1]!, im.rgba[o + 2]!]); }
  }
  const n = xs.length;
  if (n < 2) return null;
  let mx = 0, my = 0;
  for (let k = 0; k < n; k++) { mx += xs[k]!; my += ys[k]!; }
  mx /= n; my /= n;
  let sxx = 0, syy = 0, sxy = 0;
  for (let k = 0; k < n; k++) { const dx = xs[k]! - mx, dy = ys[k]! - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
  const ang = 0.5 * Math.atan2(2 * sxy, sxx - syy);
  const ux = Math.cos(ang), uy = Math.sin(ang);
  const proj = xs.map((x, k) => (x - mx) * ux + (ys[k]! - my) * uy);
  const t0 = Math.min(...proj) - 0.5, t1 = Math.max(...proj) + 0.5;
  const sr = new Float64Array(BINS), sg = new Float64Array(BINS), sb = new Float64Array(BINS), sn = new Float64Array(BINS);
  proj.forEach((t, k) => {
    const b = Math.min(BINS - 1, Math.floor(((t - t0) / (t1 - t0)) * BINS));
    sr[b] += cs[k]![0]; sg[b] += cs[k]![1]; sb[b] += cs[k]![2]; sn[b]++;
  });
  const prof = Array.from(sn, (c, b): Rgb | null => (c ? [sr[b]! / c, sg[b]! / c, sb[b]! / c] : null));
  let gw = 0, gs = 0;
  proj.forEach((t, k) => {
    const hi = Math.max(...cs[k]!), lo = Math.min(...cs[k]!);
    const grey = hi ? 1 - (hi - lo) / hi : 0;
    if (grey > 0.6) { gw += grey; gs += grey * t; }
  });
  const guard = gw ? (gs / gw - (t0 + t1) / 2) / ((t1 - t0) / 2) : 0;
  return { cx: mx, cy: my, ux, uy, t0, t1, length: t1 - t0, prof, guard };
}

function profDist(a: (Rgb | null)[], b: (Rgb | null)[]): number {
  let s = 0, c = 0;
  a.forEach((p, i) => { const q = b[i]; if (p && q) { s += (p[0] - q[0]) ** 2 + (p[1] - q[1]) ** 2 + (p[2] - q[2]) ** 2; c++; } });
  return c ? s / c : 1e9;
}

/** (pommel, tip); flip = the line runs tip -> pommel. */
function endpoints(m: Measure, flip: boolean): [P, P] {
  const a: P = [m.cx + m.ux * m.t0, m.cy + m.uy * m.t0], b: P = [m.cx + m.ux * m.t1, m.cy + m.uy * m.t1];
  return flip ? [b, a] : [a, b];
}

const dist = (a: P, b: P) => Math.hypot(a[0] - b[0], a[1] - b[1]);

/** weaponpose.py direction_of: the facing direction of a frame index. */
export function directionOf(idx: number): number {
  const local = idx % GENDER_BLOCK;
  for (const [start, n] of ACTIONS) if (local >= start && local < start + 8 * n) return Math.floor((local - start) / n);
  return 0;
}

/** weaponpose.py analyse_ref: per gender, the pommel / tip of every frame and the longest frame's length. */
export async function analyseReference(lib: CrystalLib): Promise<Reference> {
  const all: number[] = [];
  for (let i = 0; i < Math.min(lib.count, 2 * GENDER_BLOCK); i++) all.push(i);
  await lib.preload(all);
  const frames = await Promise.all(all.map((i) => lib.image(i).then(toImg, () => null)));
  const genders = [0, GENDER_BLOCK].map((goff) => {
    const ms = new Map<number, Measure>();
    for (const [start, n] of ACTIONS) for (let k = 0; k < 8 * n; k++) {
      const im = frames[goff + start + k];
      const m = im ? measure(im) : null;
      if (m) ms.set(goff + start + k, m);
    }
    let master = -1;
    for (const [i, m] of ms) if (master < 0 || m.length > ms.get(master)!.length) master = i;
    if (master < 0) return { poses: new Map<number, Pose>(), Lm: 1 };
    const mm = ms.get(master)!;
    // the guard (the brightest bin) sits nearer the pommel: run the master profile pommel -> tip
    const lum = mm.prof.map((c) => (c ? c[0] + c[1] + c[2] : 0));
    const ref = lum.indexOf(Math.max(...lum)) > lum.length / 2 ? [...mm.prof].reverse() : mm.prof;
    const poses = new Map<number, Pose>();
    for (const [i, m] of ms) {
      let flip: boolean, conf: number;
      if (Math.abs(m.guard) > 0.15) { flip = m.guard > 0; conf = Math.min(1, Math.abs(m.guard) * 2); }
      else {
        const fwd = profDist(m.prof, ref), rev = profDist(m.prof, [...ref].reverse());
        flip = rev < fwd; conf = (0.5 * Math.abs(fwd - rev)) / Math.max(fwd, rev, 1e-9);
      }
      const [pommel, tip] = endpoints(m, flip);
      poses.set(i, { pommel, tip, conf });
    }
    fixContinuity(poses, goff);
    return { poses, Lm: mm.length };
  });
  return { count: lib.count, frames, genders };
}

/** Low-confidence frames take the orientation whose pommel is nearest the most confident frame's pommel in the same
 *  action + direction. */
function fixContinuity(poses: Map<number, Pose>, goff: number): void {
  for (const [start, n] of ACTIONS) for (let d = 0; d < 8; d++) {
    const idxs: number[] = [];
    for (let k = 0; k < n; k++) if (poses.has(goff + start + d * n + k)) idxs.push(goff + start + d * n + k);
    if (!idxs.length) continue;
    const best = idxs.reduce((a, b) => (poses.get(b)!.conf > poses.get(a)!.conf ? b : a));
    const bp = poses.get(best)!.pommel;
    for (const i of idxs) {
      const p = poses.get(i)!;
      if (p.conf < 0.35 && dist(p.tip, bp) < dist(p.pommel, bp)) poses.set(i, { pommel: p.tip, tip: p.pommel, conf: p.conf });
    }
  }
}

function toImg(l: LibImage | null): Img | null {
  return l && l.width > 0 && l.height > 0 && l.rgba.length ? { w: l.width, h: l.height, x: l.x, y: l.y, rgba: l.rgba } : null;
}

/** weaponpose.py load_input: decode (any format the browser reads), drop a flat background connected to the edge,
 *  with `glow` also a glow's dark coloured halo (anywhere: it sits in pockets inside loops of lightning too), keep
 *  the biggest shape, axis = the handle's centre line from the bottom 20 %. */
export async function loadPicture(file: Blob, glow = 0): Promise<{ img: Img; pommel: P; tip: P }> {
  const bmp = await createImageBitmap(file);
  const canvas = new OffscreenCanvas(bmp.width, bmp.height);
  const ctx = canvas.getContext("2d")!;
  ctx.drawImage(bmp, 0, 0);
  bmp.close();
  const img: Img = { w: canvas.width, h: canvas.height, x: 0, y: 0, rgba: new Uint8Array(ctx.getImageData(0, 0, canvas.width, canvas.height).data.buffer) };
  let opaque = true;
  for (let k = 3; k < img.rgba.length; k += 4) if (img.rgba[k] !== 255) { opaque = false; break; }
  if (opaque) {
    floodKey(img, [img.rgba[0]!, img.rgba[1]!, img.rgba[2]!]);
    if (glow) for (let k = 0; k < img.rgba.length; k += 4) if (isGlow(img.rgba[k]!, img.rgba[k + 1]!, img.rgba[k + 2]!, glow)) img.rgba[k + 3] = 0;
    keepLargest(img);
  }
  let y0 = -1, y1 = -1;
  for (let j = 0; j < img.h; j++) for (let i = 0; i < img.w; i++) if (img.rgba[(j * img.w + i) * 4 + 3]) { if (y0 < 0) y0 = j; y1 = j + 1; break; }
  if (y0 < 0) throw new Error("the picture has no weapon pixels");
  const hx: number[] = [];
  for (let j = y1 - Math.max(2, Math.floor((y1 - y0) / 5)); j < y1; j++)
    for (let i = 0; i < img.w; i++) if (img.rgba[(j * img.w + i) * 4 + 3]) hx.push(i + 0.5);
  hx.sort((a, b) => a - b);
  const ax = hx[hx.length >> 1]!;
  return { img, pommel: [ax, y1], tip: [ax, y0] };
}

function floodKey(im: Img, key: Rgb): void {
  const near = (p: number) => Math.max(Math.abs(im.rgba[p * 4]! - key[0]), Math.abs(im.rgba[p * 4 + 1]! - key[1]), Math.abs(im.rgba[p * 4 + 2]! - key[2])) <= KEY_TOL;
  const seen = new Uint8Array(im.w * im.h);
  const stack: number[] = [];
  const seed = (p: number) => { if (!seen[p] && near(p)) { seen[p] = 1; stack.push(p); } };
  for (let i = 0; i < im.w; i++) { seed(i); seed((im.h - 1) * im.w + i); }
  for (let j = 0; j < im.h; j++) { seed(j * im.w); seed(j * im.w + im.w - 1); }
  while (stack.length) {
    const p = stack.pop()!;
    im.rgba[p * 4 + 3] = 0;
    const x = p % im.w, y = (p / im.w) | 0;
    if (x > 0) seed(p - 1);
    if (x < im.w - 1) seed(p + 1);
    if (y > 0) seed(p - im.w);
    if (y < im.h - 1) seed(p + im.w);
  }
}

/** Drops everything except the biggest 8-connected opaque shape (background specks). */
function keepLargest(im: Img): void {
  const label = new Int32Array(im.w * im.h);
  let best = 0, bestSize = 0, next = 0;
  for (let s = 0; s < label.length; s++) {
    if (label[s] || !im.rgba[s * 4 + 3]) continue;
    const id = ++next;
    let size = 0;
    const stack = [s];
    label[s] = id;
    while (stack.length) {
      const p = stack.pop()!;
      size++;
      const x = p % im.w, y = (p / im.w) | 0;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        const X = x + dx, Y = y + dy;
        if (X < 0 || Y < 0 || X >= im.w || Y >= im.h) continue;
        const q = Y * im.w + X;
        if (!label[q] && im.rgba[q * 4 + 3]) { label[q] = id; stack.push(q); }
      }
    }
    if (size > bestSize) { bestSize = size; best = id; }
  }
  for (let p = 0; p < label.length; p++) if (label[p] !== best) im.rgba[p * 4 + 3] = 0;
}

/** Area-average downscale by an integer factor (alpha-weighted colour). */
function boxShrink(im: Img, f: number): Img {
  const w = Math.floor(im.w / f), h = Math.floor(im.h / f);
  const out = new Uint8Array(w * h * 4);
  for (let j = 0; j < h; j++) for (let i = 0; i < w; i++) {
    let a = 0, r = 0, g = 0, b = 0;
    for (let y = j * f; y < j * f + f; y++) for (let x = i * f; x < i * f + f; x++) {
      const q = (y * im.w + x) * 4, A = im.rgba[q + 3]!;
      if (A) { a += A; r += im.rgba[q]! * A; g += im.rgba[q + 1]! * A; b += im.rgba[q + 2]! * A; }
    }
    if (a) out.set([Math.floor(r / a), Math.floor(g / a), Math.floor(b / a), Math.min(255, Math.floor(a / (f * f)))], (j * w + i) * 4);
  }
  return { w, h, x: 0, y: 0, rgba: out };
}

/** Python's round(): halves go to the even neighbour, so the colours match tools/weaponpose byte for byte. */
function roundEven(v: number): number {
  const r = Math.round(v);
  return v - Math.floor(v) === 0.5 && r % 2 !== 0 ? r - 1 : r;
}

function axes(p: P, t: P): { u: P; n: P } {
  const L = dist(p, t), u: P = [(t[0] - p[0]) / L, (t[1] - p[1]) / L];
  return { u, n: [-u[1], u[0]] };
}

/** weaponpose.py warp: maps src (pommel sp, tip st) so sp lands on dp with the axis towards dt; along / across scale
 *  the axis and its perpendicular (a negative across mirrors). Trimmed, positioned in destination coordinates. */
function warp(src: Img, sp: P, st: P, dp: P, dt: P, along: number, across: number): Img | null {
  const { u: us, n: ns } = axes(sp, st), { u: ud, n: nd } = axes(dp, dt);
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const [cx, cy] of [[0, 0], [src.w, 0], [0, src.h], [src.w, src.h]] as P[]) {
    const s = (cx - sp[0]) * us[0] + (cy - sp[1]) * us[1], r = (cx - sp[0]) * ns[0] + (cy - sp[1]) * ns[1];
    const X = dp[0] + s * along * ud[0] + r * across * nd[0], Y = dp[1] + s * along * ud[1] + r * across * nd[1];
    x0 = Math.min(x0, X); y0 = Math.min(y0, Y); x1 = Math.max(x1, X); y1 = Math.max(y1, Y);
  }
  x0 = Math.floor(x0); y0 = Math.floor(y0);
  const w = Math.ceil(x1) - x0, h = Math.ceil(y1) - y0;
  const out = new Uint8Array(w * h * 4);
  let minX = w, minY = h, maxX = -1, maxY = -1;
  for (let j = 0; j < h; j++) for (let i = 0; i < w; i++) {
    let cov = 0, r_ = 0, g_ = 0, b_ = 0;
    for (let sj = 0; sj < SS; sj++) for (let si = 0; si < SS; si++) {
      const X = x0 + i + (si + 0.5) / SS - dp[0], Y = y0 + j + (sj + 0.5) / SS - dp[1];
      const s = (X * ud[0] + Y * ud[1]) / along, r = (X * nd[0] + Y * nd[1]) / across;
      const qx = Math.floor(sp[0] + s * us[0] + r * ns[0]), qy = Math.floor(sp[1] + s * us[1] + r * ns[1]);
      if (qx < 0 || qy < 0 || qx >= src.w || qy >= src.h) continue;
      const o = (qy * src.w + qx) * 4, a = src.rgba[o + 3]!;
      if (!a) continue;
      const af = a / 255;
      cov += af; r_ += src.rgba[o]! * af; g_ += src.rgba[o + 1]! * af; b_ += src.rgba[o + 2]! * af;
    }
    if (cov / (SS * SS) < ALPHA_CUT) continue;
    out.set([roundEven(r_ / cov), roundEven(g_ / cov), roundEven(b_ / cov), 255], (j * w + i) * 4);
    minX = Math.min(minX, i); minY = Math.min(minY, j); maxX = Math.max(maxX, i); maxY = Math.max(maxY, j);
  }
  if (maxX < 0) return null;
  const tw = maxX - minX + 1, th = maxY - minY + 1, rgba = new Uint8Array(tw * th * 4);
  for (let j = 0; j < th; j++) rgba.set(out.subarray(((minY + j) * w + minX) * 4, ((minY + j) * w + minX + tw) * 4), j * tw * 4);
  return { w: tw, h: th, x: x0 + minX, y: y0 + minY, rgba };
}

/** weaponpose.py cmd_render: every reference frame posed from the picture, as library images (null = empty slot). */
export function poseFrames(ref: Reference, pic: { img: Img; pommel: P; tip: P }, o: PoseOptions): (Img | null)[] {
  let { img: src, pommel: sp, tip: st } = pic;
  let Ls = dist(sp, st);
  // big pictures: average down first to ~6 source px per output px of the longest frame
  const f = Math.floor(Ls / (Math.max(...ref.genders.map((g) => g.Lm)) * o.length * 6));
  if (f >= 2) { src = boxShrink(src, f); sp = [sp[0] / f, sp[1] / f]; st = [st[0] / f, st[1] / f]; Ls = dist(sp, st); }
  const out: (Img | null)[] = new Array(ref.count).fill(null);
  for (const g of ref.genders) {
    const across = ((g.Lm * o.length) / Ls) * o.width;
    for (const [idx, p] of g.poses) {
      const along = (dist(p.pommel, p.tip) * o.length) / Ls;
      const side = o.mirror.has(directionOf(idx)) ? -1 : 1;
      const dt: P = [p.pommel[0] + (p.tip[0] - p.pommel[0]) * o.length, p.pommel[1] + (p.tip[1] - p.pommel[1]) * o.length];
      const loc = warp(src, sp, st, p.pommel, dt, along, across * side);
      const base = ref.frames[idx];
      if (loc && base) { loc.x += base.x; loc.y += base.y; }
      out[idx] = loc;
    }
  }
  return out;
}

async function gzip(data: Uint8Array): Promise<Uint8Array> {
  const s = new Blob([data as BlobPart]).stream().pipeThrough(new CompressionStream("gzip"));
  return new Uint8Array(await new Response(s).arrayBuffer());
}

/** mirlib.py write_lib: a v2 .Lib (header, offset table, images: 6 x i16, u8 shadow, i32 length, gzip'd BGRA). */
export async function writeLib(frames: (Img | null)[]): Promise<Uint8Array> {
  const blobs = await Promise.all(frames.map(async (im) => {
    if (!im) return null;
    const bgra = im.rgba.slice();
    for (let k = 0; k < bgra.length; k += 4) { bgra[k] = im.rgba[k + 2]!; bgra[k + 2] = im.rgba[k]!; }
    const data = await gzip(bgra);
    const b = new Uint8Array(17 + data.length), dv = new DataView(b.buffer);
    dv.setInt16(0, im.w, true); dv.setInt16(2, im.h, true); dv.setInt16(4, im.x, true); dv.setInt16(6, im.y, true);
    dv.setInt32(13, data.length, true);
    b.set(data, 17);
    return b;
  }));
  let pos = 8 + 4 * frames.length;
  const total = pos + blobs.reduce((s, b) => s + (b?.length ?? 0), 0);
  const file = new Uint8Array(total), dv = new DataView(file.buffer);
  dv.setInt32(0, 2, true); dv.setInt32(4, frames.length, true);
  blobs.forEach((b, i) => {
    if (!b) return;
    dv.setInt32(8 + i * 4, pos, true);
    file.set(b, pos);
    pos += b.length;
  });
  return file;
}

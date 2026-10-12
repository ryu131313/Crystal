// Port of Client/MirObjects/PathFinder.cs: A* over the map grid, 8 neighbours (x -1..1 outer, y -1..1 inner),
// diagonal cost 14 / straight 10, the heap preferring the lowest F then the lowest H. A node is walkable when
// MapControl.EmptyCell holds. Returns start..target (inclusive), or null; maxNodes > 0 refuses longer paths.
// The desktop keeps one Node grid across searches (stale G costs leak between calls); this search starts clean each time.

import type { WirePoint } from "../protocol/binaryCodec.ts";

export function findPath(width: number, height: number, walkable: (x: number, y: number) => boolean,
  start: WirePoint, target: WirePoint, maxNodes = 0): WirePoint[] | null {
  if (start.X < 0 || start.Y < 0 || start.X >= width || start.Y >= height) return null;
  if (target.X < 0 || target.Y < 0 || target.X >= width || target.Y >= height) return null;
  const n = width * height;
  const id = (x: number, y: number) => x * height + y;
  const g = new Int32Array(n);
  const h = new Int32Array(n);
  const parent = new Int32Array(n).fill(-1);
  const state = new Uint8Array(n); // 0 unseen, 1 open, 2 closed
  const heapPos = new Int32Array(n).fill(-1);
  const heap: number[] = [];
  const dist = (a: number, b: number) => {
    const dx = Math.abs(Math.trunc(a / height) - Math.trunc(b / height)), dy = Math.abs((a % height) - (b % height));
    return dx > dy ? 14 * dy + 10 * (dx - dy) : 14 * dx + 10 * (dy - dx);
  };
  // Node.CompareTo: lower F first, then lower H
  const better = (a: number, b: number) => (g[a]! + h[a]!) < (g[b]! + h[b]!) || ((g[a]! + h[a]!) === (g[b]! + h[b]!) && h[a]! < h[b]!);
  const swap = (i: number, j: number) => { const t = heap[i]!; heap[i] = heap[j]!; heap[j] = t; heapPos[heap[i]!] = i; heapPos[heap[j]!] = j; };
  const up = (i: number) => { while (i > 0) { const p = (i - 1) >> 1; if (!better(heap[i]!, heap[p]!)) break; swap(i, p); i = p; } };
  const down = (i: number) => {
    for (;;) {
      const l = i * 2 + 1, r = l + 1;
      let m = i;
      if (l < heap.length && better(heap[l]!, heap[m]!)) m = l;
      if (r < heap.length && better(heap[r]!, heap[m]!)) m = r;
      if (m === i) return;
      swap(i, m);
      i = m;
    }
  };
  const push = (v: number) => { heap.push(v); heapPos[v] = heap.length - 1; up(heap.length - 1); };
  const pop = () => { const top = heap[0]!; const last = heap.pop()!; if (heap.length) { heap[0] = last; heapPos[last] = 0; down(0); } heapPos[top] = -1; return top; };

  const s = id(start.X, start.Y), t = id(target.X, target.Y);
  push(s);
  state[s] = 1;
  while (heap.length) {
    const cur = pop();
    state[cur] = 2;
    if (cur === t) {
      const path: WirePoint[] = [];
      for (let v = t; v !== -1; v = v === s ? -1 : parent[v]!) path.push({ X: Math.trunc(v / height), Y: v % height });
      path.reverse();
      return maxNodes === 0 || path.length < maxNodes ? path : null;
    }
    const cx = Math.trunc(cur / height), cy = cur % height;
    for (let dx = -1; dx <= 1; dx++) for (let dy = -1; dy <= 1; dy++) {
      if (dx === 0 && dy === 0) continue;
      const x = cx + dx, y = cy + dy;
      if (x < 0 || y < 0 || x >= width || y >= height) continue;
      const nb = id(x, y);
      if (state[nb] === 2 || !walkable(x, y)) continue;
      const cost = g[cur]! + dist(cur, nb);
      if (cost < g[nb]! || state[nb] !== 1) {
        g[nb] = cost;
        h[nb] = dist(nb, t);
        parent[nb] = cur;
        if (state[nb] !== 1) { state[nb] = 1; push(nb); } else up(heapPos[nb]!);
      }
    }
  }
  return null;
}

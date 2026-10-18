// The fishing window's water scene (fishing.ts): a canvas-2D night lake, drawn only while the window is shown (the
// window's frameShown subscription calls frame()). The static layers (sky, moon and halo, hills and their reflection,
// mist, vignette) are cached to offscreen canvases on resize; a frame draws the moving parts: stars, clouds, waves, the
// moon's reflection, reeds, fireflies, the rod, line and float, ripples, splash / sparkle particles and the leaping fish.
// No shadowBlur (Performance.md T3-5): every glow is a pre-rendered sprite. Purely visual: it follows the Fishing
// model's phase (setPhase) and the window's cast results (outcome / slip) and decides nothing.

export type ScenePhase = "idle" | "wait" | "bite";
export type SceneOutcome = "catch" | "lost" | "empty";

/** 0 = water drop, 1 = gold sparkle, 2 = foam. */
interface Particle { x: number; y: number; vx: number; vy: number; life: number; max: number; size: number; kind: 0 | 1 | 2 }
interface Ring { x: number; y: number; t: number; max: number; r: number; a: number }
interface Fish { x: number; y: number; vx: number; vy: number; t: number }
interface Star { x: number; y: number; s: number; ph: number; sp: number }
interface Wave { y: number; d: number; segs: { x: number; len: number }[]; speed: number }
interface Reed { x: number; h: number; bend: number; w: number; ph: number; head: boolean }
interface Firefly { ax: number; ay: number; ph: number; sp: number }

type Anim = "hang" | "cast" | "float" | "reel";

const CAST_TIME = 0.85, REEL_TIME = 0.8;
const GRAVITY = 620;

/** Small seeded PRNG (mulberry32) so a resize rebuilds the same hills, stars and reeds. */
function rng(seed: number): () => number {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const lerp = (a: number, b: number, t: number) => a + (b - a) * t;
const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);
const easeOut = (t: number) => 1 - (1 - t) * (1 - t);

function canvas(w: number, h: number): [HTMLCanvasElement, CanvasRenderingContext2D] {
  const c = document.createElement("canvas");
  c.width = Math.max(1, Math.ceil(w));
  c.height = Math.max(1, Math.ceil(h));
  return [c, c.getContext("2d")!];
}

/** A soft round glow sprite (radial falloff), drawn with "lighter" for light sources. */
function glowSprite(r: number, rgb: string, core = 1): HTMLCanvasElement {
  const [c, g] = canvas(r * 2, r * 2);
  const grad = g.createRadialGradient(r, r, 0, r, r, r);
  grad.addColorStop(0, `rgba(${rgb},${core})`);
  grad.addColorStop(0.25, `rgba(${rgb},${core * 0.45})`);
  grad.addColorStop(1, `rgba(${rgb},0)`);
  g.fillStyle = grad;
  g.fillRect(0, 0, r * 2, r * 2);
  return c;
}

/** A wispy cloud sprite: overlapping soft blobs. */
function cloudSprite(w: number, h: number, seed: number): HTMLCanvasElement {
  const [c, g] = canvas(w, h), r = rng(seed);
  for (let i = 0; i < 9; i++) {
    const x = w * (0.15 + r() * 0.7), y = h * (0.35 + r() * 0.35), rad = h * (0.25 + r() * 0.3);
    const grad = g.createRadialGradient(x, y, 0, x, y, rad);
    grad.addColorStop(0, "rgba(150,175,215,.22)");
    grad.addColorStop(1, "rgba(150,175,215,0)");
    g.fillStyle = grad;
    g.fillRect(0, 0, w, h);
  }
  return c;
}

export class FishingScene {
  readonly el: HTMLCanvasElement;
  private readonly ctx: CanvasRenderingContext2D;
  private readonly reduced = typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;
  private w = 0;
  private h = 0;
  private dpr = 1;
  private last = 0;
  private time = 0;

  // Cached layers and sprites
  private bg: HTMLCanvasElement | null = null;
  private fg: HTMLCanvasElement | null = null;
  private readonly fireGlow = glowSprite(14, "210,255,140");
  private readonly sparkGlow = glowSprite(10, "255,214,120");
  private readonly biteGlow = glowSprite(34, "255,196,80", 0.7);
  private readonly clouds = [cloudSprite(220, 60, 7), cloudSprite(160, 44, 19), cloudSprite(260, 70, 31)];

  // Layout (recomputed on resize)
  private hy = 0;
  private moon = { x: 0, y: 0, r: 0 };
  private tip = { x: 0, y: 0 };
  private rest = { x: 0, y: 0 };
  private stars: Star[] = [];
  private waves: Wave[] = [];
  private reeds: Reed[] = [];
  private flies: Firefly[] = [];

  // State
  private primed = false;
  private phase: ScenePhase = "idle";
  private anim: Anim = "hang";
  private animT = 0;
  private pending: SceneOutcome | null = null;
  private from = { x: 0, y: 0 };
  /** The float's spring: y offset below the waterline and its velocity. */
  private bobY = 0;
  private bobV = 0;
  private biteT = 0;
  private ringClock = 0;
  private tension = 0;
  private shake = 0;
  private flash = 0;
  private flashRgb = "255,200,90";
  private readonly parts: Particle[] = [];
  private readonly rings: Ring[] = [];
  private fish: Fish | null = null;
  private readonly fpos = { x: 0, y: 0 };

  constructor() {
    [this.el, this.ctx] = canvas(1, 1);
    this.el.setAttribute("aria-hidden", "true");
  }

  /** Fits the backing store to the element's CSS size (device pixels, capped at 2x) and rebuilds the static layers. */
  resize(w: number, h: number): void {
    w = Math.round(w); h = Math.round(h);
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    if (w < 2 || h < 2 || (w === this.w && h === this.h && dpr === this.dpr)) return;
    this.w = w; this.h = h; this.dpr = dpr;
    this.el.width = Math.round(w * dpr);
    this.el.height = Math.round(h * dpr);
    this.layout();
    this.bg = this.paintBackground();
    this.fg = this.paintOverlay();
  }

  /** The next setPhase jumps straight to that phase's steady pose (the window was hidden while things changed). */
  snap(): void { this.primed = false; }

  setPhase(p: ScenePhase): void {
    if (!this.primed) {
      this.primed = true;
      this.phase = p;
      this.anim = p === "idle" ? "hang" : "float";
      this.biteT = p === "bite" ? 0.5 : 0;
      return;
    }
    if (p === this.phase) return;
    const prev = this.phase;
    this.phase = p;
    if (prev === "idle") this.startCast();
    else if (p === "idle") this.startReel(this.pending ?? "empty");
    if (p === "bite") this.startBite();
    this.pending = null;
  }

  /** The cast's result, worked out by the window just before the Fishing = false update reels the float in. */
  outcome(kind: SceneOutcome): void {
    this.pending = kind;
    if (this.phase === "idle" && this.anim === "hang") this.playOutcome(kind, this.tip.x, this.tip.y + 40);
  }

  /** The bite ran out: the float bobs back up. */
  slip(): void {
    this.bobV = -160;
    this.splash(this.fpos.x, this.rest.y, 6, 110);
    this.ring(this.rest.x, this.rest.y, 26, 0.5);
  }

  frame(now: number): void {
    if (this.w < 2) return;
    const dt = this.last ? Math.min(0.05, Math.max(0, (now - this.last) / 1000)) : 0.016;
    this.last = now;
    this.time += dt;
    this.update(dt);
    this.draw();
  }

  // — Layout and static layers —

  private layout(): void {
    const { w, h } = this, r = rng(1234);
    this.hy = Math.round(h * 0.33);
    this.moon = { x: w * 0.8, y: h * 0.12, r: Math.max(11, Math.min(w, h) * 0.04) };
    this.tip = { x: w * 0.15, y: h * 0.17 };
    this.rest = { x: w * 0.58, y: this.hy + (h - this.hy) * 0.3 };
    this.stars = [];
    for (let i = 0; i < 80; i++) {
      const y = r() * (this.hy - 18);
      this.stars.push({ x: r() * w, y, s: 0.5 + r() * r() * 1.4, ph: r() * 6.28, sp: 0.6 + r() * 2.2 });
    }
    this.waves = [];
    const rows = 30;
    for (let k = 0; k < rows; k++) {
      const d = (k + 1) / rows, segs: { x: number; len: number }[] = [];
      const n = 3 + Math.round(r() * 4);
      for (let i = 0; i < n; i++) segs.push({ x: r() * (w + 120), len: (8 + r() * 36) * (0.35 + d * 1.1) });
      this.waves.push({ y: this.hy + (h - this.hy) * Math.pow(d, 1.65), d, segs, speed: (4 + d * 16) * (k % 2 ? 1 : -0.6) });
    }
    this.reeds = [];
    for (let i = 0; i < 16; i++) {
      this.reeds.push({ x: w * (0.8 + r() * 0.24), h: h * (0.28 + r() * 0.34), bend: (r() - 0.3) * 26, w: 1.4 + r() * 1.8, ph: r() * 6.28, head: r() < 0.45 });
    }
    for (let i = 0; i < 5; i++) this.reeds.push({ x: w * (-0.04 + r() * 0.1), h: h * (0.16 + r() * 0.2), bend: (r() - 0.6) * 18, w: 1.3 + r() * 1.2, ph: r() * 6.28, head: r() < 0.3 });
    this.flies = [];
    for (let i = 0; i < 8; i++) this.flies.push({ ax: w * (0.62 + r() * 0.36), ay: this.hy + (h - this.hy) * (0.05 + r() * 0.45) - h * 0.08, ph: r() * 6.28, sp: 0.3 + r() * 0.5 });
  }

  /** Hills as a closed polygon along the horizon (heights from summed sines, seeded). */
  private hills(g: CanvasRenderingContext2D, base: number, amp: number, seed: number, jag: number): void {
    const r = rng(seed), { w } = this, f1 = 1 + r() * 2, f2 = 3 + r() * 4, p1 = r() * 6, p2 = r() * 6;
    g.beginPath();
    g.moveTo(0, base);
    for (let x = 0; x <= w; x += 4) {
      const u = x / w;
      let y = base - amp * (0.55 + 0.3 * Math.sin(u * f1 * 6.28 + p1) + 0.15 * Math.sin(u * f2 * 6.28 + p2));
      if (jag) y -= (r() * jag) * (x % 8 === 0 ? 1.6 : 0.4); // a ragged pine edge
      g.lineTo(x, y);
    }
    g.lineTo(w, base);
    g.closePath();
  }

  private paintBackground(): HTMLCanvasElement {
    const { w, h, hy, dpr, moon } = this;
    const [c, g] = canvas(w * dpr, h * dpr);
    g.scale(dpr, dpr);

    // Sky
    const sky = g.createLinearGradient(0, 0, 0, hy);
    sky.addColorStop(0, "#030713");
    sky.addColorStop(0.55, "#0b1a35");
    sky.addColorStop(1, "#1f3d66");
    g.fillStyle = sky;
    g.fillRect(0, 0, w, hy + 1);
    // Moon: halo, disc, a few maria
    const halo = g.createRadialGradient(moon.x, moon.y, moon.r * 0.8, moon.x, moon.y, moon.r * 7);
    halo.addColorStop(0, "rgba(255,240,200,.28)");
    halo.addColorStop(0.35, "rgba(190,210,255,.08)");
    halo.addColorStop(1, "rgba(190,210,255,0)");
    g.fillStyle = halo;
    g.fillRect(0, 0, w, hy);
    const disc = g.createRadialGradient(moon.x - moon.r * 0.35, moon.y - moon.r * 0.35, 0, moon.x, moon.y, moon.r);
    disc.addColorStop(0, "#fffdf0");
    disc.addColorStop(0.7, "#f6e6b4");
    disc.addColorStop(1, "#d9c385");
    g.fillStyle = disc;
    g.beginPath(); g.arc(moon.x, moon.y, moon.r, 0, 6.2832); g.fill();
    g.fillStyle = "rgba(150,130,80,.16)";
    for (const [dx, dy, rr] of [[-0.3, -0.1, 0.28], [0.25, 0.2, 0.2], [0.05, -0.4, 0.14], [0.35, -0.25, 0.1]] as const) {
      g.beginPath(); g.arc(moon.x + dx * moon.r, moon.y + dy * moon.r, rr * moon.r, 0, 6.2832); g.fill();
    }

    // Hills: far (misty blue), near (treeline)
    this.hills(g, hy, h * 0.11, 5, 0);
    g.fillStyle = "#14294a"; g.fill();
    const mist = g.createLinearGradient(0, hy - h * 0.1, 0, hy);
    mist.addColorStop(0, "rgba(90,130,180,0)");
    mist.addColorStop(1, "rgba(90,130,180,.35)");
    g.fillStyle = mist; g.fill();
    this.hills(g, hy, h * 0.055, 11, 3.5);
    g.fillStyle = "#07111f"; g.fill();

    // Water
    const water = g.createLinearGradient(0, hy, 0, h);
    water.addColorStop(0, "#1c3f66");
    water.addColorStop(0.18, "#113052");
    water.addColorStop(0.6, "#08203a");
    water.addColorStop(1, "#030b18");
    g.fillStyle = water;
    g.fillRect(0, hy, w, h - hy);
    // Hill reflections (mirrored, squashed, faded)
    g.save();
    g.globalAlpha = 0.45;
    g.translate(0, hy * 1.62);
    g.scale(1, -0.62);
    this.hills(g, hy, h * 0.11, 5, 0);
    g.fillStyle = "#0e2240"; g.fill();
    this.hills(g, hy, h * 0.055, 11, 3.5);
    g.fillStyle = "#050d19"; g.fill();
    g.restore();
    // Moonlight on the water and the horizon line
    g.save();
    g.translate(moon.x, hy + (h - hy) * 0.35);
    g.scale(1, 3.2);
    const pool = g.createRadialGradient(0, 0, 0, 0, 0, (h - hy) * 0.22);
    pool.addColorStop(0, "rgba(255,235,180,.10)");
    pool.addColorStop(1, "rgba(255,235,180,0)");
    g.fillStyle = pool;
    g.fillRect(-w, -h, w * 2, h * 2);
    g.restore();
    const line = g.createLinearGradient(0, 0, w, 0);
    line.addColorStop(0, "rgba(160,200,255,0)");
    line.addColorStop(0.5, "rgba(160,200,255,.28)");
    line.addColorStop(1, "rgba(160,200,255,0)");
    g.fillStyle = line;
    g.fillRect(0, hy - 0.5, w, 1.5);
    // Low mist over the water's far edge
    const fog = g.createLinearGradient(0, hy - 6, 0, hy + (h - hy) * 0.25);
    fog.addColorStop(0, "rgba(120,155,200,.18)");
    fog.addColorStop(1, "rgba(120,155,200,0)");
    g.fillStyle = fog;
    g.fillRect(0, hy - 6, w, (h - hy) * 0.25 + 6);
    return c;
  }

  /** Vignette and the dark band under the HUD, drawn over everything. */
  private paintOverlay(): HTMLCanvasElement {
    const { w, h, dpr } = this;
    const [c, g] = canvas(w * dpr, h * dpr);
    g.scale(dpr, dpr);
    const v = g.createRadialGradient(w * 0.5, h * 0.42, Math.min(w, h) * 0.35, w * 0.5, h * 0.5, Math.max(w, h) * 0.78);
    v.addColorStop(0, "rgba(0,0,0,0)");
    v.addColorStop(1, "rgba(0,2,8,.62)");
    g.fillStyle = v;
    g.fillRect(0, 0, w, h);
    const b = g.createLinearGradient(0, h - 190, 0, h);
    b.addColorStop(0, "rgba(2,6,14,0)");
    b.addColorStop(0.55, "rgba(2,6,14,.55)");
    b.addColorStop(1, "rgba(2,6,14,.88)");
    g.fillStyle = b;
    g.fillRect(0, h - 190, w, 190);
    return c;
  }

  // — Simulation —

  private hangPos(): { x: number; y: number } {
    return { x: this.tip.x + Math.sin(this.time * 1.4) * 4, y: this.tip.y + 38 + Math.cos(this.time * 1.1) * 1.5 };
  }

  private startCast(): void {
    const p = this.anim === "hang" ? this.hangPos() : { x: this.fpos.x, y: this.fpos.y };
    this.from = p;
    this.anim = "cast";
    this.animT = 0;
    this.biteT = 0;
  }

  private startReel(kind: SceneOutcome): void {
    this.from = { x: this.fpos.x, y: this.rest.y };
    this.anim = "reel";
    this.animT = 0;
    this.playOutcome(kind, this.fpos.x, this.rest.y);
  }

  private playOutcome(kind: SceneOutcome, x: number, y: number): void {
    if (kind === "catch") {
      this.fish = { x, y: y + 2, vx: (this.tip.x + 30 - x) / 1.05, vy: -360, t: 0 };
      this.splash(x, y, 22, 210);
      this.ring(x, y, 40, 0.9);
      this.ring(x, y, 24, 0.7);
      this.flash = 0.55; this.flashRgb = "255,205,110";
      this.shake = Math.max(this.shake, 0.5);
    } else if (kind === "lost") {
      this.splash(x, y, 14, 170);
      this.ring(x, y, 34, 0.8);
      this.flash = 0.25; this.flashRgb = "255,90,80";
    } else {
      this.ring(x, y, 20, 0.5);
    }
  }

  private startBite(): void {
    this.biteT = 0;
    this.bobV = 260;
    this.splash(this.fpos.x, this.rest.y, 16, 190);
    this.ring(this.rest.x, this.rest.y, 36, 0.9);
    this.flash = 0.35; this.flashRgb = "255,200,90";
    this.shake = 1;
  }

  private splash(x: number, y: number, n: number, power: number): void {
    for (let i = 0; i < n; i++) {
      const a = -Math.PI / 2 + (Math.random() - 0.5) * 1.9, s = power * (0.35 + Math.random() * 0.75);
      const max = 0.5 + Math.random() * 0.5;
      this.parts.push({ x: x + (Math.random() - 0.5) * 8, y, vx: Math.cos(a) * s, vy: Math.sin(a) * s, life: max, max, size: 1 + Math.random() * 1.8, kind: 0 });
    }
  }

  private sparkles(x: number, y: number, n: number): void {
    for (let i = 0; i < n; i++) {
      const a = Math.random() * 6.2832, s = 40 + Math.random() * 130, max = 0.6 + Math.random() * 0.7;
      this.parts.push({ x, y, vx: Math.cos(a) * s, vy: Math.sin(a) * s - 40, life: max, max, size: 0.5 + Math.random() * 0.7, kind: 1 });
    }
  }

  private ring(x: number, y: number, r: number, a: number): void {
    this.rings.push({ x, y, t: 0, max: 1.4 + r / 40, r, a });
  }

  private update(dt: number): void {
    const { rest } = this;
    this.animT += dt;

    // Float position and line tension per animation
    let tension = 0.1;
    if (this.anim === "hang") {
      const p = this.hangPos();
      this.fpos.x = p.x; this.fpos.y = p.y;
      tension = 0;
    } else if (this.anim === "cast") {
      const u = clamp01(this.animT / CAST_TIME), e = easeOut(u);
      this.fpos.x = lerp(this.from.x, rest.x, e);
      this.fpos.y = lerp(this.from.y, rest.y, u) - Math.sin(Math.PI * u) * this.h * 0.24;
      tension = 0.25 * (1 - u);
      if (u >= 1) {
        this.anim = "float";
        this.bobY = 0; this.bobV = 210;
        this.splash(rest.x, rest.y, 12, 150);
        this.ring(rest.x, rest.y, 30, 0.85);
        this.ring(rest.x, rest.y, 16, 0.6);
      }
    } else if (this.anim === "reel") {
      const u = clamp01(this.animT / REEL_TIME), hp = this.hangPos();
      const skim = clamp01(u / 0.6);
      this.fpos.x = lerp(this.from.x, hp.x, u < 0.6 ? skim * 0.7 : 0.7 + (u - 0.6) / 0.4 * 0.3);
      this.fpos.y = u < 0.6 ? this.from.y : lerp(this.from.y, hp.y, easeOut((u - 0.6) / 0.4));
      if (u < 0.6 && Math.random() < dt * 40) this.parts.push({ x: this.fpos.x + 6, y: this.fpos.y, vx: 20 + Math.random() * 30, vy: -20 - Math.random() * 40, life: 0.4, max: 0.4, size: 1 + Math.random(), kind: 2 });
      tension = 0.6 * (1 - u);
      if (u >= 1) this.anim = "hang";
    }

    if (this.anim === "float") {
      // Spring toward the target depth: a gentle bob while waiting; a deep, jerking plunge on a bite
      const bite = this.phase === "bite";
      if (bite) this.biteT += dt;
      const target = bite ? 9 + Math.sin(this.biteT * 21) * 3.5 : Math.sin(this.time * 2.1) * 1.6;
      this.bobV += (-(this.bobY - target) * (bite ? 260 : 60) - this.bobV * (bite ? 10 : 5)) * dt;
      this.bobY += this.bobV * dt;
      const sway = bite ? Math.sin(this.biteT * 17) * 2.8 : Math.sin(this.time * 0.5) * 3;
      this.fpos.x = rest.x + sway;
      this.fpos.y = rest.y;
      tension = bite ? 0.85 + Math.sin(this.biteT * 21) * 0.1 : 0.12;
      this.ringClock -= dt;
      if (this.ringClock <= 0) {
        this.ringClock = bite ? 0.33 : 1.9;
        this.ring(this.fpos.x, rest.y, bite ? 26 : 16, bite ? 0.75 : 0.35);
        if (bite) this.splash(this.fpos.x, rest.y, 3, 110);
      }
    }
    this.tension += (tension - this.tension) * Math.min(1, dt * 10);

    // Particles, rings, the leaping fish, camera
    for (let i = this.parts.length - 1; i >= 0; i--) {
      const p = this.parts[i]!;
      p.life -= dt;
      if (p.life <= 0) { this.parts[i] = this.parts[this.parts.length - 1]!; this.parts.pop(); continue; }
      p.vy += (p.kind === 1 ? 60 : GRAVITY) * dt;
      if (p.kind === 1) { p.vx *= 1 - dt * 2; p.vy *= 1 - dt * 2; }
      p.x += p.vx * dt; p.y += p.vy * dt;
    }
    for (let i = this.rings.length - 1; i >= 0; i--) {
      const r = this.rings[i]!;
      r.t += dt;
      if (r.t >= r.max) { this.rings[i] = this.rings[this.rings.length - 1]!; this.rings.pop(); }
    }
    const f = this.fish;
    if (f) {
      f.t += dt;
      f.vy += GRAVITY * 0.7 * dt;
      f.x += f.vx * dt; f.y += f.vy * dt;
      if (Math.random() < dt * 30) this.parts.push({ x: f.x, y: f.y, vx: (Math.random() - 0.5) * 40, vy: 10, life: 0.5, max: 0.5, size: 1 + Math.random(), kind: 0 });
      if (f.t > 0.95) { this.sparkles(f.x, f.y, 26); this.flash = Math.max(this.flash, 0.3); this.fish = null; }
    }
    this.shake = Math.max(0, this.shake - dt * 2.2);
    this.flash = Math.max(0, this.flash - dt * 1.6);
  }

  // — Drawing —

  private draw(): void {
    const g = this.ctx, { w, h, hy, time, dpr } = this;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.globalCompositeOperation = "source-over";
    g.globalAlpha = 1;
    if (this.bg) g.drawImage(this.bg, 0, 0, w, h);
    if (this.shake > 0 && !this.reduced) {
      const a = this.shake * this.shake * 4;
      g.translate((Math.random() - 0.5) * a, (Math.random() - 0.5) * a);
    }

    // Stars and clouds
    g.fillStyle = "#e9f0ff";
    for (const s of this.stars) {
      g.globalAlpha = 0.25 + 0.6 * (0.5 + 0.5 * Math.sin(time * s.sp + s.ph));
      g.fillRect(s.x, s.y, s.s, s.s);
    }
    g.globalAlpha = 1;
    this.clouds.forEach((c, i) => {
      const span = w + c.width, x = ((time * (5 + i * 3) + i * span * 0.37) % span) - c.width;
      g.drawImage(c, x, hy * (0.18 + i * 0.2));
    });

    // Waves (one stroke per row; lighter and longer toward the viewer)
    g.lineCap = "round";
    for (const row of this.waves) {
      g.strokeStyle = `rgba(150,200,255,${(0.05 + row.d * 0.2).toFixed(3)})`;
      g.lineWidth = 0.6 + row.d * 1.4;
      g.beginPath();
      const span = w + 120;
      for (const s of row.segs) {
        let x = (s.x + time * row.speed) % span;
        if (x < 0) x += span;
        x -= 60;
        const y = row.y + Math.sin(time * 1.3 + s.x) * row.d * 1.5;
        g.moveTo(x, y);
        g.lineTo(x + s.len, y);
      }
      g.stroke();
    }

    // The moon's broken reflection
    g.globalCompositeOperation = "lighter";
    g.fillStyle = "rgb(255,232,170)";
    const rows = 24;
    for (let k = 0; k < rows; k++) {
      const d = k / rows, y = hy + 3 + (h - hy) * 0.62 * Math.pow(d, 1.3);
      const width = (6 + 46 * d) * (0.45 + 0.55 * Math.abs(Math.sin(time * 1.6 + k * 1.31)));
      const x = this.moon.x + Math.sin(time * 0.9 + k * 0.7) * (2 + 5 * d) - width / 2;
      g.globalAlpha = 0.5 - 0.38 * d;
      g.fillRect(x, y, width, 1 + d * 2);
    }
    g.globalAlpha = 1;
    g.globalCompositeOperation = "source-over";

    // Ripples (perspective ellipses)
    g.lineWidth = 1.3;
    for (const r of this.rings) {
      const u = r.t / r.max, rad = r.r * (0.2 + easeOut(u) * 1.6);
      g.strokeStyle = `rgba(200,230,255,${(r.a * (1 - u)).toFixed(3)})`;
      g.beginPath();
      g.ellipse(r.x, r.y + 1, rad, rad * 0.27, 0, 0, 6.2832);
      g.stroke();
    }

    // Bite glow under the float
    if (this.phase === "bite" && this.anim === "float") {
      g.globalCompositeOperation = "lighter";
      g.globalAlpha = 0.45 + 0.35 * Math.sin(time * 12);
      const s = this.biteGlow;
      g.drawImage(s, this.fpos.x - s.width / 2, this.rest.y - s.height / 2 + 2);
      g.globalAlpha = 1;
      g.globalCompositeOperation = "source-over";
    }

    this.drawLine();
    this.drawFloat();
    if (this.fish) this.drawFish(this.fish);
    this.drawRod();
    this.drawReeds();

    // Particles: drops / foam, then additive sparkles
    for (const p of this.parts) {
      if (p.kind === 1) continue;
      g.globalAlpha = clamp01(p.life / p.max) * (p.kind === 2 ? 0.5 : 0.85);
      g.fillStyle = p.kind === 2 ? "#cfe6ff" : "#a9d6ff";
      g.beginPath(); g.arc(p.x, p.y, p.size, 0, 6.2832); g.fill();
    }
    g.globalCompositeOperation = "lighter";
    for (const p of this.parts) {
      if (p.kind !== 1) continue;
      g.globalAlpha = clamp01(p.life / p.max);
      const s = this.sparkGlow, sz = s.width * p.size;
      g.drawImage(s, p.x - sz / 2, p.y - sz / 2, sz, sz);
    }
    // Fireflies
    for (const f of this.flies) {
      const x = f.ax + Math.sin(time * f.sp + f.ph) * 22, y = f.ay + Math.sin(time * f.sp * 1.7 + f.ph * 2) * 10;
      g.globalAlpha = Math.max(0, Math.sin(time * f.sp * 2.3 + f.ph)) * 0.85;
      g.drawImage(this.fireGlow, x - 14, y - 14);
    }
    g.globalAlpha = 1;
    g.globalCompositeOperation = "source-over";

    if (this.phase === "bite" && this.anim === "float") this.drawBiteMark();

    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    if (this.fg) g.drawImage(this.fg, 0, 0, w, h);
    if (this.flash > 0) {
      g.fillStyle = `rgba(${this.flashRgb},${(this.flash * 0.35).toFixed(3)})`;
      g.fillRect(0, 0, w, h);
    }
  }

  /** The rod tip bent toward the float by the line's tension. */
  private rodTip(): { x: number; y: number } {
    const t = this.tension;
    return { x: this.tip.x + t * this.w * 0.05, y: this.tip.y + t * this.h * 0.07 };
  }

  private drawRod(): void {
    const g = this.ctx, tip = this.rodTip();
    const bx = -this.w * 0.04, by = this.h * 1.04;
    const cx = lerp(bx, tip.x, 0.55) - this.tension * 6, cy = lerp(by, tip.y, 0.55) - 18 - this.tension * 26;
    // Tapered body as a filled strip along the quadratic curve
    const n = 18, left: number[] = [], right: number[] = [];
    for (let i = 0; i <= n; i++) {
      const u = i / n, a = (1 - u) * (1 - u), b = 2 * (1 - u) * u, c = u * u;
      const x = a * bx + b * cx + c * tip.x, y = a * by + b * cy + c * tip.y;
      const dx = 2 * (1 - u) * (cx - bx) + 2 * u * (tip.x - cx), dy = 2 * (1 - u) * (cy - by) + 2 * u * (tip.y - cy);
      const len = Math.hypot(dx, dy) || 1, wid = lerp(5, 0.7, u);
      left.push(x - (dy / len) * wid, y + (dx / len) * wid);
      right.push(x + (dy / len) * wid, y - (dx / len) * wid);
    }
    g.beginPath();
    g.moveTo(left[0]!, left[1]!);
    for (let i = 2; i < left.length; i += 2) g.lineTo(left[i]!, left[i + 1]!);
    for (let i = right.length - 2; i >= 0; i -= 2) g.lineTo(right[i]!, right[i + 1]!);
    g.closePath();
    g.fillStyle = "#120c08";
    g.fill();
    // Moonlit edge
    g.beginPath();
    g.moveTo(right[0]!, right[1]!);
    for (let i = 2; i < right.length; i += 2) g.lineTo(right[i]!, right[i + 1]!);
    g.strokeStyle = "rgba(255,225,170,.35)";
    g.lineWidth = 0.8;
    g.stroke();
    // Line guides
    g.strokeStyle = "rgba(200,210,230,.5)";
    g.lineWidth = 0.8;
    for (const u of [0.55, 0.75, 0.9]) {
      const a = (1 - u) * (1 - u), b = 2 * (1 - u) * u, c = u * u;
      const x = a * bx + b * cx + c * tip.x, y = a * by + b * cy + c * tip.y;
      g.beginPath(); g.arc(x, y + 2, 1.6, 0, 6.2832); g.stroke();
    }
  }

  private drawLine(): void {
    const g = this.ctx, tip = this.rodTip(), fx = this.fpos.x, fy = this.floatTopY();
    const sag = (1 - this.tension) * (this.anim === "float" ? 46 : this.anim === "hang" ? 4 : 18);
    g.strokeStyle = this.tension > 0.6 ? "rgba(240,248,255,.8)" : "rgba(220,235,255,.5)";
    g.lineWidth = this.tension > 0.6 ? 1.1 : 0.9;
    g.beginPath();
    g.moveTo(tip.x, tip.y);
    g.quadraticCurveTo((tip.x + fx) / 2, (tip.y + fy) / 2 + sag, fx, fy);
    g.stroke();
  }

  /** The top of the float's antenna (where the line ties on). */
  private floatTopY(): number {
    return this.anim === "float" ? this.fpos.y + this.bobY - 24 : this.fpos.y - 24;
  }

  private drawFloat(): void {
    const g = this.ctx, x = this.fpos.x, onWater = this.anim === "float";
    const y = onWater ? this.fpos.y + this.bobY : this.fpos.y, wl = this.fpos.y;
    const body = () => {
      g.strokeStyle = "#e9e3d2"; g.lineWidth = 1.4;
      g.beginPath(); g.moveTo(x, y - 24); g.lineTo(x, y - 13); g.stroke();
      g.fillStyle = "#ff3b2f";
      g.beginPath(); g.arc(x, y - 24, 1.6, 0, 6.2832); g.fill();
      const grad = g.createLinearGradient(x - 6, 0, x + 6, 0);
      grad.addColorStop(0, "#a3141a"); grad.addColorStop(0.4, "#ff4a3d"); grad.addColorStop(1, "#7a0d12");
      g.fillStyle = grad;
      g.beginPath(); g.ellipse(x, y - 6, 6, 7.5, 0, Math.PI, 0); g.fill();
      const low = g.createLinearGradient(x - 6, 0, x + 6, 0);
      low.addColorStop(0, "#9aa2ad"); low.addColorStop(0.4, "#ffffff"); low.addColorStop(1, "#7d8590");
      g.fillStyle = low;
      g.beginPath(); g.ellipse(x, y - 6, 6, 7.5, 0, 0, Math.PI); g.fill();
      g.fillStyle = "rgba(255,255,255,.55)";
      g.beginPath(); g.ellipse(x - 2.2, y - 9.5, 1.3, 2.4, -0.4, 0, 6.2832); g.fill();
      g.strokeStyle = "#5a5f66"; g.lineWidth = 1.2;
      g.beginPath(); g.moveTo(x, y + 1.5); g.lineTo(x, y + 8); g.stroke();
    };
    if (!onWater) { body(); return; }
    // Above the waterline at full strength, below it faded and tinted
    g.save();
    g.beginPath(); g.rect(x - 20, wl - 40, 40, 40); g.clip();
    body();
    g.restore();
    g.save();
    g.beginPath(); g.rect(x - 20, wl, 40, 30); g.clip();
    g.globalAlpha = 0.28;
    body();
    g.restore();
    // Waterline contact
    g.strokeStyle = "rgba(210,235,255,.55)"; g.lineWidth = 1;
    g.beginPath(); g.ellipse(x, wl, 8, 2, 0, 0, 6.2832); g.stroke();
  }

  private drawFish(f: Fish): void {
    const g = this.ctx, a = Math.atan2(f.vy, f.vx), fade = clamp01((0.95 - f.t) / 0.25);
    g.save();
    g.globalAlpha = fade;
    g.translate(f.x, f.y);
    g.rotate(a);
    const grad = g.createLinearGradient(0, -6, 0, 6);
    grad.addColorStop(0, "#2c4a5e"); grad.addColorStop(0.5, "#8fb7c9"); grad.addColorStop(1, "#d9e8ee");
    g.fillStyle = grad;
    g.beginPath(); g.ellipse(0, 0, 14, 5.5, 0, 0, 6.2832); g.fill();
    const flap = Math.sin(f.t * 40) * 3;
    g.beginPath(); g.moveTo(-11, 0); g.lineTo(-20, -6 + flap); g.lineTo(-20, 6 + flap); g.closePath(); g.fill();
    g.fillStyle = "#0a1016";
    g.beginPath(); g.arc(8.5, -1.5, 1.2, 0, 6.2832); g.fill();
    g.strokeStyle = "rgba(255,240,200,.6)"; g.lineWidth = 0.8;
    g.beginPath(); g.ellipse(0, 0, 14, 5.5, 0, Math.PI * 1.1, Math.PI * 1.9); g.stroke();
    g.restore();
  }

  private drawReeds(): void {
    const g = this.ctx, { h, time } = this;
    g.strokeStyle = "#03080f";
    g.fillStyle = "#050b12";
    for (const r of this.reeds) {
      const sway = Math.sin(time * 0.9 + r.ph) * 5 * (r.h / h) + r.bend;
      const tx = r.x + sway, ty = h + 4 - r.h;
      g.lineWidth = r.w;
      g.beginPath();
      g.moveTo(r.x, h + 4);
      g.quadraticCurveTo(r.x + sway * 0.2, h - r.h * 0.5, tx, ty);
      g.stroke();
      if (r.head) {
        g.save();
        g.translate(tx - sway * 0.06, ty + 12);
        g.rotate(sway * 0.012);
        g.beginPath(); g.ellipse(0, 0, 2.6, 9, 0, 0, 6.2832); g.fill();
        g.restore();
      }
    }
  }

  private drawBiteMark(): void {
    const g = this.ctx, x = this.fpos.x, y = this.rest.y - 46, s = 1 + 0.12 * Math.sin(this.time * 14);
    g.save();
    g.translate(x, y);
    g.scale(s, s);
    g.globalCompositeOperation = "lighter";
    g.globalAlpha = 0.8;
    g.drawImage(this.biteGlow, -34, -34);
    g.globalCompositeOperation = "source-over";
    g.globalAlpha = 1;
    g.font = "900 26px system-ui, sans-serif";
    g.textAlign = "center";
    g.textBaseline = "middle";
    g.lineWidth = 4;
    g.strokeStyle = "rgba(40,16,0,.85)";
    g.strokeText("!", 0, 1);
    g.fillStyle = "#ffd66b";
    g.fillText("!", 0, 1);
    g.restore();
  }
}

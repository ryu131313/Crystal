// Weather particles: port of MapControl.Weather + MapControl.UpdateWeather (Client/MirScenes/GameScene.cs), the
// ParticleEngine (Client/MirGraphics/ParticleEngine.cs) and Particle / ParticleImageInfo / FogParticle / SnowParticle
// (Client/MirGraphics/Particles/*.cs). Screen-space particles drawn from Weather.Lib after the map objects and before
// the lights (MapControl.CreateTexture). There is no desktop option to turn weather off: the map's flags decide.
//
// Deliberate differences (see the report / webclient.md):
// - The 50 ms particle step and the ember spawn delay catch up in fixed steps (desktop: `next = Now + 50` at its ~1 ms loop
//   granularity); a literal `now + 50` under a 60 Hz requestAnimationFrame would run at 66.7 ms, 25 % slow. More than
//   250 ms behind (hidden tab) resyncs instead of replaying.
// - Animation frames advance per texture once per update (desktop: every particle's ProcessImage advances the shared
//   ParticleImageInfo, so it only advances while a particle uses it; visually the same).
// - The wrap-around needs the base frame's size; while that image loads, wrapping is skipped (the desktop always knows it).
// - The grid is rebuilt when the canvas size changes (the desktop screen size is fixed).
// - Particle.Size is never used by the desktop draw, so it is not kept.

import { sprite } from "./libraries.ts";
import type { Painter } from "./mapView.ts";

/** Shared/Enums.cs WeatherSetting (ushort [Flags]-style values; not in enums.generated.ts). */
export const WeatherSetting = {
  None: 0, Fog: 1, RedEmber: 2, WhiteEmber: 4, YellowEmber: 8, FireParticle: 16, Snow: 32, Rain: 64, Leaves: 128,
  FireyLeaves: 256, PurpleLeaves: 512,
} as const;

const WEATHER_LIB = "Weather"; // Libraries.Weather = Settings.DataPath + "Weather"
const STEP_MS = 50; // Particle.UpdateDelay / FogParticle / SnowParticle .Update: 50 ms
const MAX_BEHIND_MS = 250;

type Colour = { r: number; g: number; b: number; a: number };
const rgb = (r: number, g: number, b: number, a = 1): Colour => ({ r: r / 255, g: g / 255, b: b / 255, a });
// System.Drawing named colours used by ParticleEngine.GenerateNewParticle
const WHITE = rgb(255, 255, 255), FIREBRICK = rgb(178, 34, 34), GOLDENROD = rgb(218, 165, 32), PURPLE = rgb(128, 0, 128);
const DARK_RED = rgb(139, 0, 0), YELLOW = rgb(255, 255, 0);
const RAIN = rgb(255, 255, 0x85); // ColorTranslator.FromHtml("#ffffff85") parses as ARGB 0xFFFFFF85

/** CMain.Random.Next(max) / Next(min, max). */
const rand = (max: number): number => Math.floor(Math.random() * max);
const randRange = (min: number, max: number): number => min + Math.floor(Math.random() * (max - min));

/** ParticleImageInfo: one animated Weather.Lib strip, its frame shared by every particle that picked it. */
class ImageInfo {
  current = 0;
  private start: number;
  private nextFrame: number;
  private readonly frameMs: number;

  constructor(readonly baseIndex: number, readonly count: number, drawMs: number, now: number) {
    this.frameMs = drawMs; // Duration / Count with Duration = drawMS * count
    this.start = now;
    this.nextFrame = now; // the ctor computes NextFrame while Duration is still 0
  }

  /** Particle.ProcessImage (at most one frame per call). */
  process(now: number): void {
    if (now <= this.nextFrame) return;
    if (++this.current >= this.count) {
      this.current = 0;
      this.start = now; // + Delay (always 0)
    }
    this.nextFrame = this.start + this.frameMs * (this.current + 1);
  }

  /** ParticleImageInfo.Size = Library.GetSize(BaseIndex); null while loading / absent. */
  size(): { w: number; h: number } | null {
    const s = sprite(WEATHER_LIB, this.baseIndex);
    return s ? { w: s.width, h: s.height } : null;
  }
}

type WrapKind = "base" | "snow";

/** Particle (+ FogParticle / SnowParticle, which only change the wrap-around). */
interface Particle {
  x: number; y: number;
  vx: number; vy: number;
  image: ImageInfo;
  colour: Colour;
  blend: boolean;
  blendRate: number;
  aliveUntil: number; // AliveTime (Infinity = DateTime.MaxValue)
  nextUpdate: number;
  wrap: WrapKind;
}

/** ParticleType values that UpdateWeather uses. */
type ParticleType = "Fog" | "Snow" | "FireyLeaves" | "Leaves" | "PurpleLeaves" | "Rain" | "RedFogEmber" | "WhiteEmber" | "YellowEmber" | "Bird";

/** ParticleEngine. */
class Engine {
  readonly particles: Particle[] = [];
  generate = true; // GenerateParticles
  private nextParticle = -Infinity; // NextParticleTime (DateTime default: the first Process generates)
  readonly updateDelay = STEP_MS; // UpdateDelay (Fog sets 20 ms, but it never generates)

  constructor(private readonly textures: ImageInfo[], private readonly type: ParticleType, private readonly owner: Weather) {}

  /** ParticleEngine.GenerateNewParticle. Null when the engine has no textures (FireParticle). */
  newParticle(now: number): Particle | null {
    if (this.textures.length === 0) return null;
    const image = this.textures[rand(this.textures.length)]!;
    const W = this.owner.width, H = this.owner.height;
    const base = (colour: Colour, blend: boolean, blendRate: number, wrap: WrapKind = "base"): Particle =>
      ({ x: 0, y: 0, vx: 0, vy: 0, image, colour, blend, blendRate, aliveUntil: Infinity, nextUpdate: now, wrap });
    let p: Particle;
    switch (this.type) {
      case "Fog": p = base(WHITE, false, 0.4); break;
      case "Snow": p = base(WHITE, true, 1, "snow"); break;
      case "FireyLeaves": p = base(FIREBRICK, true, 1); break;
      case "Leaves": p = base(GOLDENROD, true, 0.1); break;
      case "PurpleLeaves": p = base(PURPLE, true, 0.1); break;
      case "Rain": p = base(RAIN, true, 1); break;
      case "RedFogEmber": case "WhiteEmber": case "YellowEmber": {
        const colour = this.type === "RedFogEmber" ? DARK_RED : this.type === "WhiteEmber" ? WHITE : YELLOW;
        p = base(colour, true, 0.35);
        p.aliveUntil = now + 1000 * (1 + rand(2));
        this.owner.setPosition(p, rand(W), randRange(Math.trunc(H / 2), H));
        p.vy = -2 * rand(3);
        break;
      }
      case "Bird": {
        p = base(WHITE, true, 0.35);
        p.aliveUntil = now + 1000 * (1 + rand(2));
        this.owner.setPosition(p, rand(W), randRange(Math.trunc(H / 4), H * 2));
        p.vx = -2; p.vy = -2 * rand(3);
        break;
      }
    }
    this.particles.push(p);
    return p;
  }

  /** ParticleEngine.Process. */
  process(now: number): void {
    for (const t of this.textures) t.process(now);

    if (this.generate) {
      if (this.nextParticle < now - MAX_BEHIND_MS) this.nextParticle = now - 1;
      while (now > this.nextParticle) {
        this.nextParticle += this.updateDelay;
        this.newParticle(now);
      }
    }

    for (let i = 0; i < this.particles.length; i++) {
      const p = this.particles[i]!;
      this.owner.updateParticle(p, now);
      if (now > p.aliveUntil) {
        this.particles.splice(i, 1);
        i--;
      }
    }
  }

  /** ParticleEngine.Draw -> Particle.Draw: DrawBlend (additive, tinted) or Draw with BlendRate as opacity, image offset on. */
  draw(painter: Painter): void {
    for (const p of this.particles) {
      const index = p.image.baseIndex + p.image.current;
      const x = Math.trunc(p.x), y = Math.trunc(p.y);
      // MLibrary.DrawBlend -> DXManager.SetBlend(true, rate) in BlendMode.NORMAL: SourceAlpha / One; the rate only feeds the
      // blend factor, which NORMAL never reads, so it is passed as 1.
      if (p.blend) painter.drawBlend(WEATHER_LIB, index, x, y, true, 1, p.colour);
      // MLibrary.Draw(index, point, colour, offSet, opacity) -> DXManager.DrawOpaque: colour.Alpha = opacity, normal alpha blend
      else painter.draw(WEATHER_LIB, index, x, y, true, 1, { ...p.colour, a: p.blendRate });
    }
  }
}

export class Weather {
  /** MapControl.Weather. */
  setting = 0;
  width = 1024;
  height = 768;
  private engines: Engine[] = [];
  private built = false;

  /** MapControl.Weather = p.WeatherParticles / p.Weather, then MapControl.UpdateWeather. */
  set(setting: number, now = performance.now()): void {
    this.setting = setting;
    this.rebuild(now);
  }

  /** MapControl.UpdateWeather: drop every engine and build one per set flag (WeatherSetting value order). */
  rebuild(now = performance.now()): void {
    this.engines = [];
    this.built = true;
    const W = this.width, H = this.height;
    const s = this.setting;
    const img = (index: number, count = 1, drawMs = 50) => new ImageInfo(index, count, drawMs, now);
    const leaves = () => [img(359, 170, 50), img(531, 55, 50), img(587, 200, 50)];
    /** The static grids: one particle per `step` cell from -step to the screen edge + step. */
    const grid = (engine: Engine, step: number, vx: number, vy: number) => {
      for (let y = -step; y < H + step; y += step)
        for (let x = -step; x < W + step; x += step) {
          const p = engine.newParticle(now);
          if (!p) continue;
          this.setPosition(p, x, y);
          p.vx = vx; p.vy = vy;
        }
      engine.generate = false;
    };
    const has = (flag: number) => (s & flag) === flag;

    if (has(WeatherSetting.Fog)) {
      const e = new Engine([img(0)], "Fog", this);
      grid(e, 512, 2, -2);
      this.engines.push(e);
    }
    if (has(WeatherSetting.RedEmber)) this.engines.push(new Engine([img(1, 9, 150)], "RedFogEmber", this));
    if (has(WeatherSetting.WhiteEmber)) this.engines.push(new Engine([img(1, 9, 150)], "WhiteEmber", this));
    if (has(WeatherSetting.YellowEmber)) this.engines.push(new Engine([img(1, 9, 100)], "YellowEmber", this));
    // FireParticle: the desktop's textures are commented out (TODO), so its Bird engine never makes a particle.
    if (has(WeatherSetting.FireParticle)) this.engines.push(new Engine([], "Bird", this));
    if (has(WeatherSetting.Snow)) {
      const e = new Engine([img(43, 20, 50)], "Snow", this);
      grid(e, 400, 1, -1);
      this.engines.push(e);
    }
    if (has(WeatherSetting.Rain)) {
      const e = new Engine([img(164, 150, 50)], "Rain", this);
      grid(e, 512, 0, 0);
      this.engines.push(e);
    }
    if (has(WeatherSetting.Leaves)) {
      const e = new Engine(leaves(), "Leaves", this);
      grid(e, 512, 0, 0);
      this.engines.push(e);
    }
    if (has(WeatherSetting.FireyLeaves)) {
      const e = new Engine(leaves(), "FireyLeaves", this);
      grid(e, 512, 0, 0);
      this.engines.push(e);
    }
    if (has(WeatherSetting.PurpleLeaves)) {
      const e = new Engine(leaves(), "PurpleLeaves", this);
      grid(e, 512, 0, 0);
      this.engines.push(e);
    }
  }

  /** GameScene.Process: `foreach (ParticleEngine pe in ParticleEngines) pe.Process();`. `width`/`height` = the map view size. */
  update(now: number, width: number, height: number): void {
    if (width !== this.width || height !== this.height) {
      this.width = width;
      this.height = height;
      if (this.built && this.setting !== 0) this.rebuild(now);
    }
    for (const e of this.engines) e.process(now);
  }

  /** MapControl.CreateTexture: after DrawObjects, before the lights. */
  draw(painter: Painter): void {
    for (const e of this.engines) e.draw(painter);
  }

  /** Particle.Update / FogParticle.Update / SnowParticle.Update: Position += Velocity every 50 ms. */
  updateParticle(p: Particle, now: number): void {
    if (now < p.nextUpdate) return;
    if (p.nextUpdate < now - MAX_BEHIND_MS) p.nextUpdate = now;
    while (now >= p.nextUpdate) {
      p.nextUpdate += STEP_MS;
      this.setPosition(p, p.x + p.vx, p.y + p.vy);
    }
  }

  /** Particle.Position setter: on a change, OnPositionChanged may wrap the particle to the other side (recursively). */
  setPosition(p: Particle, x: number, y: number): void {
    if (p.x === x && p.y === y) return;
    p.x = x; p.y = y;
    this.onPositionChanged(p);
  }

  /** Particle.OnPositionChanged (base: image-size grid) / SnowParticle.OnPositionChanged (fixed 400 grid). */
  private onPositionChanged(p: Particle): void {
    const size = p.image.size();
    if (!size || size.w === 0 || size.h === 0) return;
    const W = this.width, H = this.height;
    let xwidth: number, ywidth: number;
    if (p.wrap === "snow") {
      xwidth = 400 * (Math.ceil(W / 400) + 2);
      ywidth = 400 * (Math.ceil(H / 400) + 2);
    } else {
      xwidth = size.w * (Math.ceil(W / size.w) + 2);
      ywidth = size.h * (Math.ceil(H / size.h) + 2);
    }
    if (p.y < -size.h * 2) this.setPosition(p, p.x, p.y + ywidth);
    else if (p.y > H + size.h) this.setPosition(p, p.x, p.y - ywidth);
    else if (p.x < -size.w * 2) this.setPosition(p, p.x + xwidth, p.y);
    else if (p.x > W + size.w) this.setPosition(p, p.x - xwidth, p.y);
  }
}

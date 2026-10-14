// Web-only snow / rain / wind state for the Enhanced atmosphere (atmosphere.ts; no desktop counterpart). Pure bookkeeping, no GL:
// - one gusting wind, integrated into a drift (world pixels) that the fog and snowfall shaders scroll by, so a gust never jumps the pattern;
// - snow cover that builds up over SNOW_BUILD_MS after a snowy map is entered (reset on map change);
// - footprints: every live player / monster leaving a cell on a snowy map leaves a pair of prints there that fade over FOOTPRINT_MS;
// - lightning on rainy maps every 9-27 s: a whole-screen strobe, then thunder after 0.4-2.6 s;
// - rain intensity swelling between drizzle and downpour over a minute or two (rainfall, ripples, splashes, wetness and tint follow it);
// - wetness: the ground soaks up over WET_RISE_S of rain (puddles spread, reflections strengthen) and dries over WET_DRY_S after it stops.

import { ObjectType } from "../protocol/enums.generated.ts";
import { CELL_W, CELL_H, type MapObj } from "./objects.ts";
import { WeatherSetting } from "./weather.ts";

const SNOW_BUILD_MS = 180000;
const FOOTPRINT_MS = 40000;
const MAX_FOOTPRINTS = 600;
const WET_RISE_S = 70; // time constant of soaking up
const WET_DRY_S = 150; // time constant of drying out
/** Wetness on entering a map where it is already raining (it has been raining there before the player arrived). */
const WET_ON_ENTRY = 0.55;
/** Scene lab knob (play/lab.ts; the game never sets it): wetness >= 0 holds the ground at that wetness (0..1) instead of soaking up / drying. */
export const wetTuning = { wetness: -1 };
/** SoundList: the MapLightning crack (SpellObject MapLightning plays it). */
export const THUNDER_SOUND = 8301;

export interface Footprint {
  /** World pixels of the cell centre. */
  wx: number; wy: number;
  /** Unit direction of travel (screen axes). */
  dx: number; dy: number;
  born: number;
}

export class WeatherFx {
  snow = false;
  rain = false;
  /** Integrated wind: multiply by a layer's speed (world px / s) for its drift. */
  driftUnit = 0;
  /** This frame's gust factor (about 0.4..1.25). */
  gust = 1;
  /** 0..1: how soaked the ground is (follows the rain with a lag, dries after it). */
  wetness = 0;
  readonly footprints: Footprint[] = [];
  private mapKey: object | null = null;
  private snowSince = 0;
  private lastNow = -1;
  private readonly lastCell = new WeakMap<MapObj, { X: number; Y: number }>();
  private nextStrike = Infinity;
  private thunderAt = Infinity;

  /** Once per frame (alive or not, so the wind and timers keep running). `weather` = MapControl.Weather flags. Returns what happened this frame. */
  update(now: number, weather: number, mapKey: object, objects: readonly MapObj[]): { strike: boolean; thunder: boolean } {
    const snow = (weather & WeatherSetting.Snow) !== 0;
    const rain = (weather & WeatherSetting.Rain) !== 0;
    if (mapKey !== this.mapKey) {
      this.mapKey = mapKey;
      this.wetness = rain ? WET_ON_ENTRY : 0;
      this.footprints.length = 0;
      this.snowSince = now;
      this.nextStrike = Infinity;
      this.thunderAt = Infinity;
    }
    if (snow && !this.snow) this.snowSince = now;
    if (rain && !this.rain) this.nextStrike = now + 4000 + Math.random() * 8000;
    if (!rain) { this.nextStrike = Infinity; this.thunderAt = Infinity; }
    this.snow = snow;
    this.rain = rain;

    const dt = this.lastNow < 0 ? 0 : Math.min(Math.max(now - this.lastNow, 0), 100) / 1000;
    this.lastNow = now;
    const t = now / 1000;
    this.gust = 0.8 + 0.35 * Math.sin(t * 0.21) * Math.sin(t * 0.077 + 1.3) + 0.12 * Math.sin(t * 0.93);
    this.driftUnit += this.gust * dt;
    if (rain) this.wetness += (0.35 + 0.65 * this.rainIntensity(now) - this.wetness) * (1 - Math.exp(-dt / WET_RISE_S));
    else if (this.wetness > 0) this.wetness = this.wetness < 0.01 ? 0 : this.wetness * Math.exp(-dt / WET_DRY_S);
    if (wetTuning.wetness >= 0) this.wetness = Math.min(1, wetTuning.wetness);

    if (snow) this.trackFootprints(now, objects);
    for (let i = 0; i < this.footprints.length && now - this.footprints[i]!.born > FOOTPRINT_MS; ) this.footprints.shift();

    let strike = false, thunder = false;
    if (now >= this.nextStrike) {
      strike = true;
      this.thunderAt = now + 400 + Math.random() * 2200;
      this.nextStrike = now + 9000 + Math.random() * 18000;
    }
    if (now >= this.thunderAt) {
      thunder = true;
      this.thunderAt = Infinity;
    }
    return { strike, thunder };
  }

  /** 0.3..1: how hard it is raining now (slow swells; 0 when dry). */
  rainIntensity(now: number): number {
    if (!this.rain) return 0;
    const t = now / 1000;
    return Math.min(1, Math.max(0.3, 0.62 + 0.38 * Math.sin(t / 37) * Math.sin(t / 91 + 0.7) + 0.08 * Math.sin(t / 11)));
  }

  /** True while the ground is wet (raining, or still drying). */
  get wet(): boolean {
    return this.rain || this.wetness > 0;
  }

  /** Horizontal lean of falling rain per pixel of fall, from the gust. */
  get rainSlant(): number {
    return Math.min(0.32, Math.max(0.03, 0.25 * this.gust - 0.02));
  }

  /** 0..1 of the snow cover's build-up target. */
  snowCover(now: number): number {
    if (!this.snow) return 0;
    return 0.35 + 0.4 * Math.min(1, (now - this.snowSince) / SNOW_BUILD_MS);
  }

  /** 1 when fresh, 0 when gone. */
  static footprintFade(f: Footprint, now: number): number {
    const age = (now - f.born) / FOOTPRINT_MS;
    return age >= 1 ? 0 : 1 - age * age;
  }

  private trackFootprints(now: number, objects: readonly MapObj[]): void {
    for (const ob of objects) {
      if (ob.dead || (ob.race !== ObjectType.Player && ob.race !== ObjectType.Monster)) continue;
      const last = this.lastCell.get(ob);
      const { X, Y } = ob.location;
      if (!last) { this.lastCell.set(ob, { X, Y }); continue; }
      if (last.X === X && last.Y === Y) continue;
      const dx = X - last.X, dy = (Y - last.Y) * (CELL_H / CELL_W);
      const len = Math.hypot(dx, dy) || 1;
      // a print where the step left and one where it landed (a run crosses two cells)
      const steps = Math.max(Math.abs(X - last.X), Math.abs(Y - last.Y));
      for (let s = 1; s <= Math.min(steps, 3); s++) {
        const cx = last.X + Math.round(((X - last.X) * s) / steps), cy = last.Y + Math.round(((Y - last.Y) * s) / steps);
        this.footprints.push({ wx: cx * CELL_W + CELL_W / 2, wy: cy * CELL_H + CELL_H / 2, dx: dx / len, dy: dy / len, born: now });
      }
      if (this.footprints.length > MAX_FOOTPRINTS) this.footprints.splice(0, this.footprints.length - MAX_FOOTPRINTS);
      last.X = X;
      last.Y = Y;
    }
  }
}

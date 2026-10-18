// Web-only atmosphere on top of Enhanced lighting (no desktop counterpart; Classic stays the desktop's MapControl.DrawLights exactly).
// Runs only while gameSettings.Lighting is Enhanced (and available), gameSettings.Atmosphere is on (atmosphereOn: not under the touch battery saver), the post programs linked and the user is alive
// (the dead-user grayscale keeps its own path). Four parts:
// - Glow (bloom): every additive DrawBlend of the frame (spells, blended monster frames, map fire tiles, embers - not snow / rain / plain leaves) is
//   recorded by the Painter and replayed into a half-resolution half-float target, blurred twice (render/postFx.ts) and added over the scene.
// - Lit particles: ember and fiery-leaf weather particles add small flickering lights to the Enhanced light map (night only, like every lamp).
// - Flashes: spell impacts and casts (keyed by the effect's library + first frame, so player, monster and server effects all match) and criticals
//   add short lights, and lightning adds a strobing whole-screen flash; these draw in daylight too (the light map then brightens up to the knee).
// - Heat haze: fire effects, fire walls and meteor / lava ground draw soft blobs into a mask that wobbles the scene's UVs in the composite.
// - Fog (maps with WeatherSetting.Fog; replaces the Weather.Lib fog sprites): world-anchored shader fog, a ground mist between the floor and the
//   objects and a lighter high layer over them, both before the light pass so lamps light it; the previous frame's light map thins it at lamp cores.
// - Depth (dungeons: the map's own light is permanent Night): the scene darkens with distance from the player in the composite.
// - Snow (WeatherSetting.Snow, on top of the Weather.Lib snow): cover that builds up on the ground, footprints (weatherFx.ts), three parallax layers of
//   shader snowfall carried by the wind, frost at the screen edges and a cool tint.
// - Rain (WeatherSetting.Rain; replaces the Weather.Lib rain tiles): three parallax layers of shader rain leaning with the wind plus big soft
//   streaks just in front of the camera, gust sheets of heavier rain sweeping across with the wind, a grey wash in heavy rain, streaks glinting
//   around lamps, lightning (a whole-screen strobe) with delayed thunder, a grey tint, and procedural rain sound (audio.ts setRain); all of it
//   swells between drizzle and downpour. One gusting wind (weatherFx.ts) drives the fog, snowfall and rain.
// - Wet ground (while raining and while it dries after; weatherFx wetness): only on floor tiles (a per-cell mask of the cells around the player;
//   never the black past the map's edge), puddles that spread the longer it rains, ripples (mostly in puddles and on water cells), splashes,
//   eaves drips falling from walls, buildings and trees to their foot, splashes on the heads and shoulders of everyone standing in the rain,
//   players' footfalls splashing through puddles and water (a ring under the foot, a spray of droplets, a slap for the user's own steps), and
//   screen-space reflections: the ground pass marks wet floor in the scene's alpha, a snapshot after the objects separates wet floor from what
//   stands on it, and a half-resolution pass mirrors objects and lamps in the puddles for the composite.

import type { WebGl2Renderer, GLTextureRef, RenderTargetRef } from "../render/backend.ts";
import type { SpriteBatch, SpriteTextureRef } from "../render/batcher.ts";
import { PostFx, type FogLayer, type CellMaskView, type WetInputs, type ReflectionSource } from "../render/postFx.ts";
import { WeatherFx, THUNDER_SOUND } from "./weatherFx.ts";
import { WeatherSetting } from "./weather.ts";
import { MirAction, Monster, ObjectType, Spell } from "../protocol/enums.generated.ts";
import type { MapCells } from "../map/mapReader.ts";
import { MAP_LIBS, sprite, visiblePixel } from "./libraries.ts";
import { CELL_W, CELL_H, type MapObj } from "./objects.ts";
import { Lib, type Effect } from "./effects.ts";
import { atmosphereOn, gameSettings } from "./settings.ts";
import type { LightSource, Rgb } from "./lighting.ts";

/** A light added to the Enhanced light map: centre (x, y) and radii in screen pixels. `scaled` lights fade out in daylight like lamps; flashes do not. */
export interface PointLight {
  x: number; y: number; rx: number; ry: number;
  colour: Rgb;
  intensity: number;
  scaled: boolean;
}

/** One recorded draw for the glow replay: an additive draw (Painter.drawBlend, `rate` > 0), or an occluder (`rate` = 0: a normal draw over an
 *  earlier glow, replayed black so walls drawn in front of a glowing tile hide its glow too). */
export interface GlowDraw {
  tex: SpriteTextureRef;
  x: number; y: number; w: number; h: number;
  colour: { r: number; g: number; b: number; a: number };
  rate: number;
}

/** Weather (weather.ts): the glowing particles' centres in screen pixels. */
export interface EmberSource {
  forEachEmber(fn: (x: number, y: number, colour: Rgb, seed: number) => void): void;
}

const GLOW_STRENGTH = 0.85;
const HAZE_PIXELS = 2.6; // peak UV wobble at mask 1
const DUNGEON_DEPTH = 0.6; // darkening at the screen edge in dungeons
const GROUND_MIST: FogLayer = { density: 0.42, floor: 0.3, scale: 1 / 230, wind: [16, -5], colour: [0.78, 0.8, 0.84] };
const HIGH_FOG: FogLayer = { density: 0.2, floor: 0.15, scale: 1 / 420, wind: [26, -8], colour: [0.86, 0.88, 0.91] };
const SNOWFALL_AMOUNT = 0.55; // share of grid cells with a flake
const SNOW_WIND = 22; // world px / s of snowfall drift at gust 1
// rain at intensity 1 (weatherFx.rainIntensity swells 0.3..1)
const RAIN_AMOUNT = 0.5; // share of streak slots filled (sparse, so the drops read apart)
const RAIN_WASH = 0.07; // grey air
const RIPPLES = 0.7; // share of ground cells rippling (the drop-size grids scale this)
const SPLASHES = 0.5; // share of splash cells splashing
const DRIP_RATE = 0.6; // share of eaves-drip cycles that drip in a downpour (drying eaves drip at up to half of it)
const NEAR_RAIN = 0.55; // fill of the out-of-focus layer in front of the camera
const SHEET_SPEED = 0.45; // gust-sheet drift in noise units per second at gust 1 (about 140 world px / s)
const BODY_SPLASHES = 7; // splashes per second on each figure standing in a downpour
const BODY_SPLASH_MS = 260;
const MAX_BODY_SPLASHES = 240;
const FOOT_SPLASH_MS = 520;
const MAX_FOOT_SPLASHES = 120;
/** Footfalls splash only where the ground is at least this much puddle (0..1, puddleAt). */
const FOOT_PUDDLE_MIN = 0.2;
/** The moving actions whose frames 1 and 4 put a foot down (6-frame walk / run cycles). */
const STEPPING: ReadonlySet<number> = new Set([MirAction.Walking, MirAction.Running, MirAction.WalkingBow, MirAction.RunningBow, MirAction.Sneek,
  MirAction.MountWalking, MirAction.MountRunning]);
const MASK_REFRESH_MS = 1500; // rebuild the cell mask this often even without moving (tall objects' sizes arrive as they load)
const RAIN_TINT: [number, number, number] = [0.86, 0.89, 0.94];
const SNOW_TINT: [number, number, number] = [0.97, 1.0, 1.05];
const NO_TINT: [number, number, number] = [1, 1, 1];
const LIGHTNING: Rgb = [170, 195, 255];
// Saturated on purpose: the light map adds up and the composite knees each channel, so a mild orange climbs to white where lights overlap.
const FIRE: Rgb = [255, 112, 34];
/** The Enhanced light colour of fire effects and burning spell objects (the desktop lights them white). */
export const FIRE_LIGHT: Rgb = [255, 120, 40];
const CRIT: Rgb = [255, 236, 190];
const DIRS = [[0, -1], [1, -1], [1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1]] as const; // MirDirection

interface FlashSpec {
  rx: number; ry: number;
  colour: Rgb;
  intensity: number;
  duration: number;
  /** Lightning: a double strobe instead of a smooth decay. */
  strike?: boolean;
  /** Whole-screen flash at peak, in light-map units (0..1 of white). */
  screen?: number;
}
interface HazeSpec { rx: number; ry: number; strength: number }
interface EffectSpec { flash?: FlashSpec; haze?: HazeSpec; /** A fire projectile: orange light only. */ fireLight?: boolean; /** Lightning (Magic 970 + dir * 20): flashes along the bolt. */ beam?: boolean }

interface Flash extends FlashSpec { wx: number; wy: number; start: number }
/** A raindrop hitting a figure: offset from its draw location (the top of its silhouette in a random column). */
interface BodySplash { ob: MapObj; ox: number; oy: number; born: number; seed: number }
/** A footfall in a puddle, in world pixels: `k` = how much puddle (0..1) sizes it; (dx, dy) = the step's unit direction on screen. */
interface FootSplash { wx: number; wy: number; born: number; k: number; seed: number; dx: number; dy: number; run: boolean }

// GROUND_FS puddle() in float32 on the CPU (Math.fround after every operation, as the GPU rounds), so a footfall finds the puddles the shader
// draws; a rare mismatch sits on a puddle's soft edge. Keep in step with NOISE_GLSL hash / noise / fbm and puddle() in render/postFx.ts.
const f32 = Math.fround;
const fract = (v: number): number => f32(v - Math.floor(v));
const H1 = f32(123.34), H2 = f32(456.21), H3 = f32(45.32), F1 = f32(2.03), F2 = f32(17.1), F3 = f32(9.2);
function hash(x: number, y: number): number {
  let px = fract(f32(x * H1)), py = fract(f32(y * H2));
  const d = f32(f32(px * f32(px + H3)) + f32(py * f32(py + H3)));
  px = f32(px + d);
  py = f32(py + d);
  return fract(f32(px * py));
}
function valueNoise(x: number, y: number): number {
  const ix = Math.floor(x), iy = Math.floor(y), fx = f32(x - ix), fy = f32(y - iy);
  const ux = f32(fx * fx * (3 - 2 * fx)), uy = f32(fy * fy * (3 - 2 * fy));
  const a = hash(ix, iy), b = hash(ix + 1, iy), c = hash(ix, iy + 1), d = hash(ix + 1, iy + 1);
  const m1 = a + (b - a) * ux, m2 = c + (d - c) * ux;
  return m1 + (m2 - m1) * uy;
}
function fbm(x: number, y: number): number {
  let v = 0, a = 0.5;
  for (let i = 0; i < 4; i++) {
    v += a * valueNoise(x, y);
    x = f32(f32(x * F1) + F2);
    y = f32(f32(y * F1) + F3);
    a *= 0.5;
  }
  return v;
}
const smoothstep = (e0: number, e1: number, x: number): number => {
  const t = Math.min(1, Math.max(0, (x - e0) / (e1 - e0)));
  return t * t * (3 - 2 * t);
};

/** A white `size` x `size` RGBA ring (soft-edged, radius ~0.8 of the half size): footfall ripples. */
function softRing(size: number): Uint8Array {
  const data = new Uint8Array(size * size * 4);
  const c = (size - 1) / 2;
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const d = Math.abs(Math.hypot(x - c, y - c) / (c + 0.5) - 0.8) / 0.14;
    const i = (y * size + x) * 4;
    data[i] = data[i + 1] = data[i + 2] = 255;
    data[i + 3] = Math.round(Math.max(0, 1 - d * d) * 255);
  }
  return data;
}

const strike = (rx: number, intensity: number, duration: number, screen = 0): FlashSpec =>
  ({ rx, ry: Math.round(rx * 0.76), colour: LIGHTNING, intensity, duration, strike: true, screen });
const burst = (rx: number, intensity: number, duration: number, screen = 0): FlashSpec =>
  ({ rx, ry: Math.round(rx * 0.76), colour: FIRE, intensity, duration, screen });
const HAZE_SMALL: HazeSpec = { rx: 46, ry: 64, strength: 0.6 };
const HAZE_MEDIUM: HazeSpec = { rx: 80, ry: 90, strength: 0.8 };
const HAZE_LARGE: HazeSpec = { rx: 120, ry: 100, strength: 0.9 };

/** Effects by `${lib}:${baseIndex}` (the indices spellVisuals.ts / monsterSpecials.ts create them with). */
const EFFECTS = new Map<string, EffectSpec>();
const add = (lib: string, index: number, spec: EffectSpec) => EFFECTS.set(`${lib}:${index}`, spec);
// Lightning family
add(Lib.Magic2, 20, { flash: strike(130, 1.6, 220) });                 // ThunderBolt cast
add(Lib.Magic2, 10, { flash: strike(330, 3.5, 380, 0.18) });           // ThunderBolt strike
for (let d = 0; d < 8; d++) add(Lib.Magic, 970 + d * 20, { beam: true }); // Lightning
add(Lib.Magic, 1680, { flash: strike(520, 3.5, 600, 0.3) });           // ThunderStorm
add(Lib.Magic3, 590, { flash: strike(240, 2.4, 350, 0.08) });          // StormEscape cast
add(Lib.Magic3, 610, { flash: strike(240, 2.4, 350, 0.08) });          // StormEscape arrival
add(Lib.Magic, 1560, { flash: strike(130, 1.4, 220) });                // ElectricShock cast
add(Lib.Magic, 1570, { flash: strike(150, 1.8, 260) });                // ElectricShock hit
for (const i of [400, 410, 420]) add(Lib.Dragon, i, { flash: strike(380, 3.2, 380, 0.15) }); // MapLightning, RedThunderZuma / FrozenRedZuma
add(Lib.Monster(Monster.GeneralMeowMeow), 522, { flash: strike(380, 3.2, 420, 0.15) });     // GeneralMeowMeowThunder
// Fire family
add(Lib.Magic, 170, { flash: burst(150, 2.0, 350), haze: HAZE_SMALL });    // FireBall hit
add(Lib.Magic, 570, { flash: burst(220, 2.6, 450), haze: HAZE_MEDIUM });   // GreatFireBall / FireBounce / MeteorShower hit
add(Lib.Magic, 930, { flash: burst(110, 1.2, 300), haze: HAZE_SMALL });    // HellFire
add(Lib.Magic, 1660, { flash: burst(300, 3.0, 600, 0.05), haze: HAZE_LARGE }); // FireBang
add(Lib.Magic2, 140, { flash: burst(200, 2.4, 450), haze: HAZE_MEDIUM });  // FlameDisruptor
add(Lib.Magic2, 910, { flash: burst(260, 1.6, 700), haze: HAZE_LARGE });   // FlameField
add(Lib.Magic2, 1600, { flash: burst(260, 2.5, 500), haze: HAZE_LARGE });  // MeteorStrike ground
add(Lib.Magic2, 2320, { flash: burst(140, 1.4, 300), haze: HAZE_SMALL });  // FireBurst
add(Lib.Magic3, 1660, { flash: burst(260, 2.5, 500), haze: HAZE_LARGE });  // NapalmShot
add(Lib.Dragon, 440, { flash: burst(260, 2.2, 600), haze: HAZE_LARGE });   // MapLava
add(Lib.Dragon, 470, { haze: HAZE_LARGE });
add(Lib.Monster(Monster.FlameMage), 592, { flash: burst(170, 2.0, 400), haze: HAZE_SMALL });
add(Lib.Monster(Monster.FlameAssassin), 640, { flash: burst(150, 1.8, 350), haze: HAZE_SMALL });
add(Lib.Monster(Monster.FlameScythe), 586, { flash: burst(170, 2.0, 400), haze: HAZE_SMALL });
add(Lib.Monster(Monster.FlameQueen), 720, { flash: burst(240, 2.4, 500), haze: HAZE_MEDIUM });
// Fire projectiles in flight (createProjectile base indices): only their light turns orange
add(Lib.Magic, 10, { fireLight: true });   // FireBall (also monster fire balls)
add(Lib.Magic, 410, { fireLight: true });  // GreatFireBall / FireBounce / MeteorShower
add(Lib.Monster(Monster.FlameMage), 544, { fireLight: true });
add(Lib.Monster(Monster.FlameAssassin), 592, { fireLight: true });
for (let d = 0; d < 8; d++) add(Lib.Monster(Monster.FireCat), 248 + d * 10, { haze: HAZE_SMALL });
for (let d = 0; d < 8; d++) add(Lib.Monster(Monster.FlameSpear), 544 + d * 10, { haze: HAZE_SMALL });

/** Spell objects (SpellObject.cs) that shimmer while they burn. */
const HAZE_SPELL_OBJECTS = new Map<number, HazeSpec>([
  [Spell.FireWall, { rx: 40, ry: 70, strength: 0.75 }],
  [Spell.MeteorStrike, HAZE_LARGE],
]);

/** Atmosphere on: the warm light colour for a fire effect (any table entry with a fire flash, heat haze or fireLight), else null (keep its own colour). */
export function fireLightColour(e: Effect): Rgb | null {
  if (!atmosphereOn()) return null;
  const spec = EFFECTS.get(`${e.lib}:${e.baseIndex}`);
  return spec && (spec.fireLight || spec.haze || spec.flash?.colour === FIRE) ? FIRE_LIGHT : null;
}

/** Atmosphere on: the warm light colour for a burning spell object (FireWall, MeteorStrike), else null. */
export function fireSpellObjectLight(spell: number | undefined): Rgb | null {
  return atmosphereOn() && spell !== undefined && HAZE_SPELL_OBJECTS.has(spell) ? FIRE_LIGHT : null;
}

/** `tint` applied at strength `k` (0 = none, 1 = full). */
function mixTint(tint: [number, number, number], k: number): [number, number, number] {
  return [1 + (tint[0] - 1) * k, 1 + (tint[1] - 1) * k, 1 + (tint[2] - 1) * k];
}

/** A white `size` x `size` RGBA dot with a soft round alpha edge (footprints). */
function softDot(size: number): Uint8Array {
  const data = new Uint8Array(size * size * 4);
  const c = (size - 1) / 2;
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const d = Math.hypot(x - c, y - c) / (c + 0.5);
    const a = Math.max(0, 1 - d * d);
    const i = (y * size + x) * 4;
    data[i] = data[i + 1] = data[i + 2] = 255;
    data[i + 3] = Math.round(a * a * 255);
  }
  return data;
}

/** 0..1 brightness of a flash `t` ms in. */
function envelope(f: FlashSpec, t: number): number {
  if (t < 0 || t >= f.duration) return 0;
  const left = 1 - t / f.duration;
  if (f.strike) {
    const strobe = t < 50 ? 1 : t < 95 ? 0.25 : t < 150 ? 0.9 : 0.6;
    return strobe * Math.pow(left, 1.4);
  }
  return t < 40 ? t / 40 : left * left;
}

/** What one frame needs from the view to place things. */
export interface AtmosphereFrame {
  user: MapObj;
  objects: readonly MapObj[];
  mapEffects: readonly Effect[];
  spellObjects: Iterable<LightSource>;
  offSetX: number;
  offSetY: number;
  drawLocation: (ob: MapObj) => { x: number; y: number };
  /** The body image an object drew this frame (MapView.bodyOf), or null. */
  body: (ob: MapObj) => { lib: string | null; f: number } | null;
  embers: EmberSource | null;
  now: number;
}

export class Atmosphere {
  private readonly renderer: WebGl2Renderer;
  private post: PostFx | null = null;
  private scene: RenderTargetRef | null = null;
  private glow: RenderTargetRef | null = null;
  private glowTmp: RenderTargetRef | null = null;
  private haze: RenderTargetRef | null = null;
  /** Half-resolution reflectivity snapshot (postFx snapshotAlpha). */
  private wetMask: RenderTargetRef | null = null;
  /** The half-resolution puddle reflections (postFx.ts REFLECT_FS): the object mirror (RGBA8) and the lamps (RGBA16F). */
  private reflMirror: RenderTargetRef | null = null;
  private reflLamps: RenderTargetRef | null = null;
  private size = "";
  private white: GLTextureRef | null = null;
  private dot: GLTextureRef | null = null;
  private readonly fx = new WeatherFx();
  /** SoundManager.PlaySound (thunder); set by game.ts. */
  playSound: (id: number) => void = () => {};
  /** SoundManager.setRain (0 = dry); set by game.ts. Called every frame. */
  rainAudio: (level: number) => void = () => {};
  /** SoundManager.splash (the user's own footfalls in puddles); set by game.ts. */
  splashAudio: (level: number) => void = () => {};
  private ring: GLTextureRef | null = null;
  private readonly footSplashes: FootSplash[] = [];
  /** Each player's frame index last frame (a footfall = entering frame 1 or 4 of a step). */
  private readonly lastFoot = new WeakMap<MapObj, number>();
  private maskData: Uint8Array | null = null;
  private maskView: CellMaskView | null = null;
  private maskMap: object | null = null;
  private maskKey = "";
  private maskAt = 0;
  /** This frame's ground pass wrote the reflectivity alpha / the snapshot of it was taken. */
  private groundWet = false;
  private snapshotOk = false;
  /** This frame's ground ripple share (the composite ripples the lamp reflections with the same rings). */
  private ripplesNow = 0;
  private readonly bodySplashes: BodySplash[] = [];
  private lastSplashAt = 0;
  /** Last collect's drawLocation (body splashes follow their figure). */
  private place: ((ob: MapObj) => { x: number; y: number }) | null = null;
  private readonly flashes: Flash[] = [];
  private readonly seen = new WeakSet<Effect>();
  private readonly pendingCrits: MapObj[] = [];
  private readonly hazeBlobs: { x: number; y: number; rx: number; ry: number; strength: number }[] = [];
  /** This frame's light-map additions (EnhancedLightLayer reads them through LightContext). */
  readonly lights: PointLight[] = [];
  screenFlash: Rgb | null = null;
  /** The additive draws of this frame, recorded by the Painter while active. */
  readonly glowDraws: GlowDraw[] = [];
  private active = false;
  private camX = 0;
  private camY = 0;
  private fogOn = false;
  private weather = 0;
  private depth = 0;
  private centre = { x: 0, y: 0 };

  constructor(renderer: WebGl2Renderer) {
    this.renderer = renderer;
  }

  /** Whether this frame runs the atmosphere (decided in `begin`). */
  get running(): boolean {
    return this.active;
  }

  /** Call after the frame's begin(): binds the scene target when the atmosphere runs this frame. `enhanced` = SceneLights.useEnhanced. */
  begin(batch: SpriteBatch, width: number, height: number, enhanced: boolean, userDead: boolean): boolean {
    this.active = false;
    this.glowDraws.length = 0;
    if (!enhanced || userDead || !atmosphereOn()) {
      this.flashes.length = 0;
      return false;
    }
    if (!this.post) this.post = new PostFx(this.renderer);
    if (!this.post.available || !this.ensureTargets(width, height)) return false;
    batch.Flush();
    this.renderer.bindRenderTarget(this.scene);
    this.renderer.clearColour(0, 0, 0, 255); // MapControl.CreateTexture: Clear(Black)
    this.active = true;
    return true;
  }

  /** Call every frame after `begin`: the camera (world pixel of the screen's top-left), the player's screen centre, the map (its identity resets snow
   *  cover and footprints), MapControl.Weather and the dungeon flag. Runs the wind / snow / lightning bookkeeping. */
  setView(user: MapObj, offSetX: number, offSetY: number, map: MapCells, weather: number, dungeon: boolean, objects: readonly MapObj[], now: number): void {
    this.camX = (user.movement.X - offSetX) * CELL_W - user.offsetMove.X;
    this.camY = (user.movement.Y - offSetY) * CELL_H - user.offsetMove.Y;
    this.centre = { x: offSetX * CELL_W + CELL_W / 2, y: offSetY * CELL_H - 12 };
    this.weather = weather;
    this.fogOn = (weather & WeatherSetting.Fog) !== 0;
    this.depth = dungeon ? DUNGEON_DEPTH : 0;
    const happened = this.fx.update(now, weather, map, objects);
    this.rainAudio(atmosphereOn() && this.fx.rain ? this.fx.rainIntensity(now) * (0.85 + 0.15 * Math.min(this.fx.gust, 1.2)) : 0);
    if (!this.active) return;
    if (this.fx.wet) this.updateCellMask(map, user, offSetX, offSetY, now);
    if (happened.strike) this.flashes.push({ ...strike(0, 0, 560, 0.38), wx: 0, wy: 0, start: now });
    if (happened.thunder) this.playSound(THUNDER_SOUND);
  }

  /** True when this frame draws shader rain (the Weather.Lib rain tiles are then skipped). */
  get shaderRain(): boolean {
    return this.active && this.fx.rain;
  }

  /** True when this frame draws shader fog (the Weather.Lib fog sprites are then skipped). */
  get shaderFog(): boolean {
    return this.active && this.fogOn;
  }

  /** The cell mask around the player (postFx MASK_GLSL): R floor tile, G water, B eaves-drip base (the row under a tall, non-blended front object,
   *  across its width, where rain lands). Rebuilt when the player's cell or the map changes, and every MASK_REFRESH_MS. */
  private updateCellMask(map: MapCells, user: MapObj, offSetX: number, offSetY: number, now: number): void {
    const cols = offSetX * 2 + 10, rows = offSetY * 2 + 14;
    const x0 = user.movement.X - offSetX - 4, y0 = user.movement.Y - offSetY - 4;
    const key = `${x0},${y0},${cols},${rows}`;
    if (map === this.maskMap && key === this.maskKey && now - this.maskAt < MASK_REFRESH_MS) return;
    this.maskMap = map;
    this.maskKey = key;
    this.maskAt = now;
    const n = cols * rows * 4;
    if (!this.maskData || this.maskData.length !== n) this.maskData = new Uint8Array(n);
    const d = this.maskData, H = map.height;
    d.fill(0);
    for (let r = 0; r < rows; r++) {
      const y = y0 + r;
      if (y < 0 || y >= H) continue;
      for (let c = 0; c < cols; c++) {
        const x = x0 + c;
        if (x < 0 || x >= map.width) continue;
        const i = x * H + y, bi = (x & ~1) * H + (y & ~1); // back tiles cover 2 x 2 cells from even cells (MapControl.DrawFloor)
        const back = map.backImage[bi]!;
        const o = (r * cols + c) * 4;
        if ((back !== 0 && map.backIndex[bi] !== -1 && (back & 0x1fffffff) > 0) || (map.middleImage[i]! > 0 && map.middleIndex[i] !== -1)) d[o] = 255;
        if (map.fishing[i]) d[o] = d[o + 1] = 255;
        d[o + 3] = 255;
      }
    }
    for (let y = Math.max(0, y0 - 1); y < Math.min(H, y0 + rows - 1); y++) {
      const r = y + 1 - y0;
      for (let x = Math.max(0, x0 - 6); x < Math.min(map.width, x0 + cols); x++) {
        const i = x * H + y;
        const fi = map.frontIndex[i]!, index = (map.frontImage[i]! & 0x7fff) - 1;
        if (index < 0 || fi === -1 || (map.frontAnimationFrame[i]! & 0x80) !== 0) continue; // blended (fire, glow) tiles do not drip
        const s = sprite(MAP_LIBS[fi] ?? null, index);
        if (!s || s.height < CELL_H * 2 || s.width < CELL_W / 2) continue;
        for (let k = 0, w = Math.max(1, Math.round(s.width / CELL_W)); k < w; k++) {
          const c = x + k - x0;
          if (c < 0 || c >= cols || r < 0 || r >= rows) continue;
          const o = (r * cols + c) * 4;
          if (d[o]) d[o + 2] = 255;
        }
      }
    }
    this.maskView = { originX: x0 * CELL_W - offSetX, originY: y0 * CELL_H, cellX: x0, cellY: y0, cols, rows }; // DrawFloor's "- OffSetX" px
    this.post!.setCellMask(d, cols, rows);
  }

  /** The wet-ground inputs of this frame, or null (dry, or no mask yet). */
  private wetInputs(now: number): WetInputs | null {
    const fx = this.fx;
    if (!fx.wet || !this.maskView) return null;
    const drip = fx.rain ? DRIP_RATE * (0.3 + 0.7 * fx.rainIntensity(now)) : DRIP_RATE * 0.5 * fx.wetness;
    return { wetness: fx.wetness, drip, mask: this.maskView };
  }

  /** After the floor, before the objects: snow cover, footprints, wet ground and the ground mist. `lightMap` = last frame's Enhanced map. Returns true
   *  when it changed GL blend state. */
  drawGround(batch: SpriteBatch, width: number, height: number, lightMap: RenderTargetRef | null): boolean {
    this.groundWet = false;
    if (!this.active) return false;
    const fx = this.fx, post = this.post!, now = performance.now(), time = (now % 3600000) * 0.001;
    if (!fx.snow && !fx.wet && !this.fogOn) return false;
    batch.Flush();
    if (fx.snow) {
      post.ground(width, height, "snow", fx.snowCover(now), 0, 0, this.camX, this.camY, time);
      this.drawFootprints(batch, width, height, now);
    }
    const wet = this.wetInputs(now);
    if (wet) {
      const i = fx.rainIntensity(now); // 0 once dry: no new ripples or splashes, the ground stays dark and reflective while it dries
      this.ripplesNow = RIPPLES * (fx.rain ? 0.25 + 0.75 * i : 0);
      post.ground(width, height, "wet", 0.5 + 0.5 * fx.wetness, this.ripplesNow, SPLASHES * (fx.rain ? 0.25 + 0.75 * i : 0),
        this.camX, this.camY, time, wet);
      this.groundWet = true;
      this.drawFootRipples(batch, width, height, now);
    }
    if (this.fogOn) this.fogLayer(GROUND_MIST, width, height, lightMap);
    this.renderer.setBlend(false, 1, "NORMAL");
    return true;
  }

  /** After the objects and weather, before the lights: the reflectivity snapshot, splashes on figures, the rain (and eaves drips), the snowfall and
   *  the high fog. Returns true when it changed GL blend state. */
  drawSky(batch: SpriteBatch, width: number, height: number, lightMap: RenderTargetRef | null): boolean {
    this.snapshotOk = false;
    if (!this.active || (!this.fx.snow && !this.fx.wet && !this.fogOn)) return false;
    batch.Flush();
    const fx = this.fx, now = performance.now(), time = (now % 3600000) * 0.001;
    if (this.groundWet && this.wetMask) {
      this.post!.snapshotAlpha(this.scene!, this.wetMask);
      this.snapshotOk = true;
    }
    if (fx.rain) this.drawBodySplashes(batch, now);
    if (fx.wet) this.drawFootSpray(batch, width, height, now);
    const wet = this.wetInputs(now);
    if (fx.rain || wet) {
      const i = fx.rainIntensity(now); // dry: only the drying eaves drip
      this.post!.rainfall(width, height, fx.rain ? RAIN_AMOUNT * (0.3 + 0.7 * i) : 0, fx.rainSlant, RAIN_WASH * i, this.camX, this.camY, time,
        { light: lightMap, sheet: fx.driftUnit * SHEET_SPEED, near: fx.rain ? NEAR_RAIN * (0.2 + 0.8 * i) : 0, wet });
    }
    if (this.fx.snow) this.post!.snowfall(width, height, SNOWFALL_AMOUNT, this.camX, this.camY, this.fx.driftUnit * SNOW_WIND, time);
    if (this.fogOn) this.fogLayer(HIGH_FOG, width, height, lightMap);
    this.renderer.setBlend(false, 1, "NORMAL");
    return true;
  }

  private fogLayer(layer: FogLayer, width: number, height: number, lightMap: RenderTargetRef | null): void {
    const d = this.fx.driftUnit;
    this.post!.fog(width, height, layer, this.camX, this.camY, layer.wind[0] * d, layer.wind[1] * d, lightMap);
  }

  /** Raindrops bursting on figures (collect spawns them): a flash and two droplets thrown up and out, following the figure. Lit by the light pass. */
  private drawBodySplashes(batch: SpriteBatch, now: number): void {
    const list = this.bodySplashes;
    if (list.length === 0 || !this.place) return;
    const r = this.renderer;
    if (!this.dot) this.dot = r.createRgbaTexture(softDot(16), 16, 16);
    r.setBlend(false, 1, "NORMAL");
    const src = { x: 0, y: 0, width: 16, height: 16 };
    for (let i = 0; i < list.length; i++) {
      const sp = list[i]!;
      const age = (now - sp.born) / BODY_SPLASH_MS;
      if (age >= 1 || sp.ob.dead) { list.splice(i--, 1); continue; }
      const at = this.place(sp.ob);
      const x = at.x + sp.ox, y = at.y + sp.oy, fade = 1 - age;
      batch.DrawScaled(this.dot, src, x - 2.5, y - 1.5, 5, 3, { r: 0.9, g: 0.94, b: 1, a: 0.8 * fade * fade });
      for (const side of [-1, 1]) {
        const dx = side * (3 + 5 * sp.seed) * age, dy = -8 * 4 * age * (1 - age) + 3 * age;
        batch.DrawScaled(this.dot, src, x + dx - 1, y + dy - 1, 2, 2, { r: 0.88, g: 0.92, b: 1, a: 0.85 * fade });
      }
    }
    batch.Flush();
  }

  /** New body splashes: each live figure in view catches about BODY_SPLASHES drops a second in a downpour, at the top of its silhouette in a random
   *  column (the body's hit mask), i.e. on its head, shoulders and arms. */
  private spawnBodySplashes(f: AtmosphereFrame): void {
    const now = f.now, dt = Math.min(100, Math.max(0, now - this.lastSplashAt));
    this.lastSplashAt = now;
    if (!this.fx.rain || dt === 0) return;
    const chance = (BODY_SPLASHES * this.fx.rainIntensity(now) * dt) / 1000;
    for (const ob of f.objects) {
      if (this.bodySplashes.length >= MAX_BODY_SPLASHES) break;
      if (ob.dead || ob.hidden || (ob.race !== ObjectType.Player && ob.race !== ObjectType.Monster && ob.race !== ObjectType.Merchant)) continue;
      if (Math.random() >= chance) continue;
      const b = f.body(ob);
      const s = b?.lib ? sprite(b.lib, b.f) : null;
      if (!s || !s.mask) continue;
      const lx = Math.floor(Math.random() * s.width);
      let ly = -1;
      for (let y = 0; y < s.height; y++) if (visiblePixel(s, lx, y)) { ly = y; break; }
      if (ly < 0) continue;
      this.bodySplashes.push({ ob, ox: s.x + lx, oy: s.y + ly + 1, born: now, seed: Math.random() });
    }
  }

  /** 0..1 how much puddle the ground is at world pixel (wx, wy): 1 on water, GROUND_FS puddle() on floor, 0 off the floor or past the mask. */
  private puddleAt(wx: number, wy: number): number {
    const m = this.maskView, d = this.maskData;
    if (!m || !d) return 0;
    const c = Math.floor((wx - m.originX) / CELL_W), r = Math.floor((wy - m.originY) / CELL_H);
    if (c < 0 || r < 0 || c >= m.cols || r >= m.rows) return 0;
    const o = (r * m.cols + c) * 4;
    if (!d[o]) return 0;
    if (d[o + 1]) return 1;
    const edge = 0.72 - 0.24 * this.fx.wetness;
    return smoothstep(edge, edge + 0.09, fbm(f32(f32(wx / 150) + 3.1), f32(f32(wy / 150) + 3.1)));
  }

  /** New footfall splashes: a player entering frame 1 (left foot) or 4 (right foot) of a step, where the foot lands in a puddle or water. */
  private spawnFootSplashes(f: AtmosphereFrame): void {
    for (const ob of f.objects) {
      if (ob.race !== ObjectType.Player) continue;
      const fi = ob.frameIndex, prev = this.lastFoot.get(ob);
      this.lastFoot.set(ob, fi);
      if (ob.dead || fi === prev || (fi !== 1 && fi !== 4) || !STEPPING.has(ob.action) || !this.fx.wet) continue;
      if (this.footSplashes.length >= MAX_FOOT_SPLASHES) continue;
      const [cx, cy] = DIRS[ob.direction & 7]!;
      const sx = cx * CELL_W, sy = cy * CELL_H, len = Math.hypot(sx, sy) || 1;
      const dx = sx / len, dy = sy / len, side = fi === 1 ? -1 : 1;
      const at = f.drawLocation(ob);
      const wx = at.x + CELL_W / 2 - dy * 5 * side + this.camX, wy = at.y + CELL_H / 2 + 2 + dx * 3 * side + this.camY;
      const k = this.puddleAt(wx, wy);
      if (k < FOOT_PUDDLE_MIN) continue;
      const run = ob.action === MirAction.Running || ob.action === MirAction.RunningBow || ob.action === MirAction.MountRunning;
      this.footSplashes.push({ wx, wy, born: f.now, k, seed: Math.random(), dx, dy, run });
      if (ob === f.user) this.splashAudio(k * (run ? 1 : 0.7));
    }
  }

  /** Footfall ripples on the puddle (before the objects, so the foot stands in them): two rings spreading from where the foot landed. */
  private drawFootRipples(batch: SpriteBatch, width: number, height: number, now: number): void {
    if (this.footSplashes.length === 0) return;
    const r = this.renderer;
    if (!this.ring) this.ring = r.createRgbaTexture(softRing(48), 48, 48);
    r.setBlend(false, 1, "NORMAL");
    const src = { x: 0, y: 0, width: 48, height: 48 };
    for (const s of this.footSplashes) {
      const x = s.wx - this.camX, y = s.wy - this.camY;
      if (x < -40 || y < -40 || x > width + 40 || y > height + 40) continue;
      const age = (now - s.born) / FOOT_SPLASH_MS;
      for (const [delay, size, strength] of [[0, 1, 0.55], [0.25, 0.65, 0.35]] as const) {
        const t = (age - delay) / (1 - delay);
        if (t <= 0 || t >= 1) continue;
        const rx = (4 + (12 + 8 * s.k) * Math.sqrt(t)) * size * (s.run ? 1.15 : 1), ry = rx / 1.7;
        batch.DrawScaled(this.ring, src, x - rx, y - ry, rx * 2, ry * 2, { r: 0.85, g: 0.9, b: 1, a: strength * s.k * (1 - t) * (1 - t) });
      }
    }
    batch.Flush();
  }

  /** Footfall spray (after the objects, so it shows in front of the legs): a brief white slap and droplets thrown up and out in a fan, mostly
   *  ahead of the step; retires splashes past their life. */
  private drawFootSpray(batch: SpriteBatch, width: number, height: number, now: number): void {
    const list = this.footSplashes;
    if (list.length === 0) return;
    const r = this.renderer;
    if (!this.dot) this.dot = r.createRgbaTexture(softDot(16), 16, 16);
    r.setBlend(false, 1, "NORMAL");
    const src = { x: 0, y: 0, width: 16, height: 16 };
    for (let i = 0; i < list.length; i++) {
      const s = list[i]!;
      const age = (now - s.born) / FOOT_SPLASH_MS;
      if (age >= 1) { list.splice(i--, 1); continue; }
      const x = s.wx - this.camX, y = s.wy - this.camY;
      if (x < -40 || y < -60 || x > width + 40 || y > height + 40) continue;
      const fade = 1 - age, power = (0.6 + 0.4 * s.k) * (s.run ? 1.3 : 1);
      if (age < 0.3) batch.DrawScaled(this.dot, src, x - 6 * power, y - 2.5, 12 * power, 5, { r: 0.9, g: 0.94, b: 1, a: 0.55 * s.k * (1 - age / 0.3) });
      const t = Math.min(1, age / 0.75); // droplets land at 75% of the life
      for (let j = 0; j < 8; j++) {
        const h1 = fract(s.seed * 97.31 + j * 0.618), h2 = fract(s.seed * 41.7 + j * 0.377);
        const spread = (j / 7 - 0.5) * 2.4 + (h1 - 0.5) * 0.5; // fan across the step, a little forward
        const reach = (7 + 9 * h2) * power;
        const ox = (Math.sin(spread) * 1 + s.dx * 0.6) * reach * t, oy = (Math.cos(spread) * 0.35 + s.dy * 0.4) * reach * t * 0.6;
        const hgt = (6 + 8 * h1) * power * 4 * t * (1 - t);
        const sz = 1.5 + h2;
        batch.DrawScaled(this.dot, src, x + ox - sz / 2, y + oy - hgt - sz / 2, sz, sz, { r: 0.88, g: 0.92, b: 1, a: 0.85 * s.k * fade });
      }
    }
    batch.Flush();
  }

  /** Pairs of soft dark prints, fading out (weatherFx.ts footprints). */
  private drawFootprints(batch: SpriteBatch, width: number, height: number, now: number): void {
    const prints = this.fx.footprints;
    if (prints.length === 0) return;
    const r = this.renderer;
    if (!this.dot) this.dot = r.createRgbaTexture(softDot(16), 16, 16);
    r.setBlend(false, 1, "NORMAL");
    const src = { x: 0, y: 0, width: 16, height: 16 };
    for (const f of prints) {
      const x = f.wx - this.camX, y = f.wy - this.camY;
      if (x < -20 || y < -20 || x > width + 20 || y > height + 20) continue;
      const a = 0.42 * WeatherFx.footprintFade(f, now);
      if (a <= 0.01) continue;
      const colour = { r: 0.42, g: 0.47, b: 0.58, a };
      const px = -f.dy * 4.5, py = f.dx * 3; // to the side of the step
      const fx = f.dx * 2.5, fy = f.dy * 1.7; // one foot a little ahead
      batch.DrawScaled(this.dot, src, x + px - fx - 4, y + py - fy - 2.5, 8, 5, colour);
      batch.DrawScaled(this.dot, src, x - px + fx - 4, y - py + fy - 2.5, 8, 5, colour);
    }
    batch.Flush();
  }

  /** The scene target lights must rebind after their pass (null when not running). */
  get sceneTarget(): RenderTargetRef | null {
    return this.active ? this.scene : null;
  }

  /** A critical hit on `ob` (GameScene.DamageIndicator, Critical): placed at the next `collect`. */
  crit(ob: MapObj): void {
    if (atmosphereOn() && this.pendingCrits.length < 32) this.pendingCrits.push(ob);
  }

  /** After the objects and weather are drawn, before the lights: new flashes from started effects, this frame's lights and haze. */
  collect(f: AtmosphereFrame): void {
    this.lights.length = 0;
    this.hazeBlobs.length = 0;
    this.screenFlash = null;
    if (!this.active) { this.pendingCrits.length = 0; this.bodySplashes.length = 0; this.footSplashes.length = 0; return; }
    const { now } = f;
    this.place = f.drawLocation;
    this.spawnBodySplashes(f);
    this.spawnFootSplashes(f);
    for (const ob of this.pendingCrits) {
      const at = f.drawLocation(ob);
      this.flashes.push({ wx: at.x + CELL_W / 2 + this.camX, wy: at.y - 8 + this.camY, rx: 140, ry: 105, colour: CRIT, intensity: 2.2, duration: 230, start: now });
    }
    this.pendingCrits.length = 0;
    const effects = gameSettings.Effect;

    if (effects) {
      for (const ob of f.objects) for (const e of ob.effects) this.effect(e, f, now);
      for (const e of f.mapEffects) this.effect(e, f, now);
      for (const so of f.spellObjects) {
        const spec = so.spell !== undefined ? HAZE_SPELL_OBJECTS.get(so.spell) : undefined;
        if (spec) this.addHaze(so.drawLocation.x + CELL_W / 2, so.drawLocation.y - 24, spec);
      }
    }

    // flashes -> lights + screen flash
    let sr = 0, sg = 0, sb = 0;
    for (let i = 0; i < this.flashes.length; i++) {
      const fl = this.flashes[i]!;
      const t = now - fl.start;
      if (t >= fl.duration) { this.flashes.splice(i--, 1); continue; }
      const k = envelope(fl, t);
      if (k <= 0) continue;
      if (fl.rx > 0) this.lights.push({ x: fl.wx - this.camX, y: fl.wy - this.camY, rx: fl.rx, ry: fl.ry, colour: fl.colour, intensity: fl.intensity * k, scaled: false });
      if (fl.screen) {
        const s = fl.screen * k;
        sr += fl.colour[0] * s; sg += fl.colour[1] * s; sb += fl.colour[2] * s;
      }
    }
    // several strikes at once never add more than 0.45 of white
    if (sr + sg + sb > 0) this.screenFlash = [Math.min(sr, 115), Math.min(sg, 115), Math.min(sb, 115)];

    // lit particles (scaled: they only show where it is dark)
    f.embers?.forEachEmber((x, y, colour, seed) => {
      const flick = 0.8 + 0.2 * Math.sin(now * 0.017 + seed * 2.39);
      this.lights.push({ x, y, rx: 42, ry: 32, colour, intensity: 0.9 * flick, scaled: true });
    });
  }

  /** True when the light pass must run in daylight too (a flash is showing). */
  get forcesLightPass(): boolean {
    return this.screenFlash !== null || this.lights.some((l) => !l.scaled);
  }

  private effect(e: Effect, f: AtmosphereFrame, now: number): void {
    if (now < e.start || e.held) return;
    const spec = EFFECTS.get(`${e.lib}:${e.baseIndex}`);
    if (!spec) return;
    const at = e.owner ? f.drawLocation(e.owner) : {
      x: (e.source.X - f.user.movement.X + f.offSetX) * CELL_W + f.user.offsetMove.X,
      y: (e.source.Y - f.user.movement.Y + f.offSetY) * CELL_H + f.user.offsetMove.Y,
    };
    const cx = at.x + CELL_W / 2, cy = at.y;
    if (spec.haze) this.addHaze(cx, cy - 26, spec.haze);
    if (this.seen.has(e)) return;
    this.seen.add(e);
    if (spec.flash) this.flashes.push({ ...spec.flash, wx: cx + this.camX, wy: cy - 8 + this.camY, start: now });
    if (spec.beam) {
      const [dx, dy] = DIRS[Math.trunc((e.baseIndex - 970) / 20) & 7]!;
      for (const step of [1, 3, 5]) {
        this.flashes.push({ ...strike(170, 2.6, 420, step === 1 ? 0.1 : 0), wx: cx + dx * step * CELL_W + this.camX, wy: cy - 8 + dy * step * CELL_H + this.camY, start: now + step * 15 });
      }
    }
  }

  private addHaze(x: number, y: number, spec: HazeSpec): void {
    this.hazeBlobs.push({ x, y, rx: spec.rx, ry: spec.ry, strength: spec.strength });
  }

  /** Before the frame's end(), after the lights: glow, haze mask and the composite onto the canvas. `lightMap` = THIS frame's Enhanced light map
   *  (wet reflections) or null; `lamps` = that pass's lamps (puddle reflections). Returns true (GL blend state changed). */
  finish(batch: SpriteBatch, lightMap: RenderTargetRef | null, lamps: readonly ReflectionSource[]): boolean {
    if (!this.active) return false;
    this.active = false;
    const r = this.renderer, post = this.post!;
    const glow = this.glow!, tmp = this.glowTmp!, haze = this.haze!;
    batch.Flush();

    // glow: replay the additive draws at half size, then blur
    r.bindRenderTarget(glow);
    r.clearColour(0, 0, 0, 255);
    if (this.glowDraws.length > 0) {
      let rate = -1;
      for (const d of this.glowDraws) {
        if (d.rate !== rate) {
          batch.Flush();
          if (d.rate > 0) r.setBlend(true, d.rate, "COLORFY");
          else r.setBlend(false, 1, "NORMAL"); // occluder: black over the glow under it
          rate = d.rate;
        }
        batch.DrawScaled(d.tex, { x: 0, y: 0, width: d.w, height: d.h }, d.x * 0.5, d.y * 0.5, d.w * 0.5, d.h * 0.5, d.colour);
      }
      batch.Flush();
      post.blurPass(glow, tmp, 1, 0, 1);
      post.blurPass(tmp, glow, 0, 1, 1);
      post.blurPass(glow, tmp, 1, 0, 2.6);
      post.blurPass(tmp, glow, 0, 1, 2.6);
    }
    this.glowDraws.length = 0;

    // haze mask: soft blobs (Light_PS falloff), additive
    r.bindRenderTarget(haze);
    r.clearColour(0, 0, 0, 255);
    if (this.hazeBlobs.length > 0) {
      if (!this.white) this.white = r.createRgbaTexture(new Uint8Array([255, 255, 255, 255]), 1, 1);
      r.setShaderMode("Light");
      r.setEnhancedLightAdditiveBlend();
      for (const h of this.hazeBlobs) {
        batch.DrawScaled(this.white, { x: 0, y: 0, width: 1, height: 1 }, (h.x - h.rx) * 0.5, (h.y - h.ry) * 0.5, h.rx, h.ry,
          { r: 1, g: 1, b: 1, a: Math.min(1, h.strength * 0.25) });
      }
      batch.Flush();
      r.setShaderMode("Default");
    }

    const fx = this.fx;
    post.compositeToCanvas(this.scene!, glow, haze, (performance.now() % 3600000) * 0.001, GLOW_STRENGTH, HAZE_PIXELS, {
      centre: this.centre, depth: this.depth, light: lightMap,
      tint: fx.rain ? mixTint(RAIN_TINT, 0.6 + 0.4 * fx.rainIntensity(performance.now())) : fx.snow ? SNOW_TINT : NO_TINT,
      reflect: fx.wet ? 0.4 + 0.6 * fx.wetness : 0, frost: fx.snow ? 1 : 0,
      wetMask: this.snapshotOk ? this.wetMask : null,
      reflectTargets: this.reflMirror && this.reflLamps ? { mirror: this.reflMirror, lamps: this.reflLamps } : null,
      lamps, camX: this.camX, camY: this.camY, ripples: this.groundWet ? this.ripplesNow : 0,
    });
    r.setBlend(false, 1, "NORMAL");
    return true;
  }

  private ensureTargets(width: number, height: number): boolean {
    const size = `${width}x${height}`;
    if (this.size === size && this.scene && this.glow && this.glowTmp && this.haze && this.wetMask && this.reflMirror && this.reflLamps) return true;
    const r = this.renderer;
    for (const t of [this.scene, this.glow, this.glowTmp, this.haze, this.wetMask, this.reflMirror, this.reflLamps]) r.releaseRenderTarget(t);
    const hw = Math.max(1, Math.ceil(width / 2)), hh = Math.max(1, Math.ceil(height / 2));
    this.scene = r.createRenderTarget(width, height);
    this.glow = r.createRenderTarget(hw, hh, "RGBA16F");
    this.glowTmp = r.createRenderTarget(hw, hh, "RGBA16F");
    this.haze = r.createRenderTarget(hw, hh);
    this.wetMask = r.createRenderTarget(hw, hh);
    this.reflMirror = r.createRenderTarget(hw, hh);
    this.reflLamps = r.createRenderTarget(hw, hh, "RGBA16F");
    this.size = size;
    if (!this.scene || !this.glow || !this.glowTmp || !this.haze || !this.wetMask || !this.reflMirror || !this.reflLamps) {
      console.warn("[atmosphere] render targets unavailable - off:", r.lastRenderTargetError);
      this.size = "";
      return false;
    }
    for (const t of [this.glow, this.glowTmp, this.haze, this.wetMask, this.reflMirror, this.reflLamps]) this.post!.setLinear(t);
    return true;
  }
}

// Scene lab (play/lab.html): a map drawn by the game's own MapView (floor, objects, doors, weather, Classic / Enhanced lights,
// the web atmosphere) with no session, HUD, players or monsters, and a panel of knobs to tune how the scene looks: time of
// day, lighting mode, weather, light strength / ambient, shadow style (with sample players / monsters to cast them), puddle reflections of the lamps
// (postFx.ts reflectionTuning, with the ground's wetness held), and a colour grade (exposure, contrast, saturation, white balance,
// split toning, vignette, ...) applied as a post pass over the finished frame. Dev tool only: the game uses the same pass (src/render/grade.ts) only for the fixed Cozy mode grade;
// copy the JSON (Export) when a look is worth porting. Settings live in localStorage "crystal.lab" and never touch the
// game's own "crystal.settings".

import { loadManifest, assetNames } from "../src/assets/manifest.ts";
import { WebGl2Renderer } from "../src/render/backend.ts";
import { Grade, type GradeParams } from "../src/render/grade.ts";
import { attachRenderer, setLocalLib, libAsync, LibPath } from "../src/game/libraries.ts";
import { analyseReference, loadPicture, poseFrames, writeLib, DIRS, type Reference } from "./weaponPose.ts";
import { CrystalLib } from "../src/assets/crystalLib.ts";
import { MapView, shadowTuning } from "../src/game/mapView.ts";
import { MapObj, CELL_W, CELL_H, pointMove } from "../src/game/objects.ts";
import { Doors } from "../src/game/doors.ts";
import { Weather, WeatherSetting } from "../src/game/weather.ts";
import { mapCells } from "../src/game/mapFiles.ts";
import { gameSettings, type GameSettings } from "../src/game/settings.ts";
import { lightTuning } from "../src/game/lightingEnhanced.ts";
import { reflectionTuning } from "../src/render/postFx.ts";
import { wetTuning } from "../src/game/weatherFx.ts";
import { LightSetting, MirAction, MirClass, MirGender, Monster, ObjectType } from "../src/protocol/enums.generated.ts";
import type { MapCells } from "../src/map/mapReader.ts";

// — Tuning model —

type Num = { kind: "num"; key: string; label: string; min: number; max: number; step: number; def: number; unit?: string };
type Col = { kind: "col"; key: string; label: string; def: string };
type Knob = Num | Col;

/** The colour grade (GRADE_FS), in the order the shader applies it. */
const GRADE: Knob[] = [
  { kind: "num", key: "exposure", label: "Exposure", min: -2, max: 2, step: 0.01, def: 0, unit: "EV" },
  { kind: "num", key: "brightness", label: "Brightness", min: -0.5, max: 0.5, step: 0.005, def: 0 },
  { kind: "num", key: "contrast", label: "Contrast", min: 0, max: 2.5, step: 0.01, def: 1 },
  { kind: "num", key: "gamma", label: "Gamma", min: 0.3, max: 3, step: 0.01, def: 1 },
  { kind: "num", key: "black", label: "Black point", min: 0, max: 0.4, step: 0.005, def: 0 },
  { kind: "num", key: "white", label: "White point", min: 0.5, max: 1.5, step: 0.005, def: 1 },
  { kind: "num", key: "saturation", label: "Saturation", min: 0, max: 2.5, step: 0.01, def: 1 },
  { kind: "num", key: "vibrance", label: "Vibrance", min: -1, max: 1.5, step: 0.01, def: 0 },
  { kind: "num", key: "hue", label: "Hue shift", min: -180, max: 180, step: 1, def: 0, unit: "°" },
  { kind: "num", key: "temperature", label: "Temperature", min: -1, max: 1, step: 0.01, def: 0 },
  { kind: "num", key: "tint", label: "Tint (G↔M)", min: -1, max: 1, step: 0.01, def: 0 },
  { kind: "col", key: "gain", label: "Colour gain", def: "#ffffff" },
  { kind: "col", key: "shadowTint", label: "Shadow tint", def: "#808080" },
  { kind: "num", key: "shadowAmount", label: "Shadow tint amt", min: 0, max: 1, step: 0.01, def: 0 },
  { kind: "col", key: "highTint", label: "Highlight tint", def: "#808080" },
  { kind: "num", key: "highAmount", label: "Highlight amt", min: 0, max: 1, step: 0.01, def: 0 },
  { kind: "num", key: "vignette", label: "Vignette", min: 0, max: 1, step: 0.01, def: 0 },
  { kind: "num", key: "vignetteRadius", label: "Vignette radius", min: 0.1, max: 1, step: 0.01, def: 0.55 },
  { kind: "num", key: "sharpen", label: "Sharpen", min: 0, max: 1.5, step: 0.01, def: 0 },
];

/** Lighting knobs (lightingEnhanced.ts lightTuning + the camera's own light). */
const LIGHT: Knob[] = [
  { kind: "num", key: "lightIntensity", label: "Light intensity", min: 0, max: 3, step: 0.01, def: 1, unit: "x" },
  { kind: "num", key: "camLight", label: "Camera light", min: 0, max: 74, step: 1, def: 0 },
  { kind: "col", key: "ambientColour", label: "Ambient colour", def: "#283040" },
];

/** Smooth shadow knobs (mapView.ts shadowTuning; the defaults are the shipped look). */
const SHADOW: Knob[] = [
  { kind: "num", key: "shadowStrength", label: "Shadow strength", min: 0, max: 1, step: 0.01, def: 0.4 },
  { kind: "col", key: "shadowColour", label: "Shadow colour", def: "#000000" },
];
/** Puddle reflection knobs (postFx.ts reflectionTuning; the defaults are the shipped look) + the held wetness. */
const RT = reflectionTuning;
const REFLECT: Knob[] = [
  { kind: "num", key: "wetness", label: "Wetness (held)", min: 0, max: 1, step: 0.01, def: 0.85 },
  { kind: "num", key: "rfGain", label: "Streak gain", min: 0, max: 4, step: 0.01, def: RT.gain },
  { kind: "num", key: "rfLength", label: "Streak length", min: 0, max: 400, step: 1, def: RT.streakLength, unit: "px" },
  { kind: "num", key: "rfWidth", label: "Streak width", min: 0, max: 40, step: 0.5, def: RT.streakWidth, unit: "px" },
  { kind: "num", key: "rfCore", label: "Core gain", min: 0, max: 5, step: 0.01, def: RT.coreGain },
  { kind: "num", key: "rfHalo", label: "Halo gain", min: 0, max: 3, step: 0.01, def: RT.haloGain },
  { kind: "num", key: "rfRough", label: "Wet ground strength", min: 0, max: 1.5, step: 0.01, def: RT.roughGain },
  { kind: "num", key: "rfWarm", label: "Colour warmth", min: 0, max: 5, step: 0.05, def: RT.warmth },
  { kind: "num", key: "rfFlame", label: "Flame mirror", min: 0, max: 3, step: 0.01, def: RT.flameGain },
  { kind: "num", key: "rfSheen", label: "Wet sheen", min: 0, max: 1, step: 0.005, def: RT.sheenGain },
  { kind: "num", key: "rfRipple", label: "Ripple push", min: 0, max: 8, step: 0.05, def: RT.rippleDisp, unit: "px" },
  { kind: "num", key: "rfWobble", label: "Wobble", min: 0, max: 6, step: 0.05, def: RT.wobble, unit: "px" },
  { kind: "num", key: "rfPudLow", label: "Puddle from", min: 0, max: 1, step: 0.01, def: RT.puddleLow },
  { kind: "num", key: "rfPudHigh", label: "Puddle full", min: 0, max: 1, step: 0.01, def: RT.puddleHigh },
  { kind: "num", key: "rfFade", label: "Contact fade", min: 0.5, max: 30, step: 0.5, def: RT.contactFade, unit: "px" },
  { kind: "num", key: "rfHot", label: "Hot to white", min: 0, max: 2, step: 0.01, def: RT.hotWhite },
  { kind: "num", key: "rfLegacy", label: "Old light streak", min: 0, max: 2, step: 0.01, def: RT.legacy },
  { kind: "num", key: "rfStatic", label: "Static lamp height", min: 0, max: 160, step: 1, def: RT.staticLampHeight, unit: "px" },
  { kind: "num", key: "rfLampEmit", label: "Lamp flame offset", min: -150, max: 50, step: 1, def: RT.lampEmitter, unit: "px" },
  { kind: "num", key: "rfLampFoot", label: "Lamp foot offset", min: -150, max: 50, step: 1, def: RT.lampContact, unit: "px" },
  { kind: "num", key: "rfLampScale", label: "Lamp sprite lift", min: 0, max: 2, step: 0.01, def: RT.lampHeightScale, unit: "x" },
  { kind: "num", key: "rfCarried", label: "Carried light height", min: 0, max: 100, step: 1, def: RT.carriedHeight, unit: "px" },
  { kind: "num", key: "rfFoot", label: "Carried foot", min: 0, max: 60, step: 1, def: RT.carriedFoot, unit: "px" },
  { kind: "num", key: "rfEffect", label: "Effect height", min: 0, max: 100, step: 1, def: RT.effectHeight, unit: "px" },
];

type ShadowStyle = "dither" | "flat" | "soft";
const SHADOW_STYLES: [ShadowStyle, string][] = [["dither", "Dither (desktop art)"], ["flat", "Flat"], ["soft", "Soft edge (shipped)"]];

interface LabState {
  map: string;
  camX: number; camY: number;
  resolution: number;
  timeOfDay: number;
  darkLight: number;
  lighting: "Classic" | "Enhanced";
  atmosphere: boolean; effects: boolean;
  shadowStyle: ShadowStyle; oneShadow: boolean; mapShadows: boolean; actors: boolean; turnActors: boolean;
  weather: number;
  reflections: boolean; holdWet: boolean;
  ambientOverride: boolean;
  gradeOn: boolean;
  split: boolean;
  paused: boolean;
  actualSize: boolean;
  tester: boolean; testAction: string; testArmour: number; testHair: number;
  poseLength: number; poseWidth: number; poseMirror: boolean; poseRef: number; poseGlow: number;
  v: Record<string, number | string>;
}

const defaults = (): LabState => ({
  map: "0", camX: -1, camY: -1, resolution: 1024, timeOfDay: LightSetting.Day, darkLight: 0, lighting: "Enhanced",
  atmosphere: true, effects: true, weather: 0, ambientOverride: false, gradeOn: true, split: false, paused: false,
  shadowStyle: "soft", oneShadow: false, mapShadows: true, actors: true, turnActors: false, reflections: true, holdWet: true,
  actualSize: false, tester: true, testAction: "cycle", testArmour: 0, testHair: 1, poseLength: 1, poseWidth: 1, poseMirror: true, poseRef: 0, poseGlow: 0, v: Object.fromEntries([...GRADE, ...LIGHT, ...SHADOW, ...REFLECT].map((k) => [k.key, k.def])),
});

const STORE = "crystal.lab";
const PRESETS = "crystal.lab.presets";
const readJson = <T>(key: string): T | null => { try { const s = localStorage.getItem(key); return s ? (JSON.parse(s) as T) : null; } catch { return null; } };
const writeJson = (key: string, v: unknown) => { try { localStorage.setItem(key, JSON.stringify(v)); } catch { /* not persisted */ } };

function merge(saved: Partial<LabState> | null): LabState {
  const s = defaults();
  if (!saved) return s;
  for (const k of Object.keys(s) as (keyof LabState)[]) {
    if (k === "v" || saved[k] === undefined || typeof saved[k] !== typeof s[k]) continue;
    (s as unknown as Record<string, unknown>)[k] = saved[k];
  }
  if (saved.v) for (const k of Object.keys(s.v)) if (typeof saved.v[k] === typeof s.v[k]) s.v[k] = saved.v[k]!;
  return s;
}

const state = merge(readJson<Partial<LabState>>(STORE));
let saveTimer = 0;
const save = () => { clearTimeout(saveTimer); saveTimer = window.setTimeout(() => writeJson(STORE, state), 200); };
const num = (k: string) => Number(state.v[k]);
const hexRgb = (h: string): [number, number, number] => {
  const n = parseInt(h.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
};

// — Grade pass (src/render/grade.ts) —

const rgb01 = (k: string): [number, number, number] => hexRgb(String(state.v[k])).map((c) => c / 255) as [number, number, number];
/** The lab's sliders as grade inputs. */
function gradeParams(): GradeParams {
  return {
    exposure: num("exposure"), brightness: num("brightness"), contrast: num("contrast"), gamma: num("gamma"), black: num("black"), white: num("white"),
    saturation: num("saturation"), vibrance: num("vibrance"), hue: num("hue"), temperature: num("temperature"), tint: num("tint"),
    gain: rgb01("gain"), shadowTint: rgb01("shadowTint"), shadowAmount: num("shadowAmount"), highTint: rgb01("highTint"), highAmount: num("highAmount"),
    vignette: num("vignette"), vignetteRadius: num("vignetteRadius"), sharpen: num("sharpen"),
  };
}

// — Page —

const canvas = document.getElementById("game") as HTMLCanvasElement;
const viewEl = document.getElementById("view") as HTMLElement;
const statusEl = document.getElementById("status") as HTMLElement;
const msgEl = document.getElementById("msg") as HTMLElement;
const splitLine = document.getElementById("splitLine") as HTMLElement;
const panel = document.getElementById("panel") as HTMLElement;

const RESOLUTIONS: Record<number, [number, number]> = { 1024: [1024, 768], 1280: [1280, 800], 1366: [1366, 768], 1920: [1920, 1080] };
let [width, height] = RESOLUTIONS[state.resolution] ?? [1024, 768];
canvas.width = width;
canvas.height = height;

const renderer = WebGl2Renderer.create(canvas);
if (!renderer) { msgEl.textContent = "WebGL2 is not available in this browser."; throw new Error("no WebGL2"); }
attachRenderer(renderer);
const view = new MapView(renderer, width, height);
const weather = new Weather();
view.weather = weather;
const grade = new Grade(renderer);
const doors = new Doors();
/** The camera: a hidden Player object at the view centre (MapView needs a user; ViewState.hideUser keeps it undrawn). Its light is the "Camera light" knob. */
const cam = new MapObj(1, ObjectType.Player, { X: 0, Y: 0 }, 0);

/** Sample actors around the camera so the shadow styles have something to judge on: players in a few looks, then monsters. */
let actors: MapObj[] = [];
const PLAYER_LOOKS = [
  { class: MirClass.Warrior, gender: MirGender.Male, hair: 1, armour: 5, weapon: 10 },
  { class: MirClass.Wizard, gender: MirGender.Female, hair: 2, armour: 8, weapon: 6 },
  { class: MirClass.Taoist, gender: MirGender.Male, hair: 3, armour: 12, weapon: 15 },
  { class: MirClass.Warrior, gender: MirGender.Female, hair: 4, armour: 20, weapon: 18 },
  { class: MirClass.Wizard, gender: MirGender.Male, hair: 0, armour: 0, weapon: -1 },
];
const SAMPLE_MONSTERS = [Monster.Guard, Monster.Deer, Monster.ForestYeti, Monster.Scorpion, Monster.BoneElite, Monster.WoomaTaurus];
function placeActors(): void {
  actors = [];
  if (!state.actors || !map) return;
  const cx = Math.round(state.camX), cy = Math.round(state.camY);
  PLAYER_LOOKS.forEach((look, i) => {
    const ob = new MapObj(100 + i, ObjectType.Player, { X: cx - 4 + i * 2, Y: cy - 1 }, (i * 3) % 8);
    ob.look = { ...look, weaponEffect: 0 };
    actors.push(ob);
  });
  SAMPLE_MONSTERS.forEach((image, i) => {
    const ob = new MapObj(200 + i, ObjectType.Monster, { X: cx - 5 + i * 2, Y: cy + 2 }, (i * 5 + 3) % 8);
    ob.image = image;
    actors.push(ob);
  });
  for (const ob of actors) ob.snap(ob.location, ob.direction);
}
let nextTurn = 0;

// Weapon tester: a .Lib picked from disk stands in for CWeapon/<TEST_SLOT> (no item, no database) on 16 Warriors around
// the camera, male on the upper row and female below, one per direction, looping the chosen action in place.
const TEST_SLOT = 99;
const TEST_ACTIONS: [string, number][] = [
  ["Standing", MirAction.Standing], ["Walking", MirAction.Walking], ["Running", MirAction.Running], ["Stance", MirAction.Stance],
  ["Attack1", MirAction.Attack1], ["Attack2", MirAction.Attack2], ["Attack3", MirAction.Attack3], ["Spell", MirAction.Spell],
  ["Struck", MirAction.Struck], ["Die", MirAction.Die],
];
const CYCLE = TEST_ACTIONS.filter(([, a]) => a !== MirAction.Standing && a !== MirAction.Stance);
let testLib = "";
let testers: { ob: MapObj; home: { X: number; Y: number }; next: number; step: number }[] = [];
function placeTesters(): void {
  testers = [];
  if (!state.tester || !testLib || !map) return;
  const cx = Math.round(state.camX), cy = Math.round(state.camY);
  [MirGender.Male, MirGender.Female].forEach((gender, row) => {
    for (let d = 0; d < 8; d++) {
      const home = { X: cx - 7 + d * 2, Y: cy - 7 + row * 3 };
      const ob = new MapObj(300 + row * 8 + d, ObjectType.Player, home, d);
      ob.look = { class: MirClass.Warrior, gender, hair: state.testHair, armour: state.testArmour, weapon: TEST_SLOT, weaponEffect: 0 };
      ob.snap(home, d);
      testers.push({ ob, home, next: 0, step: 0 });
    }
  });
}
/** Restart a tester's action once it is back to idle (or lying dead): snap home, queue the action facing its direction
 *  (Walking / Running step 1 / 2 cells forward, then snap back, like a treadmill). */
function driveTesters(t: number): void {
  for (const ts of testers) {
    const ob = ts.ob;
    if (ob.feed.length || !(ob.action === MirAction.Standing || ob.action === MirAction.Dead)) continue;
    if (t < ts.next) continue;
    const action = state.testAction === "cycle" ? CYCLE[ts.step++ % CYCLE.length]![1] : Number(state.testAction);
    if (action === MirAction.Standing && ob.action === MirAction.Standing) continue;
    ob.dead = false;
    ob.snap(ts.home, ob.direction);
    const dist = action === MirAction.Walking ? 1 : action === MirAction.Running ? 2 : 0;
    ob.queue(action, ob.direction, dist ? pointMove(ts.home, ob.direction, dist) : ts.home);
    ts.next = t + (action === MirAction.Die ? 900 : 250); // a dead body lies a moment before the next loop
  }
}
/** The last picture posed (Re-pose uses it), the posed library's bytes (Download), the measured reference weapon. */
let lastPicture: File | null = null;
let posed: { name: string; bytes: Uint8Array } | null = null;
let reference: { shape: number; ref: Promise<Reference> } | null = null;
const MIRRORED = new Set([DIRS.indexOf("DownLeft"), DIRS.indexOf("Left"), DIRS.indexOf("UpLeft")]);
const isPicture = (name: string) => /\.(png|webp|jpe?g|gif|bmp)$/i.test(name);

/** A picture: pose it onto the reference weapon's frames (weaponPose.ts, = tools/weaponpose), then test it like a .Lib. */
async function poseTestWeapon(file: File): Promise<void> {
  lastPicture = file;
  msgEl.hidden = false;
  msgEl.textContent = `Posing ${file.name}…`;
  try {
    if (!reference || reference.shape !== state.poseRef) {
      const shape = state.poseRef;
      reference = { shape, ref: libAsync(LibPath.cWeapon(shape)).then((l) => {
        if (!l) throw new Error(`reference weapon CWeapon/${shape} not found`);
        return analyseReference(l);
      }) };
      reference.ref.catch(() => { reference = null; });
    }
    const [ref, pic] = await Promise.all([reference.ref, loadPicture(file, state.poseGlow)]);
    const frames = poseFrames(ref, pic, { length: state.poseLength, width: state.poseWidth, mirror: state.poseMirror ? MIRRORED : new Set() });
    const name = file.name.replace(/\.[^.]+$/, "") + ".Lib";
    posed = { name, bytes: await writeLib(frames) };
    await loadTestWeapon(new File([posed.bytes as BlobPart], name), `posed from ${file.name} on CWeapon/${state.poseRef}, length ${state.poseLength}, width ${state.poseWidth}${state.poseMirror ? ", mirrored left" : ""}${state.poseGlow ? `, glow ${state.poseGlow}` : ""}`);
    msgEl.hidden = true;
  } catch (e) {
    msgEl.textContent = `${file.name}: ${(e as Error).message}`;
    setTimeout(() => { msgEl.hidden = true; }, 4000);
  }
}

async function loadTestWeapon(file: File, note = ""): Promise<void> {
  if (isPicture(file.name)) return poseTestWeapon(file);
  try {
    const l = CrystalLib.fromBytes(file.name, new Uint8Array(await file.arrayBuffer()));
    setLocalLib(LibPath.cWeapon(TEST_SLOT), l);
    testLib = `${file.name}: ${l.count} images` + (l.count < 832 ? " (expected 832: male 0-415, female 416-831)" : "") + (note ? ` (${note})` : "");
    if (!note) posed = null;
  } catch (e) {
    // keep the weapon already loaded; the message names the file (LibFormatError does)
    msgEl.hidden = false;
    msgEl.textContent = (e as Error).message;
    setTimeout(() => { msgEl.hidden = true; }, 4000);
    return;
  }
  placeTesters();
  refreshUi();
}

let map: MapCells | null = null;
let loadedName = "";
let splitX = Math.round(width / 2);
let animationCount = 0, nextAnimation = 0;
let frames = 0, fps = 0, fpsAt = 0;
let mouseCell: { X: number; Y: number } | null = null;
let holdOriginal = false;

/** The game's own settings, changed in memory only (setSetting would persist them into the game's localStorage). */
function applyGameSettings(): void {
  const g = gameSettings as GameSettings;
  g.Lighting = state.lighting;
  g.Atmosphere = state.atmosphere;
  g.SmoothShadows = state.shadowStyle !== "dither";
  shadowTuning.soft = state.shadowStyle === "soft";
  shadowTuning.oneShadow = state.oneShadow;
  shadowTuning.mapObjects = state.mapShadows;
  shadowTuning.strength = num("shadowStrength");
  shadowTuning.colour = hexRgb(String(state.v.shadowColour)).map((c) => c / 255) as [number, number, number];
  g.Effect = state.effects;
  lightTuning.intensity = num("lightIntensity");
  lightTuning.ambient = state.ambientOverride ? hexRgb(String(state.v.ambientColour)) : null;
  cam.light = num("camLight");
  RT.enabled = state.reflections;
  RT.gain = num("rfGain"); RT.streakLength = num("rfLength"); RT.streakWidth = num("rfWidth"); RT.coreGain = num("rfCore");
  RT.flameGain = num("rfFlame"); RT.sheenGain = num("rfSheen"); RT.rippleDisp = num("rfRipple"); RT.wobble = num("rfWobble");
  RT.puddleLow = num("rfPudLow"); RT.puddleHigh = Math.max(num("rfPudHigh"), num("rfPudLow") + 0.01); RT.contactFade = num("rfFade");
  RT.hotWhite = num("rfHot"); RT.legacy = num("rfLegacy"); RT.staticLampHeight = num("rfStatic"); RT.lampHeightScale = num("rfLampScale");
  RT.haloGain = num("rfHalo"); RT.roughGain = num("rfRough"); RT.warmth = num("rfWarm");
  RT.lampEmitter = num("rfLampEmit"); RT.lampContact = num("rfLampFoot");
  RT.carriedHeight = num("rfCarried"); RT.carriedFoot = num("rfFoot"); RT.effectHeight = num("rfEffect");
  wetTuning.wetness = state.holdWet ? num("wetness") : -1;
}

function placeCamera(): void {
  if (!map) return;
  state.camX = Math.min(Math.max(state.camX, 0), map.width - 1);
  state.camY = Math.min(Math.max(state.camY, 0), map.height - 1);
  const mx = Math.round(state.camX), my = Math.round(state.camY);
  cam.location = { X: mx, Y: my };
  cam.movement = { X: mx, Y: my };
  cam.offsetMove = { X: -Math.round((state.camX - mx) * CELL_W), Y: -Math.round((state.camY - my) * CELL_H) };
}

async function loadMap(name: string): Promise<void> {
  msgEl.hidden = false;
  msgEl.textContent = `Loading map ${name}…`;
  try {
    const m = await mapCells(name);
    map = m;
    doors.clear();
    if (name !== loadedName && loadedName !== "" || state.camX < 0) { state.camX = m.width / 2; state.camY = m.height / 2; }
    loadedName = state.map = name;
    placeCamera();
    placeActors();
    placeTesters();
    msgEl.hidden = true;
    save();
    refreshUi();
  } catch (e) {
    msgEl.textContent = `Map ${name}: ${(e as Error).message}`;
  }
}

function setResolution(res: number): void {
  [width, height] = RESOLUTIONS[res] ?? [1024, 768];
  state.resolution = res;
  canvas.width = width;
  canvas.height = height;
  view.resize(width, height);
  weather.width = width;
  weather.height = height;
  weather.rebuild();
  splitX = Math.round(width / 2);
  fit();
}

/** Scale the canvas into the view (or 1:1 with scroll bars in actual size). */
let scale = 1;
function fit(): void {
  viewEl.classList.toggle("actual", state.actualSize);
  scale = state.actualSize ? 1 : Math.min(viewEl.clientWidth / width, viewEl.clientHeight / height);
  canvas.style.transform = `scale(${scale})`;
  canvas.style.left = state.actualSize ? "0" : Math.max(0, (viewEl.clientWidth - width * scale) / 2) + "px";
  canvas.style.top = state.actualSize ? "0" : Math.max(0, (viewEl.clientHeight - height * scale) / 2) + "px";
}
window.addEventListener("resize", fit);

// — Input: drag / keys pan, the split handle, hold Space for the ungraded frame —

const canvasPos = (e: MouseEvent) => {
  const r = canvas.getBoundingClientRect();
  return { x: ((e.clientX - r.left) / r.width) * width, y: ((e.clientY - r.top) / r.height) * height };
};
let drag: { x: number; y: number; split: boolean } | null = null;
canvas.addEventListener("mousedown", (e) => {
  const p = canvasPos(e);
  drag = { x: p.x, y: p.y, split: state.split && Math.abs(p.x - splitX) < 8 * (1 / scale) };
  viewEl.classList.add("dragging");
  e.preventDefault();
});
window.addEventListener("mouseup", () => { drag = null; viewEl.classList.remove("dragging"); save(); });
// Drop a .Lib on the view: the weapon tester loads it.
viewEl.addEventListener("dragover", (e) => { if (e.dataTransfer?.types.includes("Files")) e.preventDefault(); });
viewEl.addEventListener("drop", (e) => {
  const f = e.dataTransfer?.files[0];
  if (!f) return;
  e.preventDefault();
  if (/\.lib$/i.test(f.name) || isPicture(f.name)) void loadTestWeapon(f);
});
window.addEventListener("mousemove", (e) => {
  const p = canvasPos(e);
  if (map) mouseCell = view.mapLocation(p.x - cam.offsetMove.X, p.y - cam.offsetMove.Y, cam);
  if (!drag) return;
  if (drag.split) { splitX = Math.min(Math.max(Math.round(p.x), 0), width); return; }
  state.camX -= (p.x - drag.x) / CELL_W;
  state.camY -= (p.y - drag.y) / CELL_H;
  drag.x = p.x; drag.y = p.y;
  placeCamera();
});
const keys = new Set<string>();
window.addEventListener("keydown", (e) => {
  if ((e.target as HTMLElement).closest("input, textarea, select")) return;
  if (e.code === "Space") { holdOriginal = true; e.preventDefault(); return; }
  if (e.code === "KeyG") { state.gradeOn = !state.gradeOn; refreshUi(); save(); return; }
  if (e.code === "KeyH") {
    const i = SHADOW_STYLES.findIndex(([v]) => v === state.shadowStyle);
    state.shadowStyle = SHADOW_STYLES[(i + 1) % SHADOW_STYLES.length]![0];
    applyGameSettings(); refreshUi(); save(); return;
  }
  keys.add(e.code);
});
window.addEventListener("keyup", (e) => { if (e.code === "Space") holdOriginal = false; keys.delete(e.code); if (!keys.size) save(); });
window.addEventListener("blur", () => { keys.clear(); holdOriginal = false; });

function panKeys(dt: number): void {
  if (!keys.size || !map) return;
  const fast = keys.has("ShiftLeft") || keys.has("ShiftRight") ? 3 : 1;
  const sp = 8 * fast * dt / 1000; // cells a second
  let dx = 0, dy = 0;
  if (keys.has("KeyA") || keys.has("ArrowLeft")) dx -= 1;
  if (keys.has("KeyD") || keys.has("ArrowRight")) dx += 1;
  if (keys.has("KeyW") || keys.has("ArrowUp")) dy -= 1;
  if (keys.has("KeyS") || keys.has("ArrowDown")) dy += 1;
  if (!dx && !dy) return;
  state.camX += dx * sp;
  state.camY += dy * sp * (CELL_W / CELL_H) * 0.67; // about the same screen speed both ways
  placeCamera();
}

// — Panel —

const el = <K extends keyof HTMLElementTagNameMap>(tag: K, props: Record<string, unknown> = {}, ...kids: (Node | string)[]): HTMLElementTagNameMap[K] => {
  const n = document.createElement(tag);
  Object.assign(n, props);
  n.append(...kids);
  return n;
};
const refreshers: (() => void)[] = [];
const refreshUi = () => { for (const f of refreshers) f(); };
let camRefresh: () => void = () => {};

function section(title: string, open = true): HTMLElement {
  const d = el("details", { open });
  d.append(el("summary", {}, title));
  panel.append(d);
  return d;
}

function knobRow(k: Knob, parent: HTMLElement, onChange: () => void): void {
  if (k.kind === "col") {
    const input = el("input", { type: "color" });
    input.addEventListener("input", () => { state.v[k.key] = input.value; onChange(); save(); });
    refreshers.push(() => { input.value = String(state.v[k.key]); });
    const reset = el("span", { className: "val", title: "Reset" }, "↺");
    reset.addEventListener("click", () => { state.v[k.key] = k.def; refreshUi(); onChange(); save(); });
    parent.append(el("div", { className: "row" }, el("label", { title: k.label }, k.label), input, reset));
    return;
  }
  const range = el("input", { type: "range", min: String(k.min), max: String(k.max), step: String(k.step) });
  const val = el("span", { className: "val", title: "Double-click to reset" });
  const show = () => {
    const v = num(k.key);
    val.textContent = (k.step >= 1 ? String(v) : v.toFixed(k.step < 0.01 ? 3 : 2)) + (k.unit ?? "");
  };
  range.addEventListener("input", () => { state.v[k.key] = Number(range.value); show(); onChange(); save(); });
  val.addEventListener("dblclick", () => { state.v[k.key] = k.def; refreshUi(); onChange(); save(); });
  range.addEventListener("dblclick", () => { state.v[k.key] = k.def; refreshUi(); onChange(); save(); });
  refreshers.push(() => { range.value = String(state.v[k.key]); show(); });
  parent.append(el("div", { className: "row" }, el("label", { title: k.label }, k.label), range, val));
}

function check(parent: HTMLElement, label: string, get: () => boolean, set: (v: boolean) => void): void {
  const box = el("input", { type: "checkbox" });
  box.addEventListener("change", () => { set(box.checked); applyGameSettings(); save(); });
  refreshers.push(() => { box.checked = get(); });
  parent.append(el("label", {}, box, " " + label));
}

function select(parent: HTMLElement, label: string, options: [string, string][], get: () => string, set: (v: string) => void): void {
  const s = el("select");
  for (const [v, t] of options) s.append(el("option", { value: v }, t));
  s.addEventListener("change", () => { set(s.value); applyGameSettings(); save(); });
  refreshers.push(() => { s.value = get(); });
  parent.append(el("div", { className: "row wide" }, el("label", {}, label), s));
}

function buildPanel(): void {
  panel.append(el("h1", {}, "Scene lab"));
  panel.append(el("div", { className: "hint" }, "Drag or WASD / arrows (Shift = faster) to pan. Hold Space = ungraded. G = grade on/off. H = next shadow style. Double-click a slider to reset it."));

  // Scene
  const scene = section("Scene");
  const mapInput = el("input", { type: "text" });
  mapInput.setAttribute("list", "maps");
  const maps = el("datalist", { id: "maps" });
  for (const n of assetNames("map").filter((n) => n.endsWith(".map")).map((n) => n.slice(0, -4)).sort((a, b) => a.localeCompare(b, undefined, { numeric: true })))
    maps.append(el("option", { value: n }));
  mapInput.addEventListener("change", () => void loadMap(mapInput.value.trim()));
  refreshers.push(() => { mapInput.value = state.map; });
  scene.append(maps, el("div", { className: "row wide" }, el("label", {}, "Map"), mapInput));
  const cx = el("input", { type: "number" }), cy = el("input", { type: "number" });
  const go = () => { state.camX = Number(cx.value); state.camY = Number(cy.value); placeCamera(); save(); };
  cx.addEventListener("change", go);
  cy.addEventListener("change", go);
  camRefresh = () => { cx.value = String(Math.round(state.camX)); cy.value = String(Math.round(state.camY)); };
  refreshers.push(() => camRefresh());
  scene.append(el("div", { className: "row" }, el("label", {}, "Camera X / Y"), cx, cy));
  select(scene, "Resolution", Object.entries(RESOLUTIONS).map(([k, [w, h]]) => [k, `${w} x ${h}`]), () => String(state.resolution), (v) => setResolution(Number(v)));
  const sc = el("div", { className: "checks" });
  check(sc, "Actual size (1:1)", () => state.actualSize, (v) => { state.actualSize = v; fit(); });
  check(sc, "Pause animation", () => state.paused, (v) => { state.paused = v; });
  scene.append(sc);

  // Lighting
  const light = section("Lighting");
  select(light, "Time of day", [[String(LightSetting.Day), "Day"], [String(LightSetting.Dawn), "Dawn"], [String(LightSetting.Evening), "Evening"], [String(LightSetting.Night), "Night (dungeon)"]],
    () => String(state.timeOfDay), (v) => { state.timeOfDay = Number(v); });
  select(light, "Night colour", [["0", "0 Black"], ["1", "1 Dark grey"], ["2", "2 Slate grey"], ["3", "3 Sky blue"], ["4", "4 Goldenrod"]],
    () => String(state.darkLight), (v) => { state.darkLight = Number(v); });
  select(light, "Mode", [["Enhanced", "Enhanced"], ["Classic", "Classic (desktop)"]], () => state.lighting, (v) => { state.lighting = v as LabState["lighting"]; });
  const lc = el("div", { className: "checks" });
  check(lc, "Atmosphere", () => state.atmosphere, (v) => { state.atmosphere = v; });
  check(lc, "Effect lights", () => state.effects, (v) => { state.effects = v; });
  check(lc, "Override ambient", () => state.ambientOverride, (v) => { state.ambientOverride = v; });
  light.append(lc);
  for (const k of LIGHT) knobRow(k, light, applyGameSettings);
  light.append(el("div", { className: "hint" }, "Light intensity and the ambient override are Enhanced only. Camera light: range = value % 15, colour band = value / 15 (0 none, 1 dim, 2 candle, 3 torch, 4 peddler torch)."));

  // Shadows
  const sh = section("Shadows");
  select(sh, "Style (H)", SHADOW_STYLES, () => state.shadowStyle, (v) => { state.shadowStyle = v as ShadowStyle; });
  const shc = el("div", { className: "checks" });
  check(shc, "One shadow per player", () => state.oneShadow, (v) => { state.oneShadow = v; });
  check(shc, "Map object shadows", () => state.mapShadows, (v) => { state.mapShadows = v; });
  check(shc, "Sample actors", () => state.actors, (v) => { state.actors = v; placeActors(); });
  check(shc, "Turn actors", () => state.turnActors, (v) => { state.turnActors = v; });
  sh.append(shc);
  for (const k of SHADOW) knobRow(k, sh, applyGameSettings);
  const shb = el("div", { className: "buttons" });
  const here = el("button", {}, "Actors to camera");
  here.addEventListener("click", () => { placeActors(); placeTesters(); });
  const resetShadow = el("button", {}, "Shipped look");
  resetShadow.addEventListener("click", () => {
    state.shadowStyle = "soft"; state.oneShadow = false; state.mapShadows = true;
    for (const k of SHADOW) state.v[k.key] = k.def;
    applyGameSettings(); refreshUi(); save();
  });
  shb.append(here, resetShadow);
  sh.append(shb, el("div", { className: "hint" },
    "Strength and colour apply to Flat and Soft. Map object shadows: trees, walls and buildings get the same fill as players and monsters. One shadow per player: the weapon and hair layers drop their own shadow, so it stops doubling up where it overlaps the body's. " +
    "Soft edge feathers the outline by about a pixel. Turn actors steps every actor through all eight directions."));

  // Weapon tester
  const wt = section("Weapon tester");
  const file = el("input", { type: "file", accept: ".lib,.Lib,image/*" });
  file.addEventListener("change", () => { const f = file.files?.[0]; if (f) void loadTestWeapon(f); file.value = ""; });
  wt.append(el("div", { className: "row wide" }, el("label", {}, ".Lib or picture"), file));
  const info = el("div", { className: "hint" });
  refreshers.push(() => { info.textContent = testLib || "No weapon loaded. Pick a .Lib or drop one on the view."; });
  wt.append(info);
  select(wt, "Action", [["cycle", "Cycle all"], ...TEST_ACTIONS.map(([n, a]): [string, string] => [String(a), n])],
    () => state.testAction, (v) => { state.testAction = v; for (const ts of testers) { ts.ob.dead = false; ts.ob.snap(ts.home, ts.ob.direction); ts.next = 0; } });
  const look = (label: string, get: () => number, set: (v: number) => void) => {
    const n = el("input", { type: "number", min: "0", step: "1" });
    n.addEventListener("change", () => { set(Math.max(0, Math.floor(Number(n.value) || 0))); save(); placeTesters(); });
    refreshers.push(() => { n.value = String(get()); });
    wt.append(el("div", { className: "row" }, el("label", {}, label), n));
  };
  look("Armour", () => state.testArmour, (v) => { state.testArmour = v; });
  look("Hair", () => state.testHair, (v) => { state.testHair = v; });
  const fnum = (label: string, step: number, get: () => number, set: (v: number) => void) => {
    const n = el("input", { type: "number", min: String(step), step: String(step) });
    n.addEventListener("change", () => { const v = Number(n.value); if (v > 0) set(v); save(); });
    refreshers.push(() => { n.value = String(get()); });
    wt.append(el("div", { className: "row" }, el("label", {}, label), n));
  };
  fnum("Picture length", 0.05, () => state.poseLength, (v) => { state.poseLength = v; });
  fnum("Picture width", 0.05, () => state.poseWidth, (v) => { state.poseWidth = v; });
  look("Reference weapon", () => state.poseRef, (v) => { state.poseRef = v; });
  look("Glow halo cut", () => state.poseGlow, (v) => { state.poseGlow = Math.min(255, v); });
  const wtb = el("div", { className: "buttons" });
  const repose = el("button", {}, "Re-pose picture");
  repose.addEventListener("click", () => { if (lastPicture) void poseTestWeapon(lastPicture); });
  const download = el("button", {}, "Download .Lib");
  download.addEventListener("click", () => {
    if (!posed) return;
    const a = el("a", { href: URL.createObjectURL(new Blob([posed.bytes as BlobPart])), download: posed.name });
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  });
  refreshers.push(() => { repose.disabled = !lastPicture; download.disabled = !posed; });
  wtb.append(repose, download);
  wt.append(wtb);
  const wtc = el("div", { className: "checks" });
  check(wtc, "Show testers", () => state.tester, (v) => { state.tester = v; placeTesters(); });
  check(wtc, "Mirror left-facing", () => state.poseMirror, (v) => { state.poseMirror = v; });
  wt.append(wtc, el("div", { className: "hint" },
    `The file stands in for CWeapon/${TEST_SLOT} on 16 Warriors: male on the top row, female below, one per direction (Up first, clockwise). ` +
    "Pick the file again after re-rendering it. Harvest always shows weapon 01 (desktop rule), so it is not listed. " +
    "A picture (weapon pointing straight up, tip at the top, on a transparent or flat background) is posed onto the reference weapon's frames " +
    "like tools/weaponpose: Length / width are relative to the reference (Dragon Slayer, CWeapon/29, is 2.0), Mirror flips the weapon in DownLeft / Left / UpLeft, " +
    "Glow halo cut (0 = off, try 90) drops the dark coloured halo of a glow on a black background (it also eats a dark brown handle). " +
    "Change them and press Re-pose; Download .Lib saves the result for Data/CWeapon."));

  // Weather
  const w = section("Weather", false);
  const wc = el("div", { className: "checks" });
  for (const [name, bit] of Object.entries(WeatherSetting)) {
    if (!bit) continue;
    check(wc, name, () => (state.weather & bit) !== 0, (v) => { state.weather = v ? state.weather | bit : state.weather & ~bit; weather.set(state.weather); });
  }
  w.append(wc);

  // Puddle reflections
  const rf = section("Puddle reflections");
  const rfc = el("div", { className: "checks" });
  check(rfc, "Lamp reflections", () => state.reflections, (v) => { state.reflections = v; });
  check(rfc, "Hold wetness", () => state.holdWet, (v) => { state.holdWet = v; });
  rf.append(rfc);
  for (const k of REFLECT) knobRow(k, rf, applyGameSettings);
  const rfb = el("div", { className: "buttons" });
  const rainNight = el("button", {}, "Rainy night");
  rainNight.addEventListener("click", () => {
    state.weather |= WeatherSetting.Rain; weather.set(state.weather);
    state.timeOfDay = LightSetting.Night; state.lighting = "Enhanced"; state.atmosphere = true; state.holdWet = true;
    applyGameSettings(); refreshUi(); save();
  });
  const resetRf = el("button", {}, "Shipped look");
  resetRf.addEventListener("click", () => { for (const k of REFLECT) state.v[k.key] = k.def; state.reflections = true; applyGameSettings(); refreshUi(); save(); });
  rfb.append(rainNight, resetRf);
  rf.append(rfb, el("div", { className: "hint" },
    "Needs Enhanced lighting, Atmosphere, a wet ground (Rain, or Hold wetness) and lamps lit (night / evening). Every lamp is mirrored across the line where its object meets the ground: " +
    "a hot core in puddles, a streak stretched towards the viewer (long and soft on wet ground, short and sharp in puddles), a wet sheen, and the lamp's own flame art. " +
    "Ripple push bends it with the visible rain rings. Placement: Lamp flame / foot offset move an animated map lamp's flame and foot (from its light's centre; the foot also follows the sprite lift); Static lamp height = map lamps with no animation; Carried / Effect = players' torches and spell lights. " +
    "Puddle from / full: the wetness of the ground that counts as rough wet ground and as a mirror puddle. Old light streak = the first look, for comparison."));

  // Grade
  const g = section("Colour grade");
  const gc = el("div", { className: "checks" });
  check(gc, "Grade on (G)", () => state.gradeOn, (v) => { state.gradeOn = v; });
  check(gc, "Split compare", () => state.split, (v) => { state.split = v; });
  g.append(gc);
  for (const k of GRADE) knobRow(k, g, () => {});
  const gb = el("div", { className: "buttons" });
  const resetGrade = el("button", {}, "Reset grade");
  resetGrade.addEventListener("click", () => { for (const k of GRADE) state.v[k.key] = k.def; refreshUi(); save(); });
  gb.append(resetGrade);
  g.append(gb, el("div", { className: "hint" }, "Split compare: left of the line is ungraded; drag the line to move it."));

  // Presets
  const p = section("Presets / export", false);
  const presetSel = el("select");
  const listPresets = () => {
    presetSel.replaceChildren(el("option", { value: "" }, "— saved presets —"));
    for (const name of Object.keys(readJson<Record<string, LabState>>(PRESETS) ?? {})) presetSel.append(el("option", { value: name }, name));
  };
  listPresets();
  const nameIn = el("input", { type: "text", placeholder: "preset name" });
  const btn = (label: string, fn: () => void) => { const b = el("button", {}, label); b.addEventListener("click", fn); return b; };
  const text = el("textarea", { spellcheck: false });
  const loadState = (s: Partial<LabState>) => {
    const keepMap = state.map;
    Object.assign(state, merge(s));
    applyGameSettings();
    weather.set(state.weather);
    setResolution(state.resolution);
    refreshUi();
    save();
    if (state.map !== keepMap || !map) void loadMap(state.map); else placeCamera();
  };
  p.append(
    el("div", { className: "row wide" }, el("label", {}, "Preset"), presetSel),
    el("div", { className: "buttons" },
      btn("Load", () => { const all = readJson<Record<string, LabState>>(PRESETS) ?? {}; const s = all[presetSel.value]; if (s) loadState(s); }),
      btn("Delete", () => { const all = readJson<Record<string, LabState>>(PRESETS) ?? {}; delete all[presetSel.value]; writeJson(PRESETS, all); listPresets(); })),
    el("div", { className: "row wide" }, el("label", {}, "Save as"), nameIn),
    el("div", { className: "buttons" },
      btn("Save preset", () => { const n = nameIn.value.trim(); if (!n) return; const all = readJson<Record<string, LabState>>(PRESETS) ?? {}; all[n] = structuredClone(state); writeJson(PRESETS, all); listPresets(); presetSel.value = n; }),
      btn("Export JSON", () => { text.value = JSON.stringify(state, null, 1); text.select(); void navigator.clipboard?.writeText(text.value).catch(() => {}); }),
      btn("Import JSON", () => { try { loadState(JSON.parse(text.value) as Partial<LabState>); } catch (e) { text.value = `Bad JSON: ${(e as Error).message}\n` + text.value; } }),
      btn("Reset all", () => loadState({ map: state.map, camX: state.camX, camY: state.camY }))),
    text,
    el("div", { className: "hint" }, "Export copies the whole lab state (scene + grade) to the clipboard; paste it back with Import or hand it over to port the look into the game."));
}

// — Frame loop —

let last = performance.now();
function frame(t: number): void {
  const dt = Math.min(t - last, 100);
  last = t;
  panKeys(dt);
  if (!state.paused && t >= nextAnimation) { animationCount++; nextAnimation = t + 100; } // MapControl.AnimationCount
  if (map) {
    weather.update(t, width, height);
    if (state.turnActors && t >= nextTurn) {
      nextTurn = t + 1500;
      for (const ob of actors) ob.snap(ob.location, (ob.direction + 1) % 8);
    }
    if (!state.paused) for (const ob of actors) ob.process(t);
    if (!state.paused) { driveTesters(t); for (const ts of testers) ts.ob.process(t); }
    view.render({
      map, user: cam, objects: [cam, ...actors, ...testers.map((ts) => ts.ob)], animationCount, mapEffects: [], doors, now: t, hideUser: true,
      lights: { mapLights: state.timeOfDay, timeOfDay: state.timeOfDay, mapDarkLight: state.darkLight },
    });
    const split = state.split ? splitX : 0;
    if (state.gradeOn && !holdOriginal) grade.apply(width, height, gradeParams(), split);
    splitLine.hidden = !(state.split && state.gradeOn && !holdOriginal);
    if (!splitLine.hidden) {
      const r = canvas.getBoundingClientRect(), vr = viewEl.getBoundingClientRect();
      splitLine.style.left = r.left - vr.left + viewEl.scrollLeft + (splitX / width) * r.width + "px";
    }
  }
  frames++;
  if (t - fpsAt >= 1000) {
    fps = frames; frames = 0; fpsAt = t;
    refreshCamFields();
  }
  const i = map && mouseCell && mouseCell.X >= 0 && mouseCell.Y >= 0 && mouseCell.X < map.width && mouseCell.Y < map.height ? mouseCell.X * map.height + mouseCell.Y : -1;
  statusEl.textContent = `${loadedName || "-"}  cam ${state.camX.toFixed(1)},${state.camY.toFixed(1)}  ${width}x${height}  ${fps} fps` +
    (i >= 0 && mouseCell ? `   cell ${mouseCell.X},${mouseCell.Y} light ${map!.light[i]}` : "") + (holdOriginal || !state.gradeOn ? "   [ungraded]" : "");
  requestAnimationFrame(frame);
}
/** Keep the camera X / Y boxes current after panning without fighting the user's typing. */
function refreshCamFields(): void {
  const active = document.activeElement;
  if (active instanceof HTMLInputElement && active.type === "number") return;
  camRefresh();
}

// — Start —

await loadManifest();
buildPanel();
applyGameSettings();
weather.width = width;
weather.height = height;
weather.set(state.weather);
setResolution(state.resolution);
refreshUi();
requestAnimationFrame(frame);
void loadMap(state.map);
(window as unknown as { crystalLab: unknown }).crystalLab = { state, view, renderer, cam, get actors() { return actors; } };

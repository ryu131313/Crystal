// The desktop's per-player client options (Client/Settings.cs: the [Game] / [Chat] / [Filters] / [Graphics] Lighting values
// that OptionDialog and ChatOptionDialog edit), kept in localStorage instead of Mir2Config.ini. Sound volumes stay in
// audio.ts (it already persists them). Read `gameSettings.X`; change with `setSetting("X", v)` so listeners hear it.

/** Settings.Lighting (LightingMode): Classic = MapControl.DrawLights, Enhanced = MapControl.Lighting.cs. */
export type LightingMode = "Classic" | "Enhanced";

/** Web-only: which game UI game.ts builds (src/ui/gameUi.ts). "Default" follows DEFAULT_UI_STYLE; resolve with uiStyle(). */
export type UiStyle = "Default" | "Modern" | "Classic";

/** What "Default" means: Modern since phase 5 of the UI port (2026-10-16). A saved explicit choice always wins; "Default"
 *  exists because setSetting saves the whole object, so a 2-value field would pin every early user to Classic. */
export const DEFAULT_UI_STYLE: "Modern" | "Classic" = "Modern";

/** Web-only: the touch layout of the Modern UI (src/ui/modern/mobile). Auto = on for a coarse pointer (phones, tablets). */
export type MobileUiMode = "Auto" | "On" | "Off";
export type MobileTextSize = "Small" | "Normal" | "Large";

export interface GameSettings {
  // Settings [Game]
  SkillMode: boolean; SkillBar: boolean; Effect: boolean; LevelEffect: boolean; DropView: boolean; NameView: boolean; HPView: boolean;
  TransparentChat: boolean; ModeView: boolean; DuraView: boolean; DisplayDamage: boolean; TargetDead: boolean; HighlightTarget: boolean;
  ExpandedBuffWindow: boolean; ExpandedHeroBuffWindow: boolean; DisplayBodyName: boolean; NewMove: boolean;
  /** Web-only: glide moving objects (and the camera) between animation frames (objects.ts updateMovement). */
  SmoothMove: boolean;
  /** Web-only: glow, spell flashes, lit embers and heat haze on top of Enhanced lighting (atmosphere.ts). */
  Atmosphere: boolean;
  /** Web-only: the art's checkerboard shadows (bodies and map objects) draw as a soft-edged solid shadow (shaders.ts SHADOW_FILL_PS, mapView.ts shadowTuning). */
  SmoothShadows: boolean;
  /** Web-only: a colour grade over the map, slightly more contrast and vibrance (render/grade.ts COZY_GRADE). */
  CozyMode: boolean;
  /** Web-only: drink the best HP / MP potion below these percents (autoPot.ts); a percent of 0 turns that pool off. */
  AutoPot: boolean; AutoPotHP: number; AutoPotMP: number;
  // [Chat] (ChatOptionDialog: which chat types show) and [Filters] (FilterXChat: hide that type)
  ShowNormalChat: boolean; ShowYellChat: boolean; ShowWhisperChat: boolean; ShowLoverChat: boolean; ShowMentorChat: boolean;
  ShowGroupChat: boolean; ShowGuildChat: boolean;
  FilterNormalChat: boolean; FilterWhisperChat: boolean; FilterShoutChat: boolean; FilterSystemChat: boolean; FilterLoverChat: boolean;
  FilterMentorChat: boolean; FilterGroupChat: boolean; FilterGuildChat: boolean;
  // [Graphics] Lighting (web: both modes run on WebGL2)
  Lighting: LightingMode;
  /** Web-only: the game UI (scene-lab "Modern" or the desktop-look "Classic"); read through uiStyle(). */
  UiStyle: UiStyle;
  // Web-only, the Modern UI (src/ui/modern): HUD scale k = UiScale x (UiAutoScale ? screen height / 768 : 1)
  /** HUD scale multiplier (0.5-2). */
  UiScale: number;
  /** Scale the HUD with the screen height (768 = 1). */
  UiAutoScale: boolean;
  /** Theme preset name (src/ui/modern/core/theme.ts THEME_PRESETS; an unknown name = the default preset). */
  UiTheme: string;
  /** Custom colours over the preset: "" = the preset's (else #rrggbb). */
  UiAccent: string; UiFrame: string; UiText: string;
  /** Custom knobs over the preset: a negative value = the preset's (hue 0-360, sat 0-2, lit 0.3-2.5, glow 0-2, radius 0-12 px). */
  UiHue: number; UiSat: number; UiLit: number; UiGlow: number; UiRadius: number;
  /** HUD opacity (0.2-1). */
  UiOpacity: number;
  /** The six HUD stat plates, a comma list of stat keys (src/ui/modern/hud/statPlates.ts PLATE_STATS). */
  UiPlates: string;
  /** Touch controls and the phone layout (Modern UI only); read through isMobileUi(). */
  MobileUi: MobileUiMode;
  /** Touch layout mirrored: the joystick on the right, the attack pad on the left. */
  MobileLeftHanded: boolean;
  /** Touch layout: vibrate on hits, low HP, level ups and failed casts. */
  MobileHaptics: boolean;
  /** Touch layout: walk to and pick up items within 2 cells while idle. */
  MobileAutoLoot: boolean;
  /** Touch layout: 30 fps and no atmosphere effects (lowPower()). */
  MobileBatterySaver: boolean;
  /** Touch layout: the camera zoom (1-1.6, pinch on the map). */
  MobileZoom: number;
  /** Touch layout: the pad's edited layout (mobileUi.ts PadLayout as JSON; "" = the default). */
  MobileLayout: string;
  /** Touch layout: the pad's text size (mobileUi.ts, the --mt factor). */
  MobileTextSize: MobileTextSize;
}

/** Client/Settings.cs defaults. */
export const DEFAULT_SETTINGS: GameSettings = {
  SkillMode: false, SkillBar: true, Effect: true, LevelEffect: true, DropView: true, NameView: true, HPView: true,
  TransparentChat: false, ModeView: false, DuraView: false, DisplayDamage: true, TargetDead: false, HighlightTarget: true,
  ExpandedBuffWindow: true, ExpandedHeroBuffWindow: true, DisplayBodyName: false, NewMove: false,
  SmoothMove: true, Atmosphere: true, SmoothShadows: true, CozyMode: true,
  AutoPot: false, AutoPotHP: 50, AutoPotMP: 30,
  ShowNormalChat: true, ShowYellChat: true, ShowWhisperChat: true, ShowLoverChat: true, ShowMentorChat: true, ShowGroupChat: true, ShowGuildChat: true,
  FilterNormalChat: false, FilterWhisperChat: false, FilterShoutChat: false, FilterSystemChat: false, FilterLoverChat: false,
  FilterMentorChat: false, FilterGroupChat: false, FilterGuildChat: false,
  Lighting: "Enhanced", // web default; draws Classic where the GPU has no half-float targets (SceneLights.useEnhanced)
  UiStyle: "Default",
  UiScale: 1, UiAutoScale: true, UiTheme: "Midnight electric",
  UiAccent: "", UiFrame: "", UiText: "", UiHue: -1, UiSat: -1, UiLit: -1, UiGlow: -1, UiRadius: -1, UiOpacity: 1,
  UiPlates: "AC,MAC,DC,Bag,Weight,Gold",
  MobileUi: "Auto", MobileLeftHanded: false,
  MobileHaptics: true, MobileAutoLoot: false, MobileBatterySaver: false, MobileZoom: 1, MobileLayout: "", MobileTextSize: "Normal",
};

const HEX_COLOUR = /^#[0-9a-f]{6}$/i;
/** A finite number within [lo, hi], else `def`. */
const inRange = (v: number, lo: number, hi: number, def: number) => (Number.isFinite(v) && v >= lo && v <= hi ? v : def);

const STORAGE_KEY = "crystal.settings";

function load(): GameSettings {
  const s = { ...DEFAULT_SETTINGS };
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) {
      const saved = JSON.parse(raw) as Partial<Record<keyof GameSettings, unknown>>;
      for (const k of Object.keys(s) as (keyof GameSettings)[]) {
        if (typeof saved[k] === typeof s[k]) (s as Record<string, unknown>)[k] = saved[k];
      }
      if (s.Lighting !== "Classic" && s.Lighting !== "Enhanced") s.Lighting = DEFAULT_SETTINGS.Lighting;
      if (s.UiStyle !== "Default" && s.UiStyle !== "Modern" && s.UiStyle !== "Classic") s.UiStyle = DEFAULT_SETTINGS.UiStyle;
      if (s.MobileUi !== "Auto" && s.MobileUi !== "On" && s.MobileUi !== "Off") s.MobileUi = DEFAULT_SETTINGS.MobileUi;
      s.UiScale = inRange(s.UiScale, 0.5, 2, DEFAULT_SETTINGS.UiScale);
      s.AutoPotHP = inRange(s.AutoPotHP, 0, 99, DEFAULT_SETTINGS.AutoPotHP);
      s.AutoPotMP = inRange(s.AutoPotMP, 0, 99, DEFAULT_SETTINGS.AutoPotMP);
      s.MobileZoom = inRange(s.MobileZoom, 1, 1.6, DEFAULT_SETTINGS.MobileZoom);
      if (s.MobileLayout.length > 2000) s.MobileLayout = "";
      if (s.MobileTextSize !== "Small" && s.MobileTextSize !== "Normal" && s.MobileTextSize !== "Large") s.MobileTextSize = DEFAULT_SETTINGS.MobileTextSize;
      s.UiOpacity = inRange(s.UiOpacity, 0.2, 1, DEFAULT_SETTINGS.UiOpacity);
      if (!s.UiTheme || s.UiTheme.length > 64) s.UiTheme = DEFAULT_SETTINGS.UiTheme;
      for (const k of ["UiAccent", "UiFrame", "UiText"] as const) if (s[k] !== "" && !HEX_COLOUR.test(s[k])) s[k] = "";
      // the knobs: negative = the preset's; out-of-range values are clamped where they are applied (theme.ts resolveTheme)
      for (const k of ["UiHue", "UiSat", "UiLit", "UiGlow", "UiRadius"] as const) if (!Number.isFinite(s[k]) || s[k] < 0) s[k] = -1;
      if (s.UiPlates.length > 200 || !/^[A-Za-z0-9_, ]*$/.test(s.UiPlates)) s.UiPlates = DEFAULT_SETTINGS.UiPlates;
    }
  } catch { /* private window / blocked storage: defaults */ }
  return s;
}

/** The live options; never assign fields directly (use setSetting). */
export const gameSettings: Readonly<GameSettings> = load();

const listeners = new Set<(key: keyof GameSettings) => void>();

export function setSetting<K extends keyof GameSettings>(key: K, value: GameSettings[K]): void {
  if (gameSettings[key] === value) return;
  (gameSettings as GameSettings)[key] = value;
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify(gameSettings)); } catch { /* not persisted */ }
  for (const fn of listeners) fn(key);
}

/** The game UI to build: the saved choice, or DEFAULT_UI_STYLE while it is "Default". */
export function uiStyle(): "Modern" | "Classic" {
  return gameSettings.UiStyle === "Default" ? DEFAULT_UI_STYLE : gameSettings.UiStyle;
}

/** `?mobile=on|off` in the page URL forces the touch layout for this visit (testing on a desktop); not saved. */
function mobileOverride(): boolean | undefined {
  try {
    const v = new URLSearchParams(location.search).get("mobile");
    return v === "on" || v === "1" ? true : v === "off" || v === "0" ? false : undefined;
  } catch { return undefined; }
}

/** The touch layout is on: Modern UI and MobileUi On, or Auto on a coarse (touch) pointer. Read once per screen. */
export function isMobileUi(): boolean {
  if (uiStyle() !== "Modern") return false;
  const forced = mobileOverride();
  if (forced !== undefined) return forced;
  if (gameSettings.MobileUi !== "Auto") return gameSettings.MobileUi === "On";
  try { return matchMedia("(pointer: coarse)").matches; } catch { return false; }
}

/** The running game uses the touch layout (game.ts startGame sets it): gates lowPower(). */
let touchActive = false;
export function setTouchActive(on: boolean): void { touchActive = on; }

/** Battery saver: the touch layout with MobileBatterySaver on (game.ts caps the loop at 30 fps, atmosphere.ts is off). */
export function lowPower(): boolean { return touchActive && gameSettings.MobileBatterySaver; }

/** Atmosphere effects on: the setting, unless the battery saver runs. */
export function atmosphereOn(): boolean { return gameSettings.Atmosphere && !lowPower(); }

/** Returns an unsubscribe function. */
export function onSettingChange(fn: (key: keyof GameSettings) => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

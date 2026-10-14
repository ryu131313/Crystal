// The desktop's per-player client options (Client/Settings.cs: the [Game] / [Chat] / [Filters] / [Graphics] Lighting values
// that OptionDialog and ChatOptionDialog edit), kept in localStorage instead of Mir2Config.ini. Sound volumes stay in
// audio.ts (it already persists them). Read `gameSettings.X`; change with `setSetting("X", v)` so listeners hear it.

/** Settings.Lighting (LightingMode): Classic = MapControl.DrawLights, Enhanced = MapControl.Lighting.cs. */
export type LightingMode = "Classic" | "Enhanced";

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
  // [Chat] (ChatOptionDialog: which chat types show) and [Filters] (FilterXChat: hide that type)
  ShowNormalChat: boolean; ShowYellChat: boolean; ShowWhisperChat: boolean; ShowLoverChat: boolean; ShowMentorChat: boolean;
  ShowGroupChat: boolean; ShowGuildChat: boolean;
  FilterNormalChat: boolean; FilterWhisperChat: boolean; FilterShoutChat: boolean; FilterSystemChat: boolean; FilterLoverChat: boolean;
  FilterMentorChat: boolean; FilterGroupChat: boolean; FilterGuildChat: boolean;
  // [Graphics] Lighting (web: both modes run on WebGL2)
  Lighting: LightingMode;
}

/** Client/Settings.cs defaults. */
export const DEFAULT_SETTINGS: GameSettings = {
  SkillMode: false, SkillBar: true, Effect: true, LevelEffect: true, DropView: true, NameView: true, HPView: true,
  TransparentChat: false, ModeView: false, DuraView: false, DisplayDamage: true, TargetDead: false, HighlightTarget: true,
  ExpandedBuffWindow: true, ExpandedHeroBuffWindow: true, DisplayBodyName: false, NewMove: false,
  SmoothMove: true, Atmosphere: true, SmoothShadows: true, CozyMode: true,
  ShowNormalChat: true, ShowYellChat: true, ShowWhisperChat: true, ShowLoverChat: true, ShowMentorChat: true, ShowGroupChat: true, ShowGuildChat: true,
  FilterNormalChat: false, FilterWhisperChat: false, FilterShoutChat: false, FilterSystemChat: false, FilterLoverChat: false,
  FilterMentorChat: false, FilterGroupChat: false, FilterGuildChat: false,
  Lighting: "Enhanced", // web default; draws Classic where the GPU has no half-float targets (SceneLights.useEnhanced)
};

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

/** Returns an unsubscribe function. */
export function onSettingChange(fn: (key: keyof GameSettings) => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

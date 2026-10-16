// Ported from play/hudMock.ts (the theme variables, ~150-160, and HudMock.set) and play/lab.ts (the nine theme presets,
// ~711-721): the new UI's colours. Every colour in src/ui/modern comes from CSS variables on the '.hudm' root:
//   base (written here):   --h --sat --lit (panel hue / saturation / brightness), --ac accent, --fr frame, --tx text,
//                          --gl glow strength, --rad corner radius
//   derived (styles.ts):   --pa --pb --pc panels, --frl --frd frame light / dark, --acd, --dim labels, --glow, --glr
//   semantic (styles.ts):  --ok --bad --warn --gold --info, --grade-none ... --grade-heroic (ItemGrade), --cls-* (MirClass)
// Players pick a preset and may override any base value (the decision table: theme presets + custom colours).
// DOM-free apart from applyTheme().

/** The base values one theme sets. */
export interface ThemeColors {
  /** Panel hue (0-360), saturation (0-2) and brightness (0.3-2.5) multipliers. */
  hue: number; sat: number; lit: number;
  /** Accent, frame and text colours (#rrggbb). */
  accent: string; frame: string; text: string;
  /** Glow strength (0-2). */
  glow: number;
  /** Corner radius in px (0-12). */
  radius: number;
}

export interface ThemePreset extends ThemeColors { readonly name: string }

const p = (name: string, hue: number, sat: number, lit: number, accent: string, frame: string, text: string, glow: number): ThemePreset =>
  ({ name, hue, sat, lit, accent, frame, text, glow, radius: 5 });

/** The scene lab's nine presets (play/lab.ts THEMES); the first is the default. */
export const THEME_PRESETS: readonly ThemePreset[] = [
  p("Midnight electric", 218, 1, 1, "#3fb4ff", "#3a5a8c", "#d6e2f5", 1),
  p("Abyss", 222, 0.8, 0.65, "#00c8ff", "#24395e", "#c8d6ec", 1.2),
  p("Neon", 224, 1.3, 0.85, "#19e3ff", "#1f4f9a", "#e0f4ff", 1.6),
  p("Royal", 230, 1.1, 1.15, "#5c8dff", "#4a5fa8", "#e2e8ff", 1),
  p("Steel", 214, 0.45, 1.1, "#7cc4ff", "#5a6c86", "#dce4ee", 0.7),
  p("Ice", 205, 0.6, 1.3, "#a6ecff", "#5d86a8", "#eef8ff", 0.9),
  p("Royal gold", 224, 1.25, 1.2, "#e8b84a", "#8a6a2e", "#f2ead6", 0.9),
  p("Dark brown / red", 14, 0.55, 0.9, "#c8402e", "#5e3a2a", "#ead8cc", 0.8),
  p("Lost Ark red", 356, 0.8, 0.9, "#ff3b3b", "#6a2a30", "#f0dada", 1),
];

export const DEFAULT_THEME = "Midnight electric";

/** Knob ranges (the lab's HUD knobs, lab.ts HUD array) for an options page and for clamping saved values. */
export const THEME_RANGES = {
  hue: [0, 360], sat: [0, 2], lit: [0.3, 2.5], glow: [0, 2], radius: [0, 12],
} as const satisfies Partial<Record<keyof ThemeColors, readonly [number, number]>>;

/** What the settings hold: a preset name plus optional overrides (the custom colours). Unknown preset = the default.
 *  "Not overridden" may be undefined, "" (colours) or a negative number, so flat settings fields can keep a typed default. */
export interface ThemeSettings {
  theme?: string;
  custom?: Partial<ThemeColors>;
}

const HEX = /^#[0-9a-f]{6}$/i;
const clamp = (v: number, [lo, hi]: readonly [number, number]) => Math.min(hi, Math.max(lo, v));

export function themePreset(name: string | undefined): ThemePreset {
  return THEME_PRESETS.find((t) => t.name === name) ?? THEME_PRESETS[0]!;
}

/** The preset with the valid overrides applied (numbers clamped, colours must be #rrggbb). */
export function resolveTheme(s: ThemeSettings | undefined): ThemeColors {
  const base = themePreset(s?.theme);
  const out: ThemeColors = { hue: base.hue, sat: base.sat, lit: base.lit, accent: base.accent, frame: base.frame, text: base.text, glow: base.glow, radius: base.radius };
  const c = s?.custom;
  if (!c) return out;
  for (const k of ["hue", "sat", "lit", "glow", "radius"] as const) {
    const v = c[k];
    if (typeof v === "number" && Number.isFinite(v) && v >= 0) out[k] = clamp(v, THEME_RANGES[k]); // < 0 = not overridden
  }
  for (const k of ["accent", "frame", "text"] as const) {
    const v = c[k];
    if (typeof v === "string" && HEX.test(v)) out[k] = v.toLowerCase();
  }
  return out;
}

/** Writes the base variables on the root (the derived and semantic ones follow through CSS). */
export function applyTheme(root: HTMLElement, s: ThemeSettings | undefined): ThemeColors {
  const t = resolveTheme(s);
  const st = root.style;
  st.setProperty("--h", String(t.hue));
  st.setProperty("--sat", String(t.sat));
  st.setProperty("--lit", String(t.lit));
  st.setProperty("--ac", t.accent);
  st.setProperty("--fr", t.frame);
  st.setProperty("--tx", t.text);
  st.setProperty("--gl", String(t.glow));
  st.setProperty("--rad", t.radius + "px");
  return t;
}

/** "#rrggbb" + alpha (0-1) as an rgba() string, for canvas drawing (the minimap reads the accent from the resolved
 *  theme instead of getComputedStyle, Performance.md T3-5). */
export function rgba(hex: string, alpha = 1): string {
  const m = HEX.exec(hex) ? hex : "#ffffff";
  const n = parseInt(m.slice(1), 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
}

/** Semantic colours as literal values for canvas drawing (the same as the CSS tokens in styles.ts). */
export const SEMANTIC = {
  ok: "#6fe08a", bad: "#ff5a4a", warn: "#f3c04a", gold: "#f3e08a", info: "#7cc4ff",
} as const;

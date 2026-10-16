// Options window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern settings screen for the
// desktop's OptionDialog / KeyboardLayoutDialog: a category menu (with a dot where there are unsaved changes), search
// across every setting and key bind, graphics presets, toggles / sliders / selects / segmented / colour swatches with a
// description each and a reset arrow when a value differs from its default, and a Controls page listing the client's
// key binds (src/game/keybinds.ts: the desktop's KeyBindSettings defaults, grouped) with click-to-rebind and conflict
// warnings. Changes are drafts until Apply; Cancel reverts. Nothing is saved anywhere: rebinding is local to the mock
// (the lab shares the web client's origin, so it must never write the game's "crystal.keybinds" storage).

import { keyBinds, keyName, isModifierKeyName, tildeHeld, type KeyBind } from "../src/game/keybinds.ts";
import { HudWindow, h, injectCss, rafCoalesce } from "./hudWindow.ts";

type Cat = "Graphics" | "Interface" | "Gameplay" | "Audio" | "Chat" | "Controls" | "Social" | "Accessibility";
type Ctl =
  | { kind: "toggle" }
  | { kind: "slider"; min: number; max: number; step: number; unit?: string }
  | { kind: "select"; options: string[] }
  | { kind: "seg"; options: string[] }
  | { kind: "swatch"; options: string[] };
type Val = boolean | number | string;
interface Setting { id: string; cat: Cat; group: string; label: string; desc: string; ctl: Ctl; def: Val }

const CATS: [Cat, string][] = [
  ["Graphics", "M3 5h18v12H3zM8 21h8M12 17v4"],
  ["Interface", "M4 4h16v16H4zM4 9h16M9 9v11"],
  ["Gameplay", "M3 3l12 12M21 3L9 15M13 17l4 4M17 17l-4 4M7 17l4 4M11 17l-4 4"],
  ["Audio", "M4 9h4l5-4v14l-5-4H4zM16 9a4 4 0 010 6M19 6a8 8 0 010 12"],
  ["Chat", "M4 5h16v10H9l-5 4z"],
  ["Controls", "M3 7h18v10H3zM7 11h.01M11 11h.01M15 11h.01M7 14h10"],
  ["Social", "M9 11a3 3 0 100-6 3 3 0 000 6zM17 11a2.5 2.5 0 100-5 2.5 2.5 0 000 5zM3 20c0-3.5 2.7-6 6-6s6 2.5 6 6M15 14.5c3 0 6 2 6 5"],
  ["Accessibility", "M12 4a2 2 0 110 4 2 2 0 010-4zM4 9l8 2 8-2M12 11v4l-3 6M12 15l3 6"],
];
const T = (): Ctl => ({ kind: "toggle" });
const S = (min: number, max: number, step = 1, unit = ""): Ctl => ({ kind: "slider", min, max, step, unit });
const SEL = (...options: string[]): Ctl => ({ kind: "select", options });
const SEG = (...options: string[]): Ctl => ({ kind: "seg", options });
const SETTINGS: Setting[] = [
  // Graphics
  { id: "res", cat: "Graphics", group: "Display", label: "Resolution", desc: "The game's screen size; the HUD scales with it.", ctl: SEL("1024 x 768", "1280 x 800", "1366 x 768", "1920 x 1080"), def: "1920 x 1080" },
  { id: "mode", cat: "Graphics", group: "Display", label: "Display mode", desc: "Windowed keeps the desktop visible; borderless fills the screen without changing it.", ctl: SEG("Windowed", "Borderless", "Fullscreen"), def: "Borderless" },
  { id: "renderer", cat: "Graphics", group: "Display", label: "Renderer", desc: "Direct3D 11 is the default; Direct3D 9 is kept for old hardware.", ctl: SEG("Direct3D 11", "Direct3D 9"), def: "Direct3D 11" },
  { id: "bright", cat: "Graphics", group: "Display", label: "Brightness", desc: "Overall brightness of the scene (not the HUD).", ctl: S(50, 150, 1, "%"), def: 100 },
  { id: "light", cat: "Graphics", group: "Lighting & effects", label: "Lighting", desc: "Enhanced adds soft light, glow and night shading; Classic matches the original client.", ctl: SEG("Classic", "Enhanced"), def: "Enhanced" },
  { id: "atmo", cat: "Graphics", group: "Lighting & effects", label: "Atmosphere effects", desc: "Glow, embers, heat haze, fog and puddle reflections.", ctl: T(), def: true },
  { id: "weather", cat: "Graphics", group: "Lighting & effects", label: "Weather", desc: "Rain, snow and fog where the map has them.", ctl: T(), def: true },
  { id: "fxq", cat: "Graphics", group: "Lighting & effects", label: "Effect quality", desc: "Particle density of spells and weather.", ctl: SEG("Low", "Medium", "High"), def: "High" },
  { id: "shadow", cat: "Graphics", group: "Lighting & effects", label: "Smooth shadows", desc: "Solid soft shadows instead of the original checkerboard.", ctl: T(), def: true },
  { id: "objshadow", cat: "Graphics", group: "Lighting & effects", label: "Map object shadows", desc: "Trees, walls and buildings cast the same soft shadow.", ctl: T(), def: true },
  { id: "cozy", cat: "Graphics", group: "Lighting & effects", label: "Cozy colour grade", desc: "A gentle contrast and colour pass over the scene.", ctl: T(), def: true },
  // Interface
  { id: "hudscale", cat: "Interface", group: "HUD", label: "HUD scale", desc: "Size of the HUD and windows; Auto follows the screen height.", ctl: S(70, 160, 5, "%"), def: 100 },
  { id: "hudauto", cat: "Interface", group: "HUD", label: "Auto scale", desc: "Grow the HUD with the screen height (1 at 768 px).", ctl: T(), def: true },
  { id: "theme", cat: "Interface", group: "HUD", label: "Theme", desc: "The colour theme of the HUD and every window.", ctl: SEL("Midnight electric", "Abyss", "Neon", "Royal", "Royal gold", "Steel", "Ice", "Dark brown / red"), def: "Midnight electric" },
  { id: "accent", cat: "Interface", group: "HUD", label: "Accent colour", desc: "Highlights, glows and the EXP ring.", ctl: { kind: "swatch", options: ["#3fb4ff", "#19e3ff", "#5c8dff", "#a6ecff", "#e8b84a", "#c8402e", "#4fe07a", "#c07cff"] }, def: "#3fb4ff" },
  { id: "keys", cat: "Interface", group: "HUD", label: "Key labels on slots", desc: "Show F1-F8 and 1-0 on the skill bar and belt.", ctl: T(), def: true },
  { id: "tracker", cat: "Interface", group: "HUD", label: "Quest tracker", desc: "Show tracked quests on the right of the screen.", ctl: T(), def: true },
  { id: "names", cat: "Interface", group: "World", label: "Names above heads", desc: "Which names are always shown (others show on hover).", ctl: SEG("All", "Players", "None"), def: "All" },
  { id: "drops", cat: "Interface", group: "World", label: "Item names on the ground", desc: "Label dropped items (Alt shows them all).", ctl: T(), def: true },
  { id: "dmg", cat: "Interface", group: "World", label: "Damage numbers", desc: "Floating damage, heals and crits.", ctl: SEG("Off", "Mine", "All"), def: "All" },
  { id: "hpbars", cat: "Interface", group: "World", label: "Health bars", desc: "Bars over monsters and players when damaged.", ctl: SEG("Off", "Damaged", "Always"), def: "Damaged" },
  { id: "target", cat: "Interface", group: "World", label: "Target highlight", desc: "Outline the monster or player you are attacking.", ctl: T(), def: true },
  // Gameplay
  { id: "autopick", cat: "Gameplay", group: "Combat", label: "Auto pick up", desc: "Walk over your own drops to pick them up.", ctl: T(), def: true },
  { id: "deadtarget", cat: "Gameplay", group: "Combat", label: "Target dead monsters", desc: "Allow clicking corpses (for Harvest).", ctl: T(), def: false },
  { id: "pvpconfirm", cat: "Gameplay", group: "Combat", label: "Confirm attacking players", desc: "Ask before your first hit on a non-red player outside a war.", ctl: T(), def: true },
  { id: "helmet", cat: "Gameplay", group: "Character", label: "Show helmet", desc: "Draw your helmet on your character.", ctl: T(), def: true },
  { id: "dura", cat: "Gameplay", group: "Character", label: "Durability warnings", desc: "Show equipment that is close to breaking.", ctl: T(), def: true },
  { id: "potion", cat: "Gameplay", group: "Character", label: "Low health warning", desc: "Pulse the HP bar below this much health.", ctl: S(10, 60, 5, "%"), def: 30 },
  // Audio
  { id: "vmaster", cat: "Audio", group: "Volume", label: "Master", desc: "Everything the game plays.", ctl: S(0, 100, 1, "%"), def: 80 },
  { id: "vmusic", cat: "Audio", group: "Volume", label: "Music", desc: "Map and login music.", ctl: S(0, 100, 1, "%"), def: 60 },
  { id: "vsfx", cat: "Audio", group: "Volume", label: "Effects", desc: "Spells, hits, footsteps and monsters.", ctl: S(0, 100, 1, "%"), def: 85 },
  { id: "vamb", cat: "Audio", group: "Volume", label: "Ambience", desc: "Rain, wind and thunder.", ctl: S(0, 100, 1, "%"), def: 30 },
  { id: "vui", cat: "Audio", group: "Volume", label: "Interface", desc: "Button clicks and window sounds.", ctl: S(0, 100, 1, "%"), def: 70 },
  { id: "unfocus", cat: "Audio", group: "Behaviour", label: "Mute in background", desc: "Silence the game when its window is not in front.", ctl: T(), def: false },
  // Chat
  { id: "chatsize", cat: "Chat", group: "Window", label: "Font size", desc: "Chat text size.", ctl: SEG("Small", "Normal", "Large"), def: "Normal" },
  { id: "chatop", cat: "Chat", group: "Window", label: "Background opacity", desc: "How dark the chat box is behind the text.", ctl: S(0, 100, 5, "%"), def: 45 },
  { id: "chattime", cat: "Chat", group: "Window", label: "Timestamps", desc: "Show the time before each line.", ctl: T(), def: false },
  { id: "chwhisper", cat: "Chat", group: "Channels", label: "Whispers in their own tab", desc: "Keep private messages out of the main chat.", ctl: T(), def: true },
  { id: "chshout", cat: "Chat", group: "Channels", label: "Shouts", desc: "Show shouts from the whole map.", ctl: T(), def: true },
  { id: "chtrade", cat: "Chat", group: "Channels", label: "Trade channel", desc: "Show buying / selling messages.", ctl: T(), def: false },
  { id: "chsys", cat: "Chat", group: "Channels", label: "System messages", desc: "Drops, EXP and other notices.", ctl: SEG("Off", "Important", "All"), def: "Important" },
  { id: "filter", cat: "Chat", group: "Channels", label: "Language filter", desc: "Mask rude words.", ctl: T(), def: true },
  // Social
  { id: "whisperfrom", cat: "Social", group: "Who can reach you", label: "Whispers from", desc: "Others get a polite notice when you do not accept theirs.", ctl: SEG("Everyone", "Friends & guild", "Nobody"), def: "Everyone" },
  { id: "tradefrom", cat: "Social", group: "Who can reach you", label: "Trade requests", desc: "Who can ask you to trade.", ctl: SEG("Everyone", "Friends & guild", "Nobody"), def: "Everyone" },
  { id: "groupfrom", cat: "Social", group: "Who can reach you", label: "Group invites", desc: "Who can invite you to a group.", ctl: SEG("Everyone", "Friends & guild", "Nobody"), def: "Everyone" },
  { id: "friendreq", cat: "Social", group: "Who can reach you", label: "Friend requests", desc: "Allow new friend requests.", ctl: T(), def: true },
  { id: "showonline", cat: "Social", group: "Privacy", label: "Show me online to friends", desc: "Appear offline while still playing.", ctl: T(), def: true },
  { id: "showloc", cat: "Social", group: "Privacy", label: "Share my location", desc: "Friends and guild see which map you are on.", ctl: T(), def: true },
  // Accessibility
  { id: "textscale", cat: "Accessibility", group: "Reading", label: "Interface text size", desc: "Make window text larger.", ctl: S(90, 140, 5, "%"), def: 100 },
  { id: "contrast", cat: "Accessibility", group: "Reading", label: "High contrast outlines", desc: "Stronger borders around slots, bars and windows.", ctl: T(), def: false },
  { id: "cb", cat: "Accessibility", group: "Colour", label: "Colour-blind mode", desc: "Adjust HP / MP, quest and team colours.", ctl: SEL("Off", "Protanopia", "Deuteranopia", "Tritanopia"), def: "Off" },
  { id: "motion", cat: "Accessibility", group: "Motion", label: "Reduce motion", desc: "Calmer glows, no screen shake or flashing.", ctl: T(), def: false },
  { id: "flash", cat: "Accessibility", group: "Motion", label: "Lightning flashes", desc: "The white flash of lightning and big spells.", ctl: T(), def: true },
];
const PRESETS: Record<string, Record<string, Val>> = {
  Performance: { light: "Classic", atmo: false, weather: false, fxq: "Low", shadow: false, objshadow: false, cozy: false },
  Balanced: { light: "Enhanced", atmo: true, weather: true, fxq: "Medium", shadow: true, objshadow: false, cozy: true },
  Quality: { light: "Enhanced", atmo: true, weather: true, fxq: "High", shadow: true, objshadow: true, cozy: true },
};

/** A bind's label: the modifiers it requires (Require 1; "~" = the tilde key held, CMain.Tilde) and the key. */
const bindLabel = (b: Bind) =>
  !b.key ? "Unbound" : `${b.tilde === 1 ? "~+" : ""}${b.ctrl === 1 ? "Ctrl+" : ""}${b.alt === 1 ? "Alt+" : ""}${b.shift === 1 ? "Shift+" : ""}${b.key.replace(/^D(\d)$/, "$1")}`;
type Bind = { key: string; ctrl: number; alt: number; shift: number; tilde: number };
const fromKb = (b: KeyBind): Bind => ({ key: b.key, ctrl: b.requireCtrl, alt: b.requireAlt, shift: b.requireShift, tilde: b.requireTilde });

const CSS = `
.hudm .owin { --winh: 580px; width: 920px; }
.hudm .owin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .op-ic { width: 15px; height: 15px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .owin .omain { flex: 1; min-height: 0; display: flex; }
.hudm .owin .nav { width: 180px; flex: none; padding: 10px 8px; display: flex; flex-direction: column; gap: 3px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .owin .nav input { height: 28px; margin-bottom: 8px; }
.hudm .owin .nav .it { position: relative; display: flex; align-items: center; gap: 10px; height: 32px; padding: 0 10px; cursor: pointer; user-select: none; border-radius: var(--rad);
  font-size: 12px; font-weight: 600; color: var(--dim); border: 1px solid transparent; }
.hudm .owin .nav .it:hover { color: var(--tx); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .owin .nav .it.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .owin .nav .it.on .op-ic { color: var(--ac); filter: drop-shadow(0 0 4px var(--glow)); }
.hudm .owin .nav .it.dirty::after { content: ""; position: absolute; right: 10px; width: 7px; height: 7px; border-radius: 50%; background: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .owin input.in { width: 100%; box-sizing: border-box; padding: 0 9px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .owin input.in:focus { border-color: var(--ac); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .owin input.in::placeholder { color: var(--dim); }
.hudm .owin .page { flex: 1; min-width: 0; overflow-y: auto; padding: 14px 20px 18px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .owin .ph { display: flex; align-items: center; gap: 12px; margin-bottom: 6px; }
.hudm .owin .ph h2 { margin: 0; font-size: 18px; color: #fff; }
.hudm .owin .ph .sub { font-size: 11px; color: var(--dim); }
.hudm .owin .ph .right { margin-left: auto; display: flex; gap: 6px; align-items: center; }
.hudm .owin .presets { display: flex; border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); overflow: hidden; }
.hudm .owin .presets div { padding: 0 12px; height: 24px; display: flex; align-items: center; cursor: pointer; font-size: 11px; font-weight: 600; color: var(--dim); background: color-mix(in srgb, var(--pb) 60%, #000); }
.hudm .owin .presets div + div { border-left: 1px solid var(--fr); }
.hudm .owin .presets div:hover { color: #fff; }
.hudm .owin .presets div.on { color: #fff; background: color-mix(in srgb, var(--ac) 28%, var(--pb)); box-shadow: inset 0 -2px 0 var(--ac); }
.hudm .owin .gh { margin: 16px 0 4px; font-size: 10px; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; color: var(--acd); display: flex; align-items: center; gap: 10px; }
.hudm .owin .gh::after { content: ""; flex: 1; height: 1px; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 40%, transparent), transparent); }
.hudm .owin .gh span { color: var(--dim); letter-spacing: .08em; }
.hudm .owin .set { display: flex; align-items: center; gap: 16px; padding: 9px 10px; border-radius: var(--rad); border: 1px solid transparent; }
.hudm .owin .set:hover { background: color-mix(in srgb, var(--frl) 5%, transparent); border-color: color-mix(in srgb, var(--fr) 40%, transparent); }
.hudm .owin .set .lb { flex: 1; min-width: 0; }
.hudm .owin .set .lb b { display: flex; align-items: center; gap: 7px; font-size: 13px; font-weight: 600; color: var(--tx); }
.hudm .owin .set .lb small { display: block; margin-top: 2px; font-size: 11px; color: var(--dim); }
.hudm .owin .set.chg .lb b::after { content: "unsaved"; font-size: 9px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; color: var(--ac); }
.hudm .owin .rst { width: 20px; height: 20px; flex: none; display: flex; align-items: center; justify-content: center; border-radius: 50%; cursor: pointer; font-size: 13px; color: var(--dim); visibility: hidden; }
.hudm .owin .set.mod .rst { visibility: visible; }
.hudm .owin .rst:hover { color: #fff; background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .owin .ctl { flex: none; width: 300px; display: flex; justify-content: flex-end; align-items: center; gap: 10px; }
.hudm .owin .tog { width: 38px; height: 20px; border-radius: 10px; position: relative; cursor: pointer; background: color-mix(in srgb, var(--fr) 60%, #000); border: 1px solid var(--fr); }
.hudm .owin .tog::after { content: ""; position: absolute; left: 2px; top: 2px; width: 14px; height: 14px; border-radius: 50%; background: var(--dim); transition: left .15s, background .15s; }
.hudm .owin .tog.on { background: color-mix(in srgb, var(--ac) 45%, #000); border-color: var(--ac); }
.hudm .owin .tog.on::after { left: 20px; background: #fff; box-shadow: 0 0 6px var(--ac); }
.hudm .owin input[type=range] { -webkit-appearance: none; appearance: none; width: 210px; height: 4px; border-radius: 2px; outline: none; cursor: pointer;
  background: linear-gradient(90deg, var(--ac) 0 var(--p, 50%), color-mix(in srgb, var(--fr) 70%, #000) var(--p, 50%) 100%); }
.hudm .owin input[type=range]::-webkit-slider-thumb { -webkit-appearance: none; width: 14px; height: 14px; border-radius: 50%; background: #fff; border: 2px solid var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .owin .val { width: 48px; text-align: right; font-size: 12px; font-weight: 600; color: var(--tx); font-variant-numeric: tabular-nums; }
.hudm .owin select { height: 26px; min-width: 200px; padding: 0 8px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx); cursor: pointer;
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .owin select:focus { border-color: var(--ac); }
.hudm .owin .seg { display: flex; border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); overflow: hidden; }
.hudm .owin .seg div { padding: 0 10px; height: 24px; display: flex; align-items: center; cursor: pointer; user-select: none; font-size: 11px; font-weight: 600; color: var(--dim);
  background: color-mix(in srgb, var(--pb) 60%, #000); white-space: nowrap; }
.hudm .owin .seg div + div { border-left: 1px solid var(--fr); }
.hudm .owin .seg div:hover { color: var(--tx); }
.hudm .owin .seg div.on { color: #fff; background: color-mix(in srgb, var(--ac) 30%, var(--pb)); }
.hudm .owin .sw { display: flex; gap: 6px; }
.hudm .owin .sw div { width: 22px; height: 22px; border-radius: 50%; cursor: pointer; border: 2px solid rgba(0,0,0,.6); box-shadow: 0 0 0 1px var(--fr); }
.hudm .owin .sw div.on { box-shadow: 0 0 0 2px #fff, 0 0 10px currentColor; }
/* Controls */
.hudm .owin .kb { display: flex; align-items: center; gap: 12px; padding: 6px 10px; border-radius: var(--rad); font-size: 12px; }
.hudm .owin .kb:nth-child(even) { background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .owin .kb span { flex: 1; color: var(--tx); }
.hudm .owin .kb.conf span::after { content: " · conflicts with " attr(data-with); font-size: 10px; color: #ff9a6a; }
.hudm .owin .key { min-width: 90px; height: 24px; padding: 0 10px; display: flex; align-items: center; justify-content: center; cursor: pointer; user-select: none;
  font: 600 11px ui-monospace, Consolas, monospace; color: var(--tx); border-radius: calc(var(--rad) * .7); border: 1px solid var(--fr);
  background: linear-gradient(180deg, var(--pc), var(--pb)); box-shadow: 0 2px 0 rgba(0,0,0,.6); }
.hudm .owin .key:hover { border-color: var(--ac); }
.hudm .owin .key.unbound { color: var(--dim); font-style: italic; }
.hudm .owin .key.wait { color: #fff; border-color: var(--ac); box-shadow: 0 0 0 1px var(--ac), 0 0 10px var(--glow); animation: hudm-keywait 1s ease-in-out infinite; }
@keyframes hudm-keywait { 50% { background: color-mix(in srgb, var(--ac) 35%, var(--pb)); } }
.hudm .owin .kb.conf .key { border-color: #ff9a6a; }
.hudm .owin .kb.chg .key { color: var(--ac); }
.hudm .owin .hint { font-size: 11px; color: var(--dim); margin: 2px 0 6px; }
.hudm .owin .empty { padding: 30px; text-align: center; color: var(--dim); }
.hudm .owin mark { background: color-mix(in srgb, var(--ac) 45%, transparent); color: #fff; border-radius: 2px; }
/* Footer */
.hudm .owin .foot { display: flex; align-items: center; gap: 8px; padding: 10px 16px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .owin .foot .state { font-size: 11px; color: var(--dim); }
.hudm .owin .foot .state b { color: var(--ac); }
.hudm .owin .foot .state.saved { color: #6fe08f; }
.hudm .owin .foot .sp { flex: 1; }
.hudm .owin .foot .btn { height: 28px; padding: 0 14px; }
.hudm .owin .foot .btn.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .owin .foot .btn.dis { opacity: .4; pointer-events: none; }
`;

export class OptionsMock extends HudWindow {
  private cat: Cat = "Graphics";
  private saved: Record<string, Val> = Object.fromEntries(SETTINGS.map((s) => [s.id, s.def]));
  private draft: Record<string, Val> = { ...this.saved };
  private bindDefs: { fn: number; group: string; desc: string; def: Bind }[] = keyBinds().map((b) => ({ fn: b.function, group: b.group, desc: b.description, def: fromKb(b) }));
  private bindSaved = new Map<number, Bind>(this.bindDefs.map((b) => [b.fn, { ...b.def }]));
  private bindDraft = new Map<number, Bind>(this.bindDefs.map((b) => [b.fn, { ...b.def }]));
  private waiting: number | null = null;
  private justSaved = false;
  private query = "";
  private nav!: HTMLElement;
  private page!: HTMLElement;
  private foot!: HTMLElement;

  constructor() {
    super("owin", 52, 30);
    injectCss("hudm-options-css", CSS);
    this.build();
    // Rebinding: the next key press (with its modifiers) becomes the bind; Escape cancels, Backspace unbinds.
    window.addEventListener("keydown", (e) => {
      if (this.waiting === null || !this.isFront) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      const name = keyName(e);
      if (isModifierKeyName(name)) return;
      const fn = this.waiting;
      this.waiting = null;
      if (name !== "Escape") this.bindDraft.set(fn, name === "Back" ? { key: "", ctrl: 2, alt: 2, shift: 2, tilde: 2 } : { key: name, ctrl: e.ctrlKey ? 1 : 0, alt: e.altKey ? 1 : 0, shift: e.shiftKey ? 1 : 0, tilde: tildeHeld() ? 1 : 0 });
      this.render();
    }, { capture: true });
  }

  protected override shown(): void { this.render(); }
  /** A rebind still waiting for its key is dropped on close, so the next key after reopening is not taken as a bind. */
  protected override closed(): void { this.waiting = null; }

  private build(): void {
    const search = h("input", "in");
    search.placeholder = "Search settings…";
    search.addEventListener("keydown", (e) => e.stopPropagation());
    search.addEventListener("input", () => { this.query = search.value.trim(); this.render(); });
    this.nav = h("div", "nav", search);
    this.page = h("div", "page");
    this.foot = h("div", "foot");
    this.el.append(h("div", "panel", this.titleBar("OPTIONS", "Close (O)"), h("div", "omain", this.nav, this.page), this.foot));
  }

  // — State —
  private changedIds(): string[] { return SETTINGS.filter((s) => this.draft[s.id] !== this.saved[s.id]).map((s) => s.id); }
  private bindChanged(fn: number): boolean { const a = this.bindDraft.get(fn)!, b = this.bindSaved.get(fn)!; return bindLabel(a) !== bindLabel(b); }
  private changedBinds(): number[] { return this.bindDefs.filter((b) => this.bindChanged(b.fn)).map((b) => b.fn); }
  private catDirty(c: Cat): boolean { return c === "Controls" ? this.changedBinds().length > 0 : SETTINGS.some((s) => s.cat === c && this.draft[s.id] !== this.saved[s.id]); }

  private render(): void {
    this.renderNav();
    this.renderPage();
    this.renderFoot();
  }

  /** The category list: built once, then only its on / dirty classes change (it re-renders on every setting edit). */
  private navItems = new Map<Cat, HTMLElement>();
  private renderNav(): void {
    if (!this.navItems.size) for (const [c, d] of CATS) {
      const it = h("div", "it");
      it.append(icon(d), c);
      it.addEventListener("click", () => { this.cat = c; this.query = ""; (this.nav.firstChild as HTMLInputElement).value = ""; this.waiting = null; this.render(); });
      this.nav.append(it);
      this.navItems.set(c, it);
    }
    for (const [c, it] of this.navItems) {
      it.classList.toggle("on", c === this.cat && !this.query);
      it.classList.toggle("dirty", this.catDirty(c));
    }
  }
  /** Slider drags update the footer and the nav dirty marks at most once a frame. */
  private syncDirty = rafCoalesce(() => { this.renderFoot(); this.renderNav(); });

  private renderPage(): void {
    const top = this.page.scrollTop;
    this.page.replaceChildren();
    const q = this.query.toLowerCase();
    if (q) {
      // Search: matching settings and binds from every category, grouped by category.
      this.page.append(h("div", "ph", h("h2", "", `Results for “${this.query}”`)));
      let n = 0;
      for (const [c] of CATS) {
        const hits = c === "Controls"
          ? this.bindDefs.filter((b) => b.desc.toLowerCase().includes(q) || bindLabel(this.bindDraft.get(b.fn)!).toLowerCase().includes(q)).map((b) => this.bindRow(b))
          : SETTINGS.filter((s) => s.cat === c && (s.label + " " + s.desc + " " + s.group).toLowerCase().includes(q)).map((s) => this.settingRow(s));
        if (!hits.length) continue;
        n += hits.length;
        this.page.append(h("div", "gh", c, h("span", "", String(hits.length))), ...hits);
      }
      if (!n) this.page.append(h("div", "empty", "No settings match."));
      this.highlight(q);
      return;
    }
    const info: Record<Cat, string> = {
      Graphics: "How the world looks.", Interface: "The HUD, windows and labels.", Gameplay: "How the game behaves while you play.", Audio: "Volume and sound behaviour.",
      Chat: "The chat box and its channels.", Controls: "Click a key to rebind it. Esc cancels, Backspace unbinds.", Social: "Who can contact you and what they see.", Accessibility: "Make the game easier to read and calmer to watch.",
    };
    const ph = h("div", "ph", h("div", "", h("h2", "", this.cat), h("div", "sub", info[this.cat])));
    const right = h("div", "right");
    if (this.cat === "Graphics") {
      const pre = h("div", "presets");
      for (const [name, vals] of Object.entries(PRESETS)) {
        const on = Object.entries(vals).every(([k, v]) => this.draft[k] === v);
        const b = h("div", on ? "on" : "", name);
        b.addEventListener("click", () => { Object.assign(this.draft, vals); this.justSaved = false; this.render(); });
        pre.append(b);
      }
      right.append(h("span", "sub", "Preset"), pre);
    }
    const reset = h("div", "btn", "Reset section");
    reset.addEventListener("click", () => {
      if (this.cat === "Controls") for (const b of this.bindDefs) this.bindDraft.set(b.fn, { ...b.def });
      else for (const s of SETTINGS) if (s.cat === this.cat) this.draft[s.id] = s.def;
      this.justSaved = false; this.render();
    });
    right.append(reset);
    ph.append(right);
    this.page.append(ph);
    if (this.cat === "Controls") {
      const groups = [...new Set(this.bindDefs.map((b) => b.group))];
      for (const g of groups) {
        const rows = this.bindDefs.filter((b) => b.group === g);
        this.page.append(h("div", "gh", g, h("span", "", String(rows.length))), ...rows.map((b) => this.bindRow(b)));
      }
    } else {
      let group = "";
      for (const s of SETTINGS.filter((x) => x.cat === this.cat)) {
        if (s.group !== group) { group = s.group; this.page.append(h("div", "gh", group)); }
        this.page.append(this.settingRow(s));
      }
    }
    this.page.scrollTop = top;
  }

  /** Wrap search hits in <mark> inside labels / descriptions. */
  private highlight(q: string): void {
    for (const el of this.page.querySelectorAll<HTMLElement>(".lb b, .lb small, .kb > span")) {
      const t = el.firstChild;
      if (!t || t.nodeType !== Node.TEXT_NODE) continue;
      const s = t.textContent ?? "", i = s.toLowerCase().indexOf(q);
      if (i < 0) continue;
      t.replaceWith(s.slice(0, i), h("mark", "", s.slice(i, i + q.length)), s.slice(i + q.length));
    }
  }

  private settingRow(s: Setting): HTMLElement {
    const v = this.draft[s.id]!;
    const set = (nv: Val) => { this.draft[s.id] = nv; this.justSaved = false; this.render(); };
    const ctl = h("div", "ctl");
    const c = s.ctl;
    if (c.kind === "toggle") {
      const t = h("div", "tog" + (v ? " on" : ""));
      t.addEventListener("click", () => set(!v));
      ctl.append(t);
    } else if (c.kind === "slider") {
      const r = h("input");
      Object.assign(r, { type: "range", min: String(c.min), max: String(c.max), step: String(c.step), value: String(v) });
      const val = h("span", "val", `${v}${c.unit ?? ""}`);
      const paint = () => r.style.setProperty("--p", ((Number(r.value) - c.min) / (c.max - c.min) * 100) + "%");
      paint();
      r.addEventListener("input", () => { val.textContent = `${r.value}${c.unit ?? ""}`; paint(); this.draft[s.id] = Number(r.value); this.justSaved = false; this.syncDirty(); row.classList.toggle("chg", this.draft[s.id] !== this.saved[s.id]); row.classList.toggle("mod", this.draft[s.id] !== s.def); });
      r.addEventListener("keydown", (e) => e.stopPropagation());
      ctl.append(r, val);
    } else if (c.kind === "select") {
      const sel = h("select");
      for (const o of c.options) sel.append(Object.assign(h("option", "", o), { value: o }));
      sel.value = String(v);
      sel.addEventListener("change", () => set(sel.value));
      sel.addEventListener("keydown", (e) => e.stopPropagation());
      ctl.append(sel);
    } else if (c.kind === "seg") {
      const seg = h("div", "seg");
      for (const o of c.options) { const b = h("div", o === v ? "on" : "", o); b.addEventListener("click", () => set(o)); seg.append(b); }
      ctl.append(seg);
    } else {
      const sw = h("div", "sw");
      for (const o of c.options) { const b = h("div", o === v ? "on" : ""); b.style.background = o; b.style.color = o; b.title = o; b.addEventListener("click", () => set(o)); sw.append(b); }
      ctl.append(sw);
    }
    const rst = h("div", "rst", "↺");
    rst.title = "Back to the default";
    rst.addEventListener("click", () => set(s.def));
    const row = h("div", "set" + (v !== this.saved[s.id] ? " chg" : "") + (v !== s.def ? " mod" : ""), h("div", "lb", h("b", "", s.label), h("small", "", s.desc)), rst, ctl);
    return row;
  }

  private bindRow(b: { fn: number; group: string; desc: string; def: Bind }): HTMLElement {
    const cur = this.bindDraft.get(b.fn)!;
    const label = bindLabel(cur);
    const clash = cur.key ? this.bindDefs.find((o) => o.fn !== b.fn && bindLabel(this.bindDraft.get(o.fn)!) === label) : undefined;
    const key = h("div", "key" + (this.waiting === b.fn ? " wait" : "") + (cur.key ? "" : " unbound"), this.waiting === b.fn ? "Press a key…" : label);
    key.addEventListener("click", () => { this.waiting = this.waiting === b.fn ? null : b.fn; this.render(); });
    const rst = h("div", "rst", "↺");
    rst.title = `Default: ${bindLabel(b.def)}`;
    rst.addEventListener("click", () => { this.bindDraft.set(b.fn, { ...b.def }); this.render(); });
    const row = h("div", "kb set" + (clash ? " conf" : "") + (this.bindChanged(b.fn) ? " chg" : "") + (label !== bindLabel(b.def) ? " mod" : ""), h("span", "", b.desc), rst, key);
    if (clash) (row.firstChild as HTMLElement).dataset.with = clash.desc;
    return row;
  }

  private renderFoot(): void {
    const n = this.changedIds().length + this.changedBinds().length;
    const state = h("div", "state" + (n === 0 && this.justSaved ? " saved" : ""), ...(n ? [h("b", "", String(n)), ` unsaved change${n === 1 ? "" : "s"}`] : [this.justSaved ? "✓ Settings applied" : "All changes saved"]));
    const defaults = h("div", "btn", "Restore all defaults");
    defaults.addEventListener("click", () => {
      for (const s of SETTINGS) this.draft[s.id] = s.def;
      for (const b of this.bindDefs) this.bindDraft.set(b.fn, { ...b.def });
      this.justSaved = false; this.render();
    });
    const cancel = h("div", "btn" + (n ? "" : " dis"), "Cancel");
    cancel.addEventListener("click", () => {
      this.draft = { ...this.saved };
      for (const [k, v] of this.bindSaved) this.bindDraft.set(k, { ...v });
      this.waiting = null; this.render();
    });
    const apply = h("div", "btn main" + (n ? "" : " dis"), "Apply");
    apply.addEventListener("click", () => {
      this.saved = { ...this.draft };
      for (const [k, v] of this.bindDraft) this.bindSaved.set(k, { ...v });
      this.justSaved = true; this.render();
    });
    this.foot.replaceChildren(state, h("div", "sp"), defaults, cancel, apply);
  }
}

function icon(d: string): SVGElement {
  const s = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  s.setAttribute("viewBox", "0 0 24 24");
  s.setAttribute("class", "op-ic");
  const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
  p.setAttribute("d", d);
  s.append(p);
  return s;
}

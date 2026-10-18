// The touch layout of the Modern UI (settings.ts isMobileUi; built by modernUi.ts when UiRuntime.mobile). Hides the
// desktop rig and adds, inside the '.hudm' root:
//   surface  first child, the whole screen in game pixels: the map fingers. A press in the left 40% (below the top bar)
//            floats a joystick (walk, run when pushed past 70%); anywhere else it targets / talks / walks like a left
//            mouse-down, a held finger walks or runs toward it, a short tap on the ground walks there (a path), on the
//            user's cell picks up. A picked item or gold drops on the map first (DragController.mapClick).
//   pad      after the rig, scaled by 1 / (the stage's fit scale) so its pixels are CSS pixels: real 44+ px touch
//            targets and env(safe-area-inset-*) work. Top: HP / MP / level (tap = Character), the target (x clears),
//            Menu / Bag / Chat / Map left of the minimap. Bottom right: the held Attack button, four skill slots on an arc
//            (the HUD's SkillRow, cast through TouchControl) and the page / bar toggle. Bottom: four belt cells + the
//            context button (Pick up / Talk).
// Also (2026-10-17 round 3): a second map finger pinches the camera zoom; a skill dragged off its slot aims (castAt);
// a sideways swipe on the target frame cycles targets; quick chat lines in the chat sheet; haptics (MobileHaptics);
// the screen wake lock; a first-run gesture card; holding Menu edits the pad (move / size / opacity, MobileLayout).
// Round 4 (2026-10-18): double-tap Attack = sticky auto-attack (TouchControl.autoAttack); a dim suggested target with no
// target (suggestedId); the hidden skill page's ready dots on the page button; a ring filling toward the Attack lock; long
// presses cancel past TAP_PX; MobileTextSize; a LOW HP label; safe-area insets on every edge; prefers-contrast: more;
// the pad scales down to 0.85 under 700 CSS px wide and the belt keeps clear of the skill arc (--bw).
// All input goes through game.ts's TouchControl (GameUiContext.touch): no synthetic mouse or key events, no key listener.

import { ChatType, Stat } from "../../../protocol/enums.generated.ts";
import { CHAT_LINK } from "../../../game/chatLog.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { asButton, h, injectCss, rafCoalesce, restartAnim, setStyle, setText } from "../core/dom.ts";
import { svg, svgNode, type IconName } from "../core/icons.ts";
import { itemCells, type ItemCell } from "../core/itemCells.ts";
import { toLocal } from "../core/layers.ts";
import type { UiRuntime } from "../core/runtime.ts";
import type { ChatHud } from "../hud/chat.ts";
import { WINDOW_KEYS } from "../hud/gameMenu.ts";
import type { Hud } from "../hud/hud.ts";
import { SkillRow, type SkillView } from "../hud/skillRow.ts";
import { gameSettings, onSettingChange, setSetting } from "../../../game/settings.ts";

const CSS = `
.hudm.mobile > .rig { display: none; }
.hudm.mobile .qtrack { display: none; }
.hudm.mobile .mmap { transform: scale(.85); transform-origin: top right; }
/* safe areas: the root is in game pixels (scaled by the fit, --mfit), env() is in CSS pixels */
.hudm.mobile .mmap { translate: calc(env(safe-area-inset-right) / var(--mfit, 1) * -1) calc(env(safe-area-inset-top) / var(--mfit, 1)); }
/* "You gained ..." output lines: right side, under the minimap, clear of the HP / MP box */
.hudm.mobile .houts { left: auto; right: 21%; top: 140px; align-items: flex-end; text-align: right; }
.hudm.mobile.mleft .houts { right: auto; left: 21%; align-items: flex-start; text-align: left; }
/* the hero frame 10 px higher: a phone's punch-hole camera sat over the portrait's level badge */
.hudm.mobile .hframe { top: calc(41% - 10px); }
/* buffs (the HUD's own buff / poison rows, moved here) under the HP / MP box */
.hudm .mpad .mbuffs { left: calc(10px + env(safe-area-inset-left)); top: calc(10px + 80px * var(--mt) + env(safe-area-inset-top)); display: flex; flex-direction: column; gap: 4px; pointer-events: none; }
.hudm .mpad .mbuffs .hm-buffs, .hudm .mpad .mbuffs .hm-debuffs { position: static; flex-wrap: wrap; flex-direction: row; max-width: 206px; }
.hudm .mpad .mbuffs .hm-buff { pointer-events: auto; }
.hudm .msurf { position: absolute; inset: 0; pointer-events: auto; touch-action: none; }
.hudm .mpad { position: absolute; left: 0; top: 0; transform-origin: 0 0; pointer-events: none; touch-action: none; --mt: 1;
  font: 600 calc(13px * var(--mt))/1.2 system-ui, sans-serif; color: var(--tx); -webkit-user-select: none; user-select: none; }
.hudm .mpad > * { position: absolute; pointer-events: auto; }
.hudm .mpad .mbtn { display: flex; align-items: center; justify-content: center; box-sizing: border-box; border-radius: 50%;
  background: color-mix(in srgb, var(--pa) 72%, transparent); border: 1.5px solid color-mix(in srgb, var(--fr) 75%, transparent);
  color: var(--tx); box-shadow: 0 2px 10px rgba(0,0,0,.45); transition: transform .08s, opacity .15s; }
.hudm .mpad .mbtn:active, .hudm .mpad .mbtn.down { transform: scale(.92); background: color-mix(in srgb, var(--ac) 40%, var(--pa)); }
.hudm .mpad .mbtn .ic { width: 55%; height: 55%; }
/* Settings MobileTextSize: the pad's text factor */
.hudm .mpad.mts-s { --mt: .9; } .hudm .mpad.mts-l { --mt: 1.25; }
/* top bar */
.hudm .mpad .mstat { left: calc(10px + env(safe-area-inset-left)); top: calc(8px + env(safe-area-inset-top)); width: 190px; padding: 6px 8px; border-radius: 10px;
  background: color-mix(in srgb, var(--pa) 70%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .mpad .mstat .lv { display: flex; justify-content: space-between; align-items: center; gap: 4px; font-size: calc(13px * var(--mt)); margin-bottom: 4px; }
/* low HP as text too (iOS has no vibration; colour alone is not enough) */
.hudm .mpad .mstat .lowtx { display: none; padding: 0 5px; border-radius: 6px; background: #c41e1e; color: #fff; font-size: calc(11px * var(--mt)); font-weight: 800; letter-spacing: .03em; }
.hudm .mpad .mstat.low .lowtx { display: inline-block; }
.hudm .mpad .mbar { position: relative; height: calc(14px * var(--mt)); border-radius: 6px; overflow: hidden; background: rgba(0,0,0,.55); margin-top: 3px; }
.hudm .mpad .mbar > i { position: absolute; inset: 0; transform-origin: 0 50%; }
.hudm .mpad .mbar.hp > i { background: linear-gradient(#e2504a, #a3221d); }
.hudm .mpad .mbar.mp > i { background: linear-gradient(#4f8ee8, #24509e); }
.hudm .mpad .mbar.xp { height: 4px; } .hudm .mpad .mbar.xp > i { background: #e7c35a; }
.hudm .mpad .mbar > b { position: absolute; inset: 0; font-size: calc(11px * var(--mt)); line-height: calc(14px * var(--mt)); text-align: center; text-shadow: 0 1px 2px #000; font-weight: 600; }
.hudm .mpad .mtgt { left: 50%; top: calc(62px + env(safe-area-inset-top)); transform: translateX(-50%); display: flex; align-items: center; gap: 10px; min-width: 200px;
  padding: 5px 6px; border-radius: 10px; background: color-mix(in srgb, var(--pa) 70%, transparent);
  border: 1px solid color-mix(in srgb, var(--bad, #c44) 60%, transparent); }
.hudm .mpad .mtgt .nm { flex: 1; min-width: 0; }
.hudm .mpad .mtgt .nm > div:first-child { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .mpad .mtgt .mbtn { position: static; width: 44px; height: 44px; flex: none; }
/* no target: the suggested one (TouchControl.suggestedId), dim, without lock / clear */
.hudm .mpad .mtgt.sug { opacity: .6; border-style: dashed; padding-left: 12px; padding-right: 12px; }
.hudm .mpad .mtgt.sug .mbtn { display: none; }
.hudm .mpad .mtop { top: calc(8px + env(safe-area-inset-top)); display: flex; gap: 8px; }
.hudm .mpad .mtop .mbtn { position: static; width: 46px; height: 46px; }
/* bottom right */
.hudm .mpad .matk { width: 92px; height: 92px; right: calc(18px + env(safe-area-inset-right)); bottom: calc(18px + env(safe-area-inset-bottom)); }
.hudm .mpad .matk .ic { width: 50%; height: 50%; }
/* auto-attack on (double-tap): a gold ring and AUTO */
.hudm .mpad .matk.auto { border: 3px solid #f2cf5b; box-shadow: 0 0 0 3px rgba(242,207,91,.3), 0 0 18px rgba(242,207,91,.75); }
.hudm .mpad .matk .au { display: none; position: absolute; bottom: 9px; font-size: calc(11px * var(--mt)); font-weight: 800; color: #f2cf5b; text-shadow: 0 1px 2px #000; }
.hudm .mpad .matk.auto .au { display: block; }
/* holding Attack toward the target lock: a filling ring (700 ms) */
.hudm .mpad .matk .mring { position: absolute; left: -7px; top: -7px; width: calc(100% + 14px); height: calc(100% + 14px); pointer-events: none;
  transform: rotate(-90deg); opacity: 0; overflow: visible; }
.hudm .mpad .matk .mring circle { fill: none; stroke: #f2cf5b; stroke-width: 5; stroke-dasharray: 100; stroke-dashoffset: 100; }
.hudm .mpad .matk.hold .mring { opacity: 1; } .hudm .mpad .matk.hold .mring circle { animation: mlockfill .7s linear forwards; }
@keyframes mlockfill { to { stroke-dashoffset: 0; } }
.hudm .mpad .marc { right: calc(18px + env(safe-area-inset-right)); bottom: calc(18px + env(safe-area-inset-bottom)); width: 0; height: 0; pointer-events: none; }
.hudm .mpad .marc .hm-hrow { display: contents; }
.hudm .mpad .marc .hm-hrow[hidden] .hm-slot { display: none; }
.hudm .mpad .marc .hm-slot { position: absolute; width: 54px; height: 54px; pointer-events: auto; border-radius: 50%; overflow: hidden;
  box-shadow: 0 2px 8px rgba(0,0,0,.5); }
.hudm .mpad .marc .hm-slot .key { display: none; }
.hudm .mpad .marc .hm-slot:nth-child(1) { right: 106px; bottom: 10px; }
.hudm .mpad .marc .hm-slot:nth-child(2) { right: 96px; bottom: 74px; }
.hudm .mpad .marc .hm-slot:nth-child(3) { right: 52px; bottom: 118px; }
.hudm .mpad .marc .hm-slot:nth-child(4) { right: -8px; bottom: 128px; }
.hudm .mpad .mpage { width: 44px; height: 44px; right: calc(174px + env(safe-area-inset-right)); bottom: calc(108px + env(safe-area-inset-bottom)); font-size: calc(12px * var(--mt)); }
.hudm .mpad .mpage .pl { margin-top: -6px; }
/* the hidden page's four slots: ready (green), cooldown (grey), no MP (blue), empty (none) */
.hudm .mpad .mpage .pd { position: absolute; left: 0; right: 0; bottom: 7px; display: flex; justify-content: center; gap: 3px; pointer-events: none; }
.hudm .mpad .mpage .pd > i { width: 6px; height: 6px; border-radius: 50%; background: #7fdc6a; box-shadow: 0 0 3px rgba(0,0,0,.8); }
.hudm .mpad .mpage .pd > i.cooldown { background: rgba(255,255,255,.28); } .hudm .mpad .mpage .pd > i.nomp { background: #4f8ee8; }
.hudm .mpad .mpage .pd > i.empty { visibility: hidden; }
/* bottom: belt + pick up; centred, but kept left of the page button / skill arc (226 px; --bw = its width, layout()) */
.hudm .mpad .mbelt { left: min(50%, calc(100% - 226px - env(safe-area-inset-right) - var(--bw, 0px) / 2)); bottom: calc(10px + env(safe-area-inset-bottom)); transform: translateX(-50%); display: flex; gap: 8px; align-items: center;
  padding: 6px; border-radius: 12px; background: color-mix(in srgb, var(--pa) 55%, transparent); }
.hudm .mpad .mbelt .mbtn { position: static; width: 48px; height: 48px; border-radius: 10px; }
.hudm .mpad .mbelt .mbtn.lit { border-color: var(--gold, #e7c35a); box-shadow: 0 0 12px color-mix(in srgb, var(--gold, #e7c35a) 70%, transparent); }
.hudm .mpad .mbelt .mbtn:not(.lit) { opacity: .55; }
/* joystick */
.hudm .mpad .mstick { width: 140px; height: 140px; margin: -70px 0 0 -70px; border-radius: 50%; pointer-events: none;
  background: radial-gradient(circle, rgba(255,255,255,.06), rgba(255,255,255,.14)); border: 2px solid rgba(255,255,255,.28); }
.hudm .mpad .mstick > i { position: absolute; left: 40px; top: 40px; width: 60px; height: 60px; border-radius: 50%;
  background: color-mix(in srgb, var(--ac) 55%, rgba(255,255,255,.35)); box-shadow: 0 2px 10px rgba(0,0,0,.5); }
.hudm .mpad .mstick.run { border-color: color-mix(in srgb, var(--ac) 80%, transparent); }
/* chat: a faded log bottom left (touches pass through to the joystick); the Chat button opens it with the input at the
   top, clear of the on-screen keyboard */
.hudm.mobile .hchat { left: calc(8px + env(safe-area-inset-left) / var(--mfit, 1)) !important; top: auto !important; bottom: calc(8px + env(safe-area-inset-bottom) / var(--mfit, 1)) !important;
  width: 28% !important; height: 17% !important; pointer-events: none; opacity: .85; }
.hudm.mobile .hchat .cin, .hudm.mobile .hchat .ctop, .hudm.mobile .hchat .grip { display: none; }
.hudm.mobile.mchat .hchat { height: 36% !important; width: 58% !important; top: calc(4px + env(safe-area-inset-top) / var(--mfit, 1)) !important; bottom: auto !important;
  left: 21% !important; right: auto !important; z-index: 45; pointer-events: auto; opacity: 1; }
.hudm.mobile.mchat .hchat .ctop { order: -2; }
.hudm.mobile.mchat .hchat .cin { order: -1; border-top: 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
/* the tap marker: where a map finger landed, coloured by what it hit */
.hudm .mpad .mtap { width: 60px; height: 60px; margin: -30px 0 0 -30px; border-radius: 50%; pointer-events: none; opacity: 0; box-sizing: border-box;
  border: 3px solid var(--mc, #fff); box-shadow: 0 0 12px var(--mc, #fff), inset 0 0 8px var(--mc, #fff); }
.hudm .mpad .mtap.go { animation: mtap .5s ease-out; }
.hudm .mpad .mtap.ground { --mc: color-mix(in srgb, var(--ac) 70%, #fff); } .hudm .mpad .mtap.self, .hudm .mpad .mtap.item { --mc: #f2cf5b; }
.hudm .mpad .mtap.monster, .hudm .mpad .mtap.player { --mc: #ff5a4a; } .hudm .mpad .mtap.npc { --mc: #5ab4ff; }
@keyframes mtap { from { transform: scale(.35); opacity: .95; } to { transform: scale(1.25); opacity: 0; } }
/* low HP: a pulsing red edge + the HP bar beating (opacity / transform only) */
.hudm .mpad .mlow { inset: 0; pointer-events: none; opacity: 0; box-shadow: inset 0 0 90px 24px rgba(220,20,20,.6); transition: opacity .3s; }
.hudm .mpad .mlow.on { animation: mlow 1.1s ease-in-out infinite; }
@keyframes mlow { 0%, 100% { opacity: .25; } 50% { opacity: .85; } }
.hudm .mpad .mstat.low .mbar.hp { animation: mbeat 1.1s ease-in-out infinite; }
@keyframes mbeat { 0%, 100% { transform: scale(1); } 15% { transform: scale(1.05, 1.25); } }
@media (prefers-reduced-motion: reduce) { .hudm .mpad .mlow.on { animation: none; opacity: .6; } .hudm .mpad .mstat.low .mbar.hp { animation: none; } }
/* target lock + the extra belt buttons */
.hudm .mpad .mtgt.locked { border-color: #f2cf5b; box-shadow: 0 0 12px rgba(242,207,91,.5); }
.hudm .mpad .mtgt .mbtn.lk.on, .hudm .mpad .mbelt .mbtn.on { border-color: #f2cf5b; color: #f2cf5b; opacity: 1; }
.hudm .mpad .mbelt .icell.mhero { position: relative; }
.hudm .mpad .mbelt .icell.mhero::before { content: "H"; position: absolute; right: 3px; top: 1px; z-index: 2; font-size: 12px; font-weight: 800; color: #f2cf5b; text-shadow: 0 1px 2px #000; }
.hudm .mpad .mbelt .msep { width: 1px; align-self: stretch; background: color-mix(in srgb, var(--fr) 60%, transparent); }
/* left-handed: the attack pad on the left (the joystick zone moves right in code) */
.hudm .mpad.mleft .matk { right: auto; left: calc(18px + env(safe-area-inset-left)); }
.hudm .mpad.mleft .marc { right: auto; left: calc(18px + env(safe-area-inset-left)); transform: scaleX(-1); }
.hudm .mpad.mleft .marc .hm-slot { transform: scaleX(-1); }
.hudm .mpad.mleft .mpage { right: auto; left: calc(174px + env(safe-area-inset-left)); }
.hudm .mpad.mleft .mbelt { left: max(50%, calc(226px + env(safe-area-inset-left) + var(--bw, 0px) / 2)); }
.hudm.mobile.mleft .hchat { left: auto !important; right: calc(8px + env(safe-area-inset-right) / var(--mfit, 1)) !important; }
.hudm.mobile.mleft.mchat .hchat { right: auto !important; left: 21% !important; }
/* windows: item cells let a vertical swipe scroll the sheet (tap / long press / sideways drag still work) */
.hudm.mobile .hwin .icell { touch-action: pan-y; }
.hudm.mobile.mchat .hchat .cin, .hudm.mobile.mchat .hchat .ctop { display: flex; opacity: 1; }
.hudm.mobile.mchat .hchat .cin input { font-size: 16px; }
/* quick chat: canned lines under the input while the chat sheet is open */
.hudm.mobile .hchat .mquick { display: none; }
.hudm.mobile.mchat .hchat .mquick { display: flex; order: -1; flex-wrap: nowrap; gap: 6px; padding: 6px; overflow-x: auto; scrollbar-width: none; touch-action: pan-x; }
.hudm.mobile .hchat .mquick > div { flex: none; white-space: nowrap; }
.hudm.mobile .hchat .mquick > div { display: flex; align-items: center; box-sizing: border-box; min-height: calc(44px / var(--mfit, 1)); padding: 0 calc(14px / var(--mfit, 1));
  border-radius: calc(22px / var(--mfit, 1)); font-size: calc(14px / var(--mfit, 1)); background: color-mix(in srgb, var(--pa) 80%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); color: var(--tx); }
.hudm.mobile .hchat .mquick > div:active { background: color-mix(in srgb, var(--ac) 40%, var(--pa)); }
/* the open chat: solid, every line shown (no fade), readable, scrollable and selectable */
.hudm.mobile.mchat .hchat { --cfs: calc(14px / var(--mfit, 1)) !important; background: color-mix(in srgb, var(--pb) 94%, transparent) !important;
  border-color: color-mix(in srgb, var(--fr) 70%, transparent) !important; }
.hudm.mobile.mchat .hchat .ln { animation: none !important; opacity: 1 !important; }
.hudm.mobile.mchat .hchat .log { user-select: text; -webkit-user-select: text; touch-action: pan-y; overscroll-behavior: contain; mask-image: none; }
/* the reading view (a tap on the faded log): centred and tall, no keyboard; a tap on the input moves it to the top */
.hudm.mobile.mchat.mread .hchat { left: 15% !important; right: auto !important; width: 70% !important; height: 82% !important; bottom: auto !important;
  top: calc(6% + env(safe-area-inset-top) / var(--mfit, 1)) !important; }
.hudm.mobile .mchatbg { position: absolute; inset: 0; z-index: 44; display: none; pointer-events: auto; background: rgba(0,0,0,.45); }
.hudm.mobile.mchat.mread .mchatbg { display: block; }
.hudm.mobile.mchat.mread .hchat .mquick { display: none; } /* reading: the room goes to the log (they show while typing) */
.hudm.mobile .hchat .mclose { display: none; }
.hudm.mobile.mchat .hchat .mclose { display: flex; flex: none; align-items: center; justify-content: center; width: calc(40px / var(--mfit, 1)); height: calc(32px / var(--mfit, 1));
  font-size: calc(20px / var(--mfit, 1)); line-height: 1; color: var(--tx); cursor: pointer; border-radius: 6px; }
.hudm.mobile.mchat .hchat .mclose:active { background: color-mix(in srgb, var(--ac) 35%, transparent); }
/* whisper toast: the sender and the text at the top for a few seconds; a tap answers */
.hudm.mobile .mwhisper { position: absolute; z-index: 46; left: 50%; top: calc(8px + env(safe-area-inset-top) / var(--mfit, 1)); translate: -50% -140%;
  width: min(62%, calc(560px / var(--mfit, 1))); padding: calc(8px / var(--mfit, 1)) calc(12px / var(--mfit, 1)); pointer-events: auto; cursor: pointer;
  border-radius: calc(12px / var(--mfit, 1)); border: 1px solid color-mix(in srgb, #d878ff 70%, transparent); color: var(--tx);
  background: linear-gradient(color-mix(in srgb, var(--pb) 95%, transparent), color-mix(in srgb, var(--pb) 95%, transparent));
  box-shadow: 0 6px 24px rgba(0,0,0,.6); font-size: calc(14px / var(--mfit, 1)); transition: translate .25s ease; }
.hudm.mobile .mwhisper.on { translate: -50% 0; }
.hudm.mobile .mwhisper b { color: #e9a6ff; }
.hudm.mobile .mwhisper p { margin: 2px 0 0; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow-wrap: anywhere; }
.hudm.mobile .mwhisper small { display: block; margin-top: 3px; font-size: .8em; color: var(--dim); }
/* target frame: swipe sideways for the next / previous target */
.hudm .mpad .mtgt { touch-action: none; }
.hudm .mpad .mtgt .sw { font-size: calc(11px * var(--mt)); opacity: .65; }
.hudm .mpad .mtgt.swl { animation: mswl .2s ease-out; } .hudm .mpad .mtgt.swr { animation: mswr .2s ease-out; }
@keyframes mswl { from { translate: -14px 0; } to { translate: 0 0; } } @keyframes mswr { from { translate: 14px 0; } to { translate: 0 0; } }
/* the context button: pick up / talk */
.hudm .mpad .mbelt .mbtn.ctx.talk { border-color: #5ab4ff; box-shadow: 0 0 12px rgba(90,180,255,.6); opacity: 1; }
/* layout editor (long press Menu) */
.hudm .mpad .medbg { inset: 0; display: none; background: rgba(0,0,0,.45); }
.hudm .mpad.medit .medbg { display: block; }
.hudm .mpad.medit [data-mg] { outline: 2px dashed color-mix(in srgb, var(--ac) 80%, transparent); outline-offset: 4px; }
.hudm .mpad.medit [data-mg].sel { outline-color: #f2cf5b; outline-style: solid; }
.hudm .mpad.medit .marc[data-mg] { outline: none; }
.hudm .mpad.medit .marc .hm-slot { outline: 2px dashed color-mix(in srgb, var(--ac) 80%, transparent); }
.hudm .mpad .medp { left: 50%; top: 50%; transform: translate(-50%, -50%); display: none; flex-direction: column; gap: 10px; width: 280px; padding: 14px;
  border-radius: 14px; background: color-mix(in srgb, var(--pa) 92%, transparent); border: 1px solid var(--fr); box-shadow: 0 6px 30px rgba(0,0,0,.6); }
.hudm .mpad.medit .medp { display: flex; }
.hudm .mpad .medp .hd { font-size: 15px; color: var(--ac); }
.hudm .mpad .medp .ht { font-size: 12px; opacity: .75; font-weight: 400; }
.hudm .mpad .medp label { display: flex; align-items: center; gap: 10px; font-size: 13px; }
.hudm .mpad .medp label > span { width: 64px; } .hudm .mpad .medp input[type=range] { flex: 1; height: 28px; accent-color: var(--ac); }
.hudm .mpad .medp .row { display: flex; gap: 10px; justify-content: flex-end; }
.hudm .mpad .medp .row > div { padding: 10px 16px; border-radius: 10px; border: 1px solid var(--fr); background: var(--pb, rgba(255,255,255,.06)); }
.hudm .mpad .medp .row > div.pri { border-color: var(--ac); color: var(--ac); }
/* first-run gesture tips */
.hudm .mpad .mtips { inset: 0; display: flex; align-items: center; justify-content: center; background: rgba(0,0,0,.55); z-index: 5; }
.hudm .mpad .mtips > div { max-width: 460px; margin: 16px; padding: 16px 18px; border-radius: 14px; background: color-mix(in srgb, var(--pa) 94%, transparent);
  border: 1px solid var(--fr); font-weight: 400; font-size: 14px; line-height: 1.45; }
.hudm .mpad .mtips b { color: var(--ac); } .hudm .mpad .mtips ul { margin: 8px 0; padding-left: 18px; } .hudm .mpad .mtips .ok { margin-top: 10px; text-align: right; color: var(--ac); font-weight: 600; }
/* bigger NPC links and options for fingers; sheets close with a swipe down on their title */
.hudm.mobile .npwin .say a { display: inline-block; padding: 6px 4px; margin: 1px 0; }
.hudm.mobile .npwin .op { min-height: 56px; padding: 12px 12px; font-size: 15px; }
.hudm.mobile .npwin .svc button { padding: 9px 14px; font-size: 13px; }
/* the safe-area probe (layout(): the insets in pad pixels) */
.hudm .mpad .mins { left: 0; top: 0; width: 0; height: 0; visibility: hidden; pointer-events: none;
  padding: env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom) env(safe-area-inset-left); }
/* prefers-contrast: more: solid backgrounds, light outlines, no faded controls */
@media (prefers-contrast: more) {
  .hudm .mpad .mbtn { background: color-mix(in srgb, var(--pa) 92%, #000); border: 2px solid var(--tx); box-shadow: 0 0 0 1px #000, 0 2px 10px rgba(0,0,0,.6); }
  .hudm .mpad .mstat, .hudm .mpad .mtgt, .hudm .mpad .mbelt { background: color-mix(in srgb, var(--pa) 94%, #000); border: 2px solid var(--tx); }
  .hudm .mpad .mbelt .mbtn:not(.lit) { opacity: .85; }
  .hudm .mpad .mbar { outline: 1px solid rgba(255,255,255,.6); }
  .hudm .mpad .mbar > b, .hudm .mpad .matk .au { text-shadow: 0 0 2px #000, 0 0 2px #000, 0 1px 3px #000; }
  .hudm .mpad .mtgt .sw, .hudm .mpad .mtgt.sug { opacity: .9; }
  .hudm .mpad .mpage .pd > i.cooldown { background: rgba(255,255,255,.5); }
  .hudm.mobile .hchat { opacity: 1; background: rgba(0,0,0,.6); }
}
`;

/** The joystick's 8-way direction (MapControl.MouseDirection's 22.5 degree split, 0 = up, clockwise) with hysteresis:
 *  the previous direction is kept within 8 degrees past its edge. -1 inside the dead zone. */
export function stickDir(dx: number, dy: number, r: number, prev: number): { dir: number; run: boolean } {
  const d = Math.hypot(dx, dy);
  if (d < r * 0.25) return { dir: -1, run: false };
  let angle = (Math.atan2(dx, -dy) * 180) / Math.PI;
  if (angle < 0) angle += 360;
  let dir = Math.trunc(((angle + 22.5) % 360) / 45) & 7;
  if (prev >= 0 && dir !== prev) {
    let off = Math.abs(angle - prev * 45) % 360;
    if (off > 180) off = 360 - off;
    if (off <= 22.5 + 8) dir = prev;
  }
  return { dir, run: d >= r * 0.7 };
}

const STICK_R = 70;
const TAP_MS = 300, TAP_PX = 12;
/** A skill finger this far (CSS px) off its slot aims instead of tapping. */
const AIM_PX = 28;
/** A sideways swipe this long (CSS px) on the target frame cycles the target. */
const SWIPE_PX = 36;
/** Quick chat lines (Normal chat). */
const QUICK_LINES = ["Help!", "Thanks!", "Group?", "Wait", "Ready", "Follow me", "Heal please", "gg"];
const TIPS_KEY = "crystal.mobileTips";

interface Finger { kind: "stick" | "map" | "pinch" | "dead"; x0: number; y0: number; t0: number; moved: boolean; dir: number; x: number; y: number }

/** The pad's movable groups (layout editor). */
const GROUPS = ["stat", "tgt", "top", "action", "belt"] as const;
type Group = (typeof GROUPS)[number];
/** Settings MobileLayout: per group an offset (pad px) and a scale, plus the controls' opacity. */
interface PadLayout { o: number; g: Partial<Record<Group, { x: number; y: number; s: number }>> }

function readLayout(): PadLayout {
  try {
    const v = JSON.parse(gameSettings.MobileLayout || "{}") as Partial<PadLayout>;
    const out: PadLayout = { o: typeof v.o === "number" && v.o >= 0.3 && v.o <= 1 ? v.o : 1, g: {} };
    for (const k of GROUPS) {
      const e = v.g?.[k];
      if (e && [e.x, e.y, e.s].every(Number.isFinite)) out.g[k] = { x: Math.max(-2000, Math.min(2000, e.x)), y: Math.max(-2000, Math.min(2000, e.y)), s: Math.max(0.8, Math.min(1.4, e.s)) };
    }
    return out;
  } catch { return { o: 1, g: {} }; }
}

/** Settings MobileHaptics: a vibration (Android; iOS has no Vibration API). */
function buzz(p: number | number[]): void {
  if (gameSettings.MobileHaptics) try { navigator.vibrate?.(p); } catch { /* not allowed */ }
}

export interface MobileUiHost {
  readonly rt: UiRuntime;
  readonly ctx: GameUiContext;
  readonly hud: Hud;
  readonly chat: ChatHud;
  /** DragController.mapClick: a picked item / gold drops on the map (true = the press is used). */
  mapClick(): boolean;
  openWindow(key: string, show?: boolean): boolean;
}

export class MobileUi {
  private readonly rt: UiRuntime;
  private readonly ctx: GameUiContext;
  private readonly surface = h("div", "msurf");
  private readonly pad = h("div", "mpad");
  private readonly stick = h("div", "mstick");
  private readonly knob = h("i");
  private readonly fingers = new Map<number, Finger>();
  /** Two tap markers, alternated so quick taps each show. */
  private readonly taps = [h("div", "mtap"), h("div", "mtap")];
  private tapNext = 0;
  private readonly skills: SkillRow;
  private readonly belt: ItemCell[] = [];
  private readonly hpFill = h("i"); private readonly hpText = h("b");
  private readonly mpFill = h("i"); private readonly mpText = h("b");
  private readonly xpFill = h("i");
  private readonly lvText = h("span"); private readonly nameText = h("span");
  private readonly tgt = h("div", "mtgt");
  private readonly tgtName = h("div"); private readonly tgtFill = h("i");
  private readonly top = h("div", "mtop");
  private readonly pickBtn: HTMLElement;
  private readonly potBtn: HTMLElement;
  private readonly lockBtn: HTMLElement;
  private readonly heroCells: HTMLElement[] = [];
  private readonly low = h("div", "mlow");
  private readonly lowTx = h("span", "lowtx", "\u26A0\uFE0E LOW HP");
  private stat!: HTMLElement;
  private readonly atk: HTMLElement;
  /** Clears the held-button timers (Attack lock, page hold, Menu long press): resetInput(). */
  private readonly holdResets: (() => void)[] = [];
  private readonly beltRow = h("div", "mbelt");
  /** The safe-area insets in pad pixels [top, right, bottom, left] (layout(), from the .mins probe). */
  private readonly inset = [0, 0, 0, 0];
  private readonly insetProbe = h("div", "mins");
  /** The pad's size in pad pixels (layout()). */
  private padW = 0; private padH = 0;
  private left = false;
  /** Pad scale over CSS px: 1 on phones, up to 1.5 on tablets. */
  private ps = 1;
  private readonly pageBtn: HTMLElement;
  private readonly pageLbl = h("span", "pl");
  /** The hidden page's four slot states on the page button (frame()). */
  private readonly pageDots = [h("i"), h("i"), h("i"), h("i")];
  private readonly dotShown = ["", "", "", ""];
  private page = 0; // 0 = slots 1-4, 1 = 5-8 of the current view
  private view: SkillView = 0;
  private shown = { hp: -1, hpMax: -1, mp: -1, mpMax: -1, xp: -1, lv: -1, tgt: -1, tgtHp: -2, hero: true, locked: false, sug: false, auto: false };
  private fit = 1;
  private disposed = false;
  /** The context button's current mode (Pick up / Talk). */
  private ctxMode: "pick" | "talk" | null = null;
  private readonly ctxIcon = { pick: svg("pouch"), talk: svg("talk") };
  /** Haptics: the last HP / level seen and when a hit last buzzed. */
  private hapHp = -1; private hapLv = -1; private hapLow = false; private hapAt = 0;
  /** The pinch: its two fingers, the start distance and zoom. */
  private pinch: { a: number; b: number; d0: number; z0: number } | null = null;
  private zoomRestored = false;
  /** Screen wake lock while in game (released by the browser when the page hides; taken again when it shows). */
  private wake: { release(): Promise<void> } | null = null;
  // — layout editor —
  private readonly groups = new Map<Group, HTMLElement[]>();
  private layoutCfg: PadLayout = readLayout();
  private editSel: Group | null = null;
  private readonly editBg = h("div", "medbg");
  private readonly editPanel = h("div", "medp");
  private editSize!: HTMLInputElement;

  constructor(private readonly host: MobileUiHost) {
    const { rt, ctx } = host;
    this.rt = rt; this.ctx = ctx;
    injectCss("mobile-ui", CSS);
    const sig = rt.signal;
    const relayout = this.relayout = rafCoalesce(() => this.layout(), sig);
    rt.layers.rig.hidden = true;
    const root = rt.root;
    root.insertBefore(this.surface, root.firstChild);
    rt.layers.rig.after(this.pad);

    // — Top bar —
    this.lowTx.setAttribute("role", "status");
    const stat = asButton(h("div", "mstat", h("div", "lv", this.nameText, this.lowTx, this.lvText),
      h("div", "mbar hp", this.hpFill, this.hpText), h("div", "mbar mp", this.mpFill, this.mpText), h("div", "mbar xp", this.xpFill)), "Character");
    stat.addEventListener("click", () => host.openWindow(WINDOW_KEYS.character));
    const clear = this.button("cross", "Clear target", () => ctx.touch.clearTarget());
    this.lockBtn = this.button("lock", "Lock target (also: hold Attack)", () => this.toggleLock(), "lk");
    this.tgt.append(this.lockBtn, h("div", "nm", this.tgtName, h("div", "mbar hp", this.tgtFill)), clear); // lock and clear apart
    this.tgt.hidden = true;
    this.top.append(
      this.button("chat", "Chat", () => this.toggleChat()),
      this.button("map", "Map", () => host.openWindow(WINDOW_KEYS.bigMap)),
      this.button("bag", "Bag", () => host.openWindow(WINDOW_KEYS.inventory)),
      this.menuBtn = this.button("menu", "Menu (hold: edit the touch controls)", () => host.hud.gameMenu.toggle()),
    );

    // — Action pad: held attack, the skill arc (the HUD's SkillRow), the page / bar toggle —
    const atk = this.atk = this.button("sword", "Attack (double-tap: auto-attack, hold: lock the target)", () => { /* held: pointer handlers below */ }, "matk");
    const ring = svgNode("svg", { class: "mring", viewBox: "0 0 100 100", "aria-hidden": "true" });
    ring.append(svgNode("circle", { cx: 50, cy: 50, r: 47, pathLength: 100 }));
    atk.append(ring, h("span", "au", "AUTO"));
    let lockHold = 0, downX = 0, downY = 0, downAt = 0, tapUp = -1e9, stopped = false;
    const stopHold = () => { if (lockHold) { clearTimeout(lockHold); lockHold = 0; } atk.classList.remove("hold"); };
    const release = () => { stopHold(); atk.classList.remove("down"); ctx.touch.setAttackHeld(false); };
    atk.addEventListener("pointerdown", (e) => {
      e.preventDefault();
      try { atk.setPointerCapture(e.pointerId); } catch { /* synthetic pointer */ }
      atk.classList.add("down");
      ctx.touch.setAttackHeld(true);
      // a tap while auto-attack runs stops it (and is no first tap); a second tap within TAP_MS of a tap starts it
      stopped = ctx.touch.autoAttack;
      if (stopped) this.setAuto(false);
      else if (e.timeStamp - tapUp < TAP_MS) this.setAuto(true);
      downX = e.clientX; downY = e.clientY; downAt = e.timeStamp;
      // held 700 ms (a ring fills): lock the target it is fighting (bosses, PvP), so taps elsewhere keep it
      stopHold();
      if (!ctx.touch.locked && ctx.touch.targetId) {
        atk.classList.add("hold");
        lockHold = window.setTimeout(() => { lockHold = 0; atk.classList.remove("hold"); if (!ctx.touch.locked && ctx.touch.targetId) this.toggleLock(); }, 700);
      }
    }, { signal: sig });
    // the lock needs a still finger: one sliding off Attack only attacks
    atk.addEventListener("pointermove", (e) => { if (lockHold && Math.hypot(e.clientX - downX, e.clientY - downY) > TAP_PX) stopHold(); }, { signal: sig });
    sig.addEventListener("abort", () => { if (lockHold) clearTimeout(lockHold); }, { once: true });
    this.holdResets.push(() => { stopHold(); atk.classList.remove("down"); });
    atk.addEventListener("pointerup", (e) => {
      const tap = !stopped && !ctx.touch.autoAttack && e.timeStamp - downAt < TAP_MS && Math.hypot(e.clientX - downX, e.clientY - downY) <= TAP_PX;
      tapUp = tap ? e.timeStamp : -1e9;
      release();
    }, { signal: sig });
    atk.addEventListener("pointercancel", release, { signal: sig });
    this.skills = new SkillRow(ctx, rt);
    this.skills.onCast = (key) => ctx.touch.cast(key);
    const arc = h("div", "marc", this.skills.left, this.skills.right);
    this.pageBtn = this.button("", "Next skills (hold: bar 2 / hero)", () => this.setPage(this.page ^ 1), "mpage");
    this.pageBtn.append(this.pageLbl, h("div", "pd", ...this.pageDots));
    let hold = 0;
    this.pageBtn.addEventListener("pointerdown", () => {
      hold = window.setTimeout(() => { hold = -1; this.setView(((this.view + 1) % 3) as SkillView); buzz(10); }, 450);
    }, { signal: sig });
    this.pageBtn.addEventListener("click", (e) => { if (hold === -1) { e.stopImmediatePropagation(); } hold = 0; }, { capture: true, signal: sig });
    const pageUp = () => { if (hold > 0) { clearTimeout(hold); hold = 0; } };
    this.pageBtn.addEventListener("pointerup", pageUp, { signal: sig });
    this.pageBtn.addEventListener("pointercancel", pageUp, { signal: sig });
    sig.addEventListener("abort", () => { if (hold > 0) clearTimeout(hold); }, { once: true });
    this.holdResets.push(pageUp);

    // — Belt: the first four bag belt cells (tap = use) + Pick up —
    const beltRow = this.beltRow;
    const service = itemCells(rt);
    for (let i = 0; i < 4; i++) {
      const c = service.cell("inv", i, { size: 48, readOnly: true });
      c.el.addEventListener("click", () => ctx.models.inventory.use(i, ctx.now()), { signal: sig });
      this.belt.push(c);
      beltRow.append(c.el);
    }
    for (let i = 0; i < 2; i++) { // the hero belt (9 / 0), shown while a hero is summoned
      const c = service.cell("heroInv", i, { size: 48, readOnly: true, cls: "mhero" });
      c.el.addEventListener("click", () => ctx.models.heroItems.use(i, ctx.now()), { signal: sig });
      this.belt.push(c);
      this.heroCells.push(c.el);
      beltRow.append(c.el);
    }
    // the context button: Pick up on an item, Talk next to an NPC (TouchControl.context), else Pick up
    this.pickBtn = this.button("", "Pick up", () => ctx.touch.runContext(), "ctx");
    this.pickBtn.append(this.ctxIcon.pick);
    this.potBtn = this.button("heartFull", "Auto potion on / off", () => setSetting("AutoPot", !gameSettings.AutoPot));
    beltRow.append(h("div", "msep"), this.potBtn, this.pickBtn);
    const syncPot = () => {
      this.potBtn.classList.toggle("on", gameSettings.AutoPot);
      this.potBtn.classList.toggle("lit", gameSettings.AutoPot);
      this.potBtn.title = `Auto potion: ${gameSettings.AutoPot ? `on (HP ${gameSettings.AutoPotHP}% / MP ${gameSettings.AutoPotMP}%)` : "off"}`;
    };
    syncPot();
    const offSet = onSettingChange((k) => {
      if (k === "AutoPot" || k === "AutoPotHP" || k === "AutoPotMP") syncPot();
      else if (k === "MobileLeftHanded") this.applyHand();
      else if (k === "MobileLayout" && !this.editing) { this.layoutCfg = readLayout(); this.applyLayout(); } // Options' reset
      else if (k === "MobileZoom" && !this.pinch) this.ctx.touch.zoom = gameSettings.MobileZoom;
      else if (k === "MobileTextSize") { this.applyTextSize(); relayout(); }
    });
    sig.addEventListener("abort", offSet, { once: true });

    this.stick.append(this.knob);
    this.stick.hidden = true;
    this.stat = stat;
    const buffs = h("div", "mbuffs", host.hud.buffRow, host.hud.poisonRow);
    this.pad.append(this.insetProbe, this.low, ...this.taps, this.editBg, stat, buffs, this.tgt, this.top, arc, atk, this.pageBtn, beltRow, this.stick, this.editPanel);
    this.groups.set("stat", [stat, buffs]).set("tgt", [this.tgt]).set("top", [this.top]).set("action", [atk, arc, this.pageBtn]).set("belt", [beltRow]);
    for (const [g, els] of this.groups) for (const el of els) el.dataset.mg = g;
    this.setPage(0);
    this.applyTextSize();
    this.applyHand();
    this.buildEditor();
    this.longPress(this.menuBtn, 600, () => this.setEditing(true));
    this.skillAim();
    this.targetSwipe();
    this.quickChat();
    this.wakeLock();
    this.tips();

    // Android back: closes the front sheet / chat / menu (else opens the game menu) instead of leaving the game.
    // One guard history entry is kept while in game.
    try { history.pushState({ crystalGuard: 1 }, ""); } catch { /* sandboxed */ }
    window.addEventListener("popstate", () => {
      this.back();
      try { history.pushState({ crystalGuard: 1 }, ""); } catch { /* sandboxed */ }
    }, { signal: sig });

    // the app backgrounded / the window lost focus: no finger, held button or auto-attack stays on
    const hidden = () => { if (document.visibilityState === "hidden") this.resetInput(); };
    const reset = () => this.resetInput();
    document.addEventListener("visibilitychange", hidden, { signal: sig });
    window.addEventListener("blur", reset, { signal: sig });
    window.addEventListener("pagehide", reset, { signal: sig });
    this.offLife = () => {
      document.removeEventListener("visibilitychange", hidden);
      window.removeEventListener("blur", reset);
      window.removeEventListener("pagehide", reset);
    };

    // — Surface fingers —
    const s = this.surface;
    s.addEventListener("pointerdown", (e) => this.down(e), { signal: sig });
    s.addEventListener("pointermove", (e) => this.move(e), { signal: sig });
    s.addEventListener("pointerup", (e) => this.up(e, false), { signal: sig });
    s.addEventListener("pointercancel", (e) => this.up(e, true), { signal: sig });
    s.addEventListener("contextmenu", (e) => e.preventDefault(), { signal: sig });
    this.mapMove = rafCoalesce((x: number, y: number) => ctx.touch.move(x, y), sig);

    // Chat: once focus leaves the input (send / tap outside) the sheet closes, or drops back to the reading view when it
    // was opened from the log or the finger is on the sheet (reading / selecting)
    host.chat.el.addEventListener("pointerdown", () => { this.chatTouchAt = performance.now(); }, { capture: true, signal: sig });
    host.chat.el.addEventListener("focusout", (e) => {
      if (!root.classList.contains("mchat")) return;
      const to = (e as FocusEvent).relatedTarget;
      if (to instanceof Node && host.chat.el.contains(to)) return;
      if (this.chatRead || performance.now() - this.chatTouchAt < 600) { root.classList.add("mread"); this.chatRead = true; host.chat.follow(); this.chatFollow(); }
      else this.closeChat();
    }, { signal: sig });
    host.chat.inputEl.addEventListener("focus", () => { if (root.classList.contains("mchat")) { root.classList.remove("mread"); this.chatFollow(); } }, { signal: sig });
    // the reading view: a dim backdrop that closes it, and a close button in the chat's top bar
    const bg = h("div", "mchatbg");
    bg.addEventListener("pointerdown", (e) => { e.preventDefault(); e.stopPropagation(); this.closeChat(); }, { signal: sig });
    host.chat.el.before(bg);
    this.chatBg = bg;
    const x = asButton(h("div", "mclose", "×"), "Close the chat");
    x.addEventListener("click", (e) => { e.stopPropagation(); this.closeChat(); }, { signal: sig });
    (host.chat.el.querySelector(".ctop") ?? host.chat.el).append(x);
    this.chatX = x;
    this.whisperToast();

    // — Layout: the pad in CSS pixels, the top buttons left of the minimap —
    window.addEventListener("resize", relayout, { signal: sig });
    window.visualViewport?.addEventListener("resize", relayout, { signal: sig });
    const offLayout = rt.layoutChanged.on(relayout);
    const offTick = rt.ticker.subscribe((now) => this.frame(now));
    const slow = rt.ticker.every(250, () => this.slow());
    slow.on();
    sig.addEventListener("abort", () => { offLayout(); offTick(); slow.off(); this.dispose(); }, { once: true });
    this.layout();
  }

  private readonly mapMove: (x: number, y: number) => void;
  private readonly relayout: () => void;
  private offLife: () => void = () => {};
  private menuBtn!: HTMLElement;

  private button(icon: IconName | "", label: string, fn: () => void, cls = ""): HTMLElement {
    const b = asButton(h("div", "mbtn" + (cls ? " " + cls : "")), label);
    b.title = label;
    if (icon) b.append(svg(icon));
    b.addEventListener("click", (e) => { e.stopPropagation(); fn(); }, { signal: this.rt.signal });
    return b;
  }

  private toggleLock(): void {
    const on = this.ctx.touch.lockTarget();
    if (on) buzz(15);
    this.lockBtn.classList.toggle("on", on);
    this.tgt.classList.toggle("locked", on);
  }

  /** Sticky auto-attack (double-tap Attack): TouchControl.autoAttack, shown as a gold ring + AUTO on the button. */
  private setAuto(on: boolean): void {
    this.ctx.touch.autoAttack = on;
    const now = this.ctx.touch.autoAttack; // false when nothing is in range
    if (now !== on) { buzz([20, 40, 20]); return; }
    buzz(on ? [15, 30, 15] : 10);
    this.shown.auto = now;
    this.atk.classList.toggle("auto", now);
  }

  /** Settings MobileTextSize: the pad's --mt factor. */
  private applyTextSize(): void {
    const t = gameSettings.MobileTextSize;
    this.pad.classList.toggle("mts-s", t === "Small");
    this.pad.classList.toggle("mts-l", t === "Large");
  }

  /** Settings MobileLeftHanded: mirror the pad and the joystick zone. */
  private applyHand(): void {
    this.left = gameSettings.MobileLeftHanded;
    this.pad.classList.toggle("mleft", this.left);
    this.rt.root.classList.toggle("mleft", this.left);
    this.applyLayout(); // the action cluster's scale anchor flips
  }

  /** The Android back button: the chat sheet, the game menu, the front window, else the game menu opens. */
  private back(): void {
    const root = this.rt.root;
    if (root.classList.contains("mchat")) { this.closeChat(); return; }
    if (this.editing) { this.setEditing(false); return; } // like Done: saves the layout
    const menu = this.host.hud.gameMenu;
    if (menu.open) { menu.close(); return; }
    if (this.rt.popups.open) return; // a question: answer it
    if (this.rt.wm.handleKey(new KeyboardEvent("keydown", { key: "Escape" }))) return;
    menu.show();
  }

  private toggleChat(): void {
    const root = this.rt.root;
    if (root.classList.contains("mchat")) { this.closeChat(); return; }
    this.chatRead = false;
    root.classList.add("mchat");
    root.classList.remove("mread");
    this.host.chat.focus(); // inside the tap: iOS only shows the keyboard for a gesture's focus()
    this.host.chat.follow();
    this.chatFollow();
  }

  /** The reading view: the whole chat centred, scrollable and selectable, without the keyboard (a tap on the faded log). */
  private openChatRead(): void {
    this.chatRead = true;
    this.rt.root.classList.add("mchat", "mread");
    this.host.chat.follow();
    this.chatFollow();
  }

  /** Closes the chat sheet / reading view; the faded log follows the newest line again. */
  private closeChat(): void {
    this.chatRead = false;
    this.rt.root.classList.remove("mchat", "mread");
    this.host.chat.blur();
    this.host.chat.follow();
    this.chatFollow();
  }

  /** True when a finger at (clientX, clientY) is on the faded chat log. */
  private onChatLog(clientX: number, clientY: number): boolean {
    if (this.rt.root.classList.contains("mchat")) return false;
    const r = this.host.chat.el.getBoundingClientRect();
    return r.width > 0 && clientX >= r.left && clientX <= r.right && clientY >= r.top && clientY <= r.bottom;
  }

  /** A received whisper: its sender and text slide in at the top for a few seconds; a tap opens the chat to answer. */
  private whisperToast(): void {
    const sig = this.rt.signal;
    const who = h("b"), text = h("p");
    const el = asButton(h("div", "mwhisper", who, text, h("small", "", "Tap to reply")), "Reply to the whisper");
    this.rt.root.append(el);
    this.whisperEl = el;
    let from = "", hide = 0;
    el.addEventListener("click", (e) => {
      e.stopPropagation();
      clearTimeout(hide);
      el.classList.remove("on");
      if (!from) return;
      this.chatRead = false;
      this.rt.root.classList.add("mchat");
      this.rt.root.classList.remove("mread");
      this.host.chat.prefill("/" + from); // the Whisper channel to the sender, input focused (inside the tap: iOS keyboard)
      this.host.chat.follow();
      this.chatFollow();
    }, { signal: sig });
    const off = this.ctx.models.chatLog.added.on((m) => {
      if (m.type !== ChatType.WhisperIn) return;
      const t = m.text.replace(CHAT_LINK, "$1");
      const w = /^([^\s=]+)=>\s?/.exec(t); // "Name=>text" (PlayerObject.Chat)
      if (!w || this.host.chat.isBlocked(w[1]!)) return;
      from = w[1]!;
      setText(who, from);
      setText(text, t.slice(w[0].length));
      el.classList.add("on");
      clearTimeout(hide);
      hide = window.setTimeout(() => el.classList.remove("on"), 4000);
    });
    sig.addEventListener("abort", () => { off(); clearTimeout(hide); });
  }

  private setPage(p: number): void {
    this.page = p;
    this.skills.left.hidden = p !== 0;
    this.skills.right.hidden = p !== 1;
    const bar = this.view === 2 ? "H" : this.view === 1 ? "2·" : "";
    setText(this.pageLbl, `${bar}${p === 0 ? "1-4" : "5-8"}`);
    this.dotShown.fill(""); // frame() redraws the other page's dots
  }

  private setView(v: SkillView): void {
    this.view = v;
    this.skills.setView(v);
    this.setPage(this.page);
  }

  // — Fingers —

  /** A finger in game pixels (the surface is unscaled inside the root). */
  private local(e: PointerEvent): [number, number] { return toLocal(this.surface, e.clientX, e.clientY); }
  /** A finger in pad (CSS) pixels. */
  private padLocal(e: PointerEvent): [number, number] { return toLocal(this.pad, e.clientX, e.clientY); }

  private down(e: PointerEvent): void {
    if (e.pointerType === "mouse" && e.button !== 0) return;
    e.preventDefault();
    try { this.surface.setPointerCapture(e.pointerId); } catch { /* synthetic pointer */ }
    const [x, y] = this.local(e);
    const [sw, sh] = this.ctx.screen;
    const stickBusy = [...this.fingers.values()].some((f) => f.kind === "stick");
    const mapBusy = [...this.fingers.values()].some((f) => f.kind === "map" || f.kind === "pinch" || f.kind === "dead");
    if (!stickBusy && (this.left ? x > sw * 0.6 : x < sw * 0.4) && y > sh * 0.25) {
      const [fx, fy] = this.padLocal(e);
      // the stick's centre stays out of the safe-area strips (a notch / the home indicator)
      const [, ir, ib, il] = this.inset;
      const px = Math.min(Math.max(fx, il), this.padW - ir), py = Math.min(fy, this.padH - ib);
      this.fingers.set(e.pointerId, { kind: "stick", x0: px, y0: py, t0: e.timeStamp, moved: false, dir: -1, x: e.clientX, y: e.clientY });
      setStyle(this.stick, "left", px + "px"); setStyle(this.stick, "top", py + "px");
      setStyle(this.knob, "transform", "");
      this.stick.classList.remove("run");
      this.stick.hidden = false;
      return;
    }
    if (mapBusy) { this.startPinch(e); return; }
    this.fingers.set(e.pointerId, { kind: "map", x0: e.clientX, y0: e.clientY, t0: e.timeStamp, moved: false, dir: -1, x: e.clientX, y: e.clientY });
    if (this.host.mapClick()) { this.fingers.delete(e.pointerId); return; }
    this.marker(e, this.ctx.touch.press(x, y));
  }

  /** The tap marker at a finger, coloured by what it hit. */
  private marker(e: PointerEvent, hit: string): void {
    const m = this.taps[this.tapNext]!;
    this.tapNext ^= 1;
    const [px, py] = this.padLocal(e);
    m.className = "mtap " + hit;
    setStyle(m, "left", px.toFixed(0) + "px"); setStyle(m, "top", py.toFixed(0) + "px");
    restartAnim(m, "go");
  }

  private move(e: PointerEvent): void {
    const f = this.fingers.get(e.pointerId);
    if (!f) return;
    f.x = e.clientX; f.y = e.clientY;
    if (f.kind === "dead") return;
    if (f.kind === "pinch") {
      const p = this.pinch, a = p && this.fingers.get(p.a), b = p && this.fingers.get(p.b);
      if (p && a && b) this.ctx.touch.zoom = p.z0 * (Math.hypot(a.x - b.x, a.y - b.y) / p.d0);
      return;
    }
    if (f.kind === "stick") {
      const [px, py] = this.padLocal(e);
      let dx = px - f.x0, dy = py - f.y0;
      if (Math.hypot(dx, dy) > TAP_PX) f.moved = true;
      const r = stickDir(dx, dy, STICK_R, f.dir);
      const d = Math.hypot(dx, dy);
      if (d > STICK_R) { dx = (dx / d) * STICK_R; dy = (dy / d) * STICK_R; }
      setStyle(this.knob, "transform", `translate(${dx.toFixed(1)}px, ${dy.toFixed(1)}px)`);
      this.stick.classList.toggle("run", r.run);
      if (r.dir !== f.dir || r.run !== this.stick.classList.contains("was-run")) {
        f.dir = r.dir;
        this.stick.classList.toggle("was-run", r.run);
        this.ctx.touch.stick(r.dir, r.run);
      }
      return;
    }
    if (Math.hypot(e.clientX - f.x0, e.clientY - f.y0) > TAP_PX) f.moved = true;
    const [x, y] = this.local(e);
    this.mapMove(x, y);
  }

  private up(e: PointerEvent, cancel: boolean): void {
    const f = this.fingers.get(e.pointerId);
    if (!f) return;
    this.fingers.delete(e.pointerId);
    if (f.kind === "pinch" || f.kind === "dead") {
      if (f.kind === "pinch" && this.pinch) {
        // the other pinch finger does nothing more until it lifts
        const other = this.fingers.get(this.pinch.a === e.pointerId ? this.pinch.b : this.pinch.a);
        if (other) other.kind = "dead";
        this.pinch = null;
        setSetting("MobileZoom", this.ctx.touch.zoom);
      }
      return;
    }
    const tap = !cancel && !f.moved && e.timeStamp - f.t0 < TAP_MS;
    if (f.kind === "stick") {
      this.stick.hidden = true;
      this.stick.classList.remove("was-run");
      this.ctx.touch.stick(-1, false);
      // a short still tap in the stick zone is a map tap there
      if (tap && this.onChatLog(e.clientX, e.clientY)) { this.openChatRead(); return; }
      if (tap) { const [x, y] = this.local(e); if (!this.host.mapClick()) { this.marker(e, this.ctx.touch.press(x, y)); this.ctx.touch.release(true); } }
      return;
    }
    this.ctx.touch.release(tap);
  }

  /** A second map finger: the map finger's walk is dropped and both fingers pinch the camera zoom. */
  private startPinch(e: PointerEvent): void {
    let first = -1;
    for (const [id, f] of this.fingers) if (f.kind === "map") { first = id; break; }
    const a = first >= 0 ? this.fingers.get(first) : undefined;
    if (!a || this.pinch) return;
    this.ctx.touch.cancelPress(); // the first finger's press is undone (no target change, no NPC talk)
    a.kind = "pinch";
    this.fingers.set(e.pointerId, { kind: "pinch", x0: e.clientX, y0: e.clientY, t0: e.timeStamp, moved: true, dir: -1, x: e.clientX, y: e.clientY });
    this.pinch = { a: first, b: e.pointerId, d0: Math.max(20, Math.hypot(a.x - e.clientX, a.y - e.clientY)), z0: this.ctx.touch.zoom };
  }

  /** `fn` after `el` is held for `ms` (the click that follows is swallowed). */
  private longPress(el: HTMLElement, ms: number, fn: () => void): void {
    const sig = this.rt.signal;
    let t = 0, fired = false, sx = 0, sy = 0;
    const stop = () => { if (t) { clearTimeout(t); t = 0; } };
    el.addEventListener("pointerdown", (e) => {
      fired = false; stop(); sx = e.clientX; sy = e.clientY;
      t = window.setTimeout(() => { t = 0; fired = true; buzz(12); fn(); }, ms);
    }, { signal: sig });
    // a finger that slides more than TAP_PX is not a long press
    el.addEventListener("pointermove", (e) => { if (t && Math.hypot(e.clientX - sx, e.clientY - sy) > TAP_PX) stop(); }, { signal: sig });
    el.addEventListener("pointerup", stop, { signal: sig });
    el.addEventListener("pointercancel", stop, { signal: sig });
    this.holdResets.push(stop);
    el.addEventListener("click", (e) => { if (fired) { fired = false; e.stopImmediatePropagation(); } }, { capture: true, signal: sig });
    sig.addEventListener("abort", stop, { once: true });
  }

  /** Skill slots: a finger dragged off a slot aims (a ring on the map under it, TouchControl.aim) and casts where it
   *  lifts (castAt); back over the slot cancels. A tap still casts at the target (SkillRow.onCast). A slot that cannot
   *  cast (cooldown / no MP) buzzes. */
  private skillAim(): void {
    const sig = this.rt.signal;
    for (let i = 0; i < 8; i++) {
      const el = this.skills.slotEl(i);
      if (!el) continue;
      el.style.touchAction = "none";
      let id = -1, sx = 0, sy = 0, aiming = false, swallow = false;
      const over = (e: PointerEvent) => {
        const r = el.getBoundingClientRect();
        return e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom;
      };
      el.addEventListener("pointerdown", (e) => {
        if (this.editing) return;
        id = e.pointerId; sx = e.clientX; sy = e.clientY; aiming = false;
        try { el.setPointerCapture(e.pointerId); } catch { /* synthetic pointer */ }
      }, { signal: sig });
      el.addEventListener("pointermove", (e) => {
        if (e.pointerId !== id) return;
        if (!aiming && Math.hypot(e.clientX - sx, e.clientY - sy) > AIM_PX * this.ps && this.skills.slotState(i) !== "empty") aiming = true;
        if (!aiming) return;
        if (over(e)) { this.ctx.touch.aim(null); return; }
        const [x, y] = toLocal(this.surface, e.clientX, e.clientY);
        this.ctx.touch.aim(x, y);
      }, { signal: sig });
      const end = (e: PointerEvent) => {
        if (e.pointerId !== id) return;
        id = -1;
        if (!aiming) return;
        aiming = false; swallow = true;
        this.ctx.touch.aim(null);
        if (e.type !== "pointerup" || over(e)) return;
        if (this.skills.slotState(i) !== "ok") { buzz([20, 40, 20]); return; }
        const [x, y] = toLocal(this.surface, e.clientX, e.clientY);
        this.ctx.touch.castAt(this.skills.keyFor(i), x, y);
      };
      el.addEventListener("pointerup", end, { signal: sig });
      el.addEventListener("pointercancel", end, { signal: sig });
      el.addEventListener("click", (e) => {
        if (swallow || this.editing) { swallow = false; e.stopImmediatePropagation(); return; }
        const st = this.skills.slotState(i);
        if (st === "cooldown" || st === "nomp") buzz([20, 40, 20]);
      }, { capture: true, signal: sig });
    }
  }

  /** The target frame: a sideways swipe targets the next (right) / previous (left) hostile by distance. */
  private targetSwipe(): void {
    const sig = this.rt.signal, el = this.tgt;
    el.querySelector(".nm")?.append(h("div", "sw", "‹ swipe ›"));
    let id = -1, sx = 0, sy = 0;
    el.addEventListener("pointerdown", (e) => {
      if (this.editing || (e.target as HTMLElement).closest(".mbtn")) return;
      id = e.pointerId; sx = e.clientX; sy = e.clientY;
      try { el.setPointerCapture(e.pointerId); } catch { /* synthetic pointer */ }
    }, { signal: sig });
    const end = (e: PointerEvent) => {
      if (e.pointerId !== id) return;
      id = -1;
      const dx = e.clientX - sx, dy = e.clientY - sy;
      if (e.type !== "pointerup" || Math.abs(dx) < SWIPE_PX * this.ps || Math.abs(dx) < Math.abs(dy) * 1.5) return;
      const step = dx > 0 ? 1 : -1;
      if (this.ctx.touch.cycleTarget(step)) { buzz(8); restartAnim(el, step > 0 ? "swr" : "swl"); this.slow(); }
    };
    el.addEventListener("pointerup", end, { signal: sig });
    el.addEventListener("pointercancel", end, { signal: sig });
  }

  /** Quick chat: canned lines (Normal chat) under the input while the chat sheet is open; a tap sends and closes it. */
  private quickChat(): void {
    const sig = this.rt.signal;
    const row = h("div", "mquick");
    for (const line of QUICK_LINES) {
      const b = asButton(h("div", "", line), `Say "${line}"`);
      b.tabIndex = -1; // focusable: tapping it keeps the focus inside the chat box, so the sheet stays open for the click
      b.addEventListener("click", (e) => {
        e.stopPropagation();
        this.ctx.models.chatLog.submit(line);
        this.closeChat();
      }, { signal: sig });
      row.append(b);
    }
    this.host.chat.el.append(row);
    // iOS pans the visual viewport over the page when the keyboard opens: keep the chat sheet's input in view
    const vv = window.visualViewport;
    if (!vv) return;
    const follow = rafCoalesce(() => {
      const off = this.rt.root.classList.contains("mchat") ? vv.offsetTop / (this.fit || 1) : 0;
      setStyle(this.host.chat.el, "translate", off > 1 ? `0 ${off.toFixed(0)}px` : "");
    }, sig);
    vv.addEventListener("scroll", follow, { signal: sig });
    vv.addEventListener("resize", follow, { signal: sig });
    this.chatFollow = follow;
  }

  private chatFollow: () => void = () => {};
  /** The chat was opened as the reading view (sending drops back to it instead of closing). */
  private chatRead = false;
  /** When a finger last went down on the chat (a focusout then is a tap on the sheet, not outside it). */
  private chatTouchAt = -1e9;
  private chatBg: HTMLElement | null = null;
  private chatX: HTMLElement | null = null;
  private whisperEl: HTMLElement | null = null;

  /** The screen stays on in game (Screen Wake Lock); the browser drops the lock when the page hides, so it is taken
   *  again when the page shows. */
  private wakeLock(): void {
    const wl = (navigator as Navigator & { wakeLock?: { request(type: "screen"): Promise<{ release(): Promise<void> }> } }).wakeLock;
    if (!wl) return;
    const take = () => {
      if (this.disposed || document.visibilityState !== "visible") return;
      wl.request("screen").then((l) => { if (this.disposed) void l.release().catch(() => {}); else this.wake = l; }, () => { /* denied / OS battery saver */ });
    };
    take();
    document.addEventListener("visibilitychange", take, { signal: this.rt.signal });
  }

  /** The first time in game with the touch layout: one card with the gestures. */
  private tips(): void {
    try { if (localStorage.getItem(TIPS_KEY)) return; } catch { return; }
    const card = h("div", "",
      h("b", "", "Touch controls"),
      h("ul", "",
        h("li", "", "Left side: drag to walk, push far to run. Tap the map to walk there, a monster to attack, an NPC to talk."),
        h("li", "", "Hold Attack to fight the nearest monster; hold it longer to lock the target; double-tap it to keep attacking. Swipe the target bar to switch."),
        h("li", "", "Drag a skill onto the map to aim it. Pinch the map to zoom."),
        h("li", "", "Long-press an item for its menu. Hold Menu to move and resize these buttons."),
      ),
      h("div", "ok", "Got it"));
    const tips = h("div", "mtips", card);
    tips.addEventListener("click", (e) => {
      e.stopPropagation();
      tips.remove();
      try { localStorage.setItem(TIPS_KEY, "1"); } catch { /* not persisted */ }
    }, { signal: this.rt.signal });
    this.pad.append(tips);
  }

  // — Layout editor (hold Menu) —

  private get editing(): boolean { return this.pad.classList.contains("medit"); }

  private buildEditor(): void {
    const sig = this.rt.signal;
    const range = (min: number, max: number, val: number, fn: (v: number) => void) => {
      const r = h("input");
      r.type = "range"; r.min = String(min); r.max = String(max); r.step = "5"; r.value = String(val);
      r.addEventListener("input", () => fn(Number(r.value) / 100), { signal: sig });
      return r;
    };
    this.editSize = range(80, 140, 100, (v) => {
      if (!this.editSel) return;
      (this.layoutCfg.g[this.editSel] ??= { x: 0, y: 0, s: 1 }).s = v;
      this.applyLayout();
    });
    const opacity = range(30, 100, Math.round(this.layoutCfg.o * 100), (v) => { this.layoutCfg.o = v; this.applyLayout(); });
    const btn = (text: string, fn: () => void, cls = "") => {
      const b = asButton(h("div", cls, text), text);
      b.addEventListener("click", (e) => { e.stopPropagation(); fn(); }, { signal: sig });
      return b;
    };
    this.editPanel.append(
      h("div", "hd", "Edit touch controls"),
      h("div", "ht", "Drag a control to move it. Tap one, then set its size."),
      h("label", "", h("span", "", "Size"), this.editSize),
      h("label", "", h("span", "", "Opacity"), opacity),
      h("div", "row",
        btn("Reset", () => { this.layoutCfg = { o: 1, g: {} }; opacity.value = "100"; this.selectGroup(this.editSel); this.applyLayout(); }),
        btn("Done", () => this.setEditing(false), "pri")),
    );
    // dragging a group while editing (capture: the controls' own handlers never see it)
    let drag: { id: number; g: Group; sx: number; sy: number; ox: number; oy: number } | null = null;
    this.pad.addEventListener("pointerdown", (e) => {
      if (!this.editing) return;
      const el = (e.target as HTMLElement).closest<HTMLElement>("[data-mg]");
      if (!el) return;
      e.preventDefault(); e.stopPropagation();
      const g = el.dataset.mg as Group;
      this.selectGroup(g);
      const cur = this.layoutCfg.g[g] ?? { x: 0, y: 0, s: 1 };
      drag = { id: e.pointerId, g, sx: e.clientX, sy: e.clientY, ox: cur.x, oy: cur.y };
      try { this.pad.setPointerCapture(e.pointerId); } catch { /* synthetic pointer */ }
    }, { capture: true, signal: sig });
    this.pad.addEventListener("pointermove", (e) => {
      if (!drag || e.pointerId !== drag.id) return;
      const g = (this.layoutCfg.g[drag.g] ??= { x: 0, y: 0, s: 1 });
      // pad pixels are CSS pixels / ps
      g.x = Math.round(drag.ox + (e.clientX - drag.sx) / this.ps);
      g.y = Math.round(drag.oy + (e.clientY - drag.sy) / this.ps);
      this.applyLayout();
    }, { signal: sig });
    const end = (e: PointerEvent) => { if (drag && e.pointerId === drag.id) drag = null; };
    this.pad.addEventListener("pointerup", end, { signal: sig });
    this.pad.addEventListener("pointercancel", end, { signal: sig });
    // no control acts while editing (the panel's own buttons do)
    this.pad.addEventListener("click", (e) => {
      if (this.editing && !this.editPanel.contains(e.target as Node)) { e.stopPropagation(); e.preventDefault(); }
    }, { capture: true, signal: sig });
    this.applyLayout();
  }

  private selectGroup(g: Group | null): void {
    this.editSel = g;
    for (const [k, els] of this.groups) for (const el of els) el.classList.toggle("sel", k === g);
    this.editSize.value = String(Math.round(((g && this.layoutCfg.g[g]?.s) || 1) * 100));
    this.editSize.disabled = !g;
  }

  private setEditing(on: boolean): void {
    this.pad.classList.toggle("medit", on);
    this.selectGroup(null);
    if (on) { // the windows sit above the pad: close them all (Shift+Esc), and the menu
      this.host.hud.gameMenu.close();
      this.rt.wm.handleKey(new KeyboardEvent("keydown", { key: "Escape", shiftKey: true }));
      this.ctx.touch.setAttackHeld(false);
      return;
    }
    const cfg = this.layoutCfg;
    const plain = cfg.o === 1 && Object.values(cfg.g).every((g) => !g || (g.x === 0 && g.y === 0 && g.s === 1));
    setSetting("MobileLayout", plain ? "" : JSON.stringify(cfg));
  }

  /** The edited offsets / scales through the `translate` / `scale` properties (they compose with each control's own
   *  `transform`). The action cluster scales about its corner anchor, so the skill arc stays around Attack. */
  private applyLayout(): void {
    const cfg = this.layoutCfg;
    for (const [g, els] of this.groups) {
      const e = cfg.g[g];
      for (const el of els) {
        el.style.translate = e && (e.x || e.y) ? `${e.x}px ${e.y}px` : "";
        el.style.scale = e && e.s !== 1 ? String(e.s) : "";
        el.style.opacity = cfg.o < 1 ? String(cfg.o) : "";
      }
    }
    // the anchor = the pad corner Attack and the arc sit at (CSS: 18 px in; the page button at 174 / 108 px, 44 px wide)
    const [atk, arc, page] = this.groups.get("action")!;
    atk!.style.transformOrigin = this.left ? "0 100%" : "100% 100%";
    arc!.style.transformOrigin = "0 0";
    page!.style.transformOrigin = this.left ? "-174px 152px" : "218px 152px";
  }

  // — Layout / frame —

  private layout(): void {
    if (this.disposed) return;
    const [sw, sh] = this.ctx.screen;
    const r = this.rt.root.getBoundingClientRect();
    this.fit = r.width > 0 ? r.width / sw : 1;
    // tablets: the pad grows with the short side (a 390-420 px phone = 1, capped at 1.5); small phones (under 700 px wide,
    // e.g. 640 x 360) shrink it to 0.85 so the belt clears the skill arc
    const vv = window.visualViewport, vw = vv?.width || window.innerWidth;
    const short = Math.min(vw, vv?.height || window.innerHeight) || 390;
    this.ps = Math.min(1.5, Math.max(vw > 0 && vw < 700 ? 0.85 : 1, short / 420));
    this.padW = (sw * this.fit) / this.ps; this.padH = (sh * this.fit) / this.ps;
    setStyle(this.pad, "transform", `scale(${(this.ps / this.fit).toFixed(5)})`);
    setStyle(this.pad, "width", this.padW.toFixed(1) + "px");
    setStyle(this.pad, "height", this.padH.toFixed(1) + "px");
    this.rt.root.style.setProperty("--mfit", this.fit.toFixed(5)); // the chat / minimap insets (game pixels)
    const cs = getComputedStyle(this.insetProbe);
    this.inset[0] = parseFloat(cs.paddingTop) || 0; this.inset[1] = parseFloat(cs.paddingRight) || 0;
    this.inset[2] = parseFloat(cs.paddingBottom) || 0; this.inset[3] = parseFloat(cs.paddingLeft) || 0;
    // the belt's width (pad px, the hero cells change it): its CSS keeps it clear of the skill arc
    setStyle(this.beltRow, "--bw", this.beltRow.offsetWidth + "px");
    // left of the (inset) minimap, in pad pixels
    const mm = this.rt.root.querySelector<HTMLElement>(".mmap");
    const mr = mm?.getBoundingClientRect();
    const right = mr && mr.width > 0 ? (r.right - mr.left) / this.ps + 8 : 12;
    setStyle(this.top, "right", right.toFixed(0) + "px");
  }

  private frame(now: number): void {
    if (this.disposed || this.rt.root.hidden) return;
    this.skills.frame(now);
    const p = this.ctx.player, st = p.stats?.stats, sh = this.shown;
    const hpMax = st?.[Stat.HP] ?? 0, mpMax = st?.[Stat.MP] ?? 0;
    if (p.hp !== sh.hp || hpMax !== sh.hpMax) {
      sh.hp = p.hp; sh.hpMax = hpMax;
      setStyle(this.hpFill, "transform", `scaleX(${hpMax > 0 ? Math.min(1, Math.max(0, p.hp / hpMax)).toFixed(3) : "0"})`);
      setText(this.hpText, `${p.hp} / ${hpMax}`);
      const low = hpMax > 0 && p.hp > 0 && p.hp / hpMax < 0.3;
      this.low.classList.toggle("on", low);
      this.stat.classList.toggle("low", low);
      // haptics: a hit (at most every 350 ms), and a longer pattern when HP falls under 30 %
      if (low && !this.hapLow) buzz([60, 80, 60]);
      else if (this.hapHp > 0 && p.hp < this.hapHp && now - this.hapAt > 350) { this.hapAt = now; buzz(25); }
      this.hapLow = low;
      this.hapHp = p.hp;
    }
    if (p.mp !== sh.mp || mpMax !== sh.mpMax) {
      sh.mp = p.mp; sh.mpMax = mpMax;
      setStyle(this.mpFill, "transform", `scaleX(${mpMax > 0 ? Math.min(1, Math.max(0, p.mp / mpMax)).toFixed(3) : "0"})`);
      setText(this.mpText, `${p.mp} / ${mpMax}`);
    }
    // the hidden page's slots on the page button (SkillRow.frame keeps all eight current)
    const off = this.page === 0 ? 4 : 0;
    for (let k = 0; k < 4; k++) {
      const st = this.skills.slotState(off + k);
      if (st !== this.dotShown[k]) { this.dotShown[k] = st; this.pageDots[k]!.className = st; }
    }
  }

  private slow(): void {
    if (this.disposed || this.rt.root.hidden) return;
    const p = this.ctx.player, sh = this.shown, u = p.user;
    const xp = p.maxExperience > 0n ? Number((p.experience * 1000n) / p.maxExperience) / 1000 : 0;
    if (xp !== sh.xp) { sh.xp = xp; setStyle(this.xpFill, "transform", `scaleX(${Math.min(1, xp).toFixed(3)})`); }
    if (p.level !== sh.lv) { sh.lv = p.level; setText(this.lvText, `Lv ${p.level}`); }
    setText(this.nameText, u?.name ?? "");
    // the target
    const id = this.ctx.touch.targetId;
    const ob = id ? this.ctx.world.objects.get(id) : undefined;
    const live = ob && !ob.dead ? ob : undefined;
    // no target: the suggested one (what Attack / a skill tap would hit), dimmed, without lock / clear
    const sid = live ? 0 : this.ctx.touch.suggestedId;
    const so = sid ? this.ctx.world.objects.get(sid) : undefined;
    const show = live ?? (so && !so.dead ? so : undefined), sug = !live && !!show;
    if ((show?.id ?? 0) !== sh.tgt || sug !== sh.sug) {
      sh.tgt = show?.id ?? 0; sh.sug = sug;
      this.tgt.hidden = !show; this.tgt.classList.toggle("sug", sug); setText(this.tgtName, show?.name ?? "");
    }
    const thp = show ? show.healthPercent : -2;
    if (thp !== sh.tgtHp) { sh.tgtHp = thp; setStyle(this.tgtFill, "transform", `scaleX(${thp >= 0 ? (thp / 100).toFixed(2) : "1"})`); }
    // the context button: an item on the user's cell (Pick up) or an NPC close by (Talk)
    const mode = this.ctx.touch.context;
    if (mode !== this.ctxMode) {
      this.ctxMode = mode;
      const b = this.pickBtn, talk = mode === "talk";
      b.classList.toggle("lit", mode !== null);
      b.classList.toggle("talk", talk);
      b.replaceChildren(talk ? this.ctxIcon.talk : this.ctxIcon.pick);
      b.title = talk ? "Talk" : "Pick up";
      b.setAttribute("aria-label", b.title);
    }
    // the saved camera zoom, once the user is on the map (it zooms about the user)
    if (!this.zoomRestored && u) { this.zoomRestored = true; this.ctx.touch.zoom = gameSettings.MobileZoom; }
    // haptics: a level up
    if (this.hapLv >= 0 && p.level > this.hapLv) buzz([30, 50, 30, 50, 80]);
    this.hapLv = p.level;
    const hero = !!this.ctx.models.hero.info;
    if (hero !== sh.hero) { sh.hero = hero; for (const el of this.heroCells) el.hidden = !hero; this.relayout(); } // the belt's width
    const auto = this.ctx.touch.autoAttack; // the game ends it (nothing in range, a walk)
    if (auto !== sh.auto) { sh.auto = auto; this.atk.classList.toggle("auto", auto); }
    const locked = this.ctx.touch.locked;
    if (locked !== sh.locked) { sh.locked = locked; this.lockBtn.classList.toggle("on", locked); this.tgt.classList.toggle("locked", locked); }
  }

  /** Drops every touch input: fingers, the pinch, held buttons and their timers, auto-attack (the app backgrounded, the
   *  window blurred, the page hidden, dispose). */
  private resetInput(): void {
    const t = this.ctx.touch;
    t.stick(-1, false); t.release(false); t.setAttackHeld(false); t.aim(null);
    t.autoAttack = false;
    this.fingers.clear();
    this.pinch = null;
    for (const r of this.holdResets) r();
    this.stick.hidden = true;
    this.stick.classList.remove("run", "was-run");
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.resetInput();
    this.offLife();
    this.rt.root.style.removeProperty("--mfit");
    if (this.wake) { void this.wake.release().catch(() => {}); this.wake = null; }
    setStyle(this.host.chat.el, "translate", "");
    for (const c of this.belt) c.dispose();
    this.belt.length = 0;
    this.surface.remove();
    this.pad.remove();
    this.rt.root.classList.remove("mchat", "mread");
    this.chatBg?.remove(); this.chatX?.remove(); this.whisperEl?.remove();
  }
}

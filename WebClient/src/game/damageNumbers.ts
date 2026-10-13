// Damage numbers, lifted from the WoOOLcraft web client (src/WebClient/src/game/damageNumbers.ts, the "new" style of its
// DamageInfo port: ProcessNew / DrawNew timing, fountain, crit pop, colours, the >3-entry compression; its GDI+ text
// rasteriser, textTexture.ts rasteriseDamageText, as a Canvas2D draw). A port addition: the Crystal desktop draws
// S.DamageIndicator as small rising labels instead (GameScene.DamageIndicator + MirObjects/Damage.cs).
// Crystal mapping (GameScene.DamageIndicator): Hit = Damage (negative = damage, positive = regen), Miss = the "Miss" text,
// Critical = flags the Hit the server broadcasts right after it (HumanObject / MonsterObject.Attacked) as a crit.
// Pure: every time is an argument; the caller owns the lists and draws with `drawDamageNumber`.

export interface DamageNumber {
  /** Negative for damage, positive for heals, 0 for a text entry (Miss). */
  value: number;
  /** Drawn instead of the value when set (Miss). */
  text: string;
  critical: boolean;
  isUserTarget: boolean;
  startTime: number;
  visible: boolean;
  opacity: number;
  /** Vertical offset above the object's feet. */
  drawY: number;
  /** 0 until the first process, then +/-1. */
  fountainSide: number;
  fountainJitter: number;
  drawOffsetX: number;
  drawScale: number;
}

const TOTAL_MS = 1450;
const CRIT_TOTAL_MS = 1800;
const FADE_MS = 500;
const POP_MS = 220;
const SHAKE_MS = 260;
/** WoOOLcraft MapObject: the compression lands an entry exactly 1500 ms old (its old style's AppearDelay + ShowDelay). */
const COMPRESS_TARGET_MS = 1500;
const APPEAR_MS = 500;
/** GameScene.DamageIndicator: `if (obj.Damages.Count >= 10) return;` */
export const MAX_DAMAGE_NUMBERS = 10;

const clamp01 = (v: number): number => Math.max(0, Math.min(1, v));
const isCritHit = (d: DamageNumber): boolean => d.critical && d.value < 0;

export function makeDamageNumber(f: { value: number; text?: string; critical?: boolean; isUserTarget?: boolean; startTime: number }): DamageNumber {
  return {
    value: f.value, text: f.text ?? "", critical: f.critical ?? false, isUserTarget: f.isUserTarget ?? false, startTime: f.startTime,
    visible: true, opacity: 0, drawY: 0, fountainSide: 0, fountainJitter: 0, drawOffsetX: 0, drawScale: 1,
  };
}

/** Crit damage 28 pt, everything else 11 pt, as pixels at 96 DPI. */
export function damageFontPx(d: DamageNumber): number {
  return ((isCritHit(d) ? 28 : 11) * 4) / 3;
}

function groupThousands(n: number): string {
  const digits = Math.abs(Math.trunc(n)).toString();
  let out = "";
  for (let i = 0; i < digits.length; i++) {
    if (i > 0 && (digits.length - i) % 3 === 0) out += ",";
    out += digits[i];
  }
  return out;
}

/** The sign is in the colour: damage has no minus, heals get a "+". */
export function damageText(d: DamageNumber): string {
  if (d.text) return d.text;
  if (d.value === 0) return "";
  return d.value > 0 ? `+${groupThousands(d.value)}` : groupThousands(d.value);
}

/** Heal green, damage on the user red, crit gold, else white; Miss light grey (light coral on the user, like the desktop). */
export function damageColour(d: DamageNumber): string {
  if (d.text) return d.isUserTarget ? "rgb(240,128,128)" : "rgb(211,211,211)";
  if (d.value > 0) return "rgb(75,235,90)";
  if (d.isUserTarget) return "rgb(240,60,55)";
  if (d.critical) return "rgb(255,225,35)";
  return "rgb(255,255,255)";
}

function processOne(d: DamageNumber, previous: DamageNumber | null, now: number): void {
  const elapsed = now - d.startTime;
  const totalMs = isCritHit(d) ? CRIT_TOTAL_MS : TOTAL_MS;
  if (elapsed >= totalMs) { d.visible = false; return; }

  if (d.fountainSide === 0) {
    // Computed once per number: heals go left, the user's own numbers right, the rest by seed parity
    const seed = (((Math.floor(d.startTime) % 1000) + 1000) % 1000) + d.value + (d.critical ? 17 : 0);
    d.fountainSide = d.value > 0 ? -1 : d.isUserTarget ? 1 : seed % 2 === 0 ? -1 : 1;
    d.fountainJitter = Math.abs(seed % 9) - 4;
  }

  const progress = clamp01(elapsed / totalMs);
  const fadeStart = Math.max(0, totalMs - FADE_MS);
  d.opacity = elapsed < fadeStart ? 1 : Math.max(0, 1 - (elapsed - fadeStart) / FADE_MS);

  if (isCritHit(d)) {
    // Pop to 3x and back in 220 ms, a short shake, then rise
    const pop = Math.sin(clamp01(elapsed / POP_MS) * Math.PI);
    d.drawScale = 1 + 2 * pop;
    d.drawY = Math.trunc(26 + 72 * progress);
    const shake = elapsed < SHAKE_MS ? Math.sin(elapsed / 24) * 3 : 0;
    d.drawOffsetX = d.fountainSide * (8 + 18 * progress) + shake + d.fountainJitter;
  } else {
    d.drawScale = 1;
    d.drawY = Math.trunc(24 + 78 * progress);
    d.drawOffsetX = d.fountainSide * (10 + 24 * Math.sin((progress * Math.PI) / 2)) + d.fountainJitter;
    // Only damage on another object stacks: never closer than 20 px under the previous number
    if (previous !== null && previous.visible && d.value < 0 && !d.isUserTarget) d.drawY = Math.min(d.drawY, previous.drawY - 20);
  }
}

/** One object's list, once per frame: with more than three live entries, an entry past its first 500 ms is aged to 1500 ms
 *  (a normal number dies at once, a crit jumps into its fade); then process in order and drop the finished ones. */
export function processDamageList(list: DamageNumber[], now: number): void {
  const count = list.length;
  for (let i = 0; i < count; i++) {
    const d = list[i];
    const age = now - d.startTime;
    if (count - i > 3 && age > APPEAR_MS && age < COMPRESS_TARGET_MS) d.startTime = now - COMPRESS_TARGET_MS;
    processOne(d, i > 0 ? list[i - 1] : null, now);
  }
  for (let i = list.length - 1; i >= 0; i--) if (!list[i].visible) list.splice(i, 1);
}

export const DAMAGE_FONT_STACK = "'Source Sans 3','Source Sans Pro','Segoe UI',sans-serif";

/** DrawNew + the rasteriser: a box of the text's size + 6 (text at (3, 3), a 190-alpha black shadow at (4, 4)), scaled about
 *  its top-left and centred on (drawX + 24 + offsetX, drawY - DrawY - 34). (drawX, drawY) = the object's draw location. */
export function drawDamageNumber(g: CanvasRenderingContext2D, d: DamageNumber, drawX: number, drawY: number): void {
  const text = damageText(d);
  if (!text || d.opacity <= 0) return;
  g.save();
  g.font = `${damageFontPx(d)}px ${DAMAGE_FONT_STACK}`;
  g.textAlign = "left";
  g.textBaseline = "top";
  const m = g.measureText(text);
  const w = Math.ceil(m.width) + 6;
  const h = Math.ceil(m.fontBoundingBoxAscent + m.fontBoundingBoxDescent) + 6;
  const s = d.drawScale;
  const x = drawX + 24 + Math.trunc(d.drawOffsetX) - Math.trunc((w * s) / 2);
  const y = drawY - d.drawY - 34 - Math.trunc((h * s) / 2);
  g.globalAlpha = d.opacity;
  g.translate(x, y);
  g.scale(s, s);
  g.fillStyle = "rgba(0,0,0,0.745)";
  g.fillText(text, 4, 4);
  g.fillStyle = damageColour(d);
  g.fillText(text, 3, 3);
  g.restore();
}

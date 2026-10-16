// The new UI's shared formatting (webclient.md §3.6: no local fmt / pretty / class tables). Collected from the scene-lab
// mocks (hudPopup parseAmount, hudMock fmtTime, hudGameMenu fmtSession, hudBigMap pretty / npcName, hudChat fmtTime and
// the CLASS_TAG / CLS / CLASSES tables of hudFriends, hudGroup, hudInspect, hudMentor, hudRanking, hudRelationship,
// hudSelect, hudTooltip); class names come from MirClass (enums.generated). DOM-free.

import { MirClass, MirClassName, ItemGradeName } from "../../../protocol/enums.generated.ts";

const NUM = new Intl.NumberFormat("en-GB", { maximumFractionDigits: 0 });
const NUM1 = new Intl.NumberFormat("en-GB", { maximumFractionDigits: 1 });
const CLOCK = new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit" });
const DATE = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric" });
const DATE_TIME = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });

/** 1234567 -> "1,234,567" (rounded). Accepts bigint (experience, market prices). */
export function fmt(n: number | bigint): string {
  if (typeof n === "bigint") return n.toLocaleString("en-GB");
  return Number.isFinite(n) ? NUM.format(Math.round(n)) : "0";
}

/** One decimal at most: 12.25 -> "12.3", 12 -> "12". */
export function fmt1(n: number): string { return Number.isFinite(n) ? NUM1.format(n) : "0"; }

/** Short form for tight spots: 950 -> "950", 1500 -> "1.5k", 2300000 -> "2.3m", 4.1e9 -> "4.1b". */
export function fmtCompact(n: number | bigint): string {
  const v = typeof n === "bigint" ? Number(n) : n;
  const a = Math.abs(v);
  if (a < 1e4) return fmt(v);
  const [d, s] = a < 1e6 ? [1e3, "k"] : a < 1e9 ? [1e6, "m"] : [1e9, "b"];
  const x = v / d;
  return (Math.abs(x) < 100 ? fmt1(x) : fmt(x)) + s;
}

/** "1.5k" -> 1500, "2m" -> 2000000, "12,000" -> 12000; NaN for junk (amount boxes). */
export function parseAmount(s: string): number {
  const m = /^\s*([\d,]*\.?\d*)\s*([kKmMbB]?)\s*$/.exec(s);
  if (!m || !m[1] || m[1] === ".") return NaN;
  const n = parseFloat(m[1].replace(/,/g, ""));
  return Math.floor(n * ({ k: 1e3, m: 1e6, b: 1e9 }[m[2]!.toLowerCase()] ?? 1));
}

/** A percentage of a / b with `digits` decimals ("63.4%"); 0 when b is 0. */
export function pct(a: number | bigint, b: number | bigint, digits = 1): string {
  const x = Number(b) ? (Number(a) / Number(b)) * 100 : 0;
  return `${x.toFixed(digits)}%`;
}

/** "1 item" / "3 items" (word + "s" unless `many` is given). */
export function plural(n: number, one: string, many = one + "s"): string {
  return `${fmt(n)} ${n === 1 ? one : many}`;
}

/** Seconds as the biggest unit (buff / cooldown labels): 4000 -> "1h", 90 -> "1m", 4.2 -> "5s". */
export function fmtShort(secs: number): string {
  if (secs >= 86400) return `${Math.floor(secs / 86400)}d`;
  if (secs >= 3600) return `${Math.floor(secs / 3600)}h`;
  if (secs >= 60) return `${Math.floor(secs / 60)}m`;
  return `${Math.max(0, Math.ceil(secs))}s`;
}

/** Seconds as two units: 3725 -> "1h 02m", 125 -> "2m 05s", 42 -> "42s", 90061 -> "1d 1h". */
export function fmtDuration(secs: number): string {
  const s = Math.max(0, Math.floor(secs));
  const d = Math.floor(s / 86400), hh = Math.floor(s / 3600) % 24, m = Math.floor(s / 60) % 60, ss = s % 60;
  if (d) return `${d}d ${hh}h`;
  if (hh) return `${hh}h ${String(m).padStart(2, "0")}m`;
  if (m) return `${m}m ${String(ss).padStart(2, "0")}s`;
  return `${ss}s`;
}

/** Seconds as a clock: 125 -> "2:05", 3725 -> "1:02:05". */
export function fmtClock(secs: number): string {
  const s = Math.max(0, Math.ceil(secs));
  const hh = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, ss = String(s % 60).padStart(2, "0");
  return hh ? `${hh}:${String(m).padStart(2, "0")}:${ss}` : `${m}:${ss}`;
}

/** How long ago `then` was (ms timestamps): "just now", "5 min ago", "2 h ago", "yesterday", "4 days ago", a date. */
export function fmtAgo(then: number, now = Date.now()): string {
  const s = Math.max(0, (now - then) / 1000);
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  if (s < 2 * 86400) return "yesterday";
  if (s < 30 * 86400) return `${Math.floor(s / 86400)} days ago`;
  return DATE.format(then);
}

/** Wall-clock time "14:05" (one cached formatter, Performance.md T3-14). */
export function fmtTimeOfDay(t: number | Date = Date.now()): string { return CLOCK.format(t); }
/** "16 Oct 2026". */
export function fmtDate(t: number | Date): string { return DATE.format(t); }
/** "16 Oct, 14:05". */
export function fmtDateTime(t: number | Date): string { return DATE_TIME.format(t); }

/** A server name made readable: "BichonWall_2" -> "Bichon Wall 2", "Woomyon(1)" -> "Woomyon (1)". */
export function pretty(name: string): string {
  return name.replace(/_/g, " ").replace(/([a-z])([A-Z])/g, "$1 $2").replace(/([A-Za-z])\(/g, "$1 (").replace(/\s+/g, " ").trim();
}

/** An NPC's display name and place: "BichonProvince_Merchant_Herbalist" -> ["Herbalist", "Bichon Province · Merchant"]. */
export function npcName(n: string): [string, string] {
  const p = n.split("_").filter(Boolean);
  return [pretty(p.pop() ?? n), p.map(pretty).join(" · ")];
}

/** MirClass value -> "Warrior" (MirClassName from enums.generated). */
export function className(cls: number): string { return MirClassName[cls] ?? "Unknown"; }

const CLASS_TAGS: Readonly<Record<number, string>> = {
  [MirClass.Warrior]: "WAR", [MirClass.Wizard]: "WIZ", [MirClass.Taoist]: "TAO", [MirClass.Assassin]: "ASN", [MirClass.Archer]: "ARC",
};
/** MirClass value -> the three-letter tag the mocks show on avatars ("WAR", "WIZ", "TAO", "ASN", "ARC"). */
export function classTag(cls: number): string { return CLASS_TAGS[cls] ?? "?"; }

/** Every class in MirClass order (pickers, ranking boards). */
export const CLASSES: readonly MirClass[] = Object.values(MirClass);

/** ItemGrade value -> "Rare" (None -> ""). Colours: styles.ts gradeVar(). */
export function gradeName(grade: number): string { return grade ? ItemGradeName[grade] ?? "" : ""; }

/** A stat range "12-18", or one number when both ends match. */
export function range(min: number, max: number): string { return min === max ? fmt(min) : `${fmt(min)}-${fmt(max)}`; }

/** "+5" / "-3" / "0". */
export function signed(n: number): string { return n > 0 ? `+${fmt(n)}` : fmt(n); }

/** Truncates to `max` characters with an ellipsis. */
export function clip(s: string, max: number): string { return s.length > max ? s.slice(0, Math.max(0, max - 1)) + "…" : s; }

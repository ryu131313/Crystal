// Who's who in the lab mocks (consistency pass, 2026-10-15): one roster for the characters that appear in more than one
// window (friends, group, guild, chat, mail, inspect, mentor, relationship, rankings, trade, territories), so a name has
// one class, level, gender and guild everywhere. ME is the player: its level and class follow the HUD mock's knobs
// (hudMock.ts calls setMe), and the windows that list the player read it when they draw.
//
// The story the mocks tell: GMRyu leads GameMaster and is married to Seren (41 days); GMRyu mentors Wren and is mentored
// by Aldric (GMRyu's mentor side and mentee side are two views of the mentor window); Kaelith is an officer; Garrick
// leads the rival guild Nightfall with Elowen as an officer; Thorne and Corvin are in Lionheart, Isolde in Silver Dawn;
// Pip has no guild. The account's other characters are GMRyuWiz and GMRyuTao (character select, storage); the heroes
// (Seraphine, Rowan, Lyra) are separate from players.

export interface Person { cls: string; level: number; guild: string; female: boolean }
const p = (cls: string, level: number, guild: string, female = false): Person => ({ cls, level, guild, female });

export const ME = { name: "GMRyu", cls: "Warrior", level: 42, guild: "GameMaster" };
export function setMe(level: number, cls: string): void { ME.level = level; ME.cls = cls; }

export const PEOPLE: Record<string, Person> = {
  Seren: p("Archer", 31, "GameMaster", true), Kaelith: p("Assassin", 42, "GameMaster", true), Brannoc: p("Wizard", 45, "GameMaster"), Wren: p("Taoist", 29, "GameMaster"),
  Nyra: p("Taoist", 24, "GameMaster", true), Mordrake: p("Taoist", 31, "GameMaster"), Grimwald: p("Warrior", 47, "GameMaster"), Mirelle: p("Wizard", 44, "GameMaster", true),
  Aldric: p("Warrior", 60, "GameMaster"), Tobin: p("Assassin", 35, "GameMaster"), Vexa: p("Wizard", 22, "GameMaster", true), Ashka: p("Assassin", 39, "GameMaster", true),
  Talia: p("Taoist", 51, "GameMaster", true),
  Garrick: p("Warrior", 55, "Nightfall"), Elowen: p("Taoist", 61, "Nightfall", true), Kroll: p("Assassin", 33, "Nightfall"),
  Thorne: p("Warrior", 38, "Lionheart"), Corvin: p("Archer", 41, "Lionheart"), Isolde: p("Wizard", 36, "Silver Dawn", true), Halvard: p("Warrior", 35, "—"),
  Pip: p("Archer", 24, "—"), Dorrin: p("Taoist", 33, "—"), Lumen: p("Assassin", 38, "—"), Tamsin: p("Warrior", 24, "—", true), Odo: p("Taoist", 29, "—"), Brisk: p("Archer", 33, "—"),
  Sable: p("Wizard", 68, "—", true),
};
/** Applies the roster to a listed character (rankings, guild members): class, level and guild when the name is known. */
export function canon<T extends { name: string; cls: string; level: number; guild?: string }>(x: T): T {
  if (x.name === ME.name) { x.cls = ME.cls; x.level = ME.level; if ("guild" in x) x.guild = ME.guild; return x; }
  const q = PEOPLE[x.name];
  if (q) { x.cls = q.cls; x.level = q.level; if ("guild" in x) x.guild = q.guild; }
  return x;
}

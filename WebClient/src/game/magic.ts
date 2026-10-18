// The user's magic: UserObject.Magics + the warrior toggles, GameScene.UseSpell (key -> toggle or "next magic"),
// MapControl.UseMagic (cooldown, MP, targeting rules, range -> a queued MirAction.Spell) and the magic packet
// handlers (S.NewMagic / RemoveMagic / MagicLeveled / Magic / MagicCast / MagicDelay / SpellToggle).
// Hero magic (GameScene.Hero.Magics): the list from S.HeroInformation, NewMagic (Hero = true), the hero's MagicLeveled /
// MagicDelay / SpellToggle / ObjectMagic, UseSpell keys 17-24 and UseMagic(actor = Hero), which sends C.Magic at once.
// Mana/teleport penalty stats and the Archer bow-skill checks are not ported.

import type { ServerPacket, ClientMagic } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { Spell, MirAction as A, ObjectType, ChatType, SpellToggleState, HeroSpawnState, PoisonType } from "../protocol/enums.generated.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import type { MapObj } from "./objects.ts";
import type { Hero } from "./hero.ts";

export interface UserMagic extends Omit<ClientMagic, "CastTime" | "Delay"> {
  /** Page-clock ms of the last cast (CMain.Time); -Infinity until the first. */
  castTime: number;
  /** Cooldown ms (ClientMagic.Delay). */
  delay: number;
}

/** UseSpell's "next magic" (UserObject.NextMagic / NextMagicLocation / NextMagicObject / NextMagicDirection). */
interface NextMagic { magic: UserMagic; location: WirePoint; object: MapObj | null; direction: number }

const PASSIVE = new Set<number>([Spell.Fencing, Spell.FatalSword, Spell.MPEater, Spell.Hemorrhage, Spell.SpiritSword, Spell.Slaying, Spell.Focus, Spell.Meditation]);

/** GameScene.UseSpell's passive list: these spells do nothing when their key is pressed (they still take a key, as on
 *  the desktop). */
export function isPassiveSpell(spell: number): boolean {
  return PASSIVE.has(spell);
}

const ENEMY_TARGET = new Set<number>([
  Spell.FireBall, Spell.GreatFireBall, Spell.ElectricShock, Spell.Poisoning, Spell.ThunderBolt, Spell.FlameDisruptor, Spell.SoulFireBall, Spell.TurnUndead,
  Spell.FrostCrunch, Spell.Vampirism, Spell.Revelation, Spell.Entrapment, Spell.Hallucination, Spell.DarkBody, Spell.FireBounce, Spell.MeteorShower,
]);
const BOW_TARGET = new Set<number>([
  Spell.StraightShot, Spell.DoubleShot, Spell.ElementalShot, Spell.DelayedExplosion, Spell.BindingShot, Spell.VampireShot, Spell.PoisonShot,
  Spell.CrippleShot, Spell.NapalmShot, Spell.SummonVampire, Spell.SummonToad, Spell.SummonSnakes,
]);
const FRIEND_TARGET = new Set<number>([Spell.Purification, Spell.Healing, Spell.UltimateEnhancer, Spell.EnergyShield, Spell.PetEnhancer]);
/** Friendly spells cast on a location (the party around it): the touch pad centres them on the caster (targetKind). */
const FRIENDLY_AREA = new Set<number>([Spell.SoulShield, Spell.BlessedArmour, Spell.MassHealing]);
const OPTIONAL_TARGET = new Set<number>([
  Spell.FireBang, Spell.MassHiding, Spell.FireWall, Spell.TrapHexagon, Spell.HealingCircle, Spell.CatTongue, Spell.PoisonCloud, Spell.Blizzard,
  Spell.MeteorStrike, Spell.Trap, Spell.Stonetrap,
]);

export interface MagicHost {
  send(frame: Uint8Array): void;
  chat(text: string, type: number): void;
  now(): number;
  user(): MapObj | null;
  mp(): number;
  /** MapObject.MagicObject (the last monster targeted by a spell). */
  magicObject(): MapObj | null;
  setMagicObject(id: number): void;
  /** GameScene.Hero (hero.ts): MP and the dead state for hero casts. Hero casting is off without it. */
  hero?: Hero;
  /** GameScene.HeroObject: the hero's map object (id = hero.info.objectId), null when it is not on the map. */
  heroObject?(): MapObj | null;
}

/** UserObject toggle flags (Thrusting / HalfMoon / ...), kept per actor like the desktop's User and Hero. */
export interface Toggles { slaying: boolean; thrusting: boolean; halfMoon: boolean; crossHalfMoon: boolean; doubleSlash: boolean; twinDrakeBlade: boolean; flamingSword: boolean }
type ToggleField = "thrusting" | "halfMoon" | "crossHalfMoon" | "doubleSlash";

/** ClientTextKeys.Hero: the chat prefix of the hero's toggle lines. */
const HERO_PREFIX = "(Hero) ";
/** First hero key (UseSpell 17-24 = KeybindOptions.HeroSkill1-8, Shift+F1-F8). */
export const HERO_KEY_OFFSET = 16;

export class Magic {
  magics: UserMagic[] = [];
  slaying = false; thrusting = false; halfMoon = false; crossHalfMoon = false; doubleSlash = false; twinDrakeBlade = false; flamingSword = false;
  /** GameScene.SpellTime: no new spell before this. */
  spellTime = 0;
  private toggleTime = 0;
  private outputDelay = 0;
  next: NextMagic | null = null;
  /** GameScene.Hero.Magics (S.HeroInformation); empty while no hero is summoned. */
  heroMagics: UserMagic[] = [];
  /** The hero's toggle flags (display only: the server runs the hero's attacks). */
  readonly heroToggles: Toggles = { slaying: false, thrusting: false, halfMoon: false, crossHalfMoon: false, doubleSlash: false, twinDrakeBlade: false, flamingSword: false };
  /** S.HeroInformation.ObjectID while the hero is summoned, else 0. */
  heroId = 0;
  /** GameScene.HeroSpellTime. */
  private heroSpellTime = 0;
  /** S.HeroAutoCast (web-only): the hero's spells with auto-cast off (UserMagic.AutoCast false on the server; the hero AI
   *  skips them). Always the full list; empty while no hero is summoned. */
  readonly heroAutoCastOff = new Set<number>();
  private readonly listeners = new Set<() => void>();
  private readonly castListeners = new Set<() => void>();
  private readonly heroListeners = new Set<() => void>();
  private readonly host: MagicHost;

  constructor(host: MagicHost) {
    this.host = host;
  }

  get(spell: number): UserMagic | undefined {
    return this.magics.find((m) => m.Spell === spell);
  }

  /** The touch pad's targeting class of magic key 1-24 (hero keys over HERO_KEY_OFFSET): "friend" = FRIEND_TARGET (a
   *  friendly object, else the caster), "area" = FRIENDLY_AREA (about the caster), else "enemy". */
  targetKind(key: number): "friend" | "area" | "enemy" {
    const m = (key > HERO_KEY_OFFSET ? this.heroMagics : this.magics).find((x) => x.Key === key);
    return !m ? "enemy" : FRIEND_TARGET.has(m.Spell) ? "friend" : FRIENDLY_AREA.has(m.Spell) ? "area" : "enemy";
  }

  /** Hero.GetMagic. */
  getHero(spell: number): UserMagic | undefined {
    return this.heroMagics.find((m) => m.Spell === spell);
  }

  /** Subscribe to changes of the user's magic list (list, level, experience, key, delay, cast time, toggles). Returns the unsubscribe. */
  subscribe(fn: () => void): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  changed(): void {
    for (const fn of this.listeners) fn();
  }

  /** Subscribe to spell casts (every C.Magic the user sends, for the user or the hero; MapControl.UseMagic sets
   *  GameScene.LogTime before `actor == User`): the combat log-out lock. */
  onCast(fn: () => void): () => void {
    this.castListeners.add(fn);
    return () => this.castListeners.delete(fn);
  }

  /** Fires the onCast listeners; call it after sending C.Magic for the user or the hero. */
  casted(): void {
    for (const fn of this.castListeners) fn();
  }

  /** Subscribe to changes of the hero's magic list (list, level, experience, key, delay, cast time). */
  onHeroChange(fn: () => void): () => void {
    this.heroListeners.add(fn);
    return () => this.heroListeners.delete(fn);
  }

  heroChanged(): void {
    for (const fn of this.heroListeners) fn();
  }

  private wrap(m: ClientMagic): UserMagic {
    const { CastTime: _c, Delay, ...rest } = m;
    return { ...rest, castTime: -Infinity, delay: Number(Delay) };
  }

  handle(p: ServerPacket): void {
    if (this.handleHero(p)) { this.heroChanged(); return; }
    switch (p.type) {
      case "UserInformation": this.magics = p.Magics.map((m) => this.wrap(m)); break;
      case "NewMagic": if (!p.Hero) this.magics.push(this.wrap(p.Magic)); break;
      case "RemoveMagic": this.magics.splice(p.PlaceId, 1); break;
      case "MagicLeveled": { // GameScene.MagicLeveled: anything not for the hero is the user's
        const m = this.get(p.Spell);
        if (m) { m.Level = p.Level; m.Experience = p.Experience; }
        break;
      }
      case "Magic": {
        const u = this.host.user();
        if (u) { u.spell = p.Spell; u.cast = p.Cast; u.targetId = p.TargetID; u.targetPoint = { ...p.Target }; u.spellLevel = p.Level; u.secondaryTargetIds = p.SecondaryTargetIDs; }
        if (p.Cast) { const m = this.get(p.Spell); if (m) m.castTime = this.host.now(); }
        break;
      }
      case "MagicCast": { const m = this.get(p.Spell); if (m) m.castTime = this.host.now(); break; }
      case "MagicDelay": { const m = this.get(p.Spell); if (m) m.delay = Number(p.Delay); break; }
      case "SpellToggle": this.toggled(this, "", p.Spell, p.CanUse); break;
      default: return;
    }
    this.changed();
  }

  /** The hero side of GameScene.HeroInformation (UserHeroObject.Load) / UpdateHeroSpawnState / NewMagic (p.Hero) /
   *  MagicLeveled / MagicDelay / SpellToggle / ObjectMagic (p.ObjectID == Hero?.ObjectID). True when the packet was the hero's.
   *  The server forwards only MagicDelay / MagicLeveled of the hero's own packets (HeroObject.Enqueue) plus NewMagic(Hero). */
  private handleHero(p: ServerPacket): boolean {
    switch (p.type) {
      case "HeroInformation": {
        // UserHeroObject.Load: Magics[i].CastTime += CMain.Time (the server sends CastTime - Envir.Time)
        const now = this.host.now();
        this.heroId = p.ObjectID;
        this.heroMagics = p.Magics.map((m) => ({ ...this.wrap(m), castTime: now + Number(m.CastTime) }));
        Object.assign(this.heroToggles, { slaying: false, thrusting: false, halfMoon: false, crossHalfMoon: false, doubleSlash: false, twinDrakeBlade: false, flamingSword: false });
        this.heroAutoCastOff.clear(); // S.HeroAutoCast follows S.HeroInformation
        return true;
      }
      case "HeroAutoCast": { // after S.HeroInformation and as the C.HeroSpellToggle reply: the full disabled list
        this.heroAutoCastOff.clear();
        for (const spell of p.Disabled) this.heroAutoCastOff.add(spell);
        return true;
      }
      case "UpdateHeroSpawnState":
        // below Summoned the desktop disposes GameScene.Hero (hero.ts clears hero.info the same way)
        if (p.State >= HeroSpawnState.Summoned || !this.heroId) return false;
        this.heroId = 0;
        this.heroMagics = [];
        this.heroAutoCastOff.clear();
        return true;
      case "NewMagic":
        if (!p.Hero || !this.heroId) return false;
        this.heroMagics.push({ ...this.wrap(p.Magic), castTime: this.host.now() + Number(p.Magic.CastTime) });
        return true;
      case "MagicLeveled": {
        if (!this.heroId || p.ObjectID !== this.heroId) return false;
        const m = this.getHero(p.Spell);
        if (m) { m.Level = p.Level; m.Experience = p.Experience; }
        return true;
      }
      case "MagicDelay": {
        if (!this.heroId || p.ObjectID !== this.heroId) return false;
        const m = this.getHero(p.Spell);
        if (m) m.delay = Number(p.Delay);
        return true;
      }
      case "SpellToggle":
        if (!this.heroId || p.ObjectID !== this.heroId) return false;
        this.toggled(this.heroToggles, HERO_PREFIX, p.Spell, p.CanUse);
        return true;
      case "ObjectMagic": {
        // GameScene.ObjectMagic: the hero's cast starts its cooldown (game.ts still queues the Spell action on the object)
        if (!this.heroId || p.ObjectID !== this.heroId || !p.Cast) return false;
        const m = this.getHero(p.Spell);
        if (m) m.castTime = this.host.now();
        return true;
      }
      default: return false;
    }
  }

  /** GameScene.SpellToggle's chat lines (the hero's carry the "(Hero) " prefix). */
  private toggled(t: Toggles, prefix: string, spell: number, on: boolean): void {
    const say = (yes: string, no: string, noType: number = ChatType.Hint) => this.host.chat(prefix + (on ? yes : no), on ? ChatType.Hint : noType);
    switch (spell) {
      case Spell.Slaying: t.slaying = on; break;
      case Spell.Thrusting: t.thrusting = on; say("Use Thrusting.", "Do not use Thrusting."); break;
      case Spell.HalfMoon: t.halfMoon = on; say("Use Half Moon.", "Do not use Half Moon."); break;
      case Spell.CrossHalfMoon: t.crossHalfMoon = on; say("Use Cross Half Moon.", "Do not use Cross Half Moon."); break;
      case Spell.DoubleSlash: t.doubleSlash = on; say("Use Double Slash.", "Do not use Double Slash."); break;
      case Spell.TwinDrakeBlade: t.twinDrakeBlade = on; break;
      case Spell.FlamingSword: t.flamingSword = on; say("Your weapon is glowed by spirit of fire.", "The spirits of fire disappeared.", ChatType.System); break;
    }
  }

  private cost(m: UserMagic): number {
    return m.Level * m.LevelCost + m.BaseCost;
  }

  private sendToggle(spell: number, canUse: boolean): void {
    this.host.send(ClientPackets.SpellToggle({ Spell: spell, canUse: canUse ? SpellToggleState.True : SpellToggleState.False }));
  }

  /** GameScene.UseSpell(key): toggles act at once, everything else becomes the next magic, cast when the user is idle. */
  useSpell(key: number, location: WirePoint, object: MapObj | null, direction: number): void {
    if (key > HERO_KEY_OFFSET) { this.useHeroSpell(key, location, object, direction); return; }
    const u = this.host.user();
    if (!u || u.dead) return;
    const magic = this.magics.find((m) => m.Key === key);
    if (!magic) return;
    const now = this.host.now();
    if (PASSIVE.has(magic.Spell)) return;
    const flip = (field: "thrusting" | "halfMoon" | "crossHalfMoon" | "doubleSlash", yes: string, no: string) => {
      if (now < this.toggleTime) return;
      this[field] = !this[field];
      this.host.chat(this[field] ? yes : no, ChatType.Hint);
      this.toggleTime = now + 1000;
      this.sendToggle(magic.Spell, this[field]);
    };
    switch (magic.Spell) {
      case Spell.Thrusting: flip("thrusting", "Use Thrusting.", "Do not use Thrusting."); return;
      case Spell.HalfMoon: flip("halfMoon", "Use Half Moon.", "Do not use Half Moon."); return;
      case Spell.CrossHalfMoon: flip("crossHalfMoon", "Use Cross Half Moon.", "Do not use Cross Half Moon."); return;
      case Spell.DoubleSlash: flip("doubleSlash", "Use Double Slash.", "Do not use Double Slash."); return;
      case Spell.TwinDrakeBlade:
      case Spell.FlamingSword:
        if (now < this.toggleTime) return;
        this.toggleTime = now + 500;
        if (this.cost(magic) > this.host.mp()) { this.host.chat("Not enough mana.", ChatType.System); return; }
        if (magic.Spell === Spell.TwinDrakeBlade) this.twinDrakeBlade = true;
        this.sendToggle(magic.Spell, true);
        return;
      case Spell.CounterAttack:
        if (now < magic.castTime + magic.delay) return;
        magic.castTime = now;
        if (this.cost(magic) > this.host.mp()) { this.host.chat("Not enough mana.", ChatType.System); return; }
        this.sendToggle(magic.Spell, true);
        return;
      case Spell.MentalState:
        if (now < this.toggleTime) return;
        this.toggleTime = now + 500;
        this.sendToggle(magic.Spell, true);
        return;
    }
    this.next = { magic, location, object, direction };
  }

  /** GameScene.UseSpell(key 17-24, actor = Hero): KeybindOptions.HeroSkill1-8 (Shift+F1-F8). Toggles flip locally and go out as
   *  C.SpellToggle with CanUse = None (SendSpellToggle; MirConnection.SpellToggle routes those to the hero), everything else
   *  goes straight to UseMagic(Hero.NextMagic, Hero). `location` / `object` / `direction` are the user's mouse cell, mouse
   *  object and MouseDirection(), as on the desktop. */
  useHeroSpell(key: number, location: WirePoint, object: MapObj | null, direction: number): void {
    const hero = this.host.hero;
    const ob = this.host.heroObject?.() ?? null;
    const now = this.host.now();
    if (!hero || !ob || !this.heroId || now < this.heroSpellTime) return; // HeroObject == null || CMain.Time < HeroSpellTime
    this.heroSpellTime = now + 200;
    if (hero.dead || ob.dead || ob.ridingMount || ob.fishing) return;
    if (now < ob.blizzardStopTime || now < ob.reincarnationStopTime) return;
    const magic = this.heroMagics.find((m) => m.Key === key);
    if (!magic) return;
    if (magic.Spell === Spell.CounterAttack) {
      if (now < magic.castTime + magic.delay) {
        this.say(`You cannot cast ${magic.Name} for another ${Math.trunc((magic.castTime + magic.delay - now - 1) / 1000) + 1} seconds.`);
        return;
      }
      magic.castTime = now;
    }
    if (PASSIVE.has(magic.Spell)) return;
    const t = this.heroToggles;
    const mp = hero.info?.mp ?? 0;
    const flip = (field: ToggleField, yes: string, no: string) => {
      if (now < this.toggleTime) return;
      t[field] = !t[field];
      this.host.chat(HERO_PREFIX + (t[field] ? yes : no), ChatType.Hint);
      this.toggleTime = now + 1000;
      this.sendHeroToggle(magic.Spell);
    };
    switch (magic.Spell) {
      case Spell.Thrusting: flip("thrusting", "Use Thrusting.", "Do not use Thrusting."); return;
      case Spell.HalfMoon: flip("halfMoon", "Use Half Moon.", "Do not use Half Moon."); return;
      case Spell.CrossHalfMoon: flip("crossHalfMoon", "Use Cross Half Moon.", "Do not use Cross Half Moon."); return;
      case Spell.DoubleSlash: flip("doubleSlash", "Use Double Slash.", "Do not use Double Slash."); return;
      case Spell.TwinDrakeBlade:
      case Spell.FlamingSword:
        if (now < this.toggleTime) return;
        this.toggleTime = now + 500;
        if (this.cost(magic) > mp) { this.host.chat("Not enough mana.", ChatType.System); return; }
        if (magic.Spell === Spell.TwinDrakeBlade) t.twinDrakeBlade = true;
        this.sendHeroToggle(magic.Spell);
        return;
      case Spell.CounterAttack:
        if (this.cost(magic) > mp) { this.host.chat("Not enough mana.", ChatType.System); return; }
        this.sendHeroToggle(magic.Spell);
        return;
      case Spell.MentalState:
        if (now < this.toggleTime) return;
        this.toggleTime = now + 500;
        this.sendHeroToggle(magic.Spell);
        return;
    }
    this.useHeroMagic(hero, ob, { magic, location, object, direction });
  }

  /** True when the hero AI may cast `spell` on its own (no S.HeroAutoCast entry for it). */
  heroAutoCast(spell: number): boolean {
    return !this.heroAutoCastOff.has(spell);
  }

  /** C.HeroSpellToggle (web-only): turn the hero's auto-cast of a spell it knows on or off. The server answers with the
   *  full S.HeroAutoCast list (onHeroChange fires then); nothing changes locally until it does. */
  heroSpellToggle(spell: number, on: boolean): void {
    if (!this.heroId || !this.getHero(spell)) return;
    this.host.send(ClientPackets.HeroSpellToggle({ Spell: spell, AutoCast: on }));
  }

  /** SendSpellToggle(Hero, ...): no CanUse, so the server toggles the hero's flag (HumanObject.SpellToggle, state None). */
  private sendHeroToggle(spell: number): void {
    this.host.send(ClientPackets.SpellToggle({ Spell: spell, canUse: SpellToggleState.None }));
  }

  /** MapControl.UseMagic(magic, actor = Hero): the same checks and targeting as the user's, then C.Magic with the hero's
   *  ObjectID at once (the hero has no client-side action queue). Its cooldown starts on the hero's ObjectMagic (Cast). */
  private useHeroMagic(hero: Hero, ob: MapObj, next: NextMagic): void {
    const { magic } = next;
    const now = this.host.now();
    if (now < this.spellTime || (ob.poison & PoisonType.Stun)) return; // GameScene.SpellTime is shared with the user
    if (now <= magic.castTime + magic.delay) {
      this.say(`You cannot cast ${magic.Name} for another ${Math.trunc((magic.castTime + magic.delay - now - 1) / 1000) + 1} seconds.`);
      return;
    }
    if (this.cost(magic) > (hero.info?.mp ?? 0)) { this.say("Not enough mana."); return; }

    const user = this.host.user();
    const valid = (o: MapObj | null) => !!o && !o.dead && o.race !== ObjectType.Item && o.race !== ObjectType.Merchant;
    let target: MapObj | null = null;
    const s = magic.Spell;
    if (ENEMY_TARGET.has(s) || BOW_TARGET.has(s)) {
      if (valid(next.object)) target = next.object;
      if (!target) target = this.host.magicObject();
      if (target && target.race === ObjectType.Monster) this.host.setMagicObject(target.id);
    } else if (FRIEND_TARGET.has(s)) {
      if (valid(next.object)) target = next.object;
      if (!target) target = user; // the desktop defaults to the user, not the hero
    } else if (OPTIONAL_TARGET.has(s)) {
      if (valid(next.object)) target = next.object;
    } else if (s === Spell.Reincarnation) {
      const o = next.object ?? user; // actor == Hero && NextMagicObject == null: revive the user
      if (o && o.dead && o.race === ObjectType.Player) target = o;
    }

    let dir = !target || target === user ? next.direction : directionOf(ob.location, target.location);
    const location = target ? { ...target.location } : next.location;
    if (s === Spell.FlashDash) dir = ob.direction;
    if (magic.Range !== 0 && Math.max(Math.abs(ob.location.X - location.X), Math.abs(ob.location.Y - location.Y)) > magic.Range) {
      this.say("Target is too far.");
      return;
    }
    this.host.send(ClientPackets.Magic({ ObjectID: this.heroId, Spell: s, Direction: dir, TargetID: target?.id ?? 0, Location: location, SpellTargetLock: false }));
    this.casted(); // MapControl.UseMagic: GameScene.LogTime is set before the actor == User branch, so hero casts lock log-out too
  }

  /** AssignKeyPanel.SaveButton with Actor = User (keys 1-16, 0 = none): one key per spell, so the key is taken from
   *  whichever spell had it; C.MagicKey, then the new key is set at once (the server sends no answer). Shared by the
   *  classic MagicBar.assign and the new UI's Skills window; the subscribe() listeners re-render both. */
  assignKey(m: UserMagic, key: number): void {
    for (const other of this.magics) if (other.Key === key) other.Key = 0;
    this.host.send(ClientPackets.MagicKey({ Spell: m.Spell, Key: key, OldKey: m.Key }));
    m.Key = key;
    this.changed();
  }

  /** AssignKeyPanel with Actor = Hero (keys 17-24): one key per spell, sent as C.MagicKey (the server picks the hero for keys > 16). */
  assignHeroKey(m: UserMagic, key: number): void {
    if (!this.heroId || this.host.hero?.dead) return; // MagicButton: GameScene.Hero == null || Hero.Dead
    for (const other of this.heroMagics) if (other !== m && other.Key === key && key !== 0) other.Key = 0;
    this.host.send(ClientPackets.MagicKey({ Spell: m.Spell, Key: key, OldKey: m.Key }));
    m.Key = key;
    this.heroChanged();
  }

  private say(text: string): void {
    const now = this.host.now();
    if (now < this.outputDelay) return;
    this.outputDelay = now + 1000;
    this.host.chat(text, ChatType.System);
  }

  /** MapControl.UseMagic: queues the Spell action on the user (its packet goes out when the action starts). */
  useNext(user: MapObj): void {
    const next = this.next;
    this.next = null;
    if (!next) return;
    const { magic } = next;
    const now = this.host.now();
    // GameScene.UseMagic: no new spell while Blizzard / Reincarnation is still being cast
    if (now < user.blizzardStopTime || now < user.reincarnationStopTime) return;
    if (now < this.spellTime) return;
    if (now <= magic.castTime + magic.delay) {
      this.say(`You cannot cast ${magic.Name} for another ${Math.trunc((magic.castTime + magic.delay - now - 1) / 1000) + 1} seconds.`);
      return;
    }
    if (this.cost(magic) > this.host.mp()) { this.say("Not enough mana."); return; }

    const valid = (ob: MapObj | null) => !!ob && !ob.dead && ob.race !== ObjectType.Item && ob.race !== ObjectType.Merchant;
    let target: MapObj | null = null;
    const s = magic.Spell;
    if (ENEMY_TARGET.has(s) || BOW_TARGET.has(s)) {
      if (valid(next.object)) target = next.object;
      if (!target) target = this.host.magicObject();
      if (target && target.race === ObjectType.Monster) this.host.setMagicObject(target.id);
    } else if (FRIEND_TARGET.has(s)) {
      // also refuses wild monsters (MonsterObject.MasterObjectId == 0): not tracked here, the server decides
      if (valid(next.object)) target = next.object;
      if (!target) target = user;
    } else if (OPTIONAL_TARGET.has(s)) {
      if (valid(next.object)) target = next.object;
    } else if (s === Spell.Reincarnation) {
      if (next.object && next.object.dead && next.object.race === ObjectType.Player) target = next.object;
    }

    let dir = !target || target === user ? next.direction : directionOf(user.location, target.location);
    const location = target ? { ...target.location } : next.location;
    if (s === Spell.FlashDash) dir = user.direction;
    if (magic.Range !== 0 && Math.max(Math.abs(user.location.X - location.X), Math.abs(user.location.Y - location.Y)) > magic.Range) {
      this.say("Target is too far.");
      return;
    }
    user.queue(A.Spell, dir, user.location, { spell: s, targetId: target?.id ?? 0, targetPoint: location, cast: false, level: magic.Level, secondaryTargetIds: [] });
  }

  /** PlayerObject.SetAction for the user's Spell action: send C.Magic, set SpellTime. Returns the NextAction delay. */
  sendCast(user: MapObj, spell: number, targetId: number, location: WirePoint, direction: number): number {
    this.host.send(ClientPackets.Magic({ ObjectID: user.id, Spell: spell, Direction: direction, TargetID: targetId, Location: location, SpellTargetLock: false }));
    this.casted();
    const now = this.host.now();
    if (spell === Spell.FlashDash) { this.spellTime = now + 250; return 0; }
    this.spellTime = now + (spell === Spell.FlameField ? 2500 : 1800);
    return 2500;
  }

  /** PlayerObject.SetAction's Attack1 spell choice (Slaying / Thrusting / HalfMoon / CrossHalfMoon / DoubleSlash / TwinDrakeBlade / FlamingSword). */
  attackSpell(hasTarget: boolean, thrustTarget: boolean, canHalfMoon: boolean, canCrossHalfMoon: boolean): number {
    let spell: number = Spell.None;
    const mp = this.host.mp();
    const affordable = (sp: number) => { const m = this.get(sp); return !!m && m.BaseCost + m.LevelCost * m.Level <= mp; };
    if (this.slaying && hasTarget) spell = Spell.Slaying;
    if (this.thrusting && thrustTarget) spell = Spell.Thrusting;
    if (this.halfMoon && (hasTarget || canHalfMoon) && affordable(Spell.HalfMoon)) spell = Spell.HalfMoon;
    if (this.crossHalfMoon && (hasTarget || canCrossHalfMoon) && affordable(Spell.CrossHalfMoon)) spell = Spell.CrossHalfMoon;
    if (this.doubleSlash && affordable(Spell.DoubleSlash)) spell = Spell.DoubleSlash;
    if (this.twinDrakeBlade && hasTarget && affordable(Spell.TwinDrakeBlade)) spell = Spell.TwinDrakeBlade;
    if (this.flamingSword && hasTarget) { const m = this.get(Spell.FlamingSword); if (m) { spell = Spell.FlamingSword; m.castTime = this.host.now(); } }
    if (spell === Spell.Slaying) this.slaying = false;
    if (spell === Spell.TwinDrakeBlade) this.twinDrakeBlade = false;
    if (spell === Spell.FlamingSword) this.flamingSword = false;
    return spell;
  }
}

function directionOf(s: WirePoint, d: WirePoint): number {
  if (s.X < d.X) return s.Y < d.Y ? 3 : s.Y > d.Y ? 1 : 2;
  if (s.X > d.X) return s.Y < d.Y ? 5 : s.Y > d.Y ? 7 : 6;
  return s.Y < d.Y ? 4 : 0;
}

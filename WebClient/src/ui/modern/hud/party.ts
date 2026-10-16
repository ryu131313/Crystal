// Ported from play/hudGroup.ts (its on-screen party frames, GroupMock.renderFrames): one frame per other group member
// down the left of the screen, over the GroupState model (src/game/group.ts; GroupDialog.GroupList, GroupMembersMap,
// BigMapViewPort.PlayerLocations) and S.GroupMemberStatus (web-only: class, level, HP % / MP %, buffs, at most once a
// second per member). The leader (GroupList[0]) wears a crown. Where a member has sent no status yet, a member in view
// falls back to its MapObj (class, level, the S.ObjectHealth % while unexpired); without either the bars stay empty.
// The second line shows the member's map when it is not the user's (S.GroupMembersMap), else the distance and
// direction (S.SendMemberLocation). Hidden while the user is not grouped. A click opens the "group" window. The mock's
// HP / MP drift simulation is gone; frames rebuild only when the roster changes and write a bar / text only when it
// changed.

import { ObjectType } from "../../../protocol/enums.generated.ts";
import type { MapObj } from "../../../game/objects.ts";
import { buffIcon } from "../../../game/buffs.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { libPicture } from "../../mir/controls.ts";
import { h, injectCss, rafCoalesce, setText, setStyle } from "../core/dom.ts";
import { svg } from "../core/icons.ts";
import { classTag, className } from "../core/format.ts";
import { classVar } from "../core/styles.ts";
import type { UiRuntime } from "../core/runtime.ts";

const CROWN = "M3 8l4 4 5-7 5 7 4-4-2 11H5z";
const DIRS = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
const MAX_BUFFS = 6;

const CSS = `
.hudm .party { position: absolute; z-index: 2; left: 12px; top: 12px; display: flex; flex-direction: column; gap: 5px; pointer-events: none; }
.hudm .pf { position: relative; display: flex; align-items: center; gap: 7px; width: 186px; padding: 4px 7px 4px 4px; cursor: pointer; border-radius: var(--rad); pointer-events: auto;
  background: linear-gradient(90deg, color-mix(in srgb, var(--pb) 85%, transparent), color-mix(in srgb, var(--pb) 40%, transparent)); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  box-shadow: 0 0 0 1px rgba(0,0,0,.6); }
.hudm .pf:hover { border-color: var(--ac); }
.hudm .pf.dead { opacity: .55; }
.hudm .pf .badge { width: 26px; height: 26px; font-size: 8px; position: relative; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: 800;
  color: var(--pcls, var(--tx)); background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid color-mix(in srgb, var(--pcls, var(--fr)) 60%, var(--fr)); }
.hudm .pf .crown { position: absolute; top: -8px; left: 50%; transform: translateX(-50%); color: #ffd75a; display: flex; }
.hudm .pf .crown svg.ic { width: 12px; height: 12px; fill: currentColor; stroke: #000; stroke-width: 1; }
.hudm .pf .in2 { flex: 1; min-width: 0; }
.hudm .pf .n { display: flex; justify-content: space-between; gap: 6px; font-size: 10px; font-weight: 700; color: #fff; text-shadow: 0 1px 1px #000; }
.hudm .pf .n > span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .pf .n small { flex: none; font-weight: 600; color: var(--dim); max-width: 90px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .pf .n small.far { color: #ffb08a; }
.hudm .pf .b { position: relative; height: 5px; margin-top: 2px; border-radius: 2px; background: #04060a; overflow: hidden; }
.hudm .pf .b i { position: absolute; inset: 0; transform-origin: 0 50%; transform: scaleX(0); transition: transform .4s; }
.hudm .pf .b.hp i { background: linear-gradient(90deg, #6a0c14, #d0343e); } .hudm .pf .b.mp i { background: linear-gradient(90deg, #12306e, #3a7ee0); }
.hudm .pf.nobars .b i { transform: scaleX(0) !important; }
.hudm .pf .bf { display: flex; gap: 2px; margin-top: 2px; min-height: 0; }
.hudm .pf .bf:empty { display: none; }
.hudm .pf .bf i { width: 12px; height: 12px; border-radius: 2px; background: #000 center / cover no-repeat; box-shadow: 0 0 0 1px #000; }
.hudm .pf.low .b.hp { box-shadow: 0 0 6px rgba(208, 52, 62, .8); }
`;

interface Frame {
  readonly name: string;
  readonly el: HTMLElement; readonly badge: HTMLElement; readonly tag: HTMLElement; readonly where: HTMLElement;
  readonly hp: HTMLElement; readonly mp: HTMLElement; readonly buffs: HTMLElement;
  buffKey: string; cls: number;
}

export class PartyFrames {
  readonly el: HTMLElement;
  private frames: Frame[] = [];
  private rosterKey = "";
  private readonly refresh: () => void;
  private readonly poll: { on(): void; off(): void };

  constructor(private readonly rt: UiRuntime, private readonly ctx: GameUiContext) {
    injectCss("hudm-party-css", CSS);
    this.el = h("div", "party");
    this.el.hidden = true;
    rt.layers.wins.append(this.el);
    this.refresh = rafCoalesce(() => this.render(), rt.signal);
    // Members in view without a status (HP % from S.ObjectHealth) and the user's own moves (distances): 2 Hz while grouped.
    this.poll = rt.ticker.every(500, () => this.update());
    const g = ctx.models.group;
    const offs = [g.changed.on(this.refresh), g.statusChanged.on(this.refresh)];
    rt.signal.addEventListener("abort", () => { for (const off of offs) off(); this.poll.off(); }, { once: true });
    this.render();
  }

  dispose(): void {
    this.poll.off();
    this.el.remove();
  }

  private me(): string { return this.ctx.player.user?.name ?? ""; }

  private render(): void {
    const list = this.ctx.models.group.list;
    const me = this.me();
    const grouped = list.length > 1;
    this.el.hidden = !grouped;
    if (!grouped) { this.poll.off(); this.rosterKey = ""; this.frames = []; this.el.replaceChildren(); return; }
    const key = list.join("\u0001");
    if (key !== this.rosterKey) {
      this.rosterKey = key;
      this.frames = list.filter((n) => n !== me).map((n) => this.build(n, n === list[0]));
      this.el.replaceChildren(...this.frames.map((f) => f.el));
    }
    this.poll.on();
    this.update();
  }

  private build(name: string, leader: boolean): Frame {
    const tag = h("span");
    const badge = h("div", "badge", tag);
    if (leader) { const c = h("div", "crown", svg(CROWN)); c.title = "Leader"; badge.append(c); }
    const where = h("small");
    const hp = h("i"), mp = h("i"), buffs = h("div", "bf");
    const el = h("div", "pf", badge, h("div", "in2", h("div", "n", h("span", "", name), where), h("div", "b hp", hp), h("div", "b mp", mp), buffs));
    el.setAttribute("role", "button");
    el.addEventListener("click", () => this.rt.toggleWindow("group", true), { signal: this.rt.signal });
    return { name, el, badge, tag, where, hp, mp, buffs, buffKey: "", cls: -1 };
  }

  private inView(name: string): MapObj | null {
    for (const ob of this.ctx.world.objects.values()) if (ob.race === ObjectType.Player && ob.name === name) return ob;
    return null;
  }

  /** Writes what changed: class badge, level / place, bars, buffs. */
  private update(): void {
    const g = this.ctx.models.group;
    const user = this.ctx.player.user;
    const myMap = this.ctx.world.mapTitle;
    for (const f of this.frames) {
      const st = g.memberStatus.get(f.name);
      const ob = st ? null : this.inView(f.name);
      const cls = st ? st.cls : ob ? ob.look.class : -1;
      const level = st ? st.level : ob?.level ?? 0;
      const hpPct = st ? st.hpPercent : ob && ob.healthPercent >= 0 ? ob.healthPercent : -1;
      const mpPct = st ? st.mpPercent : -1;
      if (cls !== f.cls) {
        f.cls = cls;
        setText(f.tag, cls >= 0 ? classTag(cls) : "?");
        if (cls >= 0) f.badge.style.setProperty("--pcls", classVar(cls)); else f.badge.style.removeProperty("--pcls");
      }
      // Where: another map's name, else distance and direction on this one.
      const map = g.memberMaps.get(f.name);
      const loc = g.locations.get(f.name);
      let text = level ? `Lv ${level}` : "", far = false;
      if (map && map !== myMap) { text = map; far = true; }
      else if (loc && user) {
        const dx = loc.X - user.location.X, dy = loc.Y - user.location.Y, d = Math.max(Math.abs(dx), Math.abs(dy));
        if (d > 0) text = `${level ? `Lv ${level} · ` : ""}${d} ${DIRS[(Math.round(Math.atan2(dx, -dy) / (Math.PI / 4)) + 8) % 8]}`;
      }
      setText(f.where, text);
      f.where.classList.toggle("far", far);
      const dead = (ob?.dead ?? false) || hpPct === 0;
      f.el.classList.toggle("dead", dead);
      f.el.classList.toggle("nobars", hpPct < 0);
      f.el.classList.toggle("low", hpPct >= 0 && hpPct < 30 && !dead);
      setStyle(f.hp, "transform", `scaleX(${(Math.max(0, hpPct) / 100).toFixed(3)})`);
      setStyle(f.mp, "transform", `scaleX(${(Math.max(0, mpPct) / 100).toFixed(3)})`);
      const title = `${f.name}${cls >= 0 ? ` · ${className(cls)}` : ""}${level ? ` ${level}` : ""}${text && far ? ` · ${text}` : ""}\nClick to open the group`;
      if (f.el.title !== title) f.el.title = title;
      const buffs = st ? st.buffs.slice(0, MAX_BUFFS) : [];
      const bk = buffs.join(",");
      if (bk !== f.buffKey) {
        f.buffKey = bk;
        f.buffs.replaceChildren(...buffs.map((b) => {
          const i = h("i");
          const [lib, idx] = buffIcon(b);
          void libPicture(lib, idx).then((p) => { if (p) i.style.backgroundImage = `url(${p.url})`; });
          return i;
        }));
      }
    }
  }
}

// The user's guild: GuildDialog's packet-driven state (Client/MirScenes/Dialogs/GuildDialog.cs: GuildName / GuildRankName,
// Level, Experience, Gold, MemberCount, MaxMembers, MyOptions, MyRankId, Ranks, SparePoints, GuildBuffInfos,
// EnabledBuffs, Notice, the NoticeChanged / MembersChanged request throttles) + GameScene's guild handlers (GuildStatus,
// GuildInvite, GuildNameRequest, GuildNoticeChange, GuildMemberChange, GuildExpGain, GuildBuffList, GuildRequestWar,
// GuildStorageGoldChange). The guild storage items stay in Inventory. game.ts feeds handle(); the model reports the
// user's guild name (UserObject.GuildName) and the summed active guild buff stats (GameScene.GuildBuffList's Guild buff)
// through constructor callbacks. The UI follows `view` (which part of GuildDialog to redraw, in the desktop's order),
// `changed` (last, after every guild packet) and answers the prompts `invite`, `nameRequest` and `warRequest`.

import type { ServerPacket, GuildRank, GuildBuff, GuildBuffInfo } from "../protocol/server.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { ClientPackets } from "../protocol/client.ts";
import { Emitter } from "./emitter.ts";

/** GuildRankOptions ([Flags] byte, Shared/Enums.cs). */
export const GuildRankOptions = {
  CanChangeRank: 1, CanRecruit: 2, CanKick: 4, CanStoreItem: 8, CanRetrieveItem: 16, CanAlterAlliance: 32, CanChangeNotice: 64, CanActivateBuff: 128,
} as const;

/** Which GuildDialog parts a guild packet changed (the window redraws them in this order, then on `changed`). */
export type GuildView =
  /** GameScene.GuildStatus (StatusChanged; `left` = no guild any more: the dialog hides, the notice and ranks clear). */
  | { kind: "status"; left: boolean }
  /** GuildDialog.NoticeChange: `notice` holds the new lines. */
  | { kind: "notice" }
  /** GuildDialog.RefreshMemberList: the rank list was replaced, grew or a rank was renamed (Ranks[i].Index = i done). */
  | { kind: "ranks" }
  /** GuildDialog.UpdateMembers: online flags or the user's own rank moved. */
  | { kind: "members" }
  /** GuildDialog.RankChangeRecieved: UpdateMembers + UpdateRanks. */
  | { kind: "rank" };

export interface GuildDeps {
  send(f: Uint8Array): void;
  chat(text: string, type: number): void;
  /** The user's name (GameScene.User.Name). */
  me(): string;
  /** CMain.Time (performance.now()). */
  now(): number;
  /** UserObject.GuildName / GuildRankName (the user's name label; ObjectGuildNameChanged skips the user). */
  setUserGuild(name: string, rank: string): void;
  /** GameScene.GuildBuffList: the summed stats of the active guild buffs, or null to drop the Guild buff. */
  setGuildBuff(stats: Record<number, number> | null): void;
  /** The scene scope: clears the member-request retry timer. */
  signal?: AbortSignal;
}

export class GuildState {
  private _name = "";
  private _rankName = "";
  private _level = 0;
  private _experience = 0n;
  private _maxExperience = 0n;
  private _gold = 0;
  private _memberCount = 0;
  private _maxMembers = 0;
  private _myOptions = 0;
  private _myRankId = 0;
  private _ranks: GuildRank[] = [];
  private _sparePoints = 0;
  private _buffInfos: GuildBuffInfo[] = [];
  private _enabledBuffs: GuildBuff[] = [];
  private _notice: string[] = [];
  private requestedBuffList = false;
  /** GuildDialog.NoticeChanged / MembersChanged: ask again (RequestGuildInfo) when the page is shown. */
  private noticeChanged = true;
  private membersChanged = true;
  private lastNoticeRequest = 0;
  private lastMemberRequest = 0;
  private retry: ReturnType<typeof setTimeout> | 0 = 0;

  /** The GuildDialog parts to redraw (during handle(), before `changed`). */
  readonly view = new Emitter<GuildView>();
  /** Any guild packet was applied (last; the window re-asks for members and redraws). */
  readonly changed = new Emitter<void>();
  /** GameScene.GuildInvite: the `name` guild invites the user (MirMessageBox YesNo); answer with answerInvite(). */
  readonly invite = new Emitter<{ name: string }>();
  /** GameScene.GuildNameRequest: ask for the new guild's name (MirInputBox); answer with returnName(). */
  readonly nameRequest = new Emitter<void>();
  /** GameScene.GuildRequestWar: ask for the guild to go to war with (MirInputBox); answer with returnWar(). */
  readonly warRequest = new Emitter<void>();

  constructor(private readonly d: GuildDeps) {
    d.signal?.addEventListener("abort", () => { if (this.retry) clearTimeout(this.retry); this.retry = 0; });
  }

  /** UserObject.GuildName ("" = not in a guild) / GuildRankName. */
  get name(): string { return this._name; }
  get rankName(): string { return this._rankName; }
  get level(): number { return this._level; }
  get experience(): bigint { return this._experience; }
  get maxExperience(): bigint { return this._maxExperience; }
  /** The guild's funds. */
  get gold(): number { return this._gold; }
  get memberCount(): number { return this._memberCount; }
  get maxMembers(): number { return this._maxMembers; }
  /** GuildDialog.MyOptions (GuildRankOptions bits) / MyRankId. */
  get myOptions(): number { return this._myOptions; }
  get myRankId(): number { return this._myRankId; }
  /** GuildDialog.Ranks with their members (the member list; loaded on request). */
  get ranks(): readonly GuildRank[] { return this._ranks; }
  get sparePoints(): number { return this._sparePoints; }
  /** GuildDialog.GuildBuffInfos (the server's catalogue) / EnabledBuffs (bought; Active = running). */
  get buffInfos(): readonly GuildBuffInfo[] { return this._buffInfos; }
  get enabledBuffs(): readonly GuildBuff[] { return this._enabledBuffs; }
  /** GuildDialog.Notice lines. */
  get notice(): readonly string[] { return this._notice; }

  /** The user's rank has the option (GuildRankOptions). */
  can(option: number): boolean {
    return (this._myOptions & option) !== 0;
  }

  /** GuildDialog.FindGuildBuff. */
  findBuff(id: number): GuildBuff | undefined {
    return this._enabledBuffs.find((b) => b.Id === id);
  }

  /** GuildDialog.RequestUpdateNotice (the notice page is shown): C.RequestGuildInfo 0, 5 s throttle. */
  requestNotice(): void {
    if (this.noticeChanged && this.lastNoticeRequest < this.d.now()) {
      this.lastNoticeRequest = this.d.now() + 5000;
      this.d.send(ClientPackets.RequestGuildInfo({ Type: 0 }));
    }
  }

  /** MembersPage / RankPage BeforeDraw -> RequestUpdateMembers: asks while `shown()` (5 s throttle); a change inside the
   *  throttle window asks again when it ends (if still shown). */
  requestMembers(shown: () => boolean): void {
    if (!shown() || !this.membersChanged) return;
    const t = this.d.now();
    if (this.lastMemberRequest < t) { this.lastMemberRequest = t + 5000; this.d.send(ClientPackets.RequestGuildInfo({ Type: 1 })); return; }
    if (!this.retry && !this.d.signal?.aborted) this.retry = setTimeout(() => { this.retry = 0; this.requestMembers(shown); }, this.lastMemberRequest - t + 50);
  }

  /** GameScene.GuildInvite's Yes / No. */
  answerInvite(accept: boolean): void {
    this.d.send(ClientPackets.GuildInvite({ AcceptInvite: accept }));
  }

  /** GameScene.GuildNameRequest's input box (letters and digits only: the desktop blocks other characters while typing). */
  returnName(name: string): void {
    this.d.send(ClientPackets.GuildNameReturn({ Name: name.replace(/[^A-Za-z0-9]/g, "") }));
  }

  /** GameScene.GuildRequestWar's input box. */
  returnWar(name: string): void {
    this.d.send(ClientPackets.GuildWarReturn({ Name: name }));
  }

  /** Server packets. Never consumes (the classic chain ignored the guild window's return and went on). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "GuildStatus": this.status(p); break;
      case "GuildInvite": this.invite.emit({ name: p.Name }); break;
      case "GuildNameRequest": this.nameRequest.emit(); break;
      case "GuildNoticeChange":
        if (p.update === -1) this.noticeChanged = true;
        else {
          // GuildDialog.NoticeChange
          this._notice = p.notice;
          this.noticeChanged = false;
          this.view.emit({ kind: "notice" });
        }
        break;
      case "GuildMemberChange": this.memberChange(p); break;
      case "GuildExpGain": this._experience += BigInt(p.Amount); break;
      case "GuildBuffList": this.buffList(p); break;
      case "GuildRequestWar": this.warRequest.emit(); break;
      case "GuildStorageGoldChange":
        // GameScene.GuildStorageGoldChange
        if (p.Type === 0) this.d.chat(`${p.Name} donated ${p.Amount} gold to guild funds.`, ChatType.Guild);
        if (p.Type === 1) this.d.chat(`${p.Name} retrieved ${p.Amount} gold from guild funds.`, ChatType.Guild);
        if (p.Type === 0 || p.Type === 3) this._gold += p.Amount;
        else this._gold = this._gold > p.Amount ? this._gold - p.Amount : 0;
        break;
      default: return false;
    }
    this.changed.emit();
    return false;
  }

  /** GameScene.GuildStatus (+ GuildDialog.StatusChanged / GuildChange). */
  private status(p: Extract<ServerPacket, { type: "GuildStatus" }>): void {
    if (!this._name && p.GuildName) { this.noticeChanged = true; this.membersChanged = true; }
    // GuildChange: reset the guild buffs and drop the Guild buff
    if (this._name !== p.GuildName) { this._enabledBuffs = []; this.d.setGuildBuff(null); }
    this._sparePoints = p.SparePoints;
    this._name = p.GuildName; this._rankName = p.GuildRankName; this._level = p.Level; this._experience = p.Experience;
    this._maxExperience = p.MaxExperience; this._gold = p.Gold; this._memberCount = p.MemberCount; this._maxMembers = p.MaxMembers;
    this._myOptions = p.MyOptions; this._myRankId = p.MyRankId;
    if (!p.GuildName) { this._ranks = []; this._notice = []; }
    this.d.setUserGuild(p.GuildName, p.GuildRankName);
    this.view.emit({ kind: "status", left: !p.GuildName });
  }

  /** GuildDialog.RefreshMemberList's Ranks[i].Index = i. */
  private reindexRanks(): void {
    for (let i = 0; i < this._ranks.length; i++) this._ranks[i]!.Index = i;
  }

  /** GameScene.GuildMemberChange. */
  private memberChange(p: Extract<ServerPacket, { type: "GuildMemberChange" }>): void {
    const me = this.d.me();
    const setOnline = (online: boolean) => {
      for (const r of this._ranks) for (const m of r.Members) if (m.Name === p.Name) m.Online = online;
      this.view.emit({ kind: "members" });
    };
    switch (p.Status) {
      case 0: setOnline(false); break;
      case 1: this.d.chat(`${p.Name} logged on.`, ChatType.Guild); setOnline(true); break;
      case 2: this.d.chat(`${p.Name} joined guild.`, ChatType.Guild); this._memberCount++; this.membersChanged = true; break;
      case 3: this.d.chat(`${p.Name} got removed from the guild.`, ChatType.Guild); this._memberCount = Math.max(0, this._memberCount - 1); this.membersChanged = true; break;
      case 4: this.d.chat(`${p.Name} left the guild.`, ChatType.Guild); this._memberCount = Math.max(0, this._memberCount - 1); this.membersChanged = true; break;
      case 5: this.membersChanged = true; break;
      case 6: // GuildDialog.NewRankRecieved
        if (p.Ranks[0]) {
          this._ranks.splice(this._ranks.length > 1 ? this._ranks.length - 1 : 1, 0, p.Ranks[0]);
          this.reindexRanks();
          this.view.emit({ kind: "ranks" });
        }
        break;
      case 7: { // GuildDialog.RankChangeRecieved
        const r = p.Ranks[0];
        const old = r && this._ranks.find((x) => x.Index === r.Index);
        // The desktop only applies this when its member list is loaded (Ranks non-empty), so a member who never opened it
        // keeps stale rights; the packet carries the rank's members, so the user's own options are taken from it directly.
        if (r && (r.Members.some((m) => m.Name === me) || old?.Members.some((m) => m.Name === me))) {
          this._rankName = r.Name; this._myOptions = r.Options; this.d.setUserGuild(this._name, r.Name);
        }
        if (r && old) {
          const renamed = old.Name !== r.Name;
          this._ranks[this._ranks.indexOf(old)] = r;
          if (renamed) { this.reindexRanks(); this.view.emit({ kind: "ranks" }); }
        }
        this.view.emit({ kind: "rank" });
        break;
      }
      case 8: { // GuildDialog.MyRankChanged
        const r = p.Ranks[0];
        if (!r) break;
        this._myOptions = r.Options; this._rankName = r.Name; this.d.setUserGuild(this._name, r.Name);
        const old = this._ranks[this._myRankId];
        this._myRankId = r.Index;
        const i = old ? old.Members.findIndex((m) => m.Name === me) : -1;
        if (!old || i < 0) break;
        const [member] = old.Members.splice(i, 1);
        if (this._ranks[r.Index]) this._ranks[r.Index]!.Members.push(member!); else this.membersChanged = true;
        this.view.emit({ kind: "members" });
        break;
      }
      case 255: // GuildDialog.NewMembersList
        this._ranks = p.Ranks;
        this._memberCount = this._ranks.reduce((n, r) => n + r.Members.length, 0);
        this.membersChanged = false;
        this.reindexRanks();
        this.view.emit({ kind: "ranks" });
        break;
    }
  }

  /** GuildDialog.RequestGuildBuffList (once). */
  private requestBuffList(): void {
    if (this.requestedBuffList) return;
    this.requestedBuffList = true;
    this.d.send(ClientPackets.GuildBuffUpdate({ Action: 0, Id: 0 }));
  }

  /** GameScene.GuildBuffList + UpdateGuildBuff + the Guild buff (GuildDialog.UpdateActiveStats sums the active buffs' stats). */
  private buffList(p: Extract<ServerPacket, { type: "GuildBuffList" }>): void {
    if (p.GuildBuffs.length > 0) this._buffInfos = p.GuildBuffs;
    for (const b of p.ActiveBuffs) {
      const i = this._enabledBuffs.findIndex((x) => x.Id === b.Id);
      if (i >= 0) { if (p.Remove === 1) this._enabledBuffs.splice(i, 1); else this._enabledBuffs[i] = b; }
      else if (p.Remove !== 1) this._enabledBuffs.push(b);
    }
    // GuildDialog.FindGuildBuffInfo asks for the catalogue when an enabled buff has no info yet
    if (this._enabledBuffs.some((b) => !this._buffInfos.some((i) => i.Id === b.Id))) this.requestBuffList();
    this.refreshGuildBuff();
  }

  private refreshGuildBuff(): void {
    const active = this._enabledBuffs.filter((b) => b.Active);
    if (active.length === 0) { this.d.setGuildBuff(null); return; }
    const stats: Record<number, number> = {};
    for (const b of active) {
      const info = this._buffInfos.find((i) => i.Id === b.Id);
      if (info) for (const [k, v] of Object.entries(info.Stats)) stats[Number(k)] = (stats[Number(k)] ?? 0) + v;
    }
    this.d.setGuildBuff(stats);
  }
}

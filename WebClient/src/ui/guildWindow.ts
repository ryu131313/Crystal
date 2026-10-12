// Guild: GuildDialog's notice, member, storage and rank pages + GameScene's guild handlers (GuildStatus, GuildInvite,
// GuildNameRequest, GuildNoticeChange, GuildMemberChange, GuildExpGain, GuildStorageGoldChange, GuildBuffList,
// GuildRequestWar; the storage items are in Inventory). Toggle with G (or the Guild button).
// Ported: create (the server's S.GuildNameRequest from an NPC script, or @CREATEGUILD on a test server), recruit /
// accept, kick, change a member's rank, the member list by rank with online state, view + edit the notice, guild chat
// (the "!~" prefix the server reads), the status line, storage (items + gold) and rank editing (rename, new rank,
// rank options), buffs (buy / activate, the user's Guild buff icon) and war declaration (the NPC's S.GuildRequestWar asks
// for the enemy guild; the war itself shows only as server name colours). Not ported: territories, voting (Crystal has no alliances; CanAlterAlliance is an unused rank bit).
// Yes/No and input boxes use confirm() / prompt().

import type { ServerPacket, GuildRank, GuildBuff, GuildBuffInfo } from "../protocol/server.ts";
import { StatName } from "../protocol/enums.generated.ts";
import { libIcon } from "./buffBar.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import type { InventoryWindow } from "./inventoryWindow.ts";
import { GUILD_STORAGE_SLOTS } from "../game/inventory.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

/** GuildRankOptions ([Flags] byte, Shared/Enums.cs). */
const Opt = { CanChangeRank: 1, CanRecruit: 2, CanKick: 4, CanStoreItem: 8, CanRetrieveItem: 16, CanChangeNotice: 64, CanActivateBuff: 128 } as const;
/** GuildDialog's rank option labels, bit i = 1 << i. */
const OPTION_NAMES = ["Edit ranks", "Recruit member", "Kick member", "Store item", "Retrieve item", "Alter alliance", "Change notice", "Activate Buff"];

type Page = "main" | "storage" | "ranks" | "buffs";

export class Guild {
  /** UserObject.GuildName / GuildRankName. */
  name = "";
  rankName = "";
  level = 0;
  experience = 0n;
  maxExperience = 0n;
  gold = 0;
  memberCount = 0;
  maxMembers = 0;
  myOptions = 0;
  myRankId = 0;
  ranks: GuildRank[] = [];
  sparePoints = 0;
  /** GuildDialog.GuildBuffInfos (the server's catalogue, sent at login) / EnabledBuffs (bought; Active = running). */
  buffInfos: GuildBuffInfo[] = [];
  enabledBuffs: GuildBuff[] = [];
  private requestedBuffList = false;
  private lastBuffRequest = 0;
  notice: string[] = [];
  /** GuildDialog.NoticeChanged / MembersChanged: ask again (RequestGuildInfo) when the page is shown. */
  private noticeChanged = true;
  private membersChanged = true;
  private lastNoticeRequest = 0;
  private lastMemberRequest = 0;
  /** GuildDialog.LastGuildMsg: one recruit / kick / rank change per 5 s. */
  private lastGuildMsg = 0;
  private editing = false;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly me: () => string;
  private readonly now: () => number;
  private readonly root = el("div", { class: "window guild-window", hidden: true });
  private readonly title = el("span", {});
  private readonly status = el("div", { class: "magic-meta guild-status" });
  private readonly noticeView = el("div", { class: "guild-notice" });
  private readonly noticeEdit = el("textarea", { class: "guild-notice-edit", rows: 8, hidden: true }) as HTMLTextAreaElement;
  private readonly noticeButton = el("button", {});
  private readonly members = el("div", { class: "guild-members" });
  private readonly recruitButton = el("button", { onclick: () => this.recruit() }, "Recruit");
  private page: Page = "main";
  private readonly tabs = { main: el("button", {}, "Notice & members"), storage: el("button", {}, "Storage"), ranks: el("button", {}, "Ranks"), buffs: el("button", {}, "Buffs") };
  private readonly buffsPage = el("div", { hidden: true });
  private readonly buffPoints = el("div", { class: "magic-meta" });
  private readonly buffList = el("div", { class: "guild-buffs" });
  /** The user's Guild buff (GameScene.GuildBuffList adds / removes BuffType.Guild): summed active stats, or null to remove. */
  private readonly setGuildBuff: (stats: Record<number, number> | null) => void;
  private readonly mainPage = el("div", {});
  private readonly storagePage = el("div", { hidden: true });
  private readonly storageGrid = el("div", { class: "inv-grid guild-storage" });
  private readonly storageGold = el("span", { class: "trade-gold" });
  private readonly retrieveGoldButton = el("button", { onclick: () => this.storageGoldClick(1) }, "Retrieve");
  private readonly ranksPage = el("div", { hidden: true });
  private readonly rankSelect = el("select", {}) as HTMLSelectElement;
  private readonly rankNameInput = el("input", { maxLength: 20 }) as HTMLInputElement;
  private readonly rankRename = el("button", { onclick: () => this.renameRank() }, "Rename");
  private readonly rankOptions = el("div", { class: "guild-rank-options" });
  /** RanksSelectBox.SelectedIndex. */
  private selectedRank = -1;
  private readonly invWindow: InventoryWindow;
  private readonly userGold: () => number;

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, me: () => string, now: () => number,
    guildChat: () => void, invWindow: InventoryWindow, userGold: () => number, setGuildBuff: (stats: Record<number, number> | null) => void) {
    this.setGuildBuff = setGuildBuff;
    this.buffsPage.append(this.buffPoints, this.buffList);
    this.send = send;
    this.chat = chat;
    this.me = me;
    this.now = now;
    this.invWindow = invWindow;
    this.userGold = userGold;
    invWindow.guildOptions = () => this.myOptions;
    invWindow.deny = (text) => this.chat(text, ChatType.System);
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); if (this.page === "storage") this.renderStorage(); };
    this.noticeButton.onclick = () => this.noticeEditClick();
    for (const p of Object.keys(this.tabs) as Page[]) this.tabs[p].onclick = () => this.showPage(p);
    this.mainPage.append(
      el("div", { class: "inv-label" }, "Notice"), this.noticeView, this.noticeEdit,
      el("div", { class: "inv-label" }, "Members"), this.members,
      el("div", { class: "row" }, this.noticeButton, this.recruitButton, el("button", { title: "Prefill !~ in the chat box", onclick: guildChat }, "Guild chat")),
    );
    this.storagePage.append(this.storageGrid,
      el("div", { class: "row" }, this.storageGold, el("button", { onclick: () => this.storageGoldClick(0) }, "Deposit"), this.retrieveGoldButton));
    this.rankSelect.onchange = () => this.selectRank(Number(this.rankSelect.value));
    this.ranksPage.append(this.rankSelect, el("div", { class: "row" }, this.rankNameInput, this.rankRename), this.rankOptions);
    this.root.append(
      el("div", { class: "window-title" }, this.title, el("button", { class: "window-close", onclick: () => this.toggle(false) }, "×")),
      this.status,
      el("div", { class: "row guild-tabs" }, this.tabs.main, this.tabs.storage, this.tabs.ranks, this.tabs.buffs),
      this.mainPage, this.storagePage, this.ranksPage, this.buffsPage,
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.root.addEventListener("keydown", (e) => e.stopPropagation());
    stage.append(this.root);
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (matches(e, KeybindOptions.Guilds) && !(a instanceof HTMLInputElement) && !(a instanceof HTMLTextAreaElement)) this.toggle(); // G without Ctrl
    }, { signal: gameSignal() });
  }

  toggle(show = this.root.hidden): void {
    // GuildDialog.Show: not in a guild -> message box instead
    if (show && !this.name) { this.chat("You are not in a guild.", ChatType.System); return; }
    this.root.hidden = !show;
    if (show) this.requestUpdates();
    this.render();
  }

  /** GuildDialog.RequestUpdateNotice / RequestUpdateMembers (5 s throttle each). */
  private requestUpdates(): void {
    if (this.root.hidden) return;
    const t = this.now();
    if (this.noticeChanged && this.lastNoticeRequest < t) { this.lastNoticeRequest = t + 5000; this.send(ClientPackets.RequestGuildInfo({ Type: 0 })); }
    if (this.membersChanged && this.lastMemberRequest < t) { this.lastMemberRequest = t + 5000; this.send(ClientPackets.RequestGuildInfo({ Type: 1 })); }
    // The desktop re-asks on its next page show; here a change inside the throttle window re-asks when it ends.
    const wait = Math.max(this.noticeChanged ? this.lastNoticeRequest : 0, this.membersChanged ? this.lastMemberRequest : 0) - t;
    if (wait > 0 && !this.retry) this.retry = setTimeout(() => { this.retry = 0; this.requestUpdates(); }, wait + 50);
  }

  private retry: ReturnType<typeof setTimeout> | 0 = 0;

  /** GuildDialog.LeftDialog: the storage page asks for the list (C.GuildStorageItemChange Type 3; the server answers once per login). */
  private showPage(page: Page): void {
    this.page = page;
    if (page === "storage") this.send(ClientPackets.GuildStorageItemChange({ Type: 3, From: 0, To: 0 }));
    if (page === "ranks") this.membersChanged = true;
    this.requestUpdates();
    this.render();
  }

  /** GuildDialog.StorageAddGold (type 0, up to GameScene.Gold) / StorageRemoveGold (type 1, up to the guild's gold). */
  private storageGoldClick(type: 0 | 1): void {
    if (this.lastGuildMsg > this.now()) return;
    const max = type === 0 ? this.userGold() : this.gold;
    if (max <= 0) return;
    const text = prompt(`${type === 0 ? "Deposit" : "Gold to retrieve:"} (max ${max.toLocaleString()})`, String(max));
    if (text === null) return;
    const amount = Math.min(max, Math.trunc(Number(text.replace(/[,\s]/g, ""))));
    if (!(amount > 0)) return;
    this.lastGuildMsg = this.now() + 100;
    this.send(ClientPackets.GuildStorageGoldChange({ Type: type, Amount: amount }));
  }

  /** GuildDialog.OnRankSelect: the last entry is "Add New" (C.EditGuildMember ChangeType 4 after a Yes/No). */
  private selectRank(index: number): void {
    if (index < this.ranks.length) { this.selectedRank = index; this.render(); return; }
    this.render();
    if (this.ranks.length >= 255 || this.lastGuildMsg > this.now()) return;
    if (!confirm("Are you sure you want to create a new rank?") || this.throttled()) return;
    this.send(ClientPackets.EditGuildMember({ ChangeType: 4, RankIndex: 0, Name: "", RankName: `Rank-${this.ranks.length - 1}` }));
  }

  /** GuildDialog.RanksChangeName (ChangeType 3). */
  private renameRank(): void {
    const rank = this.ranks[this.selectedRank];
    const name = this.rankNameInput.value.trim();
    if (!rank || !name || rank.Index < this.myRankId) return;
    this.send(ClientPackets.EditGuildMember({ ChangeType: 3, RankIndex: rank.Index, Name: "", RankName: name }));
  }

  /** GuildDialog.SwitchRankOption (ChangeType 5): Name "true" sets the bit, "false" clears it; 300 ms between clicks. */
  private switchRankOption(bit: number): void {
    const rank = this.ranks[this.selectedRank];
    if (!rank || rank.Index < this.myRankId || this.lastGuildMsg > this.now()) { this.render(); return; }
    this.send(ClientPackets.EditGuildMember({ ChangeType: 5, RankIndex: rank.Index, Name: (rank.Options & (1 << bit)) ? "false" : "true", RankName: String(bit) }));
    this.lastGuildMsg = this.now() + 300;
  }

  /** GuildDialog.RequestBuff: buy a buff not owned yet (points, guild level, rank), else activate a timed one (guild gold). */
  private requestBuff(info: GuildBuffInfo): void {
    const buff = this.enabledBuffs.find((b) => b.Id === info.Id);
    let error = "";
    if (!buff) {
      if (this.sparePoints < info.PointsRequirement) error = "Insufficient points available.";
      if (this.level < info.LevelRequirement) error = "Guild level too low.";
    } else {
      if (buff.Active) error = "Buff is still active.";
      if (this.gold < info.ActivationCost) error = "Insufficient guild funds.";
    }
    if (!this.can(Opt.CanActivateBuff)) error = "Guild rank does not allow buff activation.";
    if (error) { alert(error); return; }
    if (this.now() < this.lastBuffRequest + 100) return;
    this.lastBuffRequest = this.now();
    this.send(ClientPackets.GuildBuffUpdate({ Action: buff ? 2 : 1, Id: info.Id }));
  }

  /** GuildDialog.RequestGuildBuffList (once). */
  private requestBuffList(): void {
    if (this.requestedBuffList) return;
    this.requestedBuffList = true;
    this.send(ClientPackets.GuildBuffUpdate({ Action: 0, Id: 0 }));
  }

  /** GameScene.GuildBuffList + UpdateGuildBuff + the Guild buff icon (GuildDialog.UpdateActiveStats sums the active buffs' stats). */
  private buffList_(p: Extract<ServerPacket, { type: "GuildBuffList" }>): void {
    if (p.GuildBuffs.length > 0) this.buffInfos = p.GuildBuffs;
    for (const b of p.ActiveBuffs) {
      const i = this.enabledBuffs.findIndex((x) => x.Id === b.Id);
      if (i >= 0) { if (p.Remove === 1) this.enabledBuffs.splice(i, 1); else this.enabledBuffs[i] = b; }
      else if (p.Remove !== 1) this.enabledBuffs.push(b);
    }
    // GuildDialog.FindGuildBuffInfo asks for the catalogue when an enabled buff has no info yet
    if (this.enabledBuffs.some((b) => !this.buffInfos.some((i) => i.Id === b.Id))) this.requestBuffList();
    this.refreshGuildBuff();
  }

  private refreshGuildBuff(): void {
    const active = this.enabledBuffs.filter((b) => b.Active);
    if (active.length === 0) { this.setGuildBuff(null); return; }
    const stats: Record<number, number> = {};
    for (const b of active) {
      const info = this.buffInfos.find((i) => i.Id === b.Id);
      if (info) for (const [k, v] of Object.entries(info.Stats)) stats[Number(k)] = (stats[Number(k)] ?? 0) + v;
    }
    this.setGuildBuff(stats);
  }

  private can(option: number): boolean {
    return (this.myOptions & option) !== 0;
  }

  private throttled(): boolean {
    if (this.lastGuildMsg > this.now()) return true;
    this.lastGuildMsg = this.now() + 5000;
    return false;
  }

  /** GuildDialog.AddMember. */
  private recruit(): void {
    if (!this.can(Opt.CanRecruit)) return;
    const name = prompt("Name of the player to recruit:");
    if (!name?.trim() || this.throttled()) return;
    this.send(ClientPackets.EditGuildMember({ ChangeType: 0, RankIndex: 0, Name: name.trim(), RankName: "" }));
  }

  /** GuildDialog.DeleteMember. */
  private kick(name: string): void {
    if (name === this.me() || this.lastGuildMsg > this.now()) return;
    if (!confirm(`Are you sure you want to kick ${name}?`) || this.throttled()) return;
    this.send(ClientPackets.EditGuildMember({ ChangeType: 1, RankIndex: 0, Name: name, RankName: "" }));
  }

  /** GuildDialog.OnNewRank (the rank combo box; MinimumOption = MyRankId: you cannot promote above your own rank). */
  private changeRank(name: string, index: number): void {
    const rank = this.ranks[index];
    if (!rank || this.lastGuildMsg > this.now()) return;
    if (!confirm(`Are you sure you want to change ${name}'s rank to ${rank.Name}?`) || this.throttled()) { this.render(); return; }
    this.send(ClientPackets.EditGuildMember({ ChangeType: 2, RankIndex: rank.Index, Name: name, RankName: rank.Name }));
  }

  /** GuildDialog.EditNotice: edit, then save sends C.EditGuildNotice with the lines. */
  private noticeEditClick(): void {
    if (!this.can(Opt.CanChangeNotice)) return;
    if (!this.editing) {
      this.editing = true;
      this.noticeEdit.value = this.notice.join("\n");
      this.render();
      this.noticeEdit.focus();
      return;
    }
    this.editing = false;
    this.send(ClientPackets.EditGuildNotice({ notice: this.noticeEdit.value.split(/\r?\n/) }));
    this.render();
  }

  /** Server packets; returns true when handled. `setUserGuild` updates the user's label (UserObject.GuildName). */
  handle(p: ServerPacket, setUserGuild: (name: string, rank: string) => void): boolean {
    switch (p.type) {
      case "GuildStatus":
        // GameScene.GuildStatus
        if (!this.name && p.GuildName) { this.noticeChanged = true; this.membersChanged = true; }
        if (!p.GuildName) { this.root.hidden = true; this.ranks = []; this.notice = []; }
        // GuildChange: reset the guild buffs and drop the Guild buff
        if (this.name !== p.GuildName) { this.enabledBuffs = []; this.setGuildBuff(null); }
        this.sparePoints = p.SparePoints;
        this.name = p.GuildName; this.rankName = p.GuildRankName; this.level = p.Level; this.experience = p.Experience;
        this.maxExperience = p.MaxExperience; this.gold = p.Gold; this.memberCount = p.MemberCount; this.maxMembers = p.MaxMembers;
        this.myOptions = p.MyOptions; this.myRankId = p.MyRankId;
        this.editing = false;
        setUserGuild(p.GuildName, p.GuildRankName);
        break;
      case "GuildInvite": {
        const yes = confirm(`Do you want to join the ${p.Name} guild?`);
        this.send(ClientPackets.GuildInvite({ AcceptInvite: yes }));
        break;
      }
      case "GuildNameRequest": {
        // GameScene.GuildNameRequest (MirInputBox; letters and digits only, no backslash)
        const name = prompt("Please enter a guild name, length must be 3~20 characters.");
        const clean = (name ?? "").replace(/[^A-Za-z0-9]/g, "");
        this.send(ClientPackets.GuildNameReturn({ Name: clean }));
        break;
      }
      case "GuildNoticeChange":
        if (p.update === -1) this.noticeChanged = true;
        else { this.notice = p.notice; this.noticeChanged = false; this.editing = false; }
        break;
      case "GuildMemberChange": this.memberChange(p, setUserGuild); break;
      case "GuildExpGain": this.experience += BigInt(p.Amount); break;
      case "GuildBuffList": this.buffList_(p); break;
      case "GuildRequestWar": {
        // GameScene.GuildRequestWar (MirInputBox)
        const name = prompt("Please enter the guild you would like to go to war with.");
        if (name !== null) this.send(ClientPackets.GuildWarReturn({ Name: name.trim() }));
        break;
      }
      case "GuildStorageGoldChange":
        // GameScene.GuildStorageGoldChange
        if (p.Type === 0) this.chat(`${p.Name} donated ${p.Amount} gold to guild funds.`, ChatType.Guild);
        if (p.Type === 1) this.chat(`${p.Name} retrieved ${p.Amount} gold from guild funds.`, ChatType.Guild);
        if (p.Type === 0 || p.Type === 3) this.gold += p.Amount;
        else this.gold = this.gold > p.Amount ? this.gold - p.Amount : 0;
        break;
      default: return false;
    }
    this.requestUpdates();
    this.render();
    return true;
  }

  /** GameScene.GuildMemberChange. */
  private memberChange(p: Extract<ServerPacket, { type: "GuildMemberChange" }>, setUserGuild: (name: string, rank: string) => void): void {
    const setOnline = (online: boolean) => { for (const r of this.ranks) for (const m of r.Members) if (m.Name === p.Name) m.Online = online; };
    switch (p.Status) {
      case 0: setOnline(false); break;
      case 1: this.chat(`${p.Name} logged on.`, ChatType.Guild); setOnline(true); break;
      case 2: this.chat(`${p.Name} joined guild.`, ChatType.Guild); this.memberCount++; this.membersChanged = true; break;
      case 3: this.chat(`${p.Name} got removed from the guild.`, ChatType.Guild); this.memberCount = Math.max(0, this.memberCount - 1); this.membersChanged = true; break;
      case 4: this.chat(`${p.Name} left the guild.`, ChatType.Guild); this.memberCount = Math.max(0, this.memberCount - 1); this.membersChanged = true; break;
      case 5: this.membersChanged = true; break;
      case 6: // GuildDialog.NewRankRecieved
        if (p.Ranks[0]) { this.ranks.splice(this.ranks.length > 1 ? this.ranks.length - 1 : 1, 0, p.Ranks[0]); this.reindex(); }
        break;
      case 7: { // GuildDialog.RankChangeRecieved
        const r = p.Ranks[0];
        const old = r && this.ranks.find((x) => x.Index === r.Index);
        // The desktop only applies this when its member list is loaded (Ranks non-empty), so a member who never opened it
        // keeps stale rights; the packet carries the rank's members, so the user's own options are taken from it directly.
        if (r && (r.Members.some((m) => m.Name === this.me()) || old?.Members.some((m) => m.Name === this.me()))) {
          this.rankName = r.Name; this.myOptions = r.Options; setUserGuild(this.name, r.Name);
        }
        if (r && old) this.ranks[this.ranks.indexOf(old)] = r;
        break;
      }
      case 8: { // GuildDialog.MyRankChanged
        const r = p.Ranks[0];
        if (!r) break;
        this.myOptions = r.Options; this.rankName = r.Name; setUserGuild(this.name, r.Name);
        const old = this.ranks[this.myRankId];
        this.myRankId = r.Index;
        const i = old ? old.Members.findIndex((m) => m.Name === this.me()) : -1;
        if (!old || i < 0) break;
        const [member] = old.Members.splice(i, 1);
        if (this.ranks[r.Index]) this.ranks[r.Index]!.Members.push(member!); else this.membersChanged = true;
        break;
      }
      case 255: // GuildDialog.NewMembersList
        this.ranks = p.Ranks;
        this.reindex();
        this.memberCount = this.ranks.reduce((n, r) => n + r.Members.length, 0);
        this.membersChanged = false;
        break;
    }
  }

  private renderStorage(): void {
    if (this.root.hidden || this.page !== "storage") return;
    this.storageGrid.replaceChildren(...Array.from({ length: GUILD_STORAGE_SLOTS }, (_, i) => this.invWindow.storageCell(i)));
    this.storageGold.textContent = `Gold ${this.gold.toLocaleString()}`;
    this.retrieveGoldButton.hidden = this.myRankId !== 0; // StoragePage.BeforeDraw: leader rank only
  }

  /** GuildDialog.UpdateBuffs: GuildSkill icon (+2 not owned, +1 active), status text, hint with requirements / time / stats. */
  private renderBuffs(): void {
    this.buffPoints.textContent = `Points left: ${this.sparePoints}`;
    this.buffList.replaceChildren(...this.buffInfos.map((info) => {
      const buff = this.enabledBuffs.find((b) => b.Id === info.Id);
      let status: string, state = "";
      if (!buff) status = info.LevelRequirement > this.level ? "Insufficient Level" : "Available";
      else {
        status = info.TimeLimit > 0 ? (buff.Active ? "Counting down." : "Expired") : "Obtained";
        state = buff.Active ? "Active" : "Inactive";
      }
      const img = el("img", { alt: "" });
      void libIcon("GuildSkill", info.Icon + (!buff ? 2 : buff.Active ? 1 : 0)).then((url) => { if (url) img.src = url; });
      const hint = [info.Name];
      if (info.LevelRequirement > 0) hint.push(`Minimum Guild Level: ${info.LevelRequirement}`);
      if (info.PointsRequirement > 0) hint.push(`Points Required: ${info.PointsRequirement}`);
      if (info.ActivationCost > 0) hint.push(`Activation Cost: ${info.ActivationCost} gold.`);
      if (info.TimeLimit > 0) hint.push(buff?.Active ? `Time Remaining: ${buff.ActiveTimeRemaining} minutes` : `Buff Length: ${info.TimeLimit} minutes.`);
      for (const [k, v] of Object.entries(info.Stats)) if (v) hint.push(`${StatName[Number(k)] ?? `Stat ${k}`} ${v > 0 ? "+" : ""}${v}`);
      return el("div", { class: `guild-buff${buff?.Active ? " active" : ""}`, title: hint.join("\n"), onclick: () => this.requestBuff(info) },
        img, el("div", { class: "guild-buff-text" }, el("div", {}, info.Name), el("div", { class: "magic-meta" }, status)), el("span", { class: "magic-meta" }, state));
    }));
  }

  /** GuildDialog.UpdateRanks: ranks above your own (Index < MyRankId) are read-only. */
  private renderRanks(): void {
    if (this.selectedRank >= this.ranks.length) this.selectedRank = -1;
    this.rankSelect.replaceChildren(
      el("option", { value: "-1", disabled: true }, "Select a rank"),
      ...this.ranks.map((r, i) => el("option", { value: String(i) }, r.Name)),
      ...(this.ranks.length < 255 ? [el("option", { value: String(this.ranks.length) }, "Add New")] : []));
    this.rankSelect.value = String(this.selectedRank);
    const rank = this.ranks[this.selectedRank];
    const editable = !!rank && rank.Index >= this.myRankId;
    if (document.activeElement !== this.rankNameInput) this.rankNameInput.value = rank?.Name ?? "";
    this.rankNameInput.disabled = !editable;
    this.rankRename.disabled = !editable;
    this.rankOptions.replaceChildren(...OPTION_NAMES.map((name, bit) => {
      const box = el("input", { type: "checkbox", checked: !!rank && (rank.Options & (1 << bit)) !== 0, disabled: !editable }) as HTMLInputElement;
      box.onchange = () => this.switchRankOption(bit);
      return el("label", {}, box, name);
    }));
  }

  /** GuildDialog.RefreshMemberList: Ranks[i].Index = i. */
  private reindex(): void {
    this.ranks.forEach((r, i) => { r.Index = i; });
  }

  private render(): void {
    if (this.root.hidden) return;
    this.title.textContent = `Guild — ${this.name}`;
    this.status.textContent = `Rank ${this.rankName} · Level ${this.level} · Members ${this.memberCount}/${this.maxMembers} · Gold ${this.gold.toLocaleString()}`
      + (this.maxExperience > 0n ? ` · Exp ${((Number(this.experience) * 100) / Number(this.maxExperience)).toFixed(1)}%` : "");
    this.noticeView.hidden = this.editing;
    this.noticeEdit.hidden = !this.editing;
    this.noticeView.textContent = this.notice.length ? this.notice.join("\n") : "(no notice)";
    this.noticeButton.hidden = !this.can(Opt.CanChangeNotice);
    this.noticeButton.textContent = this.editing ? "Save notice" : "Edit notice";
    this.recruitButton.hidden = !this.can(Opt.CanRecruit);
    // GuildDialog.StatusChanged: the storage / rank buttons follow the rank's options
    const storageOk = this.can(Opt.CanStoreItem) || this.can(Opt.CanRetrieveItem);
    this.tabs.storage.hidden = !storageOk;
    this.tabs.ranks.hidden = !this.can(Opt.CanChangeRank);
    this.tabs.buffs.hidden = this.buffInfos.length === 0; // GuildDialog.StatusChanged: BuffButton when the server has buffs
    if ((this.page === "storage" && !storageOk) || (this.page === "ranks" && !this.can(Opt.CanChangeRank))) this.page = "main";
    for (const p of Object.keys(this.tabs) as Page[]) this.tabs[p].classList.toggle("on", this.page === p);
    this.mainPage.hidden = this.page !== "main";
    this.storagePage.hidden = this.page !== "storage";
    this.ranksPage.hidden = this.page !== "ranks";
    this.buffsPage.hidden = this.page !== "buffs";
    if (this.page === "buffs") this.renderBuffs();
    if (this.page === "storage") this.renderStorage();
    if (this.page === "ranks") this.renderRanks();
    const me = this.me();
    const rows: HTMLElement[] = [];
    this.ranks.forEach((rank, ri) => {
      rows.push(el("div", { class: "guild-rank" }, rank.Name));
      for (const m of rank.Members) {
        const row = el("div", { class: `guild-member${m.Online ? " online" : ""}` }, el("span", {}, `${m.Online ? "●" : "○"} ${m.Name}`));
        if (m.Name !== me && this.can(Opt.CanChangeRank)) {
          const select = el("select", { title: "Rank" }) as HTMLSelectElement;
          this.ranks.forEach((r, i) => { if (i >= this.myRankId) select.append(el("option", { value: String(i) }, r.Name)); });
          select.value = String(ri);
          select.onchange = () => this.changeRank(m.Name, Number(select.value));
          row.append(select);
        }
        if (m.Name !== me && this.can(Opt.CanKick)) row.append(el("button", { title: "Kick", onclick: () => this.kick(m.Name) }, "×"));
        rows.push(row);
      }
    });
    this.members.replaceChildren(...(rows.length ? rows : [el("div", { class: "magic-meta" }, "Loading…")]));
  }
}

// Group: GroupDialog (GroupList, AllowGroup, add / remove members, switch) + GameScene's group handlers (SwitchGroup,
// DeleteGroup, DeleteMember, GroupInvite, AddMember, GroupMembersMap, SendMemberLocation). Toggle with P (or the Group button).
// GroupList[0] is the leader; only the leader may add or remove. The invite asks with confirm() (MirMessageBox Yes/No).

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

const MAX_GROUP = 11; // Globals.MaxGroup

export class Group {
  /** GroupDialog.GroupList (leader first). */
  list: string[] = [];
  allowGroup = false;
  /** GroupDialog.GroupMembersMap: member -> map title. */
  readonly memberMaps = new Map<string, string>();
  /** BigMapViewPort.PlayerLocations: member -> location (sent while on the same map). */
  readonly locations = new Map<string, WirePoint>();
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly me: () => string;
  private readonly root = el("div", { class: "window group-window", hidden: true });
  private readonly members = el("div", { class: "group-list" });
  private readonly switchButton = el("button", {});

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, me: () => string) {
    this.send = send;
    this.chat = chat;
    this.me = me;
    this.switchButton.onclick = () => this.send(ClientPackets.SwitchGroup({ AllowGroup: !this.allowGroup }));
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Group"), el("button", { class: "window-close", onclick: () => this.toggle(false) }, "×")),
      this.members,
      el("div", { class: "row" }, this.switchButton, el("button", { onclick: () => this.add() }, "Add"), el("button", { onclick: () => this.remove() }, "Remove")),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);
    window.addEventListener("keydown", (e) => { if (matches(e, KeybindOptions.Group) && !(document.activeElement instanceof HTMLInputElement)) this.toggle(); }, { signal: gameSignal() });
    this.render();
  }

  has(name: string): boolean {
    return this.list.includes(name);
  }

  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
    this.render();
  }

  private leaderOk(): boolean {
    if (this.list.length > 0 && this.list[0] !== this.me()) { this.chat("You are not the leader of your group.", ChatType.System); return false; }
    return true;
  }

  /** GroupDialog.AddMember (input box). */
  private add(): void {
    if (this.list.length >= MAX_GROUP) { this.chat("Your group already has the maximum number of members.", ChatType.System); return; }
    if (!this.leaderOk()) return;
    const name = prompt("Enter the name of the person you wish to group.");
    if (name) this.send(ClientPackets.AddMember({ Name: name.trim() }));
  }

  /** GroupDialog.DelMember (input box). */
  private remove(): void {
    if (!this.leaderOk()) return;
    const name = prompt("Enter the name of the person you wish to remove.");
    if (name) this.send(ClientPackets.DelMember({ Name: name.trim() }));
  }

  /** Server packets; returns true when handled. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "SwitchGroup":
        this.allowGroup = p.AllowGroup;
        if (!p.AllowGroup && this.list.length > 0) this.deleteGroup();
        break;
      case "DeleteGroup": this.deleteGroup(); break;
      case "DeleteMember":
        this.list = this.list.filter((n) => n !== p.Name);
        this.memberMaps.delete(p.Name);
        this.locations.delete(p.Name);
        this.chat(`-${p.Name} has left the group.`, ChatType.Group);
        break;
      case "GroupInvite": {
        const yes = confirm(`Do you want to group with ${p.Name}?`);
        this.send(ClientPackets.GroupInvite({ AcceptInvite: yes }));
        if (yes) this.toggle(true);
        break;
      }
      case "AddMember":
        this.list.push(p.Name);
        this.chat(`-${p.Name} has joined the group.`, ChatType.Group);
        break;
      case "GroupMembersMap": this.memberMaps.set(p.PlayerName, p.PlayerMap); break;
      case "SendMemberLocation": this.locations.set(p.MemberName, { ...p.MemberLocation }); break;
      default: return false;
    }
    this.render();
    return true;
  }

  /** GameScene.DeleteGroup. */
  private deleteGroup(): void {
    this.list = [];
    this.memberMaps.clear();
    this.locations.clear();
    this.chat("You have left the group.", ChatType.Group);
  }

  private render(): void {
    this.switchButton.textContent = this.allowGroup ? "Grouping: On" : "Grouping: Off";
    if (this.root.hidden) return;
    this.members.replaceChildren(...(this.list.length
      ? this.list.map((n, i) => el("div", { class: "group-member", title: this.memberMaps.get(n) ?? "" }, `${i === 0 ? "★ " : ""}${n}`, el("span", { class: "magic-meta" }, this.memberMaps.get(n) ? `  ${this.memberMaps.get(n)}` : "")))
      : [el("div", { class: "magic-meta" }, "You are not in a group.")]));
  }
}

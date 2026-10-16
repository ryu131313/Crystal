// The user's group: GroupDialog.GroupList / AllowGroup / GroupMembersMap, BigMapViewPort.PlayerLocations and GameScene's
// group handlers (SwitchGroup, DeleteGroup, DeleteMember, GroupInvite, AddMember, GroupMembersMap, SendMemberLocation).
// GroupList[0] is the leader; only the leader may add or remove. game.ts feeds handle(); the UI follows `changed` and
// answers the `invite` prompt (GameScene.GroupInvite: MirMessageBox YesNo) with answerInvite().

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import { Emitter } from "./emitter.ts";

/** One member's S.GroupMemberStatus (web-only: the server pushes it at most once a second per member, on change). */
export interface GroupMemberStatus {
  /** MirClass value. */
  readonly cls: number;
  readonly level: number;
  /** 0-100. */
  readonly hpPercent: number;
  /** 0-100. */
  readonly mpPercent: number;
  /** BuffType values (at most GROUP_MEMBER_STATUS_MAX_BUFFS). */
  readonly buffs: readonly number[];
}

/** Globals.MaxGroup. */
export const MAX_GROUP = 11;

export class GroupState {
  private _allowGroup = false;
  /** GroupDialog.GroupMembersMap: member -> map title. */
  readonly memberMaps = new Map<string, string>();
  /** BigMapViewPort.PlayerLocations: member -> location (sent while on the same map). */
  readonly locations = new Map<string, WirePoint>();
  /** S.GroupMemberStatus: member -> class / level / HP% / MP% / buffs (missing until the first status of that member). */
  readonly memberStatus = new Map<string, GroupMemberStatus>();
  /** A member's status arrived (fired with the name; `changed` is not fired for it: the roster did not change). */
  readonly statusChanged = new Emitter<string>();
  /** The roster or a flag changed (after the packet's chat line). */
  readonly changed = new Emitter<void>();
  /** GameScene.GroupInvite: `name` asks the user to join; answer with answerInvite(). */
  readonly invite = new Emitter<{ name: string }>();
  private members: string[] = [];

  constructor(private readonly send: (f: Uint8Array) => void, private readonly chat: (text: string, type: number) => void) {}

  /** GroupDialog.AllowGroup (S.SwitchGroup). */
  get allowGroup(): boolean { return this._allowGroup; }

  /** GroupDialog.GroupList (leader first). */
  get list(): string[] {
    return this.members;
  }

  has(name: string): boolean {
    return this.members.includes(name);
  }

  /** GroupDialog: the user may add / remove (no group, or the user leads it). */
  isLeader(me: string): boolean {
    return !(this.members.length > 0 && this.members[0] !== me);
  }

  /** SwitchButton.Click: C.SwitchGroup with the flag flipped (the server answers S.SwitchGroup). */
  toggleAllowGroup(): void {
    this.send(ClientPackets.SwitchGroup({ AllowGroup: !this._allowGroup }));
  }

  /** GroupDialog.AddMember's input box answer. */
  addMember(name: string): void {
    this.send(ClientPackets.AddMember({ Name: name }));
  }

  /** GroupDialog.DelMember's input box answer. */
  removeMember(name: string): void {
    this.send(ClientPackets.DelMember({ Name: name }));
  }

  /** GameScene.GroupInvite's Yes / No. */
  answerInvite(accept: boolean): void {
    this.send(ClientPackets.GroupInvite({ AcceptInvite: accept }));
  }

  /** Server packets. Never consumes (the classic chain went on to the other handlers). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "SwitchGroup":
        this._allowGroup = p.AllowGroup;
        if (!p.AllowGroup && this.members.length > 0) this.deleteGroup();
        break;
      case "DeleteGroup": this.deleteGroup(); break;
      case "DeleteMember":
        this.members = this.members.filter((n) => n !== p.Name);
        this.memberMaps.delete(p.Name);
        this.locations.delete(p.Name);
        this.memberStatus.delete(p.Name);
        this.chat(`-${p.Name} has left the group.`, ChatType.Group);
        break;
      case "GroupInvite": this.invite.emit({ name: p.Name }); break;
      case "AddMember":
        this.members.push(p.Name);
        this.chat(`-${p.Name} has joined the group.`, ChatType.Group);
        break;
      case "GroupMembersMap": this.memberMaps.set(p.PlayerName, p.PlayerMap); break;
      case "SendMemberLocation": this.locations.set(p.MemberName, { ...p.MemberLocation }); break;
      case "GroupMemberStatus":
        this.memberStatus.set(p.Name, { cls: p.Class, level: p.Level, hpPercent: p.HPPercent, mpPercent: p.MPPercent, buffs: p.Buffs });
        this.statusChanged.emit(p.Name);
        return false;
      default: return false;
    }
    this.changed.emit();
    return false;
  }

  /** GameScene.DeleteGroup. */
  private deleteGroup(): void {
    this.members = [];
    this.memberMaps.clear();
    this.locations.clear();
    this.memberStatus.clear();
    this.chat("You have left the group.", ChatType.Group);
  }
}

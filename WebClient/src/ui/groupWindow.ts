// Group: GroupDialog (GroupList, AllowGroup, add / remove members, switch) + GameScene's group handlers (SwitchGroup,
// DeleteGroup, DeleteMember, GroupInvite, AddMember, GroupMembersMap, SendMemberLocation). Toggle with P (or the Group button).
// GroupList[0] is the leader; only the leader may add or remove. Desktop layout and artwork (Prguse 120, Title 5).

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import { MirWindow, MirImage, MirButton, MirLabel, closeButton, Lib } from "./mir/controls.ts";
import { inputBox, messageBox } from "./messageBox.ts";
import { SoundList } from "../game/audio.ts";
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
  private readonly win: MirWindow;
  private readonly members: MirLabel[] = [];
  private readonly switchButton: MirButton;
  private readonly addButton: MirButton;
  private readonly delButton: MirButton;

  private readonly stage: HTMLElement;

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, me: () => string) {
    this.stage = stage;
    this.send = send;
    this.chat = chat;
    this.me = me;
    // GroupDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 120, movable: true, sort: true, location: "center" });
    this.members.push(new MirLabel({ parent: this.win, autoSize: true, location: [16, 33] }));
    for (let i = 1; i < MAX_GROUP; i++) this.members.push(new MirLabel({ parent: this.win, autoSize: true, location: [((i + 1) % 2) * 100 + 16, 55 + Math.floor((i - 1) / 2) * 20] }));
    new MirImage({ parent: this.win, library: Lib.Title, index: 5, location: [18, 8] });
    closeButton(this.win, [206, 3], () => this.toggle(false));
    this.switchButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 114, hoverIndex: 115, pressedIndex: 116, location: [25, 219], sound: SoundList.ButtonA, hint: "Allow/Disallow Group Requests",
      onClick: () => this.send(ClientPackets.SwitchGroup({ AllowGroup: !this.allowGroup })) });
    this.addButton = new MirButton({ parent: this.win, library: Lib.Title, index: 133, hoverIndex: 134, pressedIndex: 135, location: [70, 219], sound: SoundList.ButtonA, hint: "Add", onClick: () => this.add() });
    this.delButton = new MirButton({ parent: this.win, library: Lib.Title, index: 136, hoverIndex: 137, pressedIndex: 138, location: [140, 219], sound: SoundList.ButtonA, hint: "Remove", onClick: () => this.remove() });
    window.addEventListener("keydown", (e) => { if (matches(e, KeybindOptions.Group) && !(document.activeElement instanceof HTMLInputElement)) this.toggle(); }, { signal: gameSignal() });
    gameSignal().addEventListener("abort", () => this.win.dispose());
    this.render();
  }

  has(name: string): boolean {
    return this.list.includes(name);
  }

  toggle(show = !this.win.visible): void {
    if (show) this.win.show(); else this.win.hide();
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
    inputBox(this.stage, "Please enter the name of the person you wish to add.", "", (name) => { this.send(ClientPackets.AddMember({ Name: name })); });
  }

  /** GroupDialog.DelMember (input box). */
  private remove(): void {
    if (!this.leaderOk()) return;
    inputBox(this.stage, "Please enter the name of the person you wish to remove.", "", (name) => { this.send(ClientPackets.DelMember({ Name: name })); });
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
        // GameScene.GroupInvite: MirMessageBox YesNo
        const answer = (yes: boolean) => { this.send(ClientPackets.GroupInvite({ AcceptInvite: yes })); };
        messageBox(this.stage, `Do you want to group with ${p.Name}?`, [{ label: "Yes", onClick: () => answer(true) }, { label: "No", onClick: () => answer(false) }]);
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

  /** GroupPanel_BeforeDraw. */
  private render(): void {
    if (this.list.length === 0) this.addButton.setIndices(130, 131, 132);
    else this.addButton.setIndices(133, 134, 135);
    const canEdit = !(this.list.length > 0 && this.list[0] !== this.me());
    this.addButton.visible = canEdit;
    this.delButton.visible = canEdit;
    if (this.allowGroup) this.switchButton.setIndices(117, 118, 119);
    else this.switchButton.setIndices(114, 115, 116);
    for (let i = 0; i < this.members.length; i++) {
      const name = i < this.list.length ? this.list[i] : "";
      this.members[i].text = name;
      this.members[i].hint = (name && this.memberMaps.get(name)) || "";
    }
  }
}

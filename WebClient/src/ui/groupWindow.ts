// Group: GroupDialog (GroupList, AllowGroup, add / remove members, switch) with the GroupInvite prompt. Toggle with P (or
// the Group button). GroupList[0] is the leader; only the leader may add or remove. Desktop layout and artwork (Prguse
// 120, Title 5). The roster and GameScene's group handlers are the GroupState model (src/game/group.ts).

import { ChatType } from "../protocol/enums.generated.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import { MirWindow, MirImage, MirButton, MirLabel, closeButton, Lib } from "./mir/controls.ts";
import { inputBox, messageBox } from "./messageBox.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { MAX_GROUP, type GroupState } from "../game/group.ts";

export class Group {
  private readonly group: GroupState;
  private readonly chat: (text: string, type: number) => void;
  private readonly me: () => string;
  private readonly win: MirWindow;
  private readonly members: MirLabel[] = [];
  private readonly switchButton: MirButton;
  private readonly addButton: MirButton;
  private readonly delButton: MirButton;

  private readonly stage: HTMLElement;

  constructor(stage: HTMLElement, group: GroupState, chat: (text: string, type: number) => void, me: () => string) {
    this.stage = stage;
    this.group = group;
    this.chat = chat;
    this.me = me;
    // GroupDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 120, movable: true, sort: true, location: "center" });
    this.members.push(new MirLabel({ parent: this.win, autoSize: true, location: [16, 33] }));
    for (let i = 1; i < MAX_GROUP; i++) this.members.push(new MirLabel({ parent: this.win, autoSize: true, location: [((i + 1) % 2) * 100 + 16, 55 + Math.floor((i - 1) / 2) * 20] }));
    new MirImage({ parent: this.win, library: Lib.Title, index: 5, location: [18, 8] });
    closeButton(this.win, [206, 3], () => this.toggle(false));
    this.switchButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 114, hoverIndex: 115, pressedIndex: 116, location: [25, 219], sound: SoundList.ButtonA, hint: "Allow/Disallow Group Requests",
      onClick: () => group.toggleAllowGroup() });
    this.addButton = new MirButton({ parent: this.win, library: Lib.Title, index: 133, hoverIndex: 134, pressedIndex: 135, location: [70, 219], sound: SoundList.ButtonA, hint: "Add", onClick: () => this.add() });
    this.delButton = new MirButton({ parent: this.win, library: Lib.Title, index: 136, hoverIndex: 137, pressedIndex: 138, location: [140, 219], sound: SoundList.ButtonA, hint: "Remove", onClick: () => this.remove() });
    window.addEventListener("keydown", (e) => { if (matches(e, KeybindOptions.Group) && !(document.activeElement instanceof HTMLInputElement)) this.toggle(); }, { signal: gameSignal() });
    const offChanged = group.changed.on(() => this.render());
    // GameScene.GroupInvite: MirMessageBox YesNo
    const offInvite = group.invite.on(({ name }) => {
      messageBox(this.stage, `Do you want to group with ${name}?`, [{ label: "Yes", onClick: () => group.answerInvite(true) }, { label: "No", onClick: () => group.answerInvite(false) }]);
    });
    gameSignal().addEventListener("abort", () => { offChanged(); offInvite(); this.win.dispose(); });
    this.render();
  }

  /** The model's roster (GroupDialog.GroupList, GroupMembersMap, PlayerLocations), for the minimap, big map and inspect. */
  get list(): string[] { return this.group.list; }
  get allowGroup(): boolean { return this.group.allowGroup; }
  get memberMaps(): ReadonlyMap<string, string> { return this.group.memberMaps; }
  get locations(): ReadonlyMap<string, WirePoint> { return this.group.locations; }

  has(name: string): boolean {
    return this.group.has(name);
  }

  toggle(show = !this.win.visible): void {
    if (show) this.win.show(); else this.win.hide();
    this.render();
  }

  private leaderOk(): boolean {
    if (!this.group.isLeader(this.me())) { this.chat("You are not the leader of your group.", ChatType.System); return false; }
    return true;
  }

  /** GroupDialog.AddMember (input box). */
  private add(): void {
    if (this.list.length >= MAX_GROUP) { this.chat("Your group already has the maximum number of members.", ChatType.System); return; }
    if (!this.leaderOk()) return;
    inputBox(this.stage, "Please enter the name of the person you wish to add.", "", (name) => { this.group.addMember(name); });
  }

  /** GroupDialog.DelMember (input box). */
  private remove(): void {
    if (!this.leaderOk()) return;
    inputBox(this.stage, "Please enter the name of the person you wish to remove.", "", (name) => { this.group.removeMember(name); });
  }

  /** GroupPanel_BeforeDraw. */
  private render(): void {
    if (this.list.length === 0) this.addButton.setIndices(130, 131, 132);
    else this.addButton.setIndices(133, 134, 135);
    const canEdit = this.group.isLeader(this.me());
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

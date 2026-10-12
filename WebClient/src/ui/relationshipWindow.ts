// Marriage + mentor: RelationshipDialog (Client/MirScenes/Dialogs/RelationshipDialog.cs, Prguse 583; L / Lover button)
// and MentorDialog (MentorDialog.cs, Prguse 170; Mentor key / button) + GameScene's handlers (MarriageRequest,
// DivorceRequest, LoverUpdate, MentorRequest, MentorUpdate). Requests are answered in a non-blocking MirMessageBox
// (messageBox.ts) so the frame loop keeps running. Esc closes both (GameScene Closeall).

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, MirClassName } from "../protocol/enums.generated.ts";
import { MirWindow, MirImage, MirButton, MirLabel, closeButton, Lib } from "./mir/controls.ts";
import { messageBox, inputBox } from "./messageBox.ts";
import { SoundList } from "../game/audio.ts";
import { dateFromBinary } from "../game/itemChanges.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

/** DateTime.FromBinary(v).Ticks (Kind bits dropped). */
function ticksOf(v: bigint): bigint {
  return BigInt.asUintN(62, v);
}

const LIGHT_GRAY = "#d3d3d3"; // Color.LightGray
const DIM_GRAY = "#696969";   // Color.DimGray
const GREEN = "#008000";      // Color.Green

/** The desktop's Size(200, 30) VerticalCenter info labels. */
function infoLabel(parent: MirWindow, location: [number, number], font: string, foreColour: string, text = ""): MirLabel {
  return new MirLabel({ parent, location, size: [200, 30], vCenter: true, notControl: true, font, foreColour, text });
}

/** True while typing in a text field (window keys are not game keys then). */
function typing(): boolean {
  const a = document.activeElement;
  return a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement;
}

export class RelationshipWindow {
  // RelationshipDialog fields
  loverName = "";
  date = 0n;
  mapName = "";
  marriedDays = 0;
  /** MailButton: MailComposeLetterDialog.ComposeMail (set by the game; the button does nothing until then). */
  composeMail: ((name: string) => void) | null = null;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly prefillChat: (text: string) => void;
  private readonly stage: HTMLElement;
  private readonly win: MirWindow;
  private readonly allowButton: MirButton;
  private readonly nameLabel: MirLabel;
  private readonly dateLabel: MirLabel;
  private readonly lengthLabel: MirLabel;
  private readonly onlineLabel: MirLabel;

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, prefillChat: (text: string) => void) {
    this.stage = stage;
    this.send = send;
    this.chat = chat;
    this.prefillChat = prefillChat;
    // RelationshipDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 583, movable: true, sort: true, location: "center" });
    new MirImage({ parent: this.win, library: Lib.Title, index: 52, location: [18, 8] });
    closeButton(this.win, [260, 3], () => this.toggle(false));
    this.allowButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 610, hoverIndex: 611, pressedIndex: 612, location: [50, 164], sound: SoundList.ButtonA,
      hint: "Allow/Block Marriage", onClick: () => this.send(ClientPackets.ChangeMarriage()) });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 600, hoverIndex: 601, pressedIndex: 602, location: [85, 164], sound: SoundList.ButtonA,
      hint: "Request Marriage", onClick: () => this.request() });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 616, hoverIndex: 617, pressedIndex: 618, location: [120, 164], sound: SoundList.ButtonA,
      hint: "Request Divorce", onClick: () => this.divorce() });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 437, hoverIndex: 438, pressedIndex: 439, location: [155, 164], sound: SoundList.ButtonA,
      hint: "Mail Lover", onClick: () => this.mail() });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 566, hoverIndex: 567, pressedIndex: 568, location: [190, 164], sound: SoundList.ButtonA,
      hint: "Whisper Lover", onClick: () => this.whisper() });
    this.nameLabel = infoLabel(this.win, [30, 40], "10pt Arial", LIGHT_GRAY);
    this.dateLabel = infoLabel(this.win, [30, 65], "10pt Arial", LIGHT_GRAY);
    this.lengthLabel = infoLabel(this.win, [30, 90], "10pt Arial", LIGHT_GRAY);
    this.onlineLabel = infoLabel(this.win, [30, 115], "10pt Arial", LIGHT_GRAY);
    const signal = gameSignal();
    // KeybindOptions.Relationship = L, Closeall = Esc (GameScene key handler)
    window.addEventListener("keydown", (e) => {
      if (typing()) return;
      if (matches(e, KeybindOptions.Relationship)) this.toggle();
      else if (matches(e, KeybindOptions.Closeall) && this.win.visible) this.toggle(false);
    }, { signal });
    signal.addEventListener("abort", () => this.win.dispose());
    // the desktop fills the labels on the first S.LoverUpdate only (the server sends one at login)
  }

  toggle(show = !this.win.visible): void {
    if (show) this.win.show(); else this.win.hide();
  }

  /** RequestButton.Click. */
  private request(): void {
    if (this.loverName !== "") { this.chat("You're already married.", ChatType.System); return; }
    this.send(ClientPackets.MarriageRequest());
  }

  /** DivorceButton.Click. */
  private divorce(): void {
    if (this.loverName === "") { this.chat("You're not married.", ChatType.System); return; }
    this.send(ClientPackets.DivorceRequest());
  }

  /** MailButton.Click. */
  private mail(): void {
    if (this.loverName === "") { this.chat("You're not married.", ChatType.System); return; }
    this.composeMail?.(this.loverName);
  }

  /** WhisperButton.Click: ":)" in the chat box (the lover chat prefix). */
  private whisper(): void {
    if (this.loverName === "") { this.chat("You're not married.", ChatType.System); return; }
    if (this.mapName === "") { this.chat("Lover is not online", ChatType.System); return; }
    this.prefillChat(":)");
  }

  /** Server packets; returns true when handled. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "MarriageRequest": // GameScene.MarriageRequest
        messageBox(this.stage, `${p.Name} has asked for your hand in marriage.`, [
          { label: "Yes", primary: true, onClick: () => this.send(ClientPackets.MarriageReply({ AcceptInvite: true })) },
          { label: "No", onClick: () => this.send(ClientPackets.MarriageReply({ AcceptInvite: false })) },
        ]);
        return true;
      case "DivorceRequest": // GameScene.DivorceRequest
        messageBox(this.stage, `${p.Name} has requested a divorce`, [
          { label: "Yes", primary: true, onClick: () => this.send(ClientPackets.DivorceReply({ AcceptInvite: true })) },
          { label: "No", onClick: () => this.send(ClientPackets.DivorceReply({ AcceptInvite: false })) },
        ]);
        return true;
      case "LoverUpdate": // GameScene.LoverUpdate
        this.loverName = p.Name;
        this.date = p.Date;
        this.mapName = p.MapName;
        this.marriedDays = p.MarriedDays;
        this.updateInterface();
        return true;
    }
    return false;
  }

  /** RelationshipDialog.UpdateInterface. */
  private updateInterface(): void {
    this.nameLabel.text = `Lover:  ${this.loverName}`;
    this.onlineLabel.text = this.mapName !== "" ? `Location:  ${this.mapName}` : "Location:  Offline";
    const ticks = ticksOf(this.date);
    const shortDate = () => dateFromBinary(this.date).toLocaleDateString();
    if (this.loverName === "" && ticks !== 0n) {
      if (ticks < 2000n) { // Date < new DateTime(2000) (ticks)
        this.dateLabel.text = "Date: ";
        this.lengthLabel.text = "Length: ";
      } else {
        this.dateLabel.text = `Divorced Date:  ${shortDate()}`;
        this.lengthLabel.text = `Time Since: ${this.marriedDays} Days`;
      }
      this.onlineLabel.text = "Location: ";
      this.allowButton.hint = "Allow/Block Marriage";
    } else {
      this.dateLabel.text = `Marriage Date:  ${ticks === 0n ? "" : shortDate()}`;
      this.lengthLabel.text = `Length: ${this.marriedDays} Days`;
      // the same C.ChangeMarriage toggles recall while married
      this.allowButton.hint = "Allow/Block Recall";
    }
  }
}

export interface MentorUser { name: string; level: number; cls: number }

export class MentorWindow {
  // MentorDialog fields
  mentorName = "";
  mentorLevel = 0;
  mentorOnline = false;
  menteeExp = 0n;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly me: () => MentorUser;
  private readonly stage: HTMLElement;
  private readonly win: MirWindow;
  private readonly mentorNameLabel: MirLabel;
  private readonly mentorLevelLabel: MirLabel;
  private readonly mentorOnlineLabel: MirLabel;
  private readonly studentNameLabel: MirLabel;
  private readonly studentLevelLabel: MirLabel;
  private readonly studentOnlineLabel: MirLabel;
  private readonly menteeExpLabel: MirLabel;

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, me: () => MentorUser) {
    this.stage = stage;
    this.send = send;
    this.chat = chat;
    this.me = me;
    // MentorDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 170, movable: true, sort: true, location: "center" });
    new MirImage({ parent: this.win, library: Lib.Title, index: 51, location: [18, 8] });
    closeButton(this.win, [219, 3], () => this.toggle(false));
    // AllowButton: C.AllowMentor toggles on the server. The desktop's index swap tests Index == 116, which the button never
    // has at rest, so it always resets to 114 / 115 / 116: the picture never changes.
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 114, hoverIndex: 115, pressedIndex: 116, location: [30, 178], sound: SoundList.ButtonA,
      hint: "Allow/Disallow Mentor Requests", onClick: () => this.send(ClientPackets.AllowMentor()) });
    new MirButton({ parent: this.win, library: Lib.Title, index: 213, hoverIndex: 214, pressedIndex: 215, location: [60, 178], sound: SoundList.ButtonA,
      hint: "Add Mentor", onClick: () => this.add() });
    new MirButton({ parent: this.win, library: Lib.Title, index: 216, hoverIndex: 217, pressedIndex: 218, location: [135, 178], sound: SoundList.ButtonA,
      hint: "Remove Mentor/Mentee", onClick: () => this.remove() });
    this.mentorNameLabel = infoLabel(this.win, [20, 58], "10pt Arial", LIGHT_GRAY);
    this.mentorLevelLabel = infoLabel(this.win, [170, 58], "10pt Arial", LIGHT_GRAY);
    this.mentorOnlineLabel = infoLabel(this.win, [125, 58], "7pt Arial", GREEN, "ONLINE");
    this.mentorOnlineLabel.visible = false;
    this.studentNameLabel = infoLabel(this.win, [20, 112], "10pt Arial", LIGHT_GRAY);
    this.studentLevelLabel = infoLabel(this.win, [170, 111], "10pt Arial", LIGHT_GRAY);
    this.studentOnlineLabel = infoLabel(this.win, [125, 112], "7pt Arial", GREEN, "ONLINE");
    this.studentOnlineLabel.visible = false;
    infoLabel(this.win, [15, 41], "7pt Arial", DIM_GRAY, "MENTOR");
    infoLabel(this.win, [15, 94], "7pt Arial", DIM_GRAY, "MENTEE");
    this.menteeExpLabel = infoLabel(this.win, [15, 147], "7pt Arial", DIM_GRAY);
    const signal = gameSignal();
    // KeybindOptions.Mentor (unbound by default), Closeall = Esc (GameScene key handler)
    window.addEventListener("keydown", (e) => {
      if (typing()) return;
      if (matches(e, KeybindOptions.Mentor)) this.toggle();
      else if (matches(e, KeybindOptions.Closeall) && this.win.visible) this.toggle(false);
    }, { signal });
    signal.addEventListener("abort", () => this.win.dispose());
    this.updateInterface();
  }

  toggle(show = !this.win.visible): void {
    if (show) { this.win.show(); this.updateInterface(); } // the user's own level may have changed since the last MentorUpdate
    else this.win.hide();
  }

  /** AddButton.Click (MirInputBox). */
  private add(): void {
    if (this.mentorLevel !== 0) { this.chat("You already have a Mentor.", ChatType.System); return; }
    inputBox(this.stage, "Please enter the name of the person you would like to be your Mentor.", "", (name) => { this.send(ClientPackets.AddMentor({ Name: name })); });
  }

  /** RemoveButton.Click. */
  private remove(): void {
    if (this.mentorName === "") { this.chat("You don't currently have a Mentorship to cancel.", ChatType.System); return; }
    messageBox(this.stage, "Cancelling a Mentorship early will cause a cooldown. Are you sure?", [
      { label: "Yes", primary: true, onClick: () => this.send(ClientPackets.CancelMentor()) },
      { label: "No" },
    ]);
  }

  /** Server packets; returns true when handled. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "MentorRequest": { // GameScene.MentorRequest
        const cls = MirClassName[this.me().cls] ?? "";
        messageBox(this.stage, `${p.Name} (Level ${p.Level}) has requested you teach him the ways of the ${cls}.`, [
          { label: "Yes", primary: true, onClick: () => this.send(ClientPackets.MentorReply({ AcceptInvite: true })) },
          { label: "No", onClick: () => this.send(ClientPackets.MentorReply({ AcceptInvite: false })) },
        ]);
        return true;
      }
      case "MentorUpdate": // GameScene.MentorUpdate
        this.mentorName = p.Name;
        this.mentorLevel = p.Level;
        this.mentorOnline = p.Online;
        this.menteeExp = p.MenteeEXP;
        this.updateInterface();
        return true;
    }
    return false;
  }

  /** MentorDialog.UpdateInterface: the higher level is the mentor; MENTEE EXP shows only for the mentor. */
  private updateInterface(): void {
    const parts = [this.mentorNameLabel, this.mentorLevelLabel, this.mentorOnlineLabel, this.studentNameLabel, this.studentLevelLabel, this.studentOnlineLabel];
    if (this.mentorLevel === 0) {
      for (const l of parts) l.visible = false;
      this.menteeExpLabel.visible = false;
      return;
    }
    for (const l of parts) l.visible = true;
    const me = this.me();
    if (me.level > this.mentorLevel) {
      this.mentorNameLabel.text = me.name;
      this.mentorLevelLabel.text = `Lv ${me.level}`;
      this.mentorOnlineLabel.visible = false;
      this.studentNameLabel.text = this.mentorName;
      this.studentLevelLabel.text = `Lv ${this.mentorLevel}`;
      this.studentOnlineLabel.visible = this.mentorOnline;
      this.menteeExpLabel.visible = true;
      this.menteeExpLabel.text = `MENTEE EXP: ${this.menteeExp}`;
    } else {
      this.mentorNameLabel.text = this.mentorName;
      this.mentorLevelLabel.text = `Lv ${this.mentorLevel}`;
      this.mentorOnlineLabel.visible = this.mentorOnline;
      this.studentNameLabel.text = me.name;
      this.studentLevelLabel.text = `Lv ${me.level}`;
      this.studentOnlineLabel.visible = false;
    }
  }
}

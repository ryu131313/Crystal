// Marriage + mentor state: RelationshipDialog (LoverName, Date, MapName, MarriedDays) and MentorDialog (MentorName,
// MentorLevel, MentorOnline, MenteeEXP) with GameScene's handlers (MarriageRequest, DivorceRequest, LoverUpdate,
// MentorRequest, MentorUpdate) and the dialogs' button sends. game.ts feeds handle(); the UI follows `changed` and answers
// the request prompts (GameScene: MirMessageBox YesNo) with answerMarriage / answerDivorce / answerMentor.

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { Emitter } from "./emitter.ts";

/** RelationshipDialog state + GameScene.MarriageRequest / DivorceRequest / LoverUpdate. */
export class Relationship {
  /** S.LoverUpdate applied (RelationshipDialog.UpdateInterface). */
  readonly changed = new Emitter<void>();
  /** GameScene.MarriageRequest: `name` proposes; answer with answerMarriage(). */
  readonly marriageRequest = new Emitter<{ name: string }>();
  /** GameScene.DivorceRequest: `name` asks for a divorce; answer with answerDivorce(). */
  readonly divorceRequest = new Emitter<{ name: string }>();
  private _loverName = "";
  private _date = 0n;
  private _mapName = "";
  private _marriedDays = 0;

  constructor(private readonly send: (f: Uint8Array) => void, private readonly chat: (text: string, type: number) => void) {}

  /** RelationshipDialog.LoverName ("" = not married). */
  get loverName(): string { return this._loverName; }
  /** RelationshipDialog.Date (DateTime.ToBinary: the marriage or divorce date). */
  get date(): bigint { return this._date; }
  /** RelationshipDialog.MapName ("" = the lover is offline). */
  get mapName(): string { return this._mapName; }
  get marriedDays(): number { return this._marriedDays; }

  /** AllowButton.Click: C.ChangeMarriage (allow / block marriage, or recall while married). */
  changeMarriage(): void {
    this.send(ClientPackets.ChangeMarriage());
  }

  /** RequestButton.Click. */
  requestMarriage(): void {
    if (this._loverName !== "") { this.chat("You're already married.", ChatType.System); return; }
    this.send(ClientPackets.MarriageRequest());
  }

  /** DivorceButton.Click. */
  requestDivorce(): void {
    if (this._loverName === "") { this.chat("You're not married.", ChatType.System); return; }
    this.send(ClientPackets.DivorceRequest());
  }

  /** GameScene.MarriageRequest's Yes / No. */
  answerMarriage(accept: boolean): void {
    this.send(ClientPackets.MarriageReply({ AcceptInvite: accept }));
  }

  /** GameScene.DivorceRequest's Yes / No. */
  answerDivorce(accept: boolean): void {
    this.send(ClientPackets.DivorceReply({ AcceptInvite: accept }));
  }

  /** Server packets; true = consumed (the classic chain returned on all three). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "MarriageRequest": this.marriageRequest.emit({ name: p.Name }); return true;
      case "DivorceRequest": this.divorceRequest.emit({ name: p.Name }); return true;
      case "LoverUpdate": // GameScene.LoverUpdate
        this._loverName = p.Name;
        this._date = p.Date;
        this._mapName = p.MapName;
        this._marriedDays = p.MarriedDays;
        this.changed.emit();
        return true;
    }
    return false;
  }
}

/** MentorDialog state + GameScene.MentorRequest / MentorUpdate. */
export class Mentor {
  /** S.MentorUpdate applied (MentorDialog.UpdateInterface). */
  readonly changed = new Emitter<void>();
  /** GameScene.MentorRequest: `name` (level `level`) asks the user to teach them; answer with answerMentor(). */
  readonly request = new Emitter<{ name: string; level: number }>();
  private _mentorName = "";
  private _mentorLevel = 0;
  private _mentorOnline = false;
  private _menteeExp = 0n;

  constructor(private readonly send: (f: Uint8Array) => void, private readonly chat: (text: string, type: number) => void) {}

  /** MentorDialog.MentorName: the partner (mentor or mentee; "" = none). */
  get mentorName(): string { return this._mentorName; }
  /** MentorDialog.MentorLevel: the partner's level (0 = no mentorship). */
  get mentorLevel(): number { return this._mentorLevel; }
  get mentorOnline(): boolean { return this._mentorOnline; }
  /** MentorDialog.MenteeEXP. */
  get menteeExp(): bigint { return this._menteeExp; }

  /** AllowButton.Click: C.AllowMentor (toggled on the server). */
  allowMentor(): void {
    this.send(ClientPackets.AllowMentor());
  }

  /** AddButton.Click's refusal: true when the UI may ask for a name (then addMentor). */
  canAdd(): boolean {
    if (this._mentorLevel !== 0) { this.chat("You already have a Mentor.", ChatType.System); return false; }
    return true;
  }

  /** AddButton's MirInputBox answer. */
  addMentor(name: string): void {
    this.send(ClientPackets.AddMentor({ Name: name }));
  }

  /** RemoveButton.Click's refusal: true when the UI may ask to confirm (then cancelMentor). */
  canCancel(): boolean {
    if (this._mentorName === "") { this.chat("You don't currently have a Mentorship to cancel.", ChatType.System); return false; }
    return true;
  }

  /** RemoveButton's confirm Yes: C.CancelMentor. */
  cancelMentor(): void {
    this.send(ClientPackets.CancelMentor());
  }

  /** GameScene.MentorRequest's Yes / No. */
  answerMentor(accept: boolean): void {
    this.send(ClientPackets.MentorReply({ AcceptInvite: accept }));
  }

  /** Server packets; true = consumed (the classic chain returned on both). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "MentorRequest": this.request.emit({ name: p.Name, level: p.Level }); return true;
      case "MentorUpdate": // GameScene.MentorUpdate
        this._mentorName = p.Name;
        this._mentorLevel = p.Level;
        this._mentorOnline = p.Online;
        this._menteeExp = p.MenteeEXP;
        this.changed.emit();
        return true;
    }
    return false;
  }
}

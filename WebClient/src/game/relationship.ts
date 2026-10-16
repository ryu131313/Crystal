// Marriage + mentor state: RelationshipDialog (LoverName, Date, MapName, MarriedDays) and MentorDialog (MentorName,
// MentorLevel, MentorOnline, MenteeEXP) with GameScene's handlers (MarriageRequest, DivorceRequest, LoverUpdate,
// MentorRequest, MentorUpdate) and the dialogs' button sends. game.ts feeds handle(); the UI follows `changed` and answers
// the request prompts (GameScene: MirMessageBox YesNo) with answerMarriage / answerDivorce / answerMentor.
//
// Phase 2.5 (web-only): S.MentorDetails (partner class, start and end dates; right after S.MentorUpdate while mentored)
// and the mentor finder (C.FindMentor -> S.MentorCandidates, 3 s throttle; refusals arrive as server chat lines).

import type { ClientMentorCandidate, ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { Emitter } from "./emitter.ts";
import { dateTimeBinaryToMs } from "./accountDetails.ts";

/** The server's C.FindMentor cooldown. */
export const FIND_MENTOR_THROTTLE = 3000;

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
  private _hasDetails = false;
  private _partnerClass = 0;
  private _mentorDate = 0n;
  private _endsAt = 0n;
  private _candidates: ClientMentorCandidate[] = [];
  private lastFind = -Infinity;
  /** S.MentorCandidates applied (`candidates`). */
  readonly candidatesReceived = new Emitter<void>();

  /** @param now the page clock (performance.now) for the C.FindMentor throttle. */
  constructor(private readonly send: (f: Uint8Array) => void, private readonly chat: (text: string, type: number) => void,
    private readonly now: () => number = () => performance.now()) {}

  /** MentorDialog.MentorName: the partner (mentor or mentee; "" = none). */
  get mentorName(): string { return this._mentorName; }
  /** MentorDialog.MentorLevel: the partner's level (0 = no mentorship). */
  get mentorLevel(): number { return this._mentorLevel; }
  get mentorOnline(): boolean { return this._mentorOnline; }
  /** MentorDialog.MenteeEXP. */
  get menteeExp(): bigint { return this._menteeExp; }

  /** S.MentorDetails received since the last S.MentorUpdate (false = no mentorship, or an older server). */
  get hasDetails(): boolean { return this._hasDetails; }
  /** The partner's MirClass (S.MentorDetails; 0 without details). */
  get partnerClass(): number { return this._partnerClass; }
  /** CharacterInfo.MentorDate (DateTime.ToBinary; 0n without details) / as Unix ms (0 without details). */
  get mentorDate(): bigint { return this._mentorDate; }
  get mentorDateMs(): number { return this._hasDetails ? dateTimeBinaryToMs(this._mentorDate) : 0; }
  /** MentorDate + Settings.MentorLength days (DateTime.ToBinary; 0n without details) / as Unix ms (0 without details). */
  get endsAt(): bigint { return this._endsAt; }
  get endsAtMs(): number { return this._hasDetails ? dateTimeBinaryToMs(this._endsAt) : 0; }
  /** The last S.MentorCandidates (at most 20 online players who could mentor the user). */
  get candidates(): readonly ClientMentorCandidate[] { return this._candidates; }

  /** C.FindMentor (web): candidates with a level in [minLevel, maxLevel] (u16, swapped when reversed). False = inside the
   *  3 s throttle (nothing sent). The server answers S.MentorCandidates, or a chat line (cooldown / not eligible). */
  findMentor(minLevel: number, maxLevel: number): boolean {
    const t = this.now();
    if (t - this.lastFind < FIND_MENTOR_THROTTLE) return false;
    const clamp = (v: number) => Math.min(0xffff, Math.max(0, Math.trunc(v) || 0));
    let lo = clamp(minLevel), hi = clamp(maxLevel);
    if (lo > hi) { const x = lo; lo = hi; hi = x; }
    this.lastFind = t;
    this.send(ClientPackets.FindMentor({ MinLevel: lo, MaxLevel: hi }));
    return true;
  }

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
        // S.MentorDetails follows while mentored. Keep the details while the partner is the same (an online/offline
        // update); drop them when the mentorship ends or the partner changes, until the next S.MentorDetails.
        if (p.Name === "" || p.Name !== this._mentorName) {
          this._hasDetails = false; this._partnerClass = 0; this._mentorDate = 0n; this._endsAt = 0n;
        }
        this._mentorName = p.Name;
        this._mentorLevel = p.Level;
        this._mentorOnline = p.Online;
        this._menteeExp = p.MenteeEXP;
        this.changed.emit();
        return true;
      case "MentorDetails": // web
        this._hasDetails = true;
        this._partnerClass = p.PartnerClass;
        this._mentorDate = p.MentorDate;
        this._endsAt = p.EndsAt;
        this.changed.emit();
        return true;
      case "MentorCandidates": // web
        this._candidates = p.Candidates;
        this.candidatesReceived.emit();
        return true;
    }
    return false;
  }
}

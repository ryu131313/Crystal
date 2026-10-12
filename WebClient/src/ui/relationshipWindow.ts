// Marriage + mentor: RelationshipDialog (L / Lover button) and MentorDialog (Mentor button) + GameScene's handlers
// (MarriageRequest, DivorceRequest, LoverUpdate, MentorRequest, MentorUpdate). Requests are answered in a non-blocking
// MirMessageBox (messageBox.ts) so the frame loop keeps running. The desktop's Mail Lover button is not ported (no mail yet).

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, MirClassName } from "../protocol/enums.generated.ts";
import { el } from "./screens.ts";
import { messageBox } from "./messageBox.ts";
import { dateFromBinary } from "../game/itemChanges.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

/** DateTime.FromBinary(v).Ticks (Kind bits dropped). */
function ticksOf(v: bigint): bigint {
  return BigInt.asUintN(62, v);
}

export class RelationshipWindow {
  // RelationshipDialog fields
  loverName = "";
  date = 0n;
  mapName = "";
  marriedDays = 0;
  private received = false;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly prefillChat: (text: string) => void;
  private readonly stage: HTMLElement;
  private readonly root = el("div", { class: "window relationship-window", hidden: true });
  private readonly nameLabel = el("div", {});
  private readonly dateLabel = el("div", {});
  private readonly lengthLabel = el("div", {});
  private readonly onlineLabel = el("div", {});
  private readonly allowButton = el("button", {});

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, prefillChat: (text: string) => void) {
    this.stage = stage;
    this.send = send;
    this.chat = chat;
    this.prefillChat = prefillChat;
    // RelationshipDialog ctor: AllowButton -> C.ChangeMarriage, Request / Divorce / Whisper with their client checks
    this.allowButton.onclick = () => this.send(ClientPackets.ChangeMarriage());
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Relationship"), el("button", { class: "window-close", onclick: () => this.toggle(false), title: "Close (L)" }, "×")),
      this.nameLabel, this.dateLabel, this.lengthLabel, this.onlineLabel,
      el("div", { class: "row" },
        el("button", { title: "Request marriage with the player you face", onclick: () => this.request() }, "Request Marriage"),
        el("button", { onclick: () => this.divorce() }, "Request Divorce")),
      el("div", { class: "row" }, this.allowButton, el("button", { onclick: () => this.whisper() }, "Whisper Lover")),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);
    // KeybindOptions.Relationship = L (GameScene key handler)
    window.addEventListener("keydown", (e) => {
      if (matches(e, KeybindOptions.Relationship) && !(document.activeElement instanceof HTMLInputElement) && !(document.activeElement instanceof HTMLTextAreaElement)) this.toggle();
    }, { signal: gameSignal() });
    this.updateInterface();
  }

  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
  }

  /** RelationshipDialog.RequestButton.Click. */
  private request(): void {
    if (this.loverName !== "") { this.chat("You're already married.", ChatType.System); return; }
    this.send(ClientPackets.MarriageRequest());
  }

  /** RelationshipDialog.DivorceButton.Click. */
  private divorce(): void {
    if (this.loverName === "") { this.chat("You're not married.", ChatType.System); return; }
    this.send(ClientPackets.DivorceRequest());
  }

  /** RelationshipDialog.WhisperButton.Click: ":)" in the chat box (the lover chat prefix). */
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
        this.received = true;
        this.updateInterface();
        return true;
    }
    return false;
  }

  /** RelationshipDialog.UpdateInterface. */
  private updateInterface(): void {
    this.nameLabel.textContent = `Lover:  ${this.loverName}`;
    this.onlineLabel.textContent = this.mapName !== "" ? `Location:  ${this.mapName}` : "Location:  Offline";
    const ticks = ticksOf(this.date);
    const shortDate = () => dateFromBinary(this.date).toLocaleDateString();
    if (this.loverName === "" && this.date !== 0n) {
      if (ticks < 2000n) { // Date < new DateTime(2000) (ticks)
        this.dateLabel.textContent = "Date: ";
        this.lengthLabel.textContent = "Length: ";
      } else {
        this.dateLabel.textContent = `Divorced Date:  ${shortDate()}`;
        this.lengthLabel.textContent = `Time Since: ${this.marriedDays} Days`;
      }
      this.onlineLabel.textContent = "Location: ";
      this.allowButton.textContent = "Allow/Block Marriage";
    } else {
      this.dateLabel.textContent = `Marriage Date:  ${ticks === 0n ? "" : shortDate()}`;
      this.lengthLabel.textContent = `Length: ${this.marriedDays} Days`;
      // the same C.ChangeMarriage toggles recall while married (AllowButton.Hint)
      this.allowButton.textContent = "Allow/Block Recall";
    }
    if (!this.received) this.allowButton.textContent = "Allow/Block Marriage"; // the ctor hint, before any LoverUpdate
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
  private readonly root = el("div", { class: "window mentor-window", hidden: true });
  private readonly info = el("div", { class: "mentor-info" });

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, me: () => MentorUser) {
    this.stage = stage;
    this.send = send;
    this.chat = chat;
    this.me = me;
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Mentor"), el("button", { class: "window-close", onclick: () => this.toggle(false) }, "×")),
      this.info,
      el("div", { class: "row" },
        // MentorDialog.AllowButton: C.AllowMentor toggles on the server (the desktop's button image never changes either)
        el("button", { title: "Allow/Disallow Mentor Requests", onclick: () => this.send(ClientPackets.AllowMentor()) }, "Allow/Disallow"),
        el("button", { onclick: () => this.add() }, "Add Mentor"),
        el("button", { onclick: () => this.remove() }, "Remove")),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);
    this.updateInterface();
  }

  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
    if (show) this.updateInterface(); // the user's own level may have changed since the last MentorUpdate
  }

  /** MentorDialog.AddButton.Click (MirInputBox). */
  private add(): void {
    if (this.mentorLevel !== 0) { this.chat("You already have a Mentor.", ChatType.System); return; }
    const name = prompt("Please enter the name of the person you would like to be your Mentor.");
    if (name !== null) this.send(ClientPackets.AddMentor({ Name: name }));
  }

  /** MentorDialog.RemoveButton.Click. */
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
    if (this.mentorLevel === 0) {
      this.info.replaceChildren(el("div", { class: "magic-meta" }, "No mentorship."));
      return;
    }
    const me = this.me();
    const row = (title: string, name: string, level: number, online: boolean) => el("div", { class: "mentor-row" },
      el("span", { class: "mentor-title" }, title), el("span", { class: "mentor-name" }, name), el("span", {}, `Lv ${level}`),
      el("span", { class: "mentor-online" }, online ? "ONLINE" : ""));
    if (me.level > this.mentorLevel) {
      this.info.replaceChildren(
        row("MENTOR", me.name, me.level, false),
        row("MENTEE", this.mentorName, this.mentorLevel, this.mentorOnline),
        el("div", {}, `MENTEE EXP: ${this.menteeExp}`));
    } else {
      this.info.replaceChildren(
        row("MENTOR", this.mentorName, this.mentorLevel, this.mentorOnline),
        row("MENTEE", me.name, me.level, false));
    }
  }
}

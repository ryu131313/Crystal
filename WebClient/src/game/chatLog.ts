// The chat log: the state half of ChatDialog / ChatControlBar (Client/MirScenes/Dialogs/MainDialogs.cs) and
// MirScene.ChatItemList, without the DOM. Every chat line of the game (S.Chat, S.ObjectChat, the windows' system lines)
// goes through receive(); each UI renders the lines from the `added` event (word wrap and colours are the view's).
//   receive        ChatDialog.ReceiveChat: the message kept in full (no cap, like the desktop's History), an
//                  `announcement` first for ChatType.Announcement (ChatNoticeDialog.ShowNotice, with
//                  RegexFunctions.CleanChatString applied), then `added`.
//   submit         ChatTextBox_KeyPress Enter: '@LEVELEFFECT' / '@TARGETDEAD' flip the local settings, "@light new|old"
//                  stays local (lightingEnhanced.ts), else C.Chat with the links still in the text; '/' sets LastPM.
//   links          ChatDialog.LinkedItems (MirItemCell Shift+right-click); cleared when the text box closes.
//   prefix         ChatDialog.ChatPrefix, picked by the ChatControlBar tabs (CHAT_TAB_PREFIX).
//   chatItems      MirScene.ChatItemList (S.NewChatItem), the items behind incoming "<Title/UniqueID>" links.
//   chatVisible    ChatDialog.Update's [Filters] check, a pure function of the settings.

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { gameSettings, setSetting, type GameSettings } from "./settings.ts";
import { tryLightingCommand } from "./lightingEnhanced.ts";
import { Emitter } from "./emitter.ts";

/** Globals.MaxChatLength. */
export const MAX_CHAT_LENGTH = 80;
/** RegexFunctions.ChatItemLinks: "<Title/UniqueID>". */
export const CHAT_LINK = /<([^<>]*?)\/(\d+)>/g;

/** One received chat message (ChatDialog.ReceiveChat's text + ChatType). */
export interface ChatMessage { readonly text: string; readonly type: number }

/** ChatDialog.LinkedItems: ChatItem (UniqueID, Title, Grid). */
export interface ChatLink { UniqueID: bigint; Title: string; Grid: number }

/** ChatControlBar.ToggleChatFilter: the prefix each chat bar tab puts in front of the typed message. */
export const CHAT_TAB_PREFIX: Readonly<Record<"All" | "Shout" | "Whisper" | "Group" | "Guild" | "Lover" | "Mentor", string>> = {
  All: "", Shout: "!", Whisper: "/", Group: "!!", Guild: "!~", Lover: ":)", Mentor: "!#",
};
export type ChatTab = keyof typeof CHAT_TAB_PREFIX;

type FilterSettings = Pick<GameSettings, "FilterNormalChat" | "FilterWhisperChat" | "FilterShoutChat" | "FilterSystemChat" | "FilterGroupChat" | "FilterGuildChat">;

/** ChatDialog.Update: whether a line of this ChatType shows under the FilterXChat settings. The desktop has no case for
 *  Relationship / Mentor, so the Lover / Mentor filters hide nothing (kept). */
export function chatVisible(type: number, s: FilterSettings = gameSettings): boolean {
  switch (type) {
    case ChatType.Normal:
    case ChatType.LineMessage:
      return !s.FilterNormalChat;
    case ChatType.WhisperIn:
    case ChatType.WhisperOut:
      return !s.FilterWhisperChat;
    case ChatType.Shout:
    case ChatType.Shout2:
    case ChatType.Shout3:
      return !s.FilterShoutChat;
    case ChatType.System:
    case ChatType.System2:
      return !s.FilterSystemChat;
    case ChatType.Group:
      return !s.FilterGroupChat;
    case ChatType.Guild:
      return !s.FilterGuildChat;
  }
  return true;
}

export class ChatLog {
  /** A message arrived (after `announcement` for an Announcement). */
  readonly added = new Emitter<ChatMessage>();
  /** ChatNoticeDialog.ShowNotice: an Announcement's text with the item links cleaned. */
  readonly announcement = new Emitter<string>();
  /** MirScene.ChatItemList by UniqueID (S.NewChatItem); read with chatItem(). */
  private readonly chatItems = new Map<string, UserItem>();
  private readonly _messages: ChatMessage[] = [];
  private _lastPM = "";
  private _prefix = "";
  private _links: ChatLink[] = [];
  private readonly send: (f: Uint8Array) => void;

  constructor(send: (f: Uint8Array) => void) {
    this.send = send;
  }

  /** ChatDialog.LastPM: the "/name" of the last whisper sent ('/' starts the text box with it). */
  get lastPM(): string { return this._lastPM; }
  /** ChatDialog.ChatPrefix (the selected chat bar tab's prefix). */
  get prefix(): string { return this._prefix; }
  /** ChatDialog.LinkedItems. */
  get links(): readonly ChatLink[] { return this._links; }
  /** Every received message, oldest first (ChatDialog.History before the filter). */
  get messages(): readonly ChatMessage[] { return this._messages; }

  /** MirScene.NewChatItem. Never consumes the packet. */
  handle(p: ServerPacket): boolean {
    if (p.type === "NewChatItem") this.chatItems.set(String(p.Item.UniqueID), p.Item);
    return false;
  }

  /** The item behind an incoming chat link (GameScene.ChatItemList lookup). */
  chatItem(uniqueId: string): UserItem | undefined {
    return this.chatItems.get(uniqueId);
  }

  /** ChatDialog.ReceiveChat. */
  receive(text: string, type: number): void {
    if (type === ChatType.Announcement) this.announcement.emit(text.replace(CHAT_LINK, "$1")); // RegexFunctions.CleanChatString
    const m: ChatMessage = { text, type };
    this._messages.push(m);
    this.added.emit(m);
  }

  /** ChatControlBar.ToggleChatFilter: ChatDialog.ChatPrefix from the tab. */
  selectTab(tab: ChatTab): void {
    this._prefix = CHAT_TAB_PREFIX[tab];
  }

  /** MirItemCell Shift+right-click: one ChatDialog.LinkedItems entry (the view checks the length and adds "<Title> "). */
  addLink(link: ChatLink): void {
    this._links.push(link);
  }

  /** The text box closed (sent or cancelled): ChatDialog.LinkedItems.Clear. */
  clearLinks(): void {
    this._links = [];
  }

  /** ChatTextBox_KeyPress Enter with `msg` (the view closes the text box afterwards, which clears the links). */
  submit(msg: string): void {
    if (!msg) return;
    if (msg.toUpperCase() === "@LEVELEFFECT") setSetting("LevelEffect", !gameSettings.LevelEffect);
    if (msg.toUpperCase() === "@TARGETDEAD") setSetting("TargetDead", !gameSettings.TargetDead);
    if (tryLightingCommand(msg, (t, ty) => this.receive(t, ty))) return;
    this.send(ClientPackets.Chat({ Message: msg, LinkedItems: this._links.filter((i) => msg.includes(`<${i.Title}>`)) }));
    if (msg[0] === "/") this._lastPM = msg.split(" ")[0]!;
  }
}

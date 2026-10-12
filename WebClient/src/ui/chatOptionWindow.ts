// Chat options: port of ChatOptionDialog (Client/MirScenes/Dialogs/ChatOptionDialog.cs) with the desktop artwork (Title
// 466 / 467 per tab, Title 462-465 tab buttons, Prguse 2070-2087 filter buttons, Title 470-475 transparency buttons),
// opened by the chat bar's settings button (ChatControlBar.SettingsButton; no key) (chatDialog.ts). Filter tab: All +
// one toggle per chat group (Settings.FilterXChat = hide); Chat tab: transparency (Settings.TransparentChat).
// `chatVisible` ports ChatDialog.Update.
// Note: the desktop Show*Chat values are never read for display (the chat bar tabs only pick the message prefix, see
// CHAT_TAB_PREFIX), and Update has no case for Relationship / Mentor, so the Lover / Mentor filters hide nothing there;
// both quirks are kept.

import { KeybindOptions, matches } from "../game/keybinds.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { gameSettings, setSetting, onSettingChange, type GameSettings } from "../game/settings.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { MirWindow, MirButton, closeButton, Lib } from "./mir/controls.ts";
import { el } from "./screens.ts";

type FilterKey = "FilterNormalChat" | "FilterWhisperChat" | "FilterShoutChat" | "FilterSystemChat" | "FilterLoverChat" | "FilterMentorChat" | "FilterGroupChat" | "FilterGuildChat";

/** The filter buttons: setting, Prguse index when the filter is on (hiding) — off is index + 1 — and Location. */
const FILTERS: [FilterKey, number, [number, number]][] = [
  ["FilterNormalChat", 2070, [40, 69]], ["FilterWhisperChat", 2074, [40, 92]], ["FilterShoutChat", 2072, [40, 115]], ["FilterSystemChat", 2084, [40, 138]],
  ["FilterLoverChat", 2076, [135, 69]], ["FilterMentorChat", 2078, [135, 92]], ["FilterGroupChat", 2080, [135, 115]], ["FilterGuildChat", 2082, [135, 138]],
];

/** ChatDialog.Update: whether a line of this ChatType shows under the current FilterXChat settings. */
export function chatVisible(type: number): boolean {
  switch (type) {
    case ChatType.Normal:
    case ChatType.LineMessage:
      return !gameSettings.FilterNormalChat;
    case ChatType.WhisperIn:
    case ChatType.WhisperOut:
      return !gameSettings.FilterWhisperChat;
    case ChatType.Shout:
    case ChatType.Shout2:
    case ChatType.Shout3:
      return !gameSettings.FilterShoutChat;
    case ChatType.System:
    case ChatType.System2:
      return !gameSettings.FilterSystemChat;
    case ChatType.Group:
      return !gameSettings.FilterGroupChat;
    case ChatType.Guild:
      return !gameSettings.FilterGuildChat;
  }
  return true;
}

/** ChatDialog.Update over the whole history: hides / shows every child line that carries `data-chat-type`. */
export function refilterChat(log: HTMLElement): void {
  for (const line of Array.from(log.children) as HTMLElement[]) {
    const t = line.dataset.chatType;
    if (t !== undefined) line.hidden = !chatVisible(Number(t));
  }
}

/** ChatControlBar.ToggleChatFilter: the prefix each chat bar tab puts in front of the typed message. */
export const CHAT_TAB_PREFIX: Readonly<Record<"All" | "Shout" | "Whisper" | "Group" | "Guild" | "Lover" | "Mentor", string>> = {
  All: "", Shout: "!", Whisper: "/", Group: "!!", Guild: "!~", Lover: ":)", Mentor: "!#",
};

export class ChatOptionWindow {
  private readonly win: MirWindow;
  private readonly filterTab: MirButton;
  private readonly chatTab: MirButton;
  private readonly allButton: MirButton;
  private readonly filterButtons = new Map<FilterKey, [MirButton, number]>();
  private readonly transOn: MirButton;
  private readonly transOff: MirButton;
  private readonly chatLog: HTMLElement | null;

  /** `chatLog`: the chat history element; refiltered (`refilterChat`) and given the `chat-transparent` class on change. */
  constructor(_stage: HTMLElement, chatLog: HTMLElement | null = null) {
    this.chatLog = chatLog;

    // ChatOptionDialog ctor
    const win = this.win = new MirWindow({ library: Lib.Title, index: 466, size: [224, 180], movable: true, sort: true, location: "center", onShow: () => this.render() });
    this.filterTab = new MirButton({ parent: win, library: Lib.Title, index: 463, pressedIndex: 462, location: [8, 8], sound: SoundList.ButtonA, onClick: () => this.switchTab(0) });
    this.chatTab = new MirButton({ parent: win, library: Lib.Title, index: 464, pressedIndex: 465, location: [78, 8], sound: SoundList.ButtonA, onClick: () => this.switchTab(1) });
    closeButton(win, [198, 3], () => this.toggle(false));

    // #region Filters
    this.allButton = new MirButton({ parent: win, library: Lib.Prguse, index: 2087, location: [74, 47], size: [16, 12], sound: SoundList.ButtonA, onClick: () => this.toggleAllFilters() });
    for (const [key, onIndex, location] of FILTERS) {
      const b = new MirButton({ parent: win, library: Lib.Prguse, index: onIndex + 1, location, sound: SoundList.ButtonA, onClick: () => setSetting(key, !gameSettings[key]) });
      this.filterButtons.set(key, [b, onIndex]);
    }
    // #region Transparency
    this.transOff = new MirButton({ parent: win, library: Lib.Title, index: 471, hoverIndex: 472, pressedIndex: 470, location: [45, 90], sound: SoundList.ButtonA, visible: false,
      onClick: () => setSetting("TransparentChat", false) });
    this.transOn = new MirButton({ parent: win, library: Lib.Title, index: 474, hoverIndex: 475, pressedIndex: 473, location: [115, 90], sound: SoundList.ButtonA, visible: false,
      onClick: () => setSetting("TransparentChat", true) });

    const off = onSettingChange((key: keyof GameSettings) => {
      if (key.startsWith("Filter") && this.chatLog) refilterChat(this.chatLog); // CheckAllFilters -> ChatDialog.Update
      if (key === "TransparentChat") this.updateTransparency();
      this.render();
    });
    const signal = gameSignal();
    signal.addEventListener("abort", () => { off(); win.dispose(); });
    window.addEventListener("keydown", (e) => { if (matches(e, KeybindOptions.Closeall) && this.win.visible) this.toggle(false); }, { signal });
    this.switchTab(0);
    this.updateTransparency();
    this.render();
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** ChatOptionDialog.Toggle. */
  toggle(show = !this.win.visible): void {
    if (show) this.win.show(); else this.win.hide();
  }

  /** ChatOptionDialog.SwitchTab: tab artwork, the dialog's Index (466 / 467) and which buttons show. */
  private switchTab(tab: 0 | 1): void {
    if (tab === 0) {
      this.filterTab.setIndices(463, -1, 462);
      this.chatTab.setIndices(464, -1, 465);
      this.win.index = 466;
    } else {
      this.filterTab.setIndices(462, -1, 463);
      this.chatTab.setIndices(465, -1, 464);
      this.win.index = 467;
    }
    this.allButton.visible = tab === 0;
    for (const [b] of this.filterButtons.values()) b.visible = tab === 0;
    this.transOff.visible = tab === 1;
    this.transOn.visible = tab === 1;
  }

  /** ChatOptionDialog.CheckAllFilters (AllFiltersOff). */
  private allFiltersOff(): boolean {
    return FILTERS.every(([k]) => !gameSettings[k]);
  }

  /** ChatOptionDialog.ToggleAllFilters: all on when none is on, else all off. */
  private toggleAllFilters(): void {
    const value = this.allFiltersOff();
    for (const [k] of FILTERS) setSetting(k, value);
  }

  /** ChatOptionDialog.UpdateTransparency: the desktop dims the chat dialog to 0.8 opacity (the web chat log gets the
   *  `chat-transparent` class) and swaps the On / Off button art. */
  private updateTransparency(): void {
    this.chatLog?.classList.toggle("chat-transparent", gameSettings.TransparentChat);
    if (gameSettings.TransparentChat) {
      this.transOn.setIndices(474, 475, 473);
      this.transOff.setIndices(470, 470, 470);
    } else {
      this.transOn.setIndices(473, 473, 473);
      this.transOff.setIndices(471, 472, 470);
    }
  }

  /** ChatFilterPanel_BeforeDraw. */
  private render(): void {
    this.allButton.index = this.allFiltersOff() ? 2087 : 2086;
    for (const [k, [b, onIndex]] of this.filterButtons) b.index = gameSettings[k] ? onIndex : onIndex + 1;
  }
}

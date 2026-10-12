// Chat options: port of ChatOptionDialog (Client/MirScenes/Dialogs/ChatOptionDialog.cs), opened by the chat bar's settings
// button (ChatControlBar.SettingsButton; no key) — here the hudButton. Filter tab: All + one toggle per chat group
// (Settings.FilterXChat = hide); Chat tab: transparency (Settings.TransparentChat). `chatVisible` ports ChatDialog.Update.
// Note: the desktop Show*Chat values are never read for display (the chat bar tabs only pick the message prefix, see
// CHAT_TAB_PREFIX), and Update has no case for Relationship / Mentor, so the Lover / Mentor filters hide nothing there;
// both quirks are kept.

import { KeybindOptions, matches } from "../game/keybinds.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { gameSettings, setSetting, onSettingChange, type GameSettings } from "../game/settings.ts";
import { gameSignal } from "../game/scope.ts";
import { el } from "./screens.ts";

type FilterKey = "FilterNormalChat" | "FilterWhisperChat" | "FilterShoutChat" | "FilterSystemChat" | "FilterLoverChat" | "FilterMentorChat" | "FilterGroupChat" | "FilterGuildChat";

const FILTERS: [FilterKey, string][] = [
  ["FilterNormalChat", "General"], ["FilterWhisperChat", "Whisper"], ["FilterShoutChat", "Shout"], ["FilterSystemChat", "System"],
  ["FilterLoverChat", "Lover"], ["FilterMentorChat", "Mentor"], ["FilterGroupChat", "Group"], ["FilterGuildChat", "Guild"],
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
  readonly hudButton: HTMLButtonElement;
  private readonly root = el("div", { class: "window chat-option-window", hidden: true });
  private readonly filterTab = el("button", { class: "chat-option-tab" }, "Filter");
  private readonly chatTab = el("button", { class: "chat-option-tab" }, "Chat");
  private readonly filterPage = el("div", { class: "chat-option-page" });
  private readonly chatPage = el("div", { class: "chat-option-page" });
  private readonly allButton = el("button", { class: "chat-filter" }, "All");
  private readonly filterButtons = new Map<FilterKey, HTMLButtonElement>();
  private readonly transOn = el("button", {}, "On");
  private readonly transOff = el("button", {}, "Off");
  private readonly chatLog: HTMLElement | null;

  /** `chatLog`: the chat history element; refiltered (`refilterChat`) and given the `chat-transparent` class on change. */
  constructor(stage: HTMLElement, chatLog: HTMLElement | null = null) {
    this.chatLog = chatLog;
    this.hudButton = el("button", { title: "Chat settings", onclick: () => this.toggle() }, "Chat") as HTMLButtonElement;
    this.filterTab.onclick = () => this.switchTab(0);
    this.chatTab.onclick = () => this.switchTab(1);
    this.allButton.onclick = () => this.toggleAllFilters();
    for (const [key, label] of FILTERS) {
      const b = el("button", { class: "chat-filter", title: `Hide ${label.toLowerCase()} chat`, onclick: () => setSetting(key, !gameSettings[key]) }, label) as HTMLButtonElement;
      this.filterButtons.set(key, b);
    }
    this.filterPage.append(el("div", { class: "chat-filter-grid" }, this.allButton, ...this.filterButtons.values()),
      el("div", { class: "chat-option-note" }, "Highlighted types are hidden."));
    this.transOn.onclick = () => setSetting("TransparentChat", true);
    this.transOff.onclick = () => setSetting("TransparentChat", false);
    this.chatPage.append(el("div", { class: "option-row" }, el("span", {}, "Transparency"), el("span", { class: "option-pair" }, this.transOn, this.transOff)));
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Chat Options"), el("button", { class: "window-close", onclick: () => this.toggle(false) }, "×")),
      el("div", { class: "chat-option-tabs" }, this.filterTab, this.chatTab),
      this.filterPage, this.chatPage,
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);

    const off = onSettingChange((key: keyof GameSettings) => {
      if (key.startsWith("Filter") && this.chatLog) refilterChat(this.chatLog);
      if (key === "TransparentChat") this.updateTransparency();
      this.render();
    });
    const signal = gameSignal();
    signal.addEventListener("abort", off);
    window.addEventListener("keydown", (e) => { if (matches(e, KeybindOptions.Closeall) && !this.root.hidden) this.toggle(false); }, { signal });
    this.switchTab(0);
    this.updateTransparency();
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
    if (show) this.render();
  }

  /** ChatOptionDialog.SwitchTab. */
  private switchTab(tab: 0 | 1): void {
    this.filterPage.hidden = tab !== 0;
    this.chatPage.hidden = tab !== 1;
    this.filterTab.classList.toggle("active", tab === 0);
    this.chatTab.classList.toggle("active", tab === 1);
  }

  /** ChatOptionDialog.CheckAllFilters. */
  private allFiltersOff(): boolean {
    return FILTERS.every(([k]) => !gameSettings[k]);
  }

  /** ChatOptionDialog.ToggleAllFilters: all on when none is on, else all off. */
  private toggleAllFilters(): void {
    const value = this.allFiltersOff();
    for (const [k] of FILTERS) setSetting(k, value);
  }

  /** ChatOptionDialog.UpdateTransparency (the desktop dims the chat dialog to 0.8 opacity). */
  private updateTransparency(): void {
    this.chatLog?.classList.toggle("chat-transparent", gameSettings.TransparentChat);
  }

  /** ChatFilterPanel_BeforeDraw. */
  private render(): void {
    this.allButton.classList.toggle("active", !this.allFiltersOff());
    for (const [k, b] of this.filterButtons) b.classList.toggle("active", gameSettings[k]);
    this.transOn.classList.toggle("active", gameSettings.TransparentChat);
    this.transOff.classList.toggle("active", !gameSettings.TransparentChat);
  }
}

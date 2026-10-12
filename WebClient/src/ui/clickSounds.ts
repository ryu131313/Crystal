// UI click sounds: MirControl.OnMouseClick plays the control's Sound when it is enabled. Almost every desktop button sets
// SoundList.ButtonA (MirButton's own default is ButtonB; the chat Trade button uses ButtonC), so one delegated listener plays
// ButtonA for any enabled <button> under `root`. A button can override with `data-sound="<SoundList id>"` or opt out with
// `data-sound="none"`.

import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";

export function installClickSounds(root: HTMLElement, play: (id: number) => void, signal: AbortSignal = gameSignal()): void {
  root.addEventListener("click", (e) => {
    const target = e.target;
    if (!(target instanceof Element)) return;
    const button = target.closest("button");
    if (!button || !root.contains(button) || button.disabled) return;
    const s = button.dataset.sound;
    if (s === "none") return;
    const id = s ? Number(s) : SoundList.ButtonA;
    if (Number.isFinite(id)) play(id);
  }, { capture: true, signal }); // capture: still heard when a handler stops propagation
}

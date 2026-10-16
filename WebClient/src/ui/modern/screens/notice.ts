// The new UI's MirMessageBox outside a game runtime (web-only, plan phase 4): the message Screens shows over the account
// screens (GameScene.ReturnToLogin's "observed player logged off") and the disconnect box (MirScene S.Disconnect /
// Network disconnect -> fatal). A themed AccountRoot (accountRoot.ts) in a full-window layer on document.body, above
// #stage like the classic messageBox, with one core/popups.ts box; the root and its layer are removed when the box
// closes. Keys: the AccountRoot's element-level listener (the box takes focus), no document / window listener.

import { injectCss } from "../core/dom.ts";
import { AccountRoot } from "./accountRoot.ts";

const CSS = `
.acct-notice-layer { position: fixed; inset: 0; z-index: 1000; pointer-events: none; }
.hudm.acct.acct-notice { background: transparent; pointer-events: none; }
`;

export interface NoticeOptions {
  title?: string;
  /** "warning" for errors (disconnects), else a plain notice. */
  kind?: "info" | "warning";
  /** OK pressed (Enter / click); not called when the box is closed from outside. */
  onOk?: () => void;
}

/** Shows a one-button box; returns its closer (closing it does not call onOk). */
export function showNotice(text: string, opts: NoticeOptions = {}): () => void {
  injectCss("screens-notice", CSS);
  const layer = document.createElement("div");
  layer.className = "acct-notice-layer";
  document.body.append(layer);
  const acct = new AccountRoot(layer, { cls: "acct-notice" });
  let closed = false;
  const close = () => {
    if (closed) return;
    closed = true;
    acct.dispose();
    layer.remove();
  };
  void acct.popups.confirm({ text, title: opts.title, kind: opts.kind ?? "info", buttons: "ok" }).then((r) => {
    const ok = !closed && r?.answer === "ok";
    close();
    if (ok) opts.onOk?.();
  });
  return close;
}

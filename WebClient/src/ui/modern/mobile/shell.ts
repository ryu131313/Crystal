// The page-level side of the touch layout (Modern UI, settings.ts isMobileUi): no browser zoom / scroll / pull-to-refresh,
// full screen + landscape lock on the first tap, and a portrait "rotate your phone" cover. Installed once by play/main.ts
// before any screen; everything inside the game UI lives in mobileUi.ts. No key listeners (one input router: input.ts).

import { h, injectCss } from "../core/dom.ts";

const CSS = `
html.mobile-ui, html.mobile-ui body { touch-action: none; overscroll-behavior: none; -webkit-user-select: none; user-select: none;
  -webkit-touch-callout: none; -webkit-tap-highlight-color: transparent; -webkit-text-size-adjust: 100%; }
html.mobile-ui #stage { cursor: default; }
/* iOS: pin the page so a rotation can never leave it scrolled (the stage off screen = a black page) */
html.mobile-ui, html.mobile-ui body { position: fixed; inset: 0; width: 100%; height: 100%; overflow: hidden; }
html.mobile-ui input, html.mobile-ui textarea { -webkit-user-select: text; user-select: text; }
.mobile-rotate { display: none; }
@media (orientation: portrait) {
  html.mobile-ui .mobile-rotate { display: flex; position: fixed; inset: 0; z-index: 2147483647; flex-direction: column; gap: 18px;
    align-items: center; justify-content: center; background: #0b0d12; color: #dfe6f2; font: 600 18px/1.4 system-ui, sans-serif; text-align: center; padding: 24px; }
  html.mobile-ui .mobile-rotate svg { width: 84px; height: 84px; animation: mobile-rotate 2.4s ease-in-out infinite; }
}
/* The modern account screens (screens/login.ts, select.ts) on a phone: the stage is 768 high and as wide as the phone, so
   the panels grow to finger size and keep to the edges. */
html.mobile-ui .hudm .lgscr .logo { left: auto; right: 48px; top: 250px; transform: scale(1.25); transform-origin: top right; }
html.mobile-ui .hudm .lgscr .card { left: 36px; top: 14px; max-height: 510px; transform: scale(1.45); transform-origin: top left; animation: none; }
html.mobile-ui .hudm .lgscr .kbd { display: none; }
html.mobile-ui .hudm .lgscr .fld input { font-size: 16px; }
html.mobile-ui .hudm .acsel .roster, html.mobile-ui .hudm .acsel .classes { top: 66px; transform: scale(1.45); transform-origin: top left; max-height: 440px; overflow-y: auto; }
html.mobile-ui .hudm .acsel .info { top: 66px; transform: scale(1.4); transform-origin: top right; max-height: 330px; }
html.mobile-ui .hudm .acsel .start { transform: scale(1.4); transform-origin: bottom center; bottom: 24px; }
html.mobile-ui .hudm .acsel .top { zoom: 1.3; }
html.mobile-ui .hudm .acsel input.nmi { font-size: 16px; }
.mobile-a2hs { position: fixed; left: 50%; bottom: calc(10px + env(safe-area-inset-bottom)); transform: translateX(-50%); z-index: 2147483646;
  display: flex; gap: 12px; align-items: center; padding: 10px 14px; border-radius: 12px; background: rgba(12,16,28,.94); color: #dfe6f2;
  border: 1px solid rgba(231,195,90,.6); font: 600 14px/1.3 system-ui, sans-serif; box-shadow: 0 6px 24px rgba(0,0,0,.6); }
.mobile-a2hs button { min-height: 40px; padding: 0 14px; border-radius: 8px; border: 1px solid #e7c35a; background: #2a2412; color: #f2cf5b; font: inherit; }
@keyframes mobile-rotate { 0%, 30% { transform: rotate(0); } 60%, 100% { transform: rotate(-90deg); } }
@media (prefers-reduced-motion: reduce) { html.mobile-ui .mobile-rotate svg { animation: none; } }
`;

const PHONE = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
<rect x="7" y="2.5" width="10" height="19" rx="2"/><path d="M11 18.5h2"/></svg>`;

let installed = false;

export function installMobileShell(): void {
  if (installed) return;
  installed = true;
  document.documentElement.classList.add("mobile-ui");
  const meta = document.querySelector<HTMLMetaElement>('meta[name="viewport"]');
  if (meta) meta.content = "width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover, interactive-widget=overlays-content";
  injectCss("mobile-shell", CSS);

  const cover = h("div", "mobile-rotate");
  cover.insertAdjacentHTML("afterbegin", PHONE); // constant SVG
  cover.append(h("div", "", "Turn your phone sideways to play"));
  document.body.append(cover);

  // iOS Safari ignores user-scalable=no for pinch: cancel its gesture events.
  const stop = (e: Event) => e.preventDefault();
  document.addEventListener("gesturestart", stop, { passive: false });
  document.addEventListener("gesturechange", stop, { passive: false });

  // Full screen + landscape lock need a user gesture: try on taps until the page is full screen (iPhone has neither).
  const goFull = () => {
    if (document.fullscreenElement) return;
    const el = document.documentElement;
    try {
      const p = el.requestFullscreen?.({ navigationUI: "hide" });
      void p?.then(() => {
        const o = screen.orientation as ScreenOrientation & { lock?: (o: string) => Promise<void> };
        return o?.lock?.("landscape");
      }).catch(() => { /* not allowed: play in the browser chrome */ });
    } catch { /* no Fullscreen API */ }
  };
  document.addEventListener("pointerup", (e) => { if (!(e.target instanceof HTMLInputElement)) goFull(); });

  // iPhone / iPad Safari has no Fullscreen API or orientation lock: once, suggest Add to Home Screen (manifest.json).
  const ios = /iPhone|iPad|iPod/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  const standalone = matchMedia("(display-mode: standalone), (display-mode: fullscreen)").matches || (navigator as Navigator & { standalone?: boolean }).standalone === true;
  let seen = false;
  try { seen = localStorage.getItem(HINT_KEY) === "1"; } catch { /* storage blocked */ }
  if (ios && !standalone && !seen) {
    const close = h("button", "", "Got it");
    const hint = h("div", "mobile-a2hs", h("span", "", "For full screen: tap Share, then \u201cAdd to Home Screen\u201d."), close);
    close.addEventListener("click", () => { hint.remove(); try { localStorage.setItem(HINT_KEY, "1"); } catch { /* not kept */ } });
    document.body.append(hint);
  }
}

const HINT_KEY = "crystal.a2hsHint";

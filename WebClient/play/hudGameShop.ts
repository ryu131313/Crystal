// Game shop mock for the scene lab's UI tester (window style from hudWindow.ts), the "extra jazzed" take on the
// desktop's GameShopDialog, filled with the server's real catalogue (hudGameShopData.ts: Envir.GameShopList from a copy
// of the live Server.MirDB): a wallet (credits / gold, top up), a featured carousel of the top items and deals (auto-
// rotating, pauses on hover), categories with counts plus Top / Deals / Wishlist, search, sort and a class filter,
// product cards (glow, count, DEAL / TOP ribbons, "only N left", wishlist hearts, credit and gold prices), a product
// view (big icon on a spinning light, credits / gold choice where the item allows both, quantity within stock, Buy /
// Gift to a friend / Preview), a confirm step with the balance after, a confetti burst and a purchase history. Mock only:
// nothing is bought, the credits are pretend, and DEAL prices show a made-up 25 % saving.

import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";
import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { GAME_SHOP, type ShopRow } from "./hudGameShopData.ts";

interface Product { id: number; name: string; img: number; gold: number; credit: number; count: number; cls: string; cat: string; stock: number; deal: boolean; top: boolean; byGold: boolean; byCredit: boolean; type: string }
const PRODUCTS: Product[] = GAME_SHOP.map((r: ShopRow, id) => {
  const [name, img, gold, credit, count, cls, cat, stock, deal, top, byGold, byCredit, type] = r;
  return { id, name, img, gold, credit, count, cls, cat, stock, deal, top, byGold: byGold && gold > 0, byCredit: byCredit && credit > 0, type };
});
// A few limited-stock items so the "only N left" badges show (the export has none).
for (const [i, n] of [[2, 5], [18, 3], [33, 8]] as [number, number][]) if (PRODUCTS[i]) PRODUCTS[i]!.stock = n;
const CATS = [...new Set(PRODUCTS.map((p) => p.cat))];
const CAT_ICON: Record<string, string> = {
  Potion: "M9 3h6M10 3v5l-5 9a3 3 0 003 4h8a3 3 0 003-4l-5-9V3", Transform: "M4 8c3-5 13-5 16 0-2 8-14 8-16 0zM9 10h.01M15 10h.01",
  Special: "M12 2l2.8 6.2L21 9l-4.7 4.4 1.3 6.6L12 16.8 6.4 20l1.3-6.6L3 9l6.2-.8z", Scroll: "M6 3h10l2 2v16H8l-2-2zM9 8h6M9 12h6",
  Mount: "M5 19c2-6 5-9 9-10l3-4 1 4c2 1 3 3 3 5M7 19h3M15 19h3", Creature: "M12 13c-3 0-5 3-5 5s2 2 5 2 5 0 5-2-2-5-5-5zM6 11a1.8 1.8 0 100-3.6M10 7a1.8 1.8 0 100-3.6M14 7a1.8 1.8 0 100-3.6M18 11a1.8 1.8 0 100-3.6",
  Torch: "M12 3c3 4-1 5 2 8a4 4 0 01-8 0c0-3 3-4 6-8zM10 14h4l-1 7h-2z", Fishing: "M18 3v10a5 5 0 01-10 0v-2M8 11l-2-2", Package: "M3 8l9-5 9 5v8l-9 5-9-5zM3 8l9 5 9-5M12 13v8",
  Top: "M12 2l2.8 6.2L21 9l-4.7 4.4 1.3 6.6L12 16.8 6.4 20l1.3-6.6L3 9l6.2-.8z", Deals: "M3 12l9-9h7v7l-9 9zM15.5 7.5h.01", Wishlist: "M12 21s-7.5-4.6-9.5-9.3C1 8 3.4 4.5 7 4.5c2 0 3.6 1.1 5 3 1.4-1.9 3-3 5-3 3.6 0 6 3.5 4.5 7.2C19.5 16.4 12 21 12 21z",
  All: "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z",
};
const DESC: Record<string, string> = {
  Potion: "A potion brewed by the realm's finest alchemists. Use it from your bag or belt.", Transform: "Changes how your character looks for a while. Your stats stay the same.",
  Special: "A special item with a unique effect. Read it carefully before you use it.", Scroll: "A scroll; read it from your bag.", Mount: "A loyal mount. Ride it to travel faster.",
  Creature: "An intelligent creature companion that follows you and picks up loot.", Torch: "A torch that lights your way and empowers you while it burns.",
  Fishing: "Fishing gear for the realm's rivers and lakes.", Package: "A bundle of goodies at a better price.",
};
const FRIENDS = ["Seren", "Kaelith", "Wren", "Thorne", "Brannoc", "Nyra"];
const svg = (d: string, cls = "gs-ic") => { const s = document.createElementNS("http://www.w3.org/2000/svg", "svg"); s.setAttribute("viewBox", "0 0 24 24"); s.setAttribute("class", cls); const p = document.createElementNS("http://www.w3.org/2000/svg", "path"); p.setAttribute("d", d); s.append(p); return s; };
const fmt = (n: number) => n.toLocaleString("en-GB");
const pretty = (n: string) => n.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/\[/g, " [").replace(/(\D)(\d+)$/, "$1 $2");

const CSS = `
.hudm .gswin { --winh: 640px; width: 1000px; }
.hudm .gswin .panel { height: var(--winh); display: flex; flex-direction: column; overflow: hidden; }
.hudm .gs-ic { width: 14px; height: 14px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
/* Top bar */
.hudm .gswin .gsbar { display: flex; align-items: center; gap: 8px; padding: 9px 14px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .gswin .wal { display: flex; align-items: center; gap: 7px; padding: 4px 12px; border-radius: 16px; font-size: 13px; font-weight: 800; font-variant-numeric: tabular-nums; }
.hudm .gswin .wal.cr { color: #fff; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 40%, transparent), color-mix(in srgb, var(--ac) 15%, transparent)); border: 1px solid var(--ac);
  box-shadow: 0 0 calc(10px * var(--gl)) var(--glow); }
.hudm .gswin .wal.gd { color: #f2c94c; background: rgba(242, 201, 76, .08); border: 1px solid rgba(242, 201, 76, .4); }
.hudm .gswin .gem { width: 12px; height: 12px; transform: rotate(45deg); background: linear-gradient(135deg, #fff, var(--ac)); box-shadow: 0 0 8px var(--ac); flex: none; }
.hudm .gswin .coin { width: 13px; height: 13px; border-radius: 50%; background: radial-gradient(circle at 35% 35%, #ffe9a0, #c9921f); flex: none; }
.hudm .gswin .wal.bump { animation: hudm-walbump .6s ease-out; }
@keyframes hudm-walbump { 30% { transform: scale(1.15); box-shadow: 0 0 22px var(--ac); } }
.hudm .gswin .gsbar .sp { flex: 1; }
.hudm .gswin input.in, .hudm .gswin select { height: 28px; box-sizing: border-box; padding: 0 9px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: 14px; outline: none; }
.hudm .gswin input.in { width: 200px; } .hudm .gswin input.in:focus { border-color: var(--ac); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .gswin input.in::placeholder { color: var(--dim); }
.hudm .gswin .btn { height: 28px; border-radius: 14px; }
.hudm .gswin .btn.main { color: #fff; border-color: var(--ac); background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 55%, var(--pb)), color-mix(in srgb, var(--ac) 30%, var(--pb)));
  box-shadow: 0 0 0 1px #000, 0 0 calc(10px * var(--gl)) var(--glow); }
.hudm .gswin .btn.dis { opacity: .4; pointer-events: none; }
/* Hero carousel */
.hudm .gswin .hero { position: relative; height: 150px; margin: 10px 14px 0; border-radius: var(--rad); overflow: hidden; border: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); flex: none;
  background: radial-gradient(ellipse 50% 120% at 18% 50%, color-mix(in srgb, var(--ac) 35%, transparent), transparent 70%), linear-gradient(110deg, var(--pa), var(--pb) 60%, color-mix(in srgb, var(--ac) 18%, var(--pb))); }
.hudm .gswin .hero::before { content: ""; position: absolute; inset: 0; background: linear-gradient(105deg, transparent 30%, rgba(255,255,255,.08) 45%, transparent 60%); background-size: 250% 100%; animation: hudm-shine 4s linear infinite; }
@keyframes hudm-shine { from { background-position: 120% 0; } to { background-position: -130% 0; } }
.hudm .gswin .slide { position: absolute; inset: 0; display: flex; align-items: center; gap: 26px; padding: 0 30px 0 40px; animation: hudm-slide .5s ease-out; }
@keyframes hudm-slide { from { opacity: 0; transform: translateX(30px); } }
.hudm .gswin .slide .big { width: 110px; height: 110px; flex: none; position: relative; display: flex; align-items: center; justify-content: center; }
.hudm .gswin .slide .big::before { content: ""; position: absolute; inset: -10px; border-radius: 50%; background: conic-gradient(from 0deg, transparent, color-mix(in srgb, var(--ac) 45%, transparent), transparent 30%, color-mix(in srgb, var(--ac) 45%, transparent), transparent 60%);
  animation: hudm-spin 8s linear infinite; filter: blur(2px); }
@keyframes hudm-spin { to { transform: rotate(360deg); } }
.hudm .gswin .slide .big i { position: relative; width: 108px; height: 96px; background: center / contain no-repeat; image-rendering: pixelated; filter: drop-shadow(0 0 12px var(--glow)); }
.hudm .gswin .slide .tx { flex: 1; min-width: 0; }
.hudm .gswin .slide .kick { font-size: 10px; font-weight: 800; letter-spacing: .2em; text-transform: uppercase; color: var(--ac); text-shadow: 0 0 8px var(--glow); }
.hudm .gswin .slide h2 { margin: 4px 0; font-size: 26px; color: #fff; text-shadow: 0 2px 6px rgba(0,0,0,.7); }
.hudm .gswin .slide p { margin: 0; font-size: 12px; color: color-mix(in srgb, var(--tx) 80%, var(--dim)); }
.hudm .gswin .slide .cta { display: flex; flex-direction: column; align-items: flex-end; gap: 8px; }
.hudm .gswin .slide .price { font-size: 22px; font-weight: 800; color: #fff; display: flex; align-items: center; gap: 8px; font-variant-numeric: tabular-nums; }
.hudm .gswin .slide .price s { font-size: 13px; color: var(--dim); font-weight: 600; }
.hudm .gswin .slide .btn { height: 34px; padding: 0 22px; font-size: 13px; border-radius: 17px; }
.hudm .gswin .dots { position: absolute; left: 50%; bottom: 8px; transform: translateX(-50%); display: flex; gap: 6px; z-index: 2; }
.hudm .gswin .dots i { width: 7px; height: 7px; border-radius: 50%; cursor: pointer; background: rgba(255,255,255,.3); transition: width .2s; }
.hudm .gswin .dots i.on { width: 20px; border-radius: 4px; background: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .gswin .arw { position: absolute; top: 50%; transform: translateY(-50%); z-index: 2; width: 26px; height: 26px; border-radius: 50%; display: flex; align-items: center; justify-content: center; cursor: pointer;
  color: #fff; background: rgba(0,0,0,.45); border: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .gswin .arw:hover { background: color-mix(in srgb, var(--ac) 40%, #000); }
.hudm .gswin .arw.l { left: 8px; } .hudm .gswin .arw.r { right: 8px; }
/* Body */
.hudm .gswin .gbody { flex: 1; min-height: 0; display: flex; margin-top: 10px; }
.hudm .gswin .nav { width: 170px; flex: none; padding: 0 8px 10px 14px; display: flex; flex-direction: column; gap: 2px; overflow-y: auto; scrollbar-width: none; }
.hudm .gswin .nav div { display: flex; align-items: center; gap: 9px; height: 30px; padding: 0 10px; cursor: pointer; user-select: none; border-radius: var(--rad); font-size: 12px; font-weight: 600; color: var(--dim); border: 1px solid transparent; }
.hudm .gswin .nav div:hover { color: var(--tx); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .gswin .nav div.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .gswin .nav div.on .gs-ic { color: var(--ac); filter: drop-shadow(0 0 4px var(--glow)); }
.hudm .gswin .nav i { margin-left: auto; font-style: normal; font-size: 10px; color: var(--dim); }
.hudm .gswin .nav hr { border: 0; height: 1px; margin: 6px 4px; background: color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .gswin .grid { flex: 1; min-width: 0; padding: 0 14px 12px 4px; overflow-y: auto; display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); grid-auto-rows: max-content; gap: 10px; align-content: start;
  scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .gswin .pc { position: relative; display: flex; flex-direction: column; border-radius: var(--rad); cursor: pointer; overflow: hidden; transition: transform .15s, box-shadow .15s, border-color .15s;
  background: linear-gradient(180deg, color-mix(in srgb, var(--frl) 8%, transparent), color-mix(in srgb, var(--pb) 70%, transparent)); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .gswin .pc:hover { transform: translateY(-3px); border-color: var(--ac); box-shadow: 0 6px 18px rgba(0,0,0,.6), 0 0 calc(12px * var(--gl)) var(--glow); }
.hudm .gswin .pc .art { position: relative; height: 92px; display: flex; align-items: center; justify-content: center;
  background: radial-gradient(circle at 50% 55%, color-mix(in srgb, var(--ac) 22%, transparent), transparent 62%); }
.hudm .gswin .pc .art i { width: 72px; height: 64px; background: center / contain no-repeat; image-rendering: pixelated; filter: drop-shadow(0 4px 6px rgba(0,0,0,.6)); transition: transform .2s; }
.hudm .gswin .pc:hover .art i { transform: scale(1.08); }
.hudm .gswin .pc .cnt { position: absolute; right: 8px; bottom: 6px; font-size: 11px; font-weight: 800; color: #fff; text-shadow: 0 0 3px #000; }
.hudm .gswin .rib { position: absolute; left: -26px; top: 10px; z-index: 2; width: 96px; transform: rotate(-38deg); text-align: center; font-size: 9px; font-weight: 800; letter-spacing: .12em; color: #fff;
  background: linear-gradient(90deg, #d0283a, #ff5a4a); box-shadow: 0 2px 6px rgba(0,0,0,.5); }
.hudm .gswin .star { position: absolute; left: 7px; top: 6px; z-index: 2; color: #ffd75a; filter: drop-shadow(0 0 4px rgba(255, 210, 90, .8)); }
.hudm .gswin .star .gs-ic { fill: currentColor; stroke: #000; stroke-width: 1; width: 16px; height: 16px; }
.hudm .gswin .rib + .star { top: 30px; }
.hudm .gswin .heart { position: absolute; right: 6px; top: 6px; z-index: 2; width: 24px; height: 24px; display: flex; align-items: center; justify-content: center; border-radius: 50%; color: var(--dim);
  background: rgba(0,0,0,.35); }
.hudm .gswin .heart:hover { color: #fff; } .hudm .gswin .heart.on { color: #ff6a8a; } .hudm .gswin .heart.on .gs-ic { fill: currentColor; }
.hudm .gswin .pc .low { position: absolute; left: 6px; bottom: 6px; font-size: 9px; font-weight: 800; padding: 1px 6px; border-radius: 6px; color: #fff; background: #c8402e; }
.hudm .gswin .pc .meta { padding: 7px 9px 9px; }
.hudm .gswin .pc .meta b { display: block; font-size: 12px; color: #fff; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .gswin .pc .meta span { font-size: 10px; color: var(--dim); }
.hudm .gswin .prices { display: flex; align-items: center; gap: 8px; margin-top: 5px; font-size: 12px; font-weight: 800; font-variant-numeric: tabular-nums; }
.hudm .gswin .prices .c { display: flex; align-items: center; gap: 5px; color: #fff; } .hudm .gswin .prices .g { display: flex; align-items: center; gap: 4px; color: #f2c94c; font-size: 11px; }
.hudm .gswin .prices s { color: var(--dim); font-size: 10px; font-weight: 600; }
.hudm .gswin .empty { grid-column: 1 / -1; padding: 40px; text-align: center; color: var(--dim); }
/* Product view */
.hudm .gswin .modal { position: absolute; inset: 31px 0 0; z-index: 10; display: flex; align-items: center; justify-content: center; background: rgba(0,0,0,.65); backdrop-filter: blur(2px); animation: hudm-fade .2s; }
@keyframes hudm-fade { from { opacity: 0; } }
.hudm .gswin .pv { position: relative; width: 640px; display: flex; gap: 22px; padding: 22px; border-radius: var(--rad); background: var(--page); border: 1px solid var(--ac);
  box-shadow: 0 0 40px var(--glow), 0 20px 50px rgba(0,0,0,.7); animation: hudm-pop2 .25s ease-out; }
@keyframes hudm-pop2 { from { transform: scale(.94); opacity: 0; } }
.hudm .gswin .pv .close { position: absolute; right: 10px; top: 8px; font-size: 18px; color: var(--dim); cursor: pointer; } .hudm .gswin .pv .close:hover { color: #fff; }
.hudm .gswin .pv .stage { position: relative; width: 220px; height: 240px; flex: none; display: flex; align-items: center; justify-content: center; border-radius: var(--rad); overflow: hidden;
  background: radial-gradient(circle at 50% 60%, color-mix(in srgb, var(--ac) 30%, transparent), transparent 65%), color-mix(in srgb, var(--pb) 80%, #000); }
.hudm .gswin .pv .stage::before { content: ""; position: absolute; width: 360px; height: 360px; background: repeating-conic-gradient(from 0deg, color-mix(in srgb, var(--ac) 22%, transparent) 0 10deg, transparent 10deg 30deg);
  animation: hudm-spin 20s linear infinite; mask: radial-gradient(circle, #000 20%, transparent 65%); }
.hudm .gswin .pv .stage i { position: relative; width: 150px; height: 140px; background: center / contain no-repeat; image-rendering: pixelated; filter: drop-shadow(0 0 16px var(--glow)); animation: hudm-float 3s ease-in-out infinite; }
@keyframes hudm-float { 50% { transform: translateY(-8px); } }
.hudm .gswin .pv .stage .ped { position: absolute; bottom: 26px; width: 120px; height: 18px; border-radius: 50%; background: radial-gradient(ellipse, color-mix(in srgb, var(--ac) 50%, transparent), transparent 70%); }
.hudm .gswin .pv .info { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 9px; }
.hudm .gswin .pv h2 { margin: 0; font-size: 22px; color: #fff; }
.hudm .gswin .pv .tags { display: flex; gap: 5px; flex-wrap: wrap; }
.hudm .gswin .pv .tag { font-size: 9px; font-weight: 800; letter-spacing: .08em; text-transform: uppercase; padding: 2px 7px; border-radius: 7px; color: var(--acd); border: 1px solid color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .gswin .pv .tag.hot { color: #fff; background: #c8402e; border-color: #ff6a5a; }
.hudm .gswin .pv p { margin: 0; font-size: 12px; line-height: 1.5; color: color-mix(in srgb, var(--tx) 85%, var(--dim)); }
.hudm .gswin .pv .pay { display: flex; gap: 6px; }
.hudm .gswin .pv .pay div { flex: 1; display: flex; align-items: center; justify-content: center; gap: 7px; height: 38px; border-radius: var(--rad); cursor: pointer; font-size: 14px; font-weight: 800; font-variant-numeric: tabular-nums;
  border: 1px solid var(--fr); color: var(--dim); background: color-mix(in srgb, var(--pb) 60%, #000); }
.hudm .gswin .pv .pay div.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 22%, var(--pb)); box-shadow: 0 0 10px var(--glow); }
.hudm .gswin .pv .pay div.off { opacity: .35; pointer-events: none; }
.hudm .gswin .pv .row { display: flex; align-items: center; gap: 10px; font-size: 12px; color: var(--dim); }
.hudm .gswin .qty { display: flex; align-items: center; border: 1px solid var(--fr); border-radius: 14px; overflow: hidden; }
.hudm .gswin .qty div { width: 26px; height: 26px; display: flex; align-items: center; justify-content: center; cursor: pointer; font-weight: 800; color: var(--tx); }
.hudm .gswin .qty div:hover { background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .gswin .qty span { min-width: 30px; text-align: center; font-weight: 800; color: #fff; }
.hudm .gswin .pv .total { margin-left: auto; font-size: 13px; color: var(--dim); } .hudm .gswin .pv .total b { font-size: 18px; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .gswin .pv .total b.bad { color: #ff7a6a; }
.hudm .gswin .pv .acts { display: flex; gap: 6px; margin-top: auto; }
.hudm .gswin .pv .acts .btn { height: 36px; padding: 0 16px; border-radius: 18px; }
.hudm .gswin .pv .acts .btn.main { flex: 1; justify-content: center; font-size: 14px; }
.hudm .gswin .pv .gift { display: flex; gap: 6px; align-items: center; padding: 8px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 6%, transparent); border: 1px dashed color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .gswin .pv .gift select { flex: 1; }
.hudm .gswin .pv .conf { padding: 10px 12px; border-radius: var(--rad); font-size: 12px; background: color-mix(in srgb, var(--ac) 10%, transparent); border: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .gswin .pv .conf b { color: #fff; }
.hudm .gswin .toast { position: absolute; left: 50%; bottom: 18px; transform: translateX(-50%); z-index: 12; padding: 10px 18px; border-radius: 22px; font-size: 13px; font-weight: 700; color: #fff;
  background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 60%, #000), color-mix(in srgb, var(--ac) 35%, #000)); border: 1px solid var(--ac); box-shadow: 0 0 24px var(--glow); animation: hudm-toast 3s ease-out forwards; }
@keyframes hudm-toast { 0% { opacity: 0; transform: translate(-50%, 20px); } 10%, 80% { opacity: 1; transform: translate(-50%, 0); } 100% { opacity: 0; } }
.hudm .gswin .conf-p { position: absolute; z-index: 11; width: 8px; height: 12px; border-radius: 2px; pointer-events: none; animation: hudm-conf 1.4s cubic-bezier(.2,.7,.4,1) forwards; }
@keyframes hudm-conf { from { transform: translate(0, 0) rotate(0); opacity: 1; } to { transform: translate(var(--dx), var(--dy)) rotate(var(--rot)); opacity: 0; } }
.hudm .gswin .hist { position: absolute; right: 14px; top: 76px; z-index: 9; width: 300px; max-height: 400px; overflow-y: auto; padding: 10px; border-radius: var(--rad); background: var(--page); border: 1px solid var(--ac);
  box-shadow: 0 10px 30px rgba(0,0,0,.7); }
.hudm .gswin .hist .hr { display: flex; align-items: center; gap: 8px; padding: 5px 0; font-size: 11px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 30%, transparent); }
.hudm .gswin .hist .hr .eq { width: 34px; height: 34px; } .hudm .gswin .hist .hr span { flex: 1; } .hudm .gswin .hist .hr small { color: var(--dim); }
`;

export class GameShopMock extends HudWindow {
  private credits = 3250;
  private gold = 1_240_000;
  private cat = "All";
  private query = "";
  private sort = "Featured";
  private cls = "All classes";
  private wish = new Set<number>();
  private slide = 0;
  private hoverHero = false;
  private view: Product | null = null;
  private pay: "credit" | "gold" = "credit";
  private qty = 1;
  private giftOpen = false;
  private giftTo = FRIENDS[0]!;
  private confirm = false;
  private history: { p: Product; qty: number; paid: string; to: string }[] = [];
  private showHist = false;
  private heroEl!: HTMLElement;

  constructor() {
    super("gswin", 12, 20);
    injectCss("hudm-gameshop-css", CSS);
    // The carousel turns every 5 s (not while the mouse is over it or a product is open).
    window.setInterval(() => { if (this.open && !this.hoverHero && !this.view) { this.slide++; this.renderHero(); } }, 5000);
  }

  protected override shown(): void { this.render(); }

  private featured(): Product[] { return PRODUCTS.filter((p) => p.top || p.deal).slice(0, 8); }
  private price(p: Product, pay = this.pay): number { return pay === "credit" ? p.credit : p.gold; }

  private render(): void {
    const panel = h("div", "panel", this.titleBar("GAME SHOP", "Close (Y)"), this.topBar());
    this.heroEl = h("div", "hero");
    this.heroEl.addEventListener("mouseenter", () => { this.hoverHero = true; });
    this.heroEl.addEventListener("mouseleave", () => { this.hoverHero = false; });
    panel.append(this.heroEl, h("div", "gbody", this.nav(), this.grid()));
    if (this.view) panel.append(this.productView());
    if (this.showHist) panel.append(this.historyView());
    this.el.replaceChildren(panel);
    this.renderHero();
  }

  private topBar(): HTMLElement {
    const cr = h("div", "wal cr", h("span", "gem"), fmt(this.credits), h("small", "", " credits"));
    const gd = h("div", "wal gd", h("span", "coin"), fmt(this.gold));
    const top = h("div", "btn main", "+ Top up");
    top.addEventListener("click", () => { this.credits += 1000; this.render(); this.el.querySelector(".wal.cr")?.classList.add("bump"); });
    const search = h("input", "in");
    search.placeholder = "Search the shop…"; search.value = this.query;
    search.addEventListener("keydown", (e) => e.stopPropagation());
    search.addEventListener("input", () => { this.query = search.value.trim().toLowerCase(); const g = this.el.querySelector(".grid"); g?.replaceWith(this.grid()); });
    const sort = h("select");
    for (const s of ["Featured", "Price: low to high", "Price: high to low", "Name"]) sort.append(Object.assign(h("option", "", s), { value: s }));
    sort.value = this.sort;
    sort.addEventListener("change", () => { this.sort = sort.value; this.render(); });
    const cls = h("select");
    for (const s of ["All classes", "Warrior", "Wizard", "Taoist", "Assassin", "Archer"]) cls.append(Object.assign(h("option", "", s), { value: s }));
    cls.value = this.cls;
    cls.addEventListener("change", () => { this.cls = cls.value; this.render(); });
    const hist = h("div", "btn", `History${this.history.length ? ` (${this.history.length})` : ""}`);
    hist.addEventListener("click", () => { this.showHist = !this.showHist; this.render(); });
    return h("div", "gsbar", cr, gd, top, h("div", "sp"), search, sort, cls, hist);
  }

  private renderHero(): void {
    const items = this.featured();
    if (!items.length || !this.heroEl) return;
    const p = items[((this.slide % items.length) + items.length) % items.length]!;
    const big = h("i");
    void libPicture(Lib.Items, p.img).then((pic) => { if (pic) big.style.backgroundImage = `url(${pic.url})`; });
    const price = h("div", "price", h("span", "gem"), fmt(p.credit));
    if (p.deal) price.prepend(h("s", "", fmt(Math.round(p.credit * 1.25))));
    const buy = h("div", "btn main", p.deal ? "Grab the deal" : "Buy now");
    buy.addEventListener("click", () => this.openProduct(p));
    const dots = h("div", "dots");
    items.forEach((_, i) => { const d = h("i", i === ((this.slide % items.length) + items.length) % items.length ? "on" : ""); d.addEventListener("click", () => { this.slide = i; this.renderHero(); }); dots.append(d); });
    const l = h("div", "arw l", "‹"), r = h("div", "arw r", "›");
    l.addEventListener("click", () => { this.slide--; this.renderHero(); });
    r.addEventListener("click", () => { this.slide++; this.renderHero(); });
    this.heroEl.replaceChildren(h("div", "slide", h("div", "big", big), h("div", "tx", h("div", "kick", p.deal ? "Limited deal · save 25 %" : "Top seller"), h("h2", "", pretty(p.name)),
      h("p", "", `${DESC[p.cat] ?? p.cat}${p.count > 1 ? ` · ${p.count} in a pack` : ""}`)), h("div", "cta", price, buy)), dots, l, r);
  }

  private nav(): HTMLElement {
    const nav = h("div", "nav");
    const entry = (key: string, label: string, n: number) => {
      const d = h("div", this.cat === key ? "on" : "", svg(CAT_ICON[key] ?? CAT_ICON.All!), label, h("i", "", String(n)));
      d.addEventListener("click", () => { this.cat = key; this.render(); });
      nav.append(d);
    };
    entry("All", "All items", PRODUCTS.length);
    entry("Top", "Top sellers", PRODUCTS.filter((p) => p.top).length);
    entry("Deals", "Deals", PRODUCTS.filter((p) => p.deal).length);
    entry("Wishlist", "Wishlist", this.wish.size);
    nav.append(h("hr"));
    for (const c of CATS) entry(c, c, PRODUCTS.filter((p) => p.cat === c).length);
    return nav;
  }

  private list(): Product[] {
    let items = PRODUCTS.filter((p) => (this.cat === "All" || (this.cat === "Top" ? p.top : this.cat === "Deals" ? p.deal : this.cat === "Wishlist" ? this.wish.has(p.id) : p.cat === this.cat))
      && (this.cls === "All classes" || p.cls === "All" || p.cls.includes(this.cls)) && (!this.query || p.name.toLowerCase().includes(this.query) || p.cat.toLowerCase().includes(this.query)));
    if (this.sort.startsWith("Price")) items = [...items].sort((a, b) => (a.credit - b.credit) * (this.sort.endsWith("high") ? 1 : -1));
    else if (this.sort === "Name") items = [...items].sort((a, b) => a.name.localeCompare(b.name));
    else items = [...items].sort((a, b) => Number(b.top) - Number(a.top) || Number(b.deal) - Number(a.deal));
    return items;
  }

  private grid(): HTMLElement {
    const grid = h("div", "grid");
    for (const p of this.list()) {
      const art = h("i");
      void libPicture(Lib.Items, p.img).then((pic) => { if (pic) art.style.backgroundImage = `url(${pic.url})`; });
      const heart = h("div", "heart" + (this.wish.has(p.id) ? " on" : ""), svg(CAT_ICON.Wishlist!));
      heart.title = "Wishlist";
      heart.addEventListener("click", (e) => { e.stopPropagation(); if (this.wish.has(p.id)) this.wish.delete(p.id); else this.wish.add(p.id); this.render(); });
      const artBox = h("div", "art", art, heart);
      if (p.count > 1) artBox.append(h("span", "cnt", `x${p.count}`));
      if (p.stock > 0) artBox.append(h("span", "low", `Only ${p.stock} left`));
      const prices = h("div", "prices");
      if (p.byCredit) prices.append(h("span", "c", h("span", "gem"), p.deal ? h("s", "", fmt(Math.round(p.credit * 1.25))) : "", fmt(p.credit)));
      if (p.byGold) prices.append(h("span", "g", h("span", "coin"), p.gold >= 1000 ? `${Math.round(p.gold / 1000)}k` : fmt(p.gold)));
      const card = h("div", "pc");
      if (p.deal) card.append(h("div", "rib", "DEAL"));
      if (p.top) { const s = h("div", "star", svg(CAT_ICON.Top!)); s.title = "Top seller"; card.append(s); }
      card.append(artBox, h("div", "meta", h("b", "", pretty(p.name)), h("span", "", `${p.cat}${p.cls !== "All" ? ` · ${p.cls}` : ""}`), prices));
      card.addEventListener("click", () => this.openProduct(p));
      grid.append(card);
    }
    if (!grid.childElementCount) grid.append(h("div", "empty", this.cat === "Wishlist" ? "Tap the heart on anything you like to keep it here." : "Nothing matches."));
    return grid;
  }

  private openProduct(p: Product): void {
    this.view = p; this.qty = 1; this.giftOpen = false; this.confirm = false; this.showHist = false;
    this.pay = p.byCredit ? "credit" : "gold";
    this.render();
  }

  private productView(): HTMLElement {
    const p = this.view!;
    const big = h("i");
    void libPicture(Lib.Items, p.img).then((pic) => { if (pic) big.style.backgroundImage = `url(${pic.url})`; });
    const close = h("div", "close", "×");
    close.addEventListener("click", () => { this.view = null; this.render(); });
    const tags = h("div", "tags", h("span", "tag", p.cat), h("span", "tag", p.type));
    if (p.cls !== "All") tags.append(h("span", "tag", p.cls));
    if (p.deal) tags.append(h("span", "tag hot", "Deal -25 %"));
    if (p.top) tags.append(h("span", "tag", "★ Top seller"));
    if (p.stock > 0) tags.append(h("span", "tag hot", `Only ${p.stock} left`));
    const pay = h("div", "pay");
    for (const [k, label, ok] of [["credit", fmt(p.credit), p.byCredit], ["gold", fmt(p.gold), p.byGold]] as ["credit" | "gold", string, boolean][]) {
      const d = h("div", (this.pay === k ? "on" : "") + (ok ? "" : " off"), h("span", k === "credit" ? "gem" : "coin"), label);
      d.addEventListener("click", () => { this.pay = k; this.confirm = false; this.render(); });
      pay.append(d);
    }
    const maxQty = p.stock > 0 ? p.stock : 10;
    const minus = h("div", "", "−"), plus = h("div", "", "+");
    minus.addEventListener("click", () => { this.qty = Math.max(1, this.qty - 1); this.confirm = false; this.render(); });
    plus.addEventListener("click", () => { this.qty = Math.min(maxQty, this.qty + 1); this.confirm = false; this.render(); });
    const total = this.price(p) * this.qty;
    const have = this.pay === "credit" ? this.credits : this.gold;
    const row = h("div", "row", "Quantity", h("div", "qty", minus, h("span", "", String(this.qty)), plus), h("div", "total", "Total ", h("b", total > have ? "bad" : "", `${fmt(total)} ${this.pay === "credit" ? "credits" : "gold"}`)));
    const parts: (HTMLElement | string)[] = [h("h2", "", pretty(p.name)), tags, h("p", "", `${DESC[p.cat] ?? ""}${p.count > 1 ? ` This pack holds ${p.count}.` : ""}`), pay, row];
    if (this.giftOpen) {
      const sel = h("select");
      for (const f of FRIENDS) sel.append(Object.assign(h("option", "", f), { value: f }));
      sel.value = this.giftTo;
      sel.addEventListener("change", () => { this.giftTo = sel.value; });
      parts.push(h("div", "gift", "🎁 Gift to", sel, h("span", "", "· sent by mail")));
    }
    if (this.confirm) parts.push(h("div", "conf", `${this.giftOpen ? `Gift to ${this.giftTo}: ` : ""}`, h("b", "", `${this.qty} × ${pretty(p.name)}`), ` for `, h("b", "", `${fmt(total)} ${this.pay === "credit" ? "credits" : "gold"}`),
      `. You'll have ${fmt(have - total)} left.`));
    const acts = h("div", "acts");
    const buy = h("div", "btn main" + (total > have ? " dis" : ""), total > have ? "Not enough" : this.confirm ? "Confirm purchase" : this.giftOpen ? "Send gift" : "Buy");
    buy.addEventListener("click", () => {
      if (total > have) return; // the button is greyed out, but never trust the button
      if (!this.confirm) { this.confirm = true; this.render(); return; }
      if (this.pay === "credit") this.credits -= total; else this.gold -= total;
      if (p.stock > 0) p.stock = Math.max(0, p.stock - this.qty);
      this.history.unshift({ p, qty: this.qty, paid: `${fmt(total)} ${this.pay === "credit" ? "credits" : "gold"}`, to: this.giftOpen ? this.giftTo : "" });
      const msg = this.giftOpen ? `🎁 Sent ${this.qty} × ${pretty(p.name)} to ${this.giftTo}!` : `✨ ${this.qty} × ${pretty(p.name)} added to your bag!`;
      this.view = null;
      this.render();
      this.celebrate(msg);
    });
    const gift = h("div", "btn", this.giftOpen ? "Buy for me" : "🎁 Gift");
    gift.addEventListener("click", () => { this.giftOpen = !this.giftOpen; this.confirm = false; this.render(); });
    const wish = h("div", "btn", this.wish.has(p.id) ? "♥ Wishlisted" : "♡ Wishlist");
    wish.addEventListener("click", () => { if (this.wish.has(p.id)) this.wish.delete(p.id); else this.wish.add(p.id); this.render(); });
    acts.append(buy, gift, wish);
    if (p.cat === "Transform" || p.cat === "Mount") {
      const pv = h("div", "btn", "👁 Preview");
      pv.addEventListener("click", () => this.celebrate(`Previewing ${pretty(p.name)} on GMRyu… (mock)`, false));
      acts.append(pv);
    }
    parts.push(acts);
    const modal = h("div", "modal", h("div", "pv", close, h("div", "stage", h("div", "ped"), big), h("div", "info", ...parts)));
    modal.addEventListener("mousedown", (e) => { if (e.target === modal) { this.view = null; this.render(); } });
    return modal;
  }

  private historyView(): HTMLElement {
    const box = h("div", "hist", h("div", "", h("b", "", "Purchase history")));
    if (!this.history.length) box.append(h("div", "hr", h("small", "", "Nothing bought yet.")));
    for (const x of this.history) box.append(h("div", "hr", itemCell(this.el, x.p.name, x.p.img, x.qty), h("span", "", `${x.qty} × ${pretty(x.p.name)}`, h("br"), h("small", "", x.to ? `Gift to ${x.to}` : "For you")), h("small", "", x.paid)));
    return box;
  }

  /** A toast, with a confetti burst from the middle of the window for purchases. */
  private celebrate(text: string, confetti = true): void {
    const panel = this.el.querySelector(".panel")!;
    panel.append(h("div", "toast", text));
    if (!confetti) return;
    const colours = ["var(--ac)", "#fff", "#f2c94c", "#ff6a8a", "#4fe07a"];
    const w = (panel as HTMLElement).clientWidth, hgt = (panel as HTMLElement).clientHeight;
    for (let i = 0; i < 60; i++) {
      const c = h("div", "conf-p");
      const ang = Math.random() * Math.PI * 2, dist = 120 + Math.random() * 260;
      Object.assign(c.style, { left: w / 2 + "px", top: hgt / 2 + "px", background: colours[i % colours.length]!, animationDelay: Math.random() * 0.15 + "s" });
      c.style.setProperty("--dx", Math.cos(ang) * dist + "px");
      c.style.setProperty("--dy", Math.sin(ang) * dist + 120 + "px");
      c.style.setProperty("--rot", (Math.random() * 720 - 360) + "deg");
      panel.append(c);
    }
    window.setTimeout(() => { for (const c of panel.querySelectorAll(".conf-p")) c.remove(); }, 1700);
  }
}

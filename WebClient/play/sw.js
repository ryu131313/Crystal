// The web client's service worker (registered by play/main.ts for the touch layout): an offline start for an installed
// (Add to Home Screen) client and a cache of client files that phones keep longer than their HTTP cache.
// Plain JS on purpose: it must live in play/ (its scope), and publish-webclient.ps1 copies play/ minus .ts files.
//   - ?v= URLs (src/assets/manifest.ts): cache first. Only a response the relay marked "immutable" is stored (a version
//     mismatch is served no-store and never cached, AssetHost.cs); a new version replaces the old copy of that path.
//   - The page, its scripts and styles (same origin, not ?v=): network first, the cached copy only when offline.
//   - Everything else (the /ws socket, the relay's API, range requests): untouched.

const ASSETS = "crystal-assets-v1";
const SHELL = "crystal-shell-v1";
/** Assets kept before the oldest are dropped. */
const MAX_ASSETS = 4000;
let puts = 0;

self.addEventListener("install", () => { self.skipWaiting(); });

self.addEventListener("activate", (event) => {
  event.waitUntil((async () => {
    for (const k of await caches.keys()) if (k !== ASSETS && k !== SHELL) await caches.delete(k);
    await self.clients.claim();
  })());
});

self.addEventListener("fetch", (event) => {
  const req = event.request;
  if (req.method !== "GET" || req.headers.has("range")) return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin || url.pathname.endsWith("/ws")) return;
  if (url.searchParams.has("v")) { event.respondWith(asset(req)); return; }
  const shell = req.mode === "navigate" || /\.(html|js|css|json|png)$/.test(url.pathname) && !url.pathname.includes("/data/");
  if (shell) event.respondWith(networkFirst(req));
});

async function asset(req) {
  const cache = await caches.open(ASSETS);
  const hit = await cache.match(req);
  if (hit) return hit;
  const res = await fetch(req);
  if (res.ok && res.status === 200 && (res.headers.get("cache-control") || "").includes("immutable")) {
    const copy = res.clone();
    (async () => {
      await cache.delete(req, { ignoreSearch: true }); // the old version of this file
      await cache.put(req, copy);
      if (++puts % 200 === 0) await trim(cache);
    })().catch(() => { /* quota: the HTTP cache still has it */ });
  }
  return res;
}

async function trim(cache) {
  const keys = await cache.keys(); // insertion order: oldest first
  for (let i = 0; i < keys.length - MAX_ASSETS; i++) await cache.delete(keys[i]);
}

async function networkFirst(req) {
  const cache = await caches.open(SHELL);
  try {
    const res = await fetch(req);
    if (res.ok && res.status === 200 && res.type === "basic") cache.put(req, res.clone()).catch(() => {});
    return res;
  } catch (e) {
    const hit = await cache.match(req, { ignoreSearch: req.mode === "navigate" });
    if (hit) return hit;
    throw e;
  }
}

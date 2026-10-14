// Asset manifest + versioned URLs (after WoOOLcraft's libManifest.ts / versionedSource.ts, load-time items 1-3).
// GET <root>/api/manifest once per page (revalidated, never trusted from cache: Cloudflare's Browser Cache TTL would hold
// it for hours) -> every client file's version. URLs then carry ?v=<version> and the relay serves them "immutable", so a
// return visit asks for nothing again; a patched file gets a new version (the relay answers 412 to an old one, and
// the page shows a reload notice). A file missing from the manifest is absent: no request at all (no 404 probes).
// Without a manifest (an old relay, a failed fetch) every helper falls back to the plain unversioned URL.

import { webUrl } from "./root.ts";

export type AssetFolder = "data" | "map" | "sound";
type Manifest = Record<AssetFolder, Record<string, string>>;

let manifest: Manifest | null = null;
let loading: Promise<Manifest | null> | null = null;

/** Starts (once) and returns the manifest fetch; null when unavailable. */
export function loadManifest(): Promise<Manifest | null> {
  loading ??= (async () => {
    try {
      const res = await fetch(webUrl("/api/manifest"), { cache: "no-cache" });
      if (!res.ok) return null;
      manifest = (await res.json()) as Manifest;
      return manifest;
    } catch {
      return null;
    }
  })();
  return loading;
}

/** The version of `folder/relPath` ('/' separated, with extension): a string, null when the manifest says the file
 *  does not exist, undefined when there is no manifest (use the unversioned URL). Await loadManifest() first. */
export function assetVersion(folder: AssetFolder, relPath: string): string | null | undefined {
  if (!manifest) return undefined;
  return manifest[folder][relPath.toLowerCase()] ?? null;
}

/** The URL of a whole file: versioned when known; null when the manifest says it is absent. */
export async function assetUrl(folder: AssetFolder, relPath: string): Promise<string | null> {
  await loadManifest();
  const v = assetVersion(folder, relPath);
  if (v === null) return null;
  const url = webUrl(`/${folder}/${relPath.split("/").map(encodeURIComponent).join("/")}`);
  return v === undefined ? url : `${url}?v=${v}`;
}

/** Every file of `folder` the manifest lists (lower-case '/' paths), or [] without a manifest. Await loadManifest() first. */
export function assetNames(folder: AssetFolder): string[] {
  return manifest ? Object.keys(manifest[folder]) : [];
}

let staleShown = false;

/** A versioned URL answered 412: the client folder was patched while this page was open. One notice, then the page keeps
 *  going with whatever it already has. */
export function assetStale(url: string): void {
  console.warn(`[assets] ${url}: version changed on the server`);
  if (staleShown || typeof document === "undefined") return;
  staleShown = true;
  const note = document.createElement("div");
  note.textContent = "The game files were updated. Reload the page to continue.";
  note.style.cssText = "position:fixed;left:50%;top:8px;transform:translateX(-50%);z-index:100000;padding:6px 14px;" +
    "background:#201810;color:#f0d890;border:1px solid #806030;font:13px sans-serif;cursor:pointer";
  note.onclick = () => location.reload();
  document.body.appendChild(note);
}

/** fetch() of a versioned URL: a 412 raises the reload notice and throws. */
export async function fetchAsset(url: string, init?: RequestInit): Promise<Response> {
  const res = await fetch(url, init);
  if (res.status === 412) {
    assetStale(url);
    throw new Error(`${url}: asset version changed`);
  }
  return res;
}

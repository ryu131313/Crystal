// The web client's URL root. The page always lives at <root>/play/: "" on a dev relay (http://127.0.0.1:5690/play/),
// "/portal/mir2webclient" behind the public relay's --mount (https://worldofwooolcraft.uk/portal/mir2webclient/play/).
// Every relay path (/ws, /data/, /map/, /sound/, /play/) is built with webUrl so the client works under any mount.

export const WEB_ROOT: string = typeof location === "undefined" ? "" : location.pathname.replace(/\/play\/[^/]*$/, "");

/** A relay path ("/data/...") under the page's root. */
export function webUrl(path: string): string {
  return WEB_ROOT + path;
}

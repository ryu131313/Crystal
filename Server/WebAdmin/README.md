# MirCraft Admin

MirCraft Admin is a browser admin panel built into the Crystal Legend of Mir 2 server (`Server.exe`). It is served by a
`System.Net.HttpListener` that runs inside the server process. You do not install or run anything else, and it needs no
ASP.NET Core, IIS or Node.

- The panel starts when `Server.exe` starts (`Server.MirForms\Program.cs`, before the main form is created) and stops when the
  process exits. It does **not** depend on the game world: it keeps running while the world is stopped, starting or rebooting,
  so you can use it to start the world again.
- Its structure, UX rules and several frontend modules (ag-Grid editors, schema, lookups and form drawer) come from the
  WoOOLcraft admin. The look is the "MirCraft" theme (`wwwroot/shared/mir-theme.css` and `admin-theme.css`).
- The backend is in `Server\WebAdmin\` (Server.Library assembly, namespace `Server.WebAdmin`). The frontend is static vanilla JS
  in `Server.MirForms\wwwroot\` and is copied to the output folder at build time. There is no build step and no npm. The only
  CDN libraries are ag-Grid Community 32.3.3, Chart.js 4.4.7 and Google Fonts.

---

## 1. Quick start (operators)

### 1.1 Configuration: `[WebAdmin]` in `Configs\Setup.ini`

`Settings.Save()` writes this section automatically, so it appears on the first run (with the defaults below) and whenever the
server saves its settings. Every key is read once, at process start, so **changing any of them needs a full `Server.exe` restart**.
A world reboot is not enough.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `True` | Start the panel with the server. If you set it to `False`, you can only turn it back on by editing the ini. |
| `Bind` | `127.0.0.1` | Address to listen on. `127.0.0.1` means this machine only. `0.0.0.0`, `*`, `+` or `::` mean all interfaces (mapped to the `+` wildcard). Any other value is a specific IP or host name. Empty means `127.0.0.1`. |
| `Port` | `5680` | TCP port of the panel. `0` means 5680. |
| `WebRoot` | `.\wwwroot` | Folder with the static files. A relative path is resolved against the exe folder. It must contain `admin\index.html`. |
| `SessionHours` | `8` | Sliding session lifetime in hours (clamped to 1–720). `<= 0` means 8. |
| `MaxBodyKB` | `2048` | Largest request body accepted (clamped to 16–65536 KB). Bigger bodies get `413`. This limits script file saves and bulk grid edits. |

The login rate limits and the request concurrency limit are **not** in the ini. They are fixed defaults in `WebAdminOptions`:
32 concurrent requests, 10 sign-in attempts per IP per minute, 120 sign-in attempts per minute globally, and 10 failed sign-ins
per (account, IP) per 15 minutes.

### 1.2 Open the panel

Start `Server.exe`. The server log shows:

```
[WebAdmin] MirCraft web admin listening on http://127.0.0.1:5680/admin/ (N route modules, M routes)
```

Then open **http://127.0.0.1:5680/admin/**. Requests to `/` or `/admin` redirect to `/admin/`. Any page under `/admin/` other than
`login.html` redirects to `/admin/login.html?next=...` when you are not signed in.

If the panel cannot listen (port already in use, or a non-loopback bind without a URL reservation), the server logs the reason
and a `netsh` hint (see §2). The game server still starts normally.

### 1.3 Sign in

Sign in with a **game account that has `AdminAccount = true`**. The account must not be banned and must not be flagged
"require password change". There are no separate panel users.

To make an account an admin, use either of these:
- **WinForms:** open the account in the server's *Account* form (`AccountInfoForm`), tick the **Administrator** checkbox, and
  let the server save.
- **Panel:** if you already have an admin, use *Accounts → (account) → Admin*. The panel refuses to remove the last admin, and
  refuses to demote yourself unless you confirm it.

How the password is checked depends on the world state:
- **World running:** the account is read on the game thread and the password is checked with Crystal's own PBKDF2 hash. A web
  sign-in never changes the game's `WrongPasswordCount` lockout.
- **World stopped:** the panel uses the **offline admin directory** `WebAdmin\admins.dat` (next to the exe). This is a snapshot of
  the admin accounts (index, id, password hash and salt, ban) that is written atomically every time the world loads or saves
  accounts. The panel ignores it when `Server.MirADB` is missing or was written noticeably later than the snapshot (for example
  restored or edited while the server was down). Deleting `admins.dat` turns off all sign-ins while the world is stopped.
- **Fresh install / empty directory:** no one has loaded the accounts yet, so nothing can verify a password. Sign-in returns
  `503` with *"The world is starting; web sign-in will be available as soon as the accounts have loaded"* (or *"... unavailable
  until the world has loaded the accounts. Start the world from the server window"*). This does not count as a failed attempt.
  On the very first run, start the world from the WinForms window once.

While the world runs, every request re-checks that the session's account is still an admin and not banned (a live check about
every 5 s). A demoted or banned admin loses all of their sessions.

**Sessions, CSRF and rate limits.** Sessions are kept in memory only, so restarting `Server.exe` signs everyone out. The cookie
`MirCraftAdmin` is `HttpOnly; SameSite=Strict`. Expiry slides by `SessionHours` on each request, with a hard cap of 3 ×
`SessionHours`, and an account can have at most 10 sessions. Every `POST/PUT/PATCH/DELETE` to an admin route must send the
header `X-MirCraft-CSRF`, and its value must match both the `MirCraftAdminCsrf` cookie and the session (the frontend's `portalApi`
does this for you). The login request must be `application/json` and same-origin (it checks `Sec-Fetch-Site` and `Origin`
against `Host`). Too many attempts get `429` with `Retry-After`. Any session can be revoked in *Security*.

---

## 2. Remote access and security

- **Default: loopback only.** With `Bind=127.0.0.1`, the panel is reachable only from the server machine. For remote work, an SSH
  or RDP tunnel to the box is the simplest safe option.
- **Binding to another address** (a LAN IP or `0.0.0.0`) needs an http.sys URL reservation. Without one, `listener.Start()`
  fails with "access denied", and the log shows the exact command. Run it once from an elevated prompt:

  ```
  netsh http add urlacl url=http://+:5680/ user=Everyone          (Bind=0.0.0.0 / * / + / ::)
  netsh http add urlacl url=http://192.168.1.10:5680/ user=Everyone   (Bind=192.168.1.10)
  ```

  This is the form the host's hint prints (`http://<host>:<port>/`, where `<host>` is `+` for the wildcard binds). You can use a
  specific Windows user instead of `Everyone`. Also open the port in the Windows Firewall.
- **Use HTTPS. Never expose the panel on plain HTTP.** The host speaks plain HTTP only, and its cookies are **not** marked
  `Secure`, so on an untrusted network both the session cookie and the password are sent in clear text. To publish the panel,
  keep `Bind=127.0.0.1` and put a TLS reverse proxy in front of it (IIS ARR, nginx, Caddy, ...). Notes for a proxy:
  - It must pass the original `Host` header through, because the login cross-site check compares `Origin` with `Host`.
  - The host does not read `X-Forwarded-For`. The audit log, the login log and the per-IP rate limit will therefore all see the
    proxy's IP.
- Every response carries `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: same-origin` and
  `Cross-Origin-Opener-Policy: same-origin`. API responses are also `Cache-Control: no-store`.
- **Audit log:** `Logs\WebAdmin\audit-yyyy-MM-dd.jsonl`, relative to the exe folder. Each line is
  `{ts, admin, ip, action, target, reason, details}`. The panel's own sign-ins, sign-outs and refusals are recorded here as
  `auth.*`. Passwords and hashes are never logged.
- **Game login log:** `Logs\WebAdmin\logins-yyyy-MM-dd.jsonl` records every game-client login attempt with its result (`ok`,
  `bad-password`, `banned`, `no-account`, `refused:<reason>`). You can search it in *Security*.
- Panel diagnostics go to the normal server log with a `[WebAdmin]` prefix.

---

## 3. Feature tour

Polling only runs while a section is visible and the browser tab is in the foreground. Destructive actions open one
confirmation dialog, with a reason field for moderation actions. Every successful change is audited.

**Monitoring**
- **Dashboard:** tiles (world state, players, connections, monsters, cycle delay, uptime, memory, CPU, pending reboot), Chart.js
  history (1 h / 6 h / 24 h, sampled every 5 s), host metrics, a live activity feed (logins, logouts, failed logins, world
  events, admin actions), and quick actions.
- **Server Control:** start, stop, reboot and save the world; shut down `Server.exe` (asks twice); reload NPCs, drops, line
  messages and recipes; a restart countdown that announces to players and then reboots; versions and paths; the pending-reboot
  list; a log viewer (live server/debug/chat from the in-process log tap, plus log4net files under `Logs\` with tail and
  download). Start, stop and reboot share a transition guard, so overlapping requests get `409`.
- **Players Online:** a live table of online characters with row actions (inspect, message, kick, kill, safe zone, teleport,
  chat ban, ban account).
- **Security:** banned accounts, chat bans, admins, panel sessions (revoke), IP blocks (add, remove, clear), IPs shared by
  several accounts, recent failed logins, and a searchable game login log.

**Operations**
- **GM Console:** pick a target, then run a GM verb such as kick, kill, revive, teleport, give gold/credit/item, set level,
  give skill, set PK, chat ban, spawn monsters, clear monsters or start a conquest. The verbs use the same code paths as the
  in-game `@` commands. Most verbs need the world running (`503` otherwise). Some, such as setlevel and chat ban, also work on
  offline characters.
- **Broadcast:** announcement, shout, system, hint or line-message broadcasts, a recent list with resend, and an editor for the
  login notice (`Settings.NoticePath`).
- **Chat Log:** a live chat feed fed by `WebAdminHooks.Chat`, with channel, name and text filters, per-line moderation actions,
  CSV export, and history by date from the log4net chat files.
- **Maintenance:** installs a login gate that refuses non-admin game logins with a message. It can optionally run a countdown and
  then disconnect non-admin players. The gate is process-wide, so it survives world restarts but not a `Server.exe` restart.
- **Audit Trail:** searchable view of the audit log (filter by admin, action prefix and text), with expandable details or
  before→after diffs, and CSV export.

**Players**
- **Accounts:** search by account, character or IP; ban (presets or permanent) and unban; toggle admin; set the password or
  require a password change; clear the storage password; edit gold and credit; delete with a typed confirmation. Edits work
  while the world is stopped only if the world has loaded the accounts in this process (otherwise `409`).
- **Characters:** paged search, including a search by item. The detail drawer shows info, items, magic, quests, flags, pets and
  heroes, and mail. You can edit name, level, PK, gold and similar, soft delete and restore, and add, edit or remove items.
  Changes to online characters go through `PlayerObject` so the client updates.
- **Guilds:** members, storage, notice editor, buffs, wars, remove a member, guild message, funds. Changes need the running world.
- **Market:** browse auction listings, expire them, or remove them (optionally mailing the item back to the seller).
- **Name Lists:** create, delete and edit the files under `Envir\NameLists`, and find which lists contain a name.

**Game Data**
- **Game Data:** ag-Grid editors for items, monsters, magic, NPCs, quests, maps (and their safe zones, respawns, movements and
  mine zones), game shop, conquests (and six child tables) and the dragon. Behaviour to know:
  - Edits go to the **editor copy `Envir.Edit`**, the same copy the WinForms editors use. *Save* writes `Server.MirDB`
    (Crystal makes a backup in `Back Up\Database`).
  - The running world (`Envir.Main`) **only sees saved changes after a world reboot**. Each save adds a pending-reboot entry,
    which shows in the topbar badge. There are two exceptions. **Monsters** can be applied live ("Apply live to running world",
    `PUT ...?live=1`), which updates `MonsterInfo` and refreshes the live monsters. **Magic** (spell) changes are synced into
    Main's `MagicInfo` when you save, or on the next world start if the world was not running.
  - After any Edit save, Main's periodic `SaveDB()` is skipped until the next reboot (`Envir.SystemDataStale`). Without this,
    Main's old in-memory data would overwrite your save.
  - Saves are serialised with the world's periodic save. While Main still saves, the file IO runs on the game thread between
    ticks; otherwise it runs under `Envir.LoadLock`.
  - Conflicts are detected per row with `__rev`, and dry-run previews are available. Deletes are refused when other data still
    references the row. A rename can be propagated into the drop, recipe, quest and NPC files (with backups). *Discard unsaved*
    reloads the editor copy from disk.
- **Scripts & Files:** a text editor for the NPC, drop, quest, recipe, route and value folders, the `Envir\*.txt` files and
  SystemScripts. It has a file tree, search, validation (missing labels and goto targets, unknown item and monster names,
  `#INSERT` targets), etag conflict detection (`409`), and keeps the file's encoding and line endings. Every save, delete or
  rename first backs up the old version to `Back Up\WebAdmin\Scripts\<root>\<path>.<yyyyMMdd-HHmmss>.bak`, and you can restore
  from the editor. After a save the panel offers the matching reload. There is also a drop-file Assassin/Archer class toggle.
- **Backups & History:** lists `Back Up\Database`, `Back Up\Accounts` and the script backups. You can make a manual copy of
  `Server.MirDB` or `Server.MirADB`, download, and prune. **Restore only works while the world is stopped** (`409` otherwise).
  It backs up the current file first, then reloads the editor copy or the account list. The *History* tab shows per-field
  game-data changes from the audit log, with a Revert action (not for inserts, deletes or position-keyed child tables).

**Config**
- **Server Config:** every `Setup.ini` key that `Settings.Load()` and `Save()` handle, in sections with typed inputs. Saves go
  through `Settings.Save()`. Each key carries a badge for when it takes effect: *live*, *world reboot* (read at world start), or
  *process restart* (read once per process, including all `[WebAdmin]` keys). `GMPassword` is masked.
- **Balance:** class and hero base stats (with formula preview), item stat weights and PvP flags, and random item stats. Saving
  writes the matching `Settings.Save*()` file and asks the running world to recalculate base stats.
- **Systems:** fishing, mail, goods, refine, marriage, mentor, gem, heroes, guild settings, mining, and the spawn tick. The spawn
  tick lives in `Server.MirDB` (`Envir.Edit`), so it follows the Game Data save and reboot rules.

---

## 4. How it fits into Crystal (developers)

### 4.1 Game-thread action queue

HTTP handlers run on thread-pool threads. Live game state (players, accounts, characters, guilds, auctions, maps) belongs to
the game thread and must only be touched there.

- `Envir.QueueAdminAction(Action run, Action<Exception> dropped)` enqueues work. `WorkLoop` drains the queue in
  `DrainAdminActions()` **right after `Process()`** on each tick, running at most 64 actions or about 10 ms. The queue opens just
  before `WorldStarted` and closes when the loop exits. Actions still waiting at that point get `dropped(WorldNotRunningException)`.
- `Envir.RequestSaveNow()` makes the next tick run the periodic save block.
- `WebAdminGame.RunAsync<T>(fn, timeoutMs = 5000)` queues `fn` and awaits its result. It throws `WorldNotRunningException` (503)
  if the loop is not running and `GameThreadTimeoutException` (504) on timeout. A timed-out action that has not started yet is
  cancelled. Return DTOs or snapshots from `fn`, never live objects.
- `WebAdminGame.RunOrInlineAsync<T>(fn)` runs `fn` on the game thread while the world runs. When the world is **fully stopped**
  (or crashed), it runs `fn` inline under `lock (Envir.AccountLock)`. While the world is starting or stopping it throws 503,
  because account data is changing.
- `WebAdminGame.Post(fn)` is fire-and-forget. `WorldState` returns `running`, `stopped`, `starting` or `stopping`. `WorldCrashed`
  is also available. Tests swap the world for a fake through the `IWebAdminWorld` abstraction.
- Never call `Envir.Stop()` or `Reboot()` on the game thread, because `Stop` waits for that thread and would deadlock. Server
  Control calls them from pool threads, the same way the WinForms menu does.

### 4.2 Hooks (`WebAdminHooks`)

These are static events. Every call is wrapped so that a subscriber that throws is logged and cannot break the game loop.
Subscribers run on the calling thread, which is usually the game thread, so keep them O(1) and non-blocking.

| Hook | Invoked from |
|---|---|
| `WorldStarted` | `Envir.WorkLoop`, after `StartNetwork()` and `OpenAdminQueue()` |
| `WorldStopped` | `Envir.WorkLoop` exit (and the early-exit path when `CanStartEnvir()` refuses to start) |
| `AccountsLoaded` | `Envir.StartNetwork`, right after `LoadAccounts()` |
| `AccountsSaved` | after Main serialises accounts (`BeginSaveAccounts` / `SaveAccounts`) |
| `LoginAttempt(accountId, ip, result)` | `Envir.Login`: `no-account`, `banned`, `bad-password`, `refused:<reason>`, `ok` |
| `LoginGate` (single `Func<AccountInfo,string>`) | `Envir.Login`, before a successful login. Owned by Maintenance; a non-empty result refuses the login. |
| `PlayerEntered` / `PlayerLeft` | `PlayerObject.StartGame` (after joining `Envir.Players`, before the Login scripts) / `PlayerObject.StopGame` |
| `Chat(channel, sender, message, target)` | `PlayerObject.Chat`: normal, shout, shout-map, shout-server, whisper, group, guild, mentor, relationship, announcement |

### 4.3 Stale system data guard

`Envir.SaveDB()` sets a static `_systemDataStale` flag when `this == Edit`. While `Envir.SystemDataStale` is true, Main's periodic
save in `WorkLoop` skips `SaveDB()`, so Main's stale in-memory system data never overwrites newer data that the panel or WinForms
saved through Edit. The flag clears when Main reloads its database (Start/Reboot). Calling `Main.SaveDB()` explicitly, as the
MonsterTuner and Dragon WinForms forms do, still works.

### 4.4 Log tap

`MessageQueue.Enqueue`, `Enqueue(Exception)`, `EnqueueDebugging` and `EnqueueChat` also send each line to
`WebAdminLogTap.AppendServer`, `AppendDebug` and `AppendChat`. These are ring buffers with sequence ids, read by
`/api/admin/logs/{kind}`. The WinForms log views and log4net are unaffected.

### 4.5 Changes to existing Crystal files

| File | Change |
|---|---|
| `Server\MirEnvir\Envir.cs` | Admin action queue region (`QueueAdminAction`, `RequestSaveNow`, `AdminQueueOpen`, `WorkLoopAlive`, open/drain/close), `SystemDataStale`, the `WorkLoop` drain call and save guard, hook calls in WorkLoop, StartNetwork, account saving and Login |
| `Server\MirObjects\PlayerObject.cs` | `PlayerEntered` / `PlayerLeft` / `Chat` hook calls |
| `Server\MessageQueue.cs` | Log tap calls |
| `Server\Settings.cs` | `WebAdmin*` fields, plus Load and Save of the `[WebAdmin]` section |
| `Server\Server.Library.csproj` | `InternalsVisibleTo Server.WebAdmin.Tests` |
| `Server.MirForms\Program.cs` | `WebAdminHost.Start()` after `Settings.Load()`, the `ShutdownRequested` handler (closes the main form on the UI thread), `WebAdminHost.Stop()` on exit |
| `Server.MirForms\Server.csproj` | Copies `wwwroot\**` to the output (`PreserveNewest`), never compiled or embedded |
| `Legend of Mir.sln` | Adds the `Server.WebAdmin.Tests` project |

---

## 5. Developing

### 5.1 Layout

```
Server\WebAdmin\                 host, router, request/JSON, sessions, auth, admins directory, rate limiter, game bridge,
                                 hooks, audit, login log, log tap, pending-reboot tracker, maintenance, chat log, settings
                                 catalog, script files/validation, character DTOs/logic, status sampler, host metrics
Server\WebAdmin\Routes\          one IWebAdminRoutes class per feature area (*Routes.cs) + helpers
Server\WebAdmin\GameData\        grid engine over Envir.Edit: registry, schema, codec, rev, lookups, rename, save, magic sync
Server.MirForms\wwwroot\admin\   index.html, login.html, app.js (bootstrap), admin-shell.js (AdminSections, hash routing),
                                 admin-ops-common.js / admin-toast.js (portalApi, OpsUI, toasts, dialogs), grid/schema/lookup/
                                 form-drawer modules, one admin-<stem>.js/.css pair per section, style.css
Server.MirForms\wwwroot\shared\  mir-theme.css, admin-theme.css, admin-bg.jpg, portal-*.js
Server.WebAdmin.Tests\           xUnit (net8.0), references Server.Library + Shared
```

### 5.2 Adding a route

Implement `IWebAdminRoutes` in `Server.Library`. At host start, `WebAdminRouter.RegisterAll` finds every non-abstract implementer
by reflection and creates it with its parameterless constructor:

```csharp
public sealed class WidgetRoutes : IWebAdminRoutes
{
    public void Register(WebAdminRouter router)
    {
        router.Get("/api/admin/widgets", List);                      // RouteAuth.Admin by default
        router.Post("/api/admin/widgets/{index:int}/frob", Frob);    // {name} = any segment, {name:int} = int
    }
}
```

- Default auth: `/api/auth/*` is anonymous and everything else is `RouteAuth.Admin`, with CSRF enforced on mutating methods.
- When several patterns match, the most specific wins (literal > int > any, left to right).
- Registering the same method and shape twice **throws `InvalidOperationException`**. The failure is logged and that module's
  routes are missing. `RouteRegistrationTests` catches this, so keep to your own prefix.
- Subscribe to hooks inside `Register` with a static once-guard, because registration can run more than once in tests.

### 5.3 Adding a section

1. `index.html`: add a nav button `<button class="nav-link" data-section="widgets">`, a
   `<section id="section-widgets" class="admin-section hidden"></section>`, a
   `<link rel="stylesheet" href="admin-widgets.css">`, and a `<script src="admin-widgets.js">` before `app.js`, which must stay last.
2. `admin-shell.js`: add `widgets: { label, group }` to `SECTIONS`. This controls breadcrumbs, labels and the default ordering.
3. In `admin-widgets.js` (an IIFE), call `AdminSections.register('widgets', { load(el, params), show(el, params), hide(el) })`.
   `load` runs once and may return a Promise. Use `AdminSections.poll(id, fn, ms)` for polling that stops when the section is
   hidden. Register deep links with `portalRegisterDeepLink` (`#section=widgets&...`).
4. Send every mutation through `portalApi` / `OpsUI.post` (they add the CSRF header). Escape all dynamic HTML (`OpsUI.esc`).
   Prefix localStorage keys with `mircraft_`.

### 5.4 Conventions

- **JSON** (`WebAdminJson.Options`): DTO and anonymous-object properties are camelCase; **dictionary keys are not renamed** (grid
  rows are keyed by the C# field name, e.g. `Index`, `RequiredClass`, `__rev`); fields are included; nulls are omitted; enums
  are numbers; dates are ISO-8601. Never serialise raw Crystal objects, because of cycles (`CharacterInfo.AccountInfo`,
  `AccountInfo.Connection`, ...). Project them into DTOs.
- **Responses:** `req.Ok(data, message)` → `{ok:true, message, data}`, `req.Error(status, message, data)` → `{ok:false, ...}`, or
  `req.Json(...)`. Throw `WebAdminException(status, message, data)` from anywhere in a handler. Status codes:
  400 bad input · 401 not signed in · 403 not admin / CSRF · 404 · 405 · 409 conflict or wrong world state · 413 body too large ·
  415 · 422 validation · 429 rate limited · 503 world not running (`data.world`) · 504 game thread timeout · 500 *"Internal
  error, see server log"*. Exception text never reaches the client; it is logged.
- **Audit:** call `WebAdminAudit.Record(req, "<area>.<verb>", target, details, reason)` for every successful mutation, and for
  refused sensitive actions where useful. Use dotted lowercase names such as `account.ban`, `gm.kick`, `db.update`,
  `config.update`, `server.reboot`, `script.save` and `backup.restore`. The Audit Trail filters by prefix, and Game Data history
  reads the `db.*` entries. Never put passwords or hashes in `details`.
- **Pending reboot:** after writing data that only Main reloads at world start, call `WebAdminPending.Add(area, description,
  adminName)`. The list clears itself on `WorldStarted`.

### 5.5 Threading rules

- **Never touch live state off the game thread.** Use `RunAsync` or `RunOrInlineAsync`, do the expensive work (hashing, IO,
  serialisation) outside the delegate, and keep the delegate short.
- **`WebAdminGame.EditLock`:** hold it while reading or modifying `Envir.Edit`. Write `Server.MirDB` only through
  `GameDataSave.RunFileIo` / `SaveEdit`, never by calling `Envir.Edit.SaveDB()` directly. Never take EditLock on the game thread.
- **`WebAdminOptions.SettingsLock`:** hold it for every `Settings` read-modify-write and `Settings.Save*()` from web threads,
  because `InIReader` is not thread-safe.
- **Lock order:** `EditLock` → `Envir.LoadLock` → `Envir.AccountLock`. Code that already holds `AccountLock` (the inline path of
  `RunOrInlineAsync`) must not wait for `EditLock`; use `Monitor.TryEnter` and degrade instead (see the GM target map title).
- Text data files are written atomically (temp file + replace) and confined to their root. Paths containing `..`, rooted paths,
  `:` / ADS, reserved device names and reparse-point escapes are rejected.

### 5.6 Tests and builds

```
dotnet test Server.WebAdmin.Tests
```

Tests that change process-wide statics (hooks, `LoginGate`, `WebAdminGame.World`, admins, pending, audit, `Settings`) must be in
`[Collection(WebAdminGlobalsCollection.Name)]` (defined in `GameBridgeTests.cs`) and must restore what they change. Read
`Settings` by reflection metadata only, because its static constructor opens `Configs\Setup.ini`.

> **WARNING:** `Server.MirForms\Server.csproj` sets `BaseOutputPath ..\Build\Server\`, so a plain `dotnet build` of it from the repo
> overwrites the `Server.exe` in `Build\Server\` (which may be your live server with real data). For a throwaway build, use
> `dotnet build Server.MirForms\Server.csproj -o <scratch dir>`.

**Live frontend editing:** set `[WebAdmin] WebRoot` to the source folder, e.g. `WebRoot=C:\Mir2\Server.MirForms\wwwroot`, and
restart `Server.exe`. HTML, JS and CSS are served `Cache-Control: no-cache` with ETags, so a browser refresh picks up edits
without a rebuild.

---

## 6. Known limitations / follow-ups

- The WinForms editors (Item/Monster/Map/... info forms, System/Balance forms) edit the **same** `Envir.Edit` copy and the same
  settings files. Simultaneous edits from WinForms and the web are last-writer-wins, and only the web side keeps `__rev`
  conflict detection.
- The WinForms *Reboot* (and Start/Stop) menu items bypass the web start/stop/reboot transition guard, so a WinForms reboot can
  overlap a web-initiated start/stop/reboot.
- Host shutdown drains in-flight requests for up to 15 s once `Stop()` runs, but writes are not refused between the main form
  starting to close and `Stop()`.
- ag-Grid 32.3.3 logs deprecation warnings in the browser console (the grid modules were ported from WoOOLcraft). They are harmless.
- Login-log files (`logins-*.jsonl`) and audit files (`audit-*.jsonl`) are never pruned. Clean up `Logs\WebAdmin` by hand.
- Character search, and especially search by item, scans every character on the game thread. On very large databases this can
  cause a noticeable tick hitch.
- The host does not handle `X-Forwarded-For`, so behind a proxy every client appears with the proxy's IP (see §2).
- Sessions and maintenance state live in memory and are lost when `Server.exe` restarts.

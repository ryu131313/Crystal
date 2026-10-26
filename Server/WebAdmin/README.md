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
| `RepoPath` | *(empty)* | The Crystal source folder (with `Legend of Mir.sln`) the **Tools** page runs StressClient and the scratch-server scripts from. Empty = search the folders above the exe (finds `C:\Mir2` from `Build\Server\Release`). |
| `ClientDataPath` | *(empty)* | A game client's `Data` folder (the one with `MMap.Lib` and `Map\`), e.g. `C:\Mir2\Build\Client\Release\Data`. The Live Monitor **reads** it (never writes; files are opened with full sharing, so a running client or patcher is not blocked) to draw the minimaps and the real map tiles. Relative paths are resolved against the exe folder. Empty = walkability images only. |

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
  accounts. The panel ignores it when `UserData.db` is missing or was written noticeably later than the snapshot (for example
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
- **Dashboard:** tiles (world state, players, connections, monsters + awake, game thread busy, tick time + lateness, uptime,
  memory, CPU, network out, allocation/GC, pending reboot), Chart.js history (1 h / 6 h / 24 h, sampled every 5 s; **Live** =
  the last 15 min sampled every second, tiles every second, only while selected: the page renews a 15 s server lease with
  `GET /api/admin/status/history?live=1&after=<unix ms>`), a *Server load* band (game loop phases as % of each second, this
  second's phase split and iteration histogram, per-thread CPU as % of one core with a 5 s floor, the mob thread table: CPU,
  busy, passes, 10 ms overruns, slowest pass, queued, awake, AI runs, lateness), monster activity, network and GC charts, host
  metrics, a live activity feed (logins, logouts, failed logins, world events, admin actions), and quick actions.
- **Server Control:** start, stop, reboot and save the world; shut down `Server.exe` (asks twice); reload NPCs, drops, line
  messages and recipes; a restart countdown that announces to players and then reboots; versions and paths; the pending-reboot
  list; a log viewer (live server/debug/chat from the in-process log tap, plus log4net files under `Logs\` with tail and
  download). Start, stop and reboot share a transition guard, so overlapping requests get `409`.
- **Players Online:** a live table of online characters with row actions (inspect, message, kick, kill, safe zone, teleport,
  chat ban, ban account).
- **Live Monitor:** every occupied map with its players, polled every **1 s** (a deliberate exception to the panel's ~5 s
  polling floor, approved 2026-10-11; one game-thread snapshot per poll per open tab). Map: with `[WebAdmin] ClientDataPath`
  set, "Game art" shows the map's authored minimap (`MMap.Lib` frame `MapInfo.MiniMap`) and, once zoomed in past ~4 px per
  cell, real map tiles drawn from the client's `.Lib` files with the client's floor/object rules (animation frame 0, doors
  closed, no lights); otherwise, or with "Walkability" picked, a walkability image (3:2 cells, safe zones tinted). Layers: trails (10/30/60 s, built in the browser from the snapshots; dashed = jump), heat
  (1/5/15 min, sampled by the server every 5 s so it is there when the page opens; all instances of a map index together),
  monsters (all or bosses, focus map only, at most 4000 drawn) and death flashes. Scroll zooms, drag pans, right-click a pin
  or row for player actions, right-click the map to teleport the selected player there, spawn a monster there or clear the
  map's monsters (all through `POST /api/admin/gm/action`, so the usual reason + audit apply). Recent events (deaths with
  the killer, logins, logouts) list on the side. Routes: `GET /api/admin/monitor/snapshot?map=<index>:<instance>&mobs=off|all|bosses&evSince=<seq>&sel=<name>`
  (player rows for the focus map only, counts for the others, plus the `sel` player's row and map wherever they are; the table
  lists at most 200 rows, the map draws everyone),
  `GET /api/admin/monitor/heat?map=<index>&minutes=1..15`, `GET /api/admin/monitor/maps/{index}/image` (PNG, cached per world run),
  `GET /api/admin/monitor/maps/{index}/minimap` and `GET /api/admin/monitor/maps/{index}/tiles/{z}/{tx}/{ty}` (768 x 512 PNG covering
  16·2^z x 16·2^z cells at 1/2^z scale, z = 0..3; rendered on demand from the server's own `Maps\*.map`, one render at a time,
  ~55–180 ms each on this machine; caches ~128 MB of decoded frames + ~192 MB of tile PNGs; `Server/WebAdmin/MapArt/`).
- **Security:** banned accounts, chat bans, admins, panel sessions (revoke), IP blocks (add, remove, clear), IPs shared by
  several accounts, recent failed logins, and a searchable game login log.
- **Tools:** the StressClient bot fleet and a scratch server for it (`Server/WebAdmin/Tools/`, `Routes/ToolsRoutes.cs`,
  `admin-tools.js`). *Stress bots*: target (scratch server or **this server**), count, ramp, duration (0 = until stopped),
  class (or mixed), account prefix/first index/password, behaviour checkboxes (hunt, loot, town runs, quests, potions, equip,
  chat, party, Director routing, run, reconnect) with presets, and advanced numbers; live tiles, an in-game/packets chart and
  the bot output, polled every 5 s. *Scratch server*: start/stop a separate `Server.exe` on 127.0.0.1:17000-17999 through
  `tools\start-scratch-server.ps1` / `stop-scratch-server.ps1` (folder `%TEMP%\mir2-webadmin-scratch`), with synthetic maps or
  copies of this server's maps, reset and rebuild. The bots run as a `StressClient.exe` child process (`--control-stdin
  --status-json`): Stop writes `stop` to its stdin (polite disconnects, killed after 15 s); it also stops when `Server.exe`
  exits, and a self-targeted fleet stops with the world. Needs the source checkout (`[WebAdmin] RepoPath`, else the first
  folder above the exe with `Legend of Mir.sln`) and the .NET SDK for *Build bots* / the first scratch start. **This server**
  is refused unless `[LoadTest] ExemptLoopback=True`, `CheckVersion=False`, logins and StartGame are allowed, the bind is
  loopback/any, the bot password is not the public default and the bot count is within `[Network] MaxUser` (shown as a
  check; the form allows up to 10000 bots); the bots then create real accounts in this server's
  account database. Routes: `GET /api/admin/tools/status`, `POST /api/admin/tools/{bots/start,bots/stop,bots/build,scratch/start,scratch/stop}`;
  audit `tools.bots.*`, `tools.scratch.*` (never the password).

**Operations**
- **GM Console:** pick a target, then run a GM verb such as kick, kill, revive, teleport, give gold/credit/item, set level,
  give skill, give all skills (every spell of the character's class: its `Spell` band plus any skill book for the class), set PK,
  chat ban, spawn monsters, clear monsters or start a conquest. The World actions card also has a **Mass actions** group that
  gives gold, credit, an item, a skill or all class skills, or sets the level (`masslevel`, the online Set level path), for every online player in a scope (everyone, non-GM players, GMs
  only, or players on a map; a map narrows any scope); `massskill` skips players of other classes, and the skill grants raise
  known spells but never lower them. Mass grants are spread over ticks (50 players per tick); the reply says how many are queued
  and the final tally (granted, nothing, partial, logged out) goes to the server log. The verbs use the same code paths as the in-game `@` commands. Most verbs need the world running (`503` otherwise). Some, such as setlevel and chat ban, also work on
  offline characters. Spawn monster takes up to 50,000 at once; its **Map wide** button (spread 1000) scatters them over every
  walkable cell of the map and makes X/Y optional (also in the Live Monitor and Map editor spawn dialogs).
- **Remembered inputs:** the GM inline forms and every `OpsUI.actionDialog` popup prefill the values last used in that form or
  dialog (keyed by the dialog title without its target suffix), held in `sessionStorage` for the browser tab only. Checkboxes and
  fields whose key mentions confirm/password/secret/token are never remembered; `remember: false` on a dialog or field opts out.
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
  - **LiveDb** (`[LiveDb] Enabled=True`, the default; see §4.4): *Apply* sends the changed cells of a grid as **one
    batch** that is validated as a whole, saved to `Server.MirDB` at once and, for the columns marked *live*, applied to the
    running world between ticks. Restart-only columns are saved too and take effect after the next world restart (pending-reboot
    entry). Grid headers show a green dot (live) or an amber dotted underline (restart-only) with a tooltip saying when the change
    takes effect. *Delete selected* sends the rows as the batch's deletes: saved at once, and removed from the running world for
    respawns, safe zones, movements, mine zones and the game shop.
  - With LiveDb off, the running world (`Envir.Main`) **only sees saved changes after a world reboot**. Each save adds a
    pending-reboot entry, which shows in the topbar badge. `PUT ...?live=1` (the "Apply live to running world" box on Monsters)
    still runs one request through the LiveDb pipeline. **Magic** (spell) changes are synced into Main's `MagicInfo` when you
    save, or on the next world start if the world was not running.
  - After any Edit save, Main's periodic `SaveDB()` is skipped until the next reboot (`Envir.SystemDataStale`). Without this,
    Main's old in-memory data would overwrite your save.
  - Saves are serialised with the world's periodic save. While Main still saves, the file IO runs on the game thread between
    ticks; otherwise it runs under `Envir.LoadLock`.
  - Conflicts are detected per row with `__rev`, and dry-run previews are available. Deletes are refused when other data still
    references the row. A rename can be propagated into the recipe and NPC files (with backups; quests link items and monsters by Index since quest V2); drops need nothing (their
    rows link items by Index, and a monster finds its own drop list by Index). *Discard unsaved* reloads the editor copy from disk.
- **Quests** (Game Data › Quests, UserData-and-Quests-Plan.md Part B "Authoring"; `Server/WebAdmin/Quests/`, `wwwroot/admin/admin-quests.js`):
  the quest V2 editor. A filtered list (category, group, storyline, has errors, search) and a whole-graph form: quest columns, steps
  and objectives (pickers for monsters / items / NPCs / maps / spells, target groups), branches, givers (NPC endpoints by
  `NPCInfo.Index`), rewards, an And/Or/Not/Leaf condition builder (owned conditions; shared ones selectable), variables and one
  text box per text field. Save writes Envir.Edit under EditLock with fresh counter ids (never reused) and **validation blocks the
  save** for that quest (422 + every error); refuses while Game Data has unsaved grid changes or a WinForms editor is open. Where
  used (also `GET /api/admin/quests/where-used`), per-quest JSON export and a two-step import (preview diff + token, then apply).
  Saves go live through the quest-replace hook (`QuestEditNotifier.QuestsChangedAsync`, see LiveDb "Quest replace"). Deleting a monster,
  item, NPC, map, quest, quest group or condition that a quest references is refused everywhere (`GameDataEngine.Delete`).
  For authoring speed: a **New** wizard (`wwwroot/admin/admin-quests-wizard.js`) builds the common shapes (kill / collect / deliver /
  talk / go to / reach level, one step per objective) as an unsaved draft, **Duplicate** copies a saved quest on new temporary ids and
  text keys, the Quest tab opens with a giver / turn-in / exp / gold strip and a basic grid (advanced settings behind a toggle) and a
  clickable "ready to play" checklist. Enum members with no runtime are hidden from the dropdowns (meta marks them `reserved`;
  editor-only warnings name the rows that do nothing in game), validation problems carry `refKind` / `refId` so **Show** jumps to the
  row, and a **Test** tab starts / jumps to a step / completes / resets the saved quest on an online character through the characters'
  quest routes (`POST /api/admin/characters/{index}/quests/edit` and `/quests/reset`, audited).
  Places are picked on the map (the map picker draws the ReachArea square and counts the spawns in it); a Kill step can target "monsters
  in an area", which creates or reuses a shared `area.…` monster group and can gate progress with a "only kills on this map" condition.
  Pickers show hints (`GET /api/admin/quests/hints?kind=monster|item|npc|map&id=`: spawn maps, "dropped by", an NPC's map and cell, a map's
  respawns) and item icons (`GET /api/admin/quests/item-icon/{index}`). The wizard and the editor share `wwwroot/admin/admin-quests-assist.js`
  (hints, map thumbnails, teleport, `QuestsAssist.TUNING` reward/level suggestions, one-sentence text writing) and offer templates,
  a region filter for NPCs/monsters, "Use as template" / "Follow-up quest" from a saved quest, a live preview, and **Create, save & start**
  (save, start on an online character, optionally teleport it to the giver) — all through existing audited routes.
- **Drops** (Game Data › Drops, Drops-Tables-Plan phase 2; `Server/WebAdmin/Routes/DropRoutes.cs`, `wwwroot/admin/admin-drops.js`):
  every drop list as rows (the `drop_lists` / `drops` tables in SystemData.db): a list picker (search, kind filter, the monsters
  that load each list) and a grid per list (chance as 1/N, item lookup, quest, gold, group mode, dragon level; group members
  indented; notes, `#INSERT` lines and lines the game can't load shown greyed with their text). The Monsters grid opens a
  monster's list (a monster without DropPath has its own list, bound to its Index; a DropPath list is shared and says so).
  *Save* sends the whole list (dry run first; the list's `rev` must match, else `409`): unchanged lines keep their text, changed and
  new rows get canonical lines, a new or complete group keeps `{ }` around its members; the previous text goes to the Scripts
  backups (`drops/<name>`), the save is audited (`drops.save`), written to SystemData.db at once (drops have no unsaved Edit state)
  and then reloaded in the running world for that list's owners only (`Envir.ReloadDropList`: its monsters, the special lists or
  the dragon; a list another list `#INSERT`s reloads everything). Also *Copy drops from…*, *Delete all disabled*, *Create list*
  and the Assassin/Archer class toggle (`/api/admin/scripts/drops/class-toggle`, all lists). Routes: `GET /api/admin/drops/lists`,
  `GET .../monster?index=`, `GET .../list?id=`, `POST .../save`, `.../create`, `.../copy`, `.../delete-disabled`.
- **Maps:** a visual editor for one map at a time (section `mapeditor`, under Game Data). A filterable map list (pin it beside the
  map, or pick from the drop-down), then the map: walkability, the authored minimap or, with `[WebAdmin] ClientDataPath`, the real
  map tiles once zoomed in; layers for safe zones (green; start points dashed gold), respawns (red square of `Spread`, a marker
  with the monster and count; bosses amber), movements (blue arrow to a destination on the same map, violet stub with the
  destination map's name otherwise), inbound movements from other maps (green triangle), mine zones (cyan), NPCs (gold), online
  players, blocked cells and a grid. Scroll zooms, drag pans, click selects (click again to cycle through overlapping objects),
  double-click follows a movement to its destination map, right-click opens a menu of live GM actions on that cell
  (instance 1, running world only): teleport an online GM there (one click each), teleport a chosen player there, teleport all
  online players or gather the players already on the map there (GM verb `teleportall`, with a spread, 100 players per tick), random-teleport every player on the map to their own random walkable spot (GM verb `randomteleport`, 100 players per tick so a 10k-bot map does not stall the loop; also on a Maps row's right-click in Game data), spawn a monster,
  open the map in the Live Monitor, copy `x, y`. Side column: every object grouped by kind
  with a filter, and the selected object's details (walkable cells in a respawn area, blocked-cell warnings). Under the map: the
  map's own row (summary chips, *Edit all fields…* opens the row form) and its safe zones, respawns, movements, mine zones and
  NPCs as Game Data grids filtered to the map; clicking a row spotlights it on the map. Saves use the Game Data pipeline (LiveDb
  batch, or PUT + save), so the same live / restart rules apply. Unsaved grid edits block switching maps and leaving the
  section until discarded. Deep link `#section=mapeditor&map=<index>[&sel=<kind>:<row index>][&x=&y=]`. Keys: `\` map only,
  `+` / `-` / `0` zoom / fit, Esc leaves full screen or clears the selection. Read routes (editor copy and the server's own map
  file, so they work while the world is stopped): `GET /api/admin/mapeditor/maps`, `GET /api/admin/mapeditor/maps/{index}/overlay`
  (the map row and its child rows exactly as the grids return them, with `__rev`, plus inbound movements and the map size),
  `.../terrain` (walkability PNG, one pixel per cell: the client's wall rule plus the server's old-format middle-tile wall),
  `.../minimap`, `.../tiles/{z}/{tx}/{ty}` (the Live Monitor's renderer, sized from the map file) and `.../players` (empty while the
  world is stopped).
  - **Edit mode** (*Edit* in the map toolbar; needs `[LiveDb] Enabled=True`, `wwwroot/admin/admin-mapeditor-edit.js`): drag an
    object to move it (respawns, safe zones, mine zones, NPCs, a movement's source, an inbound movement's arrival cell (the other map's movement row's Destination_X / Y, same batch); an NPC dropped on a blocked cell snaps back),
    drag the selected object's handles to change its `Size` / `Spread`, drag a same-map movement's destination ring. *+ Safe zone*,
    *+ Respawn* (monster picked first), *+ Movement* (destination 3 cells to the right on this map) and *+ Mine zone* place a new
    object with a click (Shift+click keeps the tool). *Delete* / Del removes the selection (NPCs: in the NPCs grid). The details
    column edits the selected object's fields. Keys: arrows nudge (Shift: 10), `[` / `]` shrink / grow, Ctrl+Z / Ctrl+Y undo / redo
    (100 steps), Esc drops an add tool. Pending changes are drawn on the map (dashed old square + arrow, amber frame for new
    objects, red cross for deletes) and listed under it with checks (no walkable cell in a respawn area, Count above the walkable
    cells, safe zone / respawn overlap, NPC or portal on a blocked cell, outside the map); the checks warn, the server's dry run
    decides. *Review & apply* sends everything as **one LiveDb batch** over the map's tables (edited rows: `Index`, `__rev` and the
    changed cells; new rows whole with temporary negative indexes; deletes with `deleteRevs`) through the usual dry run and preview.
    Rows that share a key with another row (duplicate `RespawnIndex` in old data) are shown but cannot be edited. Unapplied edits
    set the DbSave key `mapeditor:edits`, so switching maps, leaving the section or closing the tab asks first; an overlay reload
    keeps pending edits whose row `__rev` is unchanged and drops (and reports) the others.
  - **Extras:** online-player badges in the map list (`GET /api/admin/mapeditor/online`, one game-thread count, polled every 10 s
    while the section is visible; empty while the world is stopped), optional list thumbnails (the walkability PNGs, loaded as they
    scroll into view), a *Go to x, y* box (`G`), and `?` for every key and mouse action. Edit tools sit on their own row.
  - **Elsewhere in the panel** (`wwwroot/admin/admin-mapeditor-picker.js`): `MapPicker.pick({ map, x, y, walkableOnly, lockMap })`
    is a modal cell picker (map list + the same canvas; click or type X / Y; Enter / Esc). The row form drawer shows *Pick on
    map…* next to every `<Name>_X` / `<Name>_Y` pair: the map is the row's `DestinationMapIndex` for `Destination_*`, else its
    `MapIndex` (locked), otherwise chosen in the picker; NPC locations must be walkable. It hooks in through
    `FormDrawer.EXTENSIONS` (functions called after the drawer builds its fields, with the number inputs; a failing extension is
    logged and skipped). Grid cells get hover previews: a `...MapIndex` cell shows that map, an X / Y cell of a row with a map
    shows the map with the point marked (installed as `window.ImagePreview` unless another module already provides one).
- **Scripts & Files:** a text editor for the NPC, recipe, route and value folders (no quests root since quest V2 wave 4), the `Envir\*.txt` files and
  SystemScripts. It has a file tree, search, validation (missing labels and goto targets, unknown item and monster names,
  `#INSERT` targets), etag conflict detection (`409`), and keeps the file's encoding and line endings. Every save, delete or
  rename first backs up the old version to `Back Up\WebAdmin\Scripts\<root>\<path>.<yyyyMMdd-HHmmss>.bak`, and you can restore
  from the editor. After a save the panel offers the matching reload. Drops are shown **read only** (`readOnly` root: view,
  search, validate, backups; Save / Create / Delete / Rename answer `409`): edit them in Game Data › Drops. Restoring a drops backup
  still works here, as the undo for a grid save.
- **Backups & History:** lists `Back Up\Database`, `Back Up\Accounts` and the script backups. You can make a manual copy of
  `SystemData.db` or `UserData.db` (both `VACUUM INTO` on the store's writer thread, never a file copy), download, and prune.
  **Restore only works while the world is stopped** (`409` otherwise). It backs up the current file first (`Pre-restore <ts>.bak`),
  then reloads the editor copy or the account list. An accounts restore swaps the file through the UserData writer (integrity,
  schema and world checks first), drops every pending account / guild delete, and refuses a binary `Server.MirADB` backup with
  the import route instead (Runbook §12). The live version shown for accounts is `meta.envir_version`, read through the writer. The *History* tab shows per-field
  game-data changes from the audit log, with a Revert action (not for inserts, deletes or position-keyed child tables).
- **LiveDb History:** every LiveDb batch from the journal (filter by table, admin and date; paged), each expandable to the
  before → after of every field with what reached the running world, the live counters and the client push. **Undo** previews
  the inverse batch (dry run) and applies it after a confirmation; conflicts are shown under the batch. A status line shows
  whether LiveDb is on, the last batch, the sweeper queue and diverged parents (also the *LiveDb* tile on the Dashboard).

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
- `Envir.AddAdminTickJob(Func<bool> step)` (game thread only, from a queued action) runs `step` once per tick after the drain until it returns true; for jobs too big for one tick (`randomteleport`, `teleportall`, the mass grants). Discarded when the loop exits.
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
| `AccountsSaved` | after Main serialises accounts (`SaveUserData`, the shutdown save, the offline `SaveAccounts`) |
| `LoginAttempt(accountId, ip, result)` | `Envir.Login`: `no-account`, `banned`, `bad-password`, `refused:<reason>`, `ok` |
| `LoginGate` (single `Func<AccountInfo,string>`) | `Envir.Login`, before a successful login. Owned by Maintenance; a non-empty result refuses the login. |
| `PlayerEntered` / `PlayerLeft` | `PlayerObject.StartGame` (after joining `Envir.Players`, before the Login scripts) / `PlayerObject.StopGame` |
| `PlayerDied(victim, lastHitter)` | end of `PlayerObject.Die`. **May run on a mob thread** (a monster kills a player during the mob pass): subscribers must be lock-free (the Live Monitor only enqueues into a capped `ConcurrentQueue`) |
| `Chat(channel, sender, message, target)` | `PlayerObject.Chat`: normal, shout, shout-map, shout-server, whisper, group, guild, mentor, relationship, announcement |

### 4.3 Stale system data guard

`Envir.SaveDB()` sets a static `_systemDataStale` flag when `this == Edit`. While `Envir.SystemDataStale` is true, Main's periodic
save in `WorkLoop` skips `SaveDB()`, so Main's stale in-memory system data never overwrites newer data that the panel or WinForms
saved through Edit. The flag clears when Main reloads its database (Start/Reboot). The MonsterTuner and Dragon WinForms forms
used to call `Main.SaveDB()` on close; they now skip it (with a message box) while `[LiveDb] Enabled` is on or the flag is set,
so Edit stays the only writer of system data.

Two details keep Edit a faithful writer: the respawn tick counter only advances in Main, so Main publishes
`RespawnTick.CurrentTickcounter` after each `RespawnTick.Process()` and Edit's `SaveDB` writes that published value (F4); and
Edit's clock never advances, so editor saves name their backup from the wall clock through `LiveDbBackupPolicy`: a timestamped
`Database yyyy-MM-dd HH-mm-ss.bak` at most every `[LiveDb] BackupEveryMinutes`, a rolling `Database livedb-previous.bak` in
between (F3, `Envir.SaveDB(string backupFileName)`).

### 4.4 LiveDb (live game-data edits)

Settings, `[LiveDb]` in `Setup.ini` (all read at use time, also on the Server Config page): `Enabled=True`,
`BackupEveryMinutes=10`, `SweepPerTick=512`, `MaxBatchRows=2000`, `MaxPushRows=500` (client push limit, see below).

Routes (`Routes/LiveDbRoutes.cs`, admin + CSRF like every mutating route):

| Route | Purpose |
|---|---|
| `GET /api/admin/livedb/status` | enabled, world, open WinForms editors, unsaved legacy edits, live tables, sweeper, diverged parents, last batch |
| `GET /api/admin/livedb/schema/{endpoint}` | per column: effective `liveClass` / `liveTiming`, the plan's class, a note |
| `POST /api/admin/livedb/batch[?dryRun=1]` | `{id, reason?, flushUnsaved?, tables:[{endpoint, rows:[changed cells + Index + __rev], deletes:[...]}]}` |
| `GET /api/admin/livedb/history[?limit=&offset=&days=&table=&admin=&from=&to=]` | recent batches from the journal, newest first, paged and filtered; per batch the live counters, `inverseOf` (it is an undo) and `undoneBy` (an applied undo reverses it) |
| `GET /api/admin/livedb/history/{id}` | one batch: every row's before / after values with its live outcome, full row images for inserts / deletes, the undo state (`undoId`, `undoable`, `refused`, `notes`) |
| `POST /api/admin/livedb/undo/{id}[?dryRun=1]` | `{reason?, flushUnsaved?}` → the inverse batch's outcome plus `target`, `undoId`, `alreadyUndone`, `undoNotes` |

Pipeline (`LiveDb/LiveDbEngine.cs`, on a `Task.Run` worker holding `EditLock`): checks (world starting/stopping → 503; a WinForms
DB editor open → 409, counted by `Envir.EditorFormsOpen` around `SMain`'s editor dialogs; other unsaved editor-copy edits → 409
unless `flushUnsaved:true`; a batch id seen before → the stored outcome, `duplicate:true`) → **validate all** rows with dry runs
(any conflict or validation problem rejects the whole batch: 409 / 422, nothing changes) → apply to Edit with before-images →
save (`GameDataSave.SaveEdit(backupName)`; a failure restores Edit, 500) → **one game-thread step** (`LiveDbMainApplier`: copies
the live columns onto Main's records, clones new rows into Main (monsters load their drops), removes deleted rows, copies spells,
runs the hooks; any exception restores Main — values, rows, and each hook's own compensation — and keeps the save, reply "takes
effect after restart"; last, it queues the **client push**) → journal + audit + pending-reboot entry. A batch that moves or adds an
NPC first asks the game thread whether the target is a walkable cell of a loaded map (after Edit was changed, before the save): an
unwalkable cell rejects the whole batch (422, Edit restored); a map the running world has not loaded saves the move only. Refusals
of the running world are per row / field with the reason in `rows[].changes[].reason` / `rows[].rowReason` and counted in
`live.refusedLive` (the change is saved and applies after a restart).

Classification (`LiveDb/LiveDbClassification.cs`): every column of every table is `ReadThrough`, `Hooked`, `RestartOnly` or
`QuestReplace`, with a timing (now / hook / spawn / clients / restart / quest-replace). Every table is live except the six conquest
child tables (restart-only), the quest V2 tables and `quests` (see "Quest replace" below).
Items: price, requirements, bind, flags and the other
read-on-use columns are read through; Stats, Weight, Light, Set, Shape, Effect, Unique and CanFastRun refresh every online player
and spawned hero holding the item (equipped, in the bag or socketed, including holders of a class / level based base item:
`HumanObject.RefreshStats`, which also re-broadcasts looks and light); RandomStatsId re-resolves `RandomStats`; StartItem rebuilds
`Envir.StartItems`; new items are cloned into Main; Name, Type and deletes are restart-only. Monsters: Level, Experience, ViewRange and the Can*/IsBoss flags are
read through; stats and speeds queue the budgeted sweeper (`LiveDbSweeper`: at most `SweepPerTick` world objects per tick after
the admin-action drain, `RefreshAll` + HP clamped to the new maximum, no scaling); DropPath reloads that monster's drop file;
Name, Image, AI, Effect, CoolEye, Light, Undead, AutoRev and deletes are restart-only.

World tables (Batch C; the hooks are `ILiveDbHooks` / `LiveDbWorldHooks`, game thread, plain loops, per-object errors isolated with
`WorldErrors.Record`):

| Table | Live | Restart-only |
|---|---|---|
| npcs | visibility fields → schedule + per-player check for nearby players; Image / Name → re-broadcast; FileName → script rebound (speech from the new script, unsaved used goods kept; a script that fails to load refuses only FileName); MapIndex / Location → moved (`RelocateNpc`: leave the old cell while `Info.Location` still names it, then change, then enter — F10); ShowOnBigMap / BigMapIcon (and any change of an NPC shown on the big map) → map info invalidated; insert → `NPCObject` spawned as at map load | Conquest, ConquestVisible, delete |
| quests | — (Name, Group and the V2 columns are `QuestReplace`: edited in the Quests page; the legacy columns were dropped in quest V2 wave 4) | insert / delete only in the Quests page |
| maps | flags (read through `Map.Info`); MineIndex → mine rebuilt (mine state resets); Title / BigMap → sent at the next map entry, big-map info invalidated for the map and for maps whose movements show it | FileName, GT, insert, delete |
| mapsafezones | the map's safe-zone spell objects removed (only caster-less, never-expiring zone decorations / healing cells; conquest war borders skipped) and recreated from the new zones, `Envir.StartPoints` rebuilt, `InSafeZone` recomputed for players, heroes and pets on the map; insert, delete | — |
| maprespawns | Count / Delay / RandomDelay / Direction read at the next spawn; MonsterIndex / Location / RoutePath / Spread → `MapRespawn` cache rebuilt in place (monster, spawn cells, route; spawned monsters stay and keep counting against it); SaveRespawnTime / RespawnTicks → `Envir.SavedSpawns` membership; insert (spawns at the map's next respawn check, within `[Optimisation] MapRespawnCheckMs`, default 1 s), delete (spawned monsters stay) | — |
| mapmovements | read when a player steps on the cell; big-map info invalidated; insert, delete | — |
| mapminezones | mine rebuilt (mine state resets); insert, delete | — |
| gameshop | read when the shop opens / an item is bought; ItemIndex → bound to Main's item; insert (bound to **Main's** `ItemInfo`), delete; pushed to every player in the game (stock level per player as at login; a row sold out for that player is sent as removed) | — |
| conquests | schedule and area fields; a conquest whose war is on refuses the row (saved only) | Map, Palace, ExtraMaps, FullMap, counters |
| dragon | Exps (read on level up); drop area → recomputed | Enabled, map, monster names, location |

Position-keyed child rows (safe zones, movements, mine zones; keys `MapIndex * 10000 + position`) are matched to Main by position,
so the live step first checks every touched list: its length must equal the editor copy's before the batch, and each touched row
must still hold the editor copy's pre-batch values. A list that does not line up is never changed by position (rows refused, the
parent marked diverged); a single row with other values is refused alone. Inserts append and deletes are resolved first and removed
by reference, exactly as in Edit. One batch may insert and delete rows of the same list (the Maps editor does): the inserts are
applied before the deletes, so an inserted row's outcome is found by its record and reports its key after the deletes
(`LiveDbEngine.CaptureInserted`). A structural change (insert / delete) that does not reach Main — a restart-only table, a failed or
refused live step — marks the parent diverged (`LiveDbState`), and live edits, inserts and deletes of its children are saved only
until the next world start. Respawns are keyed by their global `RespawnIndex` and never diverge.

**Quest replace** (UserData-and-Quests-Plan.md Part B "Live edits", wave 3; `Quests/QuestEditNotifier.cs` →
`MirEnvir/Quests/QuestReplace.cs`): the quest V2 tables (`queststeps`, `questobjectives`, `questbranches`, `questendpoints`,
`questrewards`, `questvars`, `conditions`, `texts`, `storylines`, `questgroups` + members, `questpools` + members) and the V2 columns
of `quests` are `QuestReplace`: read only in the grids, never applied row by row (their position-keyed children would diverge). The
Quests page (save, delete, JSON import, group save / delete) calls `QuestEditNotifier.QuestsChangedAsync`
after the store was written: under `EditLock` it copies the changed quests' whole graphs and the shared quest tables from
`Envir.Edit` (`QuestReplace.Take`, plain copies), then one `WebAdminGame.RunAsync` (30 s) runs `QuestReplace.Apply` on the game
thread: every referenced monster / item / NPC / map / group member is resolved against Main (a missing id keeps that quest's old
graph and is reported), the fresh rows are swapped into Main's quest lists with the old contents as before-images (any exception
restores the whole batch), the catalog is rebuilt, quests that use a changed shared row (text, group, shared condition, storyline;
a reverse index built from Main each time) are added, and every online player runs `PlayerQuests.ApplyDefinitionChange` (revision
migration of runs, index rebuild, the quest dropped from the connection's sent set and re-sent where it was held, `S.QuestStates` /
`S.QuestProgress` for migrated runs, availability re-sent to NPC entries in view, an empty entry for an NPC that no longer has
quests). A deleted quest's online runs are dropped (`S.QuestStates` None), history stays. The routes return `live` (applied,
replaced, deleted, refreshed, players, failed with reasons); only what could not be applied (world not running, a missing
reference, a rollback, a timeout) goes to `WebAdminPending`.

Client push (`LiveDb/LiveDbPush.cs`, packet `S.GameDataChanged`, private): after the hooks, the live step snapshots every
changed item (a fresh `ItemInfo` copy), monster (`ClientInformation`) and spell, then, under `lock (Connections)`, sends each
connection in the game (and each spectator) only the rows in its own `SentItemInfo` / `SentMonsterInfo` / ... lists, and each
player's / hero's own `ClientMagic` for changed spells. Elements are measured by writing them to a scratch buffer and packets are
cut at 60 KB (never above 65 535 bytes); the last packet of a connection has `Last = true`, when the client refreshes its stats,
skill bars and tooltip. Packets hold copies, never Main references (EncodeOnce). A push failure is logged and never undoes the
world change (clients see it at their next login: `CheckItemInfo` sends the current record). A batch with more client-visible rows
(item / monster / spell updates) than `MaxPushRows` is not pushed: its item updates are saved only (reason in the outcome),
monsters and spells still apply live. The outcome's `live` block reports `itemHoldersRefreshed`, `pushConnections`,
`pushPackets`, `pushRows`, `pushSkipped`, `pushFailed`. Batch C fills the other sections: changed NPCs (`ClientInformation`),
quests (each connection's own `CreateClientQuestInfo(player)` copy, which first sends the infos its links mention), game-shop rows
(a copy whose `Info` is a copy of Main's item; per-player stock) and removed shop indexes, and invalidated maps (the connection's
`SentMapInfo` entry is dropped so the next request re-sends it). NPC, quest and game-shop rows count against `MaxPushRows` too
(only item updates are refused live when the limit is passed). Client: `GameScene.GameDataChanged` (in-place `ItemInfo.CopyFrom`, upserts, spells by `Spell`,
shop remove + add, invalidated big-map records dropped and re-requested); `S.NewItemInfo` / `S.NewQuestInfo` now upsert (F7).
Ship client and server from the same commit.

Journal: `Logs\WebAdmin\livedb\batches-yyyy-MM-dd.jsonl` (next to the exe; ops with before / after images and revs, the live
outcome, the backup file, `inverseOf` and notes for undo batches), plus an LRU of the last 500 batch ids. Reading a line back
restores the row images too, so undo works after a restart (for the journal's last 14 days).

Undo (`LiveDb/LiveDbUndo.cs`; Batch D): `LiveDbUndo.Plan` builds the inverse of a journaled batch (pure, no Edit / Main access) and
it runs through the same engine pipeline, so it is all-or-nothing, saved, applied live, journaled (`source: "undo"`,
`inverseOf`) and audited like any batch (`db.update` / `db.delete` rows carry `inverseOf`; `db.livedb-batch` too). Updates go
back to their recorded before-values with `__rev` = the row's `revAfter`, so a row changed after the batch is a conflict and the
whole undo is refused (409, per-row conflicts; nothing changes). Inserts become deletes guarded by `revAfter` (the new optional
`deleteRevs: {"index": "__rev"}` of a batch table; a mismatch is a conflict). For position-keyed rows the key
(`parent * 10000 + position`) names the same row only while no earlier sibling was deleted; the rev guard refuses it otherwise.
Removing an added respawn, game-shop row, safe zone, movement or mine zone is live; an added item, monster or NPC is removed from the
saved data only and the running world keeps it until a restart (said in the outcome's `notes`). **Deletes cannot be undone** (a
re-insert would get a new RespawnIndex / GIndex or go to the end of its list): such a batch answers 409 with a hint to restore a
`Back Up\Database` backup with the world stopped. Other editor changes a batch saved with `flushUnsaved` are not reverted. The undo
id is derived from the target: `undo:<id>` (a hash when longer than 64 characters), so a repeated click returns the stored outcome
(`alreadyUndone`); undoing an undo is a redo (`undo:undo:<id>`), after which undoing the original again uses `undo2:<id>`. A dry
run (`?dryRun=1`) previews the rows, their live / restart outcome and any conflict. Undo refuses like any batch: LiveDb disabled
(409), world starting / stopping (503), a WinForms DB editor open (409), other unsaved edits (409 unless `flushUnsaved`).

Status: `GET /api/admin/status` carries a `livedb` block (`enabled`, `batches`, `undos`, `rejected`, `liveFailures`, the last
batch's id / time / live outcome / saved, `sweeperActive` / `sweeperPending`, `divergedCount` / `diverged`) for the Dashboard tile. Audit: one `db.update` / `db.insert` / `db.delete` per row with
`batch:<id>` in the details, plus `db.livedb-batch`. Each batch logs `[LiveDb] batch …: validate / apply / save / live ms`.

### 4.5 Log tap

`MessageQueue.Enqueue`, `Enqueue(Exception)`, `EnqueueDebugging` and `EnqueueChat` also send each line to
`WebAdminLogTap.AppendServer`, `AppendDebug` and `AppendChat`. These are ring buffers with sequence ids, read by
`/api/admin/logs/{kind}`. The WinForms log views and log4net are unaffected.

### 4.6 Changes to existing Crystal files

| File | Change |
|---|---|
| `Server\MirEnvir\Envir.cs` | Admin action queue region (`QueueAdminAction`, `RequestSaveNow`, `AdminQueueOpen`, `WorkLoopAlive`, open/drain/close), `SystemDataStale`, the `WorkLoop` drain call and save guard, hook calls in WorkLoop, StartNetwork, account saving and Login; LiveDb: `SaveDB(string backupFileName)`, the published respawn tick counter, the `LiveDbSweeper.Tick` call after the drain, `EditorFormsOpen` |
| `Server\MirEnvir\RespawnTimer.cs` | `Save(writer, tickCounter)` overload (same layout) |
| `Server\MirObjects\PlayerObject.cs` | `PlayerEntered` / `PlayerLeft` / `PlayerDied` / `Chat` hook calls |
| `Server\MessageQueue.cs` | Log tap calls |
| `Server\Settings.cs` | `WebAdmin*` fields, plus Load and Save of the `[WebAdmin]` section; `LiveDb*` fields and the `[LiveDb]` section |
| `Server.MirForms\SMain.cs` | `ShowEditor(form)` counts the open DB editor dialogs (`Envir.EditorFormOpened/Closed`) |
| `Server.MirForms\Systems\MonsterTunerForm.cs`, `DragonInfoForm.cs` | Skip `Main.SaveDB()` with a message box while LiveDb is enabled or `SystemDataStale` |
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
  conflict detection. LiveDb batches are refused (409) while a WinForms DB editor dialog is open, but a legacy PUT is not.
- LiveDb (this build): items, monsters and magic reach the running world (with the client push); other tables are saved and
  wait for a restart. Deletes in the grids still use the legacy delete + database/save. NPCs, quests, maps, the game shop and
  the other world tables (with their push sections) and undo come in later batches. An old client without the
  `GameDataChanged` packet would discard its receive buffer: always deploy client and server together.
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
- Live Monitor: there is no per-player idle time; heat buckets, the event ring and the map-art caches live in memory only.
  Map art needs a client `Data` folder whose libraries match the maps (`[WebAdmin] ClientDataPath`); the tile renderer copies
  the client's map parser and library table (`MapArt/ClientMapData.cs`, `MapArt/MapLibCatalog.cs`), so a new map format or
  library slot in the client must be added there too. Tiles show animation frame 0 with doors closed and no lighting.

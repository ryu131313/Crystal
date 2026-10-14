# Mir Ametrine client vs Crystal desktop client — comparison

*Written 2026-10-13 from a read-only review of the decompiled Mir Ametrine client at `C:\Mir Ametrine\Decompile\` (not in this repo). Nothing was changed in either codebase.*

**Status:** ideas only — nothing here is scheduled. Each item needs its own task (and roadmap slot if it is a modernisation phase). Fixing Crystal Known debt listed in `Client/client.md` remains a separately requested task.

**Licensing:** Ametrine is decompiled third-party code. Borrow **ideas and designs**, never copy its code.

**Path conventions:** `A:` = `C:\Mir Ametrine\Decompile\MirAmetrine.Client\` unless another project is named (`MirAmetrine.Shared\`, `MirAmetrine.AssetLibrary\`). `B:` = `C:\Mir2\Client\`. Line numbers are approximate (decompiler output).

---

## 1. What Ametrine is

- A ground-up rewrite, not a Crystal fork: ~140k lines, .NET 10, D3D11 + Direct2D/DirectWrite via Vortice, XAudio2, TLS, SharpCompress (xz).
- Three assemblies: `MirAmetrine.Client` (Graphics, Audio, Networking, Patching, Scenes, UserInterface, World), `MirAmetrine.Shared` (497 networking files, Patching, World rules), `MirAmetrine.AssetLibrary` (runtime reader + PNG authoring for its `.maim`/`.mamap` formats).
- World layer is well factored: ~215 small files, many 10–70-line records/policies, `readonly record struct` decisions, injected callbacks (`Func<Point,bool>`, `IMapCellSource`) so logic is unit-testable, shared rule classes (`Shared/World/CombatRules.cs`, `MiningRules.cs`, …) keep client predictions consistent with the server.
- Weakness: the god object moved up one layer — `A:Scenes\GameScene.cs` is ~21k lines (Crystal's is ~13k), and `MainForm.cs` is ~18k.

---

## 2. Rendering, graphics, assets

| # | Ametrine does better | Evidence (A) | Crystal today (B) | Effort for B |
|---|---|---|---|---|
| R1 | **BC1-compressed asset format** `.maim`: magic + version + offset index; 36-byte record header; main/shadow/overlay images as pre-compressed BC1 blocks uploaded as `Immutable` textures — no gunzip, no CPU conversion, ~8× less VRAM than ARGB. Thread-safe `RandomAccess.Read` on a `SafeFileHandle`; limits validated | `MirAmetrine.AssetLibrary\Runtime\RuntimeLibraryContainer.cs:155-215`, `RuntimeImageLibrary.cs`, `RuntimeLibraryLimits.cs`; `Graphics\GraphicsTexture.cs:41-49` | `.Lib`: gzip + A8R8G8B8 decoded on the UI thread (`MirGraphics\MLibrary.cs` `CheckImage`/`CreateTexture` ~1117/1140) | **High** — converter in LibraryEditor, new reader, compressed-texture creation in both backends (D3D9 has DXT1/DXT5). BC1 has 1-bit alpha: use BC3/BC7 for soft-alpha UI/effects |
| R2 | **Async decode + per-frame upload budget**: CPU prep on the thread pool (`Lazy` in `ConcurrentDictionary`), GPU materialise on the main thread capped (20 urgent, 1 ms background); monster art pre-warmed | `World\MapTextureCatalog.cs:100-113`, `MapTextureCache.cs:28-44`, `MonsterTextureCatalog.cs:53-57,172+`, `MapRenderer.cs:60,251`, `Scenes\GameScene.cs:1000` | Synchronous decode on first draw — Known debt (client.md §3.1) | **Medium** — worker decode → `ConcurrentQueue` → upload N/frame, draw nothing until ready (sanctioned hand-off pattern) |
| R3 | Ref-counted texture eviction (10 min idle, 1 s scan) | `World\MapTextureCache.cs:74-116` | `DXManager.Clean`, time only | Low (neither has a memory budget) |
| R4 | **Precomputed hit masks** per texture at load | `Graphics\TextureHitMask.cs`, `GraphicsTexture.Prepare` :83-95 | `VisiblePixel` reads the dangling `MImage.Data` pointer (client.md §3.3 NEVER) | Low–medium; removes a real hazard |
| R5 | **Independently cached world layers**: terrain / objects / additive / lighting render targets, each invalidated on its own (animated terrain on 200 ms ticks, lighting only on light change, moving objects drawn direct) | `World\MapRenderer.cs:107-110,838-908,2086-2121` | One map RT + `FloorTexture`; `RebuildOptimisations` rebuilds everything together | **Medium** — split `MapControl.CreateTexture`, lighting first |
| R6 | **Per-character composite RT** (mount/back/body/hair/weapons) rebuilt only on (action, dir, frame) change, drawn premultiplied — correct whole-character translucency/tint, cheap hover outline | `World\CharacterCompositeTexture.cs:43-73,107,156-167` | Each layer drawn per rebuild; translucency shows overlaps | Medium — pooled RT per visible player, must sit in `ControlList` for D3D9 reset |
| R7 | **Per-window UI caches** (`UiTextureCache`, palette-generation counter for theme invalidation) | `Graphics\UiTextureCache.cs`, `GraphicsDevice.DrawUiCache` :162-183 | Whole-scene UI RT + `LiveOverlay` exceptions (client.md §2.4) | Medium — generalise `LiveOverlay`: one RT per top-level dialog, z-ordered composite |
| R8 | **DirectWrite text** on the D3D11 device: grayscale AA, cached formats, ellipsis, colour ranges, true pixel scale | `Graphics\UiRasterizer.cs:83-87,168-186` | GDI `TextRenderer` into a locked texture, 5 draws per outlined label | Medium, D3D11 path only (no D3D9 equivalent → parity waiver needed, see `Client/D3D11-Parity.md`) |
| R9 | Resolution-independent UI: logical 1280×720 × `min(w/1280,h/720)`, rasterised at real scale | `UserInterface\UiViewport.cs:16-22`, `UiScale.cs` | 4 fixed resolutions; login/select 1024×768 | High (fixed-pixel `MirControl` layout) |
| R10 | Shader extras: dither-shadow → 42 % alpha fill, skewed projected shadows, radial light smoothstep, in-shader hover outline, `LightMap` multiply blend | `Graphics\TextureRenderer.cs:22` (one HLSL string) | — | Low–medium, D3D11 only (golden/parity implications). **Dither-shadow fill done in the web client 2026-10-13** (`SHADOW_FILL_PS`, own checkerboard-aware rule; webclient.md port notes) |
| R11 | `AllocationDiagnostics`: ~100-line `EventListener` on GC allocation-tick events, top allocating types | `AllocationDiagnostics.cs` | `FrameProfiler` has no allocation view | Trivial |
| R12 | Frame limiter (sleep-then-spin, drift-free) + one timestamp per frame (`ClientFrameTime`) | `FrameLimiter.cs`, `ClientFrameTime.cs`, `ApplicationLoop.cs` | Uncapped | **Do not adopt the cap** — the user runs uncapped on purpose. The single per-frame timestamp idea is fine |

**Where Crystal is already ahead (rendering):**
- **Batching** — A issues one `Draw(6)` per quad with a constant-buffer update, full state rebind and 4 array allocations per draw (`TextureRenderer.cs:233-355`). B's `MirGraphics\Rendering\D3D11Backend.cs` ring buffer (4096 sprites, `WriteNoOverwrite`/`Discard`, one `DrawIndexed` per texture run) is far better.
- **GC in the frame** — A runs LINQ in `MapRenderer.Draw` (:793,823,872,883), a closure per damage number, and a D3D texture + D2D bitmap per damage number (`World\DamageNumberOverlay.cs:100,161`).
- **Device robustness** — A has no device-removed recovery (`GraphicsDevice.cs:71-76` throws), FL 11_0 only, no fallback. B has D3D9 fallback, device-lost reset, frame-latency control.
- **Weather/lighting** — A's `Scenes\SceneAtmosphere.cs` is additive particles on login/select only; B's `ParticleEngine` weather + Enhanced float light map with day/night and flicker is richer.
- **Sharpness** — A's world layer is a fixed 1282×722 upscaled linearly (blurry at 1080p); B renders the world natively and shows more map.

---

## 3. Networking, patching, lifecycle, audio

| # | Ametrine does better | Evidence (A) | Crystal today (B) | Effort for B |
|---|---|---|---|---|
| N1 | **Validated protocol**: `[u32 len][u16 id]` frame; explicit IDs; `ProtocolMessageRegistry` gives each message a direction, allowed connection stages and a max payload (ping = 8 bytes); length checked before allocation; unknown/oversized → clean disconnect; `PacketReader.Complete()` rejects trailing bytes; `Read` validates fields semantically; UTF-8 strings with max bytes | `MirAmetrine.Shared\Networking\ProtocolFrame.cs` ~150-165, `ProtocolMessageRegistry.cs` 207+/325-650, `MessageDescriptor.cs`, `NetworkMessageId.cs`, e.g. `MovementResultMessage.cs:22-48` | Implicit-ordinal IDs; u16 length with no guard; unknown ID discards the whole receive buffer (`Shared/Packet.cs`) | Per-packet caps + unknown-id disconnect + trailing-byte check: **M** (430+ packet classes). u32 frame: M, wire break. Stage-gated registry: L |
| N2 | **Explicit protocol version** (`NetworkProtocol.Version`) in hello; mismatch → `UpdateRequiredMessage` | `MainForm.cs` ~2938/4263 | MD5 of the apphost exe (weak; client.md §3.5 Known defect) | S (server too) |
| N3 | **TLS** via `SslStream`; self-signed allowed only for loopback | `Networking\LoginNetworkClient.cs` ~190-200, `NetworkProtocol.IsServerCertificateAllowed` | Plaintext TCP; plaintext password in ini | M (certificate provisioning) |
| N4 | **Connection health**: async send/recv/heartbeat tasks; bounded outbound `Channel` (64) and inbound semaphore (512) for backpressure; per-connection generation counter drops stale events; connect 15 s / idle 10 min / partial-payload 30 s timeouts; nonce ping every 30 s, 90 s timeout, RTT exposed; exponential backoff 3–30 s with jitter, on-screen 5 s countdown, reconnect after desync | `LoginNetworkClient.cs:135-260`, `HeartbeatTracker.cs`, `RecoveryRetry.cs`, `MainForm.cs` ~4031-4135 | APM, 8 KB buffer, KeepAlive after 5 s idle, 20 fixed 5 s retries, no half-open timeout, no RTT, silent catches (client.md §2.6/§3.4) | Heartbeat+RTT, timeouts, jittered backoff: **S** (1–2 d, in `Network.cs`). Async + bounded queues + generation: M |
| N5 | **Transport telemetry**: per-frame trace (queue depths, write latency, stage changes) to a capped 64 MB file via a bounded channel | `Networking\GameplayNetworkDiagnostics.cs` (613 lines) | — | S–M |
| N6 | **Sequenced prediction**: `MovementRequestMessage.Sequence` → `MovementResult` Accepted/Corrected with authoritative X/Y; results for cancelled sequences dropped; magic drawn predictively then confirmed/replaced/cancelled; `ForcedPlayerMovementMessage` | `Scenes\GameScene.cs` ~5271, 5287+, 5419-5468, 6218; `World\MagicEffectCatalog.cs:149-170`; `World\Objects\UserObject.cs:98` | No sequence numbers on replies | **L** (1–2 wk, client + server) |
| P1 | **Patcher**: `PList.Bin` (name, MD5, compressed size), xz; GUID cache-buster; hash computed while decompressing; temp file + atomic move, old file to a trash folder; 4 workers smallest-first; jittered retry 1–30 s | `Patching\PatchClient.cs` 203-233, 609-700; `MirAmetrine.Shared\Patching\PatchList.cs`, `PatchLocations.cs` | `Forms\AMain.cs`: Length+Creation change check (~233), gzip, optional **SHA-256** (~297, stronger than MD5), non-atomic `File.WriteAllBytes`, no backoff | S–M (keep SHA-256) |
| P2 | **On-demand assets**: installed content patched eagerly, missing assets fetched on first use (`Demand`/`Promote`); per-asset revision counters let the renderer hot-reload | `PatchClient.cs` 544-575 | — | L |
| P3 | **Transactional program update**: separate `MirAmetrine.Patcher.exe`, cross-process transaction file with rollback; auto sign-in after update via Windows Credential Manager | `Patching\ApplicationUpdate.cs` 214-275, `InstallationUpdate.cs`, `RecoveryCredentials.cs`, `SessionRecovery.cs` | Rename to `Old__` + restart (~305-345) | M |
| L1 | **Crash/incident reporting**: crash log per crash in `%LocalAppData%\Mir Ametrine\Crashes`; recoverable errors logged once; incidents fingerprinted (SHA-256 of kind+type+stack) with build id, protocol version, recent network trace; persisted atomically (≤ 20 pending) and uploaded over the game connection; admin view; chunked hashed log bundles attachable to tickets with player consent | `ClientCrashReporter.cs` 62-80, `ClientIncidentReporter.cs` 183-290, `ClientIncidentRules.cs`, `SupportLogBundle.cs`, `TicketLogTransfer.cs` | `Error.txt` (cwd-relative, 100 entries/process, no dedupe, no upload) | M |
| L2 | Main loop recovers from per-frame exceptions | `ApplicationLoop.cs` | — | S |
| L3 | **Settings**: JSON, saved under a named mutex via temp file + `File.Replace`; corrupt file → defaults | `ClientSettingsStore.cs` 54-80 | CWD-relative ini, plaintext password | S (atomic write) |
| L4 | Audio: XAudio2 with 4 buses (music/system/monster/player), voice caps (24 monster + 16 player, 32 total, oldest stolen), focus mute unless background sound on; typed sound-cue catalogs | `Audio\AudioDevice.cs` + cue catalogs | NAudio `MixingSampleProvider` (`MirSounds\SoundManager.cs`), integer sound indexes | S–M on NAudio |

**Where Ametrine is worse (networking/patching):** MD5 manifest, unsigned; no resumable downloads (no Range); no protocol compression; settings in the exe folder (breaks under Program Files); music not streamed (whole PCM buffer looped 255×); no positional audio; a single hard-coded protocol version with no compatibility range.

---

## 4. UI / UX / quality of life

`[C]` = client-only, `[S]` = needs server packets/persistence.

**Windows / layout**
- Saved position + size for every major window (`UiWindowLayout` in `ClientSettings.cs`); base `UserInterface\UiWindow.cs` has drag, 8 px resize border, clamping, anchors (`UiWindowAnchor.cs`). B: 62 movable dialogs, positions not persisted (only `SkillbarLocation`), none resizable. **[C]**
- Map window (`World\MapWindow.cs`, ~1.7k lines): map list + search, respawn overlay, monster/NPC inspector tabs, legend; click-to-teleport callback (GM gating unclear). B: `BigMapDialog`. [S] for spawn data.
- Expandable minimap with keybind, portal icons, monster markers, coords, map history (`World\MiniMapRadar.cs`). B: basic `MiniMapDialog` (`MainDialogs.cs` ~1805). [C]
- Expandable target frame with HP, stats, elemental affinity (`World\MonsterTargetWindow.cs`, `MonsterHealthOverlay.cs`). B: HP bars only. [S] for stats.
- Hide-interface toggle; `UiScale`/`SceneScaling`. [C]

**Chat** (`World\ChatWindow.cs`): resizable 320–850 × 170–540, layout saved, tabs All/Chat/System with unread markers (`ChatFilterTab.cs`), hover timestamp, click name to whisper, channel selector, 500-line history. B (`MainDialogs.cs` ~564, `ChatOptionDialog.cs`) has richer per-type filters but only 3-step resize, no tabs/unread/timestamps. [C]

**Items / inventory / loot**
- Named drop-filter presets (New/Rename/Duplicate/Delete; accept-all/reject/special-only; rarity/type/profile filters; search), server-side filtering — `World\DropFilterWindow.cs` (~1.4k) + 5 `DropFilter*GameActionRequest`. **[S]**
- Quick-sell filters (Always/Never/Normal-only), sell tray with select-matching and estimated value (`NpcSellWindow.cs`). **[S]**
- NPC buyback (`NpcBuyBackWindow.cs`) **[S]**; repair tray with "add all equipment" (`NpcRepairWindow.cs`) [S, light]; quick-buy to a saved target quantity (`NpcShopWindow.cs`) **[S]**.
- Persistent item lock (`ItemLockChangeGameActionRequest`); B's `Locked` is a transient UI flag. **[S]**
- Inventory auto-sort planned client-side as a sequence of normal item-move packets (`World\ItemSortPlanner.cs`, `ItemSortButton.cs`). **[C]**
- Sectioned tooltips with stat icons (`ItemTooltip.cs`). Neither client compares against equipped gear.
- Repeat-upgrade runners: gemming / weapon oil until target or stop reason (`World\GemmingRun.cs`, `GemmingPlan.cs`, `GemmingStopReason.cs`, `WeaponOilRun.cs`). [C] if upgrade results carry clear codes.
- Timed item-buff strip with countdown (`World\ActiveItemEffectStrip.cs`). [C]

**Combat feedback**
- Damage numbers (dealt/received colours, miss text) — roughly equal to B's `MirObjects\Damage.cs`; the web client already has floating damage numbers (`f3728d8a`).
- Status-effect strip + tooltips ≈ B's `BuffDialog`.
- Bestiary (`World\BestiaryWindow.cs`, ~2k): knowledge tiers Basic→Master, hunting record, spawns, drops, stats, drop preferences, auto-target mode; `MonsterKnowledgeCache.cs`, `WorldKnowledgeCache.cs`. **[S]**
- PvP log (`PvpLogWindow.cs`). **[S]**

**Social**
- Player right-click menu: Inspect / Whisper / Guild invite / Group / Trade / Add friend (`PlayerContextMenu.cs`). **[C]**
- Confirmation dialogs for invites (`GameConfirmationKind.cs`).
- Mailbox Collect All / Delete All, to inventory or storage, paging, postage (`MailboxWindow.cs`). B: one parcel at a time (`MailDialogs.cs`). **[S]**
- Marketplace (~2k): price + sales history, fee/tax preview, safe-zone gating, delivery by mail. B's `TrustMerchantDialog` has search only. **[S]**

**Settings / input / accessibility**
- 43 rebindable shortcuts with per-binding repeat policy (PressOnce/Repeat/PressAndRelease) — `ClientShortcutCatalog.cs`, `UserInterface\KeyBindingsWindow.cs`. B's `KeyBindSettings` is comparable.
- **Gamepad** support + binding editor + target cycling (`Controller*.cs`, `ControllerBindingEditor.cs`; `CycleControllerTarget` in `Scenes\GameScene.cs` ~843). B: absent. [C]
- Per-category volumes, background-sound toggle, "always show" name toggles, IME composition (`ImeComposition.cs`), RTL layout (`UiFlowDirection`, `InterfaceLanguage.cs`; English/Arabic/Japanese/Chinese/Greek). B has localization but no RTL.

**Login / select**: saved accounts with paging, remember-me, Google/Discord login, in-client create account, change/reset password, email verification, patch notes and rankings from login; duplicate-session takeover confirm; character select from in game (`UserInterface\LoginAction.cs`, `SelectionAction.cs`). B: change-password only, plaintext `AccountID`/`Password` in `Settings.cs`. **[S]** for OAuth/reset/verify.

**GM / support**: GM controls window (Immortal, Invisible, Targetable, MapMonsters, MapPlayers, teleport — `GameMasterControlsWindow.cs`) **[S]**; ticket system with categories, threaded replies, status workflow, GM view, consented log upload (`TicketsWindow.cs`, 9 `Ticket*Request`) **[S]**. B: chat commands + single free-text `ReportDialog`.

---

## 5. Gameplay systems

**G1 Auto-play bot (largest gap; B has only `MapControl.AutoRun`/`AutoHit`).**
- Mode state machine Idle / AutoHunting / AutoFollowing / AutoTravelling (`World\AutomationMode.cs`); one priority chain `CreateAutoPlayAction` (`Scenes\GameScene.cs` ~9946-10345): trap escape → reactive warrior skills → travel blockers → group follow → town work / return to hunting map → route → target selection → Taoist support → harvest → loot → combat.
- Per-class selectors returning immutable decision records: `WizardAutoPlaySelector.cs` (2295 lines; scores each spell/target pair by expected return — damage distributions, time-to-kill, one-cast kill chance, AoE collateral, Fire Wall hazard cells, elemental affinity, Turn/Expel Undead odds, ~247-1100) → `WizardCastDecision`; `WarriorAutoPlaySelector.cs` (1380) → `Warrior{Attack,Area,Charge}Decision`; `AssassinAutoPlaySelector.cs` (1172) → `Assassin{Cast,Control,Trap,Supplemental}Decision`; `TaoistAutoPlaySelector.cs`; `PlayerAutoPlaySelector.cs`; `AutoPlayMagicTrainingRules.cs`.
- Positioning: `ICombatPositionPolicy.TrySelectDesiredCell` implemented by `MeleeCombatPositionPolicy` and `WizardKitingPositionPolicy`; `GroupFollowPositionPolicy` (formation radius 2–4, catch-up at 6, leash at 10); `AutoFollow{Combat,Collection,TravelFallback}Policy`, `HuntingTravelPolicy`.
- Recovery Hunting → Town → Return with revive (`AutoPlayRecovery.cs`, `AutoPlayRecoveryPhase.cs`), `AutoPlayDiagnostics.cs`, settings UI `AutoSettingsWindow.cs`.
- Server support: auto-potion/return/town/quick-buy/target-preference settings are server-synced (`GameScene.cs` ~5711-5966); group member map+XY; damage estimates rely on shared rule classes.
- **This is an officially sanctioned bot — a game-design decision for the user, not just engineering.** If ever built, copy the selector + decision-record + `ICombatPositionPolicy` pattern and keep it out of `GameScene`.

**G2 Pathfinding / travel.** `World\MapPathPlanner.cs` (weighted A*, 10/14 costs, multi-target `FindPathsToTargets` ~219, reachability flood fill ~98, destination resolution ~24). `MapTravelPlanner.cs` (817): cross-map routing through portals with cached routes and cancellation (`TryPlan` ~92); `MapTravelPathOverlay.cs` draws the route; `MapCollisionCache.cs` loads collision for unloaded maps. B: `MirObjects\PathFinder.cs` single-map A* with a node cap, re-planned per step (`GameScene.cs` ~11756-11761, 12159-12194). Needs portal data client-side (Crystal map movements could supply it). **Medium.**

**G3 Targeting.** Controller target cycling (on-screen, Chebyshev order), separate magic/physical/player targets, target frame, server-synced target preferences. B: `MouseObject`/`TargetObject` statics only (`GameScene.cs` ~10852-10958). Low–medium.

**G4 Movement smoothing.** Remote actions queue as `RemotePresentationAction`s; animation runs at 2× when >1 action is backlogged; position corrected before non-move actions (`World\Objects\RemoteMapObject.cs:176-270`); clean 17-state `MapObjectAction`. B's `MapObject.ActionFeed` (~107) is similar without catch-up. Catch-up speed: **low, client-only.**

**G5 Typed presentation catalogs.** `MonsterPresentationCatalog.cs` (2817): profile+image+form → `MonsterPresentation` with per-action `MonsterAnimationLayout` (start, count, stride, ms, loop, directions, reversed) + death/ambient/hit/triggered/shadow; missing entries throw. `MagicEffectCatalog.cs` (3986), `Mir3MagicPresentation*`, `CombatEffectCatalog.cs` (graceful asset-failure recovery). Still hard-coded C# (typed tables, not external data). B: `MonsterObject.cs` (~5.7k, ~1000 `switch (BaseImage)` lines), `PlayerObject.cs` (~5.3k); `Frames.cs` `FrameSet` is part-way there. High, but incremental per monster.

**G6 Gathering / companions / mounts.** Mining and harvesting are first-class actions with shared rules (`Shared/World/MiningRules.cs`, `HarvestRules.cs`, `MiningTable.cs`); companion and mount windows; auto-mount for long journeys (`GameScene.cs` ~554-756). B already has intelligent creatures, mounts and fishing — net gap is small apart from the bestiary and auto-mount.

---

## 6. Architecture notes worth copying

- **`GameActionRequest`**: `Scenes\GameActionRequest.cs` is `abstract record`; ~110 sealed records each wrap one outgoing message. `GameScene.Update` (~4275) returns at most one request per frame from input, menus, auto-play or NPC actions; `MainForm.SendGameAction` (~5539) is the single place that sends. Gives one choke point for throttling/ordering, interception (dismount-before-action, retry of unsent ticket submits) and network-free testing. Its dispatcher is a long `as`-cast chain (a handler map would be better). **For B: a lightweight version — route outgoing packets through one send function — is worth it; a full refactor is not.** B today calls `Network.Enqueue(new C.X{…})` from dialogs and `GameScene` directly.
- Small immutable decision/record types, injected predicates, shared rule classes, diagnostics recorders — good model for any new Crystal client subsystem (and consistent with "no new code in giant files" intent).
- Warning: don't follow its giant `GameScene`/`MainForm` orchestration.

---

## 7. Suggested adoption order for Crystal

1. **Low effort, client-only:** save/restore window positions; inventory auto-sort; player right-click menu; backlog catch-up animation (G4); heartbeat + RTT display, half-open timeouts, jittered reconnect backoff (N4); `AllocationDiagnostics` (R11); atomic settings write (L3).
2. **Medium:** off-thread decode + upload budget (R2); separate lighting/object RTs (R5); per-dialog UI caches (R7); DirectWrite text on D3D11 (R8); chat tabs + timestamps; target cycling + target frame; precomputed hit masks (R4); single outgoing-send choke point.
3. **Medium–high, needs server:** explicit protocol version (N2) + per-packet caps / unknown-id disconnect (N1); sequence-numbered movement (N6); drop and quick-sell filters; mail Collect All; cross-map travel (G2); incident upload (L1).
4. **High:** BC-compressed library format (R1); typed monster/spell catalogs (G5); resolution-independent UI (R9); on-demand asset streaming (P2); auto-play only if the user decides on it (G1).

**Explicitly not adopted:** a frame cap (the user runs the client uncapped on purpose); Ametrine's per-quad draws, per-frame LINQ and per-damage-number textures; MD5 patch hashes (keep SHA-256); fixed-resolution upscaled world layer.

**Rule reminders when picking any of these up:** wire changes follow the AGENTS.md packet rule (4 places + manifest regen + web client port); D3D11-only visuals need a parity waiver per `Client/D3D11-Parity.md`; client hot path stays single-threaded with workers handing off via queues (client.md §3.2).

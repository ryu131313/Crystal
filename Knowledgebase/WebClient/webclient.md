# Crystal Web Client — Agent Reference

> Browser client for the Crystal server: `WebClient/` (TypeScript, WebGL2) behind the `WebRelay/` sidecar.
> Approved 2026-10-12 as roadmap **W** (overrides §7 "Not doing: a web client", see roadmap §8).
> Read AGENTS.md first. A packet change also needs Server §3.5/§4.2 and Client §3.5.

---

## 1. ROLE & IDENTITY

Browser-client engineer: keep the web client **wire-exact** with the desktop client while the server stays **unchanged**. Iterate fast: the user wants light verification (typecheck, codec goldens, one browser run), not long test batteries or mutation passes.

---

## 2. SYSTEM ARCHITECTURE OVERVIEW

```
browser (WebClient, TS/WebGL2) --WS binary, 1 Crystal frame per message--> WebRelay :5690 --TCP--> Crystal server (unchanged)
                               --HTTP GET/Range--> WebRelay /play/*, /js/*   (WebClient/ from the repo, no-cache)
                                                            /data/*, /map/*, /sound/*  (client folder, READ-ONLY)
```

### 2.1 WebRelay (`WebRelay/`, `$(ServerTfm)`, own `bin\`)
| File | Role |
|---|---|
| `Program.cs` | Options (`--upstream`, `--client-data`, `--map-path`, `--version-path`, `--web-root`, `--origin`, `--max-per-ip`, `--max-total`, `--connects-per-minute`, `--listen`; public relay: `--mount`, `--trust-proxy-header`, `--public`, `--accounts-per-hour`, `--characters-per-hour`); `--emit-ts`, `--emit-fixtures` |
| `RelayHost.cs` | `HttpListener` front door: `/ws` (Origin check + `PlayerGuard` block + `ConnectionBudget` before the upgrade), static web root, client data; `--mount` strips the prefix (the mount itself redirects to `<mount>/play/`); `ResolveClientIp` takes the `--trust-proxy-header` address only from a loopback peer |
| `RelaySession.cs` | One tab = one upstream `TcpClient`. Splits the server stream on `[u16 len][i16 id]` and sends one WS message per packet. Browser messages must be exactly one well-formed frame with a known `ClientPacketIds` id, or the session is closed. **Rewrites `C.ClientVersion`** with the MD5 of `--version-path` (the browser cannot supply a hash) |
| `StaticFiles.cs` | Read-only (`FileAccess.Read`, `FileShare.ReadWrite`) GET/HEAD + single Range, ETag, traversal guard |
| `AssetHost.cs` | Load times (2026-10-13, from WoOOLcraft): `/api/manifest` (every Data / Map / Sound file's version = hash of size + mtime, no-cache), `/libindex/<path>.Lib?v=` (a library's header + offset table + frame table in ONE request), `/data/<path>?v=&o=&n=` (a slice as a 200), `/map/..?v=`, `/sound/..?v=`. Every `?v=` is checked against the live file: 412 when it changed, else `immutable` |
| `Compression.cs` | Brotli q5 / gzip of full 200 bodies (maps ~27x: Bichon 12.7 MB -> 0.47 MB; indexes, manifest, page, modules), 256 MB LRU keyed by version; never slices or ranges |
| `PageBuild.cs` | `/js/<12-hex build id>/` immutable module URLs (stale id -> 307), `/play/` rewritten with the id + one `modulepreload` per module (no import waterfall) |
| `ConnectionBudget.cs`, `IpKey.cs` | Lifted from WoOOLcraft: per-IP/total/per-minute caps (IPv6 keyed by /64) |
| `PlayerGuard.cs` | `--public`: the server's per-IP failed-login (5/min), account (3/h) and character (5/h) limits re-applied per player address, from server packet ids + the first payload byte (S.Login 3/4, S.NewAccount 8, S.NewCharacterSuccess); over-quota C.NewAccount / C.NewCharacter are answered by the relay with Result 0 |
| `TsEmitter.cs` | `--emit-ts`: `WebClient/src/protocol/enums.generated.ts` from the compiled Shared enums |
| `Fixtures.cs` | `--emit-fixtures`: codec goldens written by the real Shared `GetPacketBytes` |

Shared is referenced for the enums and the generators only; relayed packets are never decoded.

### 2.2 WebClient (`WebClient/`, `tsc` to unbundled ES modules in `WebClient/js/`, git-ignored)
| Path | Role | Source |
|---|---|---|
| `src/protocol/binaryCodec.ts` | BinaryReader/Writer rules, Crystal framing, Point | lifted from WoOOLcraft |
| `src/protocol/server.ts`, `client.ts` | **Hand-ported** packets (ReadPacket / WritePacket bodies) | new |
| `src/protocol/enums.generated.ts` | Packet ids + shared enums | generated, do not edit |
| `src/assets/manifest.ts` | The asset manifest (`loadManifest` at page start, `assetVersion` / `assetUrl` / `fetchAsset`); a file it does not list is never requested; a 412 shows the reload notice | new (after WoOOLcraft libManifest / versionedSource) |
| `src/game/mapFiles.ts` | Map fetch + parse cache (last 2), last map per character (localStorage `crystal.lastMap.<name>`), `warmCharacter` on the select screen (map + the index of every library its cells use) | new |
| `src/assets/crystalLib.ts` | `.Lib` v2/v3 reader: with a manifest version one `/libindex/` GET + versioned slices, else HEAD + header + offsets + Range; one read per image span (`preload(indices)`: one GET per run of nearby images, split at a 64 KB gap / 4 MB), gzip BGRA → RGBA, v3 frame table | new (port of `MLibrary`/`MImage`) |
| `src/map/mapReader.ts` | Map formats 0–7 and 100 | port of `Client/MirObjects/MapCode.cs` |
| `src/render/*` | WebGL2 backend, sprite batcher, shaders, atlas, colour | lifted from WoOOLcraft (+ `setOpacity`) |
| `src/game/libraries.ts` | `Libraries` paths, async GPU sprite cache (`sprite()` = undefined while loading; draw misses in a high-priority queue, `prefetch()` warm-ups in a low one capped at 8 jobs, a draw promotes a waiting warm-up); `monsterLibPath` (MonsterObject.Load: Dragon, Siege 940+, Gate 950+, Pet 10000+, else Monster/NNN) and `npcLibPath` (Flag 1000+) | port |
| `src/ui/cursors.ts` | Desktop cursors (CMain.LoadMouseCursors: `Data\Cursors\*.CUR` as CSS cursors): Default on the stage (play.css), TextPrompt on text boxes, GameScene.UpdateMouseCursor on the game canvas (monster Attack, NPC NPCTalk, Shift + player AttackRed) | port |
| `src/game/preload.ts` | Art warm-up (from the WoOOLcraft web client): the user's Standing / Walking / Running / Struck / Attack1 / Spell / Die (+ AttackRange2 for an Archer with a bow; Mount* while riding) for all 8 directions and every layer, again on a look / mount / transform change; each monster image's Standing / Walking / Attack1 / Struck / Die, once. Called per frame from game.ts | new |
| `src/game/frames.ts` | `FrameSet.Player/DefaultMonster/DefaultNPC` | port of `Frames.cs` |
| `src/game/objects.ts` | `MapObj`: action feed, frames, `OffSetMove`, `PlayerObject.SetLibraries` | port (subset) |
| `src/game/mapView.ts` | `MapControl.DrawFloor` + `DrawObjects`, player layer order, 40 % user redraw | port (subset) |
| `src/game/items.ts` | ItemInfo table (`S.NewItemInfo`), `GetRealItem`, the user's equipment looks + light | port |
| `src/game/inventory.ts` | User inventory/equipment/gold kept in step with the server (GameScene item handlers) + the MirItemCell requests (move, merge, equip, unequip, use, drop) | port |
| `src/ui/inventoryWindow.ts` | InventoryDialog (I / F9): bag pages, gold, weight; owns the shared **MirItemCell** API (header: `placeCell`, `boundCell`, the per-grid cell builders, `viewCell`, `equipCell`), the pick / move / use / drop logic, the picked item following the mouse; `location` / `size` for dialogs that move it (NPC, market, trade). Builds the BeltDialog (`beltWindow.ts`) | port (desktop look) |
| `src/ui/npcWindow.ts` | NPC dialog (page markup, links, big buttons), goods list + buy, sell/repair/special-repair panel; talk on NPC click (NPCTime gating) | port (desktop look) |
| `src/game/effects.ts` | Effect / Missile / DelayedExplosionEffect (Effect.cs), Direction16, library names | port |
| `src/game/magic.ts` | User magic: Magics, warrior toggles, UseSpell / UseMagic targeting, magic packets | port |
| `src/game/spellVisuals.ts` | Spell visuals: SetAction cast effects, ProcessFrames completion (missiles, hits), attack overlays, ObjectEffect, MapEffect, ObjectProjectile, SpellObject | port (mechanical) |
| `src/ui/magicBar.ts` | CharacterDialog skill page (MagicButton, AssignKeyPanel, C.MagicKey) + the two SkillBarDialogs with cooldown frames | port (desktop look) |
| `src/game/buffs.ts` | Buff visuals (AddBuffEffect / RemoveBuffEffect), MagicShield / ElementalBarrier refresh, poison DrawColour + dots, the user's buff list, BuffImage | port |
| `src/ui/buffBar.ts` | BuffDialog (top right, expand / collapse, blink, hints) | port (desktop look) |
| `src/game/audio.ts` | SoundManager on Web Audio: SoundList.lst + NNN-N / MNN-N names, one-shots, loops, map music, volume (localStorage), unlock on first input | port |
| `src/game/sounds.ts` | Per-object sound choice: MonsterObject.Play*Sound, PlayerObject step (map tile tables) / struck / flinch / attack / die | port (mechanical) |
| `src/game/doors.ts` | Doors: GetDoor / Processdoors / OpenDoor / CheckDoorOpen; door frames in DrawFloor and DrawObjects | port |
| `src/ui/miniMap.ts` | Minimap: MMap image cropped around the user, object dots, map name + location | port (desktop look) |
| `src/ui/bigMap.ts` | Big map (B): BigMapDialog — any map via `C.RequestMapInfo` / `S.NewMapInfo` (image, movement icons, NPC icons), NPC list, search (`C.SearchMap`), teleport-to-NPC (`C.TeleportToNPC`), world map (`S.WorldMapSetup`), object / group dots, click to walk on your own map | port (desktop look) |
| `src/game/pathFinder.ts` | PathFinder A* (+ MapControl.AutoPath stepping in game.ts) | port |
| `src/ui/groupWindow.ts` | Group (P): GroupDialog + group packets (invite, add, remove, switch, member maps / locations) | port (desktop look) |
| `src/ui/tradeWindow.ts` | Trade (T / Trade button): TradeDialog + GuestTradeDialog + the trade handlers; the user's grid, gold and lock live in `Inventory` (Deposit/RetrieveTradeItem, MoveItem/MergeItem with Grid = Trade), cells share the inventory window's pick | port (desktop look) |
| `src/ui/guildWindow.ts` | Guild (G / Guild button): status, notice (view/edit), members by rank with online state, recruit / kick / change rank, invite + create prompts, "Guild chat" (prefills `!~`); Storage page (112 cells sharing the inventory pick, gold deposit / leader-only retrieve; items live in `Inventory.guildStorage`); Ranks page (rename, Add New, the 8 option bits); Buffs page (GuildSkill icons, buy / activate, hint) + the local BuffType.Guild icon; the war prompt | port (desktop look) |
| `src/game/stats.ts` | UserObject.RefreshStats and helpers (level formulas from `S.BaseStatsInfo`, equipment, sockets, sets, skills, buffs, guild buffs, caps), BaseStat.Calculate with float emulation | port (mechanical) |
| `src/game/canWear.ts` | MirItemCell.CanWearItem before `C.EquipItem` (gender, class, RequiredType, hand / wear weight, fishing / mount parts) | port |
| `src/game/scope.ts` | The game scene's lifetime: window listeners take `{ signal: gameSignal() }`; `dispose()` (log out) aborts them | new |
| `src/ui/characterWindow.ts` | Character window (C): CharacterDialog status page from `stats.ts` | port (desktop look) |
| `src/ui/hudExtras.ts` | `SendOutputMessage`, notice, timers, compass, roll (die / yut), `OpenBrowser` (chat link), NPC input box | port (desktop look) |
| `src/ui/messageBox.ts` | MirMessageBox (Prguse 360) / MirInputBox (660) / MirAmountBox (238), non-blocking and modal; promise forms `confirmBox` / `promptBox` / `amountPrompt` | port |
| `src/game/gameInfo.ts` | GameScene MonsterInfoList / NPCInfoList / MapInfoList + world-map setup, deduped `C.Request*Info` | port |
| `src/ui/storageWindow.ts` | StorageDialog (S.NPCStorage): 2 x 80 cells sharing the inventory pick (items in `Inventory.storage`), password unlock / first-time setup / change, rent extra storage (`@ADDSTORAGE`) | port (desktop look) |
| `src/ui/npcServices.ts` | NPC item services: the NPCDropDialog types (refine, check refine, wedding ring, disassemble, downgrade, reset), RefineDialog (`Inventory.refine`), crafting (NPCCraftGoodsDialog + CraftDialog), NPCAwakeDialog, pearl goods | port (desktop look) |
| `src/ui/marketWindow.ts` | TrustMerchantDialog: market / consignment / auction tabs, category filters, search, pages, buy / bid, get back, collect sold, sell now, consigning; the NPC consign panel | port (desktop look) |
| `src/game/itemChanges.ts` | Sockets, mount / fishing slot items, seal, combine, upgrade, the rental item model | port |
| `src/ui/socketWindow.ts`, `src/ui/rentalWindow.ts` | SocketDialog (Ctrl+right-click an item with sockets); item rental between two players (ItemRental* dialogs, HUD Rental button) and the rented-items list | port (desktop look) |
| `src/game/quests.ts`, `src/ui/questWindow.ts` | Quests: QuestInfoList / current / completed / quest items / tracking (localStorage per character), NPCObject quest offers + icon (drawn by `MapView.npcQuestImage`), the NPC quest list (accept / finish with reward choice), details (share, abandon), quest log (Q), tracking panel, shared-quest invitations | port (desktop look) |
| `src/ui/mailWindow.ts`, `src/ui/friendWindow.ts` | Mail list / read letter or parcel / compose letter or parcel (stamp, gold, items, cost); friends + blacklist (F), memos, whisper / mail shortcuts | port (desktop look) |
| `src/ui/relationshipWindow.ts`, `src/ui/inspectWindow.ts`, `src/ui/rankingWindow.ts` | Marriage (L) and mentor windows; inspect (Ctrl+right-click a player; equipment grid instead of the paperdoll); rankings (tabs, paging, online only, inspect from the list) | port (desktop look) |
| `src/game/lighting.ts`, `src/game/lightingEnhanced.ts` | Classic `MapControl.DrawLights` (object, effect and spell-object lights, blindness ramp) and the Enhanced mode (`MapControl.Lighting.cs`, RGBA16F target, needs `EXT_color_buffer_float`, else Classic); `SceneLights` picks by `gameSettings.Lighting`; `@light new` / `@light old`; true grayscale (death: whole scene; paralysis: the body) via the Grayscale shader | port |
| `src/game/damageNumbers.ts` | Floating damage numbers from `S.DamageIndicator` (Settings.DisplayDamage): the WoOOLcraft web client's "new" DamageInfo style (fountain, crit pop, colours, >3 compression) drawn on the 2D overlay; a Critical flags the Hit that follows it; "Miss" as text; at most 10 per object like GameScene.DamageIndicator | lifted from WoOOLcraft (replaces the desktop's rising labels) |
| `src/game/weather.ts` | `MapControl.Weather` / `UpdateWeather` + ParticleEngine (fog, embers, snow, rain, leaves) from `S.MapInformation.WeatherParticles` / `S.MapChanged.Weather`, drawn after the objects | port |
| `src/game/monsterSpecials.ts` | MonsterObject.SetAction / ProcessFrames / action-end per-`Monster` specials (effects, projectiles, sounds; 221 monsters), hooked from objects.ts and game.ts `spellHooks.action` | port (mechanical) |
| `src/game/settings.ts`, `src/game/keybinds.ts` | The desktop [Game] / [Chat] / [Filters] / Lighting options (localStorage `crystal.settings`); the central `KeybindOptions` map (KeyBindSettings defaults, localStorage `crystal.keybinds`): every key handler uses `matches(e, KeybindOptions.X)`, and game.ts keeps matched Ctrl / Alt bindings from the browser | port |
| `src/ui/optionWindow.ts`, `src/ui/helpWindow.ts`, `src/ui/chatOptionWindow.ts`, `src/ui/keyboardLayoutWindow.ts`, `src/ui/clickSounds.ts` | OptionDialog (F12 / O: toggles, volumes, allow observe, lighting), HelpDialog (H: shortcut pages with the live keys + Help.Lib pages), ChatOptionDialog (filters, transparency; `chatVisible` hides chat lines), KeyboardLayoutDialog (U: rebind, conflicts, reset), MirButton click sounds | port (desktop look) |
| `src/game/gameData.ts` | `S.GameDataChanged` (web admin LiveDb push): upserts through the New*Info handlers, CopyMagicInfo, shop rows, big-map invalidation; refresh on `Last` | port |
| `src/game/riding.ts` | Mount / transform / fishing rules: `canFish` (map `fishing` cells), `canRideAttack`, `runDistance` (3 cells riding), the riding / fishing input gates, `isIdleAction`, `transformTypeFromBuffs` | port (pure) |
| `src/ui/mountWindow.ts`, `src/ui/fishingWindow.ts` | MountDialog (J; M = `@ride`; mount picture, loyalty, MountSlot cells); FishingDialog (N; rod + FishingSlot cells) + FishingStatusDialog (chance / progress, Reel, auto-cast) | port (desktop look) |
| `src/game/hero.ts`, `src/game/heroItems.ts` | The user's hero: GameScene.Hero state (spawn state, behaviour, info, base stats, buffs, storage / manage / create) with `onChange()`; the hero's bag, belt, equipment and auto-pot kept in step with the item packets (hero grids, TakeBack / TransferHeroItem) + their requests | port |
| `src/ui/heroPanels.ts`, `src/ui/heroInventoryWindow.ts`, `src/ui/heroCharacterWindow.ts`, `src/ui/heroMagicWindow.ts`, `src/ui/heroManageWindow.ts` | HeroInfoPanel / HeroMenuPanel / HeroBehaviourPanel / hero buffs / Summon button (bottom right); hero bag + belt (7 / 8) + equipment + auto-pot (Ctrl+I, cells share the inventory pick); hero stats (Ctrl+C, `refreshStats` with the hero as wearer); hero skills + key assignment (Ctrl+S; Shift+F1-F8 cast via `C.Magic { ObjectID = hero }`, magic.ts `useHeroSpell`); HeroManageDialog + NewHeroDialog | port (desktop look) |
| `src/game/creatures.ts`, `src/ui/creatureWindow.ts` | Intelligent creatures: UserObject.IntelligentCreatures / CreatureSummoned / PearlCount from UserInformation + the creature packets (`SetItemFilter` ported exactly); IntelligentCreatureDialog (E): list, picture, fullness, expiry, pearls, blackstones, summon / dismiss / release (name check), rename, pickup mode, options (filters, grade); the non-blocking name prompt (`inputBox` in messageBox.ts); X / Alt+A send `C.IntelligentCreaturePickup` at the mouse cell | port (desktop look) |
| `src/ui/gameShopWindow.ts`, `src/ui/guildTerritoryWindow.ts` | GameShopDialog (Y / Shop button: tabs, class / category filters, search, paging, gold or credit buy) and the shop list; GuildTerritoryDialog (server-opened list + paging; purchase disabled) | port (desktop look) |
| `src/game/game.ts` | Packet handling, frame loop, input (walk/run/turn/attack), HUD, chat. Debug: `window.crystalGame.step()` / `.mouse(x, y, buttons)` | new |
| `src/game/session.ts` | WebSocket, `C.ClientVersion` on `S.Connected`, `C.KeepAlive` when idle 4 s | new |
| `src/ui/screens.ts`, `loginScene.ts`, `selectScene.ts`, `newCharacterDialog.ts`, `accountCommon.ts` | LoginScene (+ InputKey / NewAccount / ChangePassword dialogs), SelectScene (animated ChrSel previews), NewCharacterDialog (also reused by the NewHero dialog); `screens.ts` keeps `el` and the `Screens` API | port (desktop look) |
| `src/ui/mir/controls.ts` | The desktop control layer: `MirControl` / `MirImage` / `MirButton` / `MirCheckBox` / `MirLabel` / `MirTextBox` / `MirWindow` as absolutely positioned DOM on the stage, `.Lib` pictures as cached object URLs (`libPicture`, `setPicture`), hints, `closeButton`, `dragY` (scroll bars), `SCREEN_WIDTH` / `SCREEN_HEIGHT` (live), `setUiRoot` / `setUiSound` | port of `Client/MirControls` |
| `src/ui/animatedImage.ts` | `MirAnimated` (MirAnimatedControl: count, delay, loop, fade-in; one shared timer) | port |
| `src/ui/itemLabel.ts`, `src/ui/skillHints.ts` | GameScene.CreateItemLabel (the item tooltip, all sections) and the MagicButton spell hints | port |
| `src/ui/mainDialog.ts`, `chatDialog.ts`, `menuDialog.ts`, `duraPanel.ts`, `hudCommon.ts`, `beltWindow.ts` | MainDialog (bottom bar, orbs, bars, buttons), ChatDialog + ChatControlBar, MenuDialog (log out / exit), DuraStatusDialog, see-through clicks to the map, BeltDialog | port (desktop look) |
| `play/index.html`, `play/main.ts`, `play/play.css` | Page shell (keep it thin) | new |

### 2.3 Threads
| Part | Model |
|---|---|
| WebRelay | Async on pool threads; each session has two pumps, each the only writer of its side (no locks). No game state |
| Browser | One `requestAnimationFrame` loop (update + render), async fetch/decode. A hidden tab is throttled to ~1 fps (the server still gets `C.KeepAlive` from a timer) |

### 2.4 Server constraints (no server change)
- **Version check:** pass `--version-path` = the exe the server's `[General] VersionPath` hashes, or run the server with `CheckVersion=False`.
- **MaxIP:** every relay session reaches the server from the relay's address. A server behind a public relay runs with `[LoadTest] ExemptLoopback=True` (else `MaxIP` caps the **total** number of web players and one player's failed logins / 4th account IP-block every web player); the relay's `--public` mode re-applies the per-IP limits per player (`PlayerGuard`, `--max-per-ip`). Runbook §8.

---

## 3. CRITICAL RULES

### 3.1 Wire
- **ALWAYS** port a packet from its C# `ReadPacket`/`WritePacket` field by field, add a sample to `WebRelay/Fixtures.cs`, run `npm run codec:regen` + `npm run codec:test`. A full port must consume the whole payload (strict test); a deliberate prefix port goes in `PREFIX_ONLY`.
- **ALWAYS** re-run `--emit-ts` after a `Shared/Enums.cs` change (ids are implicit ordinals).
- **ALWAYS** update the TS port in the same commit when a ported packet changes in `Shared/` (this is the "fifth place" of the AGENTS.md packet rule).
- **NEVER** hand-edit `enums.generated.ts`.
- A packet whose C# `Compressed` is true goes in `COMPRESSED` (`server.ts`) and is decoded by `decodeServerPacketAsync` (whole payload inflated; the session keeps packet order while it inflates). Ported so far: `NPCGoods`.
- A malformed client frame closes the relay session; the server would IP-block a desktop client for the same. Keep `client.ts` exact.

### 3.2 Relay
- **NEVER** let the relay write to the client folder or anything under `Build\` (read-only file opens only).
- **NEVER** let the browser choose the version hash, the upstream address or a file outside the configured roots.
- **ALWAYS** keep the Origin check and the connection budget before the WebSocket upgrade.

### 3.3 Client
- **NEVER** block the frame loop on a load: `sprite()` returns undefined while loading and the frame skips it.
- Picking is the desktop's: `MapView.mouseObject` = MapControl.CheckMouseOver (cells ±2 around the mouse, MouseOver = own cell or a visible body pixel within 2 px, from the body as last drawn; hit masks are kept only for body libraries, `libraries.ts HIT_MASK_LIBS`). Every hover / click / spell target goes through game.ts `mouseObject()`, never a cell lookup.
- Hover outline (WoOOLcraft `hoverOutline.ts`, a port addition gated by `HighlightTarget`): red on the hovered live monster, blue on the hovered NPC, blue always on party / guild members (`MapView.outline`, `Painter.drawSilhouette`).
- A failed image read is retried (3 tries) and never cached as absent on the first failure; `rangeGet` checks the response length (the HTTP cache once answered a range with a short 200 and a monster frame vanished for the session).
- Map objects never blink while art streams in: `MapView.heldFrame` draws the current frame once every image of the block (this direction, every layer) is resident, else the block's first frame, else the last frame that object drew (a dead body: its current frame when ready). Warm common animations ahead with `preload.ts` / `prefetch()`, not by drawing them.
- **ALWAYS** mirror desktop behaviour (draw order, offsets, movement gating via `S.UserLocation`) from the C# source; note the source method in a comment.
- Copied WoOOLcraft files keep a header line naming their source.

**Port notes (what differs from the desktop, and how to test):**
- Items: `S.NewItemInfo` is fully decoded (stats, bind flags, tooltip). `CanWearItem` is pre-checked once stats exist (before that the server decides); `BindMode.DontTrade` and NPC `Bind` rules are not pre-checked (the server refuses and the cell unlocks). Shift+left-click splits a stack, Shift+right-click links an item in chat, a picked stack dropped on the map asks for a count, the gold line has a Drop button.
- Section 6 (2026-10-12): client-only parts. Tested live: the new name labels (feet position, NPC names split on "_" with white later words, NameView / DropView), O / H / U open Options / Help / Keyboard, Ctrl+F opens Friends with the browser's find blocked, `@light new` -> "[Lighting: Enhanced]" (this browser has `EXT_color_buffer_float`) and `@light old`, the scene renders in both modes. Not tested live (type checks only): effect lights at night, blindness, death / paralysis grayscale, weather (Bichon has none), per-monster specials, chat filters, click sounds, minimap quest markers / light icon, `GameDataChanged` (codec golden only). Fixed on the way: object and effect lights were never drawn (`view.render` got a one-shot Map iterator that the first draw loop used up); monster `ObjectAttack` Type 1-4 now plays Attack2-5. Deliberate differences: the magic window is on Skills (F11) / Skills2 (S) like the desktop, K is Ranking; the desktop's "don't care" modifiers apply (Ctrl / Alt + a letter also opens its window), so game.ts preventDefaults any matched Ctrl / Alt binding outside text inputs (Ctrl+C with a selection still copies); the death grayscale leaves the 2D overlay (names, chat) in colour; Enhanced uses the effect light colour (no sprite light tints); Options Cancel / grade quirks as in section 5; every button plays ButtonA (desktop MirButton defaults vary). Missing client sound files (e.g. `139-0`) 404 in the console: the desktop builds the same names (monster `BaseSound = image * 10`) and also finds nothing.
- Section 5, intelligent creatures (2026-10-12): monsters now use the desktop's per-range libraries (creatures from `Data/Pet/NN.Lib`, gates, siege, the dragon; `frames.ts monsterFrames` ports the hard-coded FrameSets), creature frame effects (BabySnowMan snow, OlympicFlame flame / smoke), `PlayPetSound` on attack and the PetPickup sound on `S.IntelligentCreaturePickup`. Tested live with WebHero: `@MAKE ChickEgg` / `BlackKittenEgg` + use (level 35 required) -> "Obtained a new creature", the non-blocking name prompt renames it (`C.UpdateIntelligentCreature`), the creature window lists both, Summon -> "Creature BlackKitten has been summoned." and the kitten draws from Pet/06.Lib, a dropped potion is picked up by the pet and returned. Not tested live: dismiss / release, rename after `IntelligentCreatureEnableRename`, the options filters and grade, Alt+A, pearls / blackstones, the creature effects. Notes: summoning silently fails when the cell in front of the player is not valid (the server spawns the pet at `Front`), e.g. facing water. Deliberate differences: Options Cancel restores the filter (the desktop edits the creature's filter live), the grade label follows the ItemGrade value (the desktop's label list swaps Legendary / Mythical), a creature name still draws on one line (the desktop splits on "_"), plain DOM instead of the dialog artwork, no Help button.
- Section 5, heroes (2026-10-12): every hero packet is ported (18 server, 7 client). The hero on the map is a player MapObj with `ownerName` (race stays Player; the desktop's Race = Hero is only checked where it equals Player) drawn with the "{0}'s Hero" label and an always-on health bar for the user's own hero. Tested live with WebHero: `C.NewHero` (sent directly; the desktop opens the dialog from an NPC `[@CREATEHERO]`, which this server's scripts do not have) -> "Hero created successfully." + a SealedHero item -> using it adds and summons the hero (WebSide, Taoist L1); `@SUMMONHERO` / the Summon button toggle it; HeroInformation (bag 10, equipment 14), the info panel, stats (max HP / MP match the server's values), transfer a user item to the hero, equip the hero's WoodenSword + BaseDress (the map look updates), take an item back, behaviour Follow (server echo), the hero bag / character / skills windows. Not tested live (codec + type checks): hero casting and key assignment (a level 1 hero has no magic), auto-pot (locked until `UnlockHeroAutoPot`), the manage dialog / ChangeHero (needs an NPC `[@MANAGEHERO]`), the NewHero dialog itself, hero death / revive, `GainHeroExperience` / `HeroLevelChanged`, hero buffs. Deliberate differences: the panels sit bottom right (the desktop's are in the bottom HUD), hero equipment shows in the hero bag window (the web character windows have no paperdoll), hero toggles are display state only (the server does not forward hero `SpellToggle`), DeleteItem on a hero stack clears the hero's cell (the desktop clears `User.Inventory`, a typo), the hero bag merge sends `GridTo = HeroInventory` (the desktop sends `Inventory`, which the server refuses), Ctrl+C with nothing selected opens the hero window instead of copying. Hero pre-checks not ported (the server refuses): transfer weight, SoulBound, consumable class / level checks. MagicLeveled / MagicDelay / SpellToggle for the hero no longer change the user's spells.
- Section 5, first pass (2026-10-12: mounts, transform, fishing, reincarnation, game shop, guild territories, the `UserInformation` tail): `UserInformation` is now a full port; the intelligent-creature packets are codec-only (UI next pass). Tested live with WebHero: 105 shop items arrive at login, a gold purchase from the shop window (`confirm()`, gold deducted; the server mails the item); mount (`@MAKE BlueTiger` + `Saddle` + `BronzeBell`, equip, the mount window, `@ride`): the rider draws on the tiger, MountWalking then a 3-cell MountRunning accepted by the server, dismount; fishing (`BlueFishingRod` + `FishingHook` + `FishBait` at 342,253 on Bichon facing Right): map click casts, FishingCast / FishingWait frames, ripples and sounds, the status window from `FishingUpdate`; the territory list (`C.GuildTerritoryPage`). Not tested live (codec + type checks): reeling a found fish, auto-cast, transforms (`TransformUpdate`, the user's transform from buffs), reincarnation (needs a Taoist reviving a dead player), credit purchases, removing mount / fishing slot items, `LeatherBridle` (refused: level). Deliberate differences: `C.PurchaseGuildTerritory` is never sent (no server decode case: Client §3.5 Known defect), the purchase button is disabled; the territory page resets to 0 when the server opens the window; no shop Preview (GameShopViewer); the Reel button follows `FoundFish` instead of the FishingWait frame; general `WingEffect` / weapon-effect libraries are not drawn (only transform 19 wings); a `TransformUpdate` changes the body at once. Guild voting has nothing to port: `GuildStatus.Voting` and `GuildMember.hasvoted` are stored, never used by either side. Fishing needs a shore cell whose forward and both side cells are blocked (the desktop `CanWalk` also tries the side directions first).
- Section 4 (2026-10-12): tested live with WebHero + WebAlly: quest offer icon on Merchant_Sarah, accept via the NPC quest list, quest log (WebHero has "Bat Wings" in progress), a letter WebHero -> WebAlly (new-mail flag, read clears it), add friend, inspect (by call: synthetic canvas clicks need a visible browser pane), rankings. Not tested live: quest finish / share / tracking persistence, parcels, memos, marriage, divorce, mentor. Observe is hidden in the inspect window: a granted `C.Observe` ends the requester's own game (StopGame) and the web client has no observer mode. `C.ReportIssue` is not ported (the desktop never sends it). Windows that read `user` in their constructor must be built after the state block in `startGame` (temporal dead zone).
- Section 3 (2026-10-12): NPC services open from the NPC pages like the desktop. Tested live on Bichon wall: storage (InnKeeper_Brittney; the forced first-time password setup, store / move / take back), the market (TrustMerchant_James: open, consign with the 5,000 fee, get back), crafting (CraftsLady_Alice: recipe, Auto Fill, craft) and disassemble (Master_Joffrey). Not tested live (codec + type checks only): refine (no Bichon NPC has the key), wedding ring, awakening / downgrade / reset results, pearl shop, sockets (need an item with sockets), combine, seal, upgrade, rental (two players). Notes: the buy-back list is the existing goods panel (`[@BUYBACK]` sends NPCGoods; the server's `C.BuyItemBack` handler is commented out, so it is not sent); `S.RefineCancel` is handled (the desktop never decodes it: Client §3.5 Known defect). Deliberate differences: the rental fee sends only the amount added (the desktop resends the running total, which the server charges again) and the guest sees the sum of fee packets; map / market prompts use prompt() / confirm(); awakening cells only take items picked from the bag.
- Section 2 (2026-10-12): `MapObj` plays the desktop's per-spell frames and actions (`objects.ts spellAction`: ShoulderDash -> DashL, BackStep -> Jump, FlashDash -> DashAttack, ExplosiveTrap -> Harvest, archer shots -> AttackRange2, Attack1/2/3 frames for warrior skills) with the dash / jump / dash-attack movement offsets; an Archer with a bow walks / runs with the Bow frames, left-click on a monster in range 9 (or Shift+left) shoots (`C.RangeAttack`, arrow at frame 5), Alt+left harvests (`C.Harvest`). The user's skill sends mirror PlayerObject.SetAction (ShoulderDash / BackStep / FlashDash / ExplosiveTrap send `C.Magic` without a target). Object packets (level-up effect, rename, hide / show, sit, harvest, mana %, deco objects from `Deco.Lib`, NPC image change), Concentration / element orbs / Binding Shot effects, InTrapRock (no walking or jumps) are handled. Testing archers on the test server: `AllowCreateArcher=False` there, so create a warrior and `@CHANGECLASS Archer`, then relog; many `@` commands in a row get a 5-minute chat ban. Untested live (codec only): Concentration, SetElemental, Binding Shot, ObjectSitDown, ObjectHide/Show, ObjectDeco, NPCImageUpdate, RemoveDelayedExplosion, InTrapRock, the NPC page MONSTER/NPC/ITEM tooltips, the world map and teleport (this server has the world map off and no teleportable NPC). Deliberate differences in the big map: map search sends 3+ characters (the desktop check is inverted and never sends a usable search), click-to-walk only on your own map, a repeated `NewMapInfo` replaces the cached map.
- Section 1 (2026-10-12): the status line shows HP / MP against the computed max, experience % and the attack mode (Ctrl+H cycles it, Ctrl+A the pet mode). Log out (HUD button) returns to character select on the same connection: `startGame(..., exits).dispose()` stops the loop, aborts the scene's listeners, closes its AudioContext and removes its DOM. The revive prompt opens 1.5 s after `S.Death` (the desktop waits for the death animation). The stats only recompute inside the frame loop, so a hidden (throttled) tab may lag. Testing death needs a non-GM character or a desktop GM: `@KILL` requires `IsGM`, and monsters ignore this test account. Untested live (codec only, they need NPC scripts): `NPCRequestInput`, `SetTimer`/`ExpireTimer`, `SetCompass`, `Roll`, `UpdateNotice`, `OpenBrowser`, `StartGameBanned`/`StartGameDelay`, `ReturnToLogin`.
- Damage numbers (2026-10-13, a port addition from WoOOLcraft): heals green with "+", damage white, crits gold and large, the user's own damage red, "Miss" grey; the desktop's separate "Crit" label is folded into the crit number. Checked with a page harness only (the draw and the timing), not on a live hit.
- Lighting: Classic `DrawLights` by default, Enhanced via `@light new` or Options; death and paralysis draw grayscale (section 6).
- Magic testing needs `[General] TestServer=True` for `@GIVESKILL` / `@LEVEL`. Not ported there: hero magic, mana-penalty stats, the Archer bow checks, ElementsEffect orbs, FlashDash attack-speed scaling.
- Trade: the partner needs `@ALLOWTRADE` once (the options toggle `C.ChangeTrade` is not ported); the cancel message box is a system chat line.
- Guild: the invitee needs `@ALLOWGUILD`; create via an NPC script or `@CREATEGUILD name` on a test server (level 22, 1,000,000 gold and a WoomaHorn by default). Storage works only in a safe zone and the server sends the list once per login. War declaration needs an NPC script (`S.GuildRequestWar`), the leader rank and `[Guilds] WarCost`; the war shows only as server name colours (own guild blue, enemies orange). Buffs: the Guild buff icon shows the summed active stats; the user's stats are not recomputed. Deliberate difference: a `GuildMemberChange` status 7 for the user's own rank updates their rights even when the member list was never loaded (the desktop drops it). Crystal has no guild alliances (only the unused `GuildRankOptions.CanAlterAlliance` bit).
- Desktop UI (2026-10-12/13): every window is laid out like its desktop dialog with the same `.Lib` artwork (see §3.5 and the recipe in §4). The account screens use a 1024 x 768 stage; the game uses the desktop's resolution switch (1024 x 768, 1280 x 800, 1366 x 768, 1920 x 1080), preferring 1920 x 1080 (2026-10-13; `localStorage["crystal.resolution"]` = 1024 / 1280 / 1366 picks another) lowered to the server's `[Permission] MaxResolution` (the desktop defaults to 1024); log-out returns the stage to 1024 x 768, and `play/main.ts` scales the stage to the browser window. Tested live: login, select, new character, the HUD (main bar, chat, minimap, belt, buffs), inventory + item label, character paperdoll, options, help, keyboard, group, friends, rankings, big map, quest log, mail list, game shop, the guild / mount message boxes, an NPC page with its goods list, sell panel and the inventory moving beside it. Built from the desktop sources but not looked at live: trade, guild pages, territories, market, storage, crafting / refining / awakening, mount / fishing / creature dialogs, hero windows, inspect, sockets, rental. Deliberate differences: Options has a web-only "More" panel for settings with no desktop control (lighting, damage numbers, target highlight, ...); Exit logs out to character select (a page cannot close itself); no music or sounds on the account screens (no SoundManager there yet); the shop Preview, the bag delete bin, the inspect Observe button and the territory purchase stay inert; the rented-items list has no opener (the desktop key is unbound too); skill-bar positions are not persisted.

### 3.4 Backlog (not ported yet, surveyed 2026-10-12)

Survey: 116 of 280 `ServerPacketIds` and 48 of 153 `ClientPacketIds` ported; every unported server packet is handled by the desktop. Work top to bottom; strike items here as they land.

**1. Basic play** — done 2026-10-12 (see the port notes above). Left: `UserInformation` tail (intelligent creatures, observe flags), the `DontTrade` / NPC bind pre-checks.

**2. Objects and class moves** — done 2026-10-12 (see the port notes above). Left: Sprint (running 3 cells), assassin sneaking frames, FlashDash attack-speed scaling, the custom scrollbar / button art of the big map.

**3. NPC services** — done 2026-10-12 (see the port notes above). Left: the riding-mount check in CanRemoveItem, swapping between awakening material cells, the separate Mount / Fishing dialogs, the sealed overlay icon, desktop artwork.

**4. Social** — done 2026-10-12 (see the port notes above). Left: observer mode (`UserInformation.Observer`, `GameScene.Observing`), the inspect paperdoll, mail / report buttons that need unported parts, the quest help button.

**5. Large systems** — first pass done 2026-10-12: ~~mounts, fishing, transform, reincarnation, game shop, guild territories, guild voting (nothing to port), the `UserInformation` tail~~ (see the port notes above).
- ~~Heroes~~ done 2026-10-12 (see the port notes). Left: hero casting / auto-pot / manage dialog live checks, the HeroAutoPotPreview icons, hero transfer weight / SoulBound pre-checks.
- ~~Intelligent creatures~~ done 2026-10-12 (see the port notes). Left: multi-line creature names, the remaining per-monster ProcessFrames specials.
- Left over: shop Preview, Sprint, `GameDataChanged` shop pushes, observer mode.

**6. Client-only** — done 2026-10-12 (see the port notes). Left: the multi-line chat bubble, the image health bars (Prguse2), the hero mana bar, Shinsu / burrower Hide-Show body swaps, FrameLoop monsters, the mail / big-map minimap buttons, desktop keys without a web feature (Belt Z, Skillbar R, Autorun D, Cameramode, Screenshot, DropView Tab, Logout / Exit), spell sprite light tints in Enhanced.


### 3.5 UI
- **ALWAYS** build windows with `src/ui/mir/controls.ts` from the desktop dialog: same Library / Index / HoverIndex / PressedIndex / Location / Size / Hint / Sound / font and colour, the draw-time index swaps ported. No plain DOM panels, no native scrollbars (port the desktop scroll buttons / bars), no `confirm()` / `prompt()` / `alert()` (use `messageBox.ts`).
- **ALWAYS** place item cells with the inventoryWindow.ts cell API (`boundCell` + `placeCell`) so pick / move / tooltip stay shared.
- **NEVER** hard-code 1024 / 768: read `SCREEN_WIDTH` / `SCREEN_HEIGHT` (live bindings) where the desktop uses `Settings.ScreenWidth` / `ScreenHeight`.
- Windows dispose on `gameSignal()` abort; a top-level window is a `MirWindow` (Movable / Sort / Modal like the desktop). Cross-window hooks (help pages, mail composers, the quest-items tab) are settable fields wired in game.ts.

---

## 4. CODE STYLE & PATTERNS

### Recipe — port a server packet
1. Read the C# class in `Shared/ServerPackets.cs` (ReadPacket order and widths: `ReadInt32` → `readI32`, `ReadString` → `readString`, `Point` → `readPoint`, `Color` → `readI32` ARGB, `DateTime` → `readI64` of `ToBinary`, `List<T>` → `ReadInt32` count + items).
2. Add a decoder to `decoders` in `src/protocol/server.ts` (key = C# class name; add an `ID_NAME` alias when the `ServerPacketIds` member is spelled differently, e.g. `ObjectNPC` → `ObjectNpc`).
3. Add a sample instance to `ServerSamples()` in `WebRelay/Fixtures.cs`, then `npm run codec:regen` and `npm run codec:test`.
4. Handle it in `src/game/game.ts` `handle()`.

### Recipe — port a window to the desktop look
1. Read the dialog class in `Client/MirScenes/Dialogs/*.cs` (and `MirControls/*.cs` for the control behaviour).
2. Recreate each control with `controls.ts` 1:1 (`groupWindow.ts` is the small reference), English text from `Client/Localization/English.json`.
3. Port `BeforeDraw` / `AfterDraw` / `Process` logic; keep the window's game logic and public API; settable hooks for anything another window owns.
4. `npx tsc --noEmit`, `npm run build`, one browser look.

### Recipe — run it
```
powershell -ExecutionPolicy Bypass -File tools\start-scratch-server.ps1 -Port 17000
powershell -ExecutionPolicy Bypass -File tools\start-webrelay.ps1
```
Open `http://127.0.0.1:5690/play/`. The scratch world's synthetic map has no tile art; real maps need a server whose `Server.MirDB` points at real map files.

---

## 5. DEPLOYMENT & TELEMETRY

- Checks: `powershell -File tools\verify.ps1 -Only WebRelay -NoTests`; `cd WebClient; npm run typecheck; npm run codec:test`.
- Relay console: one line per session open/close with the reason.
- Browser: `window.crystal.session.unported` counts the packet ids received but not ported.
- **Private vs public relay** (alpha, 2026-10-13; operator steps in `Knowledgebase/Runbook.md` §8):
  - Private (dev): `tools\start-webrelay.ps1`, `127.0.0.1:5690/play/`, serves the working tree.
  - Public: `tools\publish-webclient.ps1` snapshots `WebClient\js` + `play` into `WebClient\publish\public` (git-ignored, `public.old` kept); `tools\start-public-webrelay.ps1` serves it on `127.0.0.1:5695` under `--mount /portal/mir2webclient`, `--origin https://worldofwooolcraft.uk`, `--trust-proxy-header CF-Connecting-IP`, `--public`, upstream = the live server's port, builds to `WebRelay\bin\public`, and refuses to start unless the server's `Setup.ini` has `ExemptLoopback=True`.
  - Cloudflare: the WoOOLcraft tunnel routes `^/portal/mir2webclient(/|$)` to `127.0.0.1:5695` (TLS ends at Cloudflare). Every URL the client uses lives under the mount, so it never collides with WoOOLcraft's root `/js/`, `/Data/`, `/Map/` routes.
- **ALWAYS** build relay URLs with `webUrl()` (`src/assets/root.ts`) and keep `play/index.html` / `play.css` references relative: the page runs at `<root>/play/` with root `""` (dev) or the mount (public). An absolute `/data/...` breaks the public site.
- **NEVER** point the public relay at the working tree: players get only what `publish-webclient.ps1` snapshots.
- **ALWAYS** fetch client files through `manifest.ts` (`assetUrl` / `fetchAsset`, `libAsync`, `mapCells`), never a bare `fetch` of `/data/`, `/map/` or `/sound/`: versioned URLs are what make a return visit download nothing.
- **NEVER** serve a `?v=` URL without the live version check (AssetHost): an immutable URL holding patched bytes would stay wrong in browsers and Cloudflare for a year.
- Load-time notes (2026-10-13, local cold run vs before): login screen 3 MB (was 41 MB: the 19-frame ChrSel login door, ~2.5 MB a frame, now streams one frame at a time after frame 0 and is skipped when not all loaded at login), game entry 5.3 MB (was 17.5 MB), a return visit ~0 bytes for art / maps / sounds. JS is emitted without comments.

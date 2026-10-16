# Scene Lab UI — Server Changes Review (2026-10-15)

Technical overview of what the server (and `Shared/`) must change before the Scene Lab HUD + window mocks
(`WebClient/play/hud*.ts`, untracked, mock data only) can be ported into the web client (`WebClient/src/ui`) and the
desktop client (`Client/`). Ideas only — nothing here is approved or scheduled. Rules referenced: `AGENTS.md` (packet
rule, persisted-enum rule, `Envir.Version` rule), `Knowledgebase/Server/server.md` §3.4 / §3.5 / §4.2 / §4.3,
`Knowledgebase/WebClient/webclient.md` §3.

**Classes:** **a** = existing packets/handlers already carry it (client-only work) · **b** = new or changed packets /
server logic, no DB change · **c** = new persisted data (`Envir.Version` bump 118→119, `Save`/ctor pair, persistence
tests) · **d** = cosmetic or client-side settings (`localStorage` / `Mir2Config.ini` / `KeyBinds.ini`).

## Summary

- The protocol already covers the large majority of the mocks. The web client ports every packet except
  `S.UserName` and a few client packets (`ReportIssue`, `RequestUserName`; `ChangeTrade` ported 2026-10-16 phase 2.8; `S.UserSlotsRefresh`,
  `DeleteItem`, `PurchaseGuildTerritory` and `RemoveStoragePassword` ported 2026-10-16), so most windows are **client work over existing packets**.
- The HUD itself (HP/MP, EXP %, level, AC/MAC/DC, bag/weight/gold, buff row with timers, F1-F8 skill row with
  cooldowns, attack-mode and pet-mode slots) is **all class a**. **Belt decision (user, 2026-10-15): keep the 10-slot
  belt; slots 9-0 are the hero's potions.** Those two are the hero's own belt (`HeroInfo.Inventory[0..1]`, client
  `HeroBeltIdx = 2`) used with `C.UseItem { Grid = HeroInventory }` → `HeroObject.UseItem`: **class a**.
  **Slots 7-8 are proper player belt cells (user, 2026-10-16):** the player belt grows from 6 to 8 cells
  (`Inventory[0..7]`, `CharacterInfo.Inventory` 46 → 48 = 8 belt + 40 bag, resized to 88). **The user DB
  (`Server.MirADB`) is wiped when this ships, so no migration and no `Envir.Version` bump:** the inventory is saved
  with its own length (`CharacterInfo.Save` writes `Inventory.Length`; the ctor `Array.Resize`s to it), so only the
  size constants change, in the server and both clients. See "Belt: 8 player cells" below.
- Real server gaps are mostly *missing detail* for things the mocks display: poison timers, group members' HP/MP/
  level/buffs, guild member class/level, guild wars list, ranking guild/online/EXP, select-screen guild/map, login
  tries left, map spawn/safe zones, hero per-spell auto-cast, territory actions outside NPC scripts, game-shop gifts,
  a mentor finder. These are **class b** (~14 new packets, no DB change).
- **Class c** candidates are few and all optional: play time, a profile / status text, mail "Sent" folder, guild
  activity log, market price history, game-shop deal price. One bundled bump to `Version = 119`. (The 8-cell belt
  changes no saved format; it ships with the user-DB wipe.)
- Many mocks hard-code server rules read from `Settings` (Refine, Awakening, Mentor, Marriage, Mail cost, GT prices).
  Propose **one `S.ServerRules` snapshot packet** at login so both clients show real odds/prices instead of drifting
  copies.
- Biggest risk is not the protocol: bulk UI buttons ("Sell junk", "Collect all", "Repair all", storage sort/auto-stack)
  that fire many packets can trip `Settings.MaxPacket` (50 socket reads / 5 s → **24 h IP block**,
  `MirConnection.cs` ~L174-235) — and behind WebRelay every web player shares one IP unless `ExemptLoopback`. Add
  batch packets before shipping any bulk button.

## HUD (hudMock.ts, hudItems.ts, hudDrag.ts, hudExtras.ts, hudMinimap.ts, hudPopup.ts, hudChat.ts)

| Feature | Class | Existing packets / handlers | What to add |
|---|---|---|---|
| HP / MP bars, level, EXP % diamond | a | `S.UserInformation` (Experience/MaxExperience), `S.HealthChanged`, `S.GainExperience`, `S.LevelChanged` | — |
| Stat plates AC / MAC / DC, Bag count, Weight, Gold | a | `S.BaseStatsInfo` + client stat recompute (desktop `RefreshStats`), `S.GainedGold`/`LoseGold` | — |
| Buff row with countdowns | a | `S.AddBuff` / `RemoveBuff` / `PauseBuff` (`ClientBuff.ExpireTime`, `Infinite`, `Paused`, `Stats`) | — |
| Skill row F1-F8, cooldown sweep, "armed" toggle | a | `ClientMagic` (Key, Delay, CastTime, Level, Experience), `S.MagicDelay`, `C.MagicKey`, `C.SpellToggle`/`S.SpellToggle` | — |
| Attack-mode slot (left) / pet-mode slot (right) | a | `C.ChangeAMode`/`S.ChangeAMode`, `C.ChangePMode`/`S.ChangePMode` (incl. `PetMode.FocusMasterTarget`) | — |
| Belt, 8 slots (6 until phase 2.9, 2026-10-16) | a | `Inventory[0..7]` (client `UserObject.BeltIdx = 8`; the desktop belt bar shows 0-5 only), `C.MoveItem`/`UseItem`/`EquipItem` | — |
| Belt slots 9-0 = hero potions (**kept, user 2026-10-15**) | a | Hero belt `HeroInfo.Inventory[0..1]` (client `HeroBeltIdx = 2`, desktop `HeroBeltDialog`); `C.UseItem { Grid = HeroInventory }` → `MirConnection.UseItem` → `HeroObject.UseItem` (refused while the hero is dead); `C.MoveItem`/`TransferHeroItem` to fill them; `S.HeroInformation` sends the hero bag | Client: draw the hero's two belt cells, grey them out when no hero is summoned, accept only potions (the server does not restrict the cell type) |
| Belt slots 7-8 = player belt cells 7-8 (**proper cells, user 2026-10-16**) | b (constants; no saved-format change, user DB wiped) | Belt = `Inventory[0..5]` today; `CharacterInfo.Inventory` 46 → `ResizeInventory` 54…86; `PlayerObject.cs` ~L3682 `(Inventory.Length - 46) / 4`; client `BeltIdx = 6` | Grow the belt to 8 cells: see "Belt: 8 player cells" below **Done 2026-10-16 (phase 2.9).** |
| Belt takes gear for a quick swap | a | Server does not restrict belt cells by type (desktop client does); `C.EquipItem` swaps the worn item back to the source cell | — (client relaxes its own check) |
| Durability strip (amber/red/broken) | a | `S.DuraChanged`, `UserItem.CurrentDura/MaxDura` | — |
| Poison debuff row **with timers** | **b** | `S.Poisoned` carries only the `PoisonType` flags; `S.ObjectPoisoned` likewise | `S.PoisonStatus` (per poison: type, ms left, value) **Done 2026-10-16 (phase 2.4).** |
| Delayed-explosion countdown | b | `S.RemoveDelayedExplosion` only ends it | Covered by `S.PoisonStatus` (or a buff entry) **Done 2026-10-16 (phase 2.4).** |
| Compass, timers, dice | a | `S.SetCompass`, `S.SetTimer`/`ExpireTimer`, `S.Roll` | — |
| Minimap NPCs / exits / group positions | a | `S.MapInformation` / `NewMapInfo` (`ClientMapInfo.NPCs`, `Movements`), `S.GroupMembersMap`, `S.SendMemberLocation` | — |
| Popups / invite toasts with auto-decline bar | a | `S.GroupInvite`, `GuildInvite`, `MarriageRequest`, `MentorRequest`, `TradeRequest` + their replies | Timeout is client-side (send the decline) |
| Chat tabs, filters, mentions, history, fade | d | — | client settings |
| Chat channels / prefixes, whisper, item links, block | a | `C.Chat` prefixes (`PlayerObject.Chat`), `S.NewChatItem`/`C.RequestChatItem`, server already drops whispers from blocked friends (`PlayerObject.cs` ~L2005) | — |
| Announcement ticker | a | `S.UpdateNotice`, `ChatType.Announcement` | — |
| @command autocomplete | d | — | static list (optional b: server sends the commands allowed for the player's GM level) |
| Drag and drop rules, split, drop on world | a | `C.MoveItem`/`MergeItem`/`SplitItem`/`DropItem`, bind flags in `ItemInfo` | — |

## Windows

### Character (hudCharacter.ts)
| Feature | Class | Existing | Add |
|---|---|---|---|
| Paperdoll, 14 equip slots, drag to equip | a | `S.UserInformation.Equipment`, `C.EquipItem`/`RemoveItem` | — |
| Stats slide-out panel | a | Stats recomputed client-side from `S.BaseStatsInfo` + items + buffs | — |

### Inventory / Storage (hudInventory.ts, hudStorage.ts)
| Feature | Class | Existing | Add |
|---|---|---|---|
| 40-cell bag, weight, gold/credit | a | `S.UserInformation`, `S.ResizeInventory` | — |
| Storage 80 + expanded 80, password lock | a | `S.UserStorage`, `S.ResizeStorage`, `C.UnlockStorage`/`S.StorageUnlockResult` | — |
| Search highlight, category chips | d | — | — |
| Sort / auto-stack | b | Only per-cell `C.MoveItem`/`MergeItem` (rate-limit risk) | ~~`C.SortStorage` / `C.StackStorage`~~ **Done 2026-10-16 (phase 2.1)**: one `C.SortItems { Grid (Inventory / Storage), Mode (0 sort / 1 stack then compact) }` (scope addition: covers the bag too, belt excluded; 2 s cooldown; reply `S.UserSlotsRefresh` with both Inventory and Equipment / `S.UserStorage`, arrays copied at send time; one `Report.ItemMoved` per relocated item) |
| "Who stored it", storage activity log | c | Not stored | `UserItem` field — **not recommended** (every item record). Drop, or keep a local log (d) |

### Skills (hudSkills.ts)
| Feature | Class | Existing | Add |
|---|---|---|---|
| Spell list, level pips, training bar, MP, cooldown, range, unlock level | a | `ClientMagic` (Need1-3, Level1-3, Experience, BaseCost, Range, Delay) | — |
| Put on F1-F8 | a | `C.MagicKey` (keys ≤ 16 = player, > 16 = hero) | — |

### Quests (hudQuests.ts)
| Feature | Class | Existing | Add |
|---|---|---|---|
| Active / available / completed, QuestType groups, objectives, rewards incl. choose-one | a | `S.ChangeQuest`, `S.CompleteQuest`, `S.NewQuestInfo`, `C.AcceptQuest`/`FinishQuest(SelectedItemIndex)`/`AbandonQuest`/`ShareQuest` | — |
| Track up to 5 | d | Desktop keeps it in `QuestTracking.ini` | local |
| Find on map | a/d | Giver NPC location in `ClientNPCInfo` | — |

### Guild (hudGuild.ts) and Territories (hudTerritory.ts)
| Feature | Class | Existing | Add |
|---|---|---|---|
| Banner: level, EXP, fund, buff points, member counts, my rank/options | a | `S.GuildStatus` | — |
| Notice board + edit | a | `S.GuildNoticeChange`, `C.EditGuildNotice` | — |
| Members table with class, level, last online | **b** | `S.GuildMemberChange` (`GuildRank` → `GuildMember`: Name, Id, LastLogin, Online). `GuildRank.Save`/`GuildMember` are **also the `.mgd` DB layout** | New `S.GuildMemberDetails` (Name, Class, Level, LastLogin, Online) on request (`C.RequestGuildMemberDetails`); do **not** extend `GuildMember` (DB change) **Done 2026-10-16 (phase 2.5)** |
| Promote / demote / kick, rank option toggles | a | `C.EditGuildMember`, `GuildRankOptions` | — |
| Buffs obtain / activate with countdown | a | `S.GuildBuffList`, `C.GuildBuffUpdate` | — |
| Storage grid, fund deposit / withdraw | a | `S.GuildStorageList`/`ItemChange`/`GoldChange`, `C.GuildStorage*` | — |
| Diplomacy: wars with timers | **b** | Wars only show as name colours; `S.GuildRequestWar` is the NPC-script prompt | `S.GuildWarList` (guild name, ms left) **Done 2026-10-16 (phase 2.5)** |
| Diplomacy: alliances | — | Crystal has none (only the unused `CanAlterAlliance` bit) | New system — out of scope |
| Activity log | c | Not stored | Capped list on `GuildInfo` (`.mgd`, gate on the guild file's version) + `S.GuildLog`; or in-memory since start (b) **Done 2026-10-16 (phase 2.5)** (stored, c) |
| Territory list, lease days, price, owner | a | `C.GuildTerritoryPage`/`S.GuildTerritoryPage` (`ClientGTMap`) | — |
| Buy a for-sale estate | **b** | `C.PurchaseGuildTerritory` has a server handler (`MirConnection.cs` ~L1819) but **no `Shared/Packet.cs` factory case** (Known defect: the server drops it and the rest of the buffer) | ~~Add the factory case~~ **Done 2026-10-16 (phase 2.1)**; TS encoder added, web window Purchase button wired; handler hardened (alive, `[@GUILDTERRITORY]` page + range, no payout/`EndGT` for owner `None`, gold clamp, `NeedSave`) |
| Rent / extend / list for sale / cancel / teleport from the window | b | Today only NPC script commands (`BuyGT`, `ExtendGT`, `GTSale`, `GTCancelSale`, `TeleportGT`) | `C.GuildTerritoryAction(Kind, Index, Price)` reusing the NPC-command code paths (same checks: leader, gold, standing in it) — or keep these on NPC pages **Done 2026-10-16 (phase 2.5)**: shared `PlayerObject.GuildTerritory*` bodies |
| Floor blueprint / amenities | b | Not sent | Optional: part of `S.MapDetails` |

### Group (hudGroup.ts)
| Feature | Class | Existing | Add |
|---|---|---|---|
| Members, leader, invite/kick/pass lead, Allow group | a | `S.AddMember`/`DeleteMember`/`DeleteGroup`/`SwitchGroup`, `C.AddMember`/`DellMember`/`SwitchGroup`/`GroupInvite` | — |
| Member map + location | a | `S.GroupMembersMap`, `S.SendMemberLocation` | — |
| Member class, level, live HP / MP, buffs (cards + party frames) | **b** | `S.ObjectHealth` only for objects in view | `S.GroupMemberStatus` (Name, Class, Level, HP %, MP %, buff types), event-driven + throttled (≤ 1/s per member) **Done 2026-10-16 (phase 2.4).** |
| Loot rule | — | No group loot logic in Crystal | New system — drop from the UI |

### Friends / Mentor / Relationship (hudFriends.ts, hudMentor.ts, hudRelationship.ts)
| Feature | Class | Existing | Add |
|---|---|---|---|
| Friends list, memo, block, online, whisper/group/inspect | a | `S.FriendUpdate` (`ClientFriend`: Index, Name, Memo, Blocked, Online), `C.AddFriend`/`RemoveFriend`/`AddMemo`/`RefreshFriends` | — |
| Requests tab (accept / decline) (**wanted, user 2026-10-16**) | b | Crystal adds friends one-sidedly | **Server done 2026-10-16 (phase 2.7)**: see "Friends: requests and details" below |
| Class badge, level, guild, last seen (**wanted**) | b | Not in `ClientFriend`, but all on `CharacterInfo` | **Done 2026-10-16 (phase 2.7)**: `ClientFriend` extended (wire-only): see below |
| Location | — | — | **Dropped (user 2026-10-16)** |
| Status message, Favourites, Recent tab | — | — | **Dropped (user 2026-10-16)** |
| Mentor: names, levels, online, banked EXP, allow, add, cancel | a | `S.MentorUpdate`, `C.AddMentor`/`MentorReply`/`AllowMentor`/`CancelMentor` | — |
| Mentor term ring (days left), graduation | b | `CharacterInfo.MentorDate` exists, not sent | Add date to a new `S.MentorDetails` (or extend `S.MentorUpdate`) **Done 2026-10-16 (phase 2.5)** |
| Mentor "find board" | b | — | `C.FindMentor(minLevel, maxLevel)` → `S.MentorCandidates` (online players with AllowMentor) **Done 2026-10-16 (phase 2.5)** |
| Marriage: lover, map, married days, allow, request, divorce, recall | a | `S.LoverUpdate`, `C.MarriageRequest`/`Reply`/`ChangeMarriage`/`DivorceRequest`/`Reply`, `@recalllover`, `S.NPCReplaceWedRing` | — |
| Memories timeline, love letters | d/c | — | Mock flavour; drop or derive from mail |

### Mail (hudMail.ts)
| Feature | Class | Existing | Add |
|---|---|---|---|
| Inbox, unread, attachments, locked, read, collect, lock, reply, delete, compose (5 items, gold, stamp), postage | a | `S.ReceiveMail` (`ClientMail` + Items), `C.SendMail`/`ReadMail`/`CollectParcel`/`DeleteMail`/`LockMail`/`MailCost` | — |
| Subject line | d | Only `Message` | first line = subject |
| Collect all / mark all read | b | One packet per mail (rate risk) | ~~`C.CollectAllParcels`, `C.ReadMails(list)`~~ **Done 2026-10-16 (phase 2.1)** (one mail-list refresh per packet; collect stops at the first full bag; a parcel whose gold would pass the wallet limit is skipped, not capped as a single collect does, with one `YouCannotReceiveMoreGold` line; ReadMails cap 100) |
| Sent folder | c | Mail lives only in the recipient's `CharacterInfo.Mail`, `Sender` is a name | `MailInfo.SenderIndex` + sender-side hidden flag, `S.SentMail` list **Done 2026-10-16 (phase 2.6)**: `C.RequestSentMail` / `C.HideSentMail` → `S.SentMail`; an entry shows only while the mail is still in the recipient's box (user) |

### Trade / Rent / Trust merchant / Game shop
| Feature | Class | Existing | Add |
|---|---|---|---|
| Trade: request, 10 slots + gold, confirm, unlock on change | a | `S.TradeRequest`…`TradeCancel`, `C.Trade*`, `C.DepositTradeItem` | — |
| Trade partner card (class, level) | a | `S.ObjectPlayer` of the partner in view (partner must face you) | — |
| Allow trade toggle | a | `C.ChangeTrade` (exists; web client has not ported it) | ~~port it~~ **Done 2026-10-16 (phase 2.8)**: ported; the state is echoed by `S.AllowToggles` (AllowTrade was saved but never sent) |
| Trade chat | a | whisper | — |
| Rental: whole flow | a | `C.ItemRental*`, `S.ItemRental*`, `S.GetRentedItems`, `C.ConfirmItemRental` | — |
| Market browse by category / search / usable by me | a | `C.MarketSearch` (Match, Type, Usermode, Shape range, MarketType), `C.MarketPage`/`Refresh` | — |
| Sort by price / filters (grade, price range) across pages | b | Desktop sorts the current page only (`TrustMerchantDialog` `MarketPriceFilter`) | Add sort/price range to a new `C.MarketSearchEx` (keep `C.MarketSearch` as is) **Done 2026-10-16 (phase 2.6)** |
| Buy now, bid (+5 / 10 / 25 %), sell now, get back, consign / auction | a | `C.MarketBuy(BidPrice)`, `MarketSellNow`, `MarketGetBack`, `ConsignItem(Type)`; `ClientAuction.Price` = current bid for auctions | — |
| Time left, fee, commission, limits | a | `ConsignmentDate` + `Globals` constants | — |
| 14-sale price history sparkline, suggested price | c | Sales are not logged | Capped sales log per ItemIndex in the accounts file (next to `Auctions`) + `C/S.MarketPriceHistory` **Done 2026-10-16 (phases 2.3 + 2.6)** (suggested price = client work) |
| Watchlist | d | — | local |
| Game shop catalogue, stock, credits/gold, buy | a | `S.GameShopInfo`/`GameShopStock`, `C.GameshopBuy`, `S.GainedCredit`/`LoseCredit` | — |
| Gift to a friend | b | `C.GameshopBuy` has no recipient | `C.GameshopGift(GIndex, Quantity, PType, Recipient)` (purchases are already delivered by mail) **Done 2026-10-16 (phase 2.6)**: mutual friends only (user) |
| Deal "was" price | c | `GameShopItem.Deal` is a bool | `GameShopItem.DealPrice` — DB **and** wire (both ctors/`Save` overloads), LiveDb push, web admin reflection |
| Purchase history | c/d | — | local list (d) or capped `CharacterInfo` list (c) |
| Top up | — | external | out of scope |

### NPC services (hudNpc.ts, hudNpcBox.ts, hudMerchant.ts, hudCraft.ts, hudRefine.ts, hudAwaken.ts, hudSocket.ts)
| Feature | Class | Existing | Add |
|---|---|---|---|
| NPC page markup, links, item links, quests tab | a | `S.NPCResponse`, `C.CallNPC`, `S.NewQuestInfo`, `C.AcceptQuest` | — |
| Buy (cart), sell, repair, special repair | a | `S.NPCGoods`/`NPCSell`/`NPCRepair`/`NPCSRepair`, `C.BuyItem`/`SellItem`/`RepairItem`/`SRepairItem` | — |
| Buy back | a | `[@BUYBACK]` → `NPCGoods` list, bought via `C.BuyItem` (`C.BuyItemBack` handler is commented out — leave it) | — |
| Sell junk / repair all | b | one packet per item (rate risk) | ~~`C.SellItems(list)`, `C.RepairItems(list, special)`~~ **Done 2026-10-16 (phase 2.1)** (cap = bag size, repeated ids ignored, per-item `SellItem` / `RepairItem` replies unchanged; `SellItems` refuses (failed `S.SellItem`, one `YouCannotReceiveMoreGold` line) a whole-stack sale whose payout would pass the gold limit, which the single `SellItem` caps silently; `RepairItems` sends each distinct failure line (`CannotRepairItem` / `CannotRepairItemHere`) once per packet, not per item; `SellItems` answers a failed `S.SellItem` per entry when the NPC is out of range). Follow-ups (separate debt tasks): the single `PlayerObject.SellItem` still returns without a reply when the NPC is on the map but out of range (enqueue `p` before that `return`); `S.DeleteItem` has no `Success` flag, so a dead player's refused delete looks done (wire change); server handlers have no automated tests (only the wire layer) |
| Item box: repair, special repair, disassemble, downgrade, reset, wedding ring | a | `C.DisassembleItem`, `DowngradeAwakening`, `ResetAddedItem`, `ReplaceWedRing`, `S.NPCDisassemble`/`Downgrade`/`Reset`/`ReplaceWedRing` | Preview texts duplicate server rules (see `S.ServerRules`) |
| Craft: recipes, chance, gold, tools, batch N | a | `S.NewRecipeInfo` (`ClientRecipeInfo` Gold, Chance, Item, tools, ingredients), `C.CraftItem(Count, Slots)`, `S.CraftItem`, `Stat.CraftRatePercent` | — |
| Refine: deposit, refine, collect, check | a | `C.DepositRefineItem`/`RefineItem`/`CheckRefine`, `S.NPCRefine`/`NPCCollectRefine`/`NPCCheckRefine` | Live chance gauge needs `RefineSystem.ini` values → `S.ServerRules` |
| Awakening: type, materials, awaken, downgrade | a | `C.AwakeningNeedMaterials`/`AwakeningLockedItem`/`Awakening`, `S.Awakening*` | Odds need `AwakeningSystem.ini` → `S.ServerRules` |
| Gems / orbs, sockets | a | `C.CombineItem`/`S.CombineItem`, `S.ItemUpgraded`, socket grid via `C.MoveItem`, `S.ItemSlotSizeChanged` | — |

### Hero / Creature / Mount / Fishing
| Feature | Class | Existing | Add |
|---|---|---|---|
| Hero roster, make active, summon/dismiss/revive | a | `S.ManageHeroes`, `C.ChangeHero`/`S.ChangeHero`, `S.UpdateHeroSpawnState`, `@SUMMONHERO` | — |
| Hero paperdoll, bag (≤ 42 = 2 belt + 40), stats, EXP | a | `S.HeroInformation`, `S.HeroBaseStatsInfo`, `GainHeroExperience`, `HeroLevelChanged`, `C.TransferHeroItem`/`TakeBackHeroItem` | — |
| Behaviour, auto-pot thresholds, auto-pot items | a | `C.SetHeroBehaviour`, `SetAutoPotValue`, `SetAutoPotItem`, `S.UnlockHeroAutoPot` | — |
| Hero skill bar Shift+F1-F8 | a | `C.MagicKey` key > 16 | — |
| Per-spell auto-cast switch for the hero | **b** | `C.SpellToggle` has no hero flag (server toggles the player only) | `C.HeroSpellToggle(Spell, State)` (+ c only if it must persist: `UserMagic` field) **Done 2026-10-16 (phase 2.4).** |
| Creatures: list, summon/dismiss/release, rename, fullness, expiry, pickup mode, filter, grade | a | `S.NewIntelligentCreature`, `UpdateIntelligentCreatureList`, `C.UpdateIntelligentCreature`, `C.IntelligentCreaturePickup` | — |
| Blackstone countdown + **Collect** | a/b | `ClientIntelligentCreature.BlackstoneTime`; the server awards blackstones itself (`PlayerObject.BlackstoneRewardItem`) | Show the countdown only, drop "Collect" |
| Mount: stable, equip, ride, gear slots, loyalty bar | a | `S.MountUpdate`, `C.EquipSlotItem`/`S.EquipSlotItem`, `@ride`; loyalty = mount dura, regained while riding (`HumanObject` loyalty timers) | "Restore" button: verify an item path, else drop |
| Fishing: cast, progress, chance, bite, auto-cast, rod parts | a | `C.FishingCast`/`FishingChangeAutocast`, `S.FishingUpdate`, `C.EquipSlotItem` | — |
| Fish shadows (Finder), catch size / rarity / record, journal | b/c | `S.FishingUpdate` has no fish data; fish have no size | Cosmetic shadows (d); sizes need server logic + `S.FishCaught` (b); records local (d) or `CharacterInfo` (c). Recommend d-only |

### Inspect / Ranking / Tooltip
| Feature | Class | Existing | Add |
|---|---|---|---|
| Inspect: name, guild, rank, lover, paperdoll, equipment | a | `C.Inspect`, `S.PlayerInspect` | — |
| Stats / Compare tabs, gear score | a/d | computed from the sent `UserItem`s; score is a client formula | — |
| Profile tab | c | — | `CharacterInfo.ProfileText` (+ `S.PlayerInspect` would change → prefer `S.PlayerProfile`) **Done 2026-10-16 (phase 2.5)**: `C.SetProfile` + `S.PlayerProfile` |
| Ranking boards, my rank, online-only | a | `C.GetRanking(RankType, RankIndex, OnlineOnly)`, `S.Rankings` | — |
| Guild tag, online dot, EXP bar per row | b | `RankCharacterInfo` sends PlayerId, Name, level, Class only | New `S.RankingsDetail` (or extend `RankCharacterInfo` wire — it is not a DB model) **Done 2026-10-16 (phase 2.5)** |
| Guilds board | b | — | new rank type built from `GuildList` (level, EXP, members) **Done 2026-10-16 (phase 2.5)** as separate `C.GetGuildRanking` / `S.GuildRankings` (RankType meaning unchanged) |
| Movement since yesterday, sparkline, best rank | b/c | — | In-memory daily snapshot (b, lost on restart) or persisted (c). Recommend b |
| Item tooltip: stats, grade, bind, set, story, compare | a | `S.NewItemInfo` (full `ItemInfo`) + `UserItem` | Set bonuses are hard-coded in `HumanObject`; client needs a copy (d, drift risk) |

### Big map / Loading / Login / Select / Game menu / Options / Keybinds / Help
| Feature | Class | Existing | Add |
|---|---|---|---|
| Big map picture, NPCs, exits, teleport to NPC, search | a | `ClientMapInfo` (BigMap, NPCs, Movements), `S.WorldMapSetup`, `C.SearchMap`, `C.TeleportToNPC` | — |
| Monster spawn zones (difficulty tint), safe zones, floors | **b** | Not sent (lab reads a DB export) | `C.RequestMapDetails(MapIndex)` → `S.MapDetails` (respawns: monster index, x, y, spread, count; safe zones), gated by a new setting (info disclosure), split or `Compressed` **Done 2026-10-16 (phase 2.4).** |
| Waypoints, layers | d | — | local |
| "Travel" via exits | — | no server teleport to exits | view-only |
| Loading screen facts (NPCs, spawns, suggested level) | a/b | NPCs/exits a; spawns via `S.MapDetails` | — |
| Login: ID / password, create account wizard, change password, ban reason + expiry | a | `C.Login`/`S.Login(Result)`, `S.LoginBanned(Reason, ExpiryDate)`, `C.NewAccount`, `C.ChangePassword` | — |
| Wrong-password tries left, lock countdown | b | `Envir.Login` counts `WrongPasswordCount` (~L4938) but sends only `Result = 4` | `S.LoginFailed(Result, TriesLeft, LockedUntil)` **Done 2026-10-16 (phase 2.4).** |
| Server card: status, players, ping | b | `[Network] StatusPort` (3100) exists | Relay/endpoint exposure; ping is client-side |
| News, language | d | — | static web content / client setting |
| Select: roster (4), create, delete, class gating, name rules, result codes | a | `S.LoginSuccess` (`SelectInfo`), `C.NewCharacter`/`S.NewCharacter(Result)`, `C.DeleteCharacter`, `C.StartGame` | — |
| Select details: guild, map, play time | b/c | `SelectInfo` = Index, Name, Level, Class, Gender, LastAccess | Guild + map title: new `S.SelectDetails` (b). Play time: `CharacterInfo.PlayTime` (c) **Done 2026-10-16 (phase 2.4).** |
| Game menu: launcher, log out / exit with 10 s combat rule | a | `C.LogOut`/`S.LogOutSuccess`/`LogOutFailed`; combat timer is client-side (desktop) | — |
| Options, key bindings, help | d | — | client settings only (never server-stored) |

## Proposed new packets (append at the END of the enums; all four places + factory case + TS port)

Prefer **new** packets over changing existing ones: existing packets are already ported in `WebClient/src/protocol`
(golden fixtures), and a changed layout breaks a stale desktop client silently.

| Name | Dir | Fields | Observable | Hero-forwarded |
|---|---|---|---|---|
| ~~`ServerRules`~~ **Done 2026-10-16 (phase 2.2)**, id 281: sent after `S.LoginSuccess` and after `S.StartGame`; fields as built (no min sale / GemRate: `GemStatIndependent` instead; adds `AllowCreateAssassin/Archer`, `MapDetailsEnabled`, `FriendRequestsEnabled` (const false until 2.7), `MaxFriends`); new `[Game] MapDetailsEnabled` (true) / `MaxFriends` (100) settings | S→C | Refine (cost, time, base chance, stat reduce, increase, crit chance/increase), Awakening (success rate, hit rate, ChanceMax[], weapon/helmet/armour values), Mentor (level gap, length, damage/EXP boosts, % to mentor), Marriage (min level, cooldown, ring cost, EXP bonus, AllowLoverRecall), Mail (cost per 1k gold, insurance %, free with stamp), GT (BuyGTGold, ExtendGT, GTDays, min sale), GemRate | yes (no secrets) | no |
| ~~`PoisonStatus`~~ **Done 2026-10-16 (phase 2.4)**, id 282: player only, on change (`PoisonStatusDirty` / list count), flushed in `PlayerObject.Process`; `PoisonType` u16, int ms left (≥ 0), int value; delayed explosion = `PoisonType.DelayedExplosion` (64) | S→C | count; per entry: `PoisonType`, ms left, value | yes | no (as built) |
| ~~`GroupMemberStatus`~~ **Done 2026-10-16 (phase 2.4)**, id 283: `GroupStatusDirty` (HP/MP, level, buff add/expire, join), ≤ 1/s per member; full exchange on join; buffs capped at 20 (reader throws above) | S→C | Name, Class, Level, HP %, MP %, buff count + `BuffType[]` | yes | no |
| ~~`GuildMemberDetails`~~ **Done 2026-10-16 (phase 2.5)**, id 288: `byte Part, PartCount` first (≤ 800 rows per part, part 0 replaces, later parts append; worst part ≈ 49.6 KB); offline class/level from `CharacterInfo`; `LastLogin` = `LastLoginDate` online, else `GuildMember.LastLogin`; needs guild + rank, silent 5 s cooldown | S→C | count; Name, Class, Level, LastLogin, Online, RankIndex | no | no |
| ~~`GuildWarList`~~ **Done 2026-10-16 (phase 2.5)**, id 289: full list (≤ 200, empty = no wars), pushed at login (`GuildObject.PlayerLogged`), on join, on `GoToWar` and `GuildAtWar.EndWar` (both guilds); ms left has minute granularity (`TimeRemaining`) | S→C | count; guild name, ms left | no | no |
| ~~`GuildLog`~~ **Done 2026-10-16 (phase 2.5)**, id 290: reply to `C.RequestGuildLog` (id 161; guild + rank, silent 5 s cooldown); ≤ 100 entries oldest first, text cut to 128 chars on the wire; `Kind` = `GuildLogKind` byte (0 Join … 8 Gold). Events logged (texts localised when they happen, `ServerTextKeys.GuildLog*`): join, leave, kick, rank change, notice change, buff bought / activated, war started / ended, territory bought (NPC `BuyGT` + `PurchaseGuildTerritory`) / sold, gold deposit / withdraw. Not logged: disband, item-granted buffs, web admin guild edits | S→C | count; time, kind, text | no | no |
| ~~`RankingsDetail`~~ **Done 2026-10-16 (phase 2.5)**, id 291: sent right after `S.Rankings` in `Envir.GetRanking` for the same rows (≤ 100); EXP as `ushort ExpPerTenThousand` | S→C | RankType, rows: PlayerId, guild name, online, EXP % | yes | no |
| ~~`GuildRankings`~~ **Done 2026-10-16 (phase 2.5)**, id 292: reply to `C.GetGuildRanking { int Page }` (id 162, page clamped, Game or Observer); level then EXP desc, newbie guild excluded, leader = first member of `Ranks[0]`; page size 20; cache rebuilt on request at most every 5 min (`Envir.BuildGuildRanking`) | S→C | Page, Total, rows: Name, Level, EXP ‱, Members, Leader | yes | no |
| ~~`MapDetails`~~ **Done 2026-10-16 (phase 2.4)**, id 285: `Compressed`, plus `Part` / `PartCount` (part 0 replaces, later parts append; ≤ 3000 respawns / 500 safe zones per part); safe zones carry `StartPoint`; `CheckMonsterInfo` before send; cached per `MapInfo` in `Envir.MapDetailsCache` (dropped after every LiveDb apply), 1 s cooldown | S→C | MapIndex, respawns (monster index, x, y, spread, count), safe zones (x, y, size); split / `Compressed` | yes | no |
| ~~`MentorDetails`~~ **Done 2026-10-16 (phase 2.5)**, id 293: after `S.MentorUpdate` in `GetMentor` while `Info.Mentor != 0` and in `LogoutMentor` (to the online partner); `EndsAt` = `MentorDate + MentorLength` days | S→C | partner class, MentorDate, ends at | no | no |
| ~~`MentorCandidates`~~ **Done 2026-10-16 (phase 2.5)**, id 294: ≤ 20; online, `AllowMentor`, no partner / cooldown, same class, level ≥ asker + `MentorLevelGap`, inside [Min, Max] (swapped if reversed); the asker needs no mentor and no cooldown (`MentorSearchNotEligible`) | S→C | count; Name, Class, Level | no | no |
| ~~`PlayerProfile`~~ **Done 2026-10-16 (phase 2.5)**, id 295: always sent right after `S.PlayerInspect` (empty text clears a stale profile) | S→C | Name, Text | yes | no |
| ~~`FriendRequest`~~ **Done 2026-10-16 (phase 2.7)**, id 298: to the target only, with a `FriendRequestReceived` chat line | S→C | Name, Class, Level (who is asking) | no | no |
| ~~`FriendRequestResult`~~ **Done 2026-10-16 (phase 2.7)**, id 299: `byte Result` (class constants Accepted 0, Declined 1, Expired 2, Offline 3, ListFull 4, NotAccepting 5 (added)); to the requester, always with a chat line | S→C | Name, result (accepted / declined / expired / offline / list full) | no | no |
| ~~`SelectDetails`~~ **Done 2026-10-16 (phase 2.4)**, id 287: `uint AccountGold` first; sent after `S.LoginSuccess`, `S.LogOutSuccess` (both paths) and `S.NewCharacterSuccess`; play time in seconds | S→C | per character: Index, guild, map title, play time | no | no |
| ~~`LoginFailed`~~ **Done 2026-10-16 (phase 2.4)**, id 286: sent right after `S.Login` (Result 3 no account, 4 wrong password), never instead; `AccountTriesLeft` / `ConnectionTriesLeft` (-1 = n/a), `LockedUntil` = `DateTime.ToBinary` (0 = not locked; set when the connection limit closes the socket) | S→C | Result, TriesLeft, LockedUntil | no | no |
| ~~`MarketPriceHistory`~~ **Done 2026-10-16 (phase 2.6)**, id 296: `Envir.MarketSales`, ≤ 14 rows (`MaxSales`; writer keeps the newest, reader throws above), oldest first, `Date` = `DateTime.ToBinary`; unknown item history = empty list | S→C | ItemIndex, last N (price, date) | yes | no |
| ~~`SentMail`~~ **Done 2026-10-16 (phase 2.6)**, id 297: ≤ 50 rows newest first (`MaxRows`; reader throws above); row = MailID, RecipientName (≤ 32), Message (≤ 200, surrogate-safe cut), Gold, ItemCount, DateSent (`ToBinary`), Opened, Collected; skips `SenderHidden` mail and deleted recipients; worst case ≈ 36 KB. Built from the in-memory `Envir.SentMailIndex` (server.md §2.6) | S→C | count; ClientMail-like rows | no | no |
| ~~`RequestGuildMemberDetails`~~ **Done 2026-10-16 (phase 2.5)**, id 160 (`RequestGuildLog` 161, `GetGuildRanking` 162: see the S→C rows) | C→S | — | | |
| ~~`FindMentor`~~ **Done 2026-10-16 (phase 2.5)**, id 163: 3 s cooldown with a chat line (`MentorSearchCooldown`) | C→S | min level, max level | | |
| ~~`RequestMapDetails`~~ **Done 2026-10-16 (phase 2.4)**, id 159: `[Game] MapDetailsEnabled`, own map or `BigMap > 0`, 1 s cooldown (armed on rejected requests too) | C→S | MapIndex (validate: own map or a map with BigMap) | | |
| ~~`MarketSearchEx`~~ **Done 2026-10-16 (phase 2.6)**, id 167: `MarketSearch` fields + `byte Sort` (0 none, 1 price asc, 2 price desc, 3 newest, 4 ending soonest = oldest `ConsignmentDate`; > 4 dropped), `uint PriceMin`, `uint PriceMax` (0 = no max), `byte MinGrade`. Same throttle, `MarketKey` / NPC gate and paging as `MarketSearch`; price filter on the shown price (current bid for auctions); sorted once per request (`AuctionID` tie-break); replies `S.NPCMarket` / `S.NPCMarketPage`. The filter stays on the player until the next plain `C.MarketSearch` or a fresh NPC market open | C→S | as `MarketSearch` + sort, price min/max, grade | | |
| ~~`MarketPriceHistory`~~ **Done 2026-10-16 (phase 2.6)**, id 168: alive, `MarketKey` page with its NPC in range, item exists, 1 s cooldown; silent on failure. `RequestSentMail` id 169 (2 s cooldown), `HideSentMail { ulong MailID }` id 170 (0.5 s throttle; mail must be in the sender's own index list; sets `SenderHidden`, replies a fresh `S.SentMail`) | C→S | ItemIndex | | |
| ~~`GameshopGift`~~ **Done 2026-10-16 (phase 2.6)**, id 166: `int GIndex, int Quantity` (1..99, checked before narrowing), `byte PType` (as `GameshopBuy`: 0 credit, 1 gold), `string Recipient`. Checks before charging: recipient exists and is not deleted (`CouldNotFindPlayer`), not yourself (`GameshopGiftSelf`), mutual unblocked friends (`GameshopGiftNotFriend`), recipient mailbox not full (`GameshopGiftMailboxFull`, `SendMail` limit). Then the shared `PlayerObject.GameshopPurchase` (body of `GameshopBuy`, unchanged for it; per-player stock counts the buyer); the mail goes to the recipient with the buyer as sender (`SenderIndex` set, so it shows in the buyer's sent folder) | C→S | GIndex, Quantity, PType, Recipient | | |
| ~~`HeroSpellToggle`~~ **Done 2026-10-16 (phase 2.4)**, id 158: `Spell`, `bool AutoCast`; spawned hero that knows the spell; replies `S.HeroAutoCast` (id 284, full disabled list, `Observable => false`), also sent after `S.HeroInformation` | C→S | Spell, `SpellToggleState` | | |
| ~~`GuildTerritoryAction`~~ **Done 2026-10-16 (phase 2.5)**, id 164: `GuildTerritoryActionKind` byte (Rent 0, Extend 1, ListForSale 2, CancelSale 3, Teleport 4; server drops other values), `int TerritoryIndex` (Rent: < 0 = first free, as the NPC), `uint Price` (> `int.MaxValue` fails like the script's `TryParse`). The NPC commands `BuyGT` / `ExtendGT` / `TeleportGT` / `GTSale` / `GTCancelSale` now call the same `PlayerObject.GuildTerritory*` methods (checks unchanged). The packet also needs alive + guild + rank, and Rent / Extend / Teleport need the `[@GUILDTERRITORY]` NPC page in range, ListForSale / CancelSale a territory map (`MapInfo.GT`); replies are chat lines (the web client re-requests its page) | C→S | Kind (Rent / Extend / ListForSale / CancelSale / Teleport), Index, Price | | |
| ~~`SetProfile`~~ **Done 2026-10-16 (phase 2.5)**, id 165: cut to `MaxChatLength`, control chars removed, trimmed, capped at 120, ignored while chat-banned, 10 s cooldown (`ProfileCooldown`), acked with `ProfileUpdated`. No server word filter exists (chat filtering is client-side), so none is applied | C→S | Text | | |
| ~~`FriendRequestReply`~~ **Done 2026-10-16 (phase 2.7)**, id 171 | C→S | Name, Accept | | |
| ~~(changed) `ClientFriend` in `S.FriendUpdate`~~ **Done 2026-10-16 (phase 2.7)**: `LastSeen` = `LastLogoutDate.ToBinary()` (0 online) | S→C | + Class, Level, Gender, GuildName, LastSeen (no `FriendDetails` packet needed) | | |
| ~~`AllowToggles`~~ **Done 2026-10-16 (phase 2.8)**, id 300: bit `1 << (int)AllowToggle.X` per toggle (new shared enum `AllowToggle : byte`, explicit values Group 0 … FriendRequests 8); sent at login after `S.SwitchGroup` and after every toggle change (`PlayerObject.SendAllowToggles()`) | S→C | ushort Flags | false | |
| ~~`SetAllowToggle`~~ **Done 2026-10-16 (phase 2.8)**, id 172: idempotent explicit setter (the old flip packets stay for the desktop); Group / Observe reuse `SwitchGroup` / `SetAllowObserve`; FriendRequests sets `CharacterInfo.AllowFriendRequests`; Marriage only when unmarried and LoverRecall only when married (the `C.ChangeMarriage` gate); undefined values rejected; always answered by `S.AllowToggles` | C→S | `AllowToggle` byte, bool Value | | |
| ~~`SellItems` / `RepairItems`~~ **Done 2026-10-16 (phase 2.1)**, ids 153 / 154 | C→S | list of (UniqueID, Count) / Special + list of UniqueID; per-item checks and replies of the existing handlers | | |
| ~~`CollectAllParcels` / `ReadMails`~~ **Done 2026-10-16 (phase 2.1)**, ids 155 / 156 | C→S | — / list of MailID | | |
| ~~`SortStorage` / `StackStorage`~~ → `SortItems` **Done 2026-10-16 (phase 2.1)**, id 157 | C→S | Grid (`MirGridType` byte: Inventory / Storage), Mode byte | | |

Also: ~~add the missing `Shared/Packet.cs` `GetClientPacket` case for `C.PurchaseGuildTerritory` (Known defect)~~ **Done 2026-10-16 (phase 2.1)**.
Every list packet must stay ≤ 65 535 bytes (big guilds, big maps, mail): measure and split, or `Compressed => true`
(web client then needs the id in `COMPRESSED`). Every new handler validates its input on the game thread and
allocates nothing per tick; `GroupMemberStatus` must be change-driven and throttled, not built every loop.

## Proposed persisted fields (class c)

**Done 2026-10-16 (phase 2.3): all rows below shipped as one bump `Envir.Version` 118 → 119** (server.md §2.6 / §4.3;
backup note in Runbook §3.1; fixtures `Server.Persistence.Tests/Fixtures/v119`). Data and helpers only: the packets and
events that use them come in phases 2.4-2.6. Also persisted in the same bump: the social toggles `EnableGroupRecall`,
`EnableGuildInvite`, `AllowMarriage`, `AllowLoverRecall`, `AllowMentor` (were session-only `PlayerObject` fields, reset on
login; now properties over `CharacterInfo`) and `AllowFriendRequests` (default true, for the Friends requests below).
**Phase 2.8 (2026-10-16):** all of them, plus `AllowGroup`, `AllowTrade` and `AllowObserve`, are echoed to the client by
`S.AllowToggles` and settable by `C.SetAllowToggle`.

| Field | Model / file | Used by | Notes |
|---|---|---|---|
| `PlayTime` (long, seconds) | `CharacterInfo` (`Server.MirADB`) | Select details | **done**: folded in at `StopGame` and before each periodic accounts save (`PlayerObject.FoldPlayTime`) |
| `ProfileText` (string, capped ~120) | `CharacterInfo` | Friends status, Inspect profile | **done** (data): `CharacterInfo.ProfileTextMaxLength` = 120; write path `C.SetProfile` done 2026-10-16 (phase 2.5) |
| `SenderIndex` + `SenderHidden` | `MailInfo` (in the recipient's `CharacterInfo`) | Mail "Sent" folder | **done** (data): -1 = system/NPC/pre-119 mail; set on player letters and parcels. Sent index + packets: phase 2.6 |
| Guild activity log (capped list) | `GuildInfo` (`Guilds\*.mgd`, ctor already takes `version`) | Guild Log tab | **done** (data): `GuildInfo.Log` (cap 100), `AddLog(GuildLogKind.X, text)` sets `NeedSave`; events: **done 2026-10-16 (phase 2.5)** (list in the `GuildLog` packet row) |
| Market sales log (capped per ItemIndex) | new list saved beside `Auctions` in `SaveAccounts` | Price history | **done**: `Envir.MarketSales` (last 14 per ItemIndex, after the respawn block), `RecordMarketSale` on consignment buy / auction win; packets: phase 2.6 |
| `DealPrice` | `GameShopItem` (`Server.MirDB`, **and** its packet ctor / `Save(writer, packet)`) | Shop deal badge | **done**: `DealGoldPrice` + `DealCreditPrice` (uint, 0 = none), DB + wire (`S.GameShopInfo`, `S.GameDataChanged`; TS + Fixtures); LiveDb read-through; MirDbExport columns |
| Hero auto-cast per spell | `UserMagic` | Hero skills tab | **done** (data): `UserMagic.AutoCast` (default true, players and heroes); hero AI use: **done 2026-10-16 (phase 2.4)**: `HeroObject.CanUseMagic` also requires `AutoCast` (still also `Key > 0`) |

Not recommended: storage "stored by" (per-`UserItem` field), fishing journal / game-shop history / favourites server-side
(keep local), relationship memories.

## Friends: requests and details (user request 2026-10-16)

**Done 2026-10-16 (phase 2.7), server + protocol + web codec** (details: `Knowledgebase/Server/server.md` §2.5 "Friends"). As planned: online-only requests (60 s, cap 10 per target, cleared at logout), accept = both sides, remove = both sides, block instant and one-sided (also drops you from their list), `NotifyFriendsChanged()` at login / logout / level up (10 s throttle) / guild create, join, leave, kick; `Settings.FriendRequestsEnabled` = true. **Deviations:** `FriendRequestResult` is sent (with a chat line) and gained `NotAccepting` (5); `AllowFriendRequests` is honoured; ~~no packet sets it yet~~ **since phase 2.8 it is set by `C.SetAllowToggle { FriendRequests }` and echoed by `S.AllowToggles`** (the desktop has no UI for it); blocking an existing friend now converts the entry to a block (was `PlayerAlreadyAdded`); adding someone who has a live request to you = accept (the desktop's only way to accept); `LastSeen` is `DateTime.ToBinary()`; level-down refresh not covered. **Not done:** desktop Yes/No box and `FriendDialog` columns (user: web only); web Requests tab / UI. **Open risk:** `S.FriendUpdate` has no row cap and can exceed 65 535 bytes with long memos.

The user wants accept / decline requests plus class, level and guild on every friend (everything the mock shows
except location, status message, favourites and recent activity). **Feasible and small; no saved-format change.**

**What the server already has.** `FriendInfo` (`CharacterInfo.cs` ~L712) saves only `Index`, `Blocked`, `Memo` and
resolves `Info` (the friend's `CharacterInfo`) lazily. `CharacterInfo` holds `Level`, `Class`, `Gender`,
`GuildIndex` (→ `Envir.GetGuild(int)`, guild name), `LastLogoutDate`; `Info.Player != null` gives online.
`ClientFriend` (`Shared/Data/ClientData.cs` ~L122) is a **wire-only** model (never saved), so it can grow freely.
`PlayerObject.AddFriend` (~L12486) adds one-sidedly at once, `GetFriends` (~L12545) sends `S.FriendUpdate`, and it
runs only at your own login (~L1202) and after your own edits: **friends' online state goes stale today** (nobody is
told when you log in or out). `Blocked` entries already drop whispers / chat (~L2005, ~L11760).

**1. Details (wire change, no DB change).** Append to `ClientFriend` (ctor + `Save`, same order): `MirClass Class`,
`ushort Level`, `MirGender Gender`, `string GuildName` (empty when guildless), `long LastSeen` (`LastLogoutDate`
ticks / unix; 0 when online). Fill them in `FriendInfo.CreateClientFriend`. Last seen is cheap and the mock has it;
drop it if unwanted. Changing a model inside an existing packet = both clients ship from the same commit (desktop
`FriendDialog` reads `ClientFriend`; web `WebClient/src/protocol/server.ts` ~L321/L586, `WebRelay/Fixtures.cs`,
`npm run codec:regen`/`codec:test`, `Shared.Tests` manifest regen with `MIR2_REGEN_PACKET_MANIFEST=1`).

**2. Keep it fresh (server only).** Push a fresh `S.FriendUpdate` to the online players who list you when you log in,
log out, level up or join / leave a guild. With mutual friendships (below) "who lists me" is your own list
(non-blocked entries); one-sided blocks do not need it. Cost: one list walk per event on the game thread; no LINQ in
per-tick paths (these are events, not ticks). Optionally throttle level-up pushes.

**3. Requests (new packets, in memory).** Modelled on group invites (`PlayerObject.GroupInvitation` ~L173 / ~L9377):
- `C.AddFriend { Name, Blocked = false }` no longer adds: if the target is online, not already a friend and has no
  pending request from you, store it on the target (`PendingFriendRequests`: requester `CharacterInfo` + expiry, cap
  ~10, expire after ~60 s or at logout) and send `S.FriendRequest { Name, Class, Level }`. Offline target →
  "player is not online". **Online-only requests (decided by the user, 2026-10-16):** nothing is persisted.
- `C.FriendRequestReply { Name, Accept }`: accept adds **both** `FriendInfo` entries (mutual) and refreshes both
  lists; decline / expiry tells the requester. Blocked by the target → request silently dropped.
- `C.AddFriend { Blocked = true }` stays immediate and one-sided (block needs no consent); blocking a friend also
  removes you from their list.
- `C.RemoveFriend`: removes **both sides** (decided by the user, 2026-10-16; Crystal today is one-sided). Refresh the
  other player's list if they are online. Removing a *blocked* entry stays one-sided.
- Anti-spam: one pending request per pair, a per-player cooldown, and an "Allow friend requests" toggle could reuse
  the pattern of `AllowGroup` (server-side bool, in memory or saved — saved = Version bump).
- Packets: append `FriendRequest` (S→C), `FriendRequestReply` (C→S), optionally `FriendRequestResult` (S→C) at the
  end of the enums; all four places + factory cases; `Observable => false` on the private replies.
  New ServerTextKeys strings (request sent / declined / expired / not online) in `Server.MirForms/Localization`.

**Optional saved data** (not wanted for now; ships with the planned user-DB wipe but still a format change, so bump
`Envir.Version` and gate the reads): friend-since date on `FriendInfo`, offline / persisted requests, a saved "allow
friend requests" toggle.

**Clients.** Desktop: a `MirMessageBox` Yes/No on `S.FriendRequest` (like the group invite), extra columns in
`FriendDialog`. Web: port the packets, a Requests tab (the mock's) and the new `ClientFriend` fields.

## Belt: 8 player cells (user decision 2026-10-16)

**Done 2026-10-16 (phase 2.9).** Server: `CharacterInfo.Inventory` 48, `ResizeInventory` `== 48` (+8) / `>= 88`, `@ADDINVENTORY`
`- 48`, `HumanObject.BeltSize` 8, auto-fill bands **unchanged** (potions/scrolls 0-3, amulets 4-5; cells 7-8 by hand
only), web admin `SecondBagStart` 48 + slot names, `PlayerInfoForm`. Territory owners need no reset: deleting
`Guilds\*.mgd` clears them (see Runbook §3.2 for the full wipe list). Desktop: compat fix only (`BeltIdx` 8, bag grid
offsets), its belt bar keeps 6 cells and the `KeybindOptions` are unchanged. Web: `BELT_IDX` 8, 8-cell classic belt
bar, keys 1-8 = `Belt1..Belt8`, web-only `HeroBelt1/2` (+`Alt`) on 9 / 0. No packets, no `Envir.Version` bump. The
table below is the original plan.

The HUD belt is 10 slots: keys 1-8 are the player's belt (`Inventory[0..7]`), keys 9-0 the hero's belt (hero
`Inventory[0..1]`, unchanged). Growing the player belt 6 → 8 keeps the bag at 40 + the Bag II steps, so every
"46" becomes "48" and every "86" becomes "88". **No migration:** the user DB is wiped when this ships. The saved format
does not change (the inventory is written with its length), so there is no `Envir.Version` bump either. Without a
wipe, old characters would load with 46 cells and the resize steps would never match; that is why the wipe is the
precondition, so ship the constant change and the wipe together.

| Where | Today | Change |
|---|---|---|
| `Server/MirDatabase/CharacterInfo.cs:75` | `Inventory = new UserItem[46]` | 48 |
| `CharacterInfo.ResizeInventory()` (~L594) | `>= 86` cap, `== 46` first step (+8), then +4 | `>= 88`, `== 48` |
| `PlayerObject.cs` ~L3682 `ADDINVENTORY` | `openLevel = (Inventory.Length - 46) / 4` | `- 48` (keeps the bag-unlock price ladder) |
| `Server/WebAdmin/CharacterLogic.cs:41,202` | `SecondBagStart = 46`, "[0-5] = Belt 1-6, [6-45] = Bag I" | 48; "[0-7] Belt 1-8, [8-47] Bag I"; check `Server.WebAdmin.Tests` |
| `Server.MirForms/Account/PlayerInfoForm.cs` | belt/bag naming of inventory cells | same split |
| `Server/MirObjects/HumanObject.cs:178-182` (player; `HeroObject.cs:25-29` overrides for the hero, unchanged) | `PotionBeltMinimum 0`, `PotionBeltMaximum 4`, `AmuletBeltMinimum 4`, `AmuletBeltMaximum 6`, `BeltSize 6`; used by `AddItem` (~L1604: potions/scrolls auto-fill belt 0-3, amulets 4-5, the rest goes to the bag from `BeltSize`) and `PlayerObject.cs` ~L6482 | `BeltSize 8`; decide the auto-fill bands for the new cells (e.g. potions 0-5, amulets 6-7). No other literal-6 belt loops were found in the server |
| Desktop client `Client/MirObjects/UserObject.cs:38` | `BeltIdx = 6` | 8; `BeltDialog` gets 8 cells (art/layout), keybinds `Belt7`/`Belt8` (+ `Alt`) appended to `KeybindOptions` (looked up by name), `KeyBinds.ini` defaults; `MirItemCell` already uses `BeltIdx` throughout |
| Web client `WebClient/src/game/inventory.ts` | `BELT_IDX` (= 6) | 8; `game.ts:1211` belt keys 1-8; the hero belt keys move from 7 / 8 (`game.ts:379`) to 9 / 0 |
| `S.UserInformation` / `S.ResizeInventory` | send the array length | nothing: the clients size from the packet |

Hero belt (keys 9-0): class a, no server change. `C.UseItem { Grid = HeroInventory }`; client-side only accept
potions there (the server does not restrict the cell type).

Tests: `Server.Persistence.Tests` (synthetic characters built with 46 cells, so update any fixture that assumes 46),
`Server.WebAdmin.Tests` (Bag II naming), `Shared.Tests` only if a `Globals` constant is added (no wire change).

## Desktop client vs web client

- Both decode through the same `Shared` wire layout; the desktop links `Shared.dll`, the web client has a hand port
  (`WebClient/src/protocol/server.ts` / `client.ts`). Each new packet = the four AGENTS.md places + the fifth: TS
  decoder/encoder, a `WebRelay/Fixtures.cs` sample, `npm run codec:regen` + `codec:test`, `WebRelay --emit-ts` after the
  `Shared/Enums.cs` change, and `Shared.Tests` with `MIR2_REGEN_PACKET_MANIFEST=1` (manifest diff committed).
- New server packets are harmless to the desktop until it handles them (the factory decodes them; `GameScene`
  ignores unknown ones), so the web client can lead. **Changed** packets/models (`SelectInfo`, `ClientFriend`,
  `RankCharacterInfo`, `S.Login`, `GameShopItem`) are not: client and server must ship from the same commit.
- `Shared` must stay `net8.0` while the desktop client is on .NET 8 (roadmap C5): no newer APIs in packet code.
- Language: new server messages go in `ServerTextKeys` + `Server.MirForms/Localization/*.json`; new UI strings and
  `BuffType` / `PoisonType` names in the client JSON on each side.
- Web UI rule conflict: webclient.md §3.5 says windows are built with `src/ui/mir/controls.ts` from the desktop dialog
  art, **no plain DOM panels** — the mocks are DOM. Porting them as-is needs an explicit user override of that rule.
- The desktop port is far larger (WinForms + D3D `MirControl` dialogs, `.Lib` art, device-reset safety), but needs
  no protocol beyond what the web client needs.

## Risks

1. **Rate limit → 24 h IP block.** `MaxPacket = 50` socket reads per 5 s. Bulk buttons and fast drag-drop sequences
   can exceed it; behind WebRelay all web players share the relay IP unless `[LoadTest] ExemptLoopback=True`, so one
   player can block everyone. Mitigate with batch packets (above) and client-side pacing.
2. **Information disclosure:** spawn zones, friend locations, guild member levels. Gate with settings / privacy
   flags; validate `RequestMapDetails` input.
3. **Rule drift:** odds and prices copied into two clients go stale when `Settings` change → `S.ServerRules`.
4. **DB format:** any class-c item needs the version gate, a backup, persistence tests; `GuildMember`/`GuildRank` and
   `GameShopItem` are dual DB/wire models — extend by new packet, not by field.
5. **Game-thread cost:** per-member status, rankings detail and map details must be cached / event-driven
   (server.md §3.1: no LINQ / allocation per tick).
6. **Packet size:** 65 535-byte cap for lists (guild members, map details, mail, price history).
7. **Observer mode:** mark private replies `Observable => false`; add observer-visible broadcasts to
   `BroadcastObservePackets` only when intended.

## Suggested order of work

1. **Client-only first (a / d):** port the HUD (10-slot belt: 8 player belt cells + 2 hero belt cells; until the 8-cell
   change lands, keys 7-8 stay disabled) and the windows whose features are class a over the
   existing web protocol; mark b/c features as disabled in the UI. No server change.
2. **Done 2026-10-16 (phase 2.1)** **Safety before bulk buttons:** `SellItems`, `RepairItems`, `CollectAllParcels`, `ReadMails`, `SortStorage` /
   `StackStorage`; fix the `C.PurchaseGuildTerritory` factory case.
3. **Done 2026-10-16 (phase 2.2)** **`S.ServerRules`** (id 281; new `[Game] MapDetailsEnabled` / `MaxFriends` settings, in the web admin catalogue) (unblocks refine / awakening / mentor / marriage / mail / territory panels).
4. **Class b detail packets**, one small commit each (**done 2026-10-16, phase 2.4:** `PoisonStatus`, `GroupMemberStatus`, `SelectDetails`, `LoginFailed`, `HeroSpellToggle`, `MapDetails`; **phase 2.5:** `GuildMemberDetails`, `GuildWarList`, `GuildLog`, `RankingsDetail`, guild board, `MentorDetails`, `FindMentor`, `GuildTerritoryAction`, profiles; **phase 2.6:** `GameshopGift`, `MarketSearchEx`, `MarketPriceHistory`, `SentMail`; **phase 2.8:** `AllowToggles` / `SetAllowToggle` + the `C.ChangeTrade` web port): `PoisonStatus`, `GroupMemberStatus`, `GuildMemberDetails` +
   `GuildWarList`, `RankingsDetail`, `SelectDetails`, `LoginFailed`, `HeroSpellToggle`, `MentorDetails` /
   `FindMentor`, `GameshopGift`, `GuildTerritoryAction`, `MarketSearchEx`, `MapDetails` (behind a setting),
   friend requests + `ClientFriend` details (see "Friends: requests and details").
5. **One class-c bundle (Version 119)** only for the fields the user keeps: `PlayTime`, `ProfileText`, mail
   `SenderIndex`, guild log, market sales log, `DealPrice`.
6. **Done 2026-10-16 (phase 2.9)** **With the user-DB wipe:** the 8-cell player belt (constants only, "Belt: 8 player cells").
7. **Defer / drop:** group loot rules, guild alliances, storage "stored by", fish sizes,
   relationship memories / letters, shop top-up.

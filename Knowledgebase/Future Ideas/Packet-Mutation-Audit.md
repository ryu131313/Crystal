# Packet-mutation audit (precondition for P9 encode-once packets)

> **Status:** Audit only. No code changed. Written 2026-10-10 against `FuckingAround` HEAD `36e422aa`.
> **Question:** If the server serialised each `S.*` packet instance once and reused the bytes for every recipient, would any recipient get different bytes from what it gets today?
> **Verdict:** **Safe to cache bytes per instance**, under the three conditions in the last section. No site has to change first.

## How packets are sent today (the baseline)

- `Connection.Enqueue(p)` puts the **instance** into that connection's `ConcurrentQueue<Packet> _sendList` (`MirConnection.cs` L231–240). If `p.Observable`, the **same instance** is also enqueued on every observer connection.
- Serialisation is late and happens per recipient. `MirConnection.Process()` (L273–284) calls `p.GetPacketBytes()` for each queued packet when that connection is processed. `Packet.GetPacketBytes` (`Shared/Packet.cs` L53–86) runs `WritePacket` into fresh MemoryStreams on every call.
- The game thread processes connections once per ms, iterating backwards (`Envir.cs` ~L2077–2083), **before** the mob-thread barrier and the object loop. So a packet enqueued for connections A and B is serialised for B possibly one pass later, after another world tick, if B was already processed in this pass. Today the bytes **already differ by recipient** whenever the packet points at state that changed in between.
- `GetPacketBytes` callers: `MirConnection.Process` L281, `SendDisconnect` L822, `ClientVersion` L859. All three run on the game thread. Enqueuers on other threads (mob-thread AI broadcasts, legacy `HttpServer /broadcast`, WinForms `GlobalMessageButton`) **only enqueue**. They never serialise.

## Method

1. Scripted scan of `Server/**/*.cs`: track every local assigned `new S.X`/`new ServerPackets.X`, record when it is passed to `Enqueue`/`Broadcast`/`SendServerPacket`/..., and flag later `v.Field =`, `v.Field +=` and `v.List.Add/Clear/Remove/Insert` in the same method. Also flag re-sends of the same variable.
2. Manually traced every hit for reachability: is the mutation on the same control-flow path as the earlier send?
3. Grepped for packets kept beyond a call: fields of packet type, packets in `DelayedAction` params, static or cached packets.
4. Checked every `Broadcast`/fan-out implementation for per-recipient tweaks.
5. Checked `Shared/ServerPackets.cs` `WritePacket` bodies for side effects.
6. Listed packet types whose fields **reference live mutable game objects** (the only way bytes can differ without mutating the packet itself).

## Results

### A. Mutation after send: none reachable

The scan produced 30 "mutate after send" hits. All are **branch-exclusive**: the earlier send is an `Enqueue(p); return;` failure branch, and the mutation (`p.Success = true` and the like) is on the success path that sends once at the end. Representative sites (all `PlayerObject.cs` unless noted):

| Site(s) | Packet | Pattern | Cache-safe? |
|---|---|---|---|
| L5299 `MoveItem` (L5383), `StoreItem` L5465, `TakeBackItem` L5533, `EquipItem` L5696, `RemoveItem` L5137, `RemoveSlotItem` L5289, `EquipSlotItem` L5016, `TakeBackHeroItem` L5745, `TransferHeroItem` L5804 | `S.MoveItem` etc. | `Enqueue(p); return;` on failure, `p.Success = true; Enqueue(p);` on success | Yes |
| `SplitItem` L6443, `MergeItem` L6700, `CombineItem` L7112 (`p.Destroy`) / L7195, `DropItem` L7473, `SellItem` L8096 | same | same | Yes |
| `DepositTradeItem` L10589, `RetrieveTradeItem` L10625, `DepositRefineItem` L12571, `RetrieveRefineItem` L12609, `CollectRefine` L12907, `DepositRentalItem` L14183, `RetrieveRentalItem` L14222 | same | same | Yes |
| `UseItem` L6147 / L6212 / L6350; `HeroObject.cs` L612 | `S.UseItem` | same | Yes (see B) |
| `SearchMap` L7629 / L7638–7639 | `S.SearchMapResult` | each `Enqueue(p); return;` is in its own branch | Yes |
| `NPC/NPCScript.cs` L1447 `Craft` | `S.CraftItem` | same | Yes |

No method mutates a field or list of a packet instance after that instance has been passed to any send path on the same execution path.

### B. Same instance sent twice to the same recipient (harmless)

| Site | Code | Cache impact |
|---|---|---|
| `ConsignItem` L8316–8327 | `p.Success = true; Enqueue(p);` inside the NPC loop, then `Enqueue(p);` again after the loop | Identical bytes both times; caching changes nothing. (Duplicate packet = existing bug.) |
| `UseItem` Strongbox `case 25` L6206–6216 + tail L6350–6351 | `p.Success = true; Enqueue(p);` then falls through to `p.Success = true; Enqueue(p);` | Identical bytes. The fall-through is a real item bug, X1-25. |

### C. Per-recipient tailoring: always a fresh instance (safe)

- `HumanObject.GetInfoEx(viewer)` (`HumanObject.cs` L7062–7072) calls `GetInfo()`, which builds a **new** `S.ObjectPlayer` each call (`PlayerObject.cs` L4776). It then sets `NameColour` for that viewer. Callers enqueue one instance per viewer: `BroadcastInfo` L2383–2399, `PlayerObject.GetObjects*` L1798, `HumanObject` L8452–8453.
- `GetUserInfo(c)` (`PlayerObject.cs` L1653) builds a new `S.UserInformation` per target connection and **copies** the inventory and equipment arrays (shallow copy; the `UserItem` objects are live).
- Monster `GetInfo()` overrides (`packet.Extra = Summoned`, `packet.Image = ...`) modify their own fresh instance before returning it.
- The `p.PType = PoisonType.None` hits (`HumanObject` L7395, `MonsterObject` L2798) modify a `Poison` object, not a packet.

### D. No retained packet instances

- No field, static or cached packet: the grep for fields typed `S.*`/`ServerPackets.*`/`Packet` in `Server/` finds only `MirConnection._retryList`, which holds **client** packets.
- No `S.*` instance travels in `DelayedAction` params.
- So an instance can only be serialised in the `Process()` passes that drain the queues it was put on, and nothing can change it between its first and last serialisation except through live references (E).

### E. `WritePacket` has no side effects

The `Shared/ServerPackets.cs` `WritePacket` bodies assign only locals (`bools |= ...` L510–511; `UserItem T = Equipment[i]` L1832; `UserItem T = TradeItems[i]` L1968). Serialising twice produces the same bytes as serialising once, provided the referenced objects did not change in between.

### F. Fan-out paths (one instance, many recipients)

| Path | File:line | Notes |
|---|---|---|
| `MapObject.Broadcast(p)` | `MapObject.cs` L408 | Same instance to every in-range player |
| `PlayerObject.Broadcast(p)` | `PlayerObject.cs` L1591–1602 | `base.Broadcast` plus the same instance to `Connection.Observers` for `ObjectTurn/Walk/Run/Attack/RangeAttack/Magic/Harvest` |
| `Map.Broadcast(p, loc)` / `BroadcastNPC` | `Map.cs` L2437 / L2450 | Same instance |
| `Envir.Broadcast(p)` | `Envir.cs` L2433 | Same instance to all players |
| `GuildObject.SendServerPacket(p)` | `GuildObject.cs` L602 | Same instance to every online member |
| Observer forwarding | `MirConnection.cs` L237–239 | **Every Observable packet** sent to a watched player is multi-recipient |
| Hero forwarding | `HeroObject.cs` L171–187 | Whitelisted IDs forwarded to `Owner.Enqueue` (one more hop; multi-recipient only through the owner's observers) |

None of these paths changes the instance between recipients.

### G. Packets referencing live mutable state (the only behaviour change)

Fields that hold live objects, so their bytes reflect the object's state **at serialisation time**:

| Packet (field) | Typical sender | Multi-recipient? |
|---|---|---|
| `S.GuildStorageItemChange.Item` (live `GuildStorageItem`) | `PlayerObject.cs` L10196, L10278 via `SendServerPacket` | **Yes**: guild members |
| `S.GuildMemberChange.Ranks` (live `GuildRank`s) | `GuildObject.cs` L349, L376 (`SendServerPacket`); L319, L328 (per-player) | **Yes** |
| `S.GuildBuffList.ActiveBuffs` (live `GuildBuff`s) | `GuildObject.cs` L821, L966, L1011, L1022 | **Yes** |
| `S.TradeItem.TradeItems` (= `Info.Trade`, the live array) | `PlayerObject.TradeItem` ~L10788 | Partner plus partner's observers |
| `S.PlayerInspect.Equipment` (= `player.Equipment`, live array) | `Envir.Inspect` ~L5336 | Inspector plus observers |
| `S.GuildStorageList.Items` (= `MyGuild.StoredItems`, live array) | `PlayerObject.cs` L10290 | Requester plus observers |
| `S.GainedItem/RefreshItem/SplitItem/ItemUpgraded/GainedQuestItem/NewChatItem.Item`, `S.UpdateRentalItem.LoanItem` (live `UserItem`) | many | Observers |
| `S.UserInformation`/`S.UserSlotsRefresh` arrays (copied arrays, live `UserItem` elements) | `GetUserInfo`, `NPCSegment.cs` L4416 | Observers |
| `S.NPCGoods/NPCPearlGoods.List`, `S.ReceiveMail.Mail[*].Items`, `S.Rankings.ListingDetails`, `S.GetRentedItems.RentedItems`, `S.NewItemInfo.Info` (`ItemInfo`) | various | Observers |

**Effect of encode-once here:** every recipient gets the state as it was at the **first** serialisation, instead of each getting its own (later) snapshot. This is not wrong:
- The handler that enqueued the packet has always finished before any `Process()` serialises it. Serialisation never runs mid-handler or concurrently with mob threads.
- Any later change to the referenced object is announced by its own packet (`RefreshItem`, the next `GuildStorageItemChange`, ...).
- Today's per-recipient late snapshots can even be *out of order* relative to those follow-up packets. Encode-once makes all recipients consistent with each other.

It is a **behaviour change**, though, so P9 should cover it with a packet golden test for at least one guild fan-out (`GuildStorageItemChange` with a second member retrieving the item in the same tick) and one observer case.

## Verdict

**Safe to cache bytes per instance.** No site has to change first. Conditions for the P9 implementation:

1. **Encode lazily, on the game thread, at the first `GetPacketBytes()`.** Do not encode inside `Enqueue`. `Enqueue` is called from mob threads (monster `Broadcast`), the legacy `HttpServer` thread and the WinForms UI thread. Eager encoding there would read live `UserItem`/guild objects concurrently with the game thread. A lazily filled `byte[]` on the instance, written only by the game thread, needs no lock.
2. **Accept the snapshot-at-first-encode semantics** for the live-reference packets in table G, and add the two golden tests named above.
3. **Make mutate-after-send impossible in future**, for example a debug-build "frozen" flag set by the first `GetPacketBytes`, with `Debug.Assert` checks or an analyzer rule on property setters. Today's code has no such site (section A), but nothing stops a new one, and it would silently send stale bytes to later recipients.

Optional cleanups (not prerequisites): remove the duplicate `Enqueue(p)` in `ConsignItem` (L8327), and fix the Strongbox fall-through (X1-25), which also double-sends.

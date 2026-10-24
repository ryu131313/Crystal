using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;
using S = ServerPackets;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Character item editor (B2): add / remove / edit items in the inventory, equipment, quest bag and account storage.
    /// Online characters get the same client packets the server itself uses (GainedItem via GainItem, DeleteItem, DeleteQuestItem,
    /// RefreshItem) and their stats are recalculated; offline characters are edited directly (and saved at once while the world is stopped).
    /// </summary>
    public sealed class CharacterItemRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Post("/api/admin/characters/{index:int}/items/add", AddItem);
            router.Post("/api/admin/characters/{index:int}/items/{uid}/remove", RemoveItem);
            router.Post("/api/admin/characters/{index:int}/items/{uid}/edit", EditItem);
        }

        private static ulong RouteUid(WebAdminRequest req)
        {
            if (!CharacterLogic.TryParseUniqueId(req.Route["uid"], out var uid))
                throw new WebAdminException(400, "Invalid item unique id");
            return uid;
        }

        private static ItemInfo ResolveItemInfo(Envir envir, int itemIndex, string itemText)
        {
            if (itemIndex > 0)
                return envir.GetItemInfo(itemIndex);

            if (string.IsNullOrWhiteSpace(itemText)) return null;
            itemText = itemText.Trim();

            if (int.TryParse(itemText, out var idx)) return envir.GetItemInfo(idx);

            var exact = envir.ItemInfoList.FirstOrDefault(i => i != null && string.Equals(i.Name, itemText, StringComparison.OrdinalIgnoreCase));
            return exact ?? envir.GetItemInfo(itemText);
        }

        private static UserItem CreateItem(Envir envir, ItemInfo info, ushort count)
        {
            var item = envir.CreateFreshItem(info);
            item.Count = count;
            item.GMMade = true;
            if (!info.NeedIdentify) item.Identified = true;
            return item;
        }

        private static UserItem[] ContainerArray(CharacterInfo ci, string container)
        {
            switch (container)
            {
                case CharacterLogic.ContainerEquipment:
                    return ci.Equipment;
                case CharacterLogic.ContainerInventory:
                    return ci.Inventory;
                case CharacterLogic.ContainerQuestInventory:
                    return ci.QuestInventory;
                case CharacterLogic.ContainerStorage:
                    return ci.AccountInfo?.Storage;
                default:
                    return null;
            }
        }

        private static CharacterItemLocation Locate(CharacterInfo ci, ulong uid)
        {
            var loc = CharacterLogic.FindItem(ci.Equipment, ci.Inventory, ci.QuestInventory, ci.AccountInfo?.Storage, uid);
            if (loc == null) throw new WebAdminException(404, "Item " + uid + " was not found on " + ci.Name + " (equipment, inventory, quest bag or storage).");
            return loc;
        }

        // ------------------------------------------------------------------ add

        private static async Task AddItem(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = CharacterRouteUtil.RequireObject(await req.ReadJsonAsync());
            var reason = CharacterRouteUtil.Reason(body);

            var itemIndex = (int)(CharacterRouteUtil.OptionalLong(body, "itemIndex", 0, int.MaxValue) ?? 0);
            var itemText = CharacterRouteUtil.OptionalString(body, "item");
            if (itemIndex <= 0 && string.IsNullOrWhiteSpace(itemText)) throw new WebAdminException(400, "itemIndex (or item name) is required");

            var count = (int)(CharacterRouteUtil.OptionalLong(body, "count", 1, int.MaxValue) ?? 1);
            var container = CharacterLogic.NormalizeAddContainer(CharacterRouteUtil.OptionalString(body, "container"));
            if (container == null) throw new WebAdminException(422, "container must be inventory or storage");

            var result = await AccountRoutes.AuditIfTimedOutAsync(req, "character.item-add", "character #" + index, new { index, itemIndex, item = itemText, count, container, reason }, () => WebAdminGame.RunOrInlineAsync(() =>
            {
                var envir = Envir.Main;
                var ci = CharacterRouteUtil.Find(index);
                var info = ResolveItemInfo(envir, itemIndex, itemText);
                if (info == null) throw new WebAdminException(404, "Item " + (itemIndex > 0 ? "#" + itemIndex : itemText) + " does not exist in the running item database.");

                var stackSize = Math.Max((ushort)1, info.StackSize);
                // O(1) cap before splitting: a huge count must never allocate count/stackSize entries on the game thread.
                if (!CharacterLogic.WithinAddLimit(count, stackSize))
                    throw new WebAdminException(422, "count is too large: at most " + CharacterLogic.MaxStacksPerAdd + " stacks of " + stackSize + " per request.");
                var stacks = CharacterLogic.SplitStacks(count, stackSize);
                if (stacks.Count > CharacterLogic.MaxStacksPerAdd)
                    throw new WebAdminException(422, "count is too large: at most " + CharacterLogic.MaxStacksPerAdd + " stacks of " + stackSize + " per request.");

                var player = ci.Player;
                var added = new List<UserItem>();
                var addedCount = 0;
                var merged = 0; // new stacks that were merged into existing ones (their fresh UniqueID exists nowhere)
                var note = string.Empty;

                if (container == CharacterLogic.ContainerStorage)
                {
                    var acc = ci.AccountInfo ?? throw new WebAdminException(409, ci.Name + " has no account storage.");
                    var usable = CharacterLogic.UsableStorageSize(acc.Storage?.Length ?? 0,
                        CharacterLogic.IsExpandedStorageActive(acc.HasExpandedStorage, acc.ExpandedStorageExpiryDate, envir.Now));
                    var free = CharacterLogic.CountFree(acc.Storage, 0, usable);
                    if (free < stacks.Count)
                        throw new WebAdminException(409, "Storage of " + acc.AccountID + " has " + free + " free slot(s); " + stacks.Count + " needed. Use GM giveitem or mail instead.");

                    foreach (var n in stacks)
                    {
                        var slot = CharacterLogic.FindFreeSlot(acc.Storage, 0, usable);
                        var item = CreateItem(envir, info, n);
                        acc.Storage[slot] = item;
                        added.Add(item);
                        addedCount += n;
                    }

                    if (player != null) note = " (shown the next time the storage is opened)";
                }
                else if (player != null)
                {
                    // Same path as @MAKE: CanGainItem + GainItem (client gets GainedItem, bag weight is refreshed).
                    foreach (var n in stacks)
                    {
                        var item = CreateItem(envir, info, n);
                        if (!player.CanGainItem(item)) break;
                        player.GainItem(item);
                        if (Array.IndexOf(ci.Inventory, item) >= 0) added.Add(item); // else merged into existing stacks (UID discarded)
                        else merged++;
                        addedCount += n;
                    }

                    if (addedCount == 0)
                        throw new WebAdminException(409, ci.Name + "'s bag is full: no free slot. Use GM giveitem or mail instead.");
                    if (addedCount < count) note = " (bag full: " + (count - addedCount) + " not added)";
                }
                else
                {
                    if (CharacterLogic.InventoryCapacity(ci.Inventory, info) < count)
                        throw new WebAdminException(409, "No free slot in " + ci.Name + "'s bag for " + count + " x " + info.FriendlyName + ". Use GM giveitem or mail instead.");

                    foreach (var n in stacks)
                    {
                        var item = CreateItem(envir, info, n);
                        if (!CharacterLogic.TryPlaceInInventory(ci.Inventory, item))
                            throw new WebAdminException(409, "No free slot in " + ci.Name + "'s bag. Use GM giveitem or mail instead.");
                        if (Array.IndexOf(ci.Inventory, item) >= 0) added.Add(item); // else fully merged into existing stacks
                        else merged++;
                        addedCount += n;
                    }
                }

                CharacterRouteUtil.PersistIfStopped();

                var uids = added.Select(i => i.UniqueID.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
                return new CharacterMutation
                {
                    Target = CharacterRouteUtil.Label(ci),
                    Message = "Added " + addedCount + " x " + info.FriendlyName + " to " + ci.Name + "'s " + (container == CharacterLogic.ContainerStorage ? "storage" : "bag") + note + ".",
                    Details = new
                    {
                        itemIndex = info.Index,
                        item = info.Name,
                        requested = count,
                        added = addedCount,
                        container,
                        uniqueIds = uids,
                        mergedIntoExistingStacks = merged,
                        online = player != null,
                    },
                    Data = new { added = addedCount, uniqueIds = uids },
                };
            }));

            WebAdminAudit.Record(req, "character.item-add", result.Target, result.Details, reason);
            await req.Ok(result.Data, result.Message);
        }

        // ------------------------------------------------------------------ remove

        private static async Task RemoveItem(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var uid = RouteUid(req);
            var body = await req.ReadJsonAsync();
            var reason = body.ValueKind == JsonValueKind.Object ? CharacterRouteUtil.Reason(body) : null;

            var result = await AccountRoutes.AuditIfTimedOutAsync(req, "character.item-remove", "character #" + index, new { index, uid, reason }, () => WebAdminGame.RunOrInlineAsync(() =>
            {
                var ci = CharacterRouteUtil.Find(index);
                var loc = Locate(ci, uid);
                var item = loc.Item;
                var label = CharacterProjection.ItemLabel(item);
                var player = ci.Player;
                var array = ContainerArray(ci, loc.Container);

                array[loc.Slot] = null;

                if (player != null)
                {
                    switch (loc.Container)
                    {
                        case CharacterLogic.ContainerInventory:
                        case CharacterLogic.ContainerEquipment:
                            player.Enqueue(new S.DeleteItem { UniqueID = item.UniqueID, Count = item.Count });
                            player.RefreshStats();
                            break;
                        case CharacterLogic.ContainerQuestInventory:
                            player.Enqueue(new S.DeleteQuestItem { UniqueID = item.UniqueID, Count = item.Count });
                            break;
                    }
                }

                CharacterRouteUtil.PersistIfStopped();

                return new CharacterMutation
                {
                    Target = CharacterRouteUtil.Label(ci),
                    Message = "Removed " + label + " from " + ci.Name + " (" + CharacterLogic.SlotName(loc.Container, loc.Slot) + ").",
                    Details = new
                    {
                        uniqueId = item.UniqueID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        itemIndex = item.ItemIndex,
                        item = item.Info?.Name,
                        count = (int)item.Count,
                        container = loc.Container,
                        slot = loc.Slot,
                        currentDura = (int)item.CurrentDura,
                        maxDura = (int)item.MaxDura,
                        addedStats = CharacterLogic.StatsToDictionary(item.AddedStats),
                        online = player != null,
                    },
                };
            }));

            WebAdminAudit.Record(req, "character.item-remove", result.Target, result.Details, reason);
            await req.Ok(null, result.Message);
        }

        // ------------------------------------------------------------------ edit

        private static async Task EditItem(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var uid = RouteUid(req);
            var body = CharacterRouteUtil.RequireObject(await req.ReadJsonAsync());
            var reason = CharacterRouteUtil.Reason(body);

            var count = CharacterRouteUtil.OptionalLong(body, "count", 1, ushort.MaxValue);
            var currentDura = CharacterRouteUtil.OptionalLong(body, "currentDura", 0, ushort.MaxValue);
            var maxDura = CharacterRouteUtil.OptionalLong(body, "maxDura", 0, ushort.MaxValue);

            Dictionary<Stat, int> stats = null;
            if (CharacterRouteUtil.Has(body, "addedStats", out var statsElement))
            {
                if (!CharacterLogic.TryParseStats(statsElement, out stats, out var error)) throw new WebAdminException(422, error);
            }

            if (count == null && currentDura == null && maxDura == null && stats == null)
                throw new WebAdminException(400, "Nothing to update");

            var result = await AccountRoutes.AuditIfTimedOutAsync(req, "character.item-edit", "character #" + index, new { index, uid, count, currentDura, maxDura, reason }, () => WebAdminGame.RunOrInlineAsync(() =>
            {
                var ci = CharacterRouteUtil.Find(index);
                var loc = Locate(ci, uid);
                var item = loc.Item;
                var player = ci.Player;
                var stackSize = Math.Max((ushort)1, item.Info?.StackSize ?? (ushort)1);

                if (count.HasValue && count.Value > stackSize)
                    throw new WebAdminException(422, "count must be between 1 and " + stackSize + " for " + (item.Info?.FriendlyName ?? "this item") + ".");

                var newMax = maxDura.HasValue ? (ushort)maxDura.Value : item.MaxDura;
                var newCur = currentDura.HasValue ? (ushort)currentDura.Value : item.CurrentDura;
                if (newCur > newMax)
                {
                    if (currentDura.HasValue) throw new WebAdminException(422, "currentDura cannot exceed maxDura (" + newMax + ").");
                    newCur = newMax; // lowering maxDura clamps the current durability
                }

                var before = new
                {
                    count = (int)item.Count,
                    currentDura = (int)item.CurrentDura,
                    maxDura = (int)item.MaxDura,
                    addedStats = CharacterLogic.StatsToDictionary(item.AddedStats),
                };

                var oldCount = item.Count;
                if (count.HasValue) item.Count = (ushort)count.Value;
                item.MaxDura = newMax;
                item.CurrentDura = newCur;

                if (stats != null)
                {
                    var fresh = new Stats();
                    foreach (var pair in stats)
                        fresh[pair.Key] = pair.Value; // 0 removes
                    item.AddedStats = fresh;
                }

                var after = new
                {
                    count = (int)item.Count,
                    currentDura = (int)item.CurrentDura,
                    maxDura = (int)item.MaxDura,
                    addedStats = CharacterLogic.StatsToDictionary(item.AddedStats),
                };

                if (player != null)
                {
                    switch (loc.Container)
                    {
                        case CharacterLogic.ContainerInventory:
                        case CharacterLogic.ContainerEquipment:
                            // The client replaces its copy by UniqueID (count, durability and added stats).
                            player.Enqueue(new S.RefreshItem { Item = item });
                            player.RefreshStats();
                            break;
                        case CharacterLogic.ContainerQuestInventory:
                            player.Enqueue(new S.DeleteQuestItem { UniqueID = item.UniqueID, Count = oldCount });
                            player.Enqueue(new S.GainedQuestItem { Item = item.Clone() });
                            break;
                    }
                }

                CharacterRouteUtil.PersistIfStopped();

                return new CharacterMutation
                {
                    Target = CharacterRouteUtil.Label(ci),
                    Message = "Updated " + CharacterProjection.ItemLabel(item) + " on " + ci.Name + ".",
                    Details = new
                    {
                        uniqueId = item.UniqueID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        itemIndex = item.ItemIndex,
                        item = item.Info?.Name,
                        container = loc.Container,
                        slot = loc.Slot,
                        before,
                        after,
                        online = player != null,
                    },
                    Data = CharacterProjection.Item(item, loc.Container, loc.Slot),
                };
            }));

            WebAdminAudit.Record(req, "character.item-edit", result.Target, result.Details, reason);
            await req.Ok(result.Data, result.Message);
        }
    }
}

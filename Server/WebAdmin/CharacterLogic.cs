using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Server.WebAdmin
{
    /// <summary>Where an item was found on a character (see <see cref="CharacterLogic.FindItem"/>).</summary>
    public sealed class CharacterItemLocation
    {
        public CharacterItemLocation(string container, int slot, UserItem item)
        {
            Container = container;
            Slot = slot;
            Item = item;
        }

        /// <summary>One of <see cref="CharacterLogic.ContainerEquipment"/>, <see cref="CharacterLogic.ContainerInventory"/>,
        /// <see cref="CharacterLogic.ContainerQuestInventory"/>, <see cref="CharacterLogic.ContainerStorage"/>.</summary>
        public string Container { get; }

        public int Slot { get; }

        public UserItem Item { get; }
    }

    /// <summary>
    /// Pure helpers for the Players / Characters admin feature (B2): validation, list filtering / sorting / paging, slot naming,
    /// stack splitting, free-slot search and item lookup. No Envir / Settings access, so everything here is unit-tested.
    /// </summary>
    public static class CharacterLogic
    {
        public const string ContainerEquipment = "equipment";
        public const string ContainerInventory = "inventory";
        public const string ContainerQuestInventory = "questInventory";
        public const string ContainerStorage = "storage";

        /// <summary>Crystal's belt layout (HumanObject.PotionBeltMinimum.. / BeltSize).</summary>
        public const int PotionBeltMinimum = 0, PotionBeltMaximum = 4, AmuletBeltMinimum = 4, AmuletBeltMaximum = 6, BeltSize = 8;

        /// <summary>First slot of "Bag II" (CharacterInfo.Inventory starts at 48 slots: 8 belt + 40 bag).</summary>
        public const int SecondBagStart = 48;

        /// <summary>AccountInfo.Storage base size; slots from here on are the expanded storage.</summary>
        public const int BaseStorageSize = 80;

        public const int DefaultPageSize = 50, MaxPageSize = 500;

        /// <summary>Most stacks a single "add item" request may create.</summary>
        public const int MaxStacksPerAdd = 100;

        /// <summary>Same pattern as Envir.CharacterReg (character creation).</summary>
        public static readonly Regex NameRegex = new Regex(
            @"^[一-龥_A-Za-z0-9]{" + Globals.MinCharacterNameLength + "," + Globals.MaxCharacterNameLength + "}$",
            RegexOptions.CultureInvariant);

        /// <summary>Null when the name is acceptable for a character, otherwise a human-readable reason.</summary>
        public static string ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
            if (name != name.Trim()) return "Name must not start or end with spaces.";
            if (name.Length < Globals.MinCharacterNameLength || name.Length > Globals.MaxCharacterNameLength)
                return "Name must be " + Globals.MinCharacterNameLength + "-" + Globals.MaxCharacterNameLength + " characters long.";
            if (!NameRegex.IsMatch(name)) return "Name may only contain letters, digits, underscores and CJK characters.";
            return null;
        }

        // ------------------------------------------------------------------ list filtering / sorting / paging

        /// <summary>"include" | "only" | "exclude" (anything else → "include").</summary>
        public static string NormalizeDeletedFilter(string value)
        {
            var v = (value ?? string.Empty).Trim().ToLowerInvariant();
            return v == "only" || v == "exclude" ? v : "include";
        }

        public static bool PassesDeletedFilter(bool deleted, string filter)
        {
            switch (NormalizeDeletedFilter(filter))
            {
                case "only":
                    return deleted;
                case "exclude":
                    return !deleted;
                default:
                    return true;
            }
        }

        /// <summary>Name match: exact (case-insensitive) or substring. Empty query matches everything.</summary>
        public static bool NameMatches(string name, string query, bool exact)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (string.IsNullOrEmpty(name)) return false;
            return exact
                ? string.Equals(name, query, StringComparison.OrdinalIgnoreCase)
                : name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Account filter: a number matches the account index (or an account id that equals it); text matches the account id exactly
        /// (case-insensitive) when <paramref name="exact"/>, otherwise as a substring.
        /// </summary>
        public static bool AccountMatches(int accountIndex, string accountId, string filter, bool exact)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            filter = filter.Trim();

            if (int.TryParse(filter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index == accountIndex)
                return true;

            if (string.IsNullOrEmpty(accountId)) return false;

            return exact
                ? string.Equals(accountId, filter, StringComparison.OrdinalIgnoreCase)
                : accountId.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string NormalizeSort(string sort)
        {
            switch ((sort ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "level":
                    return "level";
                case "lastlogin":
                    return "lastLogin";
                case "class":
                    return "class";
                case "index":
                    return "index";
                default:
                    return "name";
            }
        }

        /// <summary>Sorts (stable, name as tie-breaker), then pages. Page is clamped to [1, pages]; pageSize to [1, MaxPageSize].</summary>
        public static CharacterListResult SortAndPage(IEnumerable<CharacterListRow> rows, string sort, string dir, int page, int pageSize)
        {
            var list = (rows ?? Enumerable.Empty<CharacterListRow>()).Where(r => r != null).ToList();
            var key = NormalizeSort(sort);
            var desc = string.Equals((dir ?? string.Empty).Trim(), "desc", StringComparison.OrdinalIgnoreCase);

            Comparison<CharacterListRow> byName = (a, b) =>
            {
                var c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            };

            Comparison<CharacterListRow> primary;
            switch (key)
            {
                case "level":
                    primary = (a, b) => a.Level.CompareTo(b.Level);
                    break;
                case "lastLogin":
                    primary = (a, b) => Nullable.Compare(a.LastLogin, b.LastLogin);
                    break;
                case "class":
                    primary = (a, b) => string.Compare(a.ClassName, b.ClassName, StringComparison.OrdinalIgnoreCase);
                    break;
                case "index":
                    primary = (a, b) => a.Index.CompareTo(b.Index);
                    break;
                default:
                    primary = (a, b) => 0;
                    break;
            }

            var ordered = list
                .Select((row, i) => (row, i))
                .OrderBy(x => x, Comparer<(CharacterListRow row, int i)>.Create((x, y) =>
                {
                    var c = primary(x.row, y.row);
                    if (c == 0) c = byName(x.row, y.row);
                    if (desc) c = -c;
                    return c != 0 ? c : x.i.CompareTo(y.i);
                }))
                .Select(x => x.row)
                .ToList();

            if (pageSize <= 0) pageSize = DefaultPageSize;
            if (pageSize > MaxPageSize) pageSize = MaxPageSize;

            var total = ordered.Count;
            var pages = Math.Max(1, (total + pageSize - 1) / pageSize);
            if (page < 1) page = 1;
            if (page > pages) page = pages;

            return new CharacterListResult
            {
                Rows = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                Total = total,
                Page = page,
                Pages = pages,
                PageSize = pageSize,
                Sort = key,
                Dir = desc ? "desc" : "asc",
            };
        }

        // ------------------------------------------------------------------ slot names

        /// <summary>PlayerInfoForm naming: Inventory[0-7] = Belt 1-8, [8-47] = Bag I, [48+] = Bag II.</summary>
        public static string InventorySlotName(int slot)
        {
            if (slot < 0) return "?";
            if (slot < BeltSize) return "Belt " + (slot + 1);
            if (slot < SecondBagStart) return "Bag I #" + (slot - BeltSize + 1);
            return "Bag II #" + (slot - SecondBagStart + 1);
        }

        /// <summary>Storage[0-79] = Storage I, [80+] = Storage II (expanded storage).</summary>
        public static string StorageSlotName(int slot)
        {
            if (slot < 0) return "?";
            if (slot < BaseStorageSize) return "Storage I #" + (slot + 1);
            return "Storage II #" + (slot - BaseStorageSize + 1);
        }

        public static string EquipmentSlotName(int slot)
        {
            if (slot >= 0 && slot <= byte.MaxValue && Enum.IsDefined(typeof(EquipmentSlot), (byte)slot))
                return ((EquipmentSlot)(byte)slot).ToString();
            return "Slot " + slot;
        }

        public static string QuestSlotName(int slot) => "Quest #" + (slot + 1);

        public static string SlotName(string container, int slot)
        {
            switch (container)
            {
                case ContainerEquipment:
                    return EquipmentSlotName(slot);
                case ContainerInventory:
                    return InventorySlotName(slot);
                case ContainerQuestInventory:
                    return QuestSlotName(slot);
                case ContainerStorage:
                    return StorageSlotName(slot);
                default:
                    return "Slot " + slot;
            }
        }

        /// <summary>Magic hot key as PlayerInfoForm shows it: 0 = No key, 1-8 = F1-F8, 9-16 = CTRL+F1-F8.</summary>
        public static string MagicKeyLabel(byte key)
        {
            if (key == 0) return "No key";
            if (key > 8)
            {
                var k = key % 8;
                return "CTRL+F" + (k != 0 ? k : 8);
            }

            return "F" + key;
        }

        /// <summary>The experience needed for the next magic level ("-" at level 3), or null when unknown.</summary>
        public static int? MagicNeed(byte level, ushort need1, ushort need2, ushort need3)
        {
            switch (level)
            {
                case 0:
                    return need1;
                case 1:
                    return need2;
                case 2:
                    return need3;
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------ flags

        private static readonly Regex FlagLine = new Regex(@"^\s*\[\s*(\d+)\s*\]\s*-?\s*(.*?)\s*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses <c>Envir\SET [].txt</c> (lines like <c>[012] - Killed the boss</c>; PlayerInfoForm writes a <c>[001] -</c> template).
        /// Lines without a name are skipped; later duplicates win.
        /// </summary>
        public static Dictionary<int, string> ParseFlagNames(IEnumerable<string> lines)
        {
            var map = new Dictionary<int, string>();
            if (lines == null) return map;

            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var m = FlagLine.Match(raw);
                if (!m.Success) continue;

                if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0) continue;

                var name = m.Groups[2].Value.Trim();
                if (name.Length == 0 || name == "-") continue;

                map[index] = name;
            }

            return map;
        }

        /// <summary>Indexes of the set flags.</summary>
        public static List<int> ActiveFlags(bool[] flags)
        {
            var list = new List<int>();
            if (flags == null) return list;

            for (var i = 0; i < flags.Length; i++)
                if (flags[i]) list.Add(i);

            return list;
        }

        // ------------------------------------------------------------------ items

        /// <summary>Splits a count into stacks of at most <paramref name="stackSize"/> (like @MAKE). Empty for count &lt;= 0.</summary>
        public static List<ushort> SplitStacks(int count, ushort stackSize)
        {
            var stacks = new List<ushort>();
            if (count <= 0) return stacks;
            // StackSize 0 is a malformed/unset DB value (Crystal's @make would spin forever on it);
            // treat it as "no stacking limit" and only cap at what UserItem.Count (ushort) can hold.
            if (stackSize == 0) stackSize = ushort.MaxValue;

            while (count > 0)
            {
                var n = Math.Min(count, (int)stackSize);
                stacks.Add((ushort)n);
                count -= n;
            }

            return stacks;
        }

        public static bool IsPotionBeltItem(ItemInfo info)
        {
            return info != null && (info.Type == ItemType.Potion || info.Type == ItemType.Scroll || (info.Type == ItemType.Script && info.Effect == 1));
        }

        /// <summary>First empty index in [start, end) or -1.</summary>
        public static int FindFreeSlot(UserItem[] array, int start, int end)
        {
            if (array == null) return -1;
            if (start < 0) start = 0;
            if (end > array.Length) end = array.Length;

            for (var i = start; i < end; i++)
                if (array[i] == null) return i;

            return -1;
        }

        public static int CountFree(UserItem[] array, int start, int end)
        {
            if (array == null) return 0;
            if (start < 0) start = 0;
            if (end > array.Length) end = array.Length;

            var n = 0;
            for (var i = start; i < end; i++)
                if (array[i] == null) n++;

            return n;
        }

        /// <summary>
        /// The slot HumanObject.AddItem would use for a NEW stack of <paramref name="info"/>: potion-belt items go to belt 1-4, amulets to
        /// belt 5-6, everything else to the bag; then any free slot. -1 when the inventory is full.
        /// </summary>
        public static int PreferredInventorySlot(UserItem[] inventory, ItemInfo info)
        {
            if (inventory == null) return -1;

            int slot;
            if (IsPotionBeltItem(info))
                slot = FindFreeSlot(inventory, PotionBeltMinimum, PotionBeltMaximum);
            else if (info != null && info.Type == ItemType.Amulet)
                slot = FindFreeSlot(inventory, AmuletBeltMinimum, AmuletBeltMaximum);
            else
                slot = FindFreeSlot(inventory, BeltSize, inventory.Length);

            return slot >= 0 ? slot : FindFreeSlot(inventory, 0, inventory.Length);
        }

        /// <summary>How many of <paramref name="info"/> fit into the inventory (room in existing stacks + free slots × stack size).</summary>
        public static long InventoryCapacity(UserItem[] inventory, ItemInfo info)
        {
            if (inventory == null || info == null) return 0;
            var stackSize = Math.Max((ushort)1, info.StackSize);

            long room = 0;
            for (var i = 0; i < inventory.Length; i++)
            {
                var it = inventory[i];
                if (it == null)
                {
                    room += stackSize;
                    continue;
                }

                if (stackSize > 1 && (it.Info == info || (it.Info != null && it.Info.Index == info.Index)) && it.Count < stackSize)
                    room += stackSize - it.Count;
            }

            return room;
        }

        /// <summary>
        /// Offline equivalent of HumanObject.AddItem: merges into existing stacks of the same item first, then places the rest in
        /// <see cref="PreferredInventorySlot"/>. Returns false (and changes nothing) when the item does not fit.
        /// </summary>
        public static bool TryPlaceInInventory(UserItem[] inventory, UserItem item)
        {
            if (inventory == null || item == null || item.Info == null) return false;
            if (InventoryCapacity(inventory, item.Info) < item.Count) return false;

            var stackSize = Math.Max((ushort)1, item.Info.StackSize);

            if (stackSize > 1)
            {
                for (var i = 0; i < inventory.Length && item.Count > 0; i++)
                {
                    var temp = inventory[i];
                    if (temp == null || temp.Count >= stackSize) continue;
                    if (!(temp.Info == item.Info || (temp.Info != null && temp.Info.Index == item.Info.Index))) continue;

                    if (item.Count + temp.Count <= stackSize)
                    {
                        temp.Count += item.Count;
                        item.Count = 0;
                        return true;
                    }

                    item.Count -= (ushort)(stackSize - temp.Count);
                    temp.Count = stackSize;
                }
            }

            var slot = PreferredInventorySlot(inventory, item.Info);
            if (slot < 0) return false;

            inventory[slot] = item;
            return true;
        }

        /// <summary>True when count fits into <see cref="MaxStacksPerAdd"/> stacks of <paramref name="stackSize"/> (O(1), no allocation).</summary>
        public static bool WithinAddLimit(int count, ushort stackSize)
        {
            if (stackSize == 0) stackSize = ushort.MaxValue; // same as SplitStacks
            return (long)count <= (long)stackSize * MaxStacksPerAdd;
        }

        /// <summary>Crystal treats expanded storage as active only while it has not expired (PlayerObject clears the flag at login).</summary>
        public static bool IsExpandedStorageActive(bool hasExpandedStorage, DateTime expiryDate, DateTime now)
        {
            return hasExpandedStorage && expiryDate > now;
        }

        /// <summary>Usable storage slots: the base 80, or the whole array while expanded storage is active.</summary>
        public static int UsableStorageSize(int storageLength, bool expanded)
        {
            if (storageLength <= 0) return 0;
            return expanded ? storageLength : Math.Min(storageLength, BaseStorageSize);
        }

        /// <summary>Finds an item by UniqueID: equipment, inventory, quest inventory, then the account storage.</summary>
        public static CharacterItemLocation FindItem(UserItem[] equipment, UserItem[] inventory, UserItem[] questInventory, UserItem[] storage, ulong uniqueId)
        {
            return Find(equipment, ContainerEquipment, uniqueId)
                ?? Find(inventory, ContainerInventory, uniqueId)
                ?? Find(questInventory, ContainerQuestInventory, uniqueId)
                ?? Find(storage, ContainerStorage, uniqueId);
        }

        private static CharacterItemLocation Find(UserItem[] array, string container, ulong uniqueId)
        {
            if (array == null) return null;
            for (var i = 0; i < array.Length; i++)
            {
                var it = array[i];
                if (it != null && it.UniqueID == uniqueId) return new CharacterItemLocation(container, i, it);
            }

            return null;
        }

        /// <summary>
        /// Envir.MatchPlayerbyItem semantics for one item: a numeric identifier matches the UniqueID, text matches the item's
        /// FriendlyName exactly (case-insensitive) when <paramref name="exact"/>, otherwise as a substring.
        /// </summary>
        public static bool ItemMatches(UserItem item, string identifier, bool exact)
        {
            if (item == null || string.IsNullOrEmpty(identifier)) return false;

            if (ulong.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                return item.UniqueID == id;

            if (item.Info == null) return false;

            var name = item.FriendlyName;
            return exact
                ? name.Equals(identifier, StringComparison.OrdinalIgnoreCase)
                : name.IndexOf(identifier, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool AnyMatch(IEnumerable<UserItem> items, string identifier, bool exact)
        {
            if (items == null) return false;
            foreach (var it in items)
                if (ItemMatches(it, identifier, exact)) return true;
            return false;
        }

        /// <summary>Does the character hold the item (inventory, account storage, quest bag, equipment or mail), like MatchPlayerbyItem.</summary>
        public static bool CharacterHasItem(Server.MirDatabase.CharacterInfo ci, string identifier, bool exact)
        {
            if (ci == null || string.IsNullOrEmpty(identifier)) return false;

            if (AnyMatch(ci.Inventory, identifier, exact)) return true;
            if (AnyMatch(ci.AccountInfo?.Storage, identifier, exact)) return true;
            if (AnyMatch(ci.QuestInventory, identifier, exact)) return true;
            if (AnyMatch(ci.Equipment, identifier, exact)) return true;

            if (ci.Mail != null)
                foreach (var mail in ci.Mail)
                    if (mail != null && AnyMatch(mail.Items, identifier, exact)) return true;

            return false;
        }

        public static bool TryParseUniqueId(string text, out ulong id)
        {
            id = 0;
            return !string.IsNullOrWhiteSpace(text)
                && ulong.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id)
                && id > 0;
        }

        /// <summary>"inventory" | "storage" | "questInventory" for item add (case-insensitive; "quest" accepted). Null when unknown.</summary>
        public static string NormalizeAddContainer(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "":
                case "inventory":
                case "bag":
                    return ContainerInventory;
                case "storage":
                    return ContainerStorage;
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------ stats

        /// <summary>Non-zero stats as {StatName: value} (enum member names).</summary>
        public static Dictionary<string, int> StatsToDictionary(Stats stats)
        {
            var d = new Dictionary<string, int>();
            if (stats?.Values == null) return d;

            foreach (var pair in stats.Values)
            {
                if (pair.Value == 0) continue;
                d[pair.Key.ToString()] = pair.Value;
            }

            return d;
        }

        public static bool TryParseStat(string name, out Stat stat)
        {
            stat = Stat.Unknown;
            if (string.IsNullOrWhiteSpace(name)) return false;

            name = name.Trim();
            if (name.StartsWith("Stat_", StringComparison.OrdinalIgnoreCase)) name = name.Substring(5);

            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                if (n < 0 || n > byte.MaxValue || !Enum.IsDefined(typeof(Stat), (byte)n)) return false;
                stat = (Stat)(byte)n;
                return stat != Stat.Unknown;
            }

            if (!Enum.TryParse(name, true, out stat) || !Enum.IsDefined(typeof(Stat), stat)) return false;
            return stat != Stat.Unknown;
        }

        /// <summary>
        /// Parses an <c>addedStats</c> object ({"MinAC": 2, "Luck": 1}). A value of 0 removes the stat. Error text on failure.
        /// </summary>
        public static bool TryParseStats(JsonElement obj, out Dictionary<Stat, int> stats, out string error)
        {
            stats = new Dictionary<Stat, int>();
            error = null;

            if (obj.ValueKind == JsonValueKind.Null || obj.ValueKind == JsonValueKind.Undefined) return true;
            if (obj.ValueKind != JsonValueKind.Object)
            {
                error = "addedStats must be an object of {StatName: value}.";
                return false;
            }

            foreach (var p in obj.EnumerateObject())
            {
                if (!TryParseStat(p.Name, out var stat))
                {
                    error = "Unknown stat: " + p.Name;
                    return false;
                }

                if (!TryReadInt(p.Value, out var value))
                {
                    error = "Stat " + p.Name + " must be an integer.";
                    return false;
                }

                stats[stat] = value;
            }

            return true;
        }

        private static bool TryReadInt(JsonElement v, out int value)
        {
            value = 0;
            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    return v.TryGetInt32(out value);
                case JsonValueKind.String:
                    return int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                case JsonValueKind.Null:
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ spells / enums

        /// <summary>Spell by enum member name (case-insensitive) or numeric value; never Spell.None.</summary>
        public static bool TryParseSpell(string text, out Spell spell)
        {
            spell = Spell.None;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                if (n <= 0 || n > byte.MaxValue || !Enum.IsDefined(typeof(Spell), (byte)n)) return false;
                spell = (Spell)(byte)n;
                return spell != Spell.None;
            }

            if (!Enum.TryParse(text, true, out spell) || !Enum.IsDefined(typeof(Spell), spell)) return false;
            return spell != Spell.None;
        }

        /// <summary>MirGender by name or number.</summary>
        public static bool TryParseGender(string text, out MirGender gender)
        {
            gender = MirGender.Male;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                if (n < 0 || n > byte.MaxValue || !Enum.IsDefined(typeof(MirGender), (byte)n)) return false;
                gender = (MirGender)(byte)n;
                return true;
            }

            return Enum.TryParse(text, true, out gender) && Enum.IsDefined(typeof(MirGender), gender);
        }

        // ------------------------------------------------------------------ currency

        /// <summary>
        /// The client packet needed to move an account currency from <paramref name="current"/> to <paramref name="target"/>:
        /// (gain, amount) — gain=true → GainedGold/GainedCredit, false → LoseGold/LoseCredit; amount 0 = unchanged.
        /// </summary>
        public static (bool Gain, uint Amount) CurrencyDelta(uint current, uint target)
        {
            if (target >= current) return (true, target - current);
            return (false, current - target);
        }

        /// <summary>DateTime.MinValue (Crystal's "never") → null.</summary>
        public static DateTime? NullIfMin(DateTime value)
        {
            return value <= DateTime.MinValue.AddDays(1) ? (DateTime?)null : value;
        }

        /// <summary>First line of a mail message, trimmed to <paramref name="max"/> characters.</summary>
        public static string MailPreview(string message, int max = 80)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;

            var line = message.Replace("\r", string.Empty);
            var nl = line.IndexOf('\n');
            if (nl >= 0) line = line.Substring(0, nl);
            line = line.Trim();

            if (max > 1 && line.Length > max) line = line.Substring(0, max - 1) + "…";
            return line;
        }
    }
}

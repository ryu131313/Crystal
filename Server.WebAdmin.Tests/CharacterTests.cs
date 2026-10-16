using System.Text.Json;
using Server.MirDatabase;

namespace Server.WebAdmin.Tests
{
    /// <summary>Pure logic of the Players Online / Characters feature (B2). No Envir / Settings / process-wide statics involved.</summary>
    public class CharacterTests
    {
        private static ItemInfo Info(int index, string name, ItemType type = ItemType.Weapon, ushort stackSize = 1, byte effect = 0)
        {
            return new ItemInfo { Index = index, Name = name, Type = type, StackSize = stackSize, Effect = effect };
        }

        private static UserItem Item(ItemInfo info, ulong uid, ushort count = 1)
        {
            return new UserItem(info) { UniqueID = uid, Count = count };
        }

        private static CharacterListRow Row(int index, string name, int level, string cls = "Warrior", DateTime? lastLogin = null)
        {
            return new CharacterListRow { Index = index, Name = name, Level = level, ClassName = cls, LastLogin = lastLogin };
        }

        // ------------------------------------------------------------------ names

        [Theory]
        [InlineData("Bob")]
        [InlineData("Hero_123")]
        [InlineData("ABCDEFGHIJKLMNO")]
        [InlineData("张三丰")]
        public void ValidateName_AcceptsCrystalNames(string name)
        {
            Assert.Null(CharacterLogic.ValidateName(name));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Al")]
        [InlineData("ABCDEFGHIJKLMNOP")]
        [InlineData("Bad Name")]
        [InlineData("Evil<b>")]
        [InlineData(" Bob")]
        [InlineData("Bob-1")]
        public void ValidateName_RejectsInvalidNames(string name)
        {
            Assert.NotNull(CharacterLogic.ValidateName(name));
        }

        // ------------------------------------------------------------------ filters

        [Theory]
        [InlineData(null, "include")]
        [InlineData("", "include")]
        [InlineData("ONLY", "only")]
        [InlineData(" exclude ", "exclude")]
        [InlineData("whatever", "include")]
        public void NormalizeDeletedFilter(string input, string expected)
        {
            Assert.Equal(expected, CharacterLogic.NormalizeDeletedFilter(input));
        }

        [Fact]
        public void PassesDeletedFilter()
        {
            Assert.True(CharacterLogic.PassesDeletedFilter(true, "include"));
            Assert.True(CharacterLogic.PassesDeletedFilter(false, "include"));
            Assert.True(CharacterLogic.PassesDeletedFilter(true, "only"));
            Assert.False(CharacterLogic.PassesDeletedFilter(false, "only"));
            Assert.False(CharacterLogic.PassesDeletedFilter(true, "exclude"));
            Assert.True(CharacterLogic.PassesDeletedFilter(false, "exclude"));
        }

        [Fact]
        public void NameMatches_ExactAndContains()
        {
            Assert.True(CharacterLogic.NameMatches("DarkKnight", "", false));
            Assert.True(CharacterLogic.NameMatches("DarkKnight", "knight", false));
            Assert.False(CharacterLogic.NameMatches("DarkKnight", "knight", true));
            Assert.True(CharacterLogic.NameMatches("DarkKnight", "darkknight", true));
            Assert.False(CharacterLogic.NameMatches(null, "x", false));
        }

        [Fact]
        public void AccountMatches_IndexIdAndSubstring()
        {
            Assert.True(CharacterLogic.AccountMatches(7, "player1", null, false));
            Assert.True(CharacterLogic.AccountMatches(7, "player1", "7", true));
            Assert.True(CharacterLogic.AccountMatches(7, "player1", "PLAYER1", true));
            Assert.False(CharacterLogic.AccountMatches(7, "player1", "play", true));
            Assert.True(CharacterLogic.AccountMatches(7, "player1", "play", false));
            Assert.False(CharacterLogic.AccountMatches(7, "player1", "8", false));
        }

        // ------------------------------------------------------------------ sort + page

        [Fact]
        public void SortAndPage_DefaultsToNameAscending()
        {
            var r = CharacterLogic.SortAndPage(new[] { Row(1, "charlie", 5), Row(2, "Alpha", 9), Row(3, "bravo", 1) }, null, null, 1, 10);

            Assert.Equal(new[] { "Alpha", "bravo", "charlie" }, r.Rows.Select(x => x.Name));
            Assert.Equal("name", r.Sort);
            Assert.Equal("asc", r.Dir);
            Assert.Equal(3, r.Total);
            Assert.Equal(1, r.Pages);
        }

        [Fact]
        public void SortAndPage_LevelDescending_TiesByName()
        {
            var rows = new[] { Row(1, "b", 10), Row(2, "a", 10), Row(3, "c", 50), Row(4, "d", 1) };
            var r = CharacterLogic.SortAndPage(rows, "level", "desc", 1, 10);

            Assert.Equal(new[] { 3, 1, 2, 4 }, r.Rows.Select(x => x.Index));
        }

        [Fact]
        public void SortAndPage_LastLogin_NullsFirstAscending()
        {
            var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var rows = new[] { Row(1, "a", 1, lastLogin: t.AddDays(2)), Row(2, "b", 1), Row(3, "c", 1, lastLogin: t) };
            var r = CharacterLogic.SortAndPage(rows, "lastLogin", "asc", 1, 10);

            Assert.Equal(new[] { 2, 3, 1 }, r.Rows.Select(x => x.Index));
            Assert.Equal("lastLogin", r.Sort);
        }

        [Fact]
        public void SortAndPage_PagesAndClamps()
        {
            var rows = Enumerable.Range(1, 25).Select(i => Row(i, "n" + i.ToString("D2"), i)).ToList();

            var p2 = CharacterLogic.SortAndPage(rows, "index", "asc", 2, 10);
            Assert.Equal(25, p2.Total);
            Assert.Equal(3, p2.Pages);
            Assert.Equal(2, p2.Page);
            Assert.Equal(Enumerable.Range(11, 10), p2.Rows.Select(x => x.Index));

            var beyond = CharacterLogic.SortAndPage(rows, "index", "asc", 99, 10);
            Assert.Equal(3, beyond.Page);
            Assert.Equal(5, beyond.Rows.Count);

            var before = CharacterLogic.SortAndPage(rows, "index", "asc", -4, 10);
            Assert.Equal(1, before.Page);

            var huge = CharacterLogic.SortAndPage(rows, "index", "asc", 1, 100000);
            Assert.Equal(CharacterLogic.MaxPageSize, huge.PageSize);

            var empty = CharacterLogic.SortAndPage(Array.Empty<CharacterListRow>(), "name", "asc", 3, 0);
            Assert.Equal(1, empty.Page);
            Assert.Equal(1, empty.Pages);
            Assert.Equal(CharacterLogic.DefaultPageSize, empty.PageSize);
            Assert.Empty(empty.Rows);
        }

        // ------------------------------------------------------------------ slot names / labels

        [Theory]
        [InlineData(0, "Belt 1")]
        [InlineData(5, "Belt 6")]
        [InlineData(7, "Belt 8")]
        [InlineData(8, "Bag I #1")]
        [InlineData(47, "Bag I #40")]
        [InlineData(48, "Bag II #1")]
        [InlineData(55, "Bag II #8")]
        public void InventorySlotName(int slot, string expected)
        {
            Assert.Equal(expected, CharacterLogic.InventorySlotName(slot));
        }

        [Fact]
        public void StorageAndEquipmentSlotNames()
        {
            Assert.Equal("Storage I #1", CharacterLogic.StorageSlotName(0));
            Assert.Equal("Storage I #80", CharacterLogic.StorageSlotName(79));
            Assert.Equal("Storage II #1", CharacterLogic.StorageSlotName(80));
            Assert.Equal("Weapon", CharacterLogic.EquipmentSlotName(0));
            Assert.Equal("RingR", CharacterLogic.EquipmentSlotName((int)EquipmentSlot.RingR));
            Assert.Equal("Slot 99", CharacterLogic.EquipmentSlotName(99));
            Assert.Equal("Quest #3", CharacterLogic.SlotName(CharacterLogic.ContainerQuestInventory, 2));
        }

        [Theory]
        [InlineData(0, "No key")]
        [InlineData(1, "F1")]
        [InlineData(8, "F8")]
        [InlineData(9, "CTRL+F1")]
        [InlineData(16, "CTRL+F8")]
        public void MagicKeyLabel(byte key, string expected)
        {
            Assert.Equal(expected, CharacterLogic.MagicKeyLabel(key));
        }

        [Fact]
        public void MagicNeed_PerLevel()
        {
            Assert.Equal(100, CharacterLogic.MagicNeed(0, 100, 200, 300));
            Assert.Equal(200, CharacterLogic.MagicNeed(1, 100, 200, 300));
            Assert.Equal(300, CharacterLogic.MagicNeed(2, 100, 200, 300));
            Assert.Null(CharacterLogic.MagicNeed(3, 100, 200, 300));
        }

        // ------------------------------------------------------------------ flags

        [Fact]
        public void ParseFlagNames_SkipsTemplateAndGarbage()
        {
            var names = CharacterLogic.ParseFlagNames(new[]
            {
                "[001] -",
                "[002] - Killed the Zuma King",
                "  [10]-Opened the secret door  ",
                "garbage line",
                "",
                null,
                "[002] - Renamed later",
                "[abc] - not a number",
            });

            Assert.Equal(2, names.Count);
            Assert.Equal("Renamed later", names[2]);
            Assert.Equal("Opened the secret door", names[10]);
            Assert.False(names.ContainsKey(1));
        }

        [Fact]
        public void ActiveFlags_ListsSetIndexes()
        {
            var flags = new bool[10];
            flags[0] = flags[3] = flags[9] = true;
            Assert.Equal(new[] { 0, 3, 9 }, CharacterLogic.ActiveFlags(flags));
            Assert.Empty(CharacterLogic.ActiveFlags(null));
        }

        // ------------------------------------------------------------------ stacks / slots / placement

        [Fact]
        public void SplitStacks_LikeMake()
        {
            Assert.Equal(new ushort[] { 50, 50, 20 }, CharacterLogic.SplitStacks(120, 50));
            Assert.Equal(new ushort[] { 1, 1, 1 }, CharacterLogic.SplitStacks(3, 1));
            Assert.Equal(new ushort[] { 7 }, CharacterLogic.SplitStacks(7, 0));
            Assert.Empty(CharacterLogic.SplitStacks(0, 10));
        }

        [Fact]
        public void WithinAddLimit_IsAnO1CapBeforeSplitting()
        {
            Assert.True(CharacterLogic.WithinAddLimit(CharacterLogic.MaxStacksPerAdd, 1));
            Assert.False(CharacterLogic.WithinAddLimit(CharacterLogic.MaxStacksPerAdd + 1, 1));
            Assert.False(CharacterLogic.WithinAddLimit(int.MaxValue, 1));
            Assert.True(CharacterLogic.WithinAddLimit(50 * CharacterLogic.MaxStacksPerAdd, 50));
            Assert.False(CharacterLogic.WithinAddLimit(50 * CharacterLogic.MaxStacksPerAdd + 1, 50));
            Assert.True(CharacterLogic.WithinAddLimit(ushort.MaxValue * CharacterLogic.MaxStacksPerAdd, 0)); // StackSize 0 = unlimited, like SplitStacks
        }

        [Fact]
        public void IsExpandedStorageActive_RequiresUnexpiredFlag()
        {
            var now = new DateTime(2026, 1, 1);
            Assert.True(CharacterLogic.IsExpandedStorageActive(true, now.AddDays(1), now));
            Assert.False(CharacterLogic.IsExpandedStorageActive(true, now.AddDays(-1), now));
            Assert.False(CharacterLogic.IsExpandedStorageActive(false, now.AddDays(1), now));
        }

        [Fact]
        public void PreferredInventorySlot_FollowsAddItemRules()
        {
            var inv = new UserItem[48];
            Assert.Equal(0, CharacterLogic.PreferredInventorySlot(inv, Info(1, "HP Potion", ItemType.Potion)));
            Assert.Equal(0, CharacterLogic.PreferredInventorySlot(inv, Info(2, "TownScroll", ItemType.Script, effect: 1)));
            Assert.Equal(8, CharacterLogic.PreferredInventorySlot(inv, Info(3, "OtherScript", ItemType.Script, effect: 0)));
            Assert.Equal(4, CharacterLogic.PreferredInventorySlot(inv, Info(4, "Amulet", ItemType.Amulet)));
            Assert.Equal(8, CharacterLogic.PreferredInventorySlot(inv, Info(5, "Sword", ItemType.Weapon)));

            // Bag full: a weapon falls back to a free belt slot (HumanObject.AddItem's last loop).
            var sword = Info(5, "Sword");
            for (var i = 8; i < inv.Length; i++) inv[i] = Item(sword, (ulong)(100 + i));
            Assert.Equal(0, CharacterLogic.PreferredInventorySlot(inv, sword));

            for (var i = 0; i < 8; i++) inv[i] = Item(sword, (ulong)(200 + i));
            Assert.Equal(-1, CharacterLogic.PreferredInventorySlot(inv, sword));
        }

        [Fact]
        public void InventoryCapacity_CountsStackRoomAndFreeSlots()
        {
            var potion = Info(1, "HP Potion", ItemType.Potion, stackSize: 20);
            var inv = new UserItem[3];
            inv[0] = Item(potion, 1, 15);
            inv[1] = Item(Info(9, "Sword"), 2);

            Assert.Equal(5 + 20, CharacterLogic.InventoryCapacity(inv, potion));
            Assert.Equal(1, CharacterLogic.InventoryCapacity(inv, Info(9, "Sword")));
        }

        [Fact]
        public void TryPlaceInInventory_MergesStacksThenUsesFreeSlot()
        {
            var potion = Info(1, "HP Potion", ItemType.Potion, stackSize: 20);
            var inv = new UserItem[8];
            inv[2] = Item(potion, 1, 15);

            var incoming = Item(potion, 2, 12);
            Assert.True(CharacterLogic.TryPlaceInInventory(inv, incoming));

            Assert.Equal(20, inv[2].Count);          // topped up
            Assert.Same(incoming, inv[0]);           // remainder in the first potion-belt slot
            Assert.Equal(7, incoming.Count);
        }

        [Fact]
        public void TryPlaceInInventory_FullMergeLeavesSlotsUntouched()
        {
            var potion = Info(1, "HP Potion", ItemType.Potion, stackSize: 20);
            var inv = new UserItem[8];
            inv[6] = Item(potion, 1, 5);

            Assert.True(CharacterLogic.TryPlaceInInventory(inv, Item(potion, 2, 10)));
            Assert.Equal(15, inv[6].Count);
            Assert.Equal(1, inv.Count(x => x != null));
        }

        [Fact]
        public void TryPlaceInInventory_RefusesWhenItDoesNotFit_AndChangesNothing()
        {
            var potion = Info(1, "HP Potion", ItemType.Potion, stackSize: 20);
            var sword = Info(2, "Sword");
            var inv = new UserItem[2];
            inv[0] = Item(potion, 1, 18);
            inv[1] = Item(sword, 2);

            var incoming = Item(potion, 3, 5);
            Assert.False(CharacterLogic.TryPlaceInInventory(inv, incoming));
            Assert.Equal(18, inv[0].Count);
            Assert.Equal(5, incoming.Count);
        }

        [Fact]
        public void FreeSlotHelpers()
        {
            var arr = new UserItem[5];
            arr[0] = Item(Info(1, "A"), 1);
            arr[2] = Item(Info(1, "A"), 2);
            Assert.Equal(1, CharacterLogic.FindFreeSlot(arr, 0, 5));
            Assert.Equal(3, CharacterLogic.FindFreeSlot(arr, 2, 5));
            Assert.Equal(-1, CharacterLogic.FindFreeSlot(arr, 0, 1));
            Assert.Equal(3, CharacterLogic.CountFree(arr, 0, 99));
            Assert.Equal(80, CharacterLogic.UsableStorageSize(120, false));
            Assert.Equal(120, CharacterLogic.UsableStorageSize(120, true));
            Assert.Equal(0, CharacterLogic.UsableStorageSize(0, true));
        }

        [Fact]
        public void FindItem_SearchesEquipmentInventoryQuestStorageInOrder()
        {
            var info = Info(1, "Ring");
            var equipment = new UserItem[14];
            var inventory = new UserItem[48];
            var quest = new UserItem[40];
            var storage = new UserItem[80];
            equipment[7] = Item(info, 10);
            inventory[12] = Item(info, 11);
            quest[3] = Item(info, 12);
            storage[79] = Item(info, 13);

            var a = CharacterLogic.FindItem(equipment, inventory, quest, storage, 10);
            Assert.Equal(CharacterLogic.ContainerEquipment, a.Container);
            Assert.Equal(7, a.Slot);

            Assert.Equal(CharacterLogic.ContainerInventory, CharacterLogic.FindItem(equipment, inventory, quest, storage, 11).Container);
            Assert.Equal(CharacterLogic.ContainerQuestInventory, CharacterLogic.FindItem(equipment, inventory, quest, storage, 12).Container);
            var s = CharacterLogic.FindItem(equipment, inventory, quest, null, 13);
            Assert.Null(s);
            Assert.Equal(79, CharacterLogic.FindItem(equipment, inventory, quest, storage, 13).Slot);
            Assert.Null(CharacterLogic.FindItem(equipment, inventory, quest, storage, 999));
        }

        [Fact]
        public void ItemMatches_UidOrFriendlyName()
        {
            var item = Item(Info(1, "GoldenSword"), 42);
            Assert.True(CharacterLogic.ItemMatches(item, "42", true));
            Assert.False(CharacterLogic.ItemMatches(item, "43", false));
            Assert.True(CharacterLogic.ItemMatches(item, "goldensword", true));
            Assert.True(CharacterLogic.ItemMatches(item, "golden", false));
            Assert.False(CharacterLogic.ItemMatches(item, "golden", true));
            Assert.False(CharacterLogic.ItemMatches(null, "x", false));
        }

        [Fact]
        public void CharacterHasItem_ChecksInventoryStorageQuestAndEquipment()
        {
            var ci = new CharacterInfo { Name = "Tester", AccountInfo = new AccountInfo() };
            Assert.False(CharacterLogic.CharacterHasItem(ci, "Sword", false));

            ci.AccountInfo.Storage[5] = Item(Info(1, "IronSword"), 77);
            Assert.True(CharacterLogic.CharacterHasItem(ci, "sword", false));
            Assert.True(CharacterLogic.CharacterHasItem(ci, "77", true));
            Assert.False(CharacterLogic.CharacterHasItem(ci, "sword", true));

            var ci2 = new CharacterInfo { Name = "Other" };
            ci2.Equipment[0] = Item(Info(2, "BlackIronHelmet"), 5);
            Assert.True(CharacterLogic.CharacterHasItem(ci2, "BlackIronHelmet", true));
        }

        [Theory]
        [InlineData("123", true, 123UL)]
        [InlineData(" 9 ", true, 9UL)]
        [InlineData("0", false, 0UL)]
        [InlineData("-1", false, 0UL)]
        [InlineData("abc", false, 0UL)]
        [InlineData("", false, 0UL)]
        public void TryParseUniqueId(string text, bool ok, ulong expected)
        {
            Assert.Equal(ok, CharacterLogic.TryParseUniqueId(text, out var id));
            if (ok) Assert.Equal(expected, id);
        }

        [Fact]
        public void NormalizeAddContainer()
        {
            Assert.Equal(CharacterLogic.ContainerInventory, CharacterLogic.NormalizeAddContainer(null));
            Assert.Equal(CharacterLogic.ContainerInventory, CharacterLogic.NormalizeAddContainer("Inventory"));
            Assert.Equal(CharacterLogic.ContainerStorage, CharacterLogic.NormalizeAddContainer("STORAGE"));
            Assert.Null(CharacterLogic.NormalizeAddContainer("equipment"));
        }

        // ------------------------------------------------------------------ stats / spells / gender

        [Fact]
        public void TryParseStats_AcceptsNamesNumbersAndStatPrefix()
        {
            using var doc = JsonDocument.Parse("{\"MinAC\": 2, \"Stat_Luck\": \"3\", \"5\": 4, \"HP\": null}");
            Assert.True(CharacterLogic.TryParseStats(doc.RootElement, out var stats, out var error));
            Assert.Null(error);
            Assert.Equal(2, stats[Stat.MinAC]);
            Assert.Equal(3, stats[Stat.Luck]);
            Assert.Equal(4, stats[Stat.MaxDC]); // "5" is MaxDC by value
            Assert.Equal(0, stats[Stat.HP]);    // null = remove (0)
            Assert.Equal(4, stats.Count);
        }

        [Fact]
        public void TryParseStats_RejectsUnknownStatsAndBadValues()
        {
            using (var doc = JsonDocument.Parse("{\"NotAStat\": 1}"))
            {
                Assert.False(CharacterLogic.TryParseStats(doc.RootElement, out _, out var error));
                Assert.Contains("NotAStat", error);
            }

            using (var doc = JsonDocument.Parse("{\"MinAC\": 1.5}"))
                Assert.False(CharacterLogic.TryParseStats(doc.RootElement, out _, out _));

            using (var doc = JsonDocument.Parse("{\"Unknown\": 1}"))
                Assert.False(CharacterLogic.TryParseStats(doc.RootElement, out _, out _));

            using (var doc = JsonDocument.Parse("[1,2]"))
                Assert.False(CharacterLogic.TryParseStats(doc.RootElement, out _, out _));

            Assert.True(CharacterLogic.TryParseStats(default, out var none, out _));
            Assert.Empty(none);
        }

        [Fact]
        public void StatsToDictionary_SkipsZeros()
        {
            var stats = new Stats();
            stats[Stat.MinAC] = 3;
            stats[Stat.Luck] = -1;
            stats[Stat.HP] = 0;

            var d = CharacterLogic.StatsToDictionary(stats);
            Assert.Equal(2, d.Count);
            Assert.Equal(3, d["MinAC"]);
            Assert.Equal(-1, d["Luck"]);
            Assert.Empty(CharacterLogic.StatsToDictionary(null));
        }

        [Fact]
        public void TryParseSpell_NamesAndValues_NeverNone()
        {
            Assert.True(CharacterLogic.TryParseSpell("fencing", out var s1));
            Assert.Equal(Spell.Fencing, s1);
            Assert.True(CharacterLogic.TryParseSpell(((int)Spell.Fencing).ToString(), out var s2));
            Assert.Equal(Spell.Fencing, s2);
            Assert.False(CharacterLogic.TryParseSpell("None", out _));
            Assert.False(CharacterLogic.TryParseSpell("0", out _));
            Assert.False(CharacterLogic.TryParseSpell("NoSuchSpell", out _));
            Assert.False(CharacterLogic.TryParseSpell("", out _));
        }

        [Fact]
        public void TryParseGender()
        {
            Assert.True(CharacterLogic.TryParseGender("female", out var g));
            Assert.Equal(MirGender.Female, g);
            Assert.True(CharacterLogic.TryParseGender("0", out g));
            Assert.Equal(MirGender.Male, g);
            Assert.False(CharacterLogic.TryParseGender("7", out _));
            Assert.False(CharacterLogic.TryParseGender("other", out _));
        }

        // ------------------------------------------------------------------ currency / misc

        [Fact]
        public void CurrencyDelta_GainOrLose()
        {
            Assert.Equal((true, 50u), CharacterLogic.CurrencyDelta(100, 150));
            Assert.Equal((false, 30u), CharacterLogic.CurrencyDelta(100, 70));
            Assert.Equal((true, 0u), CharacterLogic.CurrencyDelta(5, 5));
            Assert.Equal((true, uint.MaxValue), CharacterLogic.CurrencyDelta(0, uint.MaxValue));
        }

        [Fact]
        public void NullIfMin_And_MailPreview()
        {
            Assert.Null(CharacterLogic.NullIfMin(DateTime.MinValue));
            var t = new DateTime(2026, 5, 1);
            Assert.Equal(t, CharacterLogic.NullIfMin(t));

            Assert.Equal("Hello there", CharacterLogic.MailPreview("Hello there\r\nsecond line"));
            Assert.Equal(string.Empty, CharacterLogic.MailPreview(null));
            var longText = new string('x', 200);
            var preview = CharacterLogic.MailPreview(longText, 20);
            Assert.Equal(20, preview.Length);
            Assert.EndsWith("…", preview);
        }
    }
}

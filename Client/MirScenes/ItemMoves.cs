using Client.MirObjects;
using Client.MirScenes.Models;

namespace Client.MirScenes
{
    /// <summary>One item slot: a grid and a slot index (Desktop-Modern-UI-Plan seam step 3).</summary>
    public readonly struct ItemSlotKey : IEquatable<ItemSlotKey>
    {
        public readonly MirGridType Grid;
        public readonly int Slot;

        public ItemSlotKey(MirGridType grid, int slot)
        {
            Grid = grid;
            Slot = slot;
        }

        public bool Equals(ItemSlotKey other) => Grid == other.Grid && Slot == other.Slot;
        public override bool Equals(object obj) => obj is ItemSlotKey other && Equals(other);
        public override int GetHashCode() => ((int)Grid << 16) ^ Slot;
    }

    /// <summary>
    /// A live view of one item slot by grid + slot. What a picked-up item is (<see cref="ItemMoves.Selected"/>) and what the
    /// moved MirItemCell logic works on instead of a cell: <see cref="Item"/> and <see cref="Locked"/> read and write the slot's
    /// state in ItemMoves, so a classic MirItemCell and a ModernItemCell showing the same slot always agree.
    /// </summary>
    public sealed class ItemSlotRef
    {
        public readonly MirGridType GridType;
        public readonly int ItemSlotIndex;

        public ItemSlotRef(MirGridType grid, int slot)
        {
            GridType = grid;
            ItemSlotIndex = slot;
        }

        public ItemSlotKey Key => new ItemSlotKey(GridType, ItemSlotIndex);

        /// <summary>The slot index (named like MirItemCell.ItemSlot so moved code reads the same).</summary>
        public int ItemSlot => ItemSlotIndex;

        public UserItem Item
        {
            get => ItemMoves.GetItem(GridType, ItemSlotIndex);
            set => ItemMoves.SetItem(GridType, ItemSlotIndex, value);
        }

        public UserItem ShadowItem => ItemMoves.GetShadowItem(GridType, ItemSlotIndex);

        public UserItem[] ItemArray => ItemMoves.GetArray(GridType);

        public bool Locked
        {
            get => ItemMoves.IsLocked(GridType, ItemSlotIndex);
            set => ItemMoves.SetLocked(GridType, ItemSlotIndex, value);
        }

        public bool Is(MirGridType grid, int slot) => GridType == grid && ItemSlotIndex == slot;
    }

    /// <summary>
    /// Item slot state and the pick / drop / use logic shared by every item cell (Desktop-Modern-UI-Plan seam step 3). Looks
    /// slots up by grid + slot instead of through a dialog's cell array, so it works with no classic dialog constructed.
    /// Owns: the slot arrays per grid (<see cref="GetArray"/>, was MirItemCell.ItemArray), the per-slot server round-trip locks
    /// (was MirItemCell.Locked), the picked-up item (<see cref="Selected"/>, was GameScene.SelectedCell) and
    /// <see cref="SlotChanged"/>, which tells cells showing a slot to redraw. Per game: <see cref="Reset"/> from the GameScene
    /// constructor and Dispose. UI thread only.
    /// </summary>
    public static partial class ItemMoves
    {
        private static readonly HashSet<ItemSlotKey> _locks = new HashSet<ItemSlotKey>();
        private static ItemSlotRef _selected;

        /// <summary>A slot's item, lock or pick state changed: cells showing that slot redraw. Grid-wide changes use slot -1.</summary>
        public static readonly Emitter<ItemSlotKey> SlotChanged = new Emitter<ItemSlotKey>();

        /// <summary>The picked-up item (click-pick, no optimistic moves), or null. Was GameScene.SelectedCell.</summary>
        public static ItemSlotRef Selected
        {
            get => _selected;
            set
            {
                ItemSlotRef old = _selected;
                if (old == null && value == null) return;
                _selected = value;
                if (old != null) SlotChanged.Emit(old.Key);
                if (value != null) SlotChanged.Emit(value.Key);
            }
        }

        public static bool IsSelected(MirGridType grid, int slot) => _selected != null && _selected.Is(grid, slot);

        /// <summary>Picks up the item in a slot (Selected = that slot).</summary>
        public static void Select(MirGridType grid, int slot) => Selected = new ItemSlotRef(grid, slot);

        /// <summary>Clears the pick, the locks and the change subscribers' state (new game / end of game).</summary>
        public static void Reset()
        {
            _selected = null;
            _locks.Clear();
        }

        /// <summary>Was MirItemCell.ItemArray. Throws NotImplementedException for a grid with no array (as before).</summary>
        public static UserItem[] GetArray(MirGridType grid)
        {
            GameModels models = GameScene.Models;
            switch (grid)
            {
                case MirGridType.Inventory:
                    return MapObject.User.Inventory;
                case MirGridType.Equipment:
                    return MapObject.User.Equipment;
                case MirGridType.Storage:
                    return GameScene.Storage;
                case MirGridType.Inspect:
                    return models.Inspect.Items;
                case MirGridType.GuildStorage:
                    return GameScene.GuildStorage;
                case MirGridType.Trade:
                    return GameScene.User.Trade;
                case MirGridType.GuestTrade:
                    return models.Trade.GuestItems;
                case MirGridType.Mount:
                    return MapObject.User.Equipment[(int)EquipmentSlot.Mount].Slots;
                case MirGridType.Fishing:
                    return MapObject.User.Equipment[(int)EquipmentSlot.Weapon].Slots;
                case MirGridType.QuestInventory:
                    return MapObject.User.QuestInventory;
                case MirGridType.AwakenItem:
                    return models.Npc.AwakeItems;
                case MirGridType.Mail:
                    return models.Mail.ParcelItems;
                case MirGridType.Refine:
                    return GameScene.Refine;
                case MirGridType.Craft:
                    return models.Npc.CraftItems;
                case MirGridType.Socket:
                    return GameScene.SelectedItem?.Slots;
                case MirGridType.HeroEquipment:
                    return MapObject.Hero.Equipment;
                case MirGridType.HeroInventory:
                    return MapObject.Hero.Inventory;
                case MirGridType.HeroHPItem:
                    return MapObject.Hero.HPItem;
                case MirGridType.HeroMPItem:
                    return MapObject.Hero.MPItem;

                default:
                    throw new NotImplementedException();
            }
        }

        /// <summary>The grids that hold one item in a model field instead of an array (slot is ignored).</summary>
        public static bool IsSingleSlot(MirGridType grid)
        {
            return grid == MirGridType.DropPanel || grid == MirGridType.TrustMerchant || grid == MirGridType.Renting || grid == MirGridType.GuestRenting;
        }

        /// <summary>Was the MirItemCell.Item getter.</summary>
        public static UserItem GetItem(MirGridType grid, int slot)
        {
            GameModels models = GameScene.Models;
            switch (grid)
            {
                case MirGridType.DropPanel:
                    return models.Npc.DropTarget;
                case MirGridType.TrustMerchant:
                    return models.Market.SellItem;
                case MirGridType.Renting:
                    return models.Rental.RentalItem;
                case MirGridType.GuestRenting:
                    return models.Rental.GuestLoanItem;
            }

            UserItem[] array = GetArray(grid);
            if (array != null && slot >= 0 && slot < array.Length)
                return array[slot];
            return null;
        }

        /// <summary>Was the MirItemCell.Item setter (minus the cell redraw, which <see cref="SlotChanged"/> now triggers).</summary>
        public static void SetItem(MirGridType grid, int slot, UserItem item)
        {
            GameModels models = GameScene.Models;
            switch (grid)
            {
                case MirGridType.DropPanel:
                    models.Npc.DropTarget = item;
                    break;
                case MirGridType.Renting:
                    models.Rental.RentalItem = item;
                    break;
                case MirGridType.TrustMerchant:
                    models.Market.SellItem = item;
                    break;
                case MirGridType.GuestRenting:
                    models.Rental.GuestLoanItem = item;
                    break;
                default:
                    UserItem[] array = GetArray(grid);
                    if (array != null && slot >= 0 && slot < array.Length)
                        array[slot] = item;
                    break;
            }

            SlotChanged.Emit(new ItemSlotKey(grid, slot));
        }

        /// <summary>Was MirItemCell.ShadowItem: the recipe ghost under an empty craft slot.</summary>
        public static UserItem GetShadowItem(MirGridType grid, int slot)
        {
            if (grid != MirGridType.Craft) return null;
            UserItem[] shadows = GameScene.Models.Npc.CraftShadowItems;
            if (slot >= 0 && slot < shadows.Length && slot < GetArray(grid).Length)
                return shadows[slot];
            return null;
        }

        /// <summary>The slot holding the item with this UniqueID in a grid, or -1 (was XDialog.GetCell(id)).</summary>
        public static int FindSlot(MirGridType grid, ulong uniqueID)
        {
            UserItem[] array = GetArray(grid);
            if (array == null) return -1;
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i] != null && array[i].UniqueID == uniqueID)
                    return i;
            }
            return -1;
        }

        public static bool IsLocked(MirGridType grid, int slot) => _locks.Count > 0 && _locks.Contains(new ItemSlotKey(grid, slot));

        /// <summary>Was MirItemCell.Locked: set while a request for the slot is with the server; cleared in the reply handler.</summary>
        public static void SetLocked(MirGridType grid, int slot, bool locked)
        {
            ItemSlotKey key = new ItemSlotKey(grid, slot);
            bool changed = locked ? _locks.Add(key) : _locks.Remove(key);
            if (changed) SlotChanged.Emit(key);
        }

        /// <summary>
        /// Drops every lock on the hero's grids. Locks used to live in the cells, which the classic UI rebuilds on S.HeroInformation
        /// and disposes on unsummon, so a request still pending then left no lock behind.
        /// </summary>
        public static void ClearHeroLocks()
        {
            if (_locks.Count == 0) return;
            _locks.RemoveWhere(IsHeroLock);
            GridChanged(MirGridType.HeroInventory);
            GridChanged(MirGridType.HeroEquipment);
            GridChanged(MirGridType.HeroHPItem);
            GridChanged(MirGridType.HeroMPItem);
        }

        private static bool IsHeroLock(ItemSlotKey key)
        {
            return key.Grid == MirGridType.HeroInventory || key.Grid == MirGridType.HeroEquipment ||
                   key.Grid == MirGridType.HeroHPItem || key.Grid == MirGridType.HeroMPItem;
        }

        /// <summary>Tells the cells of a whole grid to redraw (slot -1), e.g. after a full list arrived.</summary>
        public static void GridChanged(MirGridType grid) => SlotChanged.Emit(new ItemSlotKey(grid, -1));
    }
}

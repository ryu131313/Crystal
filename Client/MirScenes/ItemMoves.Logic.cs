using Client.MirNetwork;
using Client.MirObjects;
using Client.MirSounds;
using C = ClientPackets;

namespace Client.MirScenes
{
    /// <summary>
    /// The pick / drop / use / move logic moved out of MirItemCell (Desktop-Modern-UI-Plan seam step 3), working on a slot
    /// (<see cref="ItemSlotRef"/>: grid + slot) instead of a cell. Same statements, same order, same packets and sounds as the
    /// classic cell had; the classic-only pieces (chat links, delete mode, prompts, socket / awaken / mail windows) go through
    /// GameScene.Ui (IGameUi.Items.cs). UI thread only.
    /// </summary>
    public static partial class ItemMoves
    {
        /// <summary>Uses the item in a slot (was MirItemCell.UseItem on that slot's cell; belt keys, double-click).</summary>
        public static void Use(MirGridType grid, int slot)
        {
            UseItem(new ItemSlotRef(grid, slot));
        }

        /// <summary>
        /// A slot by index for the reply handlers (was XDialog.Grid[index]), or null where that index had no cell: negative, or
        /// past the grid's array. A grid whose owner is missing (no mount, no rod, no hero, no socketed item) still gets a slot,
        /// as the classic cell existed then too, so its lock is still cleared.
        /// </summary>
        public static ItemSlotRef Slot(MirGridType grid, int slot)
        {
            if (slot < 0) return null;
            UserItem[] array = TryGetArray(grid);
            if (array != null && slot >= array.Length) return null;
            return new ItemSlotRef(grid, slot);
        }

        /// <summary>The slot holding this item in a grid, or null (was XDialog.GetCell(id)).</summary>
        public static ItemSlotRef Find(MirGridType grid, ulong uniqueID)
        {
            UserItem[] array = TryGetArray(grid);
            if (array == null) return null;
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i] != null && array[i].UniqueID == uniqueID)
                    return new ItemSlotRef(grid, i);
            }
            return null;
        }

        /// <summary>The belt slot (Inventory[0..BeltSize-1]) holding this item, or null (was BeltDialog.GetCell(id)).</summary>
        public static ItemSlotRef FindBelt(ulong uniqueID)
        {
            UserItem[] array = TryGetArray(MirGridType.Inventory);
            if (array == null) return null;
            for (int i = 0; i < UserObject.BeltSize && i < array.Length; i++)
            {
                if (array[i] != null && array[i].UniqueID == uniqueID)
                    return new ItemSlotRef(MirGridType.Inventory, i);
            }
            return null;
        }

        /// <summary>The bag slot (Inventory[BeltSize..]) holding this item, or null (was InventoryDialog.GetCell(id), no belt).</summary>
        public static ItemSlotRef FindBag(ulong uniqueID)
        {
            UserItem[] array = TryGetArray(MirGridType.Inventory);
            if (array == null) return null;
            for (int i = UserObject.BeltSize; i < array.Length; i++)
            {
                if (array[i] != null && array[i].UniqueID == uniqueID)
                    return new ItemSlotRef(MirGridType.Inventory, i);
            }
            return null;
        }

        /// <summary><see cref="GetArray"/>, but null instead of throwing when the grid's owner or array is missing.</summary>
        private static UserItem[] TryGetArray(MirGridType grid)
        {
            switch (grid)
            {
                case MirGridType.Inventory:
                case MirGridType.Equipment:
                case MirGridType.QuestInventory:
                    if (MapObject.User == null) return null;
                    break;
                case MirGridType.Trade:
                    if (GameScene.User == null) return null;
                    break;
                case MirGridType.Mount:
                    if (MapObject.User == null || MapObject.User.Equipment[(int)EquipmentSlot.Mount] == null) return null;
                    break;
                case MirGridType.Fishing:
                    if (MapObject.User == null || MapObject.User.Equipment[(int)EquipmentSlot.Weapon] == null) return null;
                    break;
                case MirGridType.HeroEquipment:
                case MirGridType.HeroInventory:
                case MirGridType.HeroHPItem:
                case MirGridType.HeroMPItem:
                    if (MapObject.Hero == null) return null;
                    break;
                case MirGridType.Storage:
                case MirGridType.Inspect:
                case MirGridType.GuildStorage:
                case MirGridType.GuestTrade:
                case MirGridType.AwakenItem:
                case MirGridType.Mail:
                case MirGridType.Refine:
                case MirGridType.Craft:
                case MirGridType.Socket:
                    break;
                default:
                    return null;
            }
            return GetArray(grid);
        }

        /// <summary>
        /// First half of MirItemCell.OnMouseClick: the delete-mode and lock / pick checks. False = the click stops here (the cell
        /// then skips its base click and redraw, as before).
        /// </summary>
        public static bool BeginClick(ItemSlotRef cell, MouseEventArgs e)
        {
            if (GameScene.Ui.ItemDeleteModeClick(cell, e.Button)) return false;

            if (cell.Locked || GameScene.Observing) return false;

            if (GameScene.PickedUpGold || cell.GridType == MirGridType.Inspect || cell.GridType == MirGridType.QuestInventory) return false;

            if (Selected == null && (cell.GridType == MirGridType.Mail)) return false;

            return true;
        }

        /// <summary>Second half of MirItemCell.OnMouseClick, after the cell's base click and redraw.</summary>
        public static void Click(ItemSlotRef cell, MouseEventArgs e)
        {
            switch (e.Button)
            {
                case MouseButtons.Right:
                    if (CMain.Ctrl)
                    {
                        if (cell.Item != null)
                        {
                            OpenItem(cell);
                        }
                        break;
                    }

                    if (CMain.Shift)
                    {
                        if (cell.Item != null)
                        {
                            GameScene.Ui.ItemChatLink(cell);
                        }

                        break;
                    }

                    UseItem(cell);
                    break;
                case MouseButtons.Left:
                    if (cell.Item != null && Selected == null)
                        PlayItemSound(cell);

                    if (CMain.Shift)
                    {
                        if (cell.GridType == MirGridType.Inventory || cell.GridType == MirGridType.Storage)
                        {
                            if (Selected == null && cell.Item != null)
                            {
                                if (FreeSpace(cell) == 0)
                                {
                                    GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.NoRoomToSplitStack), ChatType.System);
                                    return;
                                }

                                if (cell.Item.Count > 1)
                                {
                                    GameScene.Ui.ItemAskAmount(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.SplitAmount), cell.Item.Image, (uint)(cell.Item.Count - 1), amount =>
                                    {
                                        if (amount == 0 || amount >= cell.Item.Count) return;
                                        Network.Enqueue(new C.SplitItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, Count = (ushort)amount });
                                        cell.Locked = true;
                                    });
                                }
                            }
                        }
                    }
                    
                    //Add support for ALT + click to sell quickly
                    else if (CMain.Alt && GameScene.Ui.IsNpcDropOpen && cell.GridType == MirGridType.Inventory) // alt sell/repair
                    {
                        MoveItem(cell); // pickup item
                        GameScene.Ui.ItemNpcDropQuickSell(e); // emulate click to drop control, then OK to confirm trade
                    }
                    //Add support for ALT + click to sell quickly

                    else if ((cell.GridType == MirGridType.HeroHPItem || cell.GridType == MirGridType.HeroMPItem) && Selected == null && cell.Item != null)
                        Network.Enqueue(new C.SetAutoPotItem { Grid = cell.GridType, ItemIndex = 0 });

                    else MoveItem(cell);
                    break;
            }
        }

        /// <summary>First half of MirItemCell.OnMouseDoubleClick (false = stop, as before).</summary>
        public static bool BeginDoubleClick(ItemSlotRef cell)
        {
            if (cell.Locked) return false;

            if (GameScene.PickedUpGold || cell.GridType == MirGridType.Inspect || cell.GridType == MirGridType.TrustMerchant || cell.GridType == MirGridType.Craft) return false;

            return true;
        }

        /// <summary>Second half of MirItemCell.OnMouseDoubleClick, after the cell's base click and redraw.</summary>
        public static void DoubleClick(ItemSlotRef cell)
        {
            Selected = null;
            UseItem(cell);
        }


        private static void BuyItem(ItemSlotRef cell)
        {
            if (cell.Item == null || cell.Item.Price() * GameScene.NPCRate > GameScene.Gold) return;

            if (cell.Item.Count > 1)
            {
                GameScene.Ui.ItemAskAmount(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PurchaseAmount), cell.Item.Image, cell.Item.Count, amount =>
                {
                    Network.Enqueue(new C.BuyItemBack { UniqueID = cell.Item.UniqueID, Count = (ushort)amount });
                    cell.Locked = true;
                });
            }
            else
            {
                GameScene.Ui.ItemAskAmountMessage(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.Purchase), cell.Item.Image, GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.ValueGold), cell.Item.Price()), () =>
                {
                    Network.Enqueue(new C.BuyItemBack { UniqueID = cell.Item.UniqueID, Count = 1 });
                    cell.Locked = true;
                });
            }
        }

        public static void OpenItem(ItemSlotRef cell)
        {
            if ((cell.GridType != MirGridType.Equipment && cell.GridType != MirGridType.Inventory) || cell.Item == null || IsSelected(cell.GridType, cell.ItemSlot)) return;

            GameScene.Ui.ItemShowSockets(cell.GridType, cell.Item);
        }

        private static bool HeroGridType(ItemSlotRef cell) => cell.GridType == MirGridType.HeroInventory || cell.GridType == MirGridType.HeroEquipment;

        public static void UseItem(ItemSlotRef cell)
        {
            if (cell.Locked || cell.GridType == MirGridType.Inspect || cell.GridType == MirGridType.TrustMerchant || cell.GridType == MirGridType.GuildStorage || cell.GridType == MirGridType.Craft) return;

            if (!HeroGridType(cell) && MapObject.User.Fishing) return;
            if (MapObject.User.RidingMount && cell.Item.Info.Type != ItemType.Scroll && cell.Item.Info.Type != ItemType.Potion && cell.Item.Info.Type != ItemType.Torch) return;

            if (cell.GridType == MirGridType.BuyBack)
            {
                BuyItem(cell);
                return;
            }

            if (cell.GridType == MirGridType.Equipment || cell.GridType == MirGridType.Mount || cell.GridType == MirGridType.Fishing || cell.GridType == MirGridType.Socket)
            {
                RemoveItem(cell);
                return;
            }

            if (cell.GridType == MirGridType.HeroEquipment)
            {
                RemoveHeroItem(cell);
                return;
            }

            if ((cell.GridType != MirGridType.Inventory && cell.GridType != MirGridType.Storage && cell.GridType != MirGridType.HeroInventory) || cell.Item == null || !CanUseItem(cell) || IsSelected(cell.GridType, cell.ItemSlot)) return;

            MirGridType equipGrid = MirGridType.Equipment;
            UserObject actor = GameScene.User;
            if (HeroGridType(cell))
            {
                equipGrid = MirGridType.HeroEquipment;
                actor = GameScene.Hero;

                if (cell.Item.SoulBoundId != -1 && MapObject.Hero.Id != cell.Item.SoulBoundId)
                    return;
            }
            else
            {
                if (cell.Item.SoulBoundId != -1 && MapObject.User.Id != cell.Item.SoulBoundId)
                    return;
            }
            

            switch (cell.Item.Info.Type)
            {
                case ItemType.Weapon:
                    if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.Weapon), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.Weapon });
                        SetLocked(equipGrid, (int)EquipmentSlot.Weapon, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Armour:
                    if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.Armour), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.Armour });
                        SetLocked(equipGrid, (int)EquipmentSlot.Armour, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Helmet:
                    if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.Helmet), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.Helmet });
                        SetLocked(equipGrid, (int)EquipmentSlot.Helmet, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Necklace:
                    if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.Necklace), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.Necklace });
                        SetLocked(equipGrid, (int)EquipmentSlot.Necklace, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Bracelet:
                    if ((GetItem(equipGrid, (int)EquipmentSlot.BraceletR) == null || GetItem(equipGrid, (int)EquipmentSlot.BraceletR).Info.Type == ItemType.Amulet) && CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.BraceletR), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.BraceletR });
                        SetLocked(equipGrid, (int)EquipmentSlot.BraceletR, true);
                        cell.Locked = true;
                    }
                    else if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.BraceletL), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.BraceletL });
                        SetLocked(equipGrid, (int)EquipmentSlot.BraceletL, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Ring:
                    if (GetItem(equipGrid, (int)EquipmentSlot.RingR) == null && CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.RingR), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.RingR });
                        SetLocked(equipGrid, (int)EquipmentSlot.RingR, true);
                        cell.Locked = true;
                    }
                    else if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.RingL), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.RingL });
                        SetLocked(equipGrid, (int)EquipmentSlot.RingL, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Amulet:
                    //if (Item.Info.Shape == 0) return;

                    if (GetItem(equipGrid, (int)EquipmentSlot.Amulet) != null && cell.Item.Info.Type == ItemType.Amulet)
                    {
                        if (GetItem(equipGrid, (int)EquipmentSlot.Amulet).Info == cell.Item.Info && GetItem(equipGrid, (int)EquipmentSlot.Amulet).Count < GetItem(equipGrid, (int)EquipmentSlot.Amulet).Info.StackSize)
                        {
                            Network.Enqueue(new C.MergeItem { GridFrom = cell.GridType, GridTo = cell.GridType == MirGridType.HeroInventory ? MirGridType.HeroEquipment : MirGridType.Equipment, IDFrom = cell.Item.UniqueID, IDTo = GetItem(equipGrid, (int)EquipmentSlot.Amulet).UniqueID });

                            cell.Locked = true;
                            return;
                        }
                    }

                    if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.Amulet), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.Amulet });
                        SetLocked(equipGrid, (int)EquipmentSlot.Amulet, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Belt:
                    if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.Belt), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.Belt });
                        SetLocked(equipGrid, (int)EquipmentSlot.Belt, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Boots:
                    if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.Boots), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.Boots });
                        SetLocked(equipGrid, (int)EquipmentSlot.Boots, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Stone:
                    if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.Stone), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.Stone });
                        SetLocked(equipGrid, (int)EquipmentSlot.Stone, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Torch:
                    if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.Torch), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.Torch });
                        SetLocked(equipGrid, (int)EquipmentSlot.Torch, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Potion:
                case ItemType.Scroll:
                case ItemType.Book:
                case ItemType.Food:
                case ItemType.Script:
                case ItemType.Pets:
                case ItemType.Transform:
                case ItemType.Deco:
                case ItemType.MonsterSpawn:
                case ItemType.SealedHero:
                    if (CanUseItem(cell) && (cell.GridType == MirGridType.Inventory || cell.GridType == MirGridType.HeroInventory))
                    {
                        if (CMain.Time < GameScene.UseItemTime) return;
                        if (cell.Item.Info.Type == ItemType.Potion && cell.Item.Info.Shape == 4)
                        {
                            GameScene.Ui.ItemAskYesNo(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AreYouWantUsePotion), () =>
                            {
                                Network.Enqueue(new C.UseItem { UniqueID = cell.Item.UniqueID, Grid = cell.GridType });

                                if (cell.Item.Count == 1 && cell.ItemSlot < GameScene.User.BeltIdx)
                                {
                                    for (int i = GameScene.User.BeltIdx; i < GameScene.User.Inventory.Length; i++)
                                        if (cell.ItemArray[i] != null && cell.ItemArray[i].Info == cell.Item.Info)
                                        {
                                            Network.Enqueue(new C.MoveItem { Grid = MirGridType.Inventory, From = i, To = cell.ItemSlot });
                                            SetLocked(MirGridType.Inventory, i, true);
                                            break;
                                        }
                                }

                                GameScene.UseItemTime = CMain.Time + 100;
                                PlayItemSound(cell);
                            });

                            return;
                        }

                        Network.Enqueue(new C.UseItem { UniqueID = cell.Item.UniqueID, Grid = cell.GridType });

                        if (HeroGridType(cell))
                        {
                            if (cell.Item.Count == 1 && cell.ItemSlot < GameScene.User.HeroBeltIdx)
                            {
                                for (int i = GameScene.User.HeroBeltIdx; i < GameScene.Hero.Inventory.Length; i++)
                                    if (cell.ItemArray[i] != null && cell.ItemArray[i].Info == cell.Item.Info)
                                    {
                                        Network.Enqueue(new C.MoveItem { Grid = MirGridType.HeroInventory, From = i, To = cell.ItemSlot });
                                        SetLocked(MirGridType.HeroInventory, i, true);
                                        break;
                                    }
                            }
                        }
                        else
                        {
                            if (cell.Item.Count == 1 && cell.ItemSlot < GameScene.User.BeltIdx)
                            {
                                for (int i = GameScene.User.BeltIdx; i < GameScene.User.Inventory.Length; i++)
                                    if (cell.ItemArray[i] != null && cell.ItemArray[i].Info == cell.Item.Info)
                                    {
                                        Network.Enqueue(new C.MoveItem { Grid = MirGridType.Inventory, From = i, To = cell.ItemSlot });
                                        SetLocked(MirGridType.Inventory, i, true);
                                        break;
                                    }
                            }
                        }                        

                        cell.Locked = true;
                    }
                    break;
                case ItemType.Mount:
                    if (CanWearItem(new ItemSlotRef(equipGrid, (int)EquipmentSlot.Mount), actor, cell.Item))
                    {
                        Network.Enqueue(new C.EquipItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)EquipmentSlot.Mount });
                        SetLocked(equipGrid, (int)EquipmentSlot.Mount, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Reins:
                case ItemType.Bells:
                case ItemType.Ribbon:
                case ItemType.Saddle:
                case ItemType.Mask:
                case ItemType.Hook:
                case ItemType.Float:
                case ItemType.Bait:
                case ItemType.Finder:
                case ItemType.Reel:
                case ItemType.Socket:
                    UseSlotItem(cell);
                    break;
            }

            GameScene.UseItemTime = CMain.Time + 300;
            PlayItemSound(cell);
        }
        public static void UseSlotItem(ItemSlotRef cell)
        {
            if (!CanUseItem(cell)) return;

            switch (cell.Item.Info.Type)
            {
                case ItemType.Socket:
                    if (GameScene.SelectedItem != null && !GameScene.SelectedItem.Info.IsFishingRod && GameScene.SelectedItem.Info.Type != ItemType.Mount)
                    {
                        switch (cell.Item.Info.Shape)
                        {
                            case 1:
                                if (GameScene.SelectedItem.Info.Type != ItemType.Weapon) return;
                                break;
                            case 2:
                                if (GameScene.SelectedItem.Info.Type != ItemType.Armour) return;
                                break;
                            case 3:
                                if (GameScene.SelectedItem.Info.Type != ItemType.Ring && GameScene.SelectedItem.Info.Type != ItemType.Bracelet && GameScene.SelectedItem.Info.Type != ItemType.Necklace) return;
                                break;
                        }

                        ItemSlotRef socketCell = null;
                        int socketSlot = GameScene.Ui.ItemFreeSocketSlot();
                        if (socketSlot >= 0)
                            socketCell = new ItemSlotRef(MirGridType.Socket, socketSlot);

                        if (socketCell != null && CanWearItem(socketCell, GameScene.User, cell.Item))
                        {
                            Network.Enqueue(new C.EquipSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = socketCell.ItemSlot, GridTo = MirGridType.Socket, ToUniqueID = GameScene.SelectedItem.UniqueID });
                            socketCell.Locked = true;
                            cell.Locked = true;
                        }
                    }
                    break;
                case ItemType.Reins:
                    if (CanWearItem(new ItemSlotRef(MirGridType.Mount, (int)MountSlot.Reins), GameScene.User, cell.Item))
                    {
                        var toItem = MapObject.User.Equipment[(byte)EquipmentSlot.Mount];
                        Network.Enqueue(new C.EquipSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)MountSlot.Reins, GridTo = MirGridType.Mount, ToUniqueID = toItem.UniqueID });
                        SetLocked(MirGridType.Mount, (int)MountSlot.Reins, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Bells:
                    if (CanWearItem(new ItemSlotRef(MirGridType.Mount, (int)MountSlot.Bells), GameScene.User, cell.Item))
                    {
                        var toItem = MapObject.User.Equipment[(byte)EquipmentSlot.Mount];
                        Network.Enqueue(new C.EquipSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)MountSlot.Bells, GridTo = MirGridType.Mount, ToUniqueID = toItem.UniqueID });
                        SetLocked(MirGridType.Mount, (int)MountSlot.Bells, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Ribbon:
                    if (CanWearItem(new ItemSlotRef(MirGridType.Mount, (int)MountSlot.Ribbon), GameScene.User, cell.Item))
                    {
                        var toItem = MapObject.User.Equipment[(byte)EquipmentSlot.Mount];
                        Network.Enqueue(new C.EquipSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)MountSlot.Ribbon, GridTo = MirGridType.Mount, ToUniqueID = toItem.UniqueID });
                        SetLocked(MirGridType.Mount, (int)MountSlot.Ribbon, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Saddle:
                    if (CanWearItem(new ItemSlotRef(MirGridType.Mount, (int)MountSlot.Saddle), GameScene.User, cell.Item))
                    {
                        var toItem = MapObject.User.Equipment[(byte)EquipmentSlot.Mount];
                        Network.Enqueue(new C.EquipSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)MountSlot.Saddle, GridTo = MirGridType.Mount, ToUniqueID = toItem.UniqueID });
                        SetLocked(MirGridType.Mount, (int)MountSlot.Saddle, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Mask:
                    if (CanWearItem(new ItemSlotRef(MirGridType.Mount, (int)MountSlot.Mask), GameScene.User, cell.Item))
                    {
                        var toItem = MapObject.User.Equipment[(byte)EquipmentSlot.Mount];
                        Network.Enqueue(new C.EquipSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)MountSlot.Mask, GridTo = MirGridType.Mount, ToUniqueID = toItem.UniqueID });
                        SetLocked(MirGridType.Mount, (int)MountSlot.Mask, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Hook:
                    if (CanWearItem(new ItemSlotRef(MirGridType.Fishing, (int)FishingSlot.Hook), GameScene.User, cell.Item))
                    {
                        var toItem = MapObject.User.Equipment[(byte)EquipmentSlot.Weapon];
                        Network.Enqueue(new C.EquipSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)FishingSlot.Hook, GridTo = MirGridType.Fishing, ToUniqueID = toItem.UniqueID });
                        SetLocked(MirGridType.Fishing, (int)FishingSlot.Hook, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Float:
                    if (CanWearItem(new ItemSlotRef(MirGridType.Fishing, (int)FishingSlot.Float), GameScene.User, cell.Item))
                    {
                        var toItem = MapObject.User.Equipment[(byte)EquipmentSlot.Weapon];
                        Network.Enqueue(new C.EquipSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)FishingSlot.Float, GridTo = MirGridType.Fishing, ToUniqueID = toItem.UniqueID });
                        SetLocked(MirGridType.Fishing, (int)FishingSlot.Float, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Bait:
                    if (GetItem(MirGridType.Fishing, (int)FishingSlot.Bait) != null && cell.Item.Info.Type == ItemType.Bait)
                    {
                        if (GetItem(MirGridType.Fishing, (int)FishingSlot.Bait).Info == cell.Item.Info && GetItem(MirGridType.Fishing, (int)FishingSlot.Bait).Count < GetItem(MirGridType.Fishing, (int)FishingSlot.Bait).Info.StackSize)
                        {
                            Network.Enqueue(new C.MergeItem { GridFrom = cell.GridType, GridTo = MirGridType.Fishing, IDFrom = cell.Item.UniqueID, IDTo = GetItem(MirGridType.Fishing, (int)FishingSlot.Bait).UniqueID });

                            cell.Locked = true;
                            Selected.Locked = true;
                            Selected = null;
                            return;
                        }
                    }

                    if (CanWearItem(new ItemSlotRef(MirGridType.Fishing, (int)FishingSlot.Bait), GameScene.User, cell.Item))
                    {
                        var toItem = MapObject.User.Equipment[(byte)EquipmentSlot.Weapon];
                        Network.Enqueue(new C.EquipSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)FishingSlot.Bait, GridTo = MirGridType.Fishing, ToUniqueID = toItem.UniqueID });
                        SetLocked(MirGridType.Fishing, (int)FishingSlot.Bait, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Finder:
                    if (CanWearItem(new ItemSlotRef(MirGridType.Fishing, (int)FishingSlot.Finder), GameScene.User, cell.Item))
                    {
                        var toItem = MapObject.User.Equipment[(byte)EquipmentSlot.Weapon];
                        Network.Enqueue(new C.EquipSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)FishingSlot.Finder, GridTo = MirGridType.Fishing, ToUniqueID = toItem.UniqueID });
                        SetLocked(MirGridType.Fishing, (int)FishingSlot.Finder, true);
                        cell.Locked = true;
                    }
                    break;
                case ItemType.Reel:
                    if (CanWearItem(new ItemSlotRef(MirGridType.Fishing, (int)FishingSlot.Reel), GameScene.User, cell.Item))
                    {
                        var toItem = MapObject.User.Equipment[(byte)EquipmentSlot.Weapon];
                        Network.Enqueue(new C.EquipSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = (int)FishingSlot.Reel, GridTo = MirGridType.Fishing, ToUniqueID = toItem.UniqueID });
                        SetLocked(MirGridType.Fishing, (int)FishingSlot.Reel, true);
                        cell.Locked = true;
                    }
                    break;
            }
        }

        public static void RemoveItem(ItemSlotRef cell)
        {
            int count = 0;

            for (int i = 0; i < GameScene.User.Inventory.Length; i++)
            {
                ItemSlotRef itemCell = new ItemSlotRef(MirGridType.Inventory, i);

                if (itemCell.Item == null) count++;
            }

            if (cell.Item == null || count < 1 || (MapObject.User.RidingMount && cell.Item.Info.Type != ItemType.Torch)) return;

            if (cell.Item.Info.StackSize > 1)
            {
                UserItem item = null;

                for (int i = 0; i < GameScene.User.Inventory.Length; i++)
                {
                    ItemSlotRef itemCell = new ItemSlotRef(MirGridType.Inventory, i);

                    if (itemCell.Item == null || itemCell.Item.Info != cell.Item.Info) continue;

                    item = itemCell.Item;
                }

                if (item != null && ((item.Count + cell.Item.Count) <= item.Info.StackSize))
                {
                    //Merge.
                    Network.Enqueue(new C.MergeItem { GridFrom = cell.GridType, GridTo = MirGridType.Inventory, IDFrom = cell.Item.UniqueID, IDTo = item.UniqueID });

                    cell.Locked = true;

                    PlayItemSound(cell);
                    return;
                }
            }

            for (int i = 0; i < GameScene.User.Inventory.Length; i++)
            {
                ItemSlotRef itemCell;

                if (cell.Item.Info.Type == ItemType.Amulet)
                {
                    itemCell = new ItemSlotRef(MirGridType.Inventory, i);
                }
                else
                {
                    itemCell = new ItemSlotRef(MirGridType.Inventory, i < (GameScene.User.Inventory.Length - GameScene.User.BeltIdx) ? GameScene.User.BeltIdx + i : i - (GameScene.User.Inventory.Length - GameScene.User.BeltIdx));
                }

                if (itemCell.Item != null) continue;

                if (cell.GridType != MirGridType.Equipment)
                {
                    ulong fromID;

                    if (cell.GridType == MirGridType.Fishing)
                    {
                        if (GetItem(MirGridType.Equipment, (byte)EquipmentSlot.Weapon) == null) return;

                        fromID = GetItem(MirGridType.Equipment, (byte)EquipmentSlot.Weapon).UniqueID;
                    }
                    else if (cell.GridType == MirGridType.Mount)
                    {
                        if (GetItem(MirGridType.Equipment, (byte)EquipmentSlot.Mount) == null) return;

                        fromID = GetItem(MirGridType.Equipment, (byte)EquipmentSlot.Mount).UniqueID;
                    }
                    else
                    {
                        if (GameScene.SelectedItem == null) return;

                        fromID = GameScene.SelectedItem.UniqueID;
                    }

                    Network.Enqueue(new C.RemoveSlotItem { Grid = cell.GridType, UniqueID = cell.Item.UniqueID, To = itemCell.ItemSlot, GridTo = MirGridType.Inventory, FromUniqueID = fromID });
                }
                else
                {
                    Network.Enqueue(new C.RemoveItem { Grid = MirGridType.Inventory, UniqueID = cell.Item.UniqueID, To = itemCell.ItemSlot });
                }

                cell.Locked = true;

                PlayItemSound(cell);
                break;
            }
        }

        public static void RemoveHeroItem(ItemSlotRef cell)
        {
            int count = 0;
            if (GameScene.Hero == null || GameScene.Hero.Dead) return;

            for (int i = 0; i < GameScene.Hero.Inventory.Length; i++)
            {
                ItemSlotRef itemCell = new ItemSlotRef(MirGridType.HeroInventory, i);

                if (itemCell.Item == null) count++;
            }

            if (cell.Item == null || count < 1 || (MapObject.Hero.RidingMount && cell.Item.Info.Type != ItemType.Torch)) return;

            if (cell.Item.Info.StackSize > 1)
            {
                UserItem item = null;

                for (int i = 0; i < GameScene.Hero.Inventory.Length; i++)
                {
                    ItemSlotRef itemCell = new ItemSlotRef(MirGridType.HeroInventory, i);

                    if (itemCell.Item == null || itemCell.Item.Info != cell.Item.Info) continue;

                    item = itemCell.Item;
                }

                if (item != null && ((item.Count + cell.Item.Count) <= item.Info.StackSize))
                {
                    //Merge.
                    Network.Enqueue(new C.MergeItem { GridFrom = cell.GridType, GridTo = MirGridType.Inventory, IDFrom = cell.Item.UniqueID, IDTo = item.UniqueID });

                    cell.Locked = true;

                    PlayItemSound(cell);
                    return;
                }
            }

            for (int i = 0; i < GameScene.Hero.Inventory.Length; i++)
            {
                ItemSlotRef itemCell;

                if (cell.Item.Info.Type == ItemType.Amulet)
                {
                    itemCell = new ItemSlotRef(MirGridType.HeroInventory, i);
                }
                else
                {
                    int heroSlot = i < (GameScene.Hero.Inventory.Length - GameScene.User.HeroBeltIdx) ? GameScene.User.HeroBeltIdx + i : i - GameScene.Hero.Inventory.Length;
                    //Was HeroBeltDialog.Grid[i - Hero.Inventory.Length]: a negative index that threw IndexOutOfRangeException (nothing sent).
                    if (heroSlot < 0) break;
                    itemCell = new ItemSlotRef(MirGridType.HeroInventory, heroSlot);
                }

                if (itemCell.Item != null) continue;

                Network.Enqueue(new C.RemoveItem { Grid = MirGridType.HeroInventory, UniqueID = cell.Item.UniqueID, To = itemCell.ItemSlot });

                cell.Locked = true;

                PlayItemSound(cell);
                break;
            }
        }

        private static void MoveItem(ItemSlotRef cell)
        {
            if (cell.GridType == MirGridType.BuyBack || cell.GridType == MirGridType.DropPanel || cell.GridType == MirGridType.Inspect || cell.GridType == MirGridType.TrustMerchant || cell.GridType == MirGridType.Craft) return;

            if (Selected != null)
            {
                if (Selected.Item == null || IsSelected(cell.GridType, cell.ItemSlot))
                {
                    Selected = null;
                    return;
                }

                switch (cell.GridType)
                {
                    #region To Inventory
                    case MirGridType.Inventory: // To Inventory
                        switch (Selected.GridType)
                        {
                            #region From Inventory
                            case MirGridType.Inventory: //From Invenotry
                                if (cell.Item != null)
                                {
                                    if (CMain.Ctrl)
                                    {
                                        GameScene.Ui.ItemAskYesNo(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouWantCombineItems), () =>
                                        {
                                            //Combine
                                            Network.Enqueue(new C.CombineItem { Grid = Selected.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });
                                            cell.Locked = true;
                                            Selected.Locked = true;
                                            Selected = null;
                                        });

                                        return;
                                    }

                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        //Merge
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                Network.Enqueue(new C.MoveItem { Grid = cell.GridType, From = Selected.ItemSlot, To = cell.ItemSlot });

                                cell.Locked = true;
                                Selected.Locked = true;
                                Selected = null;
                                return;
                            #endregion
                            #region From Equipment
                            case MirGridType.Equipment: //From Equipment
                                if (cell.Item != null && Selected.Item.Info.Type == ItemType.Amulet)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                if (!CanRemoveItem(cell, Selected.Item))
                                {
                                    Selected = null;
                                    return;
                                }
                                if (cell.Item == null)
                                {
                                    Network.Enqueue(new C.RemoveItem { Grid = cell.GridType, UniqueID = Selected.Item.UniqueID, To = cell.ItemSlot });

                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                for (int x = 6; x < cell.ItemArray.Length; x++)
                                    if (cell.ItemArray[x] == null)
                                    {
                                        Network.Enqueue(new C.RemoveItem { Grid = cell.GridType, UniqueID = Selected.Item.UniqueID, To = x });

                                        ItemSlotRef temp = Slot(MirGridType.Inventory, x);

                                        if (temp != null) temp.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                break;
                            #endregion
                            #region From Storage
                            case MirGridType.Storage: //From Storage
                                if (cell.Item != null && Selected.Item.Info.Type == ItemType.Amulet)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }


                                if (cell.Item == null)
                                {
                                    Network.Enqueue(new C.TakeBackItem { From = Selected.ItemSlot, To = cell.ItemSlot });

                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                for (int x = 6; x < cell.ItemArray.Length; x++)
                                    if (cell.ItemArray[x] == null)
                                    {
                                        Network.Enqueue(new C.TakeBackItem { From = Selected.ItemSlot, To = x });

                                        ItemSlotRef temp = Slot(MirGridType.Inventory, x);

                                        if (temp != null) temp.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                break;
                            #endregion
                            #region From Guild Storage
                            case MirGridType.GuildStorage:
                                if (cell.Item != null)
                                {
                                    GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouCannotSwapItems), ChatType.System);
                                    return;
                                }
                                if (!GameScene.Models.Guild.MyOptions.HasFlag(GuildRankOptions.CanRetrieveItem))
                                {
                                    GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.InsufficientRightsRetrieveItems), ChatType.System);
                                    return;
                                }
                                Network.Enqueue(new C.GuildStorageItemChange { Type = 1, From = Selected.ItemSlot, To = cell.ItemSlot });
                                cell.Locked = true;
                                Selected.Locked = true;
                                Selected = null;
                                break;
                            #endregion
                            #region From Trade
                            case MirGridType.Trade: //From Trade
                                if (cell.Item != null && Selected.Item.Info.Type == ItemType.Amulet)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }


                                if (cell.Item == null)
                                {
                                    Network.Enqueue(new C.RetrieveTradeItem { From = Selected.ItemSlot, To = cell.ItemSlot });

                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                for (int x = 6; x < cell.ItemArray.Length; x++)
                                    if (cell.ItemArray[x] == null)
                                    {
                                        Network.Enqueue(new C.RetrieveTradeItem { From = Selected.ItemSlot, To = x });

                                        ItemSlotRef temp = Slot(MirGridType.Inventory, x);

                                        if (temp != null) temp.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                break;
                            #endregion
                            #region From AwakenItem
                            case MirGridType.AwakenItem: //From AwakenItem
                                Network.Enqueue(new C.MoveItem { Grid = cell.GridType, From = GameScene.Models.Npc.AwakeItemsIdx[Selected.ItemSlot], To = GameScene.Models.Npc.AwakeItemsIdx[Selected.ItemSlot] });
                                Selected.Locked = false;
                                Selected.Item = null;
                                GameScene.Models.Npc.AwakeItemsIdx[Selected.ItemSlot] = 0;

                                if (Selected.ItemSlot == 0)
                                    GameScene.Ui.ItemAwakeItemChanged();
                                Selected = null;
                                break;
                            #endregion
                            #region From Refine
                            case MirGridType.Refine: //From AwakenItem
                                if (cell.Item != null && Selected.Item.Info.Type == ItemType.Amulet)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }


                                if (cell.Item == null)
                                {
                                    Network.Enqueue(new C.RetrieveRefineItem { From = Selected.ItemSlot, To = cell.ItemSlot });

                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                for (int x = 6; x < cell.ItemArray.Length; x++)
                                    if (cell.ItemArray[x] == null)
                                    {
                                        Network.Enqueue(new C.RetrieveRefineItem { From = Selected.ItemSlot, To = x });

                                        ItemSlotRef temp = Slot(MirGridType.Inventory, x);

                                        if (temp != null) temp.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                break;
                            #endregion
                            #region From Item Renting Dialog

                            case MirGridType.Renting:
                                if (GameScene.User.RentalItemLocked)
                                {
                                    GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UnableRemoveLockedItem), ChatType.System);
                                    Selected = null;
                                    return;
                                }

                                if (cell.Item == null)
                                {
                                    Network.Enqueue(new C.RetrieveRentalItem { From = Selected.ItemSlot, To = cell.ItemSlot });

                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                break;
                            #endregion
                            #region From Hero Inventory
                            case MirGridType.HeroInventory:
                                if (GameScene.Hero == null || GameScene.Hero.Dead)
                                    return;
                                if (cell.Item != null && Selected.Item.Info.Type == ItemType.Amulet)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }


                                if (cell.Item == null)
                                {
                                    Network.Enqueue(new C.TakeBackHeroItem { From = Selected.ItemSlot, To = cell.ItemSlot });

                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                for (int x = 6; x < cell.ItemArray.Length; x++)
                                    if (cell.ItemArray[x] == null)
                                    {
                                        Network.Enqueue(new C.TakeBackHeroItem { From = Selected.ItemSlot, To = x });

                                        ItemSlotRef temp = Slot(MirGridType.Inventory, x);

                                        if (temp != null) temp.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                break;
                                #endregion
                        }
                        break;
                    #endregion
                    #region To Equipment
                    case MirGridType.Equipment: //To Equipment

                        if (Selected.GridType != MirGridType.Inventory && Selected.GridType != MirGridType.Storage) return;


                        if (cell.Item != null && Selected.Item.Info.Type == ItemType.Amulet)
                        {
                            if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                            {
                                Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                cell.Locked = true;
                                Selected.Locked = true;
                                Selected = null;
                                return;
                            }
                        }

                        if (CorrectSlot(cell, Selected.Item, Selected.GridType))
                        {
                            if (CanWearItem(cell, GameScene.User, Selected.Item))
                            {
                                Network.Enqueue(new C.EquipItem { Grid = Selected.GridType, UniqueID = Selected.Item.UniqueID, To = cell.ItemSlot });
                                cell.Locked = true;
                                Selected.Locked = true;
                            }
                            Selected = null;
                        }
                        return;
                    #endregion
                    #region To Storage
                    case MirGridType.Storage: //To Storage
                        switch (Selected.GridType)
                        {
                            #region From Inventory
                            case MirGridType.Inventory: //From Invenotry
                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }


                                if (cell.ItemArray[cell.ItemSlot] == null)
                                {
                                    Network.Enqueue(new C.StoreItem { From = Selected.ItemSlot, To = cell.ItemSlot });
                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                for (int x = 0; x < cell.ItemArray.Length; x++)
                                    if (cell.ItemArray[x] == null)
                                    {
                                        Network.Enqueue(new C.StoreItem { From = Selected.ItemSlot, To = x });

                                        ItemSlotRef temp = Slot(MirGridType.Storage, x);
                                        if (temp != null) temp.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                break;
                            #endregion
                            #region From Equipment
                            case MirGridType.Equipment: //From Equipment
                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        //Merge.
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                if (!CanRemoveItem(cell, Selected.Item))
                                {
                                    Selected = null;
                                    return;
                                }

                                if (cell.Item == null)
                                {
                                    Network.Enqueue(new C.RemoveItem { Grid = cell.GridType, UniqueID = Selected.Item.UniqueID, To = cell.ItemSlot });

                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                for (int x = 0; x < cell.ItemArray.Length; x++)
                                    if (cell.ItemArray[x] == null)
                                    {
                                        Network.Enqueue(new C.RemoveItem { Grid = cell.GridType, UniqueID = Selected.Item.UniqueID, To = x });

                                        ItemSlotRef temp = Slot(MirGridType.Storage, x);
                                        if (temp != null) temp.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                break;
                            #endregion
                            #region From Storage
                            case MirGridType.Storage:
                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        //Merge.
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                Network.Enqueue(new C.MoveItem { Grid = cell.GridType, From = Selected.ItemSlot, To = cell.ItemSlot });
                                cell.Locked = true;
                                Selected.Locked = true;
                                Selected = null;
                                return;
                                #endregion

                        }
                        break;

                    #endregion
                    #region To guild storage
                    case MirGridType.GuildStorage: //To Guild Storage
                        switch (Selected.GridType)
                        {
                            case MirGridType.GuildStorage: //From Guild Storage
                                if (Selected.GridType == MirGridType.GuildStorage)
                                {
                                    if (!GameScene.Models.Guild.MyOptions.HasFlag(GuildRankOptions.CanStoreItem))
                                    {
                                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.InsufficientRightsStoreItems), ChatType.System);
                                        return;
                                    }

                                    //if (ItemArray[cell.ItemSlot] == null)
                                    //{
                                    Network.Enqueue(new C.GuildStorageItemChange { Type = 2, From = Selected.ItemSlot, To = cell.ItemSlot });
                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                    //}
                                }
                                return;

                            case MirGridType.Inventory:

                                if (Selected.GridType == MirGridType.Inventory)
                                {
                                    if (cell.Item != null)
                                    {
                                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouCannotSwapItems), ChatType.System);
                                        return;
                                    }
                                    if (!GameScene.Models.Guild.MyOptions.HasFlag(GuildRankOptions.CanStoreItem))
                                    {
                                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.InsufficientRightsStoreItems), ChatType.System);
                                        return;
                                    }
                                    if (cell.ItemArray[cell.ItemSlot] == null)
                                    {
                                        Network.Enqueue(new C.GuildStorageItemChange { Type = 0, From = Selected.ItemSlot, To = cell.ItemSlot });
                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }
                                return;
                        }
                        break;
                    #endregion
                    #region To Trade

                    case MirGridType.Trade:
                        if (cell.Item != null && cell.Item.Info.Bind.HasFlag(BindMode.DontTrade)) return;

                        switch (Selected.GridType)
                        {
                            #region From Trade
                            case MirGridType.Trade: //From Trade
                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        //Merge.
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                Network.Enqueue(new C.MoveItem { Grid = cell.GridType, From = Selected.ItemSlot, To = cell.ItemSlot });

                                cell.Locked = true;
                                Selected.Locked = true;
                                Selected = null;
                                return;
                            #endregion

                            #region From Inventory
                            case MirGridType.Inventory: //From Inventory
                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }


                                if (cell.ItemArray[cell.ItemSlot] == null)
                                {
                                    Network.Enqueue(new C.DepositTradeItem { From = Selected.ItemSlot, To = cell.ItemSlot });
                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                for (int x = 0; x < cell.ItemArray.Length; x++)
                                    if (cell.ItemArray[x] == null)
                                    {
                                        Network.Enqueue(new C.DepositTradeItem { From = Selected.ItemSlot, To = x });

                                        ItemSlotRef temp = Slot(MirGridType.Trade, x);
                                        if (temp != null) temp.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                break;
                                #endregion
                        }
                        break;

                    #endregion
                    #region To Refine 

                    case MirGridType.Refine:

                        switch (Selected.GridType)
                        {
                            #region From Refine
                            case MirGridType.Refine: //From Refine
                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        //Merge.
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                Network.Enqueue(new C.MoveItem { Grid = cell.GridType, From = Selected.ItemSlot, To = cell.ItemSlot });
                                cell.Locked = true;
                                Selected.Locked = true;
                                Selected = null;
                                return;
                            #endregion

                            #region From Inventory
                            case MirGridType.Inventory: //From Inventory
                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                Network.Enqueue(new C.DepositRefineItem { From = Selected.ItemSlot, To = cell.ItemSlot });
                                cell.Locked = true;
                                Selected.Locked = true;
                                Selected = null;
                                return;
                                #endregion
                        }
                        break;

                    #endregion
                    #region To Item Renting Dialog

                    case MirGridType.Renting:
                        switch (Selected.GridType)
                        {
                            case MirGridType.Inventory:

                                if (cell.Item == null)
                                {
                                    Network.Enqueue(new C.DepositRentalItem { From = Selected.ItemSlot, To = cell.ItemSlot });
                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                break;
                        }

                        break;

                    #endregion
                    #region To Awakening
                    case MirGridType.AwakenItem:
                        {
                            int errorCode = 0;

                            if (Selected.GridType != MirGridType.Inventory && cell.ItemSlot < 1) return;

                            switch (cell.ItemSlot)
                            {
                                //baseitem
                                case 0:
                                    {
                                        if ((Selected.Item.Info.Type == ItemType.Weapon ||
                                            Selected.Item.Info.Type == ItemType.Helmet ||
                                            Selected.Item.Info.Type == ItemType.Armour) &&
                                            Selected.Item.Info.Grade != ItemGrade.None &&
                                            cell.ItemSlot == 0)
                                        {
                                            if (cell.Item == null)
                                            {
                                                cell.Item = Selected.Item;
                                                Selected.Locked = true;
                                                GameScene.Models.Npc.AwakeItemsIdx[cell.ItemSlot] = Selected.ItemSlot;
                                            }
                                            else
                                            {
                                                Network.Enqueue(new C.AwakeningLockedItem { UniqueID = cell.Item.UniqueID, Locked = false });

                                                cell.Item = Selected.Item;
                                                Selected.Locked = true;
                                                GameScene.Models.Npc.AwakeItemsIdx[cell.ItemSlot] = Selected.ItemSlot;
                                            }
                                            GameScene.Ui.ItemAwakeItemChanged();
                                            GameScene.Ui.ItemAwakeTypeSelect(0);
                                        }
                                        else
                                        {
                                            errorCode = -2;
                                        }
                                    }
                                    break;
                                //view materials
                                case 1:
                                case 2:
                                    break;
                                //materials
                                case 3:
                                case 4:
                                    {
                                        switch (Selected.GridType)
                                        {
                                            case MirGridType.Inventory:
                                                {
                                                    if (Selected.Item.Info.Type == ItemType.Awakening &&
                                                        Selected.Item.Info.Shape < 200 && GameScene.Models.Npc.AwakeItemsIdx[cell.ItemSlot] == 0)
                                                    {
                                                        cell.Item = Selected.Item;
                                                        Selected.Locked = true;
                                                        GameScene.Models.Npc.AwakeItemsIdx[cell.ItemSlot] = Selected.ItemSlot;
                                                    }
                                                    else
                                                    {
                                                        errorCode = -2;
                                                    }
                                                }
                                                break;
                                            case MirGridType.AwakenItem:
                                                {
                                                    if (Selected.ItemSlot == cell.ItemSlot || Selected.ItemSlot == 0)
                                                    {
                                                        cell.Locked = false;
                                                        Selected = null;
                                                    }
                                                    else
                                                    {
                                                        Selected.Locked = false;
                                                        cell.Locked = false;

                                                        int beforeIdx = GameScene.Models.Npc.AwakeItemsIdx[Selected.ItemSlot];
                                                        GameScene.Models.Npc.AwakeItemsIdx[Selected.ItemSlot] = GameScene.Models.Npc.AwakeItemsIdx[cell.ItemSlot];
                                                        GameScene.Models.Npc.AwakeItemsIdx[cell.ItemSlot] = beforeIdx;

                                                        UserItem item = Selected.Item;
                                                        Selected.Item = cell.Item;
                                                        cell.Item = item;
                                                        Selected = null;
                                                    }
                                                }
                                                break;
                                        }

                                    }
                                    break;
                                //SuccessRateUpItem or RandomValueUpItem or CancelDestroyedItem etc.
                                //AllCashItem Korea Server Not Implementation.
                                case 5:
                                case 6:
                                    if (Selected.Item.Info.Type == ItemType.Awakening &&
                                            Selected.Item.Info.Shape == 200)
                                    {
                                        cell.Item = Selected.Item;
                                        Selected.Locked = true;
                                        GameScene.Models.Npc.AwakeItemsIdx[cell.ItemSlot] = Selected.ItemSlot;
                                    }
                                    else
                                    {
                                        errorCode = -2;
                                    }
                                    break;
                                default:
                                    break;
                            }

                            Selected = null;

                            switch (errorCode)
                            {
                                //case -1:
                                //    messageBox = new MirMessageBox("Item must be in your inventory.", MirMessageBoxButtons.OK);
                                //    messageBox.Show();
                                //    break;
                                case -2:
                                    //messageBox = new MirMessageBox("Cannot awaken this item.", MirMessageBoxButtons.OK);
                                    //messageBox.Show();
                                    break;
                            }
                        }
                        return;
                    #endregion
                    #region To Mail
                    case MirGridType.Mail: //To Mail
                        if (Selected.GridType == MirGridType.Inventory)
                        {
                            if (cell.Item != null)
                            {
                                GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouCannotSwapItems), ChatType.System);
                                return;
                            }

                            if (Selected.Item.Info.Bind.HasFlag(BindMode.DontTrade))
                            {
                                GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouCannotMailItem), ChatType.System);
                                return;
                            }

                            if (cell.ItemArray[cell.ItemSlot] == null)
                            {
                                cell.Item = Selected.Item;
                                Selected.Locked = true;
                                GameScene.Models.Mail.ParcelItemsIdx[cell.ItemSlot] = Selected.Item.UniqueID;
                                Selected = null;
                                GameScene.Ui.ItemMailPostageChanged();

                                return;
                            }
                        }
                        break;
                    #endregion
                    #region To Hero Inventory
                    case MirGridType.HeroInventory:
                        if (GameScene.Hero == null || GameScene.Hero.Dead)
                            return;
                        switch (Selected.GridType)
                        {
                            #region From Hero Inventory
                            case MirGridType.HeroInventory:
                                if (cell.Item != null)
                                {
                                    if (CMain.Ctrl)
                                    {
                                        GameScene.Ui.ItemAskYesNo(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouWantCombineItems), () =>
                                        {
                                            //Combine
                                            Network.Enqueue(new C.CombineItem { Grid = Selected.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });
                                            cell.Locked = true;
                                            Selected.Locked = true;
                                            Selected = null;
                                        });

                                        return;
                                    }

                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        //Merge
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                Network.Enqueue(new C.MoveItem { Grid = cell.GridType, From = Selected.ItemSlot, To = cell.ItemSlot });

                                cell.Locked = true;
                                Selected.Locked = true;
                                Selected = null;
                                return;
                            #endregion
                            #region From Hero Equipment
                            case MirGridType.HeroEquipment:
                                if (cell.Item != null && Selected.Item.Info.Type == ItemType.Amulet)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                if (!CanRemoveItem(cell, Selected.Item))
                                {
                                    Selected = null;
                                    return;
                                }
                                if (cell.Item == null)
                                {
                                    Network.Enqueue(new C.RemoveItem { Grid = cell.GridType, UniqueID = Selected.Item.UniqueID, To = cell.ItemSlot });

                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                for (int x = 2; x < cell.ItemArray.Length; x++)
                                    if (cell.ItemArray[x] == null)
                                    {
                                        Network.Enqueue(new C.RemoveItem { Grid = cell.GridType, UniqueID = Selected.Item.UniqueID, To = x });

                                        ItemSlotRef temp = Slot(MirGridType.HeroInventory, x);

                                        if (temp != null) temp.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                break;
                            #endregion
                            #region From Inventory
                            case MirGridType.Inventory:
                                if (cell.Item != null && Selected.Item.Info.Type == ItemType.Amulet)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }

                                if (Selected.Item.Weight + MapObject.Hero.CurrentBagWeight > MapObject.Hero.Stats[Stat.BagWeight])
                                {
                                    GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TooHeavyToTransfer), ChatType.System);
                                    Selected = null;
                                    return;
                                }

                                if (cell.Item != null)
                                {
                                    if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                                    {
                                        Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                        cell.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                }


                                if (cell.Item == null)
                                {
                                    Network.Enqueue(new C.TransferHeroItem { From = Selected.ItemSlot, To = cell.ItemSlot });

                                    cell.Locked = true;
                                    Selected.Locked = true;
                                    Selected = null;
                                    return;
                                }

                                for (int x = 2; x < cell.ItemArray.Length; x++)
                                    if (cell.ItemArray[x] == null)
                                    {
                                        Network.Enqueue(new C.TransferHeroItem { From = Selected.ItemSlot, To = x });

                                        ItemSlotRef temp = Slot(MirGridType.HeroInventory, x);

                                        if (temp != null) temp.Locked = true;
                                        Selected.Locked = true;
                                        Selected = null;
                                        return;
                                    }
                                break;
                                #endregion
                        }
                        break;
                    #endregion
                    #region To Hero Equipment
                    case MirGridType.HeroEquipment:

                        if (Selected.GridType != MirGridType.HeroInventory) return;
                        if (GameScene.Hero == null || GameScene.Hero.Dead)
                            return;

                        if (cell.Item != null && Selected.Item.Info.Type == ItemType.Amulet)
                        {
                            if (Selected.Item.Info == cell.Item.Info && cell.Item.Count < cell.Item.Info.StackSize)
                            {
                                Network.Enqueue(new C.MergeItem { GridFrom = Selected.GridType, GridTo = cell.GridType, IDFrom = Selected.Item.UniqueID, IDTo = cell.Item.UniqueID });

                                cell.Locked = true;
                                Selected.Locked = true;
                                Selected = null;
                                return;
                            }
                        }

                        if (CorrectSlot(cell, Selected.Item, Selected.GridType))
                        {
                            if (CanWearItem(cell, GameScene.Hero, Selected.Item))
                            {
                                Network.Enqueue(new C.EquipItem { Grid = Selected.GridType, UniqueID = Selected.Item.UniqueID, To = cell.ItemSlot });
                                cell.Locked = true;
                                Selected.Locked = true;
                            }
                            Selected = null;
                        }
                        return;
                    #endregion
                    #region To Hero AutoPot
                    case MirGridType.HeroHPItem:
                    case MirGridType.HeroMPItem:
                        if (Selected.GridType != MirGridType.HeroInventory) return;
                        if (GameScene.Hero == null || GameScene.Hero.Dead)
                            return;
                        if (Selected.Item.Info.Type != ItemType.Potion || Selected.Item.Info.Shape > 1)
                            return;

                        Network.Enqueue(new C.SetAutoPotItem { Grid = cell.GridType, ItemIndex = Selected.Item.Info.Index });
                        Selected = null;

                        return;
                        #endregion
                }

                return;
            }

            if (cell.Item != null)
            {
                Selected = cell;
            }
        }
        private static void PlayItemSound(ItemSlotRef cell)
        {
            if (cell.Item == null) return;

            switch (cell.Item.Info.Type)
            {
                case ItemType.Weapon:
                    SoundManager.PlaySound(SoundList.ClickWeapon);
                    break;
                case ItemType.Armour:
                    SoundManager.PlaySound(SoundList.ClickArmour);
                    break;
                case ItemType.Helmet:
                    SoundManager.PlaySound(SoundList.ClickHelmet);
                    break;
                case ItemType.Necklace:
                    SoundManager.PlaySound(SoundList.ClickNecklace);
                    break;
                case ItemType.Bracelet:
                    SoundManager.PlaySound(SoundList.ClickBracelet);
                    break;
                case ItemType.Ring:
                    SoundManager.PlaySound(SoundList.ClickRing);
                    break;
                case ItemType.Boots:
                    SoundManager.PlaySound(SoundList.ClickBoots);
                    break;
                case ItemType.Potion:
                    SoundManager.PlaySound(SoundList.ClickDrug);
                    break;
                default:
                    SoundManager.PlaySound(SoundList.ClickItem);
                    break;
            }
        }

        private static int FreeSpace(ItemSlotRef cell)
        {
            int count = 0;

            for (int i = 0; i < cell.ItemArray.Length; i++)
                if (cell.ItemArray[i] == null) count++;

            return count;
        }


        private static bool CanRemoveItem(ItemSlotRef cell, UserItem i)
        {
            if(MapObject.User.RidingMount && i.Info.Type != ItemType.Torch)
            {
                return false;
            }
            //stuck
            return FreeSpace(cell) > 0;
        }

        private static bool CorrectSlot(ItemSlotRef cell, UserItem i, MirGridType grid)
        {
            ItemType type = i.Info.Type;

            switch (cell.GridType)
            {
                case MirGridType.Equipment:
                    if (grid != MirGridType.Inventory && grid != MirGridType.Storage)
                        return false;
                    break;
                case MirGridType.HeroEquipment:
                    if (grid != MirGridType.HeroInventory)
                        return false;
                    break;                
            }

            switch ((EquipmentSlot)cell.ItemSlot)
            {
                case EquipmentSlot.Weapon:
                    return type == ItemType.Weapon;
                case EquipmentSlot.Armour:
                    return type == ItemType.Armour;
                case EquipmentSlot.Helmet:
                    return type == ItemType.Helmet;
                case EquipmentSlot.Torch:
                    return type == ItemType.Torch;
                case EquipmentSlot.Necklace:
                    return type == ItemType.Necklace;
                case EquipmentSlot.BraceletL:
                    return i.Info.Type == ItemType.Bracelet;
                case EquipmentSlot.BraceletR:
                    return i.Info.Type == ItemType.Bracelet || i.Info.Type == ItemType.Amulet;
                case EquipmentSlot.RingL:
                case EquipmentSlot.RingR:
                    return type == ItemType.Ring;
                case EquipmentSlot.Amulet:
                    return type == ItemType.Amulet;// && i.Info.Shape > 0;
                case EquipmentSlot.Boots:
                    return type == ItemType.Boots;
                case EquipmentSlot.Belt:
                    return type == ItemType.Belt;
                case EquipmentSlot.Stone:
                    return type == ItemType.Stone;
                case EquipmentSlot.Mount:
                    return type == ItemType.Mount;
                default:
                    return false;
            }

        }
        private static bool CanUseItem(ItemSlotRef cell)
        {
            if (cell.Item == null) return false;

            UserObject actor = GameScene.User;
            if (HeroGridType(cell))
                actor = GameScene.Hero;

            switch (actor.Gender)
            {
                case MirGender.Male:
                    if (!cell.Item.Info.RequiredGender.HasFlag(RequiredGender.Male))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.NotFemale), ChatType.System);
                        return false;
                    }
                    break;
                case MirGender.Female:
                    if (!cell.Item.Info.RequiredGender.HasFlag(RequiredGender.Female))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.NotMale), ChatType.System);
                        return false;
                    }
                    break;
            }

            switch (actor.Class)
            {
                case MirClass.Warrior:
                    if (!cell.Item.Info.RequiredClass.HasFlag(RequiredClass.Warrior))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.WarriorsCannotUseItem), ChatType.System);
                        return false;
                    }
                    break;
                case MirClass.Wizard:
                    if (!cell.Item.Info.RequiredClass.HasFlag(RequiredClass.Wizard))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.WizardsCannotUseItem), ChatType.System);
                        return false;
                    }
                    break;
                case MirClass.Taoist:
                    if (!cell.Item.Info.RequiredClass.HasFlag(RequiredClass.Taoist))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TaoistsCannotUseItem), ChatType.System);
                        return false;
                    }
                    break;
                case MirClass.Assassin:
                    if (!cell.Item.Info.RequiredClass.HasFlag(RequiredClass.Assassin))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AssassinsCannotUseItem), ChatType.System);
                        return false;
                    }
                    break;
                case MirClass.Archer:
                    if (!cell.Item.Info.RequiredClass.HasFlag(RequiredClass.Archer))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ArchersCannotUseItem), ChatType.System);
                        return false;
                    }
                    break;
            }

            switch (cell.Item.Info.RequiredType)
            {
                case RequiredType.Level:
                    if (actor.Level < cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowLevel), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxAC:
                    if (actor.Stats[Stat.MaxAC] < cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowAC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxMAC:
                    if (actor.Stats[Stat.MaxMAC] < cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowMAC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxDC:
                    if (actor.Stats[Stat.MaxDC] < cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowDC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxMC:
                    if (actor.Stats[Stat.MaxMC] < cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowMC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxSC:
                    if (actor.Stats[Stat.MaxSC] < cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowSC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxLevel:
                    if (actor.Level > cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouExceededMaxLevel), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MinAC:
                    if (actor.Stats[Stat.MinAC] < cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouDoNotHaveEnoughBaseAC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MinMAC:
                    if (actor.Stats[Stat.MinMAC] < cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouDoNotHaveEnoughBaseMac), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MinDC:
                    if (actor.Stats[Stat.MinDC] < cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouLackBaseDC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MinMC:
                    if (actor.Stats[Stat.MinMC] < cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouLackBaseMC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MinSC:
                    if (actor.Stats[Stat.MinSC] < cell.Item.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouLackBaseSC), ChatType.System);
                        return false;
                    }
                    break;
            }

            switch (cell.Item.Info.Type)
            {
                case ItemType.Saddle:
                case ItemType.Ribbon:
                case ItemType.Bells:
                case ItemType.Mask:
                case ItemType.Reins:
                    if (actor.Equipment[(int)EquipmentSlot.Mount] == null)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouDoNotHaveMountEquipped), ChatType.System);
                        return false;
                    }
                    break;
                case ItemType.Hook:
                case ItemType.Float:
                case ItemType.Bait:
                case ItemType.Finder:
                case ItemType.Reel:
                    if (actor.Equipment[(int)EquipmentSlot.Weapon] == null || !actor.Equipment[(int)EquipmentSlot.Weapon].Info.IsFishingRod)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouDoNotHaveFishingRodEquipped), ChatType.System);
                        return false;
                    }
                    break;
            }
            return true;
        }

        private static bool CanWearItem(ItemSlotRef cell, UserObject actor, UserItem i)
        {
            if (i == null) return false;

            if (actor == GameScene.Hero && actor.Dead)
                return false;

            //If Can remove;

            switch (actor.Gender)
            {
                case MirGender.Male:
                    if (!i.Info.RequiredGender.HasFlag(RequiredGender.Male))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.NotFemale), ChatType.System);
                        return false;
                    }
                    break;
                case MirGender.Female:
                    if (!i.Info.RequiredGender.HasFlag(RequiredGender.Female))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.NotMale), ChatType.System);
                        return false;
                    }
                    break;
            }

            switch (actor.Class)
            {
                case MirClass.Warrior:
                    if (!i.Info.RequiredClass.HasFlag(RequiredClass.Warrior))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.WarriorsCannotUseItem), ChatType.System);
                        return false;
                    }
                    break;
                case MirClass.Wizard:
                    if (!i.Info.RequiredClass.HasFlag(RequiredClass.Wizard))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.WizardsCannotUseItem), ChatType.System);
                        return false;
                    }
                    break;
                case MirClass.Taoist:
                    if (!i.Info.RequiredClass.HasFlag(RequiredClass.Taoist))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TaoistsCannotUseItem), ChatType.System);
                        return false;
                    }
                    break;
                case MirClass.Assassin:
                    if (!i.Info.RequiredClass.HasFlag(RequiredClass.Assassin))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AssassinsCannotUseItem), ChatType.System);
                        return false;
                    }
                    break;
                case MirClass.Archer:
                    if (!i.Info.RequiredClass.HasFlag(RequiredClass.Archer))
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ArchersCannotUseItem), ChatType.System);
                        return false;
                    }
                    break;
            }

            switch (i.Info.RequiredType)
            {
                case RequiredType.Level:
                    if (actor.Level < i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowLevel), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxAC:
                    if (actor.Stats[Stat.MaxAC] < i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowAC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxMAC:
                    if (actor.Stats[Stat.MaxMAC] < i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowMAC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxDC:
                    if (actor.Stats[Stat.MaxDC] < i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowDC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxMC:
                    if (actor.Stats[Stat.MaxMC] < i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowMC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxSC:
                    if (actor.Stats[Stat.MaxSC] < i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowSC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MaxLevel:
                    if (actor.Level > i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouExceededMaxLevel), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MinAC:
                    if (actor.Stats[Stat.MinAC] < i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouDoNotHaveEnoughBaseAC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MinMAC:
                    if (actor.Stats[Stat.MinMAC] < i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouDoNotHaveEnoughBaseMAC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MinDC:
                    if (actor.Stats[Stat.MinDC] < i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouNotEnoughBaseDC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MinMC:
                    if (actor.Stats[Stat.MinMC] < i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouNotEnoughBaseMC), ChatType.System);
                        return false;
                    }
                    break;
                case RequiredType.MinSC:
                    if (actor.Stats[Stat.MinSC] < i.Info.RequiredAmount)
                    {
                        GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouDoNotHaveEnoughBaseSC), ChatType.System);
                        return false;
                    }
                    break;
            }

            if (i.Info.Type == ItemType.Weapon || i.Info.Type == ItemType.Torch)
            {
                if (i.Weight - (cell.Item != null ? cell.Item.Weight : 0) + actor.CurrentHandWeight > actor.Stats[Stat.HandWeight])
                {
                    GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.TooHeavyToHold), ChatType.System);
                    return false;
                }
            }
            else
            {
                if (i.Weight - (cell.Item != null ? cell.Item.Weight : 0) + actor.CurrentWearWeight > actor.Stats[Stat.WearWeight])
                {
                    GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ItIsTooHeavyToWear), ChatType.System);
                    return false;
                }
            }

            switch (i.Info.Type)
            {
                case ItemType.Bait:
                case ItemType.Finder:
                case ItemType.Hook:
                case ItemType.Reel:
                case ItemType.Float:
                    if (!actor.HasFishingRod)
                    {
                        return false;
                    }
                    break;
                case ItemType.Bells:
                case ItemType.Reins:
                case ItemType.Ribbon:
                case ItemType.Saddle:
                    if (actor.MountType < 0)
                    {
                        return false;
                    }
                    break;
                case ItemType.Socket:
                    if (GameScene.SelectedItem == null || GameScene.SelectedItem.Info.Type == ItemType.Mount || (GameScene.SelectedItem.Info.Type == ItemType.Weapon && GameScene.SelectedItem.Info.IsFishingRod))
                    {
                        return false;
                    }
                    break;
            }

            return true;
        }
    }
}

using Client.MirGraphics;
using Client.MirObjects;
using Client.MirScenes;
using Client.MirScenes.Models;

namespace Client.MirControls
{
    /// <summary>
    /// The classic item slot control: a thin view of one slot (Desktop-Modern-UI-Plan seam step 3). The slot's item, lock and
    /// pick state live in <see cref="ItemMoves"/> (looked up by GridType + ItemSlot), and the click logic that used to sit here
    /// is ItemMoves' (ItemMoves.Logic.cs); the cell redraws on <see cref="ItemMoves.SlotChanged"/> for its slot.
    /// </summary>
    public sealed class MirItemCell : MirImageControl
    {
        public UserItem Item
        {
            get
            {
                return ItemMoves.GetItem(GridType, _itemSlot);
            }
            set
            {
                ItemMoves.SetItem(GridType, _itemSlot, value);

                SetEffect();
                Redraw();
            }
        }

        public UserItem ShadowItem
        {
            get
            {
                return ItemMoves.GetShadowItem(GridType, _itemSlot);
            }
        }

        public UserItem[] ItemArray
        {
            get
            {
                return ItemMoves.GetArray(GridType);
            }
        }

        public override bool Border
        {
            get { return (ItemMoves.IsSelected(GridType, _itemSlot) || MouseControl == this || Locked) && !(GridType == MirGridType.DropPanel || GridType == MirGridType.Craft); }
        }

        public bool Locked
        {
            get { return ItemMoves.IsLocked(GridType, _itemSlot); }
            set { ItemMoves.SetLocked(GridType, _itemSlot, value); }
        }

        /// <summary>This cell's slot as an ItemSlotRef (what ItemMoves works on).</summary>
        public ItemSlotRef Slot => new ItemSlotRef(GridType, _itemSlot);

        private readonly Action<ItemSlotKey> _slotChanged;



        #region GridType

        private MirGridType _gridType;
        public event EventHandler GridTypeChanged;
        public MirGridType GridType
        {
            get { return _gridType; }
            set
            {
                if (_gridType == value) return;
                _gridType = value;
                OnGridTypeChanged();
            }
        }

        private void OnGridTypeChanged()
        {
            if (GridTypeChanged != null)
                GridTypeChanged.Invoke(this, EventArgs.Empty);
        }
        #endregion

        #region ItemSlot

        private int _itemSlot;
        public event EventHandler ItemSlotChanged;
        public int ItemSlot
        {
            get { return _itemSlot; }
            set
            {
                if (_itemSlot == value) return;
                _itemSlot = value;
                OnItemSlotChanged();
            }
        }

        private void OnItemSlotChanged()
        {
            if (ItemSlotChanged != null)
                ItemSlotChanged.Invoke(this, EventArgs.Empty);
        }
        #endregion

        #region Count Label

        private MirLabel CountLabel { get; set; }

        #endregion

        public MirItemCell()
        {
            Size = new Size(36, 32);
            GridType = MirGridType.None;
            DrawImage = false;

            BorderColour = Color.Lime;

            BackColour = Color.FromArgb(255, 255, 125, 125);
            Opacity = 0.5F;
            DrawControlTexture = true;
            Library = Libraries.Items;

            _slotChanged = OnSlotChanged;
            ItemMoves.SlotChanged.On(_slotChanged);
        }

        public void SetEffect()
        {
            //put effect stuff here??
        }


        public override void OnMouseClick(MouseEventArgs e)
        {
            ItemSlotRef slot = Slot;
            if (!ItemMoves.BeginClick(slot, e)) return;

            base.OnMouseClick(e);
            
            Redraw();

            ItemMoves.Click(slot, e);
        }
        public override void OnMouseDoubleClick(MouseEventArgs e)
        {
            ItemSlotRef slot = Slot;
            if (!ItemMoves.BeginDoubleClick(slot)) return;

            base.OnMouseClick(e);

            Redraw();

            ItemMoves.DoubleClick(slot);
        }

        /// <summary>ItemMoves.SlotChanged: redraw when the change is this cell's slot (or its whole grid).</summary>
        private void OnSlotChanged(ItemSlotKey key)
        {
            if (IsDisposed || key.Grid != GridType) return;
            if (key.Slot != -1 && key.Slot != _itemSlot && !ItemMoves.IsSingleSlot(key.Grid)) return;

            Redraw();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                ItemMoves.SlotChanged.Off(_slotChanged);

            base.Dispose(disposing);
        }


        protected internal override void DrawControl()
        {
            if (Item != null && !ItemMoves.IsSelected(GridType, _itemSlot) && Locked != true)
            {
                CreateDisposeLabel();

                if (Library != null)
                {
                    ushort image = Item.Image;

                    Size imgSize = Library.GetTrueSize(image);

                    Point offSet = new Point((Size.Width - imgSize.Width) / 2, (Size.Height - imgSize.Height) / 2);

                    if (GridType == MirGridType.Craft)
                    {
                        Libraries.Prguse.Draw(1121, DisplayLocation.Add(new Point(-2, -1)), Color.White, UseOffSet, 0.8F);
                    }

                    Library.Draw(image, DisplayLocation.Add(offSet), ForeColour, UseOffSet, 1F);

                    if (Item.SealedInfo != null && Item.SealedInfo.ExpiryDate > CMain.Now)
                    {
                        Libraries.StateItems.Draw(3590, DisplayLocation.Add(new Point(2, 2)), Color.White, UseOffSet, 1F);
                    }
                }
            }
            else if (Item != null && (ItemMoves.IsSelected(GridType, _itemSlot) || Locked))
            {
                CreateDisposeLabel();

                if (Library != null)
                {
                    ushort image = Item.Image;

                    Size imgSize = Library.GetTrueSize(image);

                    Point offSet = new Point((Size.Width - imgSize.Width) / 2, (Size.Height - imgSize.Height) / 2);

                    Library.Draw(image, DisplayLocation.Add(offSet), Color.DimGray, UseOffSet, 0.8F);
                }
            }
            else if (ShadowItem != null)
            {
                CreateDisposeLabel();

                if (Library != null)
                {
                    ushort image = ShadowItem.Info.Image;

                    Size imgSize = Library.GetTrueSize(image);

                    Point offSet = new Point((Size.Width - imgSize.Width) / 2, (Size.Height - imgSize.Height) / 2);

                    if (GridType == MirGridType.Craft)
                    {
                        Libraries.Prguse.Draw(1121, DisplayLocation.Add(new Point(-2, -1)), Color.White, UseOffSet, 0.8F);
                    }

                    Library.Draw(image, DisplayLocation.Add(offSet), Color.DimGray, UseOffSet, 0.8F);
                }
            }
            else
                DisposeCountLabel();
        }

        protected override void OnMouseEnter()
        {
            base.OnMouseEnter();
            if (GridType == MirGridType.Inspect)
                GameScene.Scene.CreateItemLabel(Item, true);
            else
            {
                if (Item != null)
                    GameScene.Scene.CreateItemLabel(Item);
                else if (ShadowItem != null)
                    GameScene.Scene.CreateItemLabel(ShadowItem, false, ShadowItem.CurrentDura == ShadowItem.MaxDura);
            }
        }
        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            GameScene.Scene.DisposeItemLabel();
            GameScene.HoverItem = null;
        }

        private void CreateDisposeLabel()
        {
            if (Item == null && ShadowItem == null)
                return;

            if (Item != null && ShadowItem == null && Item.Info.StackSize <= 1)
            {
                DisposeCountLabel();
                return;
            }

            if (CountLabel == null || CountLabel.IsDisposed)
            {
                CountLabel = new MirLabel
                {
                    AutoSize = true,
                    ForeColour = Color.Yellow,
                    NotControl = true,
                    OutLine = false,
                    Parent = this,
                };
            }

            if (ShadowItem != null)
            {
                CountLabel.ForeColour = (Item == null || ShadowItem.Count > Item.Count) ? Color.Red : Color.LimeGreen;
                CountLabel.Text = string.Format("{0}/{1}", Item == null ? 0 : Item.Count, ShadowItem.Count);
            }
            else
            {
                CountLabel.Text = Item.Count.ToString("###0");
            }

            CountLabel.Location = new Point(Size.Width - CountLabel.Size.Width, Size.Height - CountLabel.Size.Height);
        }
        private void DisposeCountLabel()
        {
            if (CountLabel != null && !CountLabel.IsDisposed)
                CountLabel.Dispose();
            CountLabel = null;
        }
    }
}

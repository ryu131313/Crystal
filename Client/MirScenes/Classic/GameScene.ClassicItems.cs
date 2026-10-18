using Client.MirControls;
using Client.MirScenes.Dialogs;

namespace Client.MirScenes
{
    /// <summary>
    /// The classic UI half of the item logic (Desktop-Modern-UI-Plan seam step 3): the dialog code moved verbatim out of
    /// MirItemCell (now ItemMoves.Logic.cs) and out of GameScene's item reply handlers, called only through
    /// Classic.ClassicUi (IGameUi.Items.cs). The data half runs in ItemMoves / GameScene.cs.
    /// </summary>
    public sealed partial class GameScene
    {
        // ---- Item cells (was MirItemCell) ----

        internal bool ClassicItemDeleteModeClick(ItemSlotRef cell, MouseButtons button)
        {
            var invDlg = GameScene.Scene.InventoryDialog;
            if (invDlg.DeleteMode)
            {
                // Right-click anywhere on a cell cancels the toggle
                if (button == MouseButtons.Right)
                {
                    invDlg.ToggleDeleteMode(false);
                    return true;
                }

                // Left-click on an inventory item prompts delete
                if (button == MouseButtons.Left &&
                    cell.GridType == MirGridType.Inventory &&
                    cell.Item != null)
                {
                    invDlg.PromptDelete(cell);
                    return true;
                }
                return true;
            }

            return false;
        }

        internal void ClassicItemChatLink(ItemSlotRef cell)
        {
            string text = string.Format("<{0}> ", cell.Item.FriendlyName);

            if (GameScene.Scene.ChatDialog.ChatTextBox.Text.Length + text.Length > Globals.MaxChatLength)
            {
                GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.UnableLinkItemMessageTooLong), ChatType.System);
                return;
            }

            GameScene.Scene.ChatDialog.LinkedItems.Add(new ChatItem { UniqueID = cell.Item.UniqueID, Title = cell.Item.FriendlyName, Grid = cell.GridType });
            GameScene.Scene.ChatDialog.SetChatText(text);
        }

        internal bool ClassicIsNpcDropOpen => GameScene.Scene.NPCDropDialog.Visible;

        internal void ClassicItemNpcDropQuickSell(MouseEventArgs e)
        {
            GameScene.Scene.NPCDropDialog.ItemCell.OnMouseClick(e); // emulate click to drop control
            GameScene.Scene.NPCDropDialog.ConfirmButton.OnMouseClick(e); //emulate OK to confirm trade
        }

        internal void ClassicItemAskAmount(string title, int image, uint max, Action<uint> ok)
        {
            MirAmountBox amountBox = new MirAmountBox(title, image, max);

            amountBox.OKButton.Click += (o, a) =>
            {
                ok(amountBox.Amount);
            };

            amountBox.Show();
        }

        internal void ClassicItemAskAmountMessage(string title, int image, string message, Action ok)
        {
            MirAmountBox amountBox = new MirAmountBox(title, image, message);

            amountBox.OKButton.Click += (o, a) =>
            {
                ok();
            };

            amountBox.Show();
        }

        internal void ClassicItemAskYesNo(string message, Action yes)
        {
            MirMessageBox messageBox = new MirMessageBox(message, MirMessageBoxButtons.YesNo);
            messageBox.YesButton.Click += (o, a) =>
            {
                yes();
            };

            messageBox.Show();
        }

        internal void ClassicItemMessage(string message)
        {
            MirMessageBox messageBox = new MirMessageBox(message, MirMessageBoxButtons.OK);
            messageBox.Show();
        }

        internal void ClassicItemShowSockets(MirGridType grid, UserItem item)
        {
            GameScene.Scene.SocketDialog.Show(grid, item);
        }

        internal int ClassicItemFreeSocketSlot()
        {
            MirItemCell cell = null;
            for (int i = 0; i < GameScene.Scene.SocketDialog.Grid.Length; i++)
            {
                if (!GameScene.Scene.SocketDialog.Grid[i].Visible || GameScene.Scene.SocketDialog.Grid[i].Item != null) continue;
                cell = GameScene.Scene.SocketDialog.Grid[i];
                break;
            }

            return cell != null ? cell.ItemSlot : -1;
        }

        internal void ClassicItemAwakeItemChanged()
        {
            GameScene.Scene.NPCAwakeDialog.ItemCell_Click();
        }

        internal void ClassicItemAwakeTypeSelect(int index)
        {
            GameScene.Scene.NPCAwakeDialog.OnAwakeTypeSelect(index);
        }

        internal void ClassicItemMailPostageChanged()
        {
            GameScene.Scene.MailComposeParcelDialog.CalculatePostage();
        }

        // ---- Item reply handlers (was GameScene.cs) ----

        internal void ClassicItemTradeUnlocked()
        {
            TradeDialog.ChangeLockState(false);
        }

        internal void ClassicItemDuraRefresh()
        {
            CharacterDuraPanel.GetCharacterDura();
        }

        internal void ClassicItemDuraUpdate(UserItem item)
        {
            CharacterDuraPanel.UpdateCharacterDura(item);
        }

        internal void ClassicItemAwakeningClosing()
        {
            if (NPCAwakeDialog.Visible)
                NPCAwakeDialog.Hide();
            if (InventoryDialog.Visible)
                InventoryDialog.Hide();

            InventoryDialog.Location = new Point(0, 0);
        }

        internal void ClassicItemAwakeningResult(int result)
        {
            MirMessageBox messageBox = null;

            switch (result)
            {
                case -4:
                    messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.NotEnoughMaterials), MirMessageBoxButtons.OK);
                    break;
                case -3:
                    messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.LowGold), MirMessageBoxButtons.OK);
                    break;
                case -2:
                    messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AwakeningMaxLevel), MirMessageBoxButtons.OK);
                    break;
                case -1:
                    messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.CannotAwakenItem), MirMessageBoxButtons.OK);
                    break;
                case 0:
                    //messageBox = new MirMessageBox("Upgrade Failed.", MirMessageBoxButtons.OK);
                    break;
                case 1:
                    //messageBox = new MirMessageBox("Upgrade Success.", MirMessageBoxButtons.OK);
                    break;

            }

            if (messageBox != null) messageBox.Show();
        }

        internal void ClassicItemMailSent()
        {
            GameScene.Scene.MailComposeParcelDialog.Hide();
        }

        internal void ClassicItemRentalDeposited()
        {
            if (ItemRentingDialog.RentalPeriod == 0)
                ItemRentingDialog.InputRentalPeroid();
        }
    }
}

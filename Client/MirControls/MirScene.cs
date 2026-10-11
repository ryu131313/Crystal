using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirScenes;
using Client.MirGraphics.Rendering;
using S = ServerPackets;

namespace Client.MirControls
{
    public abstract class MirScene : MirControl
    {
        public static MirScene ActiveScene = new LoginScene();

        private static MouseButtons _buttons;
        private static long _lastClickTime;
        private static MirControl _clickedControl;

        protected MirScene()
        {
            DrawControlTexture = true;
            BackColour = Color.Magenta;
            Size = new Size(Settings.ScreenWidth, Settings.ScreenHeight);
        }

        public override sealed Size Size
        {
            get { return base.Size; }
            set { base.Size = value; }
        }

        public override void Draw()
        {
            if (IsDisposed || !Visible)
                return;

            OnBeforeShown();

            DrawControl();

            if (CMain.DebugBaseLabel != null && !CMain.DebugBaseLabel.IsDisposed)
                CMain.DebugBaseLabel.Draw();

            if (CMain.HintBaseLabel != null && !CMain.HintBaseLabel.IsDisposed)
                CMain.HintBaseLabel.Draw();

            OnShown();
        }

        /// <summary>Top-level children drawn by their own render target when nothing overlaps them (Settings.RebuildOptimisations).</summary>
        internal readonly List<LiveOverlay> LiveOverlays = new List<LiveOverlay>();

        protected void AddLiveOverlay(MirControl target)
        {
            LiveOverlays.Add(new LiveOverlay(target, this));
        }

        protected internal override void DrawControl()
        {
            for (int i = 0; i < LiveOverlays.Count; i++)
                LiveOverlays[i].Prepare();

            base.DrawControl();

            for (int i = 0; i < LiveOverlays.Count; i++)
                LiveOverlays[i].Composite(Opacity);
        }

        protected override void CreateTexture()
        {
            long rebuildStart = FrameProfiler.Now();
            int rebuildDraws = CMain.DPSCounter;

            for (int i = 0; i < LiveOverlays.Count; i++)
                LiveOverlays[i].UpdateEligibility();

            if (Size != TextureSize)
                DisposeTexture();

            if (ControlTexture == null || ControlTexture.Disposed)
            {
                DXManager.ControlList.Add(this);
                ControlTexture = DXManager.CreateRenderTarget(Size.Width, Size.Height);
                TextureSize = Size;
            }
            RenderTexture oldSurface = DXManager.CurrentSurface;
            DXManager.SetSurface(ControlTexture);

            DXManager.Clear(BackColour);

            BeforeDrawControl();
            DrawChildControls();
            AfterDrawControl();

            DXManager.Flush();


            DXManager.SetSurface(oldSurface);
            TextureValid = true;

            FrameProfiler.AddRebuild(RebuildTarget.Ui, rebuildStart, rebuildDraws);
        }

        public override void OnMouseDown(MouseEventArgs e)
        {
            if (!Enabled)
                return;

            if (MouseControl != null && MouseControl != this)
                MouseControl.OnMouseDown(e);
            else
                base.OnMouseDown(e);
        }
        public override void OnMouseUp(MouseEventArgs e)
        {
            if (!Enabled)
                return;
            if (MouseControl != null && MouseControl != this)
                MouseControl.OnMouseUp(e);
            else
                base.OnMouseUp(e);
        }
        public override void OnMouseMove(MouseEventArgs e)
        {
            if (!Enabled)
                return;

            if (MouseControl != null && MouseControl != this && MouseControl.Moving)
                MouseControl.OnMouseMove(e);
            else
                base.OnMouseMove(e);
        }
        public override void OnMouseWheel(MouseEventArgs e)
        {
            if (!Enabled)
                return;

            if (MouseControl != null && MouseControl != this)
                MouseControl.OnMouseWheel(e);
            else
                base.OnMouseWheel(e);
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            if (!Enabled)
                return;
            if (_buttons == e.Button)
            {
                if (_lastClickTime + SystemInformation.DoubleClickTime >= CMain.Time)
                {
                    OnMouseDoubleClick(e);
                    return;
                }
            }
            else
                _lastClickTime = 0;

            if (ActiveControl != null && ActiveControl.IsMouseOver(CMain.MPoint) && ActiveControl != this)
                ActiveControl.OnMouseClick(e);
            else
                base.OnMouseClick(e);

            _clickedControl = ActiveControl;

            _lastClickTime = CMain.Time;
            _buttons = e.Button;
        }

        public override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (!Enabled)
                return;
            _lastClickTime = 0;
            _buttons = MouseButtons.None;

            if (ActiveControl != null && ActiveControl.IsMouseOver(CMain.MPoint) && ActiveControl != this)
            {
                if (ActiveControl == _clickedControl)
                    ActiveControl.OnMouseDoubleClick(e);
                else
                    ActiveControl.OnMouseClick(e);
            }
            else
            {
                if (ActiveControl == _clickedControl)
                    base.OnMouseDoubleClick(e);
                else
                    base.OnMouseClick(e);
            }
        }

        public override void Redraw()
        {
            FrameProfiler.NoteInvalidation(RebuildTarget.Ui, TextureValid);
            TextureValid = false;
        }
        
        public virtual void ProcessPacket(Packet p)
        {
            switch (p.Index)
            {
                case (short)ServerPacketIds.Disconnect: // Disconnected
                    Disconnect((S.Disconnect) p);
                    Network.Disconnect();
                    break;
                case (short)ServerPacketIds.NewItemInfo:
                    NewItemInfo((S.NewItemInfo) p);
                    break;
                case (short)ServerPacketIds.NewMonsterInfo:
                    NewMonsterInfo((S.NewMonsterInfo)p);
                    break;
                case (short)ServerPacketIds.NewNPCInfo:
                    NewNPCInfo((S.NewNPCInfo)p);
                    break;
                case (short)ServerPacketIds.NewChatItem:
                    NewChatItem((S.NewChatItem)p);
                    break;
                case (short)ServerPacketIds.NewQuestInfo:
                    NewQuestInfo((S.NewQuestInfo)p);
                    break;
                case (short)ServerPacketIds.NewRecipeInfo:
                    NewRecipeInfo((S.NewRecipeInfo)p);
                    break;
                case (short)ServerPacketIds.NewHeroInfo:
                    NewHeroInfo((S.NewHeroInfo)p);
                    break;
            }
        }

        private void NewItemInfo(S.NewItemInfo info)
        {
            // Upsert in place (F7): after a relog the server sends a newer copy of an item the client already holds; updating the
            // existing object keeps every UserItem.Info that points at it current.
            GameScene.UpsertItemInfo(info.Info);
            GameScene.OnItemInfoReceived(info.Info.Index);
        }

        private void NewMonsterInfo(S.NewMonsterInfo info)
        {
            GameScene.MonsterInfoList.RemoveAll(x => x.Index == info.Info.Index);
            GameScene.MonsterInfoList.Add(info.Info);
            GameScene.OnMonsterInfoReceived(info.Info.Index);
        }

        private void NewNPCInfo(S.NewNPCInfo info)
        {
            GameScene.NPCInfoList.RemoveAll(x => x.Index == info.Info.Index);
            GameScene.NPCInfoList.Add(info.Info);
            GameScene.OnNPCInfoReceived(info.Info.Index);
        }

        private void NewHeroInfo(S.NewHeroInfo info)
        {
            AddHeroInformation(info.Info, info.StorageIndex);
        }

        public void AddHeroInformation(ClientHeroInformation info, int storageIndex = -1)
        {
            if (info == null) return;
            GameScene.HeroInfoList.RemoveAll(x => x.Index == info.Index);
            GameScene.HeroInfoList.Add(info);

            if (storageIndex < 0) return;
            GameScene.HeroStorage[storageIndex] = info;
        }

        private void NewChatItem(S.NewChatItem p)
        {
            if (GameScene.ChatItemList.Any(x => x.UniqueID == p.Item.UniqueID)) return;

            GameScene.Bind(p.Item);
            GameScene.ChatItemList.Add(p.Item);
        }

        private void NewQuestInfo(S.NewQuestInfo info)
        {
            GameScene.UpsertQuestInfo(info.Info); // F7: replace a stale copy instead of adding a second one
        }

        private void NewRecipeInfo(S.NewRecipeInfo info)
        {
            GameScene.RecipeInfoList.Add(info.Info);

            GameScene.Bind(info.Info.Item);

            for (int j = 0; j < info.Info.Tools.Count; j++)
                GameScene.Bind(info.Info.Tools[j]);

            for (int j = 0; j < info.Info.Ingredients.Count; j++)
                GameScene.Bind(info.Info.Ingredients[j]);
        }

        private static void Disconnect(S.Disconnect p)
        {
            switch (p.Reason)
            {
                case 0:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ShuttingDown), true);
                    break;
                case 1:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DisconnectedAnotherUserLogged), true);
                    break;
                case 2:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DisconnectedPacketError), true);
                    break;
                case 3:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DisconnectedServerCrashed), true);
                    break;
                case 4:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DisconnectedKickedByAdmin), true);
                    break;
                case 5:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DisconnectedMaxConnectionsReached), true);
                    break;
            }

            GameScene.LogTime = 0;
        }

        public abstract void Process();

        #region Disposable

        protected override void Dispose(bool disposing)
        {

            base.Dispose(disposing);

            if (!disposing) return;

            for (int i = 0; i < LiveOverlays.Count; i++)
                LiveOverlays[i].Dispose();
            LiveOverlays.Clear();

            if (ActiveScene == this) ActiveScene = null;

            _buttons = 0;
            _lastClickTime = 0;
            _clickedControl = null;
        }

        #endregion
    }
}
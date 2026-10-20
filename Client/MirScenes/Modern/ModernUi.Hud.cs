using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Hud;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// Phase 2 area "HUD" (worker A): the bottom HUD (orbs / bars, 10-slot belt, skill rows, stat plates, buff / poison rows,
    /// durability strip, EXP diamond, menu strip), party frames, hero frame, game menu. Components live in
    /// Client\MirScenes\Modern\Hud\ and listen to ModernEvents / GameScene.Models.
    /// </summary>
    public sealed partial class ModernUi
    {
        /// <summary>The bottom HUD (Hud\Hud.cs), its popover, the game menu and the side frames; null before BuildHud.</summary>
        internal HudRig HudRig { get; private set; }
        internal HudPopover HudPopover { get; private set; }
        internal GameMenu GameMenu { get; private set; }
        internal HeroFrame HeroFrame { get; private set; }
        internal PartyFrames PartyFrames { get; private set; }
        internal HudOutputs HudOutputs { get; private set; }
        /// <summary>The in-game patcher's download strip (Hud\DownloadStrip.cs).</summary>
        internal DownloadStrip DownloadStrip { get; private set; }

        partial void BuildHud()
        {
            HudPopover = new HudPopover { Parent = Root.Menu };
            GameMenu = new GameMenu { Parent = Root.Menu };
            HudRig = new HudRig(GameMenu, HudPopover) { Parent = Root.Hud };
            HeroFrame = new HeroFrame { Parent = Root.Hud };
            PartyFrames = new PartyFrames { Parent = Root.Hud };
            HudOutputs = new HudOutputs { Parent = Root.Hud };
            DownloadStrip = new DownloadStrip { Parent = Root.Hud };
            HudRig.HeroFrame = HeroFrame;
            HudRig.Party = PartyFrames;
            HudRig.Outputs = HudOutputs;
        }

        /// <summary>
        /// Esc (the Closeall bind) after the window manager found no window to close: an open HUD list closes, else the game menu
        /// opens or closes (gameMenu.ts escape: only with no window and no popup open; Shift+Esc stays "close all windows").
        /// A window toggle while the game menu is open closes the menu and carries on (returns false) so the window opens.
        /// </summary>
        private partial bool HudKeyBind(KeybindOptions function)
        {
            if (GameMenu == null || HudRig == null) return false;
            if (function == KeybindOptions.Closeall)
            {
                if (CMain.Shift) return false;
                if (GameMenu.IsOpen)
                {
                    GameMenu.Close();
                    return true;
                }
                if (HudRig.Escape()) return true;
                if (Root.Wm.Front != null || Root.Popups.IsOpen || Root.ModalPromptOpen) return false;
                GameMenu.Show();
                return true;
            }
            if (GameMenu.IsOpen && MenuEntry.IsWindowBind(function)) GameMenu.Close();
            return false;
        }
    }
}

#if DEBUG
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace pylorak.TinyWall
{
    // Debug-only /uipreview: the real tray menu in a fixed state. The controller never runs
    // InitController, so it does not load the database, poll for prompts or reach the service.
    internal sealed partial class TinyWallController
    {
        internal static TinyWallController CreatePreview()
        {
            var controller = new TinyWallController(new CmdLineArgs { ProgramMode = StartUpMode.UiPreview });
            System.Windows.Forms.Application.Idle -= controller.Application_Idle;
            return controller;
        }

        internal ContextMenuStrip PreviewTrayMenu => TrayMenu;
        internal ToolStripMenuItem PreviewModeMenu => mnuMode;

        // The menu as the tray shows it after InitController and a successful settings read.
        internal void ShowPreviewTrayMenu(FirewallMode mode, bool hasPassword, Point screenLocation)
        {
            string traffic = string.Format(CultureInfo.CurrentCulture, "{0}: {1}   {2}: {3}",
                Resources.Messages.TrafficIn, "12.4 KiB/s", Resources.Messages.TrafficOut, "3.1 KiB/s");
            mnuTrafficRate.Text = traffic;
            mnuElevate.Visible = !Utils.RunningAsAdmin();
            // Shown, but it would start the real, elevated controller.
            mnuElevate.Click -= mnuElevate_Click;
            mnuModeDisabled.Image = Utils.LoadSmallIconBitmap(Resources.Icons.shield_grey_small);
            mnuModeAllowOutgoing.Image = Utils.LoadSmallIconBitmap(Resources.Icons.shield_red_small);
            mnuModeBlockAll.Image = Utils.LoadSmallIconBitmap(Resources.Icons.shield_yellow_small);
            mnuModeNormal.Image = Utils.LoadSmallIconBitmap(Resources.Icons.shield_green_small);
            ApplyControllerSettings();
            // Shown, but not wired to a release check.
            TrayMenu.Items.Insert(TrayMenu.Items.IndexOf(toolStripMenuItem5) + 1, mnuCheckForUpdates);

            FirewallState.Mode = mode;
            FirewallState.HasPassword = hasPassword;
            FirewallState.Locked = false;
            UpdateDisplay();
            TrayMenu.Show(screenLocation, ToolStripDropDownDirection.AboveLeft);
            // Opening the menu starts the traffic timer, which reads this machine's real rates.
            TrafficTimer.Change(Timeout.Infinite, Timeout.Infinite);
            mnuTrafficRate.Text = traffic;
        }
    }
}
#endif

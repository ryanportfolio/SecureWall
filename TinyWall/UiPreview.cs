#if DEBUG
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    // Debug-only /uipreview: opens the tray menu, Network Activity, Manage or the update
    // window with sample data (UiPreviewSamples), without the service, its pipe or WFP. The
    // product walkthrough's replicas are checked against these windows.
    //
    //   /uipreview tray [/mode normal|blockall|allowout|disabled] [/password] [/submenu]
    //   /uipreview connections [/menu]
    //   /uipreview manage [/tab <index>]
    //   /uipreview update [/downloading]
    //   /uipreview password
    //   any of them: [/dark] [/database <profiles.json>] [/capture <png>]
    //
    // "/capture <png>" saves the window (and an open menu as <png>-menu.png) about 1.5 s
    // after it opens, then exits. The windows are faded nearly invisible while captured.
    // Without it they stay open; their actions fail as they do when the service is down.
    internal static class UiPreview
    {
        internal const string Switch = "/uipreview";
        private const string CaptureSwitch = "/capture";
        // Never the real pipe: preview actions must not reach an installed service.
        private const string NoServicePipe = "SecureWall-UiPreview-NoService";

        // Set for the whole preview: ControllerSettings.Save does nothing while it is.
        internal static bool Active { get; private set; }

        internal static int Run(string[] args)
        {
            Active = true;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Its constructor loads this build's controller settings; replaced below.
            using TinyWallController controller = TinyWallController.CreatePreview();
            // Neutral (English) strings and fixed date formats, whatever this machine uses.
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
            Application.CurrentCulture = Thread.CurrentThread.CurrentCulture;

            // A Debug build has no profiles.json beside it; "/database <path>" names one, for
            // example MsiSetup\Sources\CommonAppData\SecureWall\profiles.json.
            try
            {
                string? database = ValueAfter(args, "/database");
                GlobalInstances.AppDatabase = database == null
                    ? DatabaseClasses.AppDatabase.Load()
                    : SerializationHelper.DeserializeFromFile(database, new DatabaseClasses.AppDatabase());
            }
            catch
            {
                GlobalInstances.AppDatabase = new DatabaseClasses.AppDatabase();
            }
            ActiveConfig.Controller = new ControllerSettings { UiTheme = HasSwitch(args, "/dark") ? "dark" : "light" };
            ActiveConfig.Service = UiPreviewSamples.Configuration(GlobalInstances.AppDatabase);
            GlobalInstances.Controller = new Controller(NoServicePipe);

            string? capture = CapturePath(args);
            switch ((ValueAfter(args, Switch) ?? "tray").ToLowerInvariant())
            {
                case "tray":
                    return RunTray(controller, args, capture);
                case "connections":
                    return RunConnections(controller, args, capture);
                case "manage":
                    return RunManage(args, capture);
                case "update":
                    return RunUpdate(args, capture);
                case "password":
                    return RunPassword(capture);
                default:
                    return 2;
            }
        }

        private static int RunTray(TinyWallController controller, string[] args, string? capture)
        {
            FirewallMode mode = (ValueAfter(args, "/mode") ?? "normal").ToLowerInvariant() switch
            {
                "blockall" => FirewallMode.BlockAll,
                "allowout" => FirewallMode.AllowOutgoing,
                "disabled" => FirewallMode.Disabled,
                _ => FirewallMode.Normal,
            };
            ContextMenuStrip menu = controller.PreviewTrayMenu;
            if (capture != null)
                menu.Opacity = FadedOpacity;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            bool submenu = HasSwitch(args, "/submenu");
            if (submenu)
                controller.PreviewModeMenu.DropDown.Opacity = capture != null ? FadedOpacity : 1;
            menu.Closed += (_, _) => Application.ExitThread();
            controller.ShowPreviewTrayMenu(mode, HasSwitch(args, "/password"), new Point(area.Right - 120, area.Bottom - 4));
            if (submenu)
            {
                // Highlighted as when the pointer rests on it, with the first mode under the pointer.
                controller.PreviewModeMenu.Select();
                controller.PreviewModeMenu.ShowDropDown();
                controller.PreviewModeMenu.DropDownItems[1].Select();
            }
            if (capture != null)
            {
                After(capture, () =>
                {
                    Save(menu.Handle, capture);
                    if (submenu)
                        Save(controller.PreviewModeMenu.DropDown.Handle, Suffixed(capture, "-menu"));
                });
            }
            Application.Run();
            return 0;
        }

        private static int RunConnections(TinyWallController controller, string[] args, string? capture)
        {
            using var form = new ConnectionsForm(controller)
            {
                PreviewRows = UiPreviewSamples.ActivityRows(),
                StartPosition = FormStartPosition.CenterScreen,
            };
            bool menu = HasSwitch(args, "/menu");
            if (capture != null)
            {
                form.Opacity = FadedOpacity;
                form.PreviewMenu.Opacity = FadedOpacity;
            }
            form.Shown += (_, _) =>
            {
                if (menu)
                    form.BeginInvoke((Action)(() => form.ShowPreviewMenu("Blocked (default")));
                if (capture != null)
                {
                    After(capture, () =>
                    {
                        Save(form.Handle, capture);
                        if (menu)
                            Save(form.PreviewMenu.Handle, Suffixed(capture, "-menu"));
                    });
                }
            };
            Application.Run(form);
            return 0;
        }

        private static int RunManage(string[] args, string? capture)
        {
            var controllerSettings = Utils.DeepClone(ActiveConfig.Controller);
            if (int.TryParse(ValueAfter(args, "/tab"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tab))
                controllerSettings.SettingsTabIndex = tab;
            using var form = new SettingsForm(Utils.DeepClone(ActiveConfig.Service), controllerSettings)
            {
                StartPosition = FormStartPosition.CenterScreen,
            };
            ShowForm(form, capture);
            return 0;
        }

        private static int RunUpdate(string[] args, string? capture)
        {
            using var dialog = new UpdateDialog();
            (GitHubRelease release, ReleaseManifest manifest) = UiPreviewSamples.Update();
            bool downloading = HasSwitch(args, "/downloading");
            dialog.Shown += (_, _) =>
            {
                // The texts CheckForUpdatesCore and InstallUpdate show at each step.
                if (!downloading)
                {
                    dialog.ShowAvailable(release, manifest, UiPreviewSamples.UpdateInstalled);
                    return;
                }
                dialog.ShowBusy("Downloading SecureWall " + ReleaseVersion.Format(manifest.Version),
                    "The installer is checked against the release's SHA256SUMS.txt before anything is installed.");
                dialog.ShowDownloadProgress(4_300_000, 9_800_000);
            };
            ShowForm(dialog, capture);
            return 0;
        }

        private static int RunPassword(string? capture)
        {
            using var form = new PasswordForm { StartPosition = FormStartPosition.CenterScreen };
            ShowForm(form, capture);
            return 0;
        }

        private static void ShowForm(Form form, string? capture)
        {
            if (capture != null)
            {
                form.Opacity = FadedOpacity;
                form.Shown += (_, _) => After(capture, () => Save(form.Handle, capture));
            }
            Application.Run(form);
        }

        // For /promptpreview: capture the popup once its probes and Allow arming have settled.
        internal static void CaptureWhenReady(Form popup, string path)
        {
            popup.Opacity = FadedOpacity;
            popup.Shown += (_, _) => After(path, () => Save(popup.Handle, path));
        }

        internal static SignedFileIdentity? Signer(string[] args) =>
            ValueAfter(args, "/signer") is string name ? SignedFileIdentity.ForPreview(name) : null;

        internal static string? CapturePath(string[] args) => ValueAfter(args, CaptureSwitch);

        // Layered at alpha 1/255: still composed, so PrintWindow reads full content, but not
        // visible on the desktop. Windows created off-screen capture half white instead.
        private const double FadedOpacity = 1.0 / 255;

        private static void After(string path, Action save)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                save();
                Application.ExitThread();
            };
            timer.Start();
        }

        private static string Suffixed(string path, string suffix) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                Path.GetFileNameWithoutExtension(path) + suffix + Path.GetExtension(path));

        // PW_RENDERFULLCONTENT copies the window's compositor surface, cropped to its visible
        // frame (without the invisible resize borders).
        private static void Save(IntPtr hwnd, string path)
        {
            if (!GetWindowRect(hwnd, out RECT window))
                throw new InvalidOperationException("GetWindowRect failed.");
            RECT frame = window;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT extended, Marshal.SizeOf<RECT>()) == 0)
                frame = extended;

            using var full = new Bitmap(window.Right - window.Left, window.Bottom - window.Top, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT))
                        throw new InvalidOperationException("PrintWindow failed.");
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
            var crop = new Rectangle(frame.Left - window.Left, frame.Top - window.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
            using Bitmap cropped = full.Clone(crop, PixelFormat.Format32bppArgb);
            cropped.Save(path, ImageFormat.Png);
        }

        private static bool HasSwitch(string[] args, string name) =>
            Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) >= 0;

        private static string? ValueAfter(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("/", StringComparison.Ordinal) ? args[i + 1] : null;
        }

        private const uint PW_RENDERFULLCONTENT = 2;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);
    }
}
#endif

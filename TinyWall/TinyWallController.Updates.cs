using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    // Check for updates (tray menu and Manage > Maintenance). Manual only. The flow:
    // unlock if a password is set, ask the service for a transient TCP 443 permit, read
    // SecureWall's GitHub releases and verify the newest eligible one's signed manifest
    // (ReleaseCheck), show it, and on Install download the MSI with a fresh permit and start
    // the elevated `/apply-update` step, which verifies everything again before msiexec runs.
    internal sealed partial class TinyWallController
    {
        private readonly ToolStripMenuItem mnuCheckForUpdates = new() { Text = "Check for &updates..." };
        private UpdateDialog? UpdateWindow;
        // True from the click until the last network step has ended its permit. The service
        // holds one permit per process, so a second check must not start while an earlier one
        // (whose window may already be closed) can still release it.
        private bool UpdateBusy;

        private void InitUpdatesMenu()
        {
            TrayMenu.Items.Insert(TrayMenu.Items.IndexOf(toolStripMenuItem5) + 1, mnuCheckForUpdates);
            mnuCheckForUpdates.Click += (_, _) => CheckForUpdates(null);
        }

        // includePrereleases: Manage passes its checkbox, which may not be saved yet.
        internal async void CheckForUpdates(bool? includePrereleases)
        {
            if (UpdateWindow != null)
            {
                UpdateWindow.Activate();
                return;
            }
            if (UpdateBusy)
            {
                ShowBalloonTip("The previous update check is still finishing. Try again in a moment.", ToolTipIcon.Info);
                return;
            }
            UpdateBusy = true;
            try
            {
                await CheckForUpdatesCore(includePrereleases);
            }
            finally
            {
                UpdateBusy = false;
            }
        }

        private async Task CheckForUpdatesCore(bool? includePrereleases)
        {
            if (!EnsureUnlockedServer())
                return;

            ControllerSettings saved = ControllerSettings.Load();
            bool prereleases = includePrereleases ?? saved.IncludePrereleases;
            Version? skipped = ReleaseCheck.ParseSkipped(saved.UpdateSkippedVersion);
            Version installed = Utils.TinyWallVersion;
            string platform = InstalledPlatform();

            var cancel = new CancellationTokenSource();
            var dialog = new UpdateDialog();
            UpdateWindow = dialog;
            dialog.FormClosed += (_, _) =>
            {
                cancel.Cancel();
                UpdateWindow = null;
            };
            dialog.ShowBusy("Checking for updates", "Reading SecureWall's releases on GitHub and checking their signatures.");
            dialog.Show();
            dialog.Activate();

            ReleaseCheckResult result;
            try
            {
                CancellationToken token = cancel.Token;
                result = await Task.Run(() => WithUpdateNetwork(async () =>
                {
                    using var client = new ReleaseCheckClient();
                    return await ReleaseCheck.FindAsync(client, installed, platform, prereleases, skipped, ReleaseKeys.Trusted, token).ConfigureAwait(false);
                }, ReleaseCheckResult.Fail));
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_GUI);
                result = ReleaseCheckResult.Fail("The update check failed: " + exception.Message);
            }
            if (dialog.IsDisposed)
                return;

            switch (result.Outcome)
            {
                case ReleaseCheckOutcome.UpToDate:
                    dialog.ShowMessage("SecureWall is up to date", result.Message);
                    return;
                case ReleaseCheckOutcome.NothingEligible:
                    dialog.ShowMessage("No update to install", result.Message);
                    return;
                case ReleaseCheckOutcome.Failed:
                    dialog.ShowMessage("Could not check for updates", result.Message);
                    return;
            }

            dialog.ShowAvailable(result.Release!, result.Manifest!, installed);
            bool installing = false;
            CancellationToken installToken = cancel.Token;
            dialog.Chosen += async (_, choice) =>
            {
                if (installing)
                    return;
                if (choice == UpdateDialog.Choice.Skip)
                {
                    SaveUpdateSetting(settings => settings.UpdateSkippedVersion = ReleaseVersion.Format(result.Manifest!.Version));
                    dialog.Close();
                }
                else if (choice == UpdateDialog.Choice.Install)
                {
                    if (UpdateBusy)
                        return;
                    installing = UpdateBusy = true;
                    try
                    {
                        await InstallUpdate(dialog, result, platform, installToken);
                    }
                    catch (Exception exception)
                    {
                        Utils.LogException(exception, Utils.LOG_ID_GUI);
                        if (!dialog.IsDisposed)
                            dialog.ShowMessage("Update not installed", "The update failed: " + exception.Message);
                    }
                    finally
                    {
                        installing = UpdateBusy = false;
                    }
                }
            };
        }

        private async Task InstallUpdate(UpdateDialog dialog, ReleaseCheckResult result, string platform, CancellationToken cancellation)
        {
            // Installing changes the firewall, so it needs the password too (decision 6).
            if (!EnsureUnlockedServer())
                return;

            SignedReleaseManifest manifest = result.Manifest!;
            GitHubRelease release = result.Release!;
            dialog.ShowBusy("Downloading SecureWall " + ReleaseVersion.Format(manifest.Version), "The installer is checked against the signed release before anything is installed.");
            var progress = new Progress<(long Received, long Total)>(p => { if (!dialog.IsDisposed) dialog.ShowDownloadProgress(p.Received, p.Total); });

            string folder;
            string package;
            string? error;
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string root = UpdateApplyPolicy.UserUpdatesRoot(localAppData);
                folder = Path.Combine(root, manifest.Tag);
                package = Path.Combine(folder, SignedReleaseManifest.AssetName(platform));
                error = await Task.Run(() => WithUpdateNetwork<string?>(async () =>
                {
                    // Earlier downloads, of this or any other release, are not kept.
                    try
                    {
                        if (Directory.Exists(root))
                            Directory.Delete(root, true);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    Directory.CreateDirectory(folder);
                    foreach (string name in new[] { SignedReleaseManifest.AssetName(platform), ReleaseCheck.ManifestAsset, ReleaseCheck.SignatureAsset })
                        File.Delete(Path.Combine(folder, name));
                    File.WriteAllBytes(Path.Combine(folder, ReleaseCheck.ManifestAsset), result.ManifestBytes);
                    File.WriteAllBytes(Path.Combine(folder, ReleaseCheck.SignatureAsset), result.SignatureBytes);
                    using var client = new ReleaseCheckClient();
                    string? failure = await client.FetchToFileAsync(release.Assets[SignedReleaseManifest.AssetName(platform)], package,
                        ReleaseCheck.MaxPackageBytes, UpdateNetworkGrantPolicy.MaxLifetime, progress, cancellation).ConfigureAwait(false);
                    if (failure != null)
                        return "The installer could not be downloaded: " + failure + ".";
                    // Early feedback only: the elevated step checks the hash again on its own copy.
                    if (SignedReleaseManifest.Sha256Hex(File.ReadAllBytes(package)) != manifest.HashFor(platform))
                        return "The downloaded installer does not match the signed release. Nothing was installed.";
                    return null;
                }, message => message));
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                error = "The installer could not be saved: " + exception.Message;
                folder = package = string.Empty;
            }
            if (dialog.IsDisposed)
                return;
            if (error != null)
            {
                dialog.ShowMessage("Update not installed", error);
                return;
            }

            string version = ReleaseVersion.Format(ReleaseVersion.Normalize(Utils.TinyWallVersion));
            string user;
            using (var identity = WindowsIdentity.GetCurrent())
                user = identity.User!.Value;
            string arguments = UpdateApplyPolicy.Switch + " \"" + package + "\" \"" +
                Path.Combine(folder, ReleaseCheck.ManifestAsset) + "\" \"" + Path.Combine(folder, ReleaseCheck.SignatureAsset) + "\" " + user;
            try
            {
                SaveUpdateSetting(settings => settings.UpdateStartedFromVersion = version);
                Utils.StartProcessAndForget(Utils.ExecutablePath, arguments, true);
                dialog.Close();
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                SaveUpdateSetting(settings => settings.UpdateStartedFromVersion = string.Empty);
                dialog.ShowAvailable(release, manifest, Utils.TinyWallVersion);
                dialog.ShowNote("Windows did not get administrator permission, so nothing was installed. Click Install to try again.");
            }
            catch (Win32Exception exception)
            {
                SaveUpdateSetting(settings => settings.UpdateStartedFromVersion = string.Empty);
                dialog.ShowMessage("Update not installed", "The update step could not start: " + exception.Message);
            }
        }

        // Runs `work` while the service holds an update permit for this controller, and ends the
        // permit afterwards whatever happens. Modes that already allow outbound traffic need none.
        private static async Task<T> WithUpdateNetwork<T>(Func<Task<T>> work, Func<string, T> fail)
        {
            MessageType response = GlobalInstances.Controller.RequestUpdateNetwork(out UpdateGrantStatus status);
            if (response == MessageType.RESPONSE_LOCKED)
                return fail("SecureWall is locked. Unlock it and try again.");
            if (response != MessageType.UPDATE_NETWORK_GRANT)
            {
                // The service may still have added a permit after the pipe gave up waiting.
                GlobalInstances.Controller.ReleaseUpdateNetwork();
                return fail("The SecureWall service did not answer, so the update check could not get network access.");
            }
            if (status != UpdateGrantStatus.Granted && status != UpdateGrantStatus.NotNeeded)
                return fail(UpdateNetworkGrantPolicy.Describe(status));
            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                if (status == UpdateGrantStatus.Granted)
                    GlobalInstances.Controller.ReleaseUpdateNetwork();
            }
        }

        // The package type the MSI recorded: HKLM\SOFTWARE\SecureWall\Platform, in the 64-bit
        // view for the x64 and arm64 packages and the 32-bit view for x86.
        private static string InstalledPlatform()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using RegistryKey? key = hklm.OpenSubKey(ProductRegistryPolicy.KeyPath);
                    if (key?.GetValue("Platform") is string platform && Array.IndexOf(SignedReleaseManifest.Platforms, platform) >= 0)
                        return platform;
                }
                catch (Exception exception) when (exception is System.Security.SecurityException || exception is IOException ||
                    exception is UnauthorizedAccessException)
                {
                }
            }
            return string.Empty;
        }

        private static void SaveUpdateSetting(Action<ControllerSettings> change)
        {
            // Read the saved file, not ActiveConfig.Controller: a failed service read replaces
            // the in-memory copy with defaults, which must never be written back.
            ControllerSettings saved = ControllerSettings.Load();
            change(saved);
            saved.Save();
            change(ActiveConfig.Controller);
        }

        // After an update, the tray that setup restarts (or the next one) says so once.
        private void ShowUpdatedNotice()
        {
            ControllerSettings saved = ControllerSettings.Load();
            if (string.IsNullOrEmpty(saved.UpdateStartedFromVersion))
                return;
            string current = ReleaseVersion.Format(ReleaseVersion.Normalize(Utils.TinyWallVersion));
            bool updated = saved.UpdateStartedFromVersion != current;
            SaveUpdateSetting(settings => settings.UpdateStartedFromVersion = string.Empty);
            if (updated)
                ShowBalloonTip(string.Format(CultureInfo.CurrentCulture, "Updated to v{0}.", current), ToolTipIcon.Info, 10000);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using pylorak.TinyWall.Installer;
using System.Configuration.Install;
using System.Diagnostics;
using System.ServiceProcess;
using TaskScheduler;
using pylorak.Windows;
using pylorak.Windows.Services;
using pylorak.Windows.WFP;
using pylorak.Windows.WFP.Interop;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    internal static class TinyWallDoctor
    {
        private static readonly string CONTROLLER_START_TASKSCH_NAME = "SecureWall Controller";

        internal static bool IsServiceRunning(string logContext, bool installing)
        {
#if !DEBUG
            try
            {
                using var sc = new ServiceController(TinyWallService.SERVICE_NAME);
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch(Exception e)
            {
                if (!installing) Utils.LogException(e, logContext);
                return false;
            }
#else
            return true;
#endif
        }

        internal static bool IsServiceStopped()
        {
#if !DEBUG
            try
            {
                using var sc = new ServiceController(TinyWallService.SERVICE_NAME);
                return (sc.Status == ServiceControllerStatus.Stopped);
            }
            catch
            {
                return false;
            }
#else
            return true;
#endif
        }

        // upgradeTransaction is set only by the upgrade restore step, the one other
        // starter that may run while its own upgrade-in-progress marker is present.
        internal static bool EnsureServiceInstalledAndRunning(string logContext, bool installing, string? upgradeTransaction = null)
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                bool mayInstall = ControllerRepairPolicy.MayInstall(installing, identity.IsSystem);
                if (installing) InstallationSafety.RequireSystemMaintenance();
                InstallationSafety.RequireNoTinyWall();
                if (installing) InstallationSafety.RequireNoConflictingWfpObjects();
                InstallationSafety.RequireProtectedInstallation();
                RequireServiceNotPendingDeletion();
                ControllerRepairPolicy.RequireExistingOrInstaller(ServiceExists(), mayInstall);
#if !DEBUG
                try { InstallationSafety.RequireProtectedMachineData(); }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(Utils.MachineDataRecoveryMessage + Environment.NewLine + exception.Message, exception);
                }
#endif
                ValidateRegisteredServiceImage(true);
                WindowsFirewall.RequireServiceRunning();
                if (IsServiceRunning(logContext, installing)) return true;
                // An in-place upgrade stops the service and swaps its files; starting
                // either version in between would lock files or break the swap.
                if (UpgradeLifecyclePolicy.BlocksStart(UpgradeMarker.Read(), UpgradeMarker.InstallerRunning(), upgradeTransaction))
                    throw new InvalidOperationException(UpgradeLifecyclePolicy.InProgressMessage);
                if (mayInstall)
                {
                    if (!ServiceExists())
                        ManagedInstallerClass.InstallHelper(new string[] { "/i", Utils.ExecutablePath });
                    EnsureHealth(logContext, strictGuards: true);
                }

                // Controllers only start an existing authenticated service.
                RequireServiceNotPendingDeletion();
                ControllerRepairPolicy.RequireExistingOrInstaller(ServiceExists(), false);
                ValidateRegisteredServiceImage(true);
                using var sc = new ServiceController(TinyWallService.SERVICE_NAME);
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    if (identity.IsSystem || Utils.RunningAsAdmin()) sc.Start();
                    else
                    {
                        string command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
                        using Process process = Utils.StartProcess(command, "start " + TinyWallService.SERVICE_NAME, true, true);
                        if (!process.WaitForExit((int)ServiceLifecyclePolicy.StartupTimeout.TotalMilliseconds))
                            throw new InvalidOperationException("The service start request timed out. Check the service from a local console before retrying.");
                        // A concurrent start can give sc.exe a nonzero exit code.
                        // SCM Running observation below determines success.
                        Utils.Log("Service start request returned exit code " + process.ExitCode + ". Waiting for Running status.", logContext);
                    }
                }
                sc.WaitForStatus(ServiceControllerStatus.Running, ServiceLifecyclePolicy.StartupTimeout);
                Utils.Log("SecureWall service reached Running status.", logContext);
                return true;
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, logContext);
                if (!installing) Utils.ShowControllerFailure(exception.Message);
                return false;
            }
        }

        internal static int Uninstall()
        {
            if (!Utils.RunningAsAdmin()) return -1;
#if !DEBUG
            // Direct callers do not necessarily pass through Main's data guard.
            try { InstallationSafety.RequireProtectedMachineData(); }
            catch { return -1; } // Do not log into rejected machine data.
#endif
            using (var frm = new System.Windows.Forms.Form())
            {
                // See http://www.codeproject.com/Articles/18612/TopMost-MessageBox
                // for an explanation as for why this is needed.
                frm.Size = new System.Drawing.Size(1, 1);
                frm.ShowInTaskbar = false;
                frm.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                System.Drawing.Rectangle rect = System.Windows.Forms.SystemInformation.VirtualScreen;
                frm.Location = new System.Drawing.Point(rect.Bottom + 10, rect.Right + 10);
                frm.Show();
                frm.Focus();
                frm.BringToFront(); 
                frm.TopMost = true;

                if (System.Windows.Forms.MessageBox.Show(frm,
                    Resources.Messages.DidYouInitiateTheUninstall,
                    Resources.Messages.TinyWall,
                    System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Exclamation) != System.Windows.Forms.DialogResult.Yes)
                {
                    return -1;
                }
            }

            // Stop service
            try
            {
                if (TinyWallDoctor.IsServiceRunning(Utils.LOG_ID_INSTALLER, false))
                {
                    var twController = new Controller(SecureWallProduct.ControllerPipeName);

                    // Unlock server
                    while (twController.IsServerLocked)
                    {
                        using var pf = new PasswordForm();
                        pf.BringToFront();
                        pf.Activate();
                        if (pf.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        {
                            twController.TryUnlockServer(pf.PassHash);
                        }
                        else
                            return -1;
                    }

                    // Stop server
                    twController.RequestServerStop();
                    DateTime startTs = DateTime.Now;
                    while (!IsServiceStopped() && ((DateTime.Now - startTs) < TimeSpan.FromSeconds(15)))
                        System.Threading.Thread.Sleep(200);
                    if (!IsServiceStopped())
                    {
                        Utils.Log("Failed to stop service during uninstall.", Utils.LOG_ID_INSTALLER);
                        return -1;
                    }
                }
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_INSTALLER);
                return -1;
            }

            return CleanupStoppedInstallation();
        }

        // Explicit MSI machine administration; never open a dialog or weaken the
        // password-gated interactive controller protocol on this path.
        internal static int UninstallForMsi() => CleanupForMsi(false);

        // Only failed first-install rollback may escalate beyond graceful SCM stop.
        internal static int RollbackFailedInstallForMsi() => CleanupForMsi(true);

        // In-place upgrade steps. Decisions and ordering live in UpgradeLifecyclePolicy;
        // docs/SECURITY.md "In-place upgrade" gives the whole sequence. Every step is
        // SYSTEM MSI maintenance on an authenticated Program Files image. None of them
        // runs /msi-cleanup or touches the deny baseline, the Windows Firewall
        // compatibility rules, the hosts file or the recovery journals.

        // Step 3, old executable, before its files are removed.
        internal static int UpgradeStopForMsi(string transaction)
        {
            try
            {
                using var service = new ScmUpgradeService(ServiceExists);
                var timer = Stopwatch.StartNew();
                UpgradeLifecyclePolicy.Stop(transaction, service,
                    authenticate: () =>
                    {
                        InstallationSafety.RequireSystemMaintenance();
#if !DEBUG
                        InstallationSafety.RequireProtectedMachineData();
#endif
                        RequireServiceNotPendingDeletion();
                        ValidateRegisteredServiceImage(true);
                    },
                    requirePrerequisites: () =>
                    {
                        // The new service cannot start without them; refuse while
                        // the old one still runs.
                        WindowsFirewall.RequireServiceRunning();
                        using var bfe = new ServiceController("BFE");
                        if (bfe.Status != ServiceControllerStatus.Running)
                            throw new InvalidOperationException("The Base Filtering Engine service (BFE) must be running before SecureWall can be upgraded.");
                    },
                    writeMarker: UpgradeMarker.Write,
                    elapsed: () => timer.Elapsed,
                    delay: System.Threading.Thread.Sleep,
                    terminateControllers: TerminateInstallationControllers,
                    snapshot: () =>
                    {
                        UpgradeLifecyclePolicy.TakeSnapshot(new MachineDataUpgradeFiles(), Utils.AppDataPath, transaction);
#if !DEBUG
                        InstallationSafety.RequireProtectedMachineData();
#endif
                        UpgradeFault.Trip("upgrade-stop-fail");
                    });
                Utils.Log("Upgrade " + transaction + ": service stopped, controllers closed, policy snapshot taken.", Utils.LOG_ID_INSTALLER);
                return 0;
            }
            catch (Exception exception)
            {
                LogFailure(exception);
                return -1;
            }
        }

        // Step 2, old executable, rollback: runs last, after Windows Installer put the
        // old files and registration back.
        internal static int UpgradeRestoreForMsi(string transaction)
        {
            try
            {
                InstallationSafety.RequireSystemMaintenance();
                RequireServiceNotPendingDeletion();
                ValidateRegisteredServiceImage(true);
                var files = new MachineDataUpgradeFiles();
                string data = Utils.AppDataPath;
                string? marker = UpgradeMarker.Read();
                using var service = new ScmUpgradeService(ServiceExists);
                if (!service.Registered)
                    throw new InvalidOperationException("The SecureWall service is not registered, so the previous version cannot be started.");
                switch (UpgradeLifecyclePolicy.Restore(transaction, marker, UpgradeLifecyclePolicy.SnapshotComplete(files, data, transaction)))
                {
                    case UpgradeLifecyclePolicy.RestoreAction.Refuse:
                        throw new InvalidOperationException("The upgrade-in-progress marker names another upgrade (" + marker + "). Nothing was restored.");
                    case UpgradeLifecyclePolicy.RestoreAction.RestoreAndStart:
                        var timer = Stopwatch.StartNew();
                        if (!ServiceLifecyclePolicy.StopGracefully(() => service.State, service.Stop, () => timer.Elapsed, System.Threading.Thread.Sleep))
                            throw new InvalidOperationException("The SecureWall service did not stop, so the policy snapshot was not restored.");
                        UpgradeLifecyclePolicy.RestoreSnapshot(files, data, transaction);
                        break;
                }
                UpgradeFault.Trip("upgrade-restore-start-fail");
#if !DEBUG
                // The old version's profiles.json and hosts.bck.
                MachineDataGuard.InstallDefaults();
#endif
                // Restores the restart policy step 3 removed, also when the stop
                // step failed before stopping anything. EnsureHealth only logs SCM
                // write failures, so the restart actions are set here first.
                service.SetRestartOnFailure(true);
                EnsureHealth(Utils.LOG_ID_INSTALLER, strictGuards: true);
                if (!EnsureServiceInstalledAndRunning(Utils.LOG_ID_INSTALLER, true, transaction))
                    return -1;
                CleanUpAfterUpgrade(transaction, () => UpgradeLifecyclePolicy.DeleteStaleSnapshots(files, data, null));
                Utils.Log("Upgrade " + transaction + " rolled back: previous policy restored and service running.", Utils.LOG_ID_INSTALLER);
                return 0;
            }
            catch (Exception exception)
            {
                LogFailure(exception);
                return -1;
            }
        }

        // Step 7, new executable, after its files and registry values are installed.
        // The service starts with restart-on-failure still off, so a failing start
        // ends here and the MSI rolls back; the service itself reapplies the restart
        // policy after its first policy commit, and so does EnsureHealth below.
        internal static int UpgradeStartForMsi(string transaction)
        {
            try
            {
                InstallationSafety.RequireSystemMaintenance();
                InstallationSafety.RequireNoTinyWall();
                InstallationSafety.RequireNoConflictingWfpObjects();
                RequireServiceNotPendingDeletion();
                ValidateRegisteredServiceImage(true);
                var files = new MachineDataUpgradeFiles();
                string data = Utils.AppDataPath;
                using var service = new ScmUpgradeService(ServiceExists);
                bool registered = service.Registered;
                UpgradeLifecyclePolicy.RequireStartable(transaction, registered,
                    registered ? service.State : LifecycleServiceState.Stopped,
                    UpgradeLifecyclePolicy.SnapshotComplete(files, data, transaction), UpgradeMarker.Read());
                WindowsFirewall.RequireServiceRunning();
#if !DEBUG
                // This version's profiles.json and hosts.bck.
                MachineDataGuard.InstallDefaults();
#endif
                UpgradeLifecyclePolicy.DeleteStaleSnapshots(files, data, transaction);
                UpgradeFault.Trip("upgrade-start-pause");
                UpgradeFault.Trip("upgrade-start-fail");
                service.Start();
                var timer = Stopwatch.StartNew();
                if (!UpgradeLifecyclePolicy.WaitForRunning(() => service.State, ServiceLifecyclePolicy.StartupTimeout,
                    () => timer.Elapsed, System.Threading.Thread.Sleep))
                    throw new InvalidOperationException("The upgraded SecureWall service did not reach Running: " + InstallationSafety.DescribeServiceStatus());
                // EnsureHealth only logs SCM write failures; the restart actions must
                // be back before the upgrade counts as done.
                service.SetRestartOnFailure(true);
                EnsureHealth(Utils.LOG_ID_INSTALLER, strictGuards: true);
                Utils.Log("Upgrade " + transaction + ": upgraded service running.", Utils.LOG_ID_INSTALLER);
                return 0;
            }
            catch (Exception exception)
            {
                LogFailure(exception);
                return -1;
            }
        }

        // Step 6, new executable, rollback: stops the new service before Windows
        // Installer removes its files. Keeps the registration, the marker and the
        // deny baseline; step 2 starts the old version afterwards.
        internal static int UpgradeAbortForMsi(string transaction)
        {
            try
            {
                InstallationSafety.RequireSystemMaintenance();
                ValidateRegisteredServiceImage(true);
                using var service = new ScmUpgradeService(ServiceExists);
                if (service.Registered)
                {
                    service.SetRestartOnFailure(false);
                    var timer = Stopwatch.StartNew();
                    bool terminated = false;
                    try
                    {
                        // The files are about to be removed, so a service that ignores
                        // the stop request is terminated like a failed first install.
                        ServiceLifecyclePolicy.Cleanup(true,
                            () => ServiceLifecyclePolicy.StopGracefully(() => service.State, service.Stop, () => timer.Elapsed, System.Threading.Thread.Sleep),
                            () =>
                            {
                                terminated = true;
                                ValidateRegisteredServiceImage();
                                InstallationSafety.TerminateFailedInstallService();
                            },
                            () => ServiceLifecyclePolicy.WaitUntil(() => service.State == LifecycleServiceState.Stopped,
                                ServiceLifecyclePolicy.CleanupGrace, () => timer.Elapsed, System.Threading.Thread.Sleep),
                            () => { });
                    }
                    finally
                    {
                        // Termination disabled the service. A reboot must still start
                        // whatever version the rollback leaves in place.
                        if (terminated)
                        {
                            using var scm = new ServiceControlManager();
                            scm.SetStartupMode(TinyWallService.SERVICE_NAME, ServiceStartMode.Automatic);
                        }
                    }
                }
                TerminateInstallationControllers();
                Utils.Log("Upgrade " + transaction + " is rolling back: upgraded service stopped.", Utils.LOG_ID_INSTALLER);
                return 0;
            }
            catch (Exception exception)
            {
                LogFailure(exception);
                return -1;
            }
        }

        // Step 9, new executable, commit phase after the transaction finished.
        internal static int UpgradeCommitForMsi(string transaction)
        {
            try
            {
                InstallationSafety.RequireSystemMaintenance();
                string? keep = UpgradeLifecyclePolicy.CommitKeeps(UpgradeMarker.Read(), transaction);
                CleanUpAfterUpgrade(transaction, () => UpgradeLifecyclePolicy.DeleteStaleSnapshots(new MachineDataUpgradeFiles(), Utils.AppDataPath, keep));
                Utils.Log("Upgrade " + transaction + " committed.", Utils.LOG_ID_INSTALLER);
                return 0;
            }
            catch (Exception exception)
            {
                LogFailure(exception);
                return -1;
            }
        }

        // Runs once the service is up. A snapshot or marker left behind blocks
        // nothing (the marker is ignored once Windows Installer finishes, and the
        // next upgrade deletes stale snapshots), so each failure only warns.
        private static void CleanUpAfterUpgrade(string transaction, Action deleteSnapshots)
        {
            try { deleteSnapshots(); }
            catch (Exception exception) { Warn("Upgrade " + transaction + ": the policy snapshot under %ProgramData%\\SecureWall\\upgrade could not be deleted. " + exception.Message); }
            try { UpgradeMarker.Clear(transaction); }
            catch (Exception exception) { Warn("Upgrade " + transaction + ": the upgrade-in-progress marker could not be deleted. " + exception.Message); }
        }

        // Controller repair asks this first, to show the upgrade notice instead of
        // a repair error. EnsureServiceInstalledAndRunning enforces the same gate.
        internal static bool ServiceStartBlockedByUpgrade() => UpgradeMarker.BlocksAutomaticStart();

        // Emergency release. Main calls this only for SYSTEM MSI removal or
        // failed-install rollback after the machine-data guard rejected the tree.
        // It authenticates the protected installation and LocalSystem service
        // registration, never reads or repairs the rejected tree, skips the hosts
        // restore whose backup lives there, and restores Windows Firewall and
        // audit state only from their HKLM journals.
        //
        // Its text log is dropped and MSI discards stderr, so the first failure
        // of each step also writes event 1001 naming the step, and a failed
        // release writes one summary event. Ordinary maintenance gets 1001 from
        // RunMaintenance.
        internal static int ReleaseForMsiWithRejectedMachineData(bool failedInstallRollback)
        {
            var log = new EmergencyFailureLog(failedInstallRollback ? "/msi-rollback-install" : "/msi-cleanup");
            emergency = log;
            try
            {
                int result = CleanupForMsi(failedInstallRollback, false);
                if (result != 0)
                    MaintenanceEventLog.ReportError(MaintenanceEventLog.MaintenanceFailureId, log.Summary(result));
                return result;
            }
            finally { emergency = null; }
        }

        private static EmergencyFailureLog? emergency;
        private static string emergencyStep = "";

        // Names the operation that LogFailure reports while the emergency release runs.
        private static void Stage(string step) => emergencyStep = step;
        private static Action Staged(string step, Action action) => () => { Stage(step); action(); };

        private static int CleanupForMsi(bool failedInstallRollback, bool machineDataTrusted = true)
        {
            try
            {
                Stage("installation and service registration check");
                InstallationSafety.RequireSystemMaintenance();
#if !DEBUG
                if (machineDataTrusted) InstallationSafety.RequireProtectedMachineData();
#endif
                ValidateRegisteredServiceImage(!machineDataTrusted);
                if (!machineDataTrusted)
                    Warn("SecureWall machine data was rejected. Running the SYSTEM emergency release: the rejected directory is preserved unread, and only SecureWall-owned firewall objects, compatibility rules, audit journal entries, the scheduled task and the service are removed.");
                if (ServiceExists())
                {
                    if (failedInstallRollback || !machineDataTrusted)
                    {
                        // Registration and protected installation are validated
                        // above. Suppress queued recovery even if startup exits
                        // naturally during the graceful wait. The deny baseline's
                        // provider names no service, so this does not release it.
                        Stage("service disable");
                        using var scm = new ServiceControlManager();
                        scm.SetStartupMode(TinyWallService.SERVICE_NAME, ServiceStartMode.Disabled);
                        scm.SetRestartOnFailure(TinyWallService.SERVICE_NAME, false);
                    }
                    Stage("service stop");
                    using var service = new ServiceController(TinyWallService.SERVICE_NAME);
                    var timer = Stopwatch.StartNew();
                    LifecycleServiceState State()
                    {
                        service.Refresh();
                        return (LifecycleServiceState)service.Status;
                    }
                    bool StopGracefully()
                    {
                        try
                        {
                            return ServiceLifecyclePolicy.StopGracefully(State, () => service.Stop(),
                                () => timer.Elapsed, System.Threading.Thread.Sleep);
                        }
                        catch (InvalidOperationException exception)
                        {
                            // Stop() also throws when the service exits on its own
                            // first; that is not a failure and writes no Error event.
                            bool stopped;
                            try { stopped = State() == LifecycleServiceState.Stopped; }
                            catch { LogFailure(exception); throw; }
                            if (stopped) Utils.LogException(exception, Utils.LOG_ID_INSTALLER);
                            else LogFailure(exception);
                            return stopped;
                        }
                    }
                    int result = -1;
                    ServiceLifecyclePolicy.Cleanup(failedInstallRollback, StopGracefully,
                        () => {
                            Stage("service process termination");
                            ValidateRegisteredServiceImage();
                            InstallationSafety.TerminateFailedInstallService();
                        },
                        () => {
                            Stage("stopped-service confirmation");
                            return ServiceLifecyclePolicy.WaitUntil(() => State() == LifecycleServiceState.Stopped,
                                ServiceLifecyclePolicy.CleanupGrace, () => timer.Elapsed, System.Threading.Thread.Sleep);
                        },
                        () => result = CleanupStoppedInstallation(failedInstallRollback, machineDataTrusted));
                    return result;
                }
                return CleanupStoppedInstallation(failedInstallRollback, machineDataTrusted);
            }
            catch (Exception exception)
            {
                LogFailure(exception);
                return -1;
            }
        }

        // Utils.Log silently drops entries while machine data is rejected, so
        // maintenance warnings also go to stderr and the Application event log.
        private static void Warn(string message)
        {
            Utils.Log(message, Utils.LOG_ID_INSTALLER);
            try { Console.Error.WriteLine(message); } catch { }
            try
            {
                if (EventLog.SourceExists(TinyWallService.SERVICE_NAME))
                    EventLog.WriteEntry(TinyWallService.SERVICE_NAME, message, EventLogEntryType.Warning);
            }
            catch { }
        }

        private static void LogFailure(Exception exception)
        {
            Utils.LogException(exception, Utils.LOG_ID_INSTALLER);
            try { Console.Error.WriteLine(exception); } catch { }
            string? line = emergency?.Record(emergencyStep, exception);
            if (line != null)
                MaintenanceEventLog.ReportError(MaintenanceEventLog.MaintenanceFailureId, line);
        }

        private static bool ServiceExists()
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var service = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + TinyWallService.SERVICE_NAME);
            return service != null;
        }

        private static void RequireServiceNotPendingDeletion()
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var service = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + TinyWallService.SERVICE_NAME);
            if (service?.GetValue("DeleteFlag") is int flag && flag == 1)
                throw new InvalidOperationException("SecureWall service deletion is pending. Close Services and other service-management tools, or restart Windows, before installing again.");
        }

        private static void ValidateRegisteredServiceImage(bool requireSystemAccount = false)
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var service = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + TinyWallService.SERVICE_NAME);
            if (service == null) return;
            string image = (service.GetValue("ImagePath") as string ?? "").Trim();
            string expected = "\"" + Utils.ExecutablePath + "\"";
            if (!string.Equals(image, expected, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(image, expected + " /service", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The SecureWall service belongs to another executable. Remove it using its original installer.");
            if (requireSystemAccount)
                ControllerRepairPolicy.RequireRegistration(image, Utils.ExecutablePath,
                    service.GetValue("ObjectName") as string ?? "", service.GetValue("Type") is int type ? type : 0);
        }

        // Ordering and failure handling live in ProtectionReleasePolicy. Ordinary
        // removal aborts at the first failure and keeps the deny baseline and the
        // service registration that permits a retry. Failed first-install rollback
        // continues past hosts and audit failures, always removes the registration,
        // and removes the baseline once the compatibility rules are gone.
        private static int CleanupStoppedInstallation(bool failedInstallRollback = false, bool machineDataTrusted = true)
        {
            try
            {
                Stage("stopped-service check");
#if !DEBUG
                if (machineDataTrusted) InstallationSafety.RequireProtectedMachineData();
#endif
                ValidateRegisteredServiceImage();
                if (ServiceExists())
                {
                    using var service = new ServiceController(TinyWallService.SERVICE_NAME);
                    if (service.Status != ServiceControllerStatus.Stopped)
                        throw new InvalidOperationException("Service must be stopped before cleanup.");
                }
            }
            catch (Exception exception)
            {
                LogFailure(exception);
                return -1;
            }

            // Restore hosts before releasing any persistent protection or the
            // service registration that permits recovery to be retried. Crash
            // cleanup is independent of service disposal. Keep WFP protection
            // when compatibility restoration fails.
            bool released = ProtectionReleasePolicy.Release(failedInstallRollback, machineDataTrusted,
                Staged("hosts restore", () => { using HostsFileManager hosts = new(); hosts.DisableHostsFile(); }),
                Staged("Windows Firewall compatibility restore", () => WindowsFirewall.RestoreOwnedState()),
                () => { Stage("Windows Firewall compatibility rule check"); return WindowsFirewall.OwnedRulesAbsent(); },
                Staged("audit policy restore", RestoreAuditPolicy),
                Staged("controller termination", TerminateInstallationControllers),
                Staged("WFP object removal", RemoveWfpObjects),
                RemoveRegistration,
                Warn, LogFailure);
            RemoveProductRegistry();
            return released ? 0 : -1;
        }

        // Last step of explicit removal. The MSI no longer deletes HKLM\SOFTWARE\SecureWall,
        // because an in-place upgrade must keep the recovery journals below it. The key
        // goes only once both journals hold no restorable record; otherwise it stays for
        // the retry that needs them, minus the upgrade marker. Failure is logged and
        // does not fail the removal: nothing left here weakens protection.
        private static void RemoveProductRegistry()
        {
            Stage("SecureWall registry key removal");
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = machine.OpenSubKey(ProductRegistryPolicy.KeyPath, true);
                if (key == null) return;
                // First and unconditional: a marker left here would refuse /install
                // during the next fresh installation.
                key.DeleteValue(UpgradeLifecyclePolicy.MarkerValue, false);
                key.Flush();
                int firewallRecords = 0;
                using (var firewall = key.OpenSubKey(ProductRegistryPolicy.FirewallJournal))
                    if (firewall != null) firewallRecords = firewall.ValueCount + firewall.SubKeyCount;
                int auditRecords = RegistryAuditPolicyJournal.Instance.ReadAll(out _).Count;
                string[] values = key.GetValueNames();
                ProductRegistryPolicy.Removal? removal = ProductRegistryPolicy.Plan(key.GetSubKeyNames(), values, firewallRecords, auditRecords);
                if (removal == null)
                {
                    Warn("HKLM\\" + ProductRegistryPolicy.KeyPath + " is kept because a recovery journal below it still holds a record.");
                    return;
                }
                foreach (string subkey in removal.Value.Subkeys)
                    key.DeleteSubKeyTree(subkey, false);
                foreach (string value in removal.Value.Values)
                    key.DeleteValue(value, false);
                key.Flush();
                key.Close();
                if (removal.Value.DeleteKey)
                    machine.DeleteSubKeyTree(ProductRegistryPolicy.KeyPath, false);
            }
            catch (Exception exception)
            {
                LogFailure(exception);
            }
        }

        // A healthy journal entry that fails to restore aborts ordinary cleanup,
        // like the firewall-rule journal: removal deletes HKLM\Software\SecureWall
        // once the journals are clear and standalone /uninstall removes the service
        // that would retry, so continuing would leave the audit policy modified
        // with no record.
        // Malformed records hold no recoverable value and never block uninstall.
        private static void RestoreAuditPolicy()
        {
            try
            {
                AuditPolicyRestoreResult restored = FirewallLogWatcher.RestoreAuditPolicyFromJournal();
                foreach (string record in restored.Malformed)
                    Utils.Log("Skipped malformed audit policy recovery record " + record + " under HKLM\\" + RegistryAuditPolicyJournal.RecoveryKey + "; it holds no recoverable value and does not block uninstall.", Utils.LOG_ID_INSTALLER);
            }
            catch (AuditPolicyRestoreException exception)
            {
                foreach (Guid subcategory in exception.FailedSubcategories)
                    Warn("Audit policy subcategory " + subcategory.ToString("B") + " could not be restored to its original value. The journal at HKLM\\" + RegistryAuditPolicyJournal.RecoveryKey + " is kept; fix the audit policy write (auditpol /get /subcategory:" + subcategory.ToString("B") + ") and run the uninstall again.");
                throw;
            }
            catch (Exception)
            {
                // The journal could not even be read: its contents are unknown, so
                // keep it until an operator can inspect it.
                Warn("The audit policy journal at HKLM\\" + RegistryAuditPolicyJournal.RecoveryKey + " could not be read or restored; it is kept in place.");
                throw;
            }
        }

        // Terminate only controllers from this exact installation.
        private static void TerminateInstallationControllers()
        {
            using var ownProc = Process.GetCurrentProcess();
            int ownPid = ownProc.Id;
            Process[] procs = Process.GetProcesses();
            try
            {
                foreach (Process p in procs)
                {
                    try
                    {
                        if ((p.Id != ownPid) &&
                            string.Equals(p.ProcessName, Path.GetFileNameWithoutExtension(Utils.ExecutablePath), StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(p.MainModule?.FileName, Utils.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                        {
                            ProcessManager.TerminateProcess(p, 2000);
                        }
                    }
                    catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_INSTALLER); }
                }
            }
            finally
            {
                foreach (var p in procs)
                    p.Dispose();
            }
        }

        // Removes only objects under SecureWall's provider key, in one transaction.
        private static void RemoveWfpObjects()
        {
            using var WfpEngine = new Engine("SecureWall Uninstall Session", "", FWPM_SESSION_FLAGS.None, 5000);
            using var trx = WfpEngine.BeginTransaction();
            TinyWallServer.DeleteWfpObjects(WfpEngine, true);
            trx.Commit();
        }

        private static bool RemoveRegistration()
        {
            bool succeeded = true;
            Stage("scheduled task removal");
            try
            {
                // Disable automatic start of controller
                var taskService = new TaskScheduler.TaskScheduler();
                taskService.Connect();
                taskService.GetFolder(@"\").DeleteTask(CONTROLLER_START_TASKSCH_NAME, 0);
            }
            catch (System.Runtime.InteropServices.COMException e) when (e.HResult == unchecked((int)0x80070002)) { }
            catch (Exception e) { succeeded = false; LogFailure(e); }

            Stage("service registration removal");
            try
            {
                if (ServiceExists())
                    ManagedInstallerClass.InstallHelper(new string[] { "/u", Utils.ExecutablePath });
                InstallationSafety.EnsureStoppedServiceDeletion();
                if (ServiceExists())
                    Warn("SecureWall service deletion was accepted by Windows and is pending open handles. Close Services and other service-management tools, or restart Windows, before installing again.");
            }
            catch (Exception e) { succeeded = false; LogFailure(e); }

            return succeeded;
        }

        // Install and repair callers pass strictGuards: true and refuse on a guard failure.
        // The running service passes false after its policy committed: it logs the failure,
        // skips the privileged repairs below, and returns false so the controller is warned.
        internal static bool EnsureHealth(string logContext, bool strictGuards)
        {
            try
            {
                InstallationSafety.RequireNoTinyWall();
                InstallationSafety.RequireProtectedInstallation();
            }
            catch (Exception e) when (!strictGuards)
            {
                Utils.Log("Installation safety check failed after service start. Enforcement continues with the committed policy; service and controller registration were not refreshed.", logContext);
                Utils.LogException(e, logContext);
                return false;
            }
            // Ensure that TinyWall's dependencies can be started
            try
            {
                EnsureServiceDependencies();
            }
            catch (InvalidOperationException e)
            {
                if (!Utils.IsSystemShuttingDown())
                    Utils.LogException(e, logContext);
            }
            catch (Exception e)
            {
                Utils.LogException(e, logContext);
            }

            // Ensure that TinyWall itself can be started
            try
            {
                using var scm = new ServiceControlManager();
                scm.SetStartupMode(TinyWallService.SERVICE_NAME, ServiceStartMode.Automatic);
                scm.SetRestartOnFailure(TinyWallService.SERVICE_NAME,
                    ServiceLifecyclePolicy.RestartDelays, ServiceLifecyclePolicy.RestartResetPeriod);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                const int E_FAIL = -2147467259;
                if (!(Utils.IsSystemShuttingDown() && (e.ErrorCode == E_FAIL)))
                    Utils.LogException(e, logContext);
            }
            catch (Exception e)
            {
                Utils.LogException(e, logContext);
            }

            // Ensure that controller will be started for users
            try
            {
                const string INTERACTIVE_GROUP_SID = "S-1-5-4";
                const int TASK_CREATE_OR_UPDATE = 6;
                var taskService = new TaskScheduler.TaskScheduler();
                taskService.Connect();
                var td = taskService.NewTask(0);
                td.RegistrationInfo.Author = "SecureWall contributors; based on TinyWall by Károly Pados";
                td.RegistrationInfo.Description = "This task starts the SecureWall tray icon when a user is logged in.";
                td.Settings.Enabled = true;
                td.Principal.GroupId = INTERACTIVE_GROUP_SID;
                td.Principal.LogonType = _TASK_LOGON_TYPE.TASK_LOGON_INTERACTIVE_TOKEN;
                td.Principal.RunLevel = _TASK_RUNLEVEL.TASK_RUNLEVEL_HIGHEST;
                td.Settings.Compatibility = _TASK_COMPATIBILITY.TASK_COMPATIBILITY_V2;
                td.Settings.Enabled = true;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.Hidden = false;
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.ExecutionTimeLimit = "PT0S";
                td.Settings.MultipleInstances = _TASK_INSTANCES_POLICY.TASK_INSTANCES_PARALLEL;
                td.Triggers.Create(_TASK_TRIGGER_TYPE2.TASK_TRIGGER_LOGON);
                var act = (IExecAction)td.Actions.Create(_TASK_ACTION_TYPE.TASK_ACTION_EXEC);
                act.Path = Utils.ExecutablePath;
                taskService.GetFolder(@"\").RegisterTaskDefinition(CONTROLLER_START_TASKSCH_NAME, td, TASK_CREATE_OR_UPDATE, null, null, _TASK_LOGON_TYPE.TASK_LOGON_INTERACTIVE_TOKEN);
            }
            catch (System.Runtime.InteropServices.COMException e)
            {
                if (!Utils.IsSystemShuttingDown())
                    Utils.LogException(e, logContext);
            }
            catch (Exception e)
            {
                Utils.LogException(e, logContext);
            }
            return true;
        }

        private static void EnsureServiceDependencies()
        {
            // First, do a recursive scan of all service dependencies
            var deps = new HashSet<string>();
            foreach (var srv in TinyWallService.ServiceDependencies)
            {
                using var sc = new ServiceController(srv);
                ScanServiceDependencies(sc, deps);
            }

            // Enable services we need
            using var scm = new ServiceControlManager();
            foreach (string srv in deps)
            {
                // MpsSvc is an explicit prerequisite. Do not override an
                // administrator's disabled Windows Firewall service setting.
                if (string.Equals(srv, "MpsSvc", StringComparison.OrdinalIgnoreCase)) continue;
                if (scm.GetStartupMode(srv) == (uint)ServiceStartMode.Disabled)
                    scm.SetStartupMode(srv, ServiceStartMode.Manual);
            }
        }

        private static void ScanServiceDependencies(ServiceController srv, HashSet<string> allDeps)
        {
            if (allDeps.Contains(srv.ServiceName))
                return;

            allDeps.Add(srv.ServiceName);

            ServiceController[] ServicesDependedOn = srv.ServicesDependedOn;
            foreach (ServiceController depOn in ServicesDependedOn)
            {
                ScanServiceDependencies(depOn, allDeps);
            }
        }
    }
}

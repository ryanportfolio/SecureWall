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

        internal static bool EnsureServiceInstalledAndRunning(string logContext, bool installing)
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                bool mayInstall = ControllerRepairPolicy.MayInstall(installing, identity.IsSystem);
                if (installing) InstallationSafety.RequireSystemMaintenance();
                InstallationSafety.RequireNoTinyWall();
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
                if (mayInstall)
                {
                    if (!ServiceExists())
                        ManagedInstallerClass.InstallHelper(new string[] { "/i", Utils.ExecutablePath });
                    EnsureHealth(logContext);
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

        private static int CleanupForMsi(bool failedInstallRollback)
        {
            try
            {
                InstallationSafety.RequireSystemMaintenance();
#if !DEBUG
                InstallationSafety.RequireProtectedMachineData();
#endif
                ValidateRegisteredServiceImage();
                if (ServiceExists())
                {
                    if (failedInstallRollback)
                    {
                        // Registration and protected installation are validated
                        // above. Suppress queued recovery even if startup exits
                        // naturally during the graceful wait.
                        using var scm = new ServiceControlManager();
                        scm.SetStartupMode(TinyWallService.SERVICE_NAME, ServiceStartMode.Disabled);
                        scm.SetRestartOnFailure(TinyWallService.SERVICE_NAME, false);
                    }
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
                            Utils.LogException(exception, Utils.LOG_ID_INSTALLER);
                            return State() == LifecycleServiceState.Stopped;
                        }
                    }
                    int result = -1;
                    ServiceLifecyclePolicy.Cleanup(failedInstallRollback, StopGracefully,
                        () => {
                            ValidateRegisteredServiceImage();
                            InstallationSafety.TerminateFailedInstallService();
                        },
                        () => ServiceLifecyclePolicy.WaitUntil(() => State() == LifecycleServiceState.Stopped,
                            ServiceLifecyclePolicy.CleanupGrace, () => timer.Elapsed, System.Threading.Thread.Sleep),
                        () => result = CleanupStoppedInstallation());
                    return result;
                }
                return CleanupStoppedInstallation();
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_INSTALLER);
                return -1;
            }
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

        private static int CleanupStoppedInstallation()
        {
            try
            {
#if !DEBUG
                InstallationSafety.RequireProtectedMachineData();
#endif
                ValidateRegisteredServiceImage();
                if (ServiceExists())
                {
                    using var service = new ServiceController(TinyWallService.SERVICE_NAME);
                    if (service.Status != ServiceControllerStatus.Stopped)
                        throw new InvalidOperationException("Service must be stopped before cleanup.");
                }
                // Restore hosts before releasing any persistent protection or the
                // service registration that permits recovery to be retried.
                using (HostsFileManager hosts = new())
                    hosts.DisableHostsFile();

                // Crash cleanup is independent of service disposal. Keep WFP
                // protection when compatibility restoration fails.
                WindowsFirewall.RestoreOwnedState();
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_INSTALLER);
                return -1;
            }

            // A healthy journal entry that fails to restore aborts cleanup, like the
            // firewall-rule journal above: the MSI deletes HKLM\Software\SecureWall on
            // uninstall and standalone /uninstall removes the service that would retry,
            // so continuing would leave the audit policy modified with no record.
            // Malformed records hold no recoverable value and never block uninstall.
            try
            {
                AuditPolicyRestoreResult restored = FirewallLogWatcher.RestoreAuditPolicyFromJournal();
                foreach (string record in restored.Malformed)
                    Utils.Log("Skipped malformed audit policy recovery record " + record + " under HKLM\\" + RegistryAuditPolicyJournal.RecoveryKey + "; it holds no recoverable value and does not block uninstall.", Utils.LOG_ID_INSTALLER);
            }
            catch (AuditPolicyRestoreException exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_INSTALLER);
                foreach (Guid subcategory in exception.FailedSubcategories)
                    Utils.Log("Uninstall aborted: audit policy subcategory " + subcategory.ToString("B") + " could not be restored to its original value. The journal at HKLM\\" + RegistryAuditPolicyJournal.RecoveryKey + " is kept; fix the audit policy write (auditpol /get /subcategory:" + subcategory.ToString("B") + ") and run the uninstall again.", Utils.LOG_ID_INSTALLER);
                return -1;
            }
            catch (Exception exception)
            {
                // The journal could not even be read: its contents are unknown, so
                // keep the installation until an operator can inspect it.
                Utils.LogException(exception, Utils.LOG_ID_INSTALLER);
                Utils.Log("Uninstall aborted: the audit policy journal at HKLM\\" + RegistryAuditPolicyJournal.RecoveryKey + " could not be read or restored; it is kept in place.", Utils.LOG_ID_INSTALLER);
                return -1;
            }

            // Terminate only controllers from this exact installation.
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

            try
            {
                // Remove persistent WFP objects
                using var WfpEngine = new Engine("SecureWall Uninstall Session", "", FWPM_SESSION_FLAGS.None, 5000);
                using var trx = WfpEngine.BeginTransaction();
                TinyWallServer.DeleteWfpObjects(WfpEngine, true);
                trx.Commit();
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_INSTALLER);
                return -1;
            }


            bool succeeded = true;
            try
            {
                // Disable automatic start of controller
                var taskService = new TaskScheduler.TaskScheduler();
                taskService.Connect();
                taskService.GetFolder(@"\").DeleteTask(CONTROLLER_START_TASKSCH_NAME, 0);
            }
            catch (System.Runtime.InteropServices.COMException e) when (e.HResult == unchecked((int)0x80070002)) { }
            catch (Exception e) { succeeded = false; Utils.LogException(e, Utils.LOG_ID_INSTALLER); }

            try
            {
                if (ServiceExists())
                    ManagedInstallerClass.InstallHelper(new string[] { "/u", Utils.ExecutablePath });
                InstallationSafety.EnsureStoppedServiceDeletion();
                if (ServiceExists())
                    Utils.Log("SecureWall service deletion was accepted by Windows and is pending open handles. Close Services and other service-management tools, or restart Windows, before installing again.", Utils.LOG_ID_INSTALLER);
            }
            catch (Exception e) { succeeded = false; Utils.LogException(e, Utils.LOG_ID_INSTALLER); }

            return succeeded ? 0 : -1;
        }

        internal static void EnsureHealth(string logContext)
        {
            InstallationSafety.RequireNoTinyWall();
            InstallationSafety.RequireProtectedInstallation();
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
                scm.SetRestartOnFailure(TinyWallService.SERVICE_NAME, true);
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

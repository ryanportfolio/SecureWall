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
                InstallationSafety.RequireNoTinyWall();
                InstallationSafety.RequireProtectedInstallation();
                ValidateRegisteredServiceImage();
                RequireServiceNotPendingDeletion();
                WindowsFirewall.RequireServiceRunning();
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, logContext);
                return false;
            }
            if (TinyWallDoctor.IsServiceRunning(logContext, installing))
                return true;

            if (Utils.RunningAsAdmin())
            {
                // Run installers
                try
                {
                    if (!ServiceExists())
                        ManagedInstallerClass.InstallHelper(new string[] { "/i", Utils.ExecutablePath });
                }
                catch(Exception e)
                {
                    Utils.LogException(e, logContext);
                    return false;
                }

                // Ensure dependencies
                TinyWallDoctor.EnsureHealth(logContext);

                // Start service
                try
                {
                    using var sc = new ServiceController(TinyWallService.SERVICE_NAME);
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        sc.Start();
                    }
                    sc.WaitForStatus(ServiceControllerStatus.Running, ServiceLifecyclePolicy.StartupTimeout);
                }
                catch (Exception e)
                {
                    Utils.LogException(e, logContext);
                    return false;
                }
            }
            else
            {
                // We are not running as admin.
                try
                {
                    using Process p = Utils.StartProcess(Utils.ExecutablePath, "/install", true);
                    p.WaitForExit();
                    return (p.ExitCode == 0);
                }
                catch (Exception e)
                {
                    Utils.LogException(e, logContext);
                    return false;
                }
            }

            return true;
        }

        internal static int Uninstall()
        {
            if (!Utils.RunningAsAdmin()) return -1;
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

        private static void ValidateRegisteredServiceImage()
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var service = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + TinyWallService.SERVICE_NAME);
            if (service == null) return;
            string image = (service.GetValue("ImagePath") as string ?? "").Trim();
            string expected = "\"" + Utils.ExecutablePath + "\"";
            if (!string.Equals(image, expected, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(image, expected + " /service", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The SecureWall service belongs to another executable. Remove it using its original installer.");
        }

        private static int CleanupStoppedInstallation()
        {
            try
            {
                ValidateRegisteredServiceImage();
                if (ServiceExists())
                {
                    using var service = new ServiceController(TinyWallService.SERVICE_NAME);
                    if (service.Status != ServiceControllerStatus.Stopped)
                        throw new InvalidOperationException("Service must be stopped before cleanup.");
                }
                // Crash cleanup is independent of service disposal. Keep WFP
                // protection when compatibility restoration fails.
                WindowsFirewall.RestoreOwnedState();
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_INSTALLER);
                return -1;
            }

            // Audit policy left behind by a crashed service is not a security
            // exposure the way orphan allow rules are: log and keep going so WFP
            // cleanup still runs. The journal stays for a later retry.
            try { FirewallLogWatcher.RestoreAuditPolicyFromJournal(); }
            catch (Exception exception) { Utils.LogException(exception, Utils.LOG_ID_INSTALLER); }

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
                // Put back the user's original hosts file
                using HostsFileManager hosts = new();
                hosts.DisableHostsFile();
            }
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

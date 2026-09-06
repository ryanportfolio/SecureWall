using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using pylorak.TinyWall.Prompting;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using pylorak.Windows.Services;

namespace pylorak.TinyWall.Installer
{
    internal static class InstallationSafety
    {
        internal static void RequireNoTinyWall()
        {
            ServiceController[] services = ServiceController.GetServices();
            try
            {
                foreach (ServiceController service in services)
                    if (InstallationConflictGuard.HasTinyWallService(new[] { service.ServiceName }))
                        throw new InvalidOperationException("Uninstall TinyWall and reboot before activating SecureWall.");
            }
            finally
            {
                foreach (ServiceController service in services) service.Dispose();
            }
        }

        internal static void RequireSystemMaintenance()
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!identity.IsSystem)
                throw new UnauthorizedAccessException("MSI maintenance requires the LocalSystem execution context.");
            RequireProtectedInstallation();
        }

        internal static void RequireProtectedInstallation()
        {
            string executable = Path.GetFullPath(Utils.ExecutablePath);
            string directory = Path.GetDirectoryName(executable)!;
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!IsBelow(directory, programFiles) && !IsBelow(directory, programFilesX86))
                throw new UnauthorizedAccessException("SecureWall must be installed in a protected Program Files directory. Use the MSI installer.");

            // Validate all ancestors before enumeration, then every private assembly,
            // config and localization directory. A writable dependency is SYSTEM code.
            for (DirectoryInfo? ancestor = new DirectoryInfo(directory); ancestor != null; ancestor = ancestor.Parent)
                CheckPath(ancestor.FullName, true, !string.Equals(ancestor.FullName, directory, StringComparison.OrdinalIgnoreCase));
            CheckTree(directory);
        }

        // A failed MSI first-install rollback is the sole caller. Never reopen the
        // PID for termination: keep the authenticated handle until exit is observed.
        internal static void TerminateFailedInstallService()
        {
            RequireSystemMaintenance();
            IntPtr manager = OpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                IntPtr service = OpenService(manager, TinyWallService.SERVICE_NAME, 4);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    ServiceStatus status = ReadStatus(service);
                    if (status.CurrentState == 1) return;
                    using var process = OpenProcess(0x1000 | 0x100000 | 1, false, status.ProcessId);
                    if (process.IsInvalid)
                    {
                        int error = Marshal.GetLastWin32Error();
                        // The worker can exit between the SCM query and OpenProcess.
                        // Only a fresh stopped observation permits cleanup to continue.
                        ServiceLifecyclePolicy.RequireStoppedAfterProcessOpenFailure(
                            () => (LifecycleServiceState)ReadStatus(service).CurrentState,
                            new Win32Exception(error));
                        return;
                    }
                    var path = new StringBuilder(32768);
                    int length = path.Capacity;
                    if (!QueryFullProcessImageName(process, 0, path, ref length))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!OpenProcessToken(process, 8, out SafeAccessTokenHandle token))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    bool system;
                    using (token)
                    using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                        system = identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
                    using var ownProcess = Process.GetCurrentProcess();
                    ServiceStatus current = ReadStatus(service);
                    if (!ServiceLifecyclePolicy.IsRollbackProcess(status.ProcessId, (uint)ownProcess.Id,
                        current.ProcessId, current.ServiceType, path.ToString(), Utils.ExecutablePath, system))
                        throw new InvalidOperationException("Rollback process is not this installation's dedicated SYSTEM service.");

                    // A killed service must not restart while its durable state and
                    // MSI payload are removed. Do this only after authenticating it.
                    using (var scm = new ServiceControlManager())
                    {
                        scm.SetStartupMode(TinyWallService.SERVICE_NAME, ServiceStartMode.Disabled);
                        scm.SetRestartOnFailure(TinyWallService.SERVICE_NAME, false);
                    }
                    current = ReadStatus(service);
                    if (current.CurrentState != 1)
                    {
                        if (current.ProcessId != status.ProcessId || current.ServiceType != 0x10)
                            throw new InvalidOperationException("Service process changed during rollback.");
                        if (!TerminateProcess(process, 1) && WaitForSingleObject(process, 0) != 0)
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    if (WaitForSingleObject(process, (uint)ServiceLifecyclePolicy.ProcessExitTimeout.TotalMilliseconds) != 0)
                        throw new InvalidOperationException("Service process did not exit within the rollback deadline.");
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        // ServiceInstaller does not check DeleteService's return value. Confirm
        // deletion through SCM; an accepted pending deletion is sufficient for
        // removal, but still prevents a new CreateService until handles close.
        internal static void EnsureStoppedServiceDeletion()
        {
            IntPtr manager = OpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                IntPtr service = OpenService(manager, TinyWallService.SERVICE_NAME, 0x10000 | 4);
                if (service == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 1060 || error == 1072) return;
                    throw new Win32Exception(error);
                }
                try
                {
                    if (ReadStatus(service).CurrentState != 1)
                        throw new InvalidOperationException("Service must be stopped before deletion.");
                    if (!DeleteService(service))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 1072) throw new Win32Exception(error);
                    }
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        private static ServiceStatus ReadStatus(IntPtr service)
        {
            if (!QueryServiceStatusEx(service, 0, out ServiceStatus status, Marshal.SizeOf(typeof(ServiceStatus)), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return status;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
                ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
        [DllImport("advapi32.dll")]
        private static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        private static bool IsBelow(string path, string root)
            => !string.IsNullOrEmpty(root) && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        private static void CheckTree(string directory)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                bool isDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
                CheckPath(entry, isDirectory);
                if (isDirectory) CheckTree(entry);
            }
        }

        private static bool IsTrusted(SecurityIdentifier sid)
            => sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
               sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
               sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; // TrustedInstaller

        private static void CheckPath(string path, bool directory, bool ancestor = false)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Installation paths must not contain reparse points: " + path);
            FileSystemSecurity security = directory
                ? (FileSystemSecurity)Directory.GetAccessControl(path)
                : File.GetAccessControl(path);
            var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            if (descriptor.DiscretionaryAcl == null)
                throw new UnauthorizedAccessException("Installation path has an unrestricted DACL: " + path);
            if (!IsTrusted((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))))
                throw new UnauthorizedAccessException("Installation path has an untrusted owner: " + path);

            FileSystemRights writes =
                FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            if (!ancestor)
                writes |= FileSystemRights.WriteData | FileSystemRights.AppendData |
                    FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow &&
                    (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                    ((rule.FileSystemRights & writes) != 0 || ((uint)rule.FileSystemRights & 0x50000000U) != 0) &&
                    !IsTrusted((SecurityIdentifier)rule.IdentityReference))
                    throw new UnauthorizedAccessException("Installation path grants untrusted write access: " + path);
            }
        }
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    internal static class PipeServerIdentity
    {
        private const uint ProcessQueryAndSynchronize = 0x00101000;
        private static readonly Lazy<bool> ProtectedInstallation = new Lazy<bool>(() =>
        {
            Installer.InstallationSafety.RequireProtectedInstallation();
            return true;
        }, System.Threading.LazyThreadSafetyMode.PublicationOnly);

        // Called by the SYSTEM service before publishing its pipe. These rights reveal
        // its image and lifetime only; clients never need its primary token.
        internal static void AllowAuthenticatedProcessQueries()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!identity.IsSystem)
                    throw new UnauthorizedAccessException("Only the SYSTEM service may publish process query access.");
            IntPtr process = GetCurrentProcess();
            GetKernelObjectSecurity(process, 4, null, 0, out uint needed);
            if (needed == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] bytes = new byte[needed];
            if (!GetKernelObjectSecurity(process, 4, bytes, needed, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            bytes = CreateProcessQueryDescriptor(bytes);
            if (!SetKernelObjectSecurity(process, 4, bytes))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        internal static byte[] CreateProcessQueryDescriptor(byte[] bytes)
        {
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            RawAcl acl = descriptor.DiscretionaryAcl ??
                throw new InvalidOperationException("Service process has no restrictive DACL.");
            foreach (GenericAce ace in acl)
                if (ace is QualifiedAce denied && denied.AceQualifier == AceQualifier.AccessDenied &&
                    ((uint)denied.AccessMask & (ProcessQueryAndSynchronize | 0xf0000000U)) != 0)
                    throw new UnauthorizedAccessException("Service process denies the required read-only identity access.");
            var sid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            // Preserve all existing ACEs, including denies. Insert the explicit allow
            // before inherited entries to keep a canonical DACL.
            int index = 0;
            while (index < acl.Count && (acl[index].AceFlags & AceFlags.Inherited) == 0) index++;
            acl.InsertAce(index, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
                (int)ProcessQueryAndSynchronize, sid, false, null));
            bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            return bytes;
        }

        // Retain the process handle throughout the exchange to prevent PID reuse.
        internal static SafeProcessHandle Authenticate(NamedPipeClientStream pipe, string pipeName, int testProcessId = 0)
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid == 0)
                throw new InvalidOperationException("Cannot identify the pipe server.");
            SafeProcessHandle process = OpenProcess(ProcessQueryAndSynchronize, false, pid);
            try
            {
                if (process.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var path = new StringBuilder(32768);
                int length = path.Capacity;
                if (!QueryFullProcessImageName(process, 0, path, ref length) ||
                    !PipeClientAuthorization.IsExpectedExecutable(path.ToString(), pylorak.Windows.ProcessManager.ExecutablePath))
                    throw new InvalidOperationException("Unexpected pipe server executable.");
#if DEBUG
                // Only an explicit same-process, unique self-test pipe may skip SCM checks.
                if (testProcessId != 0)
                {
                    if (testProcessId != Process.GetCurrentProcess().Id || pid != testProcessId ||
                        !IsSelfTestPipe(pipeName) || WaitForSingleObject(process, 0) != 258)
                        throw new InvalidOperationException("Invalid pipe self-test identity.");
                    return process;
                }
#endif
                if (testProcessId != 0 || !ProtectedInstallation.Value ||
                    !IsRunningSystemService(pid, path.ToString()) ||
                    WaitForSingleObject(process, 0) != 258 ||
                    !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint verifiedPid) || verifiedPid != pid)
                    throw new InvalidOperationException("Pipe server is not the SecureWall SYSTEM service.");
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

#if DEBUG
        internal static bool IsSelfTestPipe(string name) =>
            name.StartsWith("SecureWallPipeSelfTest-", StringComparison.Ordinal) &&
            Guid.TryParseExact(name.Substring("SecureWallPipeSelfTest-".Length), "N", out _);
#endif

        private static bool IsRunningSystemService(uint connectedPid, string image)
        {
            IntPtr manager = OpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) return false;
            try
            {
                // SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS, available to normal users.
                IntPtr service = OpenService(manager, TinyWallService.SERVICE_NAME, 5);
                if (service == IntPtr.Zero) return false;
                try
                {
                    if (!HasRunningPid(service, connectedPid)) return false;
                    QueryServiceConfig(service, IntPtr.Zero, 0, out uint needed);
                    if (needed == 0 || needed > 65536) return false;
                    IntPtr buffer = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        if (!QueryServiceConfig(service, buffer, needed, out _)) return false;
                        ServiceConfiguration config = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
                        if (!PipeServerAuthorization.IsExpectedConfiguration(
                            Marshal.PtrToStringUni(config.BinaryPathName), image,
                            Marshal.PtrToStringUni(config.ServiceStartName), config.ServiceType)) return false;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                    return HasRunningPid(service, connectedPid);
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        private static bool HasRunningPid(IntPtr service, uint pid) =>
            QueryServiceStatusEx(service, 0, out ServiceStatus status,
                Marshal.SizeOf(typeof(ServiceStatus)), out _) &&
            status.CurrentState == 4 && status.ServiceType == 0x10 && status.ProcessId == pid && pid != 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceConfiguration
        {
            internal uint ServiceType, StartType, ErrorControl;
            internal IntPtr BinaryPathName, LoadOrderGroup;
            internal uint TagId;
            internal IntPtr Dependencies, ServiceStartName, DisplayName;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
                ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetKernelObjectSecurity(IntPtr handle, uint information,
            [Out] byte[]? descriptor, uint length, out uint needed);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetKernelObjectSecurity(IntPtr handle, uint information, byte[] descriptor);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryServiceConfig(IntPtr service, IntPtr configuration, uint size, out uint needed);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
        [DllImport("advapi32.dll")]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}

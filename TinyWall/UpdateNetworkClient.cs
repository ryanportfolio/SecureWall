using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    // The service's view of a controller that asked for an update permit: the pipe client's
    // process, opened with SYNCHRONIZE and query rights. The handle is held for the lease's
    // lifetime, so it signals the exit and pins the process object: the process ID cannot
    // be reused while the lease exists.
    internal sealed class UpdateNetworkClient : IDisposable
    {
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;

        private readonly ProcessWaitHandle Wait;
        private RegisteredWaitHandle? Registration;

        internal uint ProcessId { get; }
        internal long CreationTime { get; }
        internal string UserSid { get; }

        private UpdateNetworkClient(uint processId, long creationTime, string userSid, SafeWaitHandle process)
        {
            ProcessId = processId;
            CreationTime = creationTime;
            UserSid = userSid;
            Wait = new ProcessWaitHandle(process);
        }

        internal bool HasExited => Wait.WaitOne(0);

        // Opens the pipe client and confirms it is SecureWall's own image running under an
        // account the permit may name. Returns null with a reason otherwise.
        internal static UpdateNetworkClient? TryOpen(uint processId, string expectedImage, out string reason)
        {
            reason = string.Empty;
            SafeWaitHandle process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process.IsInvalid)
            {
                reason = "the requesting process could not be opened (error " + Marshal.GetLastWin32Error() + ")";
                process.Dispose();
                return null;
            }
            try
            {
                string image = QueryImage(process);
                if (!PipeClientAuthorization.IsExpectedExecutable(Utils.GetLongPathName(image), expectedImage))
                {
                    reason = "the requesting process is not SecureWall's own program: " + image;
                    process.Dispose();
                    return null;
                }
                if (!GetProcessTimes(process, out long creation, out _, out _, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                string? sid = QueryUser(process);
                if (!UpdateNetworkGrantPolicy.IsAllowedUser(sid))
                {
                    reason = "the requesting process runs as an account the permit cannot name: " + (sid ?? "(unknown)");
                    process.Dispose();
                    return null;
                }
                return new UpdateNetworkClient(processId, creation, sid!, process);
            }
            catch (Exception exception) when (exception is Win32Exception || exception is UnauthorizedAccessException ||
                exception is ArgumentException || exception is System.Security.SecurityException)
            {
                reason = "the requesting process could not be identified: " + exception.Message;
                process.Dispose();
                return null;
            }
        }

        // Runs `due` once, when the process exits or after `timeout`, whichever is first.
        internal void Watch(TimeSpan timeout, Action due)
        {
            Registration?.Unregister(null);
            long milliseconds = Math.Max(1, (long)Math.Ceiling(timeout.TotalMilliseconds));
            Registration = ThreadPool.RegisterWaitForSingleObject(Wait, (_, _) => due(), null, milliseconds, true);
        }

        public void Dispose()
        {
            Registration?.Unregister(null);
            Registration = null;
            Wait.Dispose();
        }

        private static string QueryImage(SafeWaitHandle process)
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            if (!QueryFullProcessImageName(process, 0, buffer, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return buffer.ToString(0, size);
        }

        private static string? QueryUser(SafeWaitHandle process)
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User?.Value;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        private sealed class ProcessWaitHandle : WaitHandle
        {
            internal ProcessWaitHandle(SafeWaitHandle process)
            {
                SafeWaitHandle = process;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeWaitHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(SafeWaitHandle process, uint flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(SafeWaitHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(SafeWaitHandle process, uint desiredAccess, out IntPtr token);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

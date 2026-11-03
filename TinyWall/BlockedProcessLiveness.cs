using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace pylorak.TinyWall
{
    // Whether the process an audit event named is still the one that made the blocked
    // connection. Called after the SCM snapshot: a process that is still running, was created
    // before the drop and runs the dropped image was alive while the snapshot was read, so an
    // empty service list for its PID means it ran no service. A process that exited, or a PID
    // reused by a newer process, gives no such evidence.
    internal static class BlockedProcessLiveness
    {
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint WAIT_TIMEOUT = 0x00000102;

        internal static bool IsSameLiveProcess(uint processId, string applicationPath, DateTimeOffset dropUtc)
        {
            if (processId == 0 || string.IsNullOrWhiteSpace(applicationPath))
                return false;
            using SafeWaitHandle process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process.IsInvalid)
                return false;
            try
            {
                if (WaitForSingleObject(process, 0) != WAIT_TIMEOUT)
                    return false;
                if (!GetProcessTimes(process, out long creation, out _, out _, out _) || creation > dropUtc.ToFileTime())
                    return false;
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (!QueryFullProcessImageName(process, 0, buffer, ref size))
                    return false;
                string image = Utils.GetLongPathName(buffer.ToString(0, size));
                return string.Equals(
                    Path.GetFullPath(image),
                    Path.GetFullPath(Utils.GetLongPathName(applicationPath)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is Win32Exception || exception is ArgumentException ||
                exception is NotSupportedException || exception is PathTooLongException)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeWaitHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(SafeWaitHandle process, uint flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(SafeWaitHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
    }
}

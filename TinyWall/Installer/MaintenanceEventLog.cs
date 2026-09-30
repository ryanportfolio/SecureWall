using System;
using System.Runtime.InteropServices;

namespace pylorak.TinyWall.Installer
{
    // Writes one Error entry to the Windows Application log. It uses no machine-data
    // path, so it works when the guard has rejected that tree. The MSI registers the
    // source with the .NET message file; an unregistered source still reaches the
    // Application log, only without a message-text lookup. Failures are ignored.
    internal static class MaintenanceEventLog
    {
        internal const uint GuardFailureId = 1000;
        internal const uint MaintenanceFailureId = 1001;
        private const ushort EVENTLOG_ERROR_TYPE = 0x0001;

        internal static void ReportError(uint eventId, string line)
        {
            try
            {
                IntPtr source = RegisterEventSourceW(null, SecureWallProduct.Name);
                if (source == IntPtr.Zero) return;
                try { ReportEventW(source, EVENTLOG_ERROR_TYPE, 0, eventId, IntPtr.Zero, 1, 0, new[] { line }, IntPtr.Zero); }
                finally { DeregisterEventSource(source); }
            }
            catch
            {
                // Reporting must not replace the original failure.
            }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RegisterEventSourceW(string? serverName, string sourceName);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeregisterEventSource(IntPtr eventLog);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReportEventW(IntPtr eventLog, ushort type, ushort category, uint eventId,
            IntPtr userSid, ushort numStrings, uint dataSize, string[] strings, IntPtr rawData);
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    public class ServicePidMap
    {
        private readonly ServiceProcessIndex Cache;

        public ServicePidMap() : this(false) { }

        internal ServicePidMap(bool requireStableIdentity)
        {
            // A new map is a fresh bulk snapshot, never a time-based identity cache.
            using ScmHandle scm = OpenSCManager(null, null, 0x0004);
            if (scm.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            Cache = ServiceProcessSnapshot.Index(ServiceProcessSnapshot.Read(size => ReadSnapshot(scm, size)), requireStableIdentity);
        }

        internal bool IsUncertain(uint pid) => Cache.IsUncertain(pid);

        public HashSet<string> GetServicesInPid(uint pid) => Cache.Services.TryGetValue(pid, out HashSet<string>? names)
            ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static ServiceSnapshotRead ReadSnapshot(ScmHandle scm, int size)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint resume = 0;
                // SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE; no group filter.
                bool complete = EnumServicesStatusEx(scm, 0, 0x30, 1, buffer, (uint)size,
                    out _, out uint count, ref resume, null);
                if (!complete)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 234) throw new Win32Exception(error);
                    return new ServiceSnapshotRead(false, Array.Empty<ServiceProcessEntry>());
                }
                if (resume != 0) throw new InvalidOperationException("SCM returned an incomplete process snapshot.");
                int stride = Marshal.SizeOf<EnumServiceStatusProcess>();
                if (count > size / stride) throw new InvalidOperationException("SCM returned an invalid service count.");
                var entries = new List<ServiceProcessEntry>((int)count);
                for (int index = 0; index < count; ++index)
                {
                    var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(IntPtr.Add(buffer, index * stride));
                    entries.Add(new ServiceProcessEntry(Marshal.PtrToStringUni(entry.ServiceName) ?? string.Empty,
                        entry.Status.ProcessId, entry.Status.CurrentState));
                }
                return new ServiceSnapshotRead(true, entries);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatusProcess
        {
            internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
                ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EnumServiceStatusProcess
        {
            internal IntPtr ServiceName, DisplayName;
            internal ServiceStatusProcess Status;
        }

        private sealed class ScmHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public ScmHandle() : base(true) { }
            protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ScmHandle OpenSCManager(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumServicesStatusEx(ScmHandle scm, int level, uint type, uint state,
            IntPtr buffer, uint size, out uint needed, out uint returned, ref uint resume, string? group);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}

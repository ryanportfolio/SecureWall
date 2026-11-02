using System.Collections.Generic;
using pylorak.Windows;

namespace pylorak.TinyWall
{
    internal class ProcessInfo
    {
        public uint Pid;
        public string Path;
        public UwpPackageList.Package? Package;
        public HashSet<string> Services;

        private ProcessInfo(uint pid, string path, UwpPackageList.Package? package, HashSet<string> services)
        {
            Pid = pid;
            Path = path;
            Package = package;
            Services = services;
        }

#if DEBUG
        // Sample rows for the Debug-only /uipreview switch: no process or service lookup.
        internal static ProcessInfo CreatePreview(uint pid, string path, params string[] services)
        {
            return new ProcessInfo(pid, path, null, new HashSet<string>(services));
        }
#endif

        public static ProcessInfo Create(uint pid, UwpPackageList uwp, ServicePidMap servicePids)
        {
            return new ProcessInfo(
                pid,
                Utils.GetPathOfProcessUseTwService(pid, GlobalInstances.Controller),
                uwp.FindPackageForProcess(pid),
                servicePids.GetServicesInPid(pid)
            );
        }
        public static ProcessInfo Create(uint pid, string path, UwpPackageList uwp, ServicePidMap servicePids)
        {
            return new ProcessInfo(
                pid,
                path,
                uwp.FindPackageForProcess(pid),
                servicePids.GetServicesInPid(pid)
            );
        }
        public static ProcessInfo Create(uint pid, string path, string? packageId, UwpPackageList uwp, ServicePidMap servicePids)
        {
            return new ProcessInfo(
                pid,
                path,
                uwp.FindPackage(packageId),
                servicePids.GetServicesInPid(pid)
            );
        }
    }
}


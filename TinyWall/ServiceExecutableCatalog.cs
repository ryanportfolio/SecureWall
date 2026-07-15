using Microsoft.Win32;
using pylorak.TinyWall.Prompting;
using System;
using System.Collections.Generic;
using System.IO;

namespace pylorak.TinyWall
{
    internal sealed class ServiceExecutableCatalog
    {
        private const int ServiceWin32Mask = 0x30;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
        private readonly object SyncRoot = new();
        private HashSet<string> ExecutablePaths = new(StringComparer.OrdinalIgnoreCase);
        private DateTime NextRefreshUtc = DateTime.MinValue;
        private bool Complete;

        internal bool TryContains(string applicationPath, out bool contains)
        {
            contains = false;
            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(applicationPath);
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is PathTooLongException)
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (DateTime.UtcNow >= NextRefreshUtc && !Refresh())
                    return false;

                if (!Complete)
                    return false;

                contains = ExecutablePaths.Contains(normalizedPath);
                return true;
            }
        }

        private bool Refresh()
        {
            try
            {
                var executablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool complete = true;
                string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                using RegistryKey? services = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services",
                    writable: false);
                if (services == null || string.IsNullOrWhiteSpace(windowsDirectory))
                    return false;

                foreach (string serviceName in services.GetSubKeyNames())
                {
                    using RegistryKey? service = services.OpenSubKey(serviceName, writable: false);
                    if (service?.GetValue("Type") is not int serviceType ||
                        (serviceType & ServiceWin32Mask) == 0)
                    {
                        continue;
                    }

                    string? executable = ServiceImagePath.TryExtractExecutable(
                        service.GetValue("ImagePath") as string,
                        windowsDirectory);
                    if (executable == null)
                        complete = false;
                    else
                        executablePaths.Add(executable);
                }

                ExecutablePaths = executablePaths;
                Complete = complete;
                NextRefreshUtc = DateTime.UtcNow.Add(RefreshInterval);
                return true;
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
                Complete = false;
                NextRefreshUtc = DateTime.UtcNow.AddSeconds(5);
                return false;
            }
        }
    }
}

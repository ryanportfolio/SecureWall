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
        private ServiceExecutableInventory? Inventory;
        private DateTime NextRefreshUtc = DateTime.MinValue;
        private string LastReportedHealth = string.Empty;

        // Catalog health is reported by ReportHealth in the service log, separately from the
        // per-executable answers. Unknown means "cannot rule out", never "is a service".
        internal ServiceRegistrationStatus Lookup(string applicationPath)
        {
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
                return ServiceRegistrationStatus.Unknown;
            }

            lock (SyncRoot)
            {
                if (DateTime.UtcNow >= NextRefreshUtc)
                    Refresh();

                return Inventory?.Lookup(normalizedPath) ?? ServiceRegistrationStatus.Unknown;
            }
        }

        private void Refresh()
        {
            try
            {
                var registrations = new List<ServiceImageRegistration>();
                string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                using RegistryKey? services = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services",
                    writable: false);
                if (services == null || string.IsNullOrWhiteSpace(windowsDirectory))
                    throw new InvalidOperationException("The service registry key or Windows directory is unavailable.");

                foreach (string serviceName in services.GetSubKeyNames())
                {
                    using RegistryKey? service = services.OpenSubKey(serviceName, writable: false);
                    if (service?.GetValue("Type") is not int serviceType ||
                        (serviceType & ServiceWin32Mask) == 0)
                    {
                        continue;
                    }

                    registrations.Add(new ServiceImageRegistration(serviceName, service.GetValue("ImagePath")));
                }

                ServiceExecutableInventory inventory = ServiceExecutableInventory.Build(registrations, windowsDirectory);
                Inventory = inventory;
                NextRefreshUtc = DateTime.UtcNow.Add(RefreshInterval);
                ReportHealth(inventory);
            }
            catch (Exception exception)
            {
                // A stale inventory could miss a newly registered service; answer Unknown instead.
                Inventory = null;
                NextRefreshUtc = DateTime.UtcNow.AddSeconds(5);
                if (!string.Equals(LastReportedHealth, "unavailable", StringComparison.Ordinal))
                {
                    LastReportedHealth = "unavailable";
                    Utils.Log("Service inventory is unavailable. Prompts for executables without exact SCM " +
                        "attribution stay non-allowable until it can be read.", Utils.LOG_ID_SERVICE);
                    Utils.LogException(exception, Utils.LOG_ID_SERVICE);
                }
            }
        }

        // Log only when the set of problem registrations changes, not on every refresh.
        private void ReportHealth(ServiceExecutableInventory inventory)
        {
            string unresolved = string.Join(", ", inventory.UnresolvedServices);
            string unusable = string.Join(", ", inventory.UnusableServices);
            string health = unresolved + "|" + unusable;
            string previous = LastReportedHealth;
            if (string.Equals(previous, health, StringComparison.Ordinal))
                return;
            LastReportedHealth = health;
            if (unresolved.Length != 0)
                Utils.Log("Service inventory could not resolve the image path of: " + unresolved +
                    ". Prompts for executables named in those registrations stay non-allowable.", Utils.LOG_ID_SERVICE);
            if (unusable.Length != 0)
                Utils.Log("Service inventory skipped registrations without a text image path (SCM cannot start them): " +
                    unusable + ".", Utils.LOG_ID_SERVICE);
            if (unresolved.Length == 0 && unusable.Length == 0 && previous.Length != 0)
                Utils.Log("Service inventory resolved every registered Win32 service image path.", Utils.LOG_ID_SERVICE);
        }
    }
}

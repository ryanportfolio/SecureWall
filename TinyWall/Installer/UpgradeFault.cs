using System.Diagnostics;
#if UPGRADE_FAULT_TEST
using System;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;
using pylorak.TinyWall.Prompting;
#endif

namespace pylorak.TinyWall.Installer
{
    // Fault points for the in-place upgrade VM cases (docs/HARDENING-VALIDATION.md,
    // "In-place upgrade"). Only the UpgradeFaultTest build configuration defines
    // UPGRADE_FAULT_TEST; every other build drops each Trip call, its argument and
    // this method's body, so Release artifacts contain no fault injection. That
    // configuration also stamps the build (informational version, assembly
    // metadata, MSI property and ProductName suffix), and the release builder and
    // installer contract test refuse any of those stamps.
    //
    // Fault build behavior: the REG_SZ value UpgradeFaultTest under
    // HKLM\SOFTWARE\SecureWall (64-bit view, writable only by administrators and
    // SYSTEM) names one point, either alone ("upgrade-start-fail") or for one
    // version ("0.5.1:upgrade-start-fail"), so fault builds can serve as both the
    // old and the new version of one upgrade. A point ending in "-pause" waits up
    // to 10 minutes, until the value changes; every other point throws.
    internal static class UpgradeFault
    {
        [Conditional("UPGRADE_FAULT_TEST")]
        internal static void Trip(string point)
        {
#if UPGRADE_FAULT_TEST
            if (!Matches(point)) return;
            if (point.EndsWith("-pause", StringComparison.Ordinal))
            {
                var timer = Stopwatch.StartNew();
                while (Matches(point) && timer.Elapsed < TimeSpan.FromMinutes(10))
                    Thread.Sleep(1000);
                return;
            }
            throw new InvalidOperationException("Upgrade fault test: " + point);
#endif
        }

#if UPGRADE_FAULT_TEST
        private static bool Matches(string point)
        {
            string? configured = Configured();
            if (configured == null) return false;
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            string own = version.Major + "." + version.Minor + "." + version.Build + ":";
            return string.Equals(configured, point, StringComparison.Ordinal) ||
                string.Equals(configured, own + point, StringComparison.Ordinal);
        }

        private static string? Configured()
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = machine.OpenSubKey(ProductRegistryPolicy.KeyPath);
                return key?.GetValue("UpgradeFaultTest") as string;
            }
            catch { return null; }
        }
#endif
    }
}

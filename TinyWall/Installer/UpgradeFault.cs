using System.Diagnostics;
#if UPGRADE_FAULT_TEST
using System;
using System.Threading;
using Microsoft.Win32;
using pylorak.TinyWall.Prompting;
#endif

namespace pylorak.TinyWall.Installer
{
    // Fault points for the in-place upgrade VM cases (docs/HARDENING-VALIDATION.md,
    // U5 to U15). Only the UpgradeFaultTest build configuration defines
    // UPGRADE_FAULT_TEST; every other build drops each Trip call, its argument and
    // this method's body, so Release artifacts contain no fault injection. The
    // installer contract test refuses a release package built from that
    // configuration.
    //
    // Fault build behavior: the REG_SZ value UpgradeFaultTest under
    // HKLM\SOFTWARE\SecureWall (64-bit view, writable only by administrators and
    // SYSTEM) names one point. upgrade-start-pause waits up to 10 minutes, until
    // the value changes; every other point throws.
    internal static class UpgradeFault
    {
        [Conditional("UPGRADE_FAULT_TEST")]
        internal static void Trip(string point)
        {
#if UPGRADE_FAULT_TEST
            if (!string.Equals(Configured(), point, StringComparison.Ordinal)) return;
            if (point == "upgrade-start-pause")
            {
                var timer = Stopwatch.StartNew();
                while (string.Equals(Configured(), point, StringComparison.Ordinal) && timer.Elapsed < TimeSpan.FromMinutes(10))
                    Thread.Sleep(1000);
                return;
            }
            throw new InvalidOperationException("Upgrade fault test: " + point);
#endif
        }

#if UPGRADE_FAULT_TEST
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

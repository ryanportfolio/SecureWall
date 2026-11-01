using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    // Ownership of HKLM\SOFTWARE\SecureWall (64-bit view). The MSI owns only its
    // InstallDir and Platform values and removes them itself; it no longer deletes
    // the key, because the Windows Firewall and audit recovery journals live below
    // it and must survive an in-place upgrade. Explicit removal (/msi-cleanup,
    // /uninstall, failed first-install rollback) deletes everything SecureWall's
    // executable wrote there as its last step, and only once both journals hold
    // no restorable record. No registry access lives here.
    internal static class ProductRegistryPolicy
    {
        internal const string KeyPath = @"SOFTWARE\SecureWall";
        internal const string FirewallJournal = "FirewallRecovery";
        internal const string AuditJournal = "AuditRecovery";
        internal static readonly string[] MsiValues = { "InstallDir", "Platform" };

        internal readonly struct Removal
        {
            internal readonly string[] Subkeys, Values;
            internal readonly bool DeleteKey;
            internal Removal(string[] subkeys, string[] values, bool deleteKey)
            { Subkeys = subkeys; Values = values; DeleteKey = deleteKey; }
        }

        // firewallRecords counts every value or subkey under FirewallRecovery: its
        // only record is the original notification settings, so anything left is
        // unrestored state. auditRecords counts decodable audit records only;
        // malformed ones hold no recoverable value and never block removal.
        internal static bool JournalsCleared(int firewallRecords, int auditRecords) =>
            firewallRecords == 0 && auditRecords == 0;

        // Returns null when a journal still holds a record: the key stays, because
        // the next removal attempt needs that record. The caller deletes the
        // upgrade marker before asking, whatever the answer.
        internal static Removal? Plan(IEnumerable<string> subkeys, IEnumerable<string> values,
            int firewallRecords, int auditRecords)
        {
            if (!JournalsCleared(firewallRecords, auditRecords)) return null;
            var deleteSubkeys = new List<string>(subkeys);
            var deleteValues = new List<string>();
            bool msiValuesRemain = false;
            foreach (string value in values)
            {
                if (IsMsiValue(value)) msiValuesRemain = true;
                else deleteValues.Add(value);
            }
            // With MSI values present the MSI removes them next, and the key with
            // them. Without (standalone /uninstall after MSI removal, or the x86
            // package, whose values live in the 32-bit view), delete the key here.
            return new Removal(deleteSubkeys.ToArray(), deleteValues.ToArray(), !msiValuesRemain);
        }

        private static bool IsMsiValue(string name)
        {
            foreach (string owned in MsiValues)
                if (string.Equals(owned, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}

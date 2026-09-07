using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace pylorak.TinyWall
{
    // Mirrors WindowsFirewall's FirewallRecovery journal: one 64-bit view for every
    // installer and service architecture, flushed before the audit policy changes.
    // Value name is the subcategory GUID, value is the original AuditPolicyFlags.
    internal sealed class RegistryAuditPolicyJournal : IAuditPolicyJournal
    {
        internal const string RecoveryKey = @"SOFTWARE\SecureWall\AuditRecovery";
        private const AuditPolicyFlags ValidFlags = AuditPolicyFlags.Success | AuditPolicyFlags.Failure | AuditPolicyFlags.None;

        internal static RegistryAuditPolicyJournal Instance { get; } = new RegistryAuditPolicyJournal();

        private RegistryAuditPolicyJournal()
        {
        }

        private static RegistryKey? OpenRecoveryKey(bool writable)
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return writable ? machine.CreateSubKey(RecoveryKey, true) : machine.OpenSubKey(RecoveryKey, false);
        }

        private static string ValueName(Guid subcategory) => subcategory.ToString("B");

        // Unreadable records fail closed: the original value is unknown, and
        // overwriting the record with a snapshot of the already-modified live
        // policy would lose it for good.
        private static AuditPolicyFlags Decode(string name, object? value)
        {
            if (TryDecode(value, out AuditPolicyFlags original))
                return original;
            throw new InvalidOperationException("Invalid audit policy recovery record: " + name);
        }

        private static bool TryDecode(object? value, out AuditPolicyFlags original)
        {
            original = AuditPolicyFlags.Unchanged;
            if (!(value is int raw) || ((AuditPolicyFlags)unchecked((uint)raw) & ~ValidFlags) != 0)
                return false;
            original = (AuditPolicyFlags)unchecked((uint)raw);
            return true;
        }

        public bool TryRead(Guid subcategory, out AuditPolicyFlags original)
        {
            original = AuditPolicyFlags.Unchanged;
            using var journal = OpenRecoveryKey(false);
            object? value = journal?.GetValue(ValueName(subcategory));
            if (value == null)
                return false;
            original = Decode(ValueName(subcategory), value);
            return true;
        }

        public void Write(Guid subcategory, AuditPolicyFlags original)
        {
            using var journal = OpenRecoveryKey(true)!;
            journal.SetValue(ValueName(subcategory), unchecked((int)(uint)original), RegistryValueKind.DWord);
            journal.Flush();
        }

        public void Clear(Guid subcategory)
        {
            using var journal = OpenRecoveryKey(true)!;
            journal.DeleteValue(ValueName(subcategory), false);
            journal.Flush();
        }

        // One malformed record must not block recovery of the healthy ones: it is
        // skipped, left in place, and reported by name through malformed.
        public IReadOnlyList<KeyValuePair<Guid, AuditPolicyFlags>> ReadAll(out IReadOnlyList<string> malformed)
        {
            var entries = new List<KeyValuePair<Guid, AuditPolicyFlags>>();
            var skipped = new List<string>();
            malformed = skipped;
            using var journal = OpenRecoveryKey(false);
            if (journal == null)
                return entries;
            foreach (string name in journal.GetValueNames())
            {
                if (Guid.TryParse(name, out Guid subcategory) && TryDecode(journal.GetValue(name), out AuditPolicyFlags original))
                    entries.Add(new KeyValuePair<Guid, AuditPolicyFlags>(subcategory, original));
                else
                    skipped.Add(name);
            }
            return entries;
        }
    }
}

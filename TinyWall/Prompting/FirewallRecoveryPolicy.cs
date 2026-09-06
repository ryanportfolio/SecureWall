using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    internal static class FirewallRecoveryPolicy
    {
        internal const string Group = "SecureWall.Compatibility.{A84D563A-5299-4C49-A4CE-EA63B69BE471}";
        internal const string Inbound = Group + ".Inbound";
        internal const string Outbound = Group + ".Outbound";

        internal static bool CanSkipStoppedServiceRecovery(bool stopped, bool hasRecoveryRecord)
            => stopped && !hasRecoveryRecord;

        internal static bool OwnsRule(string? name, string? group)
        {
            return string.Equals(group, Group, StringComparison.Ordinal) &&
                (string.Equals(name, Inbound, StringComparison.Ordinal) ||
                 string.Equals(name, Outbound, StringComparison.Ordinal));
        }

        // The production COM adapter uses this complete preflight before any
        // name-based removal or acquisition. COM may overwrite duplicate names.
        internal static string[] OwnedRuleNames(IEnumerable<KeyValuePair<string, string>> rules)
        {
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                if (OwnsRule(rule.Key, rule.Value)) owned.Add(rule.Key);
                else if (string.Equals(rule.Key, Inbound, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(rule.Key, Outbound, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Firewall rule ownership collision: " + rule.Key);
            }
            var names = new string[owned.Count];
            owned.CopyTo(names);
            return names;
        }

        internal static void AcquireForRules(IEnumerable<KeyValuePair<string, string>> rules, Action acquire)
        {
            OwnedRuleNames(rules);
            acquire();
        }

        // Persist all three original values in one atomic registry value before
        // changing any profile. Retain it across restarts until restoration succeeds.
        internal static int Encode(bool domain, bool privateProfile, bool publicProfile)
            => 0x100 | (domain ? 1 : 0) | (privateProfile ? 2 : 0) | (publicProfile ? 4 : 0);

        internal static bool[] Decode(int value)
        {
            if ((value & ~7) != 0x100)
                throw new InvalidOperationException("Invalid firewall notification recovery record.");
            return new[] { (value & 1) != 0, (value & 2) != 0, (value & 4) != 0 };
        }

        internal static void Acquire(Func<int?> read, Func<bool[]> capture, Action<int> saveDurably, Action apply)
        {
            int? saved = read();
            if (saved.HasValue) Decode(saved.Value);
            else
            {
                bool[] original = capture();
                saveDurably(Encode(original[0], original[1], original[2]));
            }
            apply();
        }

        internal static void Restore(Action removeRules, Func<int?> read, Action<bool[]> restore, Action clearDurably)
        {
            removeRules();
            int? saved = read();
            if (!saved.HasValue) return;
            restore(Decode(saved.Value));
            clearDurably();
        }
    }
}

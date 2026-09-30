using System;
using System.Collections.Generic;
using System.Linq;

namespace pylorak.TinyWall.Prompting
{
    internal readonly struct WfpSublayerRecord
    {
        internal WfpSublayerRecord(Guid key, Guid? providerKey)
        {
            Key = key;
            ProviderKey = providerKey;
        }

        internal Guid Key { get; }
        internal Guid? ProviderKey { get; }
    }

    internal readonly struct WfpFilterRecord
    {
        // A filter without a provider has providerKey Guid.Empty.
        internal WfpFilterRecord(Guid key, Guid providerKey, Guid sublayerKey, string name)
        {
            Key = key;
            ProviderKey = providerKey;
            SublayerKey = sublayerKey;
            Name = name ?? string.Empty;
        }

        internal Guid Key { get; }
        internal Guid ProviderKey { get; }
        internal Guid SublayerKey { get; }
        internal string Name { get; }
    }

    internal sealed class WfpCoexistenceReport
    {
        private readonly List<string> _conflicts = new();

        internal IReadOnlyList<string> Conflicts => _conflicts;
        internal bool TinyWallResidue { get; private set; }
        internal bool ForeignResidue { get; private set; }
        internal bool Clear => _conflicts.Count == 0;

        internal void Add(string conflict, bool tinyWall)
        {
            _conflicts.Add(conflict);
            if (tinyWall) TinyWallResidue = true;
            else ForeignResidue = true;
        }

        internal string Describe(string operation)
        {
            var parts = new List<string> { operation + " refused. Found: " + string.Join("; ", _conflicts) + "." };
            if (TinyWallResidue) parts.Add(WfpCoexistencePolicy.TinyWallCleanupStep);
            if (ForeignResidue) parts.Add(WfpCoexistencePolicy.ForeignCleanupStep);
            return string.Join(" ", parts);
        }
    }

    // Decides from a WFP inventory whether SecureWall may register or remove its
    // persistent objects. Pure so the rules are testable without BFE.
    internal static class WfpCoexistencePolicy
    {
        internal static readonly Guid TinyWallProviderKey = new("{66CA412C-4453-4F1E-A973-C16E433E34D0}");

        internal const string TinyWallCleanupStep =
            "Cleanup: remove TinyWall with its own uninstaller (reinstall the same TinyWall version first if it is already gone), reboot, " +
            "and confirm that `netsh wfp show state` output no longer contains 66CA412C-4453-4F1E-A973-C16E433E34D0.";

        internal const string ForeignCleanupStep =
            "Cleanup: uninstall the software that owns the listed WFP provider, reboot, and confirm with `netsh wfp show filters` that the listed filters are gone. " +
            "Do not delete WFP objects by hand without local-console access.";

        // Conflicts that block registering SecureWall's baseline.
        internal static WfpCoexistenceReport FindActivationConflicts(Guid ownProvider,
            IEnumerable<Guid> providers, IEnumerable<WfpSublayerRecord> sublayers, IEnumerable<WfpFilterRecord> filters,
            IEnumerable<Guid> ownSublayerKeys, IEnumerable<Guid> legacySublayerKeys)
        {
            var own = new HashSet<Guid>(ownSublayerKeys);
            var legacy = new HashSet<Guid>(legacySublayerKeys);
            var report = new WfpCoexistenceReport();

            if (providers.Contains(TinyWallProviderKey))
                report.Add("TinyWall's WFP provider " + Format(TinyWallProviderKey) + " is still registered", true);

            foreach (WfpSublayerRecord sublayer in sublayers)
            {
                if (sublayer.ProviderKey == ownProvider)
                    continue;
                if (own.Contains(sublayer.Key))
                    report.Add("SecureWall sublayer " + Format(sublayer.Key) + " is registered by provider " + Format(sublayer.ProviderKey),
                        sublayer.ProviderKey == TinyWallProviderKey);
                else if (legacy.Contains(sublayer.Key))
                    report.Add("TinyWall sublayer " + Format(sublayer.Key) + " is still registered by provider " + Format(sublayer.ProviderKey), true);
            }

            own.UnionWith(legacy);
            AddForeignFilters(report, ownProvider, filters, own);
            return report;
        }

        // Conflicts that block removing SecureWall's own sublayers. TinyWall objects
        // outside SecureWall-owned sublayers never block SecureWall's removal.
        internal static WfpCoexistenceReport FindRemovalConflicts(Guid ownProvider,
            IEnumerable<WfpSublayerRecord> sublayers, IEnumerable<WfpFilterRecord> filters,
            IEnumerable<Guid> ownSublayerKeys, IEnumerable<Guid> legacySublayerKeys)
        {
            var removable = new HashSet<Guid>(SublayersToRemove(ownProvider, sublayers, ownSublayerKeys, legacySublayerKeys));
            var report = new WfpCoexistenceReport();
            AddForeignFilters(report, ownProvider, filters, removable);
            return report;
        }

        // Current and legacy sublayer keys that SecureWall's provider owns. A legacy key
        // owned by any other provider belongs to TinyWall and is never touched.
        internal static IReadOnlyList<Guid> SublayersToRemove(Guid ownProvider,
            IEnumerable<WfpSublayerRecord> sublayers, IEnumerable<Guid> ownSublayerKeys, IEnumerable<Guid> legacySublayerKeys)
        {
            var candidates = new HashSet<Guid>(ownSublayerKeys);
            candidates.UnionWith(legacySublayerKeys);
            return sublayers
                .Where(s => s.ProviderKey == ownProvider && candidates.Contains(s.Key))
                .Select(s => s.Key)
                .Distinct()
                .ToList();
        }

        private static void AddForeignFilters(WfpCoexistenceReport report, Guid ownProvider,
            IEnumerable<WfpFilterRecord> filters, HashSet<Guid> sublayerKeys)
        {
            foreach (WfpFilterRecord filter in filters)
            {
                if (filter.ProviderKey == ownProvider || !sublayerKeys.Contains(filter.SublayerKey))
                    continue;
                report.Add("filter " + Format(filter.Key) + " (\"" + filter.Name + "\") from provider " +
                    Format(filter.ProviderKey == Guid.Empty ? null : filter.ProviderKey) +
                    " in sublayer " + Format(filter.SublayerKey), filter.ProviderKey == TinyWallProviderKey);
            }
        }

        private static string Format(Guid? key) => key.HasValue ? key.Value.ToString("B").ToUpperInvariant() : "(none)";
    }
}

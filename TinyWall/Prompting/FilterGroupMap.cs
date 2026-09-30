using System.Collections.Generic;
using System.Threading;

namespace pylorak.TinyWall.Prompting
{
    // Which part of SecureWall's policy owns a blocking runtime filter. Display-only:
    // nothing grants, blocks or prompts based on this value.
    public enum FilterGroup
    {
        Unknown,
        DefaultAction,
        PortScan,
        RawSocket,
        Blocklist,
        User,
    }

    internal static class FilterGroupClassifier
    {
        internal static FilterGroup ForBlockRule(
            ulong filterWeight,
            ulong blocklistWeight,
            ulong userBlockWeight,
            ulong defaultBlockWeight)
        {
            if (filterWeight == blocklistWeight) return FilterGroup.Blocklist;
            if (filterWeight == userBlockWeight) return FilterGroup.User;
            if (filterWeight == defaultBlockWeight) return FilterGroup.DefaultAction;
            return FilterGroup.Unknown;
        }
    }

    // Maps committed runtime filter IDs to their group. Published snapshots are immutable:
    // the native net-event callback never takes a lock. Any failure clears the map, so an
    // unmapped ID reads as Unknown and is never attributed to the wrong group.
    internal sealed class FilterGroupMap
    {
        internal const int Capacity = DiagnosticFilterSet.Capacity;
        private static readonly Dictionary<ulong, FilterGroup> Empty = new Dictionary<ulong, FilterGroup>();
        private readonly object writeGuard = new object();
        private Dictionary<ulong, FilterGroup> groups = Empty;

        internal FilterGroup Lookup(ulong id) =>
            Volatile.Read(ref groups).TryGetValue(id, out FilterGroup group) ? group : FilterGroup.Unknown;

        internal void Clear()
        {
            lock (writeGuard)
                Volatile.Write(ref groups, Empty);
        }

        // Call only after the full replacement transaction has committed.
        internal bool Replace(IEnumerable<KeyValuePair<ulong, FilterGroup>>? committed) => Publish(committed, keepExisting: false);

        // Call only after an incremental transaction has committed.
        internal bool Add(IEnumerable<KeyValuePair<ulong, FilterGroup>>? committed) => Publish(committed, keepExisting: true);

        private bool Publish(IEnumerable<KeyValuePair<ulong, FilterGroup>>? committed, bool keepExisting)
        {
            lock (writeGuard)
            {
                try
                {
                    if (committed == null) { Volatile.Write(ref groups, Empty); return false; }
                    var snapshot = keepExisting
                        ? new Dictionary<ulong, FilterGroup>(Volatile.Read(ref groups))
                        : new Dictionary<ulong, FilterGroup>();
                    foreach (var pair in committed)
                    {
                        if (pair.Value == FilterGroup.Unknown) continue;
                        if (snapshot.Count >= Capacity && !snapshot.ContainsKey(pair.Key))
                        {
                            Volatile.Write(ref groups, Empty);
                            return false;
                        }
                        snapshot[pair.Key] = pair.Value;
                    }
                    Volatile.Write(ref groups, snapshot);
                    return true;
                }
                catch
                {
                    Volatile.Write(ref groups, Empty);
                    return false;
                }
            }
        }
    }
}

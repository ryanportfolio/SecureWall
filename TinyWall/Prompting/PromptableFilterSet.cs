using System;
using System.Collections.Generic;
using System.Linq;

namespace pylorak.TinyWall.Prompting
{
    internal sealed class PromptableFilterSet
    {
        private readonly object _guard = new object();
        private HashSet<ulong> _filterIds = new HashSet<ulong>();

        internal bool Contains(ulong filterId)
        {
            lock (_guard)
                return _filterIds.Contains(filterId);
        }

        internal void Replace(IEnumerable<ulong> filterIds)
        {
            if (filterIds == null)
                throw new ArgumentNullException(nameof(filterIds));

            var replacement = new HashSet<ulong>(filterIds);
            lock (_guard)
                _filterIds = replacement;
        }

        internal IReadOnlyList<ulong> Snapshot()
        {
            lock (_guard)
                return Array.AsReadOnly(_filterIds.OrderBy(id => id).ToArray());
        }
    }
}

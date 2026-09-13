using System.Collections.Generic;
using System.Threading;

namespace pylorak.TinyWall.Prompting
{
    // Published snapshots are immutable: the native callback never takes a lock.
    internal sealed class DiagnosticFilterSet
    {
        internal const int Capacity = 16384;
        private static readonly HashSet<ulong> Empty = new HashSet<ulong>();
        private HashSet<ulong> ids = Empty;
        internal bool Contains(ulong id) => Volatile.Read(ref ids).Contains(id);
        internal bool Any => Volatile.Read(ref ids).Count != 0;
        internal void Clear() => Volatile.Write(ref ids, Empty);
        internal bool Replace(IEnumerable<ulong> replacement)
        {
            try
            {
                var snapshot = new HashSet<ulong>();
                int examined = 0;
                foreach (ulong id in replacement)
                {
                    if (++examined > Capacity) { Clear(); return false; }
                    snapshot.Add(id);
                }
                Volatile.Write(ref ids, snapshot);
                return true;
            }
            catch { Clear(); return false; }
        }
    }
}

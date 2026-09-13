using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    internal sealed record ServiceProcessEntry(string Name, uint ProcessId, uint State);
    internal sealed record ServiceSnapshotRead(bool Complete, IReadOnlyList<ServiceProcessEntry> Entries);

    internal sealed class ServiceProcessIndex
    {
        internal Dictionary<uint, HashSet<string>> Services { get; } = new Dictionary<uint, HashSet<string>>();
        internal HashSet<uint> UncertainPids { get; } = new HashSet<uint>();
        internal bool UnknownPendingPid { get; set; }
        internal bool IsUncertain(uint pid) => pid == 0 || UnknownPendingPid || UncertainPids.Contains(pid);
    }

    internal static class ServiceProcessSnapshot
    {
        // Restart from zero with the largest supported buffer if needed. Never combine
        // pages gathered at different times or publish a partial identity set.
        internal static IReadOnlyList<ServiceProcessEntry> Read(Func<int, ServiceSnapshotRead> read)
        {
            ServiceSnapshotRead result = read(64 * 1024);
            if (!result.Complete) result = read(256 * 1024);
            if (!result.Complete)
                throw new InvalidOperationException("The current SCM process snapshot exceeds the complete-snapshot limit.");
            return result.Entries;
        }

        internal static ServiceProcessIndex Index(IReadOnlyList<ServiceProcessEntry> entries, bool requireStableIdentity = true)
        {
            if (entries == null) throw new InvalidOperationException("SCM returned no process snapshot.");
            var result = new ServiceProcessIndex();
            var identities = new Dictionary<string, ServiceProcessEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ServiceProcessEntry entry in entries)
            {
                if (entry == null || entry.State < 1 || entry.State > 7 || string.IsNullOrWhiteSpace(entry.Name) ||
                    entry.Name != entry.Name.Trim())
                    throw new InvalidOperationException("SCM returned an invalid service identity.");
                if (identities.TryGetValue(entry.Name, out ServiceProcessEntry? prior) &&
                    (prior.ProcessId != entry.ProcessId || prior.State != entry.State))
                    throw new InvalidOperationException("SCM returned conflicting service identities.");
                identities[entry.Name] = entry;
                if (entry.State == 1)
                {
                    if (entry.ProcessId != 0)
                        throw new InvalidOperationException("SCM returned a process for a stopped service.");
                    continue;
                }
                if (entry.State == 2 || entry.State == 3)
                {
                    if (requireStableIdentity)
                    {
                        if (entry.ProcessId == 0) result.UnknownPendingPid = true;
                        else result.UncertainPids.Add(entry.ProcessId);
                    }
                    continue;
                }
                if (!requireStableIdentity && entry.ProcessId == 0) continue;
                if (entry.ProcessId == 0)
                    throw new InvalidOperationException("SCM returned an invalid active service identity.");
                if (!result.Services.TryGetValue(entry.ProcessId, out HashSet<string>? names))
                    result.Services.Add(entry.ProcessId, names = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                names.Add(entry.Name);
            }
            return result;
        }
    }
}

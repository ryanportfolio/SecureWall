using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    internal readonly struct ExecutableAccessEntry
    {
        internal ExecutableAccessEntry(string sid, uint rights, bool allow, bool inheritOnly = false)
        {
            Sid = sid;
            Rights = rights;
            Allow = allow;
            InheritOnly = inheritOnly;
        }
        internal string Sid { get; }
        internal uint Rights { get; }
        internal bool Allow { get; }
        internal bool InheritOnly { get; }
    }

    // A conservative warning, not an AccessCheck implementation. Any untrusted
    // principal can modify the image, even if it is not in the controller token.
    // Denies are not subtracted: ACE order, group attributes and OWNER RIGHTS can
    // otherwise turn a rough rights union into an unjustified assertion of safety.
    internal static class ExecutableMutationPolicy
    {
        private const uint MutationRights = 0x500D0116;
        // GENERIC_WRITE/ALL, DELETE, WRITE_DAC, WRITE_OWNER, WRITE_DATA,
        // APPEND_DATA, WRITE_EA, WRITE_ATTRIBUTES. Directory WRITE_DATA permits
        // creating a replacement; DELETE_CHILD permits removing the old image.
        internal static bool IsTrusted(string sid)
            => string.Equals(sid, "S-1-5-18", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sid, "S-1-5-32-544", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sid, "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", StringComparison.OrdinalIgnoreCase);

        internal static bool HasRisk(string? owner, IEnumerable<ExecutableAccessEntry>? entries,
            bool directory, bool inspectionSucceeded = true)
        {
            if (!inspectionSucceeded || owner == null || entries == null) return true;
            // Ownership normally permits rewriting the DACL. Warn conservatively
            // even when OWNER RIGHTS entries might restrict that implicit right.
            if (!IsTrusted(owner)) return true;
            uint mask = MutationRights | (directory ? 0x40U : 0U);
            foreach (ExecutableAccessEntry entry in entries)
            {
                // Inherit-only ACEs do not apply to this object. Inspect the actual
                // file DACL separately; a protected file may not inherit them at all.
                if (entry.Allow && !entry.InheritOnly && !IsTrusted(entry.Sid) &&
                    (entry.Rights & mask) != 0)
                    return true;
            }
            return false;
        }
    }
}

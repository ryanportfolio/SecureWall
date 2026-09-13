using System;
using System.Collections.Generic;
using System.IO;

namespace pylorak.TinyWall.Prompting
{
    // Platform-independent decision shared by the Windows adapter and regressions.
    internal static class MachineDataPolicy
    {
        internal readonly struct Grant
        {
            internal readonly string Sid;
            internal readonly uint Rights;
            internal readonly bool Allow, InheritOnly;
            internal Grant(string sid, uint rights, bool allow = true, bool inheritOnly = false)
            { Sid = sid; Rights = rights; Allow = allow; InheritOnly = inheritOnly; }
        }

        internal static bool Trusted(string sid) => sid == "S-1-5-18" || sid == "S-1-5-32-544" ||
            sid == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

        internal static void Require(string owner, bool reparse, bool unrestrictedDacl, bool ancestor, IEnumerable<Grant> grants)
        {
            if (reparse || unrestrictedDacl || !Trusted(owner))
                throw new InvalidOperationException("Machine data has an unsafe owner, DACL, or reparse path.");
            // DELETE, DELETE_CHILD, WRITE_DAC, WRITE_OWNER, GENERIC_WRITE/ALL.
            uint writes = 0x00010000U | 0x40U | 0x00040000U | 0x00080000U | 0x50000000U;
            if (!ancestor) writes |= 0x2U | 0x4U | 0x10U | 0x100U;
            foreach (Grant grant in grants)
                if (grant.Allow && (!ancestor || !grant.InheritOnly) &&
                    (grant.Rights & writes) != 0 && !Trusted(grant.Sid))
                    throw new InvalidOperationException("Machine data grants untrusted mutation rights.");
        }

        internal static void Prepare(Action verifyAncestors, Func<bool> exists, bool allowCreation,
            Action createProtected, Action verifyTree)
        {
            verifyAncestors();
            if (!exists())
            {
                if (!allowCreation) throw new InvalidOperationException("Machine data is absent. Repair using the MSI installer.");
                createProtected();
            }
            // Also catches an attacker winning the creation race. Never repair/bless it.
            verifyTree();
        }

        // inspect authenticates owner, DACL and reparse state before returning the type.
        // enumerate must materialize its results so lookup failures stay in this scope.
        internal static void CheckTree(string root, Func<string, bool> inspect,
            Func<string, string[]> enumerate)
        {
            void RequireDirectory(string path)
            {
                if (!inspect(path))
                    throw new InvalidOperationException("Machine data root/parent must be a directory: " + path);
            }

            void Visit(string path, string? parent)
            {
                string[] entries;
                try
                {
                    bool directory = inspect(path);
                    if (!directory)
                    {
                        if (parent == null)
                            throw new InvalidOperationException("Machine data root must be a directory: " + path);
                        return;
                    }
                    entries = enumerate(path);
                }
                catch (FileNotFoundException) when (parent != null)
                {
                    RequireDirectory(parent);
                    return;
                }
                catch (DirectoryNotFoundException) when (parent != null)
                {
                    RequireDirectory(parent);
                    return;
                }
                // Recursion stays outside the catch: failures from an unsafe or
                // unvalidated branch must never be mistaken for this node disappearing.
                foreach (string entry in entries) Visit(entry, path);
            }

            Visit(root, null);
            RequireDirectory(root);
        }
    }
}

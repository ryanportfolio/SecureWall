using System;
using System.Collections.Generic;

namespace pylorak.TinyWall
{
    /// <summary>
    /// Decides which ancestor, if any, passes its "child processes inherit" rules to a process.
    /// Kept free of process and WFP access so it can be tested.
    /// </summary>
    internal static class RuleInheritance
    {
        /// <summary>
        /// True when the user has a rule for the process itself: one for its executable path
        /// or one for its AppContainer package.
        /// </summary>
        internal static bool HasOwnRule(string? path, string? packageSid, ISet<string> exeRules, ISet<string> packageRules)
        {
            return (!string.IsNullOrEmpty(path) && exeRules.Contains(path!))
                || (!string.IsNullOrEmpty(packageSid) && packageRules.Contains(packageSid!));
        }

        /// <summary>
        /// Returns the path of the ancestor whose rules the process inherits, or null.
        /// A process with its own rule inherits nothing. <paramref name="ancestors"/> runs nearest parent
        /// first and is read only as far as needed. The walk ends at the first ancestor that passes rules
        /// to children, whose path is returned, or at the first ancestor with its own rule that does not,
        /// which yields null. Ancestors with neither a path nor a rule are skipped.
        /// <paramref name="alreadyInherited"/> ends the walk with null when the process already inherits
        /// from the ancestor reached.
        /// </summary>
        internal static string? FindInheritedParent(
            string path,
            string? packageSid,
            IEnumerable<(string? Path, string? PackageSid)> ancestors,
            ISet<string> exeRules,
            ISet<string> packageRules,
            Func<string, bool> passesRulesToChildren,
            Func<string, bool> alreadyInherited)
        {
            if (HasOwnRule(path, packageSid, exeRules, packageRules))
                return null;

            foreach (var (parentPath, parentPackageSid) in ancestors)
            {
                if (!string.IsNullOrEmpty(parentPath))
                {
                    if (alreadyInherited(parentPath!))
                        return null;

                    if (passesRulesToChildren(parentPath!))
                        return parentPath;
                }

                if (HasOwnRule(parentPath, parentPackageSid, exeRules, packageRules))
                    return null;
            }

            return null;
        }
    }
}

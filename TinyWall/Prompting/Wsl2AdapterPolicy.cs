using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    internal enum Wsl2AdapterKind { Other, Wsl, UnrecognizedWsl }

    internal static class Wsl2AdapterPolicy
    {
        private const string LegacyAlias = "vEthernet (WSL)";
        private const string DecoratedPrefix = "vEthernet (WSL (";

        // WSL 2 NAT networking names its Hyper-V adapter "vEthernet (WSL)"; newer builds
        // decorate it, for example "vEthernet (WSL (Hyper-V firewall))". Any other
        // WSL-looking alias is reported so a missed adapter is visible in the log.
        internal static Wsl2AdapterKind Classify(string? alias)
        {
            if (alias == null) return Wsl2AdapterKind.Other;
            if (string.Equals(alias, LegacyAlias, StringComparison.OrdinalIgnoreCase) ||
                (alias.StartsWith(DecoratedPrefix, StringComparison.OrdinalIgnoreCase) && alias.EndsWith("))", StringComparison.Ordinal)))
                return Wsl2AdapterKind.Wsl;
            return alias.IndexOf("WSL", StringComparison.OrdinalIgnoreCase) >= 0 ? Wsl2AdapterKind.UnrecognizedWsl : Wsl2AdapterKind.Other;
        }

        // WSL permits are optional: a failure for one adapter is reported and the policy
        // transaction continues. WSL blocks are restrictive, so their registration
        // failures still abort replacement like every other block filter.
        internal static int Install<T>(IEnumerable<T> adapters, bool permit, Action<T> install, Action<T, Exception> report)
        {
            int installed = 0;
            foreach (T adapter in adapters)
            {
                try
                {
                    install(adapter);
                    ++installed;
                }
                catch (Exception error) when (permit)
                {
                    report(adapter, error);
                }
            }
            return installed;
        }
    }
}

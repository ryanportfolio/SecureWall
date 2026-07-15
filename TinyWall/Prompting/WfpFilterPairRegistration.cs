using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    internal enum WfpFilterLifetime
    {
        Persistent,
        BootTime,
    }

    internal static class WfpFilterPairRegistration
    {
        internal static IReadOnlyList<ulong> Register(
            Func<WfpFilterLifetime, ulong> register,
            bool required)
        {
            if (register == null)
                throw new ArgumentNullException(nameof(register));

            var filterIds = new List<ulong>(2);
            if (required)
            {
                filterIds.Add(register(WfpFilterLifetime.Persistent));
                filterIds.Add(register(WfpFilterLifetime.BootTime));
                return filterIds;
            }

            try
            {
                filterIds.Add(register(WfpFilterLifetime.Persistent));
                filterIds.Add(register(WfpFilterLifetime.BootTime));
            }
            catch
            {
                // Preserve TinyWall's best-effort behavior for non-critical filters.
            }

            return filterIds;
        }
    }
}

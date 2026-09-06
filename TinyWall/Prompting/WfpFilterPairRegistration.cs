using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    internal enum WfpFilterLifetime
    {
        Persistent,
        BootTime,
        Dynamic,
    }

    internal static class WfpFilterPairRegistration
    {
        internal static IReadOnlyList<ulong> Register(
            Func<WfpFilterLifetime, ulong> register,
            bool required,
            bool runtimeOnly = false)
        {
            if (register == null)
                throw new ArgumentNullException(nameof(register));

            // A reported successful policy must contain every requested rule, including allows.
            // The caller owns the transaction and rolls back every registration on any failure.
            // 'required' remains in the signature for callers compiled against the old helper.
            if (runtimeOnly)
                return new[] { register(WfpFilterLifetime.Dynamic) };
            return new[] { register(WfpFilterLifetime.Persistent), register(WfpFilterLifetime.BootTime) };
        }
    }
}

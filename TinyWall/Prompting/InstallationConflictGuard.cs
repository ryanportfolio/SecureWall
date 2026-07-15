using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    internal static class InstallationConflictGuard
    {
        internal static bool HasTinyWallService(IEnumerable<string> serviceNames)
        {
            if (serviceNames == null)
                throw new ArgumentNullException(nameof(serviceNames));

            foreach (string serviceName in serviceNames)
            {
                if (string.Equals(serviceName, "TinyWall", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}

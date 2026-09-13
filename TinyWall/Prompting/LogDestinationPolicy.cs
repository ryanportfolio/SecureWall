using System;
using System.IO;

namespace pylorak.TinyWall.Prompting
{
    internal static class LogDestinationPolicy
    {
        internal static bool RequiresMachineData(bool isSystem, bool isAdministrator,
            bool isInteractive, bool isImpersonating, bool machineRole)
            => isSystem || isAdministrator || !isInteractive || isImpersonating || machineRole;

        // Lazy providers ensure privileged logging never resolves a user path.
        internal static string DirectoryPath(bool machine, Func<string> guardedMachineRoot, Func<string> localUserRoot)
            => Path.Combine(machine ? guardedMachineRoot() : Path.Combine(localUserRoot(), "SecureWall"), "logs");
    }
}

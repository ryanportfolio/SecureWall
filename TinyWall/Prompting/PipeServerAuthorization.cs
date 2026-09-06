using System;

namespace pylorak.TinyWall.Prompting
{
    internal static class PipeServerAuthorization
    {
        internal static bool IsExpectedConfiguration(string? command, string? expectedPath,
            string? account, uint serviceType)
        {
            if (serviceType != 0x10 || string.IsNullOrWhiteSpace(expectedPath) ||
                (!string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(account, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)))
                return false;
            string image = "\"" + expectedPath + "\"";
            return string.Equals(command?.Trim(), image, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command?.Trim(), image + " /service", StringComparison.OrdinalIgnoreCase);
        }
    }
}

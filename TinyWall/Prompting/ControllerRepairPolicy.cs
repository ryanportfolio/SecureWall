using System;

namespace pylorak.TinyWall.Prompting
{
    internal static class ControllerRepairPolicy
    {
        internal static bool MayInstall(bool installing, bool isSystem) => installing && isSystem;

        internal static void RequireExistingOrInstaller(bool exists, bool mayInstall)
        {
            if (!exists && !mayInstall)
                throw new InvalidOperationException("The SecureWall service is missing. Use trusted MSI recovery from a local console. This installer requires full removal followed by a fresh installation; it does not support in-place repair.");
        }

        internal static void RequireRegistration(string image, string executable, string account, int serviceType)
        {
            string expected = "\"" + executable + "\"";
            if ((!string.Equals(image.Trim(), expected, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(image.Trim(), expected + " /service", StringComparison.OrdinalIgnoreCase)) ||
                !string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase) || serviceType != 0x10)
                throw new InvalidOperationException("The SecureWall service registration does not match this installation's dedicated LocalSystem service. Use trusted MSI recovery from a local console.");
        }
    }
}

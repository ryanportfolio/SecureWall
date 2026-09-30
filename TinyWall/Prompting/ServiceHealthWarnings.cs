using System;

namespace pylorak.TinyWall.Prompting
{
    // Degraded conditions the running service reports without weakening policy.
    [Flags]
    public enum ServiceHealthWarning
    {
        None = 0,
        InstallationGuard = 1,
        HostsProtection = 2,
    }

    internal static class ServiceHealthText
    {
        // Balloon text is limited to 255 characters.
        internal const string InstallationGuard = "The installation safety check failed after startup. Enforcement continues with the saved policy. See the service log.";
        internal const string HostsProtection = "The hosts file could not be locked. Enforcement is unchanged, but other programs can edit the hosts file.";
    }

    internal static class ServiceHealthPolicy
    {
        internal static ServiceHealthWarning Set(ServiceHealthWarning current, ServiceHealthWarning warning, bool active)
            => active ? current | warning : current & ~warning;
    }

    // Controller/UI thread only. Each warning is shown once when it appears and is
    // re-armed after the service clears it or restarts.
    internal sealed class ServiceHealthNotificationGate
    {
        private ServiceHealthWarning shown;

        internal string? Update(ServiceHealthWarning current)
        {
            ServiceHealthWarning raised = current & ~shown;
            shown = current;
            string? message = null;
            if ((raised & ServiceHealthWarning.InstallationGuard) != 0) message = ServiceHealthText.InstallationGuard;
            if ((raised & ServiceHealthWarning.HostsProtection) != 0)
                message = message == null ? ServiceHealthText.HostsProtection : message + " " + ServiceHealthText.HostsProtection;
            return message;
        }
    }
}

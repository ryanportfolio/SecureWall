using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace pylorak.TinyWall.Prompting
{
    // Popup wording for a prompt whose Allow is disabled, or whose Allow also covers Windows
    // services. Pure so the text, including the admin command, is testable without WinForms.
    internal static class PromptNoticeText
    {
        internal static string Blocked(PromptAllowBlocker blocker, string? serviceName, string? executablePath = null) => blocker switch
        {
            PromptAllowBlocker.ServiceSidUnavailable =>
                $"{serviceName} has no service SID, so a rule for it cannot match. Admin fix: {SidTypeCommand(serviceName)}, then restart it.",
            PromptAllowBlocker.ServiceSidUnverified =>
                $"SecureWall could not read the service SID type of {serviceName}, so it cannot verify that a rule would match. Allow is disabled.",
            PromptAllowBlocker.ServiceRegistrationUnknown =>
                "SecureWall could not rule out that this program runs as a Windows service, so Allow is disabled. " +
                    "To allow it anyway, add a rule for it in Manage, Application Exceptions.",
            _ when IsServiceHost(executablePath) =>
                "SecureWall could not identify one exact service, so Allow is disabled. " +
                    "To allow one of its services, add a rule for that service in Manage, Application Exceptions.",
            _ =>
                "SecureWall could not identify one exact service, so Allow is disabled. " +
                    "To allow this program anyway, add a rule for it in Manage, Application Exceptions. That rule also covers the services that run the same file.",
        };

        internal static string BlockedTooltip(PromptAllowBlocker blocker) => blocker switch
        {
            PromptAllowBlocker.ServiceSidUnavailable => "Unavailable because the service has no service SID to match.",
            PromptAllowBlocker.ServiceSidUnverified => "Unavailable because the service SID type could not be read.",
            PromptAllowBlocker.ServiceRegistrationUnknown => "Unavailable because the service inventory could not confirm this program.",
            _ => "Unavailable because the service identity is ambiguous.",
        };

        // Shown instead of the usual Allow notice when the executable is also the image of
        // Windows services: an executable rule matches those services too.
        internal static string CoveredServices(IReadOnlyList<string> serviceNames)
        {
            if (serviceNames == null || serviceNames.Count == 0)
                throw new ArgumentException("At least one service name is required.", nameof(serviceNames));

            string services = serviceNames.Count == 1
                ? $"the Windows service {serviceNames[0]}"
                : $"the Windows services {JoinNames(serviceNames)}";
            string those = serviceNames.Count == 1 ? "that service" : "those services";
            return $"This file also runs as {services}. Allow permanently permits this program and {those} " +
                "to reach all destinations and ports over TCP/UDP.";
        }

        internal const string CoveredServicesTooltip =
            "Permanent outbound TCP/UDP access to all destinations and ports for this file, also when it runs as the services named here. " +
            "The shown destination does not limit this rule.";

        // Service names may contain spaces ("Steam Client Service"), so the name is always
        // quoted. SCM forbids '/' and '\' in names but not '"'; escape it for sc.exe's
        // command-line parser rather than assume it cannot occur.
        internal static string SidTypeCommand(string? serviceName) =>
            "sc.exe sidtype \"" + (serviceName ?? string.Empty).Replace("\"", "\\\"") + "\" unrestricted";

        // Every name is listed: the rule covers each of them, and the notice label grows to fit.
        private static string JoinNames(IReadOnlyList<string> names) =>
            string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];

        private static bool IsServiceHost(string? executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return true;
            try
            {
                return string.Equals(Path.GetFileName(executablePath), "svchost.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }
}

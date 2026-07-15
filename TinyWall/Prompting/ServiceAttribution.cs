using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace pylorak.TinyWall.Prompting
{
    internal static class ServiceAttribution
    {
        internal static PromptIdentity Resolve(
            DropCandidate candidate,
            BlockedConnectionAuditEvent? auditEvent,
            IEnumerable<string> serviceNames,
            bool executableIsRegisteredService = false)
        {
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));
            if (serviceNames == null)
                throw new ArgumentNullException(nameof(serviceNames));
            if (auditEvent != null && !DropCorrelator.IsMatch(candidate, auditEvent))
                throw new ArgumentException("Audit event does not match the WFP drop candidate.", nameof(auditEvent));

            string? packageSid = candidate.PackageSid ?? auditEvent?.PackageSid;
            if (!string.IsNullOrWhiteSpace(packageSid))
                return PromptIdentity.ForPackage(packageSid!, candidate.ApplicationPath);

            string[] services = serviceNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (services.Length == 1)
                return PromptIdentity.ForService(candidate.ApplicationPath, services[0]);
            if (services.Length > 1)
                return PromptIdentity.ForAmbiguousServices(candidate.ApplicationPath, services);

            if (executableIsRegisteredService || IsServiceHost(candidate.ApplicationPath))
                return PromptIdentity.ForUnattributedServiceHost(candidate.ApplicationPath);

            return PromptIdentity.ForExecutable(candidate.ApplicationPath);
        }

        private static bool IsServiceHost(string applicationPath) =>
            string.Equals(
                Path.GetFileName(applicationPath),
                "svchost.exe",
                StringComparison.OrdinalIgnoreCase);
    }
}

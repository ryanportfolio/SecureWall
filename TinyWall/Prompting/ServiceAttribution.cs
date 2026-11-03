using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace pylorak.TinyWall.Prompting
{
    internal static class ServiceAttribution
    {
        // serviceSidType returns the service's configured SERVICE_SID_INFO.dwServiceSidType,
        // or null when it could not be read. Without it an exact service stays non-allowable:
        // the rule it would write matches only a per-service SID in the process token.
        internal static PromptIdentity Resolve(
            DropCandidate candidate,
            BlockedConnectionAuditEvent? auditEvent,
            IEnumerable<string> serviceNames,
            bool executableIsRegisteredService = false,
            bool snapshotUncertain = false,
            bool registrationUnknown = false,
            Func<string, uint?>? serviceSidType = null,
            IReadOnlyList<string>? registeredServices = null,
            string? windowsDirectory = null,
            Func<bool>? processStillRunning = null)
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

            // Uncertainty dominates even a single stable name in a transitioning PID.
            // A cached negative executable catalog cannot override missing SCM evidence.
            if (snapshotUncertain)
                return PromptIdentity.ForUnattributedServiceHost(candidate.ApplicationPath);

            string[] services = serviceNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (services.Length == 1)
            {
                uint? sidType = serviceSidType?.Invoke(services[0]);
                return ServiceSidPolicy.CanMatchServiceRule(sidType)
                    ? PromptIdentity.ForService(candidate.ApplicationPath, services[0])
                    : PromptIdentity.ForServiceWithoutSid(candidate.ApplicationPath, services[0], sidType.HasValue);
            }
            if (services.Length > 1)
                return PromptIdentity.ForAmbiguousServices(candidate.ApplicationPath, services);

            if (IsServiceHost(candidate.ApplicationPath))
                return PromptIdentity.ForUnattributedServiceHost(candidate.ApplicationPath);

            // The process runs no service, but its file is the image of registered services.
            // An executable rule for it would also cover those services, so it is offered only
            // when the popup can name every one of them, and never for a file in the Windows
            // directory, whose shared hosts (dllhost.exe and the like) run unrelated code.
            // "Runs no service" needs evidence: a drop never matched to an audit event has no
            // PID, and a process that exited before the SCM snapshot (or whose PID was reused)
            // is missing from it, so an empty service list proves nothing in either case.
            // processStillRunning, asked last because it opens the process, confirms the
            // audited process outlived the snapshot.
            if (executableIsRegisteredService)
            {
                return auditEvent != null &&
                    registeredServices != null && registeredServices.Count != 0 &&
                    !IsInWindowsDirectory(candidate.ApplicationPath, windowsDirectory) &&
                    processStillRunning?.Invoke() == true
                    ? PromptIdentity.ForServiceExecutable(candidate.ApplicationPath, registeredServices)
                    : PromptIdentity.ForUnattributedServiceHost(candidate.ApplicationPath);
            }

            // The inventory could not rule out a service registration for this file. Show the
            // executable as itself, not as a service host, and keep Allow disabled.
            if (registrationUnknown)
                return PromptIdentity.ForUnconfirmedServiceRegistration(candidate.ApplicationPath);

            return PromptIdentity.ForExecutable(candidate.ApplicationPath);
        }

        private static bool IsServiceHost(string applicationPath) =>
            string.Equals(
                Path.GetFileName(applicationPath),
                "svchost.exe",
                StringComparison.OrdinalIgnoreCase);

        // Only a plain drive-letter path outside the Windows directory counts as outside it.
        // Anything else (no known Windows directory, an NT or \\?\ form, a UNC path, a "."
        // or ".." segment) is treated as inside, which keeps Allow disabled.
        internal static bool IsInWindowsDirectory(string applicationPath, string? windowsDirectory)
        {
            if (string.IsNullOrWhiteSpace(windowsDirectory) || string.IsNullOrWhiteSpace(applicationPath))
                return true;
            string path = applicationPath.Trim();
            if (path.Length < 3 || !IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
                path.IndexOf(@"\.\", StringComparison.Ordinal) >= 0 ||
                path.IndexOf(@"\..\", StringComparison.Ordinal) >= 0 ||
                path.IndexOf('/') >= 0)
                return true;
            string root = windowsDirectory!.Trim().TrimEnd('\\') + @"\";
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAsciiLetter(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    }

    internal static class ServiceSidPolicy
    {
        internal const uint SidTypeNone = 0;
        internal const uint SidTypeUnrestricted = 1;
        internal const uint SidTypeRestricted = 3;

        // Windows adds the per-service SID to the process token only for the unrestricted and
        // restricted SID types. A ServiceSubject filter matches FWPM_CONDITION_ALE_USER_ID
        // against that SID, so NONE, an unknown value, or an unreadable type cannot match.
        internal static bool CanMatchServiceRule(uint? sidType) =>
            sidType == SidTypeUnrestricted || sidType == SidTypeRestricted;
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace pylorak.TinyWall.Prompting
{
    internal enum PromptIdentityKind
    {
        Executable,
        Package,
        Service,
        AmbiguousService,
    }

    // Why the popup must not offer Allow for an identity. Anything other than None is
    // non-allowable, and the service refuses the token regardless of what the controller shows.
    internal enum PromptAllowBlocker
    {
        None,
        // Shared, pending or unattributed service host: no exact service to name.
        AmbiguousService,
        // Exact service whose SID type is NONE (or not unrestricted/restricted), so its token
        // carries no per-service SID. A ServiceSubject rule matches FWPM_CONDITION_ALE_USER_ID
        // against that SID, so it would never match the service's traffic.
        ServiceSidUnavailable,
        // Exact service whose SID type could not be read, so a matching rule is unverified.
        ServiceSidUnverified,
        // The service inventory cannot rule out that this executable is a registered
        // service. This is uncertainty, not established service attribution.
        ServiceRegistrationUnknown,
    }

    internal sealed class PromptIdentity
    {
        private PromptIdentity(
            PromptIdentityKind kind,
            string key,
            string? executablePath,
            string? packageSid,
            string? serviceName,
            IReadOnlyList<string>? ambiguousServiceNames,
            PromptAllowBlocker allowBlocker = PromptAllowBlocker.None)
        {
            Kind = kind;
            AllowBlocker = kind == PromptIdentityKind.AmbiguousService
                ? PromptAllowBlocker.AmbiguousService
                : allowBlocker;
            Key = key;
            ExecutablePath = executablePath;
            PackageSid = packageSid;
            ServiceName = serviceName;
            AmbiguousServiceNames = ambiguousServiceNames ?? Array.Empty<string>();
        }

        internal PromptIdentityKind Kind { get; }
        internal string Key { get; }
        internal string? ExecutablePath { get; }
        internal string? PackageSid { get; }
        internal string? ServiceName { get; }
        internal IReadOnlyList<string> AmbiguousServiceNames { get; }
        internal PromptAllowBlocker AllowBlocker { get; }
        internal bool CanAllow => AllowBlocker == PromptAllowBlocker.None;

        internal static PromptIdentity ForExecutable(string executablePath)
        {
            string path = Require(executablePath, nameof(executablePath));
            return new PromptIdentity(
                PromptIdentityKind.Executable,
                $"exe:{Canonical(path)}",
                path,
                null,
                null,
                null);
        }

        // Shown as the executable, but not allowable: the service inventory could not rule
        // out that it runs as a registered service. A separate key keeps this prompt from
        // coalescing with, or sharing an Ignore cooldown with, an allowable one.
        internal static PromptIdentity ForUnconfirmedServiceRegistration(string executablePath)
        {
            string path = Require(executablePath, nameof(executablePath));
            return new PromptIdentity(
                PromptIdentityKind.Executable,
                $"exe:{Canonical(path)}|registration-unknown",
                path,
                null,
                null,
                null,
                PromptAllowBlocker.ServiceRegistrationUnknown);
        }

        internal static PromptIdentity ForPackage(string packageSid, string? executablePath)
        {
            if (!global::pylorak.TinyWall.Prompting.PackageSid.TryNormalize(
                packageSid,
                out string? sid))
                throw new ArgumentException("Value is not an AppContainer package SID.", nameof(packageSid));
            string? path = Optional(executablePath);
            return new PromptIdentity(
                PromptIdentityKind.Package,
                $"package:{Canonical(sid!)}",
                path,
                sid!,
                null,
                null);
        }

        internal static PromptIdentity ForService(string executablePath, string serviceName)
        {
            string path = Require(executablePath, nameof(executablePath));
            string service = Require(serviceName, nameof(serviceName));
            return new PromptIdentity(
                PromptIdentityKind.Service,
                $"service:{Canonical(service)}|{Canonical(path)}",
                path,
                null,
                service,
                null);
        }

        // Exact service with no per-service SID (sidTypeKnown) or an unreadable SID type, so a
        // ServiceSubject rule could not be shown to match it. Kept distinct from ForService so
        // a later SID-type change starts a fresh, allowable prompt instead of coalescing into this one.
        internal static PromptIdentity ForServiceWithoutSid(
            string executablePath,
            string serviceName,
            bool sidTypeKnown = true)
        {
            string path = Require(executablePath, nameof(executablePath));
            string service = Require(serviceName, nameof(serviceName));
            return new PromptIdentity(
                PromptIdentityKind.Service,
                $"service:{Canonical(service)}|{Canonical(path)}|" +
                    (sidTypeKnown ? "no-service-sid" : "service-sid-unverified"),
                path,
                null,
                service,
                null,
                sidTypeKnown ? PromptAllowBlocker.ServiceSidUnavailable : PromptAllowBlocker.ServiceSidUnverified);
        }

        internal static PromptIdentity ForAmbiguousServices(
            string executablePath,
            IEnumerable<string> serviceNames)
        {
            string path = Require(executablePath, nameof(executablePath));
            if (serviceNames == null)
                throw new ArgumentNullException(nameof(serviceNames));

            string[] services = serviceNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (services.Length == 0)
                throw new ArgumentException("At least one service name is required.", nameof(serviceNames));

            string joinedNames = string.Join("|", services.Select(Canonical));
            ReadOnlyCollection<string> readOnlyNames = Array.AsReadOnly(services);
            return new PromptIdentity(
                PromptIdentityKind.AmbiguousService,
                $"ambiguous-service:{Canonical(path)}|{joinedNames}",
                path,
                null,
                null,
                readOnlyNames);
        }

        internal static PromptIdentity ForUnattributedServiceHost(string executablePath)
        {
            string path = Require(executablePath, nameof(executablePath));
            return new PromptIdentity(
                PromptIdentityKind.AmbiguousService,
                $"ambiguous-service:{Canonical(path)}|UNKNOWN",
                path,
                null,
                null,
                Array.Empty<string>());
        }

        internal static PromptIdentity FromAttribution(
            string executablePath,
            string? packageSid,
            IEnumerable<string>? serviceNames)
        {
            if (!string.IsNullOrWhiteSpace(packageSid))
                return ForPackage(packageSid!, executablePath);

            string[] services = serviceNames?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();

            if (services.Length == 1)
                return ForService(executablePath, services[0]);
            if (services.Length > 1)
                return ForAmbiguousServices(executablePath, services);

            return ForExecutable(executablePath);
        }

        private static string Require(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Value cannot be empty.", parameterName);
            return value.Trim();
        }

        private static string? Optional(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        private static string Canonical(string value) => value.ToUpperInvariant();
    }
}

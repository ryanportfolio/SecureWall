using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace pylorak.TinyWall.Prompting
{
    internal enum ServiceRegistrationStatus
    {
        NotRegistered,
        Registered,
        // The inventory cannot rule out that the executable is a registered service.
        Unknown,
    }

    // ImagePath is the raw registry value: a string, string[] (REG_MULTI_SZ), another
    // value type, or null when absent.
    internal sealed record ServiceImageRegistration(string ServiceName, object? ImagePath);

    // One refresh of the registered Win32 service image paths. Pure: the catalog reads the
    // registry and caches the result; this class decides what each registration means.
    internal sealed class ServiceExecutableInventory
    {
        // Resolved executable path -> the services whose image path names it, sorted.
        private readonly Dictionary<string, string[]> _executables;
        private readonly string[] _unresolvedCommands;

        private ServiceExecutableInventory(
            Dictionary<string, string[]> executables,
            string[] unresolvedCommands,
            string[] unresolvedServices,
            string[] unusableServices)
        {
            _executables = executables;
            _unresolvedCommands = unresolvedCommands;
            UnresolvedServices = Array.AsReadOnly(unresolvedServices);
            UnusableServices = Array.AsReadOnly(unusableServices);
        }

        // Registrations whose image path is text but could not be reduced to one executable.
        // Lookups stay conservative only for executables named in that text.
        internal IReadOnlyList<string> UnresolvedServices { get; }

        // Registrations with no image path, or a value that is not text. The SCM cannot
        // start a process from them, so they cannot be the source of a blocked connection.
        internal IReadOnlyList<string> UnusableServices { get; }

        internal static ServiceExecutableInventory Build(
            IEnumerable<ServiceImageRegistration> registrations,
            string windowsDirectory)
        {
            if (registrations == null)
                throw new ArgumentNullException(nameof(registrations));

            var executables = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var unresolvedCommands = new List<string>();
            var unresolvedServices = new List<string>();
            var unusableServices = new List<string>();
            foreach (ServiceImageRegistration registration in registrations)
            {
                string? command = registration.ImagePath switch
                {
                    string text => text,
                    string[] lines => string.Join(" ", lines),
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(command))
                {
                    unusableServices.Add(registration.ServiceName);
                    continue;
                }

                string? executable = ServiceImagePath.TryExtractExecutable(command, windowsDirectory);
                if (executable != null)
                {
                    if (!executables.TryGetValue(executable, out List<string>? names))
                        executables.Add(executable, names = new List<string>());
                    names.Add(registration.ServiceName);
                    continue;
                }

                unresolvedServices.Add(registration.ServiceName);
                unresolvedCommands.Add(ExpandForMatching(command!));
            }

            return new ServiceExecutableInventory(
                executables.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                unresolvedCommands.ToArray(),
                unresolvedServices.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                unusableServices.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        internal ServiceRegistrationStatus Lookup(string normalizedPath)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath))
                return ServiceRegistrationStatus.Unknown;
            if (_executables.ContainsKey(normalizedPath))
                return ServiceRegistrationStatus.Registered;
            return NamedByUnresolvedCommand(normalizedPath)
                ? ServiceRegistrationStatus.Unknown
                : ServiceRegistrationStatus.NotRegistered;
        }

        // The services whose image path is this executable, or none when the list could be
        // incomplete: an unresolved registration that names the file might be one more.
        internal IReadOnlyList<string> RegisteredServices(string normalizedPath)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath) ||
                !_executables.TryGetValue(normalizedPath, out string[]? names) ||
                NamedByUnresolvedCommand(normalizedPath))
                return Array.Empty<string>();
            return Array.AsReadOnly(names);
        }

        private bool NamedByUnresolvedCommand(string normalizedPath)
        {
            if (_unresolvedCommands.Length == 0)
                return false;

            string fileName;
            try
            {
                fileName = Path.GetFileName(normalizedPath);
            }
            catch (ArgumentException)
            {
                return true;
            }
            if (fileName.Length == 0)
                return true;

            // An unresolved command could start this executable only if it names the file:
            // either in full, or without ".exe" at a token end, which CreateProcess extends.
            string? stem = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - 4)
                : null;
            foreach (string command in _unresolvedCommands)
            {
                if (command.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (stem != null && stem.Length > 0 && NamesStemAtTokenEnd(command, stem)))
                    return true;
            }
            return false;
        }

        private static bool NamesStemAtTokenEnd(string command, string stem)
        {
            for (int index = command.IndexOf(stem, StringComparison.OrdinalIgnoreCase);
                index >= 0;
                index = command.IndexOf(stem, index + 1, StringComparison.OrdinalIgnoreCase))
            {
                int after = index + stem.Length;
                if (after == command.Length || char.IsWhiteSpace(command[after]) || command[after] == '"')
                    return true;
            }
            return false;
        }

        private static string ExpandForMatching(string command)
        {
            string expanded = Environment.ExpandEnvironmentVariables(command).Trim();
            return expanded.StartsWith(@"\??\", StringComparison.Ordinal) ? expanded.Substring(4) : expanded;
        }
    }
}

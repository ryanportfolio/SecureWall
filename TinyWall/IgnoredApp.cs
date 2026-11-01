using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    public enum IgnoredAppKind
    {
        Executable,
        Package,
        Service,
    }

    // An app the owner chose Ignore for in the popup. It only stops prompts: its default-deny
    // drops stay blocked and are never turned into WFP filters. An entry matches the same
    // identity the prompt was built on, never a file name alone, so it cannot silence another
    // app. Shared or unattributed service hosts have no exact identity and are never listed.
    [DataContract(Namespace = "TinyWall")]
    public sealed class IgnoredApp
    {
        [DataMember]
        public IgnoredAppKind Kind { get; set; }

        // Full executable path. Matched for executables and services; for packages it is
        // display only.
        [DataMember(EmitDefaultValue = false)]
        public string ExecutablePath { get; set; } = string.Empty;

        [DataMember(EmitDefaultValue = false)]
        public string PackageSid { get; set; } = string.Empty;

        [DataMember(EmitDefaultValue = false)]
        public string ServiceName { get; set; } = string.Empty;

        public IgnoredApp() { }

        internal static IgnoredApp? FromIdentity(PromptIdentity identity)
        {
            switch (identity.Kind)
            {
                case PromptIdentityKind.Executable when !string.IsNullOrWhiteSpace(identity.ExecutablePath):
                    return new IgnoredApp { Kind = IgnoredAppKind.Executable, ExecutablePath = identity.ExecutablePath! };
                case PromptIdentityKind.Package when !string.IsNullOrWhiteSpace(identity.PackageSid):
                    return new IgnoredApp { Kind = IgnoredAppKind.Package, PackageSid = identity.PackageSid!, ExecutablePath = identity.ExecutablePath ?? string.Empty };
                case PromptIdentityKind.Service when !string.IsNullOrWhiteSpace(identity.ExecutablePath) && !string.IsNullOrWhiteSpace(identity.ServiceName):
                    return new IgnoredApp { Kind = IgnoredAppKind.Service, ExecutablePath = identity.ExecutablePath!, ServiceName = identity.ServiceName! };
                default:
                    return null;
            }
        }

        internal bool IsValid => Kind switch
        {
            IgnoredAppKind.Executable => !string.IsNullOrWhiteSpace(ExecutablePath),
            IgnoredAppKind.Package => !string.IsNullOrWhiteSpace(PackageSid),
            IgnoredAppKind.Service => !string.IsNullOrWhiteSpace(ExecutablePath) && !string.IsNullOrWhiteSpace(ServiceName),
            _ => false,
        };

        // An executable entry also covers the same path while the service inventory cannot rule
        // out that it is a service; a service entry covers it with or without a service SID.
        internal bool Matches(PromptIdentity identity)
        {
            if (!IsValid)
                return false;
            return Kind switch
            {
                IgnoredAppKind.Executable => identity.Kind == PromptIdentityKind.Executable &&
                    SameText(ExecutablePath, identity.ExecutablePath),
                IgnoredAppKind.Package => identity.Kind == PromptIdentityKind.Package &&
                    SameText(PackageSid, identity.PackageSid),
                IgnoredAppKind.Service => identity.Kind == PromptIdentityKind.Service &&
                    SameText(ServiceName, identity.ServiceName) && SameText(ExecutablePath, identity.ExecutablePath),
                _ => false,
            };
        }

        // Compares only the fields Matches uses: a package entry's path is display only.
        internal bool SameEntry(IgnoredApp other) =>
            Kind == other.Kind &&
            (Kind == IgnoredAppKind.Package || SameText(ExecutablePath, other.ExecutablePath)) &&
            SameText(PackageSid, other.PackageSid) &&
            SameText(ServiceName, other.ServiceName);

        internal string DisplayName => Kind switch
        {
            IgnoredAppKind.Package => string.IsNullOrWhiteSpace(ExecutablePath) ? "App package" : FileName(ExecutablePath) + " (app package)",
            IgnoredAppKind.Service => ServiceName + " (Windows service)",
            _ => FileName(ExecutablePath),
        };

        // .NET Framework's Path.GetFileName throws on characters a hand-edited import can hold.
        private static string FileName(string path)
        {
            int slash = path.LastIndexOfAny(new[] { '\\', '/' });
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        internal string Detail => Kind == IgnoredAppKind.Package ? PackageSid : ExecutablePath;

        // Adds the entry unless the same one is already there. Returns true if it was added.
        internal static bool AddTo(List<IgnoredApp> apps, IgnoredApp app)
        {
            if (!app.IsValid)
                return false;
            foreach (IgnoredApp existing in apps)
            {
                if (existing.SameEntry(app))
                    return false;
            }
            apps.Add(app);
            return true;
        }

        internal static bool AnyMatch(List<IgnoredApp> apps, PromptIdentity identity) =>
            apps.Exists(app => app != null && app.Matches(identity));

        private static bool SameText(string? a, string? b) =>
            string.Equals(a?.Trim() ?? string.Empty, b?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}

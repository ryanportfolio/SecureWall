#if DEBUG
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    // Sample data for the Debug-only /uipreview switch, matching the demo apps of the product
    // walkthrough: Inkwell (allowed from a prompt), telemetry_agent (ignored), NoteSync
    // (Always allow) and PhotoDrop (blocked, then unblocked in Network Activity). Addresses
    // are documentation ranges; nothing here is read from this machine.
    internal static class UiPreviewSamples
    {
        internal const string InkwellPath = @"C:\Program Files\Inkwell\Inkwell.exe";
        internal const string TelemetryPath = @"C:\Users\Alex\AppData\Roaming\Updater\telemetry_agent.exe";
        internal const string NoteSyncPath = @"C:\Users\Alex\AppData\Local\NoteSync\app-3.2.0\NoteSync.exe";
        internal const string NoteSyncSigner = "NoteSync Labs Inc.";
        internal const string PhotoDropPath = @"C:\Program Files\PhotoDrop\PhotoDrop.exe";
        internal const string SvchostPath = @"C:\Windows\System32\svchost.exe";

        internal sealed class ActivityRow
        {
            internal uint Pid;
            internal string Path = string.Empty;
            internal string[] Services = Array.Empty<string>();
            internal string Protocol = "TCP";
            internal IPEndPoint Local = new(IPAddress.Any, 0);
            internal IPEndPoint Remote = new(IPAddress.Any, 0);
            internal string RemoteText = string.Empty;
            internal string Status = string.Empty;
            internal RuleDirection Direction = RuleDirection.Invalid;
            internal DateTime Timestamp;
        }

        private static readonly IPAddress LocalAddress = IPAddress.Parse("192.168.1.20");
        private static readonly DateTime Now = new(2026, 11, 1, 10, 24, 30);

        private static ActivityRow Row(uint pid, string path, string protocol, int localPort, string remote, int remotePort,
            string? host, NetworkActivityStatus status, RuleDirection direction, int secondsAgo, string? statusText = null,
            params string[] services)
        {
            return new ActivityRow
            {
                Pid = pid,
                Path = path,
                Services = services,
                Protocol = protocol,
                Local = new IPEndPoint(LocalAddress, localPort),
                Remote = new IPEndPoint(IPAddress.Parse(remote), remotePort),
                RemoteText = host == null ? remote : $"{remote} ({host})",
                Status = statusText ?? NetworkActivityStatusClassifier.ToDisplayText(status),
                Direction = direction,
                Timestamp = Now.AddSeconds(-secondsAgo),
            };
        }

        // Network Activity rows: live connections have no direction, logged decisions do.
        internal static IReadOnlyList<ActivityRow> ActivityRows()
        {
            return new[]
            {
                Row(7408, InkwellPath, "TCP", 51544, "203.0.113.45", 443, "sync.inkwell.example", NetworkActivityStatus.Allowed, RuleDirection.Invalid, 0, "Established"),
                Row(7408, InkwellPath, "TCP", 51598, "203.0.113.45", 443, "sync.inkwell.example", NetworkActivityStatus.Allowed, RuleDirection.Out, 4),
                Row(1432, SvchostPath, "UDP", 55012, "192.0.2.53", 53, null, NetworkActivityStatus.Allowed, RuleDirection.Out, 6, null, "Dnscache"),
                Row(8816, PhotoDropPath, "TCP", 51610, "198.51.100.23", 443, "upload.photodrop.example", NetworkActivityStatus.Blocked, RuleDirection.Out, 2,
                    NetworkActivityStatusClassifier.ToDisplayText(NetworkActivityStatus.Blocked, FilterGroup.DefaultAction)),
                Row(0, TelemetryPath, "TCP", 51633, "198.51.100.7", 443, null, NetworkActivityStatus.Blocked, RuleDirection.Out, 9,
                    NetworkActivityStatusClassifier.ToDisplayText(NetworkActivityStatus.Blocked, FilterGroup.DefaultAction, promptIgnored: true)),
                Row(5236, NoteSyncPath, "TCP", 51587, "192.0.2.80", 443, "api.notesync.example", NetworkActivityStatus.Blocked, RuleDirection.Out, 21,
                    NetworkActivityStatusClassifier.ToDisplayText(NetworkActivityStatus.Blocked, FilterGroup.DefaultAction, allowedByAlwaysAllow: true)),
                Row(4, "System", "TCP", 445, "0.0.0.0", 0, null, NetworkActivityStatus.Listening, RuleDirection.Invalid, 0),
            };
        }

        // A new install's profile (CreateDefaultServerConfig) after the walkthrough's prompts
        // were answered.
        internal static ServerConfiguration Configuration(DatabaseClasses.AppDatabase db)
        {
            var config = new ServerConfiguration { ActiveProfileName = Resources.Messages.Default };
            ServerProfileConfiguration profile = config.ActiveProfile;
            foreach (DatabaseClasses.Application app in db.KnownApplications)
            {
                if (app.HasFlag("TWUI:Special") && app.HasFlag("TWUI:Recommended"))
                    profile.SpecialExceptions.Add(app.Name);
            }
            profile.AppExceptions.Add(Outbound(new ExecutableSubject(InkwellPath)));
            profile.AppExceptions.Add(Outbound(new ExecutableSubject(NoteSyncPath)));
            profile.AppExceptions.Add(Outbound(new ServiceSubject(SvchostPath, "Dnscache")));
            profile.IgnoredApps.Add(new IgnoredApp { Kind = IgnoredAppKind.Executable, ExecutablePath = TelemetryPath });
            profile.AlwaysAllowRules.Add(new AlwaysAllowRule("NoteSync.exe", $"CN={NoteSyncSigner}, O={NoteSyncSigner}", NoteSyncSigner));
            return config;
        }

        private static FirewallExceptionV3 Outbound(ExceptionSubject subject)
        {
            // The outbound-only policy a prompt's Allow saves (PromptAllowPolicy).
            return new FirewallExceptionV3(subject, new TcpUdpPolicy
            {
                AllowedRemoteTcpConnectPorts = "*",
                AllowedRemoteUdpConnectPorts = "*",
            })
            {
                CreationDate = Now,
            };
        }

        internal static readonly Version UpdateInstalled = new(0, 5, 1);

        // An update offer for 0.5.2 as the update window shows it.
        internal static (GitHubRelease Release, ReleaseManifest Manifest) Update()
        {
            const string tag = "v0.5.2";
            var release = new GitHubRelease
            {
                Tag = tag,
                Version = new Version(0, 5, 2),
                Prerelease = true,
                PublishedAt = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero),
                // The start of the real v0.5.2 notes (docs/releases/v0.5.2.md).
                Notes = "This is an unsigned alpha prerelease. SecureWall can now check GitHub for a newer release and install it, " +
                    "and the Network Activity list no longer moves while it refreshes.\n\n## Changes\n\n### Check for updates\n\n" +
                    "- **Where.** **Check for updates** is in the tray menu and on the Maintenance tab in Manage. It runs only when you " +
                    "choose it; SecureWall never checks on its own. If SecureWall is locked, it asks for the password first.\n",
            };
            string manifest =
                "# SecureWall-Release-Manifest: 1\n" +
                "# Repository: ryanportfolio/SecureWall\n" +
                $"# Tag: {tag}\n" +
                "# Version: 0.5.2\n" +
                "# Prerelease: true\n" +
                "# Commit: 2ef014d8e5a1c0b7d6f4e3a2b1c0d9e8f7a6b5c4\n" +
                new string('0', 64) + "  SecureWall_x64.msi\n";
            if (!ReleaseManifest.TryParse(Encoding.ASCII.GetBytes(manifest), out ReleaseManifest? parsed, out string error) || parsed == null)
                throw new InvalidOperationException("Sample manifest rejected: " + error);
            return (release, parsed);
        }
    }
}
#endif

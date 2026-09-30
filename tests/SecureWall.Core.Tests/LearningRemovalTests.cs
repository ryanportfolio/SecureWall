using System.Text.Json;
using pylorak.TinyWall;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

// Learning mode was removed. FirewallMode.Learning (4) stays reserved so older
// messages, configurations and TinyWall .tws exports still parse.
internal static class LearningRemovalTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("removed learning mode keeps its reserved wire value", ReservedValueIsStable),
        ("service rejects a mode switch to learning", ServiceRejectsLearningSwitch),
        ("configuration naming learning loads as normal", StoredLearningLoadsAsNormal),
        ("imported configuration naming learning applies as normal", ImportedLearningAppliesAsNormal),
        ("service has no learning permit, consumer or success-audit lease", LearningCodeIsGone),
        ("event 5157 still feeds prompt attribution", Event5157StillFeedsPromptAttribution),
    };

    private static void ReservedValueIsStable()
    {
        AssertEx.Equal(4, (int)FirewallMode.Learning);
        AssertEx.Equal(0, (int)FirewallMode.Normal);
        AssertEx.Equal(3, (int)FirewallMode.Disabled);
        AssertEx.Equal(100, (int)FirewallMode.Unknown);
    }

    private static void ServiceRejectsLearningSwitch()
    {
        AssertEx.False(FirewallModePolicy.IsRuntimeMode(FirewallMode.Learning));
        AssertEx.False(FirewallModePolicy.IsStartupMode(FirewallMode.Learning));
        AssertEx.False(FirewallModePolicy.IsRuntimeMode(FirewallMode.Unknown));
        AssertEx.False(FirewallModePolicy.IsRuntimeMode((FirewallMode)5));
        foreach (FirewallMode mode in new[] { FirewallMode.Normal, FirewallMode.BlockAll, FirewallMode.AllowOutgoing, FirewallMode.Disabled })
            AssertEx.True(FirewallModePolicy.IsRuntimeMode(mode), mode + " must stay selectable.");
        AssertEx.False(FirewallModePolicy.IsStartupMode(FirewallMode.Disabled), "Disabled stays runtime-only.");

        // MODE_SWITCH refuses before cloning or applying anything, so the current mode stays.
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int start = service.IndexOf("case MessageType.MODE_SWITCH:", StringComparison.Ordinal);
        int guard = service.IndexOf("if (!FirewallModePolicy.IsRuntimeMode(newMode))", start, StringComparison.Ordinal);
        int error = service.IndexOf("return TwMessageError.Instance;", guard, StringComparison.Ordinal);
        int clone = service.IndexOf("var candidate = Utils.DeepClone(ActiveConfig.Service);", start, StringComparison.Ordinal);
        int apply = service.IndexOf("ApplyConfiguration(candidate, newMode);", start, StringComparison.Ordinal);
        AssertEx.True(start >= 0 && start < guard && guard < error && error < clone && clone < apply,
            "MODE_SWITCH must return an error for an unsupported mode before any policy work.");
        // ApplyConfiguration also refuses it, covering rebuilds and settings commits.
        AssertEx.True(service.Contains("if (!FirewallModePolicy.IsRuntimeMode(mode) || !FirewallModePolicy.IsStartupMode(candidate.StartupMode))\n                throw new ArgumentException(\"Unsupported runtime or startup firewall mode.\");"));
        AssertEx.True(service.Contains("FirewallMode mode = restoreMode is FirewallMode restored && FirewallModePolicy.IsRuntimeMode(restored)"));
    }

    private static void StoredLearningLoadsAsNormal()
    {
        // TinyWall and older SecureWall store the enum as a number.
        var stored = JsonSerializer.Deserialize<ServerConfiguration>("{\"StartupMode\":4}")!;
        AssertEx.Equal(FirewallMode.Learning, stored.StartupMode);
        AssertEx.Equal(FirewallMode.Normal, FirewallModePolicy.NormalizeStartupMode(stored.StartupMode));
        AssertEx.Equal(FirewallMode.Normal, FirewallModePolicy.NormalizeStartupMode((FirewallMode)77));
        AssertEx.Equal(FirewallMode.Normal, FirewallModePolicy.NormalizeStartupMode(FirewallMode.Disabled));
        foreach (FirewallMode mode in new[] { FirewallMode.Normal, FirewallMode.BlockAll, FirewallMode.AllowOutgoing })
            AssertEx.Equal(mode, FirewallModePolicy.NormalizeStartupMode(mode));

        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int load = service.IndexOf("candidate = LoadServerConfig()", StringComparison.Ordinal);
        int normalize = service.IndexOf("candidate.StartupMode = FirewallModePolicy.NormalizeStartupMode(candidate.StartupMode);", load, StringComparison.Ordinal);
        int publish = service.IndexOf("ActiveConfig.Service = candidate;", load, StringComparison.Ordinal);
        AssertEx.True(load >= 0 && load < normalize && normalize < publish, "Stored configuration must be normalized before use.");
    }

    private static void ImportedLearningAppliesAsNormal()
    {
        AssertEx.Equal(FirewallMode.Normal, FirewallModePolicy.MigrateStartupMode(FirewallMode.Learning));
        foreach (FirewallMode mode in new[] { FirewallMode.Normal, FirewallMode.BlockAll, FirewallMode.AllowOutgoing })
            AssertEx.Equal(mode, FirewallModePolicy.MigrateStartupMode(mode));
        // Other unsupported values are left for ApplyConfiguration to reject.
        AssertEx.Equal(FirewallMode.Disabled, FirewallModePolicy.MigrateStartupMode(FirewallMode.Disabled));
        AssertEx.Equal((FirewallMode)77, FirewallModePolicy.MigrateStartupMode((FirewallMode)77));

        // A .tws import reaches the service as PUT_SETTINGS.
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int start = service.IndexOf("case MessageType.PUT_SETTINGS:", StringComparison.Ordinal);
        int migrate = service.IndexOf("candidate.StartupMode = FirewallModePolicy.MigrateStartupMode(candidate.StartupMode);", start, StringComparison.Ordinal);
        int apply = service.IndexOf("ApplyConfiguration(candidate, VisibleState.Mode);", start, StringComparison.Ordinal);
        AssertEx.True(start >= 0 && start < migrate && migrate < apply, "Submitted settings must migrate Learning before commit.");
    }

    private static void LearningCodeIsGone()
    {
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        string watcher = PromptTransactionIntegrationTests.Source("TinyWall/FirewallLogWatcher.cs");
        AssertEx.False(service.Contains("case FirewallMode.Learning"), "Learning must not add an allow-everything rule.");
        AssertEx.False(service.Contains("NewLogEntry") || watcher.Contains("NewLogEntry"), "No learning event consumer may remain.");
        AssertEx.False(watcher.Contains("AuditPolicyFlags.Success"), "Only failure auditing is leased.");
        AssertEx.True(watcher.Contains("_failureAuditLease = AcquireAuditLease(AuditPolicyFlags.Failure);"),
            "The failure-auditing lease that prompts need must remain.");
        AssertEx.False(File.Exists(PromptTransactionIntegrationTests.SourcePath("TinyWall/Prompting/LearningPolicy.cs")));
    }

    // Security event 5157 still reaches the prompt pipeline: the watcher subscribes to it,
    // parses it by field name and raises BlockedConnection; the service matches it to a
    // pending default-block drop.
    private static void Event5157StillFeedsPromptAttribution()
    {
        string watcher = PromptTransactionIntegrationTests.Source("TinyWall/FirewallLogWatcher.cs");
        AssertEx.True(watcher.Contains("\"*[System[(EventID=5157)]]\""));
        AssertEx.True(watcher.Contains("SecurityEvent5157Parser.TryParse(fields, timestamp.ToUniversalTime(), out BlockedConnectionAuditEvent parsed)"));
        AssertEx.True(watcher.Contains("BlockedConnection?.Invoke(this, normalized);"));
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        AssertEx.True(service.Contains("LogWatcher.BlockedConnection += LogWatcherBlockedConnection;"));
        AssertEx.True(service.Contains("if (DropCandidates.TryMatch(auditEvent, out DropCandidate? candidate)"));

        var timestamp = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProcessID"] = "4242",
            ["Application"] = @"C:\apps\sample.exe",
            ["Direction"] = "%%14593",
            ["SourceAddress"] = "192.0.2.10",
            ["SourcePort"] = "52144",
            ["DestAddress"] = "203.0.113.20",
            ["DestPort"] = "443",
            ["Protocol"] = "6",
            ["FilterRTID"] = "5000000000",
        };
        AssertEx.True(SecurityEvent5157Parser.TryParse(fields, timestamp, out BlockedConnectionAuditEvent audit));

        var buffer = new DropCandidateBuffer(new FixedClock(timestamp));
        var candidate = new DropCandidate(timestamp, 5_000_000_000, @"C:\apps\sample.exe", null,
            "192.0.2.10", 52144, "203.0.113.20", 443, 6);
        AssertEx.True(buffer.TryAdd(candidate));
        AssertEx.True(buffer.TryMatch(audit, out DropCandidate? matched));
        AssertEx.Equal(candidate, matched);
        AssertEx.Equal<uint>(4242, audit.ProcessId);
    }

    private sealed class FixedClock : IClock
    {
        internal FixedClock(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; }
    }
}

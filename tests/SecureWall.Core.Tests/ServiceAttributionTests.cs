using System.Text.Json;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class ServiceAttributionTests
{
    private const string Windows = @"C:\Windows";

    internal static IEnumerable<(string Name, Action Test)> Cases
    {
        get
        {
            yield return ("service image paths find the real executable boundary", ImagePathBoundaries);
            yield return ("service image paths stay unresolved when the start point is uncertain", ImagePathUncertainty);
            yield return ("service rules require a service SID the token can carry", SidTypePolicy);
            yield return ("exact service without a service SID is shown but not allowable", ServiceWithoutSidIsNotAllowable);
            yield return ("exact service with a service SID stays allowable", ServiceWithSidIsAllowable);
            yield return ("service inventory scopes unresolved registrations to named executables", InventoryScopesUnresolved);
            yield return ("service inventory skips registrations SCM cannot start", InventorySkipsUnusable);
            yield return ("registration uncertainty is not presented as service attribution", RegistrationUncertaintyIsNotServiceAttribution);
            yield return ("allow blocker round trips on the prompt wire DTO", AllowBlockerRoundTrips);
            yield return ("SID notice quotes the service name in the sc.exe command", SidNoticeQuotesServiceName);
        }
    }

    private static void ImagePathBoundaries()
    {
        // ".exe" inside a directory name is not a boundary (Codex finding).
        AssertEx.Equal(@"C:\svc.exe-dir\daemon.exe",
            ServiceImagePath.TryExtractExecutable(@"C:\svc.exe-dir\daemon.exe", Windows));
        AssertEx.Equal(@"C:\svc.exe-dir\daemon.exe",
            ServiceImagePath.TryExtractExecutable(@"C:\svc.exe-dir\daemon.exe -k run", Windows));
        AssertEx.Equal(@"C:\dir.exe\sub.exe.d\daemon.exe",
            ServiceImagePath.TryExtractExecutable(@"C:\dir.exe\sub.exe.d\daemon.exe /service", Windows));
        // ".exe" inside the file name is not a boundary either.
        AssertEx.Equal(@"C:\tools\my.exeutil.exe",
            ServiceImagePath.TryExtractExecutable(@"C:\tools\my.exeutil.exe --run", Windows));
        AssertEx.Equal(@"C:\tools\agent.exe.exe",
            ServiceImagePath.TryExtractExecutable(@"C:\tools\agent.exe.exe", Windows));
        // Quoted names are exact, even with spaces and ".exe" in a directory.
        AssertEx.Equal(@"C:\svc.exe dir\daemon.exe",
            ServiceImagePath.TryExtractExecutable("\"C:\\svc.exe dir\\daemon.exe\" -k run", Windows));
        // CreateProcess appends ".exe" to a name without an extension.
        AssertEx.Equal(@"C:\tools\agent.exe",
            ServiceImagePath.TryExtractExecutable(@"C:\tools\agent", Windows));
        AssertEx.Equal(@"C:\tools v2\agent.exe",
            ServiceImagePath.TryExtractExecutable("\"C:\\tools v2\\agent\" -x", Windows));
        AssertEx.Equal(@"C:\x.d\agent.exe",
            ServiceImagePath.TryExtractExecutable(@"C:\x.d\agent", Windows));
        AssertEx.Equal(@"C:\x\svc.exe",
            ServiceImagePath.TryExtractExecutable(@"\??\C:\x\svc.exe", Windows));
        AssertEx.Equal(@"C:\Windows\System32\svchost.exe",
            ServiceImagePath.TryExtractExecutable(@"System32\svchost.exe -k LocalService", Windows));
    }

    private static void ImagePathUncertainty()
    {
        foreach (string command in new[]
        {
            // The old parser truncated this at the first ".exe" and returned C:\svc.exe.
            @"C:\svc.exe dir\daemon.exe",
            @"C:\svc.exe dir\daemon.exe -k run",
            // CreateProcess would try C:\Program.exe first.
            @"C:\Program Files\Vendor\svc.exe",
            @"C:\Program Files\Vendor\svc -x",
            // A second ".exe" in the arguments could be a longer executable prefix.
            @"C:\tools\agent.exe --child C:\tools\other.exe",
            @"C:\tools\agent.",
            @"C:\Windows\System32\driver.sys",
            "\"C:\\unterminated\\svc.exe",
            "\"\" -x",
            @"relative\svc.exe",
            "   ",
        })
        {
            AssertEx.Equal<string?>(null, ServiceImagePath.TryExtractExecutable(command, Windows),
                "Uncertain image path resolved: " + command);
        }
    }

    private static void SidTypePolicy()
    {
        AssertEx.False(ServiceSidPolicy.CanMatchServiceRule(null));
        AssertEx.False(ServiceSidPolicy.CanMatchServiceRule(0));
        AssertEx.True(ServiceSidPolicy.CanMatchServiceRule(1));
        AssertEx.False(ServiceSidPolicy.CanMatchServiceRule(2));
        AssertEx.True(ServiceSidPolicy.CanMatchServiceRule(3));
        AssertEx.False(ServiceSidPolicy.CanMatchServiceRule(4));
    }

    private static void ServiceWithoutSidIsNotAllowable()
    {
        var candidate = Candidate(@"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe");
        var queried = new List<string>();
        var keys = new HashSet<string>();
        foreach (var (sidType, blocker, reasonText) in new (Func<string, uint?>?, PromptAllowBlocker, string)[]
        {
            // SERVICE_SID_TYPE_NONE: the token has no service SID.
            (name => { queried.Add(name); return 0; }, PromptAllowBlocker.ServiceSidUnavailable, "has no service SID"),
            // SCM could not report the type: unverified, never claimed absent.
            (name => { queried.Add(name); return null; }, PromptAllowBlocker.ServiceSidUnverified, "could not be read"),
            // No SID evidence at all.
            (null, PromptAllowBlocker.ServiceSidUnverified, "could not be read"),
        })
        {
            var identity = ServiceAttribution.Resolve(candidate, null, new[] { " edgeupdate " },
                serviceSidType: sidType);
            AssertEx.Equal(PromptIdentityKind.Service, identity.Kind);
            AssertEx.Equal("edgeupdate", identity.ServiceName);
            AssertEx.Equal(blocker, identity.AllowBlocker);
            AssertEx.False(identity.CanAllow);
            AssertEx.False(PromptAllowPolicy.TryCreate(identity, _ => true, out _, out string? reason));
            AssertEx.True(reason != null && reason.Contains(reasonText), "Refusal text is wrong: " + reason);
            keys.Add(identity.Key);
            AssertEx.True(identity.Key != PromptIdentity.ForService(candidate.ApplicationPath, "edgeupdate").Key,
                "A SID-less prompt shares a key with an allowable service prompt.");

            var queue = new PromptQueue(new TestClock());
            var added = queue.Enqueue(identity, candidate.RemoteAddress, candidate.RemotePort, candidate.Protocol);
            AssertEx.False(queue.GetPending()[0].CanAllow);
            bool applied = false;
            AssertEx.Equal(PromptActionStatus.NotAllowable,
                queue.Allow(added.Token, _ => applied = true).Status);
            AssertEx.False(applied, "A SID-less service reached the policy writer.");
        }
        AssertEx.SequenceEqual(new[] { "edgeupdate", "edgeupdate" }, queried);
        AssertEx.Equal(2, keys.Count, "Absent and unverified SIDs share a prompt key.");
    }

    private static void ServiceWithSidIsAllowable()
    {
        var candidate = Candidate(@"C:\Windows\System32\svchost.exe");
        foreach (uint type in new[] { ServiceSidPolicy.SidTypeUnrestricted, ServiceSidPolicy.SidTypeRestricted })
        {
            var identity = ServiceAttribution.Resolve(candidate, null, new[] { "Dnscache" },
                serviceSidType: _ => type);
            AssertEx.Equal(PromptAllowBlocker.None, identity.AllowBlocker);
            AssertEx.Equal(PromptIdentity.ForService(candidate.ApplicationPath, "Dnscache").Key, identity.Key);
            AssertEx.True(PromptAllowPolicy.TryCreate(identity, _ => true, out var policy, out _) && policy != null);
        }
        // Several services in one PID never reach the SID query.
        var shared = ServiceAttribution.Resolve(candidate, null, new[] { "Dnscache", "NlaSvc" },
            serviceSidType: _ => throw new InvalidOperationException("SID queried for a shared host."));
        AssertEx.Equal(PromptAllowBlocker.AmbiguousService, shared.AllowBlocker);
    }

    private static void InventoryScopesUnresolved()
    {
        var inventory = ServiceExecutableInventory.Build(new[]
        {
            new ServiceImageRegistration("Good", "\"C:\\Program Files\\Good\\good.exe\" -service"),
            new ServiceImageRegistration("Odd", @"C:\Program Files\Odd Vendor\oddsvc.exe -run"),
            new ServiceImageRegistration("Bare", @"C:\Bare Vendor\baresvc -x"),
            new ServiceImageRegistration("Multi", new[] { @"C:\Multi\multi.exe" }),
        }, Windows);

        AssertEx.SequenceEqual(new[] { "Bare", "Odd" }, inventory.UnresolvedServices);
        AssertEx.Equal(ServiceRegistrationStatus.Registered, inventory.Lookup(@"C:\Program Files\Good\good.exe"));
        AssertEx.Equal(ServiceRegistrationStatus.Registered, inventory.Lookup(@"C:\Multi\multi.exe"));
        // Only executables the unresolved command could start become uncertain.
        AssertEx.Equal(ServiceRegistrationStatus.Unknown, inventory.Lookup(@"C:\Program Files\Odd Vendor\oddsvc.exe"));
        AssertEx.Equal(ServiceRegistrationStatus.Unknown, inventory.Lookup(@"D:\copy\ODDSVC.EXE"));
        AssertEx.Equal(ServiceRegistrationStatus.Unknown, inventory.Lookup(@"C:\Program.exe"));
        AssertEx.Equal(ServiceRegistrationStatus.Unknown, inventory.Lookup(@"C:\Bare Vendor\baresvc.exe"));
        AssertEx.Equal(ServiceRegistrationStatus.Unknown, inventory.Lookup(@"C:\Bare.exe"));
        // Ordinary applications keep their answer (SW-17).
        AssertEx.Equal(ServiceRegistrationStatus.NotRegistered,
            inventory.Lookup(@"C:\Program Files\Mozilla Firefox\firefox.exe"));
        // CreateProcess would also try C:\Program Files\Odd.exe for the unquoted command.
        AssertEx.Equal(ServiceRegistrationStatus.Unknown, inventory.Lookup(@"C:\apps\odd.exe"));
        AssertEx.Equal(ServiceRegistrationStatus.NotRegistered, inventory.Lookup(@"C:\apps\oddsvc2.exe"));
        AssertEx.Equal(ServiceRegistrationStatus.NotRegistered, inventory.Lookup(@"C:\apps\baresvcx.exe"));
        AssertEx.Equal(ServiceRegistrationStatus.Unknown, inventory.Lookup(string.Empty));
    }

    private static void InventorySkipsUnusable()
    {
        var inventory = ServiceExecutableInventory.Build(new[]
        {
            new ServiceImageRegistration("NoPath", null),
            new ServiceImageRegistration("Blank", "   "),
            new ServiceImageRegistration("Binary", new byte[] { 0x43, 0x00 }),
            new ServiceImageRegistration("Number", 7),
        }, Windows);

        AssertEx.SequenceEqual(new[] { "Binary", "Blank", "NoPath", "Number" }, inventory.UnusableServices);
        AssertEx.Equal(0, inventory.UnresolvedServices.Count);
        AssertEx.Equal(ServiceRegistrationStatus.NotRegistered, inventory.Lookup(@"C:\apps\sample.exe"));
    }

    private static void RegistrationUncertaintyIsNotServiceAttribution()
    {
        var firefox = Candidate(@"C:\Program Files\Mozilla Firefox\firefox.exe");
        var identity = ServiceAttribution.Resolve(firefox, null, Array.Empty<string>(), registrationUnknown: true);
        AssertEx.Equal(PromptIdentityKind.Executable, identity.Kind);
        AssertEx.Equal(PromptAllowBlocker.ServiceRegistrationUnknown, identity.AllowBlocker);
        AssertEx.Equal(0, identity.AmbiguousServiceNames.Count);
        AssertEx.False(PromptAllowPolicy.TryCreate(identity, _ => true, out _, out string? reason));
        AssertEx.True(reason != null && reason.Contains("inventory"), "Refusal did not name the inventory.");
        AssertEx.True(identity.Key != PromptIdentity.ForExecutable(firefox.ApplicationPath).Key,
            "An uncertain prompt shares a key with an allowable executable prompt.");
        var queue = new PromptQueue(new TestClock());
        var added = queue.Enqueue(identity, firefox.RemoteAddress, firefox.RemotePort, firefox.Protocol);
        AssertEx.Equal(PromptActionStatus.NotAllowable, queue.Allow(added.Token).Status);

        // Established evidence still dominates uncertainty.
        AssertEx.Equal(PromptIdentityKind.AmbiguousService, ServiceAttribution.Resolve(
            Candidate(@"C:\Windows\System32\svchost.exe"), null, Array.Empty<string>(), registrationUnknown: true).Kind);
        AssertEx.Equal(PromptIdentityKind.AmbiguousService, ServiceAttribution.Resolve(
            firefox, null, Array.Empty<string>(), executableIsRegisteredService: true, registrationUnknown: true).Kind);
        AssertEx.Equal(PromptIdentityKind.AmbiguousService, ServiceAttribution.Resolve(
            firefox, null, Array.Empty<string>(), snapshotUncertain: true, registrationUnknown: true).Kind);
        AssertEx.Equal(PromptAllowBlocker.None,
            ServiceAttribution.Resolve(firefox, null, Array.Empty<string>()).AllowBlocker);
    }

    private static void AllowBlockerRoundTrips()
    {
        var queue = new PromptQueue(new TestClock());
        queue.Enqueue(PromptIdentity.ForServiceWithoutSid(@"C:\apps\updater.exe", "updater"), "203.0.113.20", 443, 6);
        var dto = PromptWireDto.FromPrompt(queue.GetPending()[0]);
        var copy = JsonSerializer.Deserialize<PromptWireDto>(JsonSerializer.Serialize(dto))!;
        AssertEx.False(copy.CanAllow);
        AssertEx.Equal(PromptAllowBlocker.ServiceSidUnavailable, copy.AllowBlocker);
        AssertEx.Equal(PromptIdentityKind.Service, copy.SubjectKind);
        AssertEx.Equal("updater", copy.ServiceName);
    }

    private static void SidNoticeQuotesServiceName()
    {
        string spaced = PromptNoticeText.Blocked(PromptAllowBlocker.ServiceSidUnavailable, "Steam Client Service");
        AssertEx.True(spaced.Contains("sc.exe sidtype \"Steam Client Service\" unrestricted, then restart it."), spaced);
        AssertEx.True(spaced.StartsWith("Steam Client Service has no service SID", StringComparison.Ordinal), spaced);
        string longName = PromptNoticeText.Blocked(PromptAllowBlocker.ServiceSidUnavailable, "MicrosoftEdgeElevationService");
        AssertEx.True(longName.Contains("sc.exe sidtype \"MicrosoftEdgeElevationService\" unrestricted"), longName);
        AssertEx.Equal("sc.exe sidtype \"odd\\\"name\" unrestricted", PromptNoticeText.SidTypeCommand("odd\"name"));

        // Only the NONE notice offers the command; the unverified one never claims "no service SID".
        string unverified = PromptNoticeText.Blocked(PromptAllowBlocker.ServiceSidUnverified, "Steam Client Service");
        AssertEx.False(unverified.Contains("sc.exe") || unverified.Contains("has no service SID"), unverified);
        var texts = new[] { PromptAllowBlocker.AmbiguousService, PromptAllowBlocker.ServiceSidUnavailable,
            PromptAllowBlocker.ServiceSidUnverified, PromptAllowBlocker.ServiceRegistrationUnknown }
            .Select(blocker => PromptNoticeText.Blocked(blocker, "svc") + "|" + PromptNoticeText.BlockedTooltip(blocker));
        AssertEx.Equal(4, texts.Distinct().Count(), "Two blockers share popup wording.");
    }

    private static DropCandidate Candidate(string applicationPath) => new(
        new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
        5_000_000_000,
        applicationPath,
        null,
        "192.0.2.10",
        52144,
        "203.0.113.20",
        443,
        6);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    }
}

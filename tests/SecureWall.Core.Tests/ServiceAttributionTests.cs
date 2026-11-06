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
            yield return ("popup notice and Allow tooltip come from the notice text helper", PopupUsesNoticeText);
            yield return ("prompt preview has a service SID notice variant", PromptPreviewSidNotice);
            yield return ("service inventory names every service of a resolved executable", InventoryNamesServices);
            yield return ("a service executable outside Windows that runs no service is allowable and names its services", ServiceExecutableIsAllowable);
            yield return ("a service executable stays shared when its services cannot all be named or it is a Windows host", ServiceExecutableStaysShared);
            yield return ("a service executable whose service run is unproven offers Allow anyway", ServiceExecutableAllowAnyway);
            yield return ("only plain paths outside the Windows directory count as outside it", WindowsDirectoryCheck);
            yield return ("a service executable prompt is never Always allowed", ServiceExecutableHasNoAlwaysAllow);
            yield return ("covered services round trip and appear in the popup notice", CoveredServicesNotice);
            yield return ("disabled Allow points to Application Exceptions", DisabledAllowPointsToManage);
            yield return ("prompt preview has service executable variants", PromptPreviewServiceExecutable);
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

    // The popup is outside the test build, so pin its use of the tested helper: inline
    // strings would bring back the unquoted command and the clipped notice.
    private static void PopupUsesNoticeText()
    {
        string popup = PromptTransactionIntegrationTests.Source("TinyWall/Prompting/BlockedConnectionPopup.cs");
        AssertEx.True(popup.Contains("PromptNoticeText.Blocked(prompt.AllowBlocker, prompt.ServiceName, prompt.ExecutablePath)"), "The notice must come from PromptNoticeText.Blocked.");
        AssertEx.True(popup.Contains("PromptNoticeText.BlockedTooltip(prompt.AllowBlocker)"), "The tooltip must come from PromptNoticeText.BlockedTooltip.");
        AssertEx.False(popup.Contains("sc.exe") || popup.Contains("sidtype"), "The popup must not build the admin command itself.");
        AssertEx.True(popup.Contains("FitNoticeLabel();"), "The notice label must be fitted to its text.");
    }

    // Debug-only /promptpreview variants let the owner check the SID notice layout on a VM
    // without a real SID-type NONE service.
    private static void PromptPreviewSidNotice()
    {
        var now = DateTimeOffset.UtcNow;
        PromptWireDto plain = PromptPreviewSamples.Create(new[] { "/promptpreview" }, now);
        AssertEx.True(plain.CanAllow);
        AssertEx.Equal(PromptAllowBlocker.None, plain.AllowBlocker);

        PromptWireDto notice = PromptPreviewSamples.Create(new[] { "/promptpreview", "/sidnotice" }, now);
        AssertEx.False(notice.CanAllow);
        AssertEx.Equal(PromptAllowBlocker.ServiceSidUnavailable, notice.AllowBlocker);
        AssertEx.Equal<string?>("MicrosoftEdgeElevationService", notice.ServiceName);
        AssertEx.Equal(PromptIdentityKind.Service, notice.SubjectKind);
        AssertEx.True(notice.ExpiresUtc > now);

        PromptWireDto spaced = PromptPreviewSamples.Create(new[] { "/promptpreview", "/SIDNOTICE", "Steam Client Service", "/nowarnings" }, now);
        AssertEx.Equal<string?>("Steam Client Service", spaced.ServiceName);
        AssertEx.True(PromptNoticeText.Blocked(spaced.AllowBlocker, spaced.ServiceName)
            .Contains("sc.exe sidtype \"Steam Client Service\" unrestricted"));
        AssertEx.Equal<string?>("MicrosoftEdgeElevationService",
            PromptPreviewSamples.Create(new[] { "/promptpreview", "/sidnotice", "/nowarnings" }, now).ServiceName);

        AssertEx.Equal(ExecutableRiskFlags.None, PromptPreviewSamples.RiskFlags(new[] { "/promptpreview", "/nowarnings" }));
        AssertEx.Equal(ExecutableRiskFlags.Unsigned | ExecutableRiskFlags.UserWritableLocation | ExecutableRiskFlags.RecentlyModified,
            PromptPreviewSamples.RiskFlags(new[] { "/promptpreview", "/sidnotice" }));

        string program = PromptTransactionIntegrationTests.Source("TinyWall/Program.cs");
        AssertEx.True(program.Contains("popup.ShowPrompt(PromptPreviewSamples.Create(args, DateTimeOffset.UtcNow));"));
        AssertEx.True(program.Contains("popup.RiskProbe = _ => PromptPreviewSamples.RiskFlags(args);"));
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

    private const string Updater = @"C:\Program Files (x86)\Google\GoogleUpdater\157.0.8089.0\updater.exe";
    private static readonly string[] UpdaterServices = { "GoogleUpdaterService157.0.8089.0", "GoogleUpdaterInternalService157.0.8089.0" };

    private static void InventoryNamesServices()
    {
        var inventory = ServiceExecutableInventory.Build(new[]
        {
            new ServiceImageRegistration(UpdaterServices[0], "\"" + Updater + "\" --system --windows-service --service=update"),
            new ServiceImageRegistration(UpdaterServices[1], "\"" + Updater + "\" --system --windows-service --service=update-internal"),
            new ServiceImageRegistration("Other", "\"C:\\Program Files\\Other\\other.exe\""),
        }, Windows);

        AssertEx.Equal(ServiceRegistrationStatus.Registered, inventory.Lookup(Updater));
        AssertEx.SequenceEqual(new[] { UpdaterServices[1], UpdaterServices[0] }, inventory.RegisteredServices(Updater));
        AssertEx.SequenceEqual(new[] { UpdaterServices[1], UpdaterServices[0] }, inventory.RegisteredServices(Updater.ToUpperInvariant()));
        AssertEx.Equal(0, inventory.RegisteredServices(@"C:\Program Files\Mozilla Firefox\firefox.exe").Count);
        AssertEx.Equal(0, inventory.RegisteredServices(string.Empty).Count);

        // An unresolved registration that names the file might be one more service, so the
        // list is withheld even though the path itself is registered.
        var incomplete = ServiceExecutableInventory.Build(new[]
        {
            new ServiceImageRegistration(UpdaterServices[0], "\"" + Updater + "\" --system"),
            new ServiceImageRegistration("Unquoted", @"C:\Program Files (x86)\Google\Other Updater\updater.exe --system"),
        }, Windows);
        AssertEx.Equal(ServiceRegistrationStatus.Registered, incomplete.Lookup(Updater));
        AssertEx.Equal(0, incomplete.RegisteredServices(Updater).Count);
    }

    private static void ServiceExecutableIsAllowable()
    {
        var updater = Candidate(Updater);
        var identity = ServiceAttribution.Resolve(updater, Audit(updater), Array.Empty<string>(),
            executableIsRegisteredService: true, registeredServices: UpdaterServices, windowsDirectory: Windows,
            processStillRunning: () => true);
        AssertEx.Equal(PromptIdentityKind.Executable, identity.Kind);
        AssertEx.Equal(PromptAllowBlocker.None, identity.AllowBlocker);
        AssertEx.SequenceEqual(new[] { UpdaterServices[1], UpdaterServices[0] }, identity.CoveredServiceNames);
        AssertEx.True(identity.Key != PromptIdentity.ForExecutable(Updater).Key,
            "A service executable prompt shares a key with a plain executable prompt.");
        AssertEx.True(identity.Key != PromptIdentity.ForServiceExecutable(Updater, new[] { UpdaterServices[0] }).Key,
            "Prompts covering different services share a key.");
        AssertEx.True(PromptAllowPolicy.TryCreate(identity, _ => true, out var policy, out _) && policy != null);
        AssertEx.Equal("*", policy!.AllowedRemoteTcpConnectPorts);
        AssertEx.Equal<string?>(null, policy.AllowedLocalTcpListenerPorts);

        var queue = new PromptQueue(new TestClock());
        var added = queue.Enqueue(identity, updater.RemoteAddress, updater.RemotePort, updater.Protocol);
        AssertEx.True(queue.GetPending()[0].CanAllow);
        AssertEx.Equal(PromptActionStatus.Allowed, queue.Allow(added.Token, _ => true).Status);

        // Services running in the process still take precedence.
        AssertEx.Equal(PromptIdentityKind.Service, ServiceAttribution.Resolve(updater, null, new[] { UpdaterServices[0] },
            executableIsRegisteredService: true, serviceSidType: _ => ServiceSidPolicy.SidTypeUnrestricted,
            registeredServices: UpdaterServices, windowsDirectory: Windows).Kind);
    }

    private static void ServiceExecutableStaysShared()
    {
        var updater = Candidate(Updater);
        void Shared(DropCandidate candidate, IReadOnlyList<string>? names, string? windows, bool uncertain = false,
            string[]? running = null, bool correlated = true, Func<bool>? alive = null)
        {
            var identity = ServiceAttribution.Resolve(candidate, correlated ? Audit(candidate) : null, running ?? Array.Empty<string>(),
                executableIsRegisteredService: true, snapshotUncertain: uncertain,
                registeredServices: names, windowsDirectory: windows,
                processStillRunning: alive ?? (() => true));
            AssertEx.Equal(PromptIdentityKind.AmbiguousService, identity.Kind);
            AssertEx.False(identity.CanAllow);
            AssertEx.Equal(0, identity.CoveredServiceNames.Count);
            AssertEx.False(PromptAllowPolicy.TryCreate(identity, _ => true, out _, out _));
        }

        // The process is opened only once every other condition holds.
        Func<bool> unasked = () => throw new InvalidOperationException("Liveness checked for an ineligible drop.");
        Shared(Candidate(@"C:\Windows\System32\dllhost.exe"), new[] { "COMSysApp" }, Windows, alive: unasked);
        Shared(updater, null, Windows);
        Shared(updater, Array.Empty<string>(), Windows);
        Shared(updater, UpdaterServices, null);
        Shared(updater, null, Windows, uncertain: true);
        Shared(Candidate(@"C:\Windows\System32\svchost.exe"), new[] { "Dnscache" }, Windows, uncertain: true);
        Shared(updater, UpdaterServices, Windows, running: new[] { "A", "B" });
        Shared(Candidate(@"C:\Windows\System32\dllhost.exe"), new[] { "COMSysApp" }, Windows);
        Shared(Candidate(@"c:\windows\SysWOW64\dllhost.exe"), new[] { "COMSysApp" }, @"C:\Windows\");
        Shared(Candidate(@"C:\Program Files\Host\svchost.exe"), new[] { "Odd" }, Windows);
    }

    private static void ServiceExecutableAllowAnyway()
    {
        var updater = Candidate(Updater);
        PromptIdentity Unconfirmed(bool uncertain = false, bool correlated = true, Func<bool>? alive = null)
        {
            var identity = ServiceAttribution.Resolve(updater, correlated ? Audit(updater) : null, Array.Empty<string>(),
                executableIsRegisteredService: true, snapshotUncertain: uncertain,
                registeredServices: UpdaterServices, windowsDirectory: Windows, processStillRunning: alive);
            AssertEx.Equal(PromptIdentityKind.Executable, identity.Kind);
            AssertEx.True(identity.CanAllow);
            AssertEx.True(identity.ServiceRunUnconfirmed);
            AssertEx.Equal(2, identity.CoveredServiceNames.Count);
            AssertEx.True(PromptAllowPolicy.TryCreate(identity, _ => true, out _, out _));
            return identity;
        }

        Func<bool> unasked = () => throw new InvalidOperationException("Liveness checked for an unconfirmed drop.");
        // A drop never matched to an audit event has no PID, so "runs no service" is unproven.
        PromptIdentity uncorrelated = Unconfirmed(correlated: false, alive: unasked);
        // A process that exited before the SCM snapshot, or a reused PID, is missing from it.
        Unconfirmed(alive: () => false);
        Unconfirmed();
        // An uncertain snapshot for this PID: whichever service ran, it ran this file.
        Unconfirmed(uncertain: true, alive: unasked);

        PromptIdentity confirmed = PromptIdentity.ForServiceExecutable(Updater, UpdaterServices);
        AssertEx.False(confirmed.ServiceRunUnconfirmed);
        AssertEx.True(uncorrelated.Key != confirmed.Key, "Unconfirmed and confirmed prompts share a key.");

        var queue = new PromptQueue(new TestClock());
        queue.Enqueue(uncorrelated, updater.RemoteAddress, updater.RemotePort, updater.Protocol);
        var dto = PromptWireDto.FromPrompt(queue.GetPending()[0]);
        var copy = JsonSerializer.Deserialize<PromptWireDto>(JsonSerializer.Serialize(dto))!;
        AssertEx.True(copy.CanAllow && copy.ServiceRunUnconfirmed);

        string notice = PromptNoticeText.CoveredServices(copy.CoveredServiceNames, copy.ServiceRunUnconfirmed);
        AssertEx.True(notice.Contains("could not tell whether the program or one of those services made this connection") &&
            notice.Contains("Allow anyway permanently permits this program and those services"), notice);
        AssertEx.True(PromptNoticeText.CoveredServices(new[] { "Solo" }, true).Contains("the program or that service"));

        string popup = PromptTransactionIntegrationTests.Source("TinyWall/Prompting/BlockedConnectionPopup.cs");
        AssertEx.True(popup.Contains("PromptNoticeText.AllowAnywayButtonText"), "The popup must label Allow anyway.");
        AssertEx.True(popup.Contains("PromptNoticeText.CoveredServices(prompt.CoveredServiceNames, prompt.ServiceRunUnconfirmed)"));

        PromptWireDto preview = PromptPreviewSamples.Create(new[] { "/promptpreview", "/allowanyway" }, DateTimeOffset.UtcNow);
        AssertEx.True(preview.CanAllow && preview.ServiceRunUnconfirmed && preview.CoveredServiceNames.Length == 2);
        AssertEx.False(PromptPreviewSamples.Create(new[] { "/promptpreview", "/serviceexe" }, DateTimeOffset.UtcNow).ServiceRunUnconfirmed);
    }

    private static void WindowsDirectoryCheck()
    {
        AssertEx.False(ServiceAttribution.IsInWindowsDirectory(Updater, Windows));
        AssertEx.False(ServiceAttribution.IsInWindowsDirectory(@"C:\WindowsApps\x.exe", Windows));
        AssertEx.False(ServiceAttribution.IsInWindowsDirectory(@"D:\Windows\x.exe", Windows));
        AssertEx.True(ServiceAttribution.IsInWindowsDirectory(@"C:\Windows\System32\dllhost.exe", Windows));
        AssertEx.True(ServiceAttribution.IsInWindowsDirectory(@"C:\WINDOWS\x.exe", @"c:\windows\"));
        foreach (string odd in new[]
        {
            @"\\?\C:\Program Files\x.exe", @"\Device\HarddiskVolume3\Program Files\x.exe", @"\\server\share\x.exe",
            @"C:\Program Files\..\Windows\x.exe", @"C:\Program Files\.\x.exe", "C:/Windows/x.exe", "System", string.Empty,
        })
            AssertEx.True(ServiceAttribution.IsInWindowsDirectory(odd, Windows), odd);
        AssertEx.True(ServiceAttribution.IsInWindowsDirectory(Updater, null));
        AssertEx.True(ServiceAttribution.IsInWindowsDirectory(Updater, " "));
    }

    private static void ServiceExecutableHasNoAlwaysAllow()
    {
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int queue = service.IndexOf("private bool TryQueueAlwaysAllow(", StringComparison.Ordinal);
        AssertEx.True(queue >= 0 && service.IndexOf("identity.CoveredServiceNames.Count != 0", queue, StringComparison.Ordinal) is int at &&
            at > queue && at < queue + 600, "Automatic Always allow must skip service executables.");
        AssertEx.True(service.Contains("allowPolicy.Identity.CoveredServiceNames.Count != 0"), "ALLOW_PROMPT_ALWAYS must refuse service executables.");
        string popup = PromptTransactionIntegrationTests.Source("TinyWall/Prompting/BlockedConnectionPopup.AlwaysAllow.cs");
        AssertEx.True(popup.Contains("prompt.CoveredServiceNames.Length == 0"), "The popup must not offer Always allow.");
        string missed = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallController.MissedPrompts.cs");
        AssertEx.True(missed.Contains("prompt.CoveredServiceNames.Length == 0") &&
            missed.Contains("if (alwaysAllow && prompt.CoveredServiceNames.Length != 0)"), "The missed list must not offer Always allow.");
    }

    private static void CoveredServicesNotice()
    {
        var queue = new PromptQueue(new TestClock());
        queue.Enqueue(PromptIdentity.ForServiceExecutable(Updater, UpdaterServices), "142.251.16.100", 443, 6);
        var dto = PromptWireDto.FromPrompt(queue.GetPending()[0]);
        var copy = JsonSerializer.Deserialize<PromptWireDto>(JsonSerializer.Serialize(dto))!;
        AssertEx.True(copy.CanAllow);
        AssertEx.Equal(PromptIdentityKind.Executable, copy.SubjectKind);
        AssertEx.SequenceEqual(new[] { UpdaterServices[1], UpdaterServices[0] }, copy.CoveredServiceNames);
        var plainQueue = new PromptQueue(new TestClock());
        plainQueue.Enqueue(PromptIdentity.ForExecutable(Updater), "192.0.2.1", 443, 6);
        AssertEx.Equal(0, PromptWireDto.FromPrompt(plainQueue.GetPending()[0]).CoveredServiceNames.Length);

        string two = PromptNoticeText.CoveredServices(copy.CoveredServiceNames);
        AssertEx.Equal("This file also runs as the Windows services GoogleUpdaterInternalService157.0.8089.0 and " +
            "GoogleUpdaterService157.0.8089.0. Allow permanently permits this program and those services to reach all " +
            "destinations and ports over TCP/UDP.", two);
        AssertEx.True(PromptNoticeText.CoveredServices(new[] { "Solo" }).StartsWith(
            "This file also runs as the Windows service Solo. Allow permanently permits this program and that service", StringComparison.Ordinal));
        string four = PromptNoticeText.CoveredServices(new[] { "A", "B", "C", "D" });
        AssertEx.True(four.Contains("services A, B, C and D."), four);
        AssertEx.Throws<ArgumentException>(() => PromptNoticeText.CoveredServices(Array.Empty<string>()));

        string popup = PromptTransactionIntegrationTests.Source("TinyWall/Prompting/BlockedConnectionPopup.cs");
        AssertEx.True(popup.Contains("noticeLabel.Text = PromptNoticeText.CoveredServices(prompt.CoveredServiceNames, prompt.ServiceRunUnconfirmed);"));
        AssertEx.True(popup.Contains("PromptNoticeText.CoveredServicesTooltip"));
    }

    private static void DisabledAllowPointsToManage()
    {
        string host = PromptNoticeText.Blocked(PromptAllowBlocker.AmbiguousService, null, @"C:\Windows\System32\svchost.exe");
        AssertEx.True(host.Contains("add a rule for that service in Manage, Application Exceptions"), host);
        string file = PromptNoticeText.Blocked(PromptAllowBlocker.AmbiguousService, null, Updater);
        AssertEx.True(file.Contains("add a rule for it in Manage, Application Exceptions") &&
            file.Contains("also covers the services that run the same file"), file);
        AssertEx.Equal(host, PromptNoticeText.Blocked(PromptAllowBlocker.AmbiguousService, null));
        string unknown = PromptNoticeText.Blocked(PromptAllowBlocker.ServiceRegistrationUnknown, null, Updater);
        AssertEx.True(unknown.Contains("Manage, Application Exceptions"), unknown);
    }

    private static void PromptPreviewServiceExecutable()
    {
        var now = DateTimeOffset.UtcNow;
        PromptWireDto allowable = PromptPreviewSamples.Create(new[] { "/promptpreview", "/serviceexe" }, now);
        AssertEx.True(allowable.CanAllow);
        AssertEx.Equal(PromptIdentityKind.Executable, allowable.SubjectKind);
        AssertEx.Equal(2, allowable.CoveredServiceNames.Length);
        allowable.CoveredServiceNames[0] = "Mutated";
        AssertEx.True(PromptPreviewSamples.SampleCoveredServices[0] != "Mutated", "The preview sample array was shared.");

        PromptWireDto shared = PromptPreviewSamples.Create(new[] { "/promptpreview", "/sharedhost" }, now);
        AssertEx.False(shared.CanAllow);
        AssertEx.Equal(PromptIdentityKind.AmbiguousService, shared.SubjectKind);
        AssertEx.Equal(PromptAllowBlocker.AmbiguousService, shared.AllowBlocker);
        AssertEx.Equal(0, shared.CoveredServiceNames.Length);
    }

    // The Security audit event that correlates with the candidate, giving it a process ID.
    private static BlockedConnectionAuditEvent Audit(DropCandidate candidate) => new(
        candidate.TimestampUtc,
        4242,
        candidate.ApplicationPath,
        ConnectionDirection.Outbound,
        candidate.LocalAddress,
        candidate.LocalPort,
        candidate.RemoteAddress,
        candidate.RemotePort,
        candidate.Protocol,
        candidate.FilterRuntimeId,
        null);

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

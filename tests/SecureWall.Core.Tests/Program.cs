using pylorak.TinyWall.Prompting;
using pylorak.TinyWall;
using System.Text.Json;

namespace SecureWall.Core.Tests;

internal static class Program
{
    private static int Main()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("executable identity keys ignore case", ExecutableIdentityKeysIgnoreCase),
            ("package SID takes attribution precedence", PackageSidTakesPrecedence),
            ("service identity includes exact name and path", ServiceIdentityIncludesNameAndPath),
            ("ambiguous services are sorted and immutable", AmbiguousServicesAreSorted),
            ("empty identities are rejected", EmptyIdentitiesAreRejected),
            ("allow policy is outbound only", AllowPolicyIsOutboundOnly),
            ("allow policy rejects ambiguous services", AllowPolicyRejectsAmbiguousServices),
            ("queue enqueues and coalesces within three seconds", QueueEnqueuesAndCoalesces),
            ("queue is bounded to 32 prompts", QueueIsBounded),
            ("queue expires tokens after two minutes", QueueExpiresTokens),
            ("ignore creates a five minute cooldown", IgnoreCreatesCooldown),
            ("allow tokens are single use", AllowTokensAreSingleUse),
            ("unknown tokens fail closed", UnknownTokensFailClosed),
            ("ambiguous service tokens cannot be allowed", AmbiguousTokensCannotBeAllowed),
            ("queue delivers oldest prompts first", QueueDeliversOldestFirst),
            ("promptable filters replace atomically", PromptableFiltersReplaceAtomically),
            ("promptable filter snapshots survive concurrent replacement", PromptableFilterSnapshotsAreThreadSafe),
            ("only outbound default-block filters are promptable", OnlyOutboundDefaultBlocksArePromptable),
            ("critical WFP filter pairs require both registrations", CriticalWfpFilterPairsRequireBothRegistrations),
            ("critical WFP filter pairs propagate persistent failure", CriticalWfpFilterPairsPropagatePersistentFailure),
            ("critical WFP filter pairs propagate boot-time failure", CriticalWfpFilterPairsPropagateBootTimeFailure),
            ("optional WFP filter failures propagate", OptionalWfpFilterFailuresPropagate),
            ("network activity statuses state observed facts", NetworkActivityStatusesStateObservedFacts),
            ("event 5157 parser uses named fields and 64-bit filter IDs", Event5157ParserUsesNamedFields),
            ("non-package SID sentinels are never package authority", NonPackageSidSentinelsAreRejected),
            ("audit lease restores the exact prior flags once", AuditLeaseRestoresExactFlags),
            ("drop correlation requires exact filter path protocol and tuple", DropCorrelationRequiresExactFields),
            ("drop correlation allows one second timestamp skew", DropCorrelationAllowsOneSecondSkew),
            ("service attribution distinguishes executable service and ambiguity", ServiceAttributionDistinguishesSubjects),
            ("package attribution takes precedence over services", PackageAttributionTakesPrecedence),
            ("unattributed svchost fails closed", UnattributedServiceHostFailsClosed),
            ("unattributed registered service executable fails closed", UnattributedRegisteredServiceExecutableFailsClosed),
            ("service image paths preserve quoted and unquoted executable names", ServiceImagePathsAreParsedConservatively),
            ("pipe authorization requires exact executable path in every build", PipeAuthorizationRequiresExactExecutablePath),
            ("installation conflict guard detects TinyWall service", InstallationConflictGuardDetectsTinyWall),
            ("candidate buffer enriches exact matches before deadline", CandidateBufferEnrichesBeforeDeadline),
            ("candidate buffer releases unmatched drops after deadline", CandidateBufferReleasesAfterDeadline),
            ("candidate buffer is bounded", CandidateBufferIsBounded),
            ("failed allow application leaves token pending", FailedAllowLeavesTokenPending),
            ("prompt wire DTO round trips without authority fields", PromptWireDtoRoundTrips),
            ("display coordinator shows one prompt at a time", DisplayCoordinatorShowsOneAtATime),
            ("display coordinator advances after allow success", DisplayCoordinatorAdvancesAfterAllow),
            ("display coordinator retains block after allow failure", DisplayCoordinatorRetainsAfterAllowFailure),
            ("display coordinator retains prompt after dismiss failure", DisplayCoordinatorRetainsAfterDismissFailure),
            ("close and timeout behave as ignore", CloseAndTimeoutBehaveAsIgnore),
            ("display coordinator disposal does not grant or dismiss", DisplayCoordinatorDisposalIsPassive),
            ("ai explain subject uses file name not full path", AiExplainSubjectUsesFileNameNotFullPath),
            ("ai explain subject includes remote endpoint only when opted in", AiExplainSubjectIncludesRemoteEndpointOnlyWhenOptedIn),
            ("ai explain composer omits verdict and full path", AiExplainComposerOmitsVerdictAndFullPath),
            ("ai explain response parser extracts assistant content", AiExplainResponseParserExtractsContent),
            ("ai explain response parser maps http and malformed errors", AiExplainResponseParserMapsErrors),
            ("ai explain settings validate url and model", AiExplainSettingsValidateUrlAndModel),
        };
        tests = tests.Concat(EnforcementHardeningTests.Cases)
            .Concat(LifecycleHardeningTests.Cases)
            .Concat(ControllerHardeningTests.Cases)
            .Concat(NetEventParsingTests.Cases)
            .Concat(AtomicFileWriterTests.Cases).ToArray();
        var failed = 0;

        foreach (var (name, test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"{tests.Length - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void ExecutableIdentityKeysIgnoreCase()
    {
        var lower = PromptIdentity.ForExecutable(@"C:\apps\sample.exe");
        var upper = PromptIdentity.ForExecutable(@"c:\APPS\SAMPLE.EXE");

        AssertEx.Equal(lower.Key, upper.Key);
        AssertEx.Equal(PromptIdentityKind.Executable, lower.Kind);
    }

    private static void PackageSidTakesPrecedence()
    {
        var identity = PromptIdentity.FromAttribution(
            @"C:\Program Files\WindowsApps\sample.exe",
            "S-1-15-2-1234",
            new[] { "ExampleService" });

        AssertEx.Equal(PromptIdentityKind.Package, identity.Kind);
        AssertEx.Equal("S-1-15-2-1234", identity.PackageSid);
        AssertEx.Equal<string?>(null, identity.ServiceName);
    }

    private static void ServiceIdentityIncludesNameAndPath()
    {
        var one = PromptIdentity.ForService(@"C:\Windows\System32\svchost.exe", "Dnscache");
        var two = PromptIdentity.ForService(@"c:\windows\system32\SVCHOST.EXE", "dnscache");

        AssertEx.Equal(one.Key, two.Key);
        AssertEx.Equal(PromptIdentityKind.Service, one.Kind);
        AssertEx.Equal("Dnscache", one.ServiceName);
    }

    private static void AmbiguousServicesAreSorted()
    {
        var identity = PromptIdentity.ForAmbiguousServices(
            @"C:\Windows\System32\svchost.exe",
            new[] { "Zulu", "alpha", "Beta", "alpha" });

        AssertEx.Equal(PromptIdentityKind.AmbiguousService, identity.Kind);
        AssertEx.SequenceEqual(new[] { "alpha", "Beta", "Zulu" }, identity.AmbiguousServiceNames);
        AssertEx.Throws<NotSupportedException>(() =>
            ((IList<string>)identity.AmbiguousServiceNames).Add("Mutated"));
    }

    private static void EmptyIdentitiesAreRejected()
    {
        AssertEx.Throws<ArgumentException>(() => PromptIdentity.ForExecutable(" "));
        AssertEx.Throws<ArgumentException>(() => PromptIdentity.ForPackage("", null));
        AssertEx.Throws<ArgumentException>(() => PromptIdentity.ForPackage("S-1-0-0", null));
        AssertEx.Throws<ArgumentException>(() => PromptIdentity.ForService("", "Dnscache"));
        AssertEx.Throws<ArgumentException>(() => PromptIdentity.ForService(@"C:\app.exe", ""));
        AssertEx.Throws<ArgumentException>(() =>
            PromptIdentity.ForAmbiguousServices(@"C:\app.exe", Array.Empty<string>()));
    }

    private static void AllowPolicyIsOutboundOnly()
    {
        var identities = new[]
        {
            PromptIdentity.ForExecutable(@"C:\apps\sample.exe"),
            PromptIdentity.ForPackage("S-1-15-2-1234", null),
            PromptIdentity.ForService(@"C:\Windows\System32\svchost.exe", "Dnscache"),
        };

        foreach (var identity in identities)
        {
            var policy = PromptAllowPolicy.Create(identity);
            AssertEx.Equal("*", policy.AllowedRemoteTcpConnectPorts);
            AssertEx.Equal("*", policy.AllowedRemoteUdpConnectPorts);
            AssertEx.Equal<string?>(null, policy.AllowedLocalTcpListenerPorts);
            AssertEx.Equal<string?>(null, policy.AllowedLocalUdpListenerPorts);
        }
    }

    private static void AllowPolicyRejectsAmbiguousServices()
    {
        var identity = PromptIdentity.ForAmbiguousServices(
            @"C:\Windows\System32\svchost.exe",
            new[] { "Dnscache", "NlaSvc" });

        AssertEx.Throws<InvalidOperationException>(() => PromptAllowPolicy.Create(identity));
    }

    private static void QueueEnqueuesAndCoalesces()
    {
        var clock = new FakeClock();
        var queue = new PromptQueue(clock);
        var identity = PromptIdentity.ForExecutable(@"C:\apps\sample.exe");

        var first = queue.Enqueue(identity, "203.0.113.10", 443, 6);
        clock.Advance(TimeSpan.FromSeconds(2));
        var second = queue.Enqueue(identity, "203.0.113.11", 8443, 6);
        var pending = queue.GetPending();

        AssertEx.Equal(PromptEnqueueStatus.Added, first.Status);
        AssertEx.Equal(PromptEnqueueStatus.Coalesced, second.Status);
        AssertEx.Equal(first.Token, second.Token);
        AssertEx.Equal(1, pending.Count);
        AssertEx.Equal(2, pending[0].OccurrenceCount);
        AssertEx.Equal("203.0.113.11", pending[0].RemoteAddress);
        AssertEx.Equal(8443, pending[0].RemotePort);
    }

    private static void QueueIsBounded()
    {
        var queue = new PromptQueue(new FakeClock());
        for (int i = 0; i < 32; i++)
        {
            var result = queue.Enqueue(
                PromptIdentity.ForExecutable($@"C:\apps\sample-{i}.exe"),
                "203.0.113.10",
                443,
                6);
            AssertEx.Equal(PromptEnqueueStatus.Added, result.Status);
        }

        var overflow = queue.Enqueue(
            PromptIdentity.ForExecutable(@"C:\apps\overflow.exe"),
            "203.0.113.10",
            443,
            6);

        AssertEx.Equal(PromptEnqueueStatus.CapacityReached, overflow.Status);
        AssertEx.Equal(32, queue.GetPending().Count);
    }

    private static void QueueExpiresTokens()
    {
        var clock = new FakeClock();
        var queue = new PromptQueue(clock);
        var added = queue.Enqueue(
            PromptIdentity.ForExecutable(@"C:\apps\sample.exe"),
            "203.0.113.10",
            443,
            6);

        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromTicks(1));
        var action = queue.Allow(added.Token);

        AssertEx.Equal(PromptActionStatus.Expired, action.Status);
        AssertEx.Equal(0, queue.GetPending().Count);
    }

    private static void IgnoreCreatesCooldown()
    {
        var clock = new FakeClock();
        var queue = new PromptQueue(clock);
        var identity = PromptIdentity.ForExecutable(@"C:\apps\sample.exe");
        var added = queue.Enqueue(identity, "203.0.113.10", 443, 6);

        AssertEx.Equal(PromptActionStatus.Dismissed, queue.Dismiss(added.Token).Status);
        AssertEx.Equal(
            PromptEnqueueStatus.SuppressedByCooldown,
            queue.Enqueue(identity, "203.0.113.10", 443, 6).Status);

        clock.Advance(TimeSpan.FromMinutes(5));
        AssertEx.Equal(
            PromptEnqueueStatus.Added,
            queue.Enqueue(identity, "203.0.113.10", 443, 6).Status);
    }

    private static void AllowTokensAreSingleUse()
    {
        var queue = new PromptQueue(new FakeClock());
        var added = queue.Enqueue(
            PromptIdentity.ForExecutable(@"C:\apps\sample.exe"),
            "203.0.113.10",
            443,
            6);

        AssertEx.Equal(PromptActionStatus.Allowed, queue.Allow(added.Token).Status);
        AssertEx.Equal(PromptActionStatus.UnknownToken, queue.Allow(added.Token).Status);
    }

    private static void UnknownTokensFailClosed()
    {
        var queue = new PromptQueue(new FakeClock());
        AssertEx.Equal(PromptActionStatus.UnknownToken, queue.Allow(Guid.NewGuid()).Status);
        AssertEx.Equal(PromptActionStatus.UnknownToken, queue.Dismiss(Guid.NewGuid()).Status);
    }

    private static void AmbiguousTokensCannotBeAllowed()
    {
        var queue = new PromptQueue(new FakeClock());
        var added = queue.Enqueue(
            PromptIdentity.ForAmbiguousServices(
                @"C:\Windows\System32\svchost.exe",
                new[] { "Dnscache", "NlaSvc" }),
            "203.0.113.10",
            443,
            6);

        AssertEx.Equal(PromptActionStatus.NotAllowable, queue.Allow(added.Token).Status);
        AssertEx.Equal(1, queue.GetPending().Count);
    }

    private static void QueueDeliversOldestFirst()
    {
        var clock = new FakeClock();
        var queue = new PromptQueue(clock);
        var first = queue.Enqueue(
            PromptIdentity.ForExecutable(@"C:\apps\first.exe"),
            "203.0.113.10",
            443,
            6);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = queue.Enqueue(
            PromptIdentity.ForExecutable(@"C:\apps\second.exe"),
            "203.0.113.11",
            53,
            17);

        AssertEx.SequenceEqual(
            new[] { first.Token, second.Token },
            queue.GetPending().Select(prompt => prompt.Token));
    }

    private static void PromptableFiltersReplaceAtomically()
    {
        var filters = new PromptableFilterSet();
        AssertEx.False(filters.Contains(10));

        filters.Replace(new ulong[] { 10, 20, 20 });
        AssertEx.True(filters.Contains(10));
        AssertEx.True(filters.Contains(20));
        AssertEx.SequenceEqual(new ulong[] { 10, 20 }, filters.Snapshot());

        filters.Replace(new ulong[] { 30 });
        AssertEx.False(filters.Contains(10));
        AssertEx.True(filters.Contains(30));
        AssertEx.SequenceEqual(new ulong[] { 30 }, filters.Snapshot());
    }

    private static void PromptableFilterSnapshotsAreThreadSafe()
    {
        var filters = new PromptableFilterSet();
        var failures = new List<Exception>();
        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            try
            {
                for (int iteration = 0; iteration < 1_000; iteration++)
                {
                    ulong generation = (ulong)((worker * 1_000) + iteration);
                    filters.Replace(new[] { generation, generation + 10_000 });
                    IReadOnlyList<ulong> snapshot = filters.Snapshot();
                    AssertEx.Equal(2, snapshot.Count);
                    AssertEx.Equal<ulong>(10_000, snapshot[1] - snapshot[0]);
                }
            }
            catch (Exception exception)
            {
                lock (failures)
                    failures.Add(exception);
            }
        })).ToArray();

        Task.WaitAll(tasks);
        AssertEx.Equal(0, failures.Count, failures.FirstOrDefault()?.ToString());
    }

    private static void OnlyOutboundDefaultBlocksArePromptable()
    {
        const ulong defaultBlock = 3_000_000;
        AssertEx.True(PromptFilterClassifier.IsPromptable(true, defaultBlock, defaultBlock, true));
        AssertEx.False(PromptFilterClassifier.IsPromptable(false, defaultBlock, defaultBlock, true));
        AssertEx.False(PromptFilterClassifier.IsPromptable(true, 6_000_000, defaultBlock, true));
        AssertEx.False(PromptFilterClassifier.IsPromptable(true, defaultBlock, defaultBlock, false));
        AssertEx.True(PromptFilterClassifier.IsRequiredDefaultBlock(true, defaultBlock, defaultBlock));
        AssertEx.False(PromptFilterClassifier.IsRequiredDefaultBlock(false, defaultBlock, defaultBlock));
        AssertEx.False(PromptFilterClassifier.IsRequiredDefaultBlock(true, 6_000_000, defaultBlock));
    }

    private static void CriticalWfpFilterPairsRequireBothRegistrations()
    {
        var requested = new List<WfpFilterLifetime>();
        IReadOnlyList<ulong> ids = WfpFilterPairRegistration.Register(lifetime =>
        {
            requested.Add(lifetime);
            return lifetime == WfpFilterLifetime.Persistent ? 101UL : 202UL;
        }, required: true);

        AssertEx.SequenceEqual(
            new[] { WfpFilterLifetime.Persistent, WfpFilterLifetime.BootTime },
            requested);
        AssertEx.SequenceEqual(new ulong[] { 101, 202 }, ids);
    }

    private static void CriticalWfpFilterPairsPropagatePersistentFailure()
    {
        AssertEx.Throws<InvalidOperationException>(() =>
            WfpFilterPairRegistration.Register(
                _ => throw new InvalidOperationException("persistent failed"),
                required: true));
    }

    private static void CriticalWfpFilterPairsPropagateBootTimeFailure()
    {
        AssertEx.Throws<InvalidOperationException>(() =>
            WfpFilterPairRegistration.Register(lifetime =>
            {
                if (lifetime == WfpFilterLifetime.BootTime)
                    throw new InvalidOperationException("boot-time failed");
                return 101;
            }, required: true));
    }

    private static void OptionalWfpFilterFailuresPropagate()
    {
        AssertEx.Throws<InvalidOperationException>(() => WfpFilterPairRegistration.Register(
            _ => throw new InvalidOperationException("persistent failed"),
            required: false));
        AssertEx.Throws<InvalidOperationException>(() => WfpFilterPairRegistration.Register(lifetime =>
        {
            if (lifetime == WfpFilterLifetime.BootTime)
                throw new InvalidOperationException("boot-time failed");
            return 101;
        }, required: false));
    }

    private static void NetworkActivityStatusesStateObservedFacts()
    {
        AssertEx.Equal(
            NetworkActivityStatus.Allowed,
            NetworkActivityStatusClassifier.FromFirewallDecision(allowed: true));
        AssertEx.Equal(
            NetworkActivityStatus.Blocked,
            NetworkActivityStatusClassifier.FromFirewallDecision(allowed: false));
        AssertEx.Equal(
            "Listening (local endpoint)",
            NetworkActivityStatusClassifier.ToDisplayText(NetworkActivityStatus.Listening));
        AssertEx.True(NetworkActivityStatusClassifier.IsDecisionVisible(
            NetworkActivityStatus.Allowed,
            showAllowed: true,
            showBlocked: false));
        AssertEx.False(NetworkActivityStatusClassifier.IsDecisionVisible(
            NetworkActivityStatus.Allowed,
            showAllowed: false,
            showBlocked: true));
        AssertEx.True(NetworkActivityStatusClassifier.IsDecisionVisible(
            NetworkActivityStatus.Blocked,
            showAllowed: false,
            showBlocked: true));
        AssertEx.Throws<ArgumentException>(() =>
            NetworkActivityStatusClassifier.IsDecisionVisible(
                NetworkActivityStatus.Listening,
                showAllowed: true,
                showBlocked: true));
        AssertEx.True(NetworkActivityStatusClassifier.ShowAllowedByDefault);
        AssertEx.True(NetworkActivityStatusClassifier.ShowBlockedByDefault);
        AssertEx.True(NetworkActivityStatusClassifier.ShowListeningByDefault);

        int refreshCount = 0;
        AssertEx.True(NetworkActivityRefresh.TryRun(() => refreshCount++));
        AssertEx.Equal(1, refreshCount);
        AssertEx.False(NetworkActivityRefresh.TryRun(
            () => throw new InvalidOperationException("transient refresh failure")));
    }

    private static void Event5157ParserUsesNamedFields()
    {
        var timestamp = new DateTimeOffset(2026, 7, 14, 12, 30, 0, TimeSpan.Zero);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProcessID"] = "4242",
            ["Application"] = @"\device\harddiskvolume3\apps\sample.exe",
            ["Direction"] = "%%14593",
            ["SourceAddress"] = "192.0.2.10",
            ["SourcePort"] = "52144",
            ["DestAddress"] = "203.0.113.20",
            ["DestPort"] = "443",
            ["Protocol"] = "6",
            ["FilterRTID"] = "5000000000",
            ["LayerRTID"] = "48",
            ["FutureWindowsField"] = "ignored",
        };

        AssertEx.True(SecurityEvent5157Parser.TryParse(fields, timestamp, out var parsed));
        AssertEx.Equal<uint>(4242, parsed.ProcessId);
        AssertEx.Equal(@"\device\harddiskvolume3\apps\sample.exe", parsed.ApplicationPath);
        AssertEx.Equal(ConnectionDirection.Outbound, parsed.Direction);
        AssertEx.Equal("192.0.2.10", parsed.LocalAddress);
        AssertEx.Equal(52144, parsed.LocalPort);
        AssertEx.Equal("203.0.113.20", parsed.RemoteAddress);
        AssertEx.Equal(443, parsed.RemotePort);
        AssertEx.Equal<byte>(6, parsed.Protocol);
        AssertEx.Equal<ulong>(5_000_000_000, parsed.FilterRuntimeId);
        AssertEx.Equal(timestamp, parsed.TimestampUtc);
    }

    private static void AuditLeaseRestoresExactFlags()
    {
        var category = Guid.NewGuid();
        var backend = new FakeAuditPolicyBackend(AuditPolicyFlags.Success);

        var lease = AuditPolicyLease.Acquire(backend, category, AuditPolicyFlags.Failure);
        AssertEx.Equal(AuditPolicyFlags.Success | AuditPolicyFlags.Failure, backend.Current);

        lease.Dispose();
        lease.Dispose();

        AssertEx.Equal(AuditPolicyFlags.Success, backend.Current);
        AssertEx.SequenceEqual(
            new[]
            {
                AuditPolicyFlags.Success | AuditPolicyFlags.Failure,
                AuditPolicyFlags.Success,
            },
            backend.SetHistory);
    }

    private static void NonPackageSidSentinelsAreRejected()
    {
        var timestamp = new DateTimeOffset(2026, 7, 14, 12, 30, 0, TimeSpan.Zero);
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
            ["PackageId"] = "S-1-0-0",
        };

        AssertEx.True(SecurityEvent5157Parser.TryParse(fields, timestamp, out var parsed));
        AssertEx.Equal<string?>(null, parsed.PackageSid);
    }

    private static void DropCorrelationRequiresExactFields()
    {
        var candidate = CreateDropCandidate();
        var audit = CreateAuditEvent(candidate.TimestampUtc);

        AssertEx.True(DropCorrelator.IsMatch(candidate, audit));
        AssertEx.False(DropCorrelator.IsMatch(candidate, CreateAuditEvent(
            candidate.TimestampUtc,
            filterRuntimeId: candidate.FilterRuntimeId + 1)));
        AssertEx.False(DropCorrelator.IsMatch(candidate, CreateAuditEvent(
            candidate.TimestampUtc,
            applicationPath: @"C:\apps\other.exe")));
        AssertEx.False(DropCorrelator.IsMatch(candidate, CreateAuditEvent(
            candidate.TimestampUtc,
            remotePort: 8443)));
        AssertEx.False(DropCorrelator.IsMatch(candidate, CreateAuditEvent(
            candidate.TimestampUtc,
            protocol: 17)));
    }

    private static void DropCorrelationAllowsOneSecondSkew()
    {
        var candidate = CreateDropCandidate();
        AssertEx.True(DropCorrelator.IsMatch(
            candidate,
            CreateAuditEvent(candidate.TimestampUtc.AddSeconds(1))));
        AssertEx.False(DropCorrelator.IsMatch(
            candidate,
            CreateAuditEvent(candidate.TimestampUtc.AddSeconds(1).AddTicks(1))));
    }

    private static void ServiceAttributionDistinguishesSubjects()
    {
        var candidate = CreateDropCandidate();
        var audit = CreateAuditEvent(candidate.TimestampUtc);

        var executable = ServiceAttribution.Resolve(candidate, audit, Array.Empty<string>());
        var service = ServiceAttribution.Resolve(candidate, audit, new[] { "Dnscache" });
        var ambiguous = ServiceAttribution.Resolve(candidate, audit, new[] { "NlaSvc", "Dnscache" });

        AssertEx.Equal(PromptIdentityKind.Executable, executable.Kind);
        AssertEx.Equal(PromptIdentityKind.Service, service.Kind);
        AssertEx.Equal("Dnscache", service.ServiceName);
        AssertEx.Equal(PromptIdentityKind.AmbiguousService, ambiguous.Kind);
        AssertEx.SequenceEqual(new[] { "Dnscache", "NlaSvc" }, ambiguous.AmbiguousServiceNames);
    }

    private static void PackageAttributionTakesPrecedence()
    {
        var candidate = CreateDropCandidate(packageSid: "S-1-15-2-1234");
        var identity = ServiceAttribution.Resolve(
            candidate,
            CreateAuditEvent(candidate.TimestampUtc),
            new[] { "UnexpectedService" });

        AssertEx.Equal(PromptIdentityKind.Package, identity.Kind);
        AssertEx.Equal("S-1-15-2-1234", identity.PackageSid);
    }

    private static void UnattributedServiceHostFailsClosed()
    {
        var candidate = CreateDropCandidate(
            applicationPath: @"C:\Windows\System32\svchost.exe");
        var identity = ServiceAttribution.Resolve(candidate, null, Array.Empty<string>());

        AssertEx.Equal(PromptIdentityKind.AmbiguousService, identity.Kind);
        AssertEx.False(PromptAllowPolicy.TryCreate(identity, out _));
    }

    private static void UnattributedRegisteredServiceExecutableFailsClosed()
    {
        var candidate = CreateDropCandidate(
            applicationPath: @"C:\Program Files\Example Service\service.exe");
        var identity = ServiceAttribution.Resolve(
            candidate,
            null,
            Array.Empty<string>(),
            executableIsRegisteredService: true);

        AssertEx.Equal(PromptIdentityKind.AmbiguousService, identity.Kind);
        AssertEx.False(PromptAllowPolicy.TryCreate(identity, out _));
    }

    private static void ServiceImagePathsAreParsedConservatively()
    {
        AssertEx.Equal(
            @"C:\Program Files\Example Service\service.exe",
            ServiceImagePath.TryExtractExecutable(
                "\"C:\\Program Files\\Example Service\\service.exe\" --service",
                @"C:\Windows"));
        AssertEx.Equal(
            @"C:\Program Files\Example Service\service.exe",
            ServiceImagePath.TryExtractExecutable(
                @"C:\Program Files\Example Service\service.exe --service",
                @"C:\Windows"));
        AssertEx.Equal(
            @"C:\Windows\System32\svchost.exe",
            ServiceImagePath.TryExtractExecutable(
                @"\SystemRoot\System32\svchost.exe -k netsvcs",
                @"C:\Windows"));
        AssertEx.Equal<string?>(
            null,
            ServiceImagePath.TryExtractExecutable(
                @"C:\Windows\System32\driver.sys",
                @"C:\Windows"));
    }

    private static void PipeAuthorizationRequiresExactExecutablePath()
    {
        AssertEx.True(PipeClientAuthorization.IsExpectedExecutable(
            @"C:\Program Files\SecureWall\SecureWall.exe",
            @"c:\program files\securewall\SECUREWALL.EXE"));
        AssertEx.False(PipeClientAuthorization.IsExpectedExecutable(
            @"C:\Users\Guest\SecureWall.exe",
            @"C:\Program Files\SecureWall\SecureWall.exe"));
        AssertEx.False(PipeClientAuthorization.IsExpectedExecutable(
            string.Empty,
            @"C:\Program Files\SecureWall\SecureWall.exe"));
    }

    private static void InstallationConflictGuardDetectsTinyWall()
    {
        AssertEx.True(InstallationConflictGuard.HasTinyWallService(
            new[] { "EventLog", "TinyWall", "SecureWall" }));
        AssertEx.True(InstallationConflictGuard.HasTinyWallService(
            new[] { "tinywall" }));
        AssertEx.False(InstallationConflictGuard.HasTinyWallService(
            new[] { "EventLog", "SecureWall" }));
    }

    private static void CandidateBufferEnrichesBeforeDeadline()
    {
        var clock = new FakeClock();
        var buffer = new DropCandidateBuffer(clock);
        var candidate = CreateDropCandidate();
        AssertEx.True(buffer.TryAdd(candidate));

        AssertEx.True(buffer.TryMatch(CreateAuditEvent(candidate.TimestampUtc), out var matched));
        AssertEx.Equal(candidate, matched);
        AssertEx.Equal(0, buffer.DrainReady().Count);
    }

    private static void CandidateBufferReleasesAfterDeadline()
    {
        var clock = new FakeClock();
        var buffer = new DropCandidateBuffer(clock);
        var candidate = CreateDropCandidate();
        AssertEx.True(buffer.TryAdd(candidate));

        clock.Advance(TimeSpan.FromMilliseconds(1_199));
        AssertEx.Equal(0, buffer.DrainReady().Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        AssertEx.SequenceEqual(new[] { candidate }, buffer.DrainReady());
    }

    private static void CandidateBufferIsBounded()
    {
        var clock = new FakeClock();
        var buffer = new DropCandidateBuffer(clock, capacity: 64, enrichmentDelay: TimeSpan.FromSeconds(1));
        for (int i = 0; i < 64; i++)
        {
            AssertEx.True(buffer.TryAdd(new DropCandidate(
                clock.UtcNow,
                (ulong)i,
                $@"C:\apps\sample-{i}.exe",
                null,
                "192.0.2.10",
                52144,
                "203.0.113.20",
                443,
                6)));
        }

        AssertEx.False(buffer.TryAdd(CreateDropCandidate()));
    }

    private static void FailedAllowLeavesTokenPending()
    {
        var queue = new PromptQueue(new FakeClock());
        var added = queue.Enqueue(
            PromptIdentity.ForExecutable(@"C:\apps\sample.exe"),
            "203.0.113.10",
            443,
            6);

        var failed = queue.Allow(added.Token, _ => false);
        AssertEx.Equal(PromptActionStatus.ApplyFailed, failed.Status);
        AssertEx.Equal(1, queue.GetPending().Count);

        AssertEx.Equal(
            PromptActionStatus.Allowed,
            queue.Allow(added.Token, _ => true).Status);
        AssertEx.Equal(0, queue.GetPending().Count);
    }

    private static void PromptWireDtoRoundTrips()
    {
        var clock = new FakeClock();
        var queue = new PromptQueue(clock);
        queue.Enqueue(
            PromptIdentity.ForService(@"C:\Windows\System32\svchost.exe", "Dnscache"),
            "203.0.113.20",
            53,
            17);
        var dto = PromptWireDto.FromPrompt(queue.GetPending()[0]);

        string json = JsonSerializer.Serialize(dto);
        var copy = JsonSerializer.Deserialize<PromptWireDto>(json);

        AssertEx.True(copy != null);
        AssertEx.Equal(dto.Token, copy!.Token);
        AssertEx.Equal(dto.SubjectKind, copy.SubjectKind);
        AssertEx.Equal(dto.ExecutablePath, copy.ExecutablePath);
        AssertEx.Equal("Dnscache", copy.ServiceName);
        AssertEx.True(copy.CanAllow);
        AssertEx.Equal("203.0.113.20", copy.RemoteAddress);
        AssertEx.Equal(53, copy.RemotePort);
        AssertEx.Equal<byte>(17, copy.Protocol);
    }

    private static void DisplayCoordinatorShowsOneAtATime()
    {
        var actions = new FakePromptActions();
        var firstView = new FakePromptView();
        var secondView = new FakePromptView();
        var views = new Queue<FakePromptView>(new[] { firstView, secondView });
        using var coordinator = new PromptDisplayCoordinator(actions, () => views.Dequeue());
        PromptWireDto first = CreatePromptDto("first.exe");
        PromptWireDto second = CreatePromptDto("second.exe");

        coordinator.Enqueue(new[] { first, second, first });
        AssertEx.Equal(first.Token, coordinator.CurrentToken);
        AssertEx.Equal(1, coordinator.PendingCount);

        firstView.RaiseIgnore();

        AssertEx.SequenceEqual(new[] { first.Token }, actions.Dismissed);
        AssertEx.Equal(second.Token, coordinator.CurrentToken);
        AssertEx.Equal(0, coordinator.PendingCount);
    }

    private static void DisplayCoordinatorAdvancesAfterAllow()
    {
        var actions = new FakePromptActions();
        var created = new List<FakePromptView>();
        using var coordinator = new PromptDisplayCoordinator(actions, () =>
        {
            var view = new FakePromptView();
            created.Add(view);
            return view;
        });
        PromptWireDto first = CreatePromptDto("first.exe");
        PromptWireDto second = CreatePromptDto("second.exe");
        coordinator.Enqueue(new[] { first, second });

        created[0].RaiseAllow();

        AssertEx.SequenceEqual(new[] { first.Token }, actions.Allowed);
        AssertEx.Equal(second.Token, coordinator.CurrentToken);
        AssertEx.True(created[0].WasClosed);
    }

    private static void DisplayCoordinatorRetainsAfterAllowFailure()
    {
        var actions = new FakePromptActions { AllowResult = PromptActionStatus.ApplyFailed };
        var view = new FakePromptView();
        using var coordinator = new PromptDisplayCoordinator(actions, () => view);
        PromptWireDto prompt = CreatePromptDto("sample.exe");
        coordinator.Enqueue(new[] { prompt });

        view.RaiseAllow();

        AssertEx.Equal(prompt.Token, coordinator.CurrentToken);
        AssertEx.Equal(PromptActionStatus.ApplyFailed, view.LastFailure);
        AssertEx.False(view.WasClosed);
    }

    private static void CloseAndTimeoutBehaveAsIgnore()
    {
        var actions = new FakePromptActions();
        var views = new List<FakePromptView>();
        using var coordinator = new PromptDisplayCoordinator(actions, () =>
        {
            var view = new FakePromptView();
            views.Add(view);
            return view;
        });
        PromptWireDto first = CreatePromptDto("first.exe");
        PromptWireDto second = CreatePromptDto("second.exe");
        coordinator.Enqueue(new[] { first, second });

        views[0].RaiseClosed();
        views[1].RaiseTimeout();

        AssertEx.SequenceEqual(new[] { first.Token, second.Token }, actions.Dismissed);
        AssertEx.Equal<Guid?>(null, coordinator.CurrentToken);
    }

    private static void DisplayCoordinatorRetainsAfterDismissFailure()
    {
        var actions = new FakePromptActions { DismissResult = PromptActionStatus.ApplyFailed };
        var view = new FakePromptView();
        using var coordinator = new PromptDisplayCoordinator(actions, () => view);
        PromptWireDto prompt = CreatePromptDto("sample.exe");
        coordinator.Enqueue(new[] { prompt });

        view.RaiseIgnore();

        AssertEx.Equal(prompt.Token, coordinator.CurrentToken);
        AssertEx.Equal(PromptActionStatus.ApplyFailed, view.LastFailure);
        AssertEx.False(view.WasClosed);
    }

    private static void DisplayCoordinatorDisposalIsPassive()
    {
        var actions = new FakePromptActions();
        var view = new FakePromptView();
        var coordinator = new PromptDisplayCoordinator(actions, () => view);
        coordinator.Enqueue(new[] { CreatePromptDto("sample.exe") });

        coordinator.Dispose();

        AssertEx.Equal(0, actions.Allowed.Count);
        AssertEx.Equal(0, actions.Dismissed.Count);
        AssertEx.True(view.WasClosed);
    }

    private static PromptWireDto CreatePromptDto(string executableName)
    {
        return new PromptWireDto
        {
            Token = Guid.NewGuid(),
            SubjectKind = PromptIdentityKind.Executable,
            CanAllow = true,
            ExecutablePath = $@"C:\apps\{executableName}",
            FirstSeenUtc = new DateTimeOffset(2026, 7, 14, 14, 0, 0, TimeSpan.Zero),
            LastSeenUtc = new DateTimeOffset(2026, 7, 14, 14, 0, 0, TimeSpan.Zero),
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(2),
            RemoteAddress = "203.0.113.20",
            RemotePort = 443,
            Protocol = 6,
            OccurrenceCount = 1,
        };
    }

    private static DropCandidate CreateDropCandidate(
        string applicationPath = @"C:\apps\sample.exe",
        string? packageSid = null)
    {
        return new DropCandidate(
            new DateTimeOffset(2026, 7, 14, 13, 0, 0, TimeSpan.Zero),
            5_000_000_000,
            applicationPath,
            packageSid,
            "192.0.2.10",
            52144,
            "203.0.113.20",
            443,
            6);
    }

    private static BlockedConnectionAuditEvent CreateAuditEvent(
        DateTimeOffset timestamp,
        ulong filterRuntimeId = 5_000_000_000,
        string applicationPath = @"C:\apps\sample.exe",
        int remotePort = 443,
        byte protocol = 6)
    {
        return new BlockedConnectionAuditEvent(
            timestamp,
            4242,
            applicationPath,
            ConnectionDirection.Outbound,
            "192.0.2.10",
            52144,
            "203.0.113.20",
            remotePort,
            protocol,
            filterRuntimeId,
            null);
    }

    private sealed class FakeClock : IClock
    {
        internal FakeClock()
        {
            UtcNow = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        }

        public DateTimeOffset UtcNow { get; private set; }

        internal void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class FakePromptActions : IPromptActionClient
    {
        internal PromptActionStatus AllowResult { get; set; } = PromptActionStatus.Allowed;
        internal PromptActionStatus DismissResult { get; set; } = PromptActionStatus.Dismissed;
        internal List<Guid> Allowed { get; } = new();
        internal List<Guid> Dismissed { get; } = new();

        public PromptActionStatus Allow(Guid token)
        {
            Allowed.Add(token);
            return AllowResult;
        }

        public PromptActionStatus Dismiss(Guid token)
        {
            Dismissed.Add(token);
            return DismissResult;
        }
    }

    private sealed class FakePromptView : IPromptView
    {
        public event EventHandler? AllowRequested;
        public event EventHandler? IgnoreRequested;
        public event EventHandler? PromptClosed;
        public event EventHandler? PromptTimedOut;

        internal PromptWireDto? Prompt { get; private set; }
        internal PromptActionStatus? LastFailure { get; private set; }
        internal bool WasClosed { get; private set; }

        public void ShowPrompt(PromptWireDto prompt) => Prompt = prompt;
        public void ShowActionFailure(PromptActionStatus status) => LastFailure = status;
        public void ClosePrompt() => WasClosed = true;
        public void Dispose() => WasClosed = true;

        internal void RaiseAllow() => AllowRequested?.Invoke(this, EventArgs.Empty);
        internal void RaiseIgnore() => IgnoreRequested?.Invoke(this, EventArgs.Empty);
        internal void RaiseClosed() => PromptClosed?.Invoke(this, EventArgs.Empty);
        internal void RaiseTimeout() => PromptTimedOut?.Invoke(this, EventArgs.Empty);
    }

    private static void AiExplainSubjectUsesFileNameNotFullPath()
    {
        var prompt = new PromptWireDto
        {
            Token = Guid.NewGuid(),
            SubjectKind = PromptIdentityKind.Executable,
            ExecutablePath = @"C:\Users\alice\AppData\Local\Vendor\updater.exe",
            RemoteAddress = "203.0.113.7",
            RemotePort = 443,
            Protocol = 6,
        };

        var subject = AiExplainSubject.FromPrompt(prompt, "Vendor Inc.", includeRemoteEndpoint: false);

        AssertEx.Equal("updater.exe", subject.ExecutableName);
        AssertEx.Equal("Vendor Inc.", subject.Publisher);
        AssertEx.Equal<string?>(null, subject.RemoteEndpoint);
        AssertEx.False(subject.ExecutableName.Contains("alice"), "File name must not leak the user profile path.");
    }

    private static void AiExplainSubjectIncludesRemoteEndpointOnlyWhenOptedIn()
    {
        var prompt = new PromptWireDto
        {
            Token = Guid.NewGuid(),
            SubjectKind = PromptIdentityKind.Executable,
            ExecutablePath = @"C:\app\thing.exe",
            RemoteAddress = "198.51.100.9",
            RemotePort = 8080,
            Protocol = 6,
        };

        var excluded = AiExplainSubject.FromPrompt(prompt, null, includeRemoteEndpoint: false);
        var included = AiExplainSubject.FromPrompt(prompt, null, includeRemoteEndpoint: true);

        AssertEx.Equal<string?>(null, excluded.RemoteEndpoint);
        AssertEx.Equal("TCP 198.51.100.9:8080", included.RemoteEndpoint);
        AssertEx.Equal<string?>(null, included.Publisher);
    }

    private static void AiExplainComposerOmitsVerdictAndFullPath()
    {
        var subject = new AiExplainSubject(
            PromptIdentityKind.Executable,
            "svchost.exe",
            publisher: null,
            serviceName: null,
            packageSid: null,
            remoteEndpoint: null);

        AiExplainPrompt composed = AiExplainComposer.Compose(subject);

        AssertEx.True(composed.SystemPrompt.Contains("Do NOT tell the user whether to allow"),
            "System prompt must forbid an allow/block verdict.");
        AssertEx.True(composed.UserPrompt.Contains("svchost.exe"), "User prompt must include the file name.");
        AssertEx.True(composed.UserPrompt.Contains("none (unsigned or unverified)"),
            "Missing publisher must be stated, not fabricated.");
        AssertEx.False(composed.UserPrompt.Contains(@"C:\"), "User prompt must not contain a full path.");
    }

    private static void AiExplainResponseParserExtractsContent()
    {
        const string body =
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"  This is a software updater.  \"}}]}";

        AiExplainResult result = AiExplainResponseParser.Parse(200, body);

        AssertEx.True(result.Success, "Valid response must succeed.");
        AssertEx.Equal("This is a software updater.", result.Text);
    }

    private static void AiExplainResponseParserMapsErrors()
    {
        AiExplainResult unauthorized = AiExplainResponseParser.Parse(
            401, "{\"error\":{\"message\":\"Incorrect API key provided.\"}}");
        AssertEx.False(unauthorized.Success);
        AssertEx.True(unauthorized.Error!.Contains("401"), "401 must be surfaced.");
        AssertEx.True(unauthorized.Error!.Contains("Incorrect API key provided."),
            "API error message must be surfaced.");

        AiExplainResult malformed = AiExplainResponseParser.Parse(200, "not json");
        AssertEx.False(malformed.Success, "Malformed body must fail closed, not throw.");

        AiExplainResult empty = AiExplainResponseParser.Parse(200, "{\"choices\":[]}");
        AssertEx.False(empty.Success, "No choices must yield a friendly failure.");
    }

    private static void AiExplainSettingsValidateUrlAndModel()
    {
        AssertEx.True(AiExplainSettings.Validate("https://api.openai.com/v1", "gpt-4o-mini", out _),
            "A valid https URL and model must pass.");

        AssertEx.False(AiExplainSettings.Validate("not a url", "gpt-4o-mini", out string? urlError));
        AssertEx.True(urlError != null && urlError.Length > 0);

        AssertEx.False(AiExplainSettings.Validate("https://api.openai.com/v1", "  ", out _),
            "An empty model must fail.");

        AssertEx.False(AiExplainSettings.Validate("ftp://example.com", "gpt-4o-mini", out _),
            "Non-http(s) schemes must fail.");

        AssertEx.True(AiExplainSettings.TryBuildChatCompletionsUri("https://host/v1/", out Uri? uri));
        AssertEx.Equal("https://host/v1/chat/completions", uri!.ToString());
    }

    private sealed class FakeAuditPolicyBackend : IAuditPolicyBackend
    {
        internal FakeAuditPolicyBackend(AuditPolicyFlags initial) => Current = initial;

        internal AuditPolicyFlags Current { get; private set; }
        internal List<AuditPolicyFlags> SetHistory { get; } = new();

        public AuditPolicyFlags Query(Guid subcategory) => Current;

        public void Set(Guid subcategory, AuditPolicyFlags flags)
        {
            Current = flags;
            SetHistory.Add(flags);
        }
    }
}

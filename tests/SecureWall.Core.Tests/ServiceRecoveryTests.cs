using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

// SW-01: an environmental reload failure must not leave the service stopped behind the
// deny baseline with a clean exit code.
internal static class ServiceRecoveryTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("reload retention requires an intact session no commit and no superseded grant", RetentionMatrix),
        ("retained reload failure keeps policy and schedules a retry", RetainedFailure),
        ("retention bookkeeping or decision failure still withdraws grants", RetentionFaults),
        ("repeated retained failures escalate to revocation", RetentionEscalates),
        ("recovery backoff grows caps and resets", BackoffTiming),
        ("revoked session rebuilds first then backs off until exhausted", RebuildSchedule),
        ("reload retry waits for its due time and yields to a rebuild", RetrySchedule),
        ("worker failure reports a nonzero service-specific exit code", ExitCodes),
        ("SCM restart plan repeats backed-off restarts with a finite reset period", RestartPlan),
        ("service rebuilds revoked sessions and reports failed exits", ServiceWiring),
        ("unverified addresses survive unrelated commits and force revocation", UnverifiedAddresses),
        ("address changes and display-off restriction supersede committed grants", SupersededInputs),
        ("recovery waits are clamped against inconsistent clock readings", ClampedWait),
        ("failed address condition build is rebuilt in full on the next enumeration", AddressConditionRebuild),
        ("address refresh maps local subnet gateway and dns sets to their own condition lists", AddressConditionIndexMapping),
    };

    private static void RetentionMatrix()
    {
        foreach (bool session in new[] { false, true })
        foreach (bool committed in new[] { false, true })
        foreach (bool superseded in new[] { false, true })
        for (int prior = -1; prior <= EnvironmentalPolicyReload.MaxRetainedFailures + 1; ++prior)
        {
            bool expected = session && !committed && !superseded && prior >= 0 && prior < EnvironmentalPolicyReload.MaxRetainedFailures;
            AssertEx.Equal(expected, EnvironmentalPolicyReload.MayRetain(session, committed, superseded, prior),
                $"session={session} committed={committed} superseded={superseded} prior={prior}");
        }
    }

    private static void RetainedFailure()
    {
        var failure = new InvalidOperationException("transaction aborted");
        Exception? seen = null;
        bool revoked = false;
        bool completed = EnvironmentalPolicyReload.Run(() => throw failure, () => true, error => seen = error, () => revoked = true);
        AssertEx.True(!completed && ReferenceEquals(seen, failure) && !revoked, "Retained failure withdrew grants or lost its cause.");

        bool installed = false;
        AssertEx.True(EnvironmentalPolicyReload.Run(() => installed = true,
            () => throw new InvalidOperationException("Decision on success"),
            _ => throw new InvalidOperationException("Retention on success"),
            () => throw new InvalidOperationException("Revocation on success")) && installed);
    }

    private static void RetentionFaults()
    {
        foreach (string fault in new[] { "decision", "bookkeeping", "refused" })
        {
            var failure = new InvalidOperationException("registration");
            bool revoked = false;
            Exception? observed = null;
            try
            {
                EnvironmentalPolicyReload.Run(() => throw failure,
                    () => fault == "decision" ? throw new IOException("decision") : fault != "refused",
                    _ => throw new IOException("bookkeeping"),
                    () => revoked = true);
            }
            catch (Exception error) { observed = error; }
            AssertEx.True(revoked && ReferenceEquals(observed, failure), fault + " failure kept stale grants or replaced the cause.");
        }
    }

    private static void RetentionEscalates()
    {
        var backoff = new RecoveryBackoff();
        var now = TimeSpan.Zero;
        int retained = 0, revoked = 0;
        for (int attempt = 0; attempt <= EnvironmentalPolicyReload.MaxRetainedFailures; ++attempt)
        {
            try
            {
                if (EnvironmentalPolicyReload.Run(() => throw new InvalidOperationException("abort"),
                    () => EnvironmentalPolicyReload.MayRetain(true, false, false, backoff.Failures),
                    _ => { backoff.RecordFailure(now); ++retained; }, () => ++revoked))
                    throw new InvalidOperationException("Failure reported success.");
            }
            catch (InvalidOperationException error) when (error.Message == "abort") { }
        }
        AssertEx.Equal(EnvironmentalPolicyReload.MaxRetainedFailures, retained);
        AssertEx.Equal(1, revoked, "Bounded retention did not escalate exactly once.");
    }

    private static void BackoffTiming()
    {
        var backoff = new RecoveryBackoff();
        var now = TimeSpan.Zero;
        AssertEx.False(backoff.IsDue(now));
        AssertEx.Equal(Timeout.InfiniteTimeSpan, backoff.Wait(now));
        var observed = new List<TimeSpan>();
        for (int i = 0; i < RecoveryBackoff.Delays.Length + 2; ++i)
        {
            backoff.RecordFailure(now);
            observed.Add(backoff.Wait(now));
            AssertEx.False(backoff.IsDue(now + observed[^1] - TimeSpan.FromMilliseconds(1)), "Retry ran before its delay.");
            AssertEx.True(backoff.IsDue(now + observed[^1]));
        }
        AssertEx.SequenceEqual(new[] { 5, 15, 30, 60, 60, 60 }, observed.Select(delay => (int)delay.TotalSeconds));
        AssertEx.Equal(TimeSpan.Zero, backoff.Wait(now + TimeSpan.FromHours(1)));
        backoff.Reset();
        AssertEx.True(backoff.Failures == 0 && backoff.Due == null && !backoff.IsDue(now + TimeSpan.FromHours(1)));
    }

    private static void RebuildSchedule()
    {
        var rebuild = new RecoveryBackoff();
        var reload = new RecoveryBackoff();
        var now = TimeSpan.Zero;
        AssertEx.Equal(RuntimeRecoveryStep.RebuildSession, RuntimeRecovery.Due(true, rebuild, reload, now), "First rebuild was delayed.");
        AssertEx.Equal(TimeSpan.Zero, RuntimeRecovery.Wait(true, rebuild, reload, now));
        for (int attempt = 1; attempt <= RuntimeRecovery.MaxRebuildAttempts; ++attempt)
        {
            AssertEx.False(RuntimeRecovery.Exhausted(rebuild), "Rebuild gave up early.");
            rebuild.RecordFailure(now);
            TimeSpan wait = RuntimeRecovery.Wait(true, rebuild, reload, now);
            AssertEx.True(wait > TimeSpan.Zero, "Failed rebuild retried without backoff.");
            AssertEx.Equal(RuntimeRecoveryStep.None, RuntimeRecovery.Due(true, rebuild, reload, now));
            now += wait;
            AssertEx.Equal(RuntimeRecoveryStep.RebuildSession, RuntimeRecovery.Due(true, rebuild, reload, now));
        }
        AssertEx.True(RuntimeRecovery.Exhausted(rebuild), "Rebuild retries are unbounded.");
    }

    private static void RetrySchedule()
    {
        var rebuild = new RecoveryBackoff();
        var reload = new RecoveryBackoff();
        var now = TimeSpan.Zero;
        AssertEx.Equal(RuntimeRecoveryStep.None, RuntimeRecovery.Due(false, rebuild, reload, now));
        AssertEx.Equal(Timeout.InfiniteTimeSpan, RuntimeRecovery.Wait(false, rebuild, reload, now), "Idle loop polls.");
        reload.RecordFailure(now);
        AssertEx.Equal(RuntimeRecoveryStep.None, RuntimeRecovery.Due(false, rebuild, reload, now));
        AssertEx.Equal(TimeSpan.FromSeconds(5), RuntimeRecovery.Wait(false, rebuild, reload, now));
        AssertEx.Equal(RuntimeRecoveryStep.RetryReload, RuntimeRecovery.Due(false, rebuild, reload, now + TimeSpan.FromSeconds(5)));
        AssertEx.Equal(RuntimeRecoveryStep.RebuildSession, RuntimeRecovery.Due(true, rebuild, reload, now + TimeSpan.FromSeconds(5)),
            "A due reload retry ran on a revoked session.");
    }

    private static void ExitCodes()
    {
        AssertEx.Equal((0, 0), ServiceLifecyclePolicy.StoppedStatus(false, false));
        AssertEx.Equal((0, 0), ServiceLifecyclePolicy.StoppedStatus(false, true));
        AssertEx.Equal((1066, ServiceLifecyclePolicy.StartupFailedExitCode), ServiceLifecyclePolicy.StoppedStatus(true, false));
        AssertEx.Equal((1066, ServiceLifecyclePolicy.RuntimeRecoveryFailedExitCode), ServiceLifecyclePolicy.StoppedStatus(true, true));
        AssertEx.True(ServiceLifecyclePolicy.StartupFailedExitCode != 0 && ServiceLifecyclePolicy.RuntimeRecoveryFailedExitCode != 0 &&
            ServiceLifecyclePolicy.StartupFailedExitCode != ServiceLifecyclePolicy.RuntimeRecoveryFailedExitCode);
    }

    private static void RestartPlan()
    {
        TimeSpan[] delays = ServiceLifecyclePolicy.RestartDelays;
        AssertEx.True(delays.Length >= 3, "One restart then no action leaves a persistent failure offline.");
        AssertEx.True(delays[0] > TimeSpan.Zero && delays.Zip(delays.Skip(1), (a, b) => a < b).All(increasing => increasing),
            "Restart delays must back off.");
        TimeSpan reset = ServiceLifecyclePolicy.RestartResetPeriod;
        AssertEx.True(reset >= TimeSpan.FromMinutes(10) && reset.TotalSeconds < uint.MaxValue,
            "Reset period must be explicit, finite and longer than the restart sequence.");
    }

    private static void UnverifiedAddresses()
    {
        var addresses = new AddressVerification();
        var retry = new RecoveryBackoff();
        retry.RecordFailure(TimeSpan.Zero); // e.g. a retained 30-minute reload is pending
        var failure = new InvalidOperationException("Active network adapter enumeration failed.");
        bool revoked = false;
        Exception? observed = null;
        try
        {
            EnvironmentalPolicyReload.Run(() => { if (addresses.Refresh(() => throw failure)) throw new InvalidOperationException("installed"); },
                () => EnvironmentalPolicyReload.MayRetain(true, false, addresses.Superseded, 0),
                _ => retry.RecordFailure(TimeSpan.Zero), () => revoked = true);
        }
        catch (Exception error) { observed = error; }
        AssertEx.True(revoked && ReferenceEquals(observed, failure), "Enumeration failure retained unverified address grants.");
        // A display, mount, settings or expiry commit reuses the unverified address sets.
        addresses.Committed();
        AssertEx.True(addresses.Superseded, "An unrelated commit cancelled the unverified address state.");
        AssertEx.False(EnvironmentalPolicyReload.MayRetain(true, false, addresses.Superseded, 0));
        AssertEx.Equal(1, retry.Failures, "Pending retry was cancelled.");
        AssertEx.False(addresses.Refresh(() => false));
        AssertEx.False(addresses.Superseded, "A verified unchanged enumeration did not clear the state.");
        AssertEx.True(addresses.Refresh(() => true) && addresses.Superseded, "An observed change did not supersede grants.");
        addresses.Committed();
        AssertEx.False(addresses.Superseded, "A commit of verified new addresses did not clear the change.");
    }

    private static void SupersededInputs()
    {
        foreach (bool block in new[] { false, true })
        foreach (bool on in new[] { false, true })
        foreach (bool committed in new[] { false, true })
            AssertEx.Equal(block && !on && !committed, DisplayRestriction.Pending(block, on, committed),
                $"block={block} on={on} committed={committed}");
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int may = service.IndexOf("private bool MayRetainCommittedPolicy()", StringComparison.Ordinal);
        string decision = service.Substring(may, service.IndexOf("private void RetainCommittedPolicy(", may, StringComparison.Ordinal) - may);
        foreach (string input in new[] { "Addresses.Superseded", "VolumeMappingChanged",
            "DisplayRestriction.Pending(ActiveConfig.Service.ActiveProfile.DisplayOffBlock, DisplayCurrentlyOn, CommittedDisplayRestricted)" })
            AssertEx.True(decision.Contains(input), "Retention ignores superseded input: " + input);
        AssertEx.True(service.Contains("return Addresses.Refresh(() => EnvironmentalPolicyReload.EnumerationChanged(NetworkAdapterEnumerator.EnumerateActiveAdapters("),
            "Address enumeration bypasses superseded tracking.");
        AssertEx.True(service.Contains("if (!Addresses.Superseded)\n                        ReloadRetry.Reset();"),
            "A commit with unverified addresses cancels the pending retry.");
    }

    private static void ClampedWait()
    {
        var backoff = new RecoveryBackoff();
        backoff.RecordFailure(TimeSpan.FromDays(40));
        TimeSpan wait = backoff.Wait(TimeSpan.Zero);
        AssertEx.Equal(RecoveryBackoff.MaxDelay, wait, "Inconsistent reading postponed recovery beyond the largest delay.");
        using var queue = new System.Collections.Concurrent.BlockingCollection<int>(1);
        queue.Add(1);
        AssertEx.True(queue.TryTake(out _, wait), "Clamped wait is not a valid queue timeout.");
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int recovery = service.IndexOf("private void RunDueRecovery()", StringComparison.Ordinal);
        string body = service.Substring(recovery, service.IndexOf("private void ExpireRules()", recovery, StringComparison.Ordinal) - recovery);
        AssertEx.True(body.Contains("RecoveryClock.Elapsed") && !body.Contains("DateTimeOffset.UtcNow"), "Recovery uses wall-clock time.");
        AssertEx.True(service.Contains("RuntimeRecovery.Wait(RuntimeSessionRevoked, SessionRebuild, ReloadRetry, RecoveryClock.Elapsed)"));
    }

    private static void AddressConditionRebuild()
    {
        var sets = new AddressConditionSets<string>(3);
        var lists = new[] { new List<string>(), new List<string>(), new List<string>() };
        void Clear() { foreach (var list in lists) list.Clear(); }
        HashSet<string>[] Sets(string subnet, string gateway, string dns) =>
            new[] { new HashSet<string> { subnet, "255.255.255.255/32" }, new HashSet<string> { gateway }, new HashSet<string> { dns } };
        bool Complete(HashSet<string>[] expected) =>
            Enumerable.Range(0, 3).All(i => lists[i].Count == expected[i].Count && expected[i].SetEquals(lists[i]) &&
                expected[i].SetEquals(sets.Published(i)));

        var home = Sets("192.168.1.0/24", "192.168.1.1/32", "192.168.1.1/32");
        AssertEx.True(sets.Update(home, Clear, (i, item) => lists[i].Add(item)) && Complete(home));
        AssertEx.False(sets.Update(Sets("192.168.1.0/24", "192.168.1.1/32", "192.168.1.1/32"), Clear,
            (_, _) => throw new InvalidOperationException("Unchanged sets rebuilt.")));

        // A condition build fails partway (bad prefix or allocation failure).
        var office = Sets("10.0.0.0/8", "10.0.0.1/32", "10.0.0.53/32");
        int built = 0;
        var failure = new ArgumentOutOfRangeException("prefix");
        Exception? observed = null;
        try { sets.Update(office, Clear, (i, item) => { if (++built == 2) throw failure; lists[i].Add(item); }); }
        catch (Exception error) { observed = error; }
        AssertEx.True(ReferenceEquals(observed, failure), "Build failure was swallowed or replaced.");
        AssertEx.True(Enumerable.Range(0, 3).All(i => !sets.Published(i).Any()), "Failed build left published sets.");

        // The same OS data again must count as a change and rebuild every list.
        int calls = 0;
        AssertEx.True(sets.Update(Sets("10.0.0.0/8", "10.0.0.1/32", "10.0.0.53/32"), Clear, (i, item) => { ++calls; lists[i].Add(item); }),
            "Rebuild saw no change and kept partial condition lists.");
        AssertEx.True(calls == 4 && Complete(office), "Rebuild did not rebuild every list.");

        // A failure after returning to a previously published network must not reuse it either.
        try { sets.Update(home, Clear, (_, _) => throw new OutOfMemoryException()); } catch (OutOfMemoryException) { }
        AssertEx.True(sets.Update(Sets("10.0.0.0/8", "10.0.0.1/32", "10.0.0.53/32"), Clear, (i, item) => lists[i].Add(item)) &&
            Complete(office), "Failed switch left the previous sets published over empty lists.");

        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int enumerate = service.IndexOf("private bool ReenumerateAdresses()", StringComparison.Ordinal);
        string body = service.Substring(enumerate, service.IndexOf("internal static void DeleteWfpObjects", enumerate, StringComparison.Ordinal) - enumerate);
        AssertEx.True(body.Contains("RemoteAddressSets.Update(") && !body.Contains("SetEquals") && !body.Contains("LocalSubnetAddreses = newLocalSubnetAddreses"),
            "Address enumeration bypasses the publish-after-build helper.");
    }

    // RemoteAddressSets.Update builds list i from set i. The service passes the sets as an
    // array and picks the condition list with a ternary on the index; both orders must agree,
    // or LocalSubnet, DefaultGateway and DNS rules would match each other's addresses.
    private static void AddressConditionIndexMapping()
    {
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int update = service.IndexOf("RemoteAddressSets.Update(", StringComparison.Ordinal);
        AssertEx.True(update > 0, "Address refresh no longer uses RemoteAddressSets.Update.");
        string call = service.Substring(update, service.IndexOf("return ipConfigurationChanged;", update, StringComparison.Ordinal) - update);

        var sets = System.Text.RegularExpressions.Regex.Match(call, @"new\[\]\s*\{\s*(\w+)\s*,\s*(\w+)\s*,\s*(\w+)\s*\}");
        AssertEx.True(sets.Success, "Address sets are no longer passed as one three-element array.");
        var lists = System.Text.RegularExpressions.Regex.Match(call,
            @"\(\s*list\s*==\s*0\s*\?\s*(\w+)\s*:\s*list\s*==\s*1\s*\?\s*(\w+)\s*:\s*(\w+)\s*\)");
        AssertEx.True(lists.Success, "The index-to-list mapping is no longer a ternary on list 0 and 1.");

        string[] expectedSets = { "newLocalSubnetAddreses", "newGatewayAddresses", "newDnsAddresses" };
        string[] expectedLists = { "LocalSubnetFilterConditions", "GatewayFilterConditions", "DnsFilterConditions" };
        for (int i = 0; i < 3; ++i)
        {
            AssertEx.Equal(expectedSets[i], sets.Groups[i + 1].Value, $"Address set {i} is out of order.");
            AssertEx.Equal(expectedLists[i], lists.Groups[i + 1].Value, $"Condition list {i} is out of order.");
        }

        // The sets come from the adapter enumerator in this order, and rules read the lists by keyword.
        AssertEx.True(service.Contains("out var unicastList, out var newGatewayAddresses, out var newDnsAddresses"),
            "Adapter enumeration outputs changed order.");
        AssertEx.True(System.Text.RegularExpressions.Regex.IsMatch(service,
            @"RuleDef\.LOCALSUBNET_ID[^\n]*\n\s*\{\s*\n\s*foreach \(var filter in LocalSubnetFilterConditions\)"),
            "LocalSubnet rules no longer read LocalSubnetFilterConditions.");
        AssertEx.True(System.Text.RegularExpressions.Regex.IsMatch(service,
            @"""DefaultGateway""[^\n]*\n\s*\{\s*\n\s*foreach \(var filter in GatewayFilterConditions\)"),
            "DefaultGateway rules no longer read GatewayFilterConditions.");
        AssertEx.True(System.Text.RegularExpressions.Regex.IsMatch(service,
            @"""DNS""[^\n]*\n\s*\{\s*\n\s*foreach \(var filter in DnsFilterConditions\)"),
            "DNS rules no longer read DnsFilterConditions.");
    }

    private static void ServiceWiring()
    {
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int core = service.IndexOf("private void FailClosedCore()", StringComparison.Ordinal);
        string failClosed = service.Substring(core, service.IndexOf("private static Engine CreateRuntimeEngine()", core, StringComparison.Ordinal) - core);
        AssertEx.False(failClosed.Contains("RunService = false"), "Revocation still stops the worker.");
        AssertEx.True(failClosed.Contains("RuntimeSessionRevocation.Close(WfpEngine.NativePtr"));
        AssertEx.False(service.Contains("EnvironmentalPolicyReload.Run(InstallFirewallRules, FailClosed)"));
        foreach (string handler in new[] { "ReloadForEnvironment(RuntimeEvent.network_reload, ReenumerateAdresses);",
            "ReloadForEnvironment(RuntimeEvent.display_reload, () => true);", "VolumeMappingChanged = true;\n                        ReloadForEnvironment(null, () => true);" })
            AssertEx.True(service.Contains(handler), "Environmental handler bypasses bounded retention: " + handler);
        int reload = service.IndexOf("private void ReloadForEnvironment(", StringComparison.Ordinal);
        int clear = service.IndexOf("LastInstallCommitted = false;", reload, StringComparison.Ordinal);
        int run = service.IndexOf("EnvironmentalPolicyReload.Run(", reload, StringComparison.Ordinal);
        AssertEx.True(reload > 0 && clear > reload && run > clear, "A pre-install failure could inherit an earlier commit.");
        int loop = service.IndexOf("while (RunService)", StringComparison.Ordinal);
        int recovery = service.IndexOf("RunDueRecovery();", loop, StringComparison.Ordinal);
        int take = service.IndexOf("Q.TryTake(out TwRequest req,", loop, StringComparison.Ordinal);
        AssertEx.True(loop > 0 && recovery > loop && take > recovery, "Worker loop does not run due recovery before waiting.");
        int rebuild = service.IndexOf("private void RebuildRuntimeSession()", StringComparison.Ordinal);
        int enumerate = service.IndexOf("ReenumerateAdresses()", rebuild, StringComparison.Ordinal);
        int init = service.IndexOf("InitFirewall(RevokedMode);", rebuild, StringComparison.Ordinal);
        int resume = service.IndexOf("CorrelatedDrops.Resume();", rebuild, StringComparison.Ordinal);
        AssertEx.True(rebuild > 0 && enumerate > rebuild && init > enumerate && resume > init,
            "Rebuild must re-read addresses and commit stored policy before prompting resumes.");
        int install = service.IndexOf("private void InstallFirewallRulesCore()", StringComparison.Ordinal);
        int commit = service.IndexOf("trx.Commit();", install, StringComparison.Ordinal);
        int flag = service.IndexOf("LastInstallCommitted = true;", install, StringComparison.Ordinal);
        AssertEx.True(service.IndexOf("LastInstallCommitted = false;", install, StringComparison.Ordinal) < commit && commit < flag,
            "Commit tracking does not bracket the WFP commit.");
        int worker = service.IndexOf("private void FirewallWorkerMethod()", StringComparison.Ordinal);
        string exit = service.Substring(worker, service.IndexOf("protected override void OnStart", worker, StringComparison.Ordinal) - worker);
        AssertEx.True(exit.Contains("#if DEBUG\n                throw;\n#endif"), "Release worker still rethrows into an unhandled crash.");
        AssertEx.True(exit.Contains("ServiceLifecyclePolicy.StoppedStatus(failure != null, Server?.Ready == true)"));
        AssertEx.True(exit.Contains("SetServiceStateReached(ServiceState.Stopped, win32ExitCode, serviceExitCode);"));

        string doctor = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallDoctor.cs");
        AssertEx.True(doctor.Contains("ServiceLifecyclePolicy.RestartDelays, ServiceLifecyclePolicy.RestartResetPeriod"));
        AssertEx.False(doctor.Contains("SetRestartOnFailure(TinyWallService.SERVICE_NAME, true)"));
        string scm = PromptTransactionIntegrationTests.Source("pylorak.Windows.Services/ServiceControlManager.cs");
        AssertEx.True(scm.Contains("fFailureActionsOnNonCrashFailures = 1") && scm.Contains("(ServiceConfig2InfoLevel)4"),
            "Non-crash failure actions are not enabled.");
    }
}

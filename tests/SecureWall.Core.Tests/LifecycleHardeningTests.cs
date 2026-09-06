using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class LifecycleHardeningTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("firewall recovery preserves every profile combination", ProfilesRoundTrip),
        ("firewall ownership rejects product substring and foreign grouping", ExactOwnership),
        ("firewall journal survives repeated acquisition and restart", RestartPreservesOriginal),
        ("firewall journal failure prevents host mutation", SaveFailurePreventsMutation),
        ("compatibility removal failure prevents notification and journal changes", RuleFailureStopsRecovery),
        ("partial profile restoration retains durable journal for retry", RestoreFailureRetainsJournal),
        ("invalid firewall recovery record prevents new host mutation", InvalidJournalFailsClosed),
        ("foreign reserved name alone blocks production acquisition adapter before mutation", ForeignReservedNamePreflight),
        ("mixed rule collection preflights before any owned deletion", MixedRuleCollectionPreflight),
        ("permanently pending startup reaches bounded rollback cleanup", PendingStartupRollback),
        ("ordinary pending removal cannot terminate or clean up", PendingOrdinaryRemoval),
        ("slow startup within advertised allowance remains graceful", SlowStartupGraceful),
        ("failed rollback termination retains durable protection", RollbackTerminationFailure),
        ("rollback waits for stopped observation before cleanup", RollbackStopObservationFailure),
        ("rollback process authentication rejects foreign and recycled identities", RollbackIdentity),
        ("pending stop reaches bounded rollback without duplicate stop request", PendingStopRollback),
        ("stopped firewall recovery skips only when no compatibility record exists", StoppedFirewallRecovery),
        ("exited rollback process permits cleanup only after fresh stopped observation", RollbackProcessExitRace),
    };

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Lifecycle assertion failed.");
    }

    private static void Fails(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected operation failure.");
    }

    private static void ProfilesRoundTrip()
    {
        for (int bits = 0; bits < 8; bits++)
        {
            bool[] original = { (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0 };
            Check(original.SequenceEqual(FirewallRecoveryPolicy.Decode(
                FirewallRecoveryPolicy.Encode(original[0], original[1], original[2]))));
        }
    }

    private static void ExactOwnership()
    {
        Check(FirewallRecoveryPolicy.OwnsRule(FirewallRecoveryPolicy.Inbound, FirewallRecoveryPolicy.Group));
        Check(FirewallRecoveryPolicy.OwnsRule(FirewallRecoveryPolicy.Outbound, FirewallRecoveryPolicy.Group));
        Check(!FirewallRecoveryPolicy.OwnsRule("Admin SecureWall Block", "SecureWall"));
        Check(!FirewallRecoveryPolicy.OwnsRule(FirewallRecoveryPolicy.Inbound, "Administrator"));
        Check(!FirewallRecoveryPolicy.OwnsRule(FirewallRecoveryPolicy.Inbound + " backup", FirewallRecoveryPolicy.Group));
        Check(!FirewallRecoveryPolicy.OwnsRule(null, null));
    }

    private static void RestartPreservesOriginal()
    {
        int? durable = null;
        bool[] host = { true, false, true };
        int saves = 0;
        void Acquire() => FirewallRecoveryPolicy.Acquire(() => durable, () => host.ToArray(),
            value => { durable = value; saves++; }, () => host = new[] { true, true, true });
        Acquire();
        Acquire(); // New process reads the same durable value after a crash.
        bool rulesRemoved = false;
        FirewallRecoveryPolicy.Restore(() => rulesRemoved = true, () => durable,
            original => { Check(rulesRemoved); host = original; }, () => durable = null);
        Check(saves == 1 && durable == null && host.SequenceEqual(new[] { true, false, true }));
    }

    private static void SaveFailurePreventsMutation()
    {
        bool changed = false;
        Fails(() => FirewallRecoveryPolicy.Acquire(() => null, () => new[] { false, true, false },
            value => throw new InvalidOperationException(), () => changed = true));
        Check(!changed);
    }

    private static void RuleFailureStopsRecovery()
    {
        bool changed = false;
        bool cleared = false;
        Fails(() => FirewallRecoveryPolicy.Restore(() => throw new InvalidOperationException(),
            () => 0x100, values => changed = true, () => cleared = true));
        Check(!changed && !cleared);
    }

    private static void RestoreFailureRetainsJournal()
    {
        int? durable = FirewallRecoveryPolicy.Encode(false, true, false);
        Fails(() => FirewallRecoveryPolicy.Restore(() => { }, () => durable,
            original => throw new InvalidOperationException(), () => durable = null));
        Check(durable.HasValue);
        bool[]? restored = null;
        FirewallRecoveryPolicy.Restore(() => { }, () => durable,
            original => restored = original, () => durable = null);
        Check(durable == null && restored!.SequenceEqual(new[] { false, true, false }));
    }

    private static void InvalidJournalFailsClosed()
    {
        bool changed = false;
        Fails(() => FirewallRecoveryPolicy.Acquire(() => 8, () => throw new Exception(),
            value => throw new Exception(), () => changed = true));
        Check(!changed);
    }

    private static void ForeignReservedNamePreflight()
    {
        foreach (string name in new[] { FirewallRecoveryPolicy.Inbound, FirewallRecoveryPolicy.Outbound.ToLowerInvariant() })
        {
            int mutations = 0;
            var rules = new[] { new KeyValuePair<string, string>(name, "Administrator") };
            Fails(() => FirewallRecoveryPolicy.AcquireForRules(rules, () => mutations++));
            Check(mutations == 0);
        }
        int applied = 0;
        FirewallRecoveryPolicy.AcquireForRules(new[] {
            new KeyValuePair<string, string>("Administrator SecureWall block", "Administrator"),
            new KeyValuePair<string, string>(FirewallRecoveryPolicy.Inbound, FirewallRecoveryPolicy.Group)
        }, () => applied++);
        Check(applied == 1);
    }

    private static void MixedRuleCollectionPreflight()
    {
        var rules = new[] {
            new KeyValuePair<string, string>(FirewallRecoveryPolicy.Inbound, FirewallRecoveryPolicy.Group),
            new KeyValuePair<string, string>(FirewallRecoveryPolicy.Outbound, "Administrator")
        };
        var removed = new List<string>();
        Fails(() => { foreach (var name in FirewallRecoveryPolicy.OwnedRuleNames(rules)) removed.Add(name); });
        Check(removed.Count == 0);
    }

    private static void PendingStartupRollback()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        var state = LifecycleServiceState.StartPending;
        Check(!ServiceLifecyclePolicy.WaitUntil(() => state == LifecycleServiceState.Running,
            ServiceLifecyclePolicy.StartupTimeout, () => elapsed, duration => elapsed += duration));
        var steps = new List<string>();
        ServiceLifecyclePolicy.Cleanup(true,
            () => ServiceLifecyclePolicy.StopGracefully(() => state, () => throw new Exception("Pending service cannot accept stop."),
                () => elapsed, duration => elapsed += duration),
            () => { steps.Add("terminate"); state = LifecycleServiceState.Stopped; },
            () => state == LifecycleServiceState.Stopped, () => steps.Add("cleanup"));
        Check(elapsed == ServiceLifecyclePolicy.StartupTimeout + ServiceLifecyclePolicy.CleanupGrace);
        Check(steps.SequenceEqual(new[] { "terminate", "cleanup" }));
    }

    private static void PendingOrdinaryRemoval()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        bool terminated = false, cleaned = false;
        Fails(() => ServiceLifecyclePolicy.Cleanup(false,
            () => ServiceLifecyclePolicy.StopGracefully(() => LifecycleServiceState.StartPending, () => throw new Exception(),
                () => elapsed, duration => elapsed += duration),
            () => terminated = true, () => true, () => cleaned = true));
        Check(!terminated && !cleaned && elapsed == ServiceLifecyclePolicy.CleanupGrace);
    }

    private static void SlowStartupGraceful()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        Check(ServiceLifecyclePolicy.WaitUntil(() => elapsed >= TimeSpan.FromSeconds(110),
            ServiceLifecyclePolicy.StartupTimeout, () => elapsed, duration => elapsed += duration));
        bool stopped = false, cleaned = false;
        ServiceLifecyclePolicy.Cleanup(true,
            () => ServiceLifecyclePolicy.StopGracefully(() => stopped ? LifecycleServiceState.Stopped : LifecycleServiceState.Running,
                () => stopped = true, () => elapsed, duration => elapsed += duration),
            () => throw new Exception("Graceful stop must not terminate."), () => stopped, () => cleaned = true);
        Check(cleaned && elapsed == TimeSpan.FromSeconds(110));
    }

    private static void RollbackTerminationFailure()
    {
        bool cleaned = false;
        Fails(() => ServiceLifecyclePolicy.Cleanup(true, () => false,
            () => throw new InvalidOperationException("Identity mismatch or process did not exit."),
            () => true, () => cleaned = true));
        Check(!cleaned);
    }

    private static void RollbackStopObservationFailure()
    {
        bool cleaned = false, terminated = false;
        Fails(() => ServiceLifecyclePolicy.Cleanup(true, () => false, () => terminated = true,
            () => false, () => cleaned = true));
        Check(terminated && !cleaned);
    }

    private static void RollbackIdentity()
    {
        const string path = @"C:\Program Files\SecureWall\SecureWall.exe";
        Check(ServiceLifecyclePolicy.IsRollbackProcess(42, 9, 42, 0x10, path, path, true));
        Check(!ServiceLifecyclePolicy.IsRollbackProcess(0, 9, 0, 0x10, path, path, true));
        Check(!ServiceLifecyclePolicy.IsRollbackProcess(9, 9, 9, 0x10, path, path, true));
        Check(!ServiceLifecyclePolicy.IsRollbackProcess(42, 9, 43, 0x10, path, path, true));
        Check(!ServiceLifecyclePolicy.IsRollbackProcess(42, 9, 42, 0x20, path, path, true));
        Check(!ServiceLifecyclePolicy.IsRollbackProcess(42, 9, 42, 0x10, @"C:\Other\SecureWall.exe", path, true));
        Check(!ServiceLifecyclePolicy.IsRollbackProcess(42, 9, 42, 0x10, path, path, false));
    }

    private static void PendingStopRollback()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        bool terminated = false, cleaned = false;
        ServiceLifecyclePolicy.Cleanup(true,
            () => ServiceLifecyclePolicy.StopGracefully(() => LifecycleServiceState.StopPending,
                () => throw new Exception("A pending stop must not receive another stop request."),
                () => elapsed, duration => elapsed += duration),
            () => terminated = true, () => terminated, () => cleaned = true);
        Check(terminated && cleaned && elapsed == ServiceLifecyclePolicy.CleanupGrace);
    }

    private static void StoppedFirewallRecovery()
    {
        Check(FirewallRecoveryPolicy.CanSkipStoppedServiceRecovery(true, false));
        Check(!FirewallRecoveryPolicy.CanSkipStoppedServiceRecovery(true, true));
        Check(!FirewallRecoveryPolicy.CanSkipStoppedServiceRecovery(false, false));
        Check(!FirewallRecoveryPolicy.CanSkipStoppedServiceRecovery(false, true));
    }

    private static void RollbackProcessExitRace()
    {
        int observations = 0;
        bool cleaned = false;
        ServiceLifecyclePolicy.Cleanup(true, () => false,
            () => ServiceLifecyclePolicy.RequireStoppedAfterProcessOpenFailure(
                () => { observations++; return LifecycleServiceState.Stopped; }, new InvalidOperationException("Process exited.")),
            () => true, () => cleaned = true);
        Check(cleaned && observations == 1);

        foreach (var state in new[] { LifecycleServiceState.Running, LifecycleServiceState.StopPending, LifecycleServiceState.StartPending })
        {
            cleaned = false;
            Fails(() => ServiceLifecyclePolicy.Cleanup(true, () => false,
                () => ServiceLifecyclePolicy.RequireStoppedAfterProcessOpenFailure(() => state, new InvalidOperationException("OpenProcess failed.")),
                () => true, () => cleaned = true));
            Check(!cleaned);
        }
    }
}

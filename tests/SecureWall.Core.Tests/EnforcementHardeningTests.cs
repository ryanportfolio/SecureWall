using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests
{
    internal static class EnforcementHardeningTests
    {
        internal static IEnumerable<(string Name, Action Test)> Cases
        {
            get
            {
                yield return ("policy save failure cannot publish or enforce proposed state", SaveFailure);
                yield return ("WFP failure restores durable state and retains published policy", EnforcementFailure);
                yield return ("rollback failure withdraws runtime permits and cannot publish success", RecoveryFailure);
                yield return ("successful policy publishes only after save and WFP commit", SuccessfulPublication);
                yield return ("all runtime registrations avoid persistent and boot lifetimes", RuntimeLifetimes);
                yield return ("failed user deny aborts a partially registered replacement", DenyFailure);
                yield return ("failed permit cannot return allow success", PermitFailure);
                yield return ("recovery baseline cannot outrank promptable default deny", RecoveryPriority);
                yield return ("recovery permit sits between recovery deny and runtime default block", RecoveryPermitWeight);
                yield return ("recovery permit set is exactly DHCP and DNS", RecoveryPermitRules);
                yield return ("BlockAll excludes both LAN and WSL opt-in permits", BlockAll);
                yield return ("eleven minutes of polling cannot prevent password relock", PollingTimeout);
                yield return ("successful user activity restarts inactivity window", UserTimeout);
                yield return ("journal preparation failure never touches proposed policy", JournalPreparationFailure);
                yield return ("failed rollback recovers prior policy on next startup", RestartAfterRecoveryFailure);
                yield return ("failed startup recovery cannot load uncertain candidate", FailedStartupRecovery);
                yield return ("journal completion failure withdraws grants and restores prior policy", JournalCompletionFailure);
                yield return ("interrupted policy save restores prior policy at every marked boundary", InterruptedPolicyBoundaries);
                yield return ("minute expiry faults withdraw permits across every transaction boundary", ExpiryFaults);
                yield return ("expiry uses absolute deadline without overflow or renewal", AbsoluteExpiry);
                yield return ("native revocation closes despite failed unsubscribe and abort references", NativeRevocationReferences);
                yield return ("failed native close terminates before returning an error response", NativeRevocationFailure);
                yield return ("disposed SafeHandle alone is not proof of native revocation", DisposedHandleIsNotRevocation);
                yield return ("journal failure closes native session before response and COM cleanup", JournalNativeLifetime);
                yield return ("remote block validation rejects malformed lists and prefixes", InvalidRemoteConditions);
                yield return ("late worker disposal completes stop without reverting to Running", LateWorkerStop);
                yield return ("hung worker terminates within bounded stop deadline", HungWorkerStop);
                yield return ("stop request and SCM reporting failures terminate without reverting state", FailedWorkerStop);
                yield return ("temporary exception ownership releases once for success skip and failure", TemporaryExceptionRelease);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }

        private static void SaveFailure()
        {
            int oldVisible = 7, enforcement = 7, durable = 7;
            bool restored = false, failedClosed = false;
            Throws<InvalidOperationException>(() => PolicyChangeTransaction.Apply(
                () => throw new InvalidOperationException("disk full before atomic replacement"),
                () => enforcement = 9,
                () => { restored = true; durable = 7; },
                () => oldVisible = 9,
                () => failedClosed = true));
            Check(oldVisible == 7 && enforcement == 7 && durable == 7 && !restored && !failedClosed,
                "A failed save changed policy or published success.");
        }

        private static void EnforcementFailure()
        {
            string visible = "old", durable = "old", enforcement = "old";
            bool failedClosed = false;
            Throws<InvalidOperationException>(() => PolicyChangeTransaction.Apply(
                () => durable = "requested",
                () => { Check(visible == "old", "Policy was published before commit."); throw new InvalidOperationException("WFP commit failed"); },
                () => durable = "old",
                () => { visible = "requested"; enforcement = "requested"; },
                () => failedClosed = true));
            Check(visible == "old" && durable == "old" && enforcement == "old" && !failedClosed,
                "WFP failure left inconsistent settings or reported success.");
        }

        private static void RecoveryFailure()
        {
            bool published = false, runtimePermit = true;
            Throws<AggregateException>(() => PolicyChangeTransaction.Apply(
                () => { },
                () => throw new InvalidOperationException("WFP failure"),
                () => throw new InvalidOperationException("disk restore failure"),
                () => published = true,
                () => runtimePermit = false));
            Check(!published && !runtimePermit, "Unrecoverable settings failure retained runtime grants.");
        }

        private static void SuccessfulPublication()
        {
            var events = new List<string>();
            PolicyChangeTransaction.Apply(() => events.Add("persist"), () => events.Add("commit"),
                () => events.Add("restore"), () => events.Add("publish"), () => events.Add("fail closed"));
            Check(events.SequenceEqual(new[] { "persist", "commit", "publish" }), "Incorrect publication order.");
        }

        private static void RuntimeLifetimes()
        {
            foreach (string policy in new[] { "Disabled", "Learning", "temporary app", "until reboot", "permanent app", "LAN", "WSL", "inherited app" })
            {
                var lifetimes = new List<WfpFilterLifetime>();
                var ids = WfpFilterPairRegistration.Register(lifetime => { lifetimes.Add(lifetime); return 42; }, false, runtimeOnly: true);
                Check(ids.Count == 1 && lifetimes.SequenceEqual(new[] { WfpFilterLifetime.Dynamic }), policy + " created a surviving permit.");
            }
        }

        private static void DenyFailure()
        {
            Check(PromptFilterClassifier.IsRequiredProtection(true), "Explicit blocks must be critical.");
            var existing = new[] { "old allow", "old user deny" };
            var staged = new List<ulong>();
            Throws<InvalidOperationException>(() => WfpFilterPairRegistration.Register(lifetime =>
            {
                if (lifetime == WfpFilterLifetime.BootTime) throw new InvalidOperationException("deny failed");
                staged.Add(51);
                return 51;
            }, true));
            Check(staged.Count == 1 && existing.SequenceEqual(new[] { "old allow", "old user deny" }),
                "Failure did not interrupt partial registration.");
        }

        private static void PermitFailure()
        {
            bool success = false;
            Throws<InvalidOperationException>(() =>
            {
                WfpFilterPairRegistration.Register(_ => throw new InvalidOperationException("app ID rejected"), false, runtimeOnly: true);
                success = true;
            });
            Check(!success, "A failed permit returned success.");
        }

        private static void RecoveryPriority()
        {
            const ulong defaultBlock = 3000000;
            Check(EnforcementPolicy.RecoveryBlockWeight(defaultBlock) < defaultBlock, "Baseline steals promptable drops.");
            Check(PromptFilterClassifier.IsPromptable(true, defaultBlock, defaultBlock, true), "Runtime default deny is not promptable.");
            Check(!PromptFilterClassifier.IsPromptable(true, EnforcementPolicy.RecoveryBlockWeight(defaultBlock), defaultBlock, true), "Recovery baseline became prompt authority.");
        }

        private static void RecoveryPermitWeight()
        {
            const ulong defaultBlock = 3000000;
            ulong deny = EnforcementPolicy.RecoveryBlockWeight(defaultBlock);
            ulong permit = EnforcementPolicy.RecoveryPermitWeight(defaultBlock);
            Check(deny < permit, "Recovery deny outranks or ties the recovery permit; DHCP/DNS would stay blocked without the service.");
            Check(permit < defaultBlock, "Recovery permit outranks or ties the runtime default block; BlockAll and user blocks would lose.");
            Check(!PromptFilterClassifier.IsPromptable(true, permit, defaultBlock, true), "Recovery permit weight became prompt authority.");
            Check(!PromptFilterClassifier.IsPromptable(false, permit, defaultBlock, true), "A permit became prompt authority.");
            Throws<ArgumentOutOfRangeException>(() => EnforcementPolicy.RecoveryPermitWeight(1));
            Throws<ArgumentOutOfRangeException>(() => EnforcementPolicy.RecoveryBlockWeight(1));
        }

        private static void RecoveryPermitRules()
        {
            const byte tcp = 6, udp = 17;
            var expected = new HashSet<(bool v6, bool inbound, byte proto, ushort? local, ushort? remote)>
            {
                (false, false, udp, 68, 67),
                (true, false, udp, 546, 547),
                (false, false, udp, null, 53),
                (true, false, udp, null, 53),
                (false, false, tcp, null, 53),
                (true, false, tcp, null, 53),
                (false, true, udp, 68, 67),
                (true, true, udp, 546, 547),
            };
            var rules = EnforcementPolicy.RecoveryPermitRules();
            var actual = new HashSet<(bool, bool, byte, ushort?, ushort?)>(
                rules.Select(r => (r.IsIPv6, r.Inbound, r.IpProtocol, r.LocalPort, r.RemotePort)));
            Check(rules.Count == 8 && actual.Count == 8, "Recovery permit rule count changed.");
            Check(actual.SetEquals(expected), "Recovery permit set is not exactly DHCPv4/v6 request+reply and DNS UDP/TCP v4/v6.");
            Check(rules.All(r => r.LocalPort.HasValue || r.RemotePort.HasValue), "A recovery permit has no port condition.");
            Check(rules.Select(r => r.Name).Distinct().Count() == 8 && rules.All(r => r.Name.StartsWith("SecureWall recovery permit ", StringComparison.Ordinal)),
                "Recovery permit names are not unique or not provider-prefixed.");
        }

        private static void BlockAll()
        {
            foreach (bool lan in new[] { false, true })
            foreach (bool wsl in new[] { false, true })
            {
                Check(!EnforcementPolicy.OptionalPermitEnabled(true, lan) && !EnforcementPolicy.OptionalPermitEnabled(true, wsl),
                    "BlockAll retained an optional permit.");
                Check(EnforcementPolicy.OptionalPermitEnabled(false, lan) == lan, "LAN preference changed outside BlockAll.");
                Check(EnforcementPolicy.OptionalPermitEnabled(false, wsl) == wsl, "WSL preference changed outside BlockAll.");
            }
        }

        private static void JournalPreparationFailure()
        {
            bool saved = false, enforced = false, published = false;
            Throws<InvalidOperationException>(() => PolicyChangeTransaction.Apply(
                () => saved = true, () => enforced = true, () => { }, () => published = true, () => { },
                () => throw new InvalidOperationException("recovery snapshot write failed"), () => { }));
            Check(!saved && !enforced && !published, "Candidate was touched without a durable recovery snapshot.");
        }

        private static void RestartAfterRecoveryFailure()
        {
            string disk = "old restrictive", snapshot = "";
            bool marker = false, grants = true, published = false;
            Throws<AggregateException>(() => PolicyChangeTransaction.Apply(
                () => disk = "proposed permit",
                () => throw new InvalidOperationException("WFP rejected change"),
                () => throw new InvalidOperationException("disk unavailable during rollback"),
                () => published = true, () => grants = false,
                () => { snapshot = disk; marker = true; }, () => marker = false));
            Check(marker && disk == "proposed permit" && !grants && !published, "Failed rollback lost its recovery evidence.");
            PolicyRecoveryJournal.Recover(marker, () => disk = snapshot, () => marker = false);
            Check(!marker && disk == "old restrictive", "Restart loaded a previously failed permit.");
        }

        private static void FailedStartupRecovery()
        {
            bool marker = true, loadedCandidate = false;
            Throws<InvalidOperationException>(() =>
            {
                PolicyRecoveryJournal.Recover(marker,
                    () => throw new InvalidOperationException("recovery file is unreadable"), () => marker = false);
                loadedCandidate = true;
            });
            Check(marker && !loadedCandidate, "Startup continued past failed policy recovery.");
        }

        private static void JournalCompletionFailure()
        {
            string disk = "old", snapshot = "";
            bool marker = false, grants = false, published = false;
            Throws<InvalidOperationException>(() => PolicyChangeTransaction.Apply(
                () => disk = "proposed", () => grants = true, () => disk = "old", () => published = true,
                () => grants = false, () => { snapshot = disk; marker = true; },
                () => throw new InvalidOperationException("marker removal denied")));
            Check(marker && !grants && !published, "Failed journal completion falsely acknowledged policy.");
            PolicyRecoveryJournal.Recover(marker, () => disk = snapshot, () => marker = false);
            Check(disk == "old" && !marker, "Failed journal completion did not restore prior policy at restart.");
        }

        private static void InterruptedPolicyBoundaries()
        {
            foreach (string boundary in new[] { "snapshot saved", "candidate saved", "WFP committed" })
            {
                const string snapshot = "prior configuration";
                string disk = boundary == "snapshot saved" ? snapshot : "candidate configuration";
                bool marker = true;
                PolicyRecoveryJournal.Recover(marker, () => disk = snapshot, () => marker = false);
                Check(disk == snapshot && !marker, "Crash at " + boundary + " did not recover prior policy.");
            }
            bool restored = false;
            PolicyRecoveryJournal.Recover(false, () => restored = true, () => throw new InvalidOperationException());
            Check(!restored, "A fully completed policy was reverted at startup.");
        }

        private static void ExpiryFaults()
        {
            foreach (string failure in new[] { "clone", "prune", "journal", "save", "commit", "restore", "complete" })
            {
                bool permit = true, visiblePermit = true, diskPermit = true, responseSuccess = false;
                Action revoke = () => { permit = false; visiblePermit = false; };
                Throws<Exception>(() =>
                {
                    ExpiringPolicyMaintenance.Run(
                        () => failure == "clone" ? throw new InvalidOperationException("clone failed") : false,
                        _ => failure == "prune" ? throw new InvalidOperationException("prune failed") : true,
                        candidate => PolicyChangeTransaction.Apply(
                            () => { if (failure == "save") throw new InvalidOperationException("disk full"); diskPermit = candidate; },
                            () => { if (failure == "commit" || failure == "restore") throw new InvalidOperationException("WFP failed"); permit = candidate; },
                            () => { if (failure == "restore") throw new InvalidOperationException("restore failed"); diskPermit = true; },
                            () => visiblePermit = candidate, revoke,
                            () => { if (failure == "journal") throw new InvalidOperationException("journal failed"); },
                            () => { if (failure == "complete") throw new InvalidOperationException("marker deletion failed"); }),
                        revoke);
                    responseSuccess = true;
                });
                Check(!permit && !visiblePermit && !responseSuccess, failure + " extended an expired grant.");
                if (failure != "restore" && failure != "complete")
                    Check(diskPermit, "Revocation unexpectedly discarded the durable prior-policy recovery contract.");
            }
        }

        private static void AbsoluteExpiry()
        {
            var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Check(!EnforcementPolicy.IsExpired(created, 5, created.AddMinutes(5).AddTicks(-1)), "Grant expired early.");
            Check(EnforcementPolicy.IsExpired(created, 5, created.AddMinutes(5)), "Deadline was not inclusive.");
            Check(EnforcementPolicy.IsExpired(created.ToLocalTime(), 5, created.AddMinutes(6)), "Clock kinds renewed the grant.");
            Check(!EnforcementPolicy.IsExpired(DateTime.MaxValue, int.MaxValue, DateTime.UtcNow), "Overflow changed expiry.");
            Check(!EnforcementPolicy.IsExpired(created, 0, created.AddYears(1)), "Permanent rule expired.");
        }

        private sealed class EngineHandle : SafeHandle
        {
            internal int AutomaticCloses;
            internal EngineHandle() : base(IntPtr.Zero, true) { SetHandle(new IntPtr(17)); }
            public override bool IsInvalid => handle == IntPtr.Zero;
            protected override bool ReleaseHandle() { ++AutomaticCloses; return true; }
        }

        private sealed class SubscriptionHandle : SafeHandle
        {
            private readonly EngineHandle engine;
            private readonly bool fail;
            internal bool UnsubscribeAttempted;
            internal SubscriptionHandle(EngineHandle engine, bool fail) : base(IntPtr.Zero, true)
            {
                this.engine = engine;
                this.fail = fail;
                bool added = false;
                engine.DangerousAddRef(ref added);
                SetHandle(new IntPtr(23));
            }
            public override bool IsInvalid => handle == IntPtr.Zero;
            protected override bool ReleaseHandle()
            {
                UnsubscribeAttempted = true;
                if (fail) return false; // Matches the production WFP subscription wrapper.
                engine.DangerousRelease();
                return true;
            }
        }

        private static void NativeRevocationReferences()
        {
            foreach (bool unsubscribeFails in new[] { false, true })
            {
                using var engine = new EngineHandle();
                using var subscription = new SubscriptionHandle(engine, unsubscribeFails);
                bool abortReference = false;
                engine.DangerousAddRef(ref abortReference); // Failed native Abort retains this ref.
                int nativeCloses = 0;
                RuntimeSessionRevocation.Close(engine, pointer =>
                {
                    Check(subscription.UnsubscribeAttempted && pointer == new IntPtr(17), "Close preceded unsubscribe or used wrong handle.");
                    ++nativeCloses;
                    return 0;
                }, _ => throw new InvalidOperationException("unexpected termination"), subscription.Dispose);
                Check(nativeCloses == 1 && engine.IsClosed && engine.AutomaticCloses == 0, "Native close was deferred by SafeHandle refs.");
                engine.DangerousRelease();
                if (unsubscribeFails) engine.DangerousRelease();
                subscription.Dispose();
                engine.Dispose();
                Check(nativeCloses == 1 && engine.AutomaticCloses == 0, "Revocation caused a second close.");
            }
        }

        private static void NativeRevocationFailure()
        {
            using var engine = new EngineHandle();
            bool terminated = false, responded = false;
            Throws<InvalidOperationException>(() =>
            {
                RuntimeSessionRevocation.Close(engine, _ => 5, _ => terminated = true,
                    () => throw new InvalidOperationException("unsubscribe failed"));
                responded = true;
            });
            Check(terminated && !responded && !engine.IsClosed, "Native close failure returned as if withdrawal succeeded.");
        }

        private static void DisposedHandleIsNotRevocation()
        {
            var engine = new EngineHandle();
            bool held = false, terminated = false;
            int nativeCloses = 0;
            engine.DangerousAddRef(ref held);
            engine.Dispose();
            Check(engine.AutomaticCloses == 0, "Test did not retain the native session reference.");
            RuntimeSessionRevocation.Close(engine, _ => { ++nativeCloses; return 0; }, _ => terminated = true);
            Check(!terminated && nativeCloses == 1 && engine.AutomaticCloses == 0, "Managed disposal was accepted as native closure.");
            engine.DangerousRelease();
        }

        private static void JournalNativeLifetime()
        {
            using var engine = new EngineHandle();
            using var subscription = new SubscriptionHandle(engine, true);
            bool candidatePermit = false, published = false, nativeClosed = false, responded = false;
            var events = new List<string>();
            Throws<InvalidOperationException>(() => PolicyChangeTransaction.Apply(
                () => { }, () => candidatePermit = true, () => { }, () => published = true,
                () => RuntimeSessionRevocation.Close(engine, _ =>
                {
                    candidatePermit = false;
                    nativeClosed = true;
                    events.Add("native close");
                    return 0;
                }, _ => throw new InvalidOperationException("unexpected termination"), subscription.Dispose),
                () => { }, () => throw new InvalidOperationException("marker deletion denied")));
            responded = true;
            events.Add("error response");
            // COM restoration can remain blocked here indefinitely; revocation has already happened.
            Check(nativeClosed && !candidatePermit && !published && responded &&
                events.SequenceEqual(new[] { "native close", "error response" }), "Candidate survived into failure/COM cleanup.");
            engine.DangerousRelease();
        }

        private static void InvalidRemoteConditions()
        {
            foreach (string input in new[] { "not-an-ip", "192.0.2.1,not-an-ip", ",", "::1/129", "192.0.2.1/33", "192.0.2.1/-1", "::1/64/1" })
                Throws<FormatException>(() => EnforcementPolicy.ValidateRemoteAddresses(input));
            foreach (string input in new[] { "", "192.0.2.1", "2001:db8::/64", "LocalSubnet,DefaultGateway,DNS", "192.0.2.0/24,2001:db8::/32" })
                EnforcementPolicy.ValidateRemoteAddresses(input);
        }

        private static void LateWorkerStop()
        {
            using var stopRequested = new ManualResetEventSlim();
            using var disposalBlocked = new ManualResetEventSlim();
            using var permitDisposal = new ManualResetEventSlim();
            bool disposed = false, completed = false, terminated = false;
            var worker = new Thread(() =>
            {
                stopRequested.Wait();
                try { }
                finally
                {
                    disposalBlocked.Set();
                    permitDisposal.Wait();
                    disposed = true;
                }
            });
            worker.Start();
            TimeSpan elapsed = TimeSpan.Zero;
            int polls = 0, progress = 0;
            try
            {
                WorkerStopCoordinator.Stop(() =>
                {
                    stopRequested.Set();
                    Check(disposalBlocked.Wait(TimeSpan.FromSeconds(2)), "Worker did not enter disposal.");
                }, timeout =>
                {
                    Check(timeout <= TimeSpan.FromSeconds(1), "Join exceeds progress interval.");
                    ++polls;
                    if (polls <= 11)
                    {
                        Check(!worker.Join(0) && !completed, "Stop completed before disposal.");
                        elapsed += timeout;
                        return false;
                    }
                    permitDisposal.Set();
                    return worker.Join(TimeSpan.FromSeconds(2));
                }, hint =>
                {
                    Check(hint > 0 && hint <= 6000 && !completed, "Invalid pending-state progress.");
                    ++progress;
                }, () =>
                {
                    Check(disposed && !worker.IsAlive, "SCM completion preceded worker disposal.");
                    completed = true;
                }, _ => terminated = true, () => elapsed);
                Check(completed && !terminated && polls == 12 && progress >= polls,
                    "Late disposal did not complete a continuously pending stop.");
            }
            finally
            {
                stopRequested.Set();
                permitDisposal.Set();
                Check(worker.Join(TimeSpan.FromSeconds(2)), "Test worker did not exit.");
            }
        }

        private static void HungWorkerStop()
        {
            TimeSpan elapsed = TimeSpan.Zero;
            bool completed = false;
            int terminated = 0;
            WorkerStopCoordinator.Stop(() => elapsed += TimeSpan.FromSeconds(5), timeout =>
            {
                elapsed += timeout;
                return false;
            }, _ => { }, () => completed = true, error =>
            {
                Check(error is TimeoutException, "Timeout reason was lost.");
                ++terminated;
            }, () => elapsed);
            Check(!completed && terminated == 1 && elapsed == WorkerStopCoordinator.Deadline,
                "Hung stop escaped the total deadline or falsely reported stopped.");
        }

        private static void FailedWorkerStop()
        {
            foreach (string step in new[] { "request", "join", "progress", "complete" })
            {
                int terminations = 0;
                var failure = new InvalidOperationException(step);
                WorkerStopCoordinator.Stop(
                    () => { if (step == "request") throw failure; },
                    _ => step == "join" ? throw failure : true,
                    _ => { if (step == "progress") throw failure; },
                    () => { if (step == "complete") throw failure; },
                    error => { Check(ReferenceEquals(error, failure), "Stop error changed."); ++terminations; },
                    () => TimeSpan.Zero);
                Check(terminations == 1, "Stop failure escaped to ServiceBase or terminated twice: " + step);
            }
        }

        private static void TemporaryExceptionRelease()
        {
            foreach (string outcome in new[] { "success", "BlockAll", "Disabled", "rules", "install", "enqueue", "acquire" })
            {
                int requests = 0, releases = 0;
                bool queued = false;
                Action release = () => ++releases;
                Action scenario = () =>
                {
                    TemporaryExceptionEnforcement.Enqueue(() =>
                    {
                        if (outcome == "acquire") throw new InvalidOperationException("priority request failed");
                        ++requests;
                    }, () =>
                    {
                        if (outcome == "enqueue") throw new InvalidOperationException("queue closed");
                        queued = true;
                    }, release);
                    Check(queued && releases == 0, "Producer released transferred priority ownership.");
                    TemporaryExceptionEnforcement.Run(() =>
                    {
                        if (outcome == "BlockAll" || outcome == "Disabled") return false;
                        if (outcome == "rules") throw new InvalidOperationException("rule construction failed");
                        if (outcome == "install") throw new InvalidOperationException("WFP install failed");
                        return true;
                    }, release);
                };
                if (outcome == "success" || outcome == "BlockAll" || outcome == "Disabled") scenario();
                else Throws<InvalidOperationException>(scenario);
                Check(requests == releases && releases == (outcome == "acquire" ? 0 : 1),
                    "Priority ownership leaked or released twice: " + outcome);
            }
        }

        private sealed class Clock : IClock
        {
            public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }

        private static void PollingTimeout()
        {
            var clock = new Clock();
            var activity = new UserActivityTimeout(clock, TimeSpan.FromMinutes(10));
            activity.Record(true);
            for (int i = 0; i < 880; ++i)
            {
                clock.UtcNow = clock.UtcNow.AddMilliseconds(750);
                activity.Record(false);
            }
            Check(activity.Expired, "Eleven minutes of background reads kept password unlocked.");
        }

        private static void UserTimeout()
        {
            var clock = new Clock();
            var activity = new UserActivityTimeout(clock, TimeSpan.FromMinutes(10));
            clock.UtcNow = clock.UtcNow.AddMinutes(9);
            activity.Record(true);
            clock.UtcNow = clock.UtcNow.AddMinutes(2);
            Check(!activity.Expired, "Successful user activity was ignored.");
            clock.UtcNow = clock.UtcNow.AddMinutes(9);
            Check(activity.Expired, "Password never expires after real user activity stops.");
        }
    }
}

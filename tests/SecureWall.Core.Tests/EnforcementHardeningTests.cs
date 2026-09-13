using System;
using System.IO;
using System.Collections.Concurrent;
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
                yield return ("native enumeration failure is distinct from unchanged success", EnumerationDecision);
                yield return ("watcher drain releases service transition lock", WatcherLifetimeDrain);
                yield return ("superseded and failed watcher callbacks are rejected", WatcherLateCallbacks);
                yield return ("matched drops share one fresh snapshot per batch", CorrelatedBatchFreshness);
                yield return ("correlated queue capacity deadline and failure remain blocked", CorrelatedBatchBounds);
                yield return ("batch reset and shutdown revoke stale tokens", CorrelatedBatchInvalidation);
                yield return ("callbacks never wait for snapshots or policy transitions", CorrelatedBatchConcurrency);
                yield return ("configuration defaults require confirmed absence", ConfigurationAbsence);
                yield return ("environment reload errors revoke stale grants before escaping", EnvironmentalReloadFailure);
                yield return ("expired inactivity locks directly with a saturated worker queue", SaturatedQueueRelock);
                yield return ("application identity normalization preserves pseudo subjects and propagates errors", ApplicationNormalization);
                yield return ("unavailable volumes skip ordinary raw and incremental rules and recover on reload", UnavailableVolumes);
                yield return ("pending service uncertainty is scoped per candidate and refreshed per batch", PendingServiceScope);
                yield return ("policy save failure cannot publish or enforce proposed state", SaveFailure);
                yield return ("persistence faults before and after replacement always compensate", PersistenceSideEffects);
                yield return ("completion faults preserve prior recovery even after deletion", CompletionSideEffects);
                yield return ("WFP failure restores durable state and retains published policy", EnforcementFailure);
                yield return ("rollback failure withdraws runtime permits and cannot publish success", RecoveryFailure);
                yield return ("successful policy publishes only after save and WFP commit", SuccessfulPublication);
                yield return ("all runtime registrations avoid persistent and boot lifetimes", RuntimeLifetimes);
                yield return ("failed user deny aborts a partially registered replacement", DenyFailure);
                yield return ("failed permit cannot return allow success", PermitFailure);
                yield return ("recovery baseline cannot outrank promptable default deny", RecoveryPriority);
                yield return ("recovery deny rejects invalid priority", RecoveryWeightBounds);
                yield return ("bulk service snapshots bound calls and discard partial identity sets", BulkSnapshotCalls);
                yield return ("fresh snapshots preserve newly shared services and reject unstable identity", BulkSnapshotIdentity);
                yield return ("candidate and prompt saturation retain denial and exact overflow counts", OverflowDenial);
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

        private static void EnumerationDecision()
        {
            foreach (bool succeeded in new[] { false, true })
            foreach (bool changed in new[] { false, true })
            {
                bool read = false, revoked = false, installed = false;
                Func<bool> enumerate = () => EnvironmentalPolicyReload.EnumerationChanged(succeeded,
                    () => { read = true; return changed; });
                Action runtime = () => EnvironmentalPolicyReload.Run(() =>
                {
                    if (enumerate()) installed = true;
                }, () => revoked = true);
                if (succeeded) runtime(); else Throws<InvalidOperationException>(runtime);
                Check(read == succeeded && revoked == !succeeded && installed == (succeeded && changed),
                    "Enumeration failure was confused with an unchanged successful snapshot.");
                if (!succeeded) Throws<InvalidOperationException>(() => enumerate());
                else Check(enumerate() == changed, "Startup enumeration decision changed.");
            }
        }

        private static void WatcherLifetimeDrain()
        {
            var lifetime = new AuditWatcherLifetime();
            var sender = new object();
            Check(lifetime.Attach(sender), "Subscription attach failed.");
            object learning = new object();
            using var callbackEntered = new ManualResetEventSlim();
            using var drainEntered = new ManualResetEventSlim();
            using var callbackDone = new ManualResetEventSlim();
            Exception? callbackError = null, stopError = null;
            var callback = new Thread(() =>
            {
                try
                {
                    Check(lifetime.Accept(sender), "Initial callback rejected.");
                    callbackEntered.Set();
                    lock (learning) { }
                }
                catch (Exception error) { callbackError = error; }
                finally { callbackDone.Set(); }
            }) { IsBackground = true };
            var stopper = new Thread(() =>
            {
                try { lifetime.Stop(() => { }, () => { drainEntered.Set(); Check(callbackDone.Wait(3000), "Drain blocked callback."); }); }
                catch (Exception error) { stopError = error; }
            }) { IsBackground = true };
            lock (learning)
            {
                callback.Start();
                Check(callbackEntered.Wait(3000), "Callback did not enter.");
                stopper.Start();
                Check(drainEntered.Wait(3000), "Stop did not enter drain.");
                // Same lock order as a policy transition: learning then watcher lifecycle.
                bool entered = Monitor.TryEnter(lifetime.SyncRoot, 1000);
                try { Check(entered, "Callback drain retained the transition lock."); }
                finally { if (entered) Monitor.Exit(lifetime.SyncRoot); }
            }
            Check(callback.Join(3000) && stopper.Join(3000), "Lifecycle threads did not finish.");
            Check(callbackError == null && stopError == null, "Lifecycle drain failed: " + callbackError + stopError);
            Check(!lifetime.Accept(sender) && !lifetime.Fail(sender) && !lifetime.Attach(new object()),
                "Shutdown accepted a late callback/error or recreated a subscription.");
            int drains = 0;
            lifetime.Stop(() => throw new InvalidOperationException(), () => ++drains);
            Check(drains == 0, "Repeated shutdown drained twice.");
        }

        private static void WatcherLateCallbacks()
        {
            var old = new AuditWatcherLifetime();
            var current = new AuditWatcherLifetime();
            object first = new object(), second = new object();
            Check(old.Attach(first) && current.Attach(second), "Attach failed.");
            Check(!current.Accept(first) && !current.Fail(first) && current.Accept(second),
                "A superseded watcher delivered data or poisoned current health.");
            Check(current.Fail(second) && !current.Accept(second) && !current.Fail(second),
                "Failed subscription still delivered callbacks.");
            Check(!current.Attach(new object()), "Terminal failure silently retried.");
            old.Stop(() => { }, () => { });
            Check(!old.Accept(first) && !old.Fail(first), "Late disposed callback remained valid.");
        }

        private static (DropCandidate Candidate, BlockedConnectionAuditEvent Audit) Matched(Clock clock, int port = 1234, string? applicationPath = null)
        {
            var candidate = new DropCandidate(clock.UtcNow, 42, applicationPath ?? @"C:\Windows\System32\svchost.exe", null,
                "192.0.2.1", port, "203.0.113.1", 443, 6);
            var audit = new BlockedConnectionAuditEvent(clock.UtcNow, 77, candidate.ApplicationPath,
                ConnectionDirection.Outbound, candidate.LocalAddress, port, candidate.RemoteAddress, 443, 6, 42, null);
            return (candidate, audit);
        }

        private static void CorrelatedBatchFreshness()
        {
            var clock = new Clock();
            var queue = new PromptQueue(clock);
            var batch = new CorrelatedDropBatch(queue.SyncRoot, clock);
            var candidates = new DropCandidateBuffer(clock);
            var original = new Dictionary<int, (DropCandidate Candidate, BlockedConnectionAuditEvent Audit)>();
            for (int i = 0; i < 20; ++i)
            {
                var pair = Matched(clock, 1234 + i);
                original.Add(pair.Candidate.LocalPort, pair);
                Check(candidates.TryAdd(pair.Candidate) && candidates.TryMatch(pair.Audit, out var matched) &&
                    batch.TryAdd(matched!, pair.Audit), "Exact correlation did not reach batch.");
            }
            int snapshots = 0, prepared = 0;
            ServiceProcessEntry[] services = { new ServiceProcessEntry("first", 77, 4) };
            void Process() => batch.Process(() =>
            {
                ++snapshots;
                return ServiceProcessSnapshot.Index(ServiceProcessSnapshot.Read(_ => new ServiceSnapshotRead(true, services)));
            }, error => throw error, (candidate, audit, snapshot, unavailable) =>
            {
                ++prepared;
                Check(ReferenceEquals(original[candidate.LocalPort].Candidate, candidate) &&
                    ReferenceEquals(original[candidate.LocalPort].Audit, audit), "Original correlation evidence was replaced.");
                var identity = ServiceAttribution.Resolve(candidate, audit, snapshot.Services[audit.ProcessId],
                    false, unavailable || snapshot.IsUncertain(audit.ProcessId));
                return () => queue.Enqueue(identity, candidate.RemoteAddress, candidate.RemotePort, candidate.Protocol);
            });
            Process();
            Check(snapshots == 1 && prepared == 20 && queue.GetPending().Single().OccurrenceCount == 20,
                "N matched drops did not share exactly one fresh batch snapshot.");
            var first = queue.GetPending().Single();
            Check(first.Identity.Kind == PromptIdentityKind.Service, "Single service lost exact identity.");
            queue.Clear();
            services = new[] { new ServiceProcessEntry("replacement", 77, 4), new ServiceProcessEntry("shared", 77, 4) };
            var next = Matched(clock, 3333); original.Add(3333, next);
            Check(batch.TryAdd(next.Candidate, next.Audit), "Later batch rejected.");
            Process(); Process();
            var later = queue.GetPending().Single();
            Check(snapshots == 2 && later.Identity.Kind == PromptIdentityKind.AmbiguousService &&
                queue.Allow(later.Token).Status == PromptActionStatus.NotAllowable,
                "Later batch reused old service identity or allowed a shared host.");
        }

        private static void CorrelatedBatchBounds()
        {
            var clock = new Clock(); var batch = new CorrelatedDropBatch(new object(), clock, 2);
            var pair = Matched(clock);
            var wrong = Matched(clock, 9999);
            Check(!batch.TryAdd(pair.Candidate, wrong.Audit), "Nonmatching audit was queued.");
            Check(batch.TryAdd(pair.Candidate, pair.Audit) && batch.TryAdd(pair.Candidate, pair.Audit), "Capacity shrank.");
            for (int i = 0; i < 10; ++i) Check(!batch.TryAdd(pair.Candidate, pair.Audit), "Overflow accepted.");
            int published = 0, snapshots = 0;
            batch.Process(() => { ++snapshots; return 0; }, _ => { }, (c, a, s, u) => () => ++published);
            Check(published == 2 && snapshots == 1 && batch.Suppressed.Total == 10, "Capacity or overflow accounting failed.");
            Check(batch.Suppressed.TryReport(clock.UtcNow, out long count) && count == 10 &&
                !batch.Suppressed.TryReport(clock.UtcNow, out _), "Overflow is hidden or uncoalesced.");
            Check(batch.TryAdd(pair.Candidate, pair.Audit), "Next batch rejected.");
            batch.Process(() => 0, _ => { }, (c, a, s, u) =>
            {
                clock.UtcNow = clock.UtcNow.AddSeconds(3); // Slow catalog/SCM cannot mint a late token.
                return () => ++published;
            });
            Check(published == 2 && batch.Suppressed.Total == 11, "Deadline admitted stale work.");
            Check(batch.TryAdd(pair.Candidate, pair.Audit), "Failure batch rejected.");
            bool failed = false;
            batch.Process<int>(() => throw new IOException(), _ => failed = true, (c, a, s, u) =>
            {
                Check(u && ServiceAttribution.Resolve(c, a, Array.Empty<string>(), false, u).Kind == PromptIdentityKind.AmbiguousService,
                    "Failed snapshot became an executable-wide grant.");
                return () => { };
            });
            Check(failed, "Snapshot failure was hidden.");
        }

        private static void CorrelatedBatchInvalidation()
        {
            var applyingQueue = new PromptQueue(new Clock());
            Guid applyingToken = applyingQueue.Enqueue(PromptIdentity.ForExecutable(@"C:\apply.exe"), "203.0.113.1", 443, 6).Token;
            Check(applyingQueue.Allow(applyingToken, _ => { applyingQueue.Clear(); return true; }).Status == PromptActionStatus.Allowed &&
                applyingQueue.GetPending().Count == 0, "Policy replacement during Allow corrupted its captured queue entry.");
            foreach (bool stop in new[] { false, true })
            foreach (bool duringPreparation in new[] { false, true })
            {
                var clock = new Clock(); var queue = new PromptQueue(clock);
                var batch = new CorrelatedDropBatch(queue.SyncRoot, clock);
                var pair = Matched(clock);
                Guid prior = queue.Enqueue(PromptIdentity.ForExecutable(@"C:\old.exe"), "203.0.113.1", 443, 6).Token;
                batch.TryAdd(pair.Candidate, pair.Audit);
                void Reset() { lock (queue.SyncRoot) { batch.Reset(stop); queue.Clear(); } }
                batch.Process(() => { if (!duringPreparation) Reset(); return 0; }, _ => { }, (c, a, s, u) =>
                {
                    if (duringPreparation) Reset();
                    return () => queue.Enqueue(PromptIdentity.ForService(c.ApplicationPath, "stale"), c.RemoteAddress, c.RemotePort, c.Protocol);
                });
                Check(queue.GetPending().Count == 0 && queue.Allow(prior).Status == PromptActionStatus.UnknownToken,
                    "Mode change/shutdown retained an actionable old token.");
                Check(batch.TryAdd(pair.Candidate, pair.Audit) == !stop, "Shutdown recreated queued work or reset stopped forever.");
                if (stop) { batch.Reset(); Check(!batch.TryAccept(() => throw new InvalidOperationException()), "Stop was reversible."); }
            }
        }

        private static void CorrelatedBatchConcurrency()
        {
            var clock = new Clock(); object guard = new object();
            var batch = new CorrelatedDropBatch(guard, clock); var pair = Matched(clock);
            using var snapshotEntered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            Exception? error = null; int snapshots = 0, published = 0;
            batch.TryAdd(pair.Candidate, pair.Audit);
            var worker = new Thread(() =>
            {
                try { batch.Process(() => { ++snapshots; snapshotEntered.Set(); release.Wait(3000); return 0; },
                    e => throw e, (c, a, s, u) => () => ++published); }
                catch (Exception e) { error = e; }
            }) { IsBackground = true };
            worker.Start();
            try
            {
                Check(snapshotEntered.Wait(3000), "Snapshot never started.");
                Check(batch.TryAccept(() => Check(batch.TryAdd(pair.Candidate, pair.Audit), "Enqueue while snapshot blocked failed.")),
                    "Callback waited behind SCM snapshot.");
                batch.Process(() => { ++snapshots; return 0; }, _ => { }, (c, a, s, u) => () => ++published);
                Check(snapshots == 1, "Overlapping timer pass enumerated SCM.");
                lock (guard)
                {
                    bool accepted = true;
                    var callback = new Thread(() => accepted = batch.TryAccept(() => { })) { IsBackground = true };
                    callback.Start();
                    Check(callback.Join(1000) && !accepted, "Callback blocked on policy transition.");
                }
                batch.Reset(stop: true);
            }
            finally { release.Set(); Check(worker.Join(3000), "Snapshot worker did not finish."); }
            Check(error == null && published == 0 && batch.Suppressed.Total == 0 && batch.Contention.Total == 1,
                "Shutdown raced publication or contention suppression was lost: " + error);
        }

        private static void ConfigurationAbsence()
        {
            foreach (Exception missing in new Exception[] { new FileNotFoundException(), new DirectoryNotFoundException() })
                Check(EnforcementPolicy.LoadConfiguration<int>(() => throw missing,
                    () => throw new InvalidOperationException("Unexpected load"), () => 7) == 7, "Missing configuration rejected.");
            foreach (bool probeFailure in new[] { true, false })
            foreach (Exception error in new Exception[] { new UnauthorizedAccessException(), new IOException(), new FormatException(), new FileNotFoundException() })
            {
                if (probeFailure && error is FileNotFoundException) continue;
                bool defaults = false;
                Throws<Exception>(() => EnforcementPolicy.LoadConfiguration<int>(
                    () => { if (probeFailure) throw error; }, () => throw error,
                    () => { defaults = true; return 7; }));
                Check(!defaults, "Uncertain existing configuration was replaced with defaults.");
            }
            Check(EnforcementPolicy.LoadConfiguration(() => { }, () => 9, () => 7) == 9, "Existing configuration changed.");
        }

        private static void EnvironmentalReloadFailure()
        {
            foreach (string boundary in new[] { "enumeration", "assembly", "registration", "commit" })
            {
                bool grants = true, response = false;
                Throws<InvalidOperationException>(() =>
                {
                    EnvironmentalPolicyReload.Run(() => throw new InvalidOperationException(boundary), () => grants = false);
                    response = true;
                });
                Check(!grants && !response, boundary + " retained stale environment grants.");
            }
            bool installed = false;
            EnvironmentalPolicyReload.Run(() => installed = true, () => throw new InvalidOperationException("Unexpected withdrawal"));
            Check(installed, "Successful reload skipped.");
        }

        private static void SaturatedQueueRelock()
        {
            using var queue = new BlockingCollection<int>(1);
            queue.Add(1);
            var clock = new Clock();
            var activity = new UserActivityTimeout(clock, TimeSpan.FromMinutes(10));
            int locks = 0;
            activity.LockIfExpired(() => ++locks);
            Check(locks == 0, "Active controller locked early.");
            clock.UtcNow = clock.UtcNow.AddMinutes(11);
            activity.LockIfExpired(() => ++locks);
            Check(locks == 1 && queue.Count == 1, "Relock depended on queue capacity.");
        }

        private static void ApplicationNormalization()
        {
            foreach (string? subject in new string?[] { null, "", "System", "system", "Registry" })
                Check(EnforcementPolicy.NormalizeApplicationPath(subject, _ => throw new InvalidOperationException()) == subject,
                    "Pseudo subject was mapped.");
            foreach (string path in new[] { @"C:\app.exe", @"\\server\share\app.exe", @"\Device\HarddiskVolume1\app.exe" })
            {
                Check(EnforcementPolicy.NormalizeApplicationPath(path, value => "native:" + value) == "native:" + path,
                    "Application bypassed normalization.");
                Throws<IOException>(() => EnforcementPolicy.NormalizeApplicationPath(path, _ => throw new IOException("Unmapped drive")));
            }
        }

        private sealed class ApplicationRule
        {
            internal string? Application;
            internal ApplicationRule(string? application) { Application = application; }
        }

        private static void UnavailableVolumes()
        {
            foreach (string boundary in new[] { "ordinary", "raw", "incremental" })
            foreach (string missing in new[] { @"Z:\portable\app.exe", @"\\?\Z:\app.exe",
                @"\\?\Volume{26a21bda-a627-11d7-9931-806e6f6e6963}\app.exe" })
            {
                var saved = new[] { new ApplicationRule(missing), new ApplicationRule(@"C:\good.exe"), new ApplicationRule(null) };
                int reports = 0;
                List<ApplicationRule> Normalize(bool mounted) => EnforcementPolicy.NormalizeRules(saved,
                    rule => rule.Application, (rule, path) => rule.Application = path,
                    path => path == missing && !mounted ? throw new DriveNotFoundException() : @"\Device\Resolved\app.exe",
                    path => { Check(path == missing, "Wrong unavailable diagnostic subject."); ++reports; });
                var active = Normalize(false);
                Check(active.Count == 2 && !active.Contains(saved[0]) && saved[0].Application == missing &&
                    saved.Length == 3 && reports == 1, boundary + " lost saved rule or installed unresolved wildcard.");
                active = Normalize(true);
                Check(active.Count == 3 && active.Contains(saved[0]) && saved[0].Application == @"\Device\Resolved\app.exe" &&
                    reports == 1, boundary + " did not retry a newly available volume.");
            }
            foreach (Exception failure in new Exception[] { new UnauthorizedAccessException(), new IOException(),
                new NotSupportedException(), new ArgumentException(), new FormatException(), new InvalidOperationException() })
            {
                bool revoked = false, reported = false;
                Exception? observed = null;
                try { EnvironmentalPolicyReload.Run(() => EnforcementPolicy.NormalizeRules(
                    new[] { new ApplicationRule(@"Z:\app.exe") }, rule => rule.Application,
                    (rule, path) => rule.Application = path, _ => throw failure,
                    _ => reported = true), () => revoked = true); }
                catch (Exception error) { observed = error; }
                Check(revoked && !reported && ReferenceEquals(observed, failure),
                    "Mapping failure did not reach fail-closed reload boundary unchanged.");
            }
            foreach (string malformed in new[] { @"Z:app.exe", @"Z:\bad|app.exe", @"Z:\bad:app.exe",
                @"\\?\Volume{invalid}\app.exe", @"\\server\share\app.exe", @"\Device\Unknown\app.exe" })
                Throws<DriveNotFoundException>(() => EnforcementPolicy.NormalizeRules(new[] { new ApplicationRule(malformed) },
                    rule => rule.Application, (rule, path) => rule.Application = path, _ => throw new DriveNotFoundException(),
                    _ => throw new InvalidOperationException("Invalid subject became dormant.")));
        }

        private static void PendingServiceScope()
        {
            var clock = new Clock(); var pair = Matched(clock, applicationPath: @"C:\Apps\browser.exe");
            var host = Matched(clock);
            Check(ServiceAttribution.Resolve(host.Candidate, host.Audit, Array.Empty<string>()).Kind ==
                PromptIdentityKind.AmbiguousService, "An unattributed service host became an ordinary executable.");
            foreach (uint state in new[] { 2U, 3U })
            foreach (uint pendingPid in new[] { 99U, 77U, 0U })
            foreach (bool stableTarget in new[] { false, true })
            {
                var entries = new List<ServiceProcessEntry> { new("pending", pendingPid, state) };
                if (stableTarget) entries.Add(new("stable", 77, 4));
                var index = ServiceProcessSnapshot.Index(entries);
                var names = index.Services.TryGetValue(77, out var found) ? found : new HashSet<string>();
                // A negative minute-cached catalog must never override current uncertainty.
                var identity = ServiceAttribution.Resolve(pair.Candidate, pair.Audit, names,
                    executableIsRegisteredService: false, snapshotUncertain: index.IsUncertain(77));
                var expected = pendingPid != 99 ? PromptIdentityKind.AmbiguousService :
                    stableTarget ? PromptIdentityKind.Service : PromptIdentityKind.Executable;
                Check(identity.Kind == expected, "Pending identity poisoned unrelated PID or widened an uncertain grant.");
                var queue = new PromptQueue(clock);
                var token = queue.Enqueue(identity, pair.Candidate.RemoteAddress, 443, 6).Token;
                if (pendingPid != 99)
                    Check(queue.Allow(token).Status == PromptActionStatus.NotAllowable, "Uncertain target was allowable.");
            }
            foreach (var invalid in new[] {
                new[] { new ServiceProcessEntry("", 99, 2) },
                new[] { new ServiceProcessEntry("bad", 99, 8) },
                new[] { new ServiceProcessEntry("bad", 0, 4) },
                new[] { new ServiceProcessEntry("stopped", 77, 1) },
                new[] { new ServiceProcessEntry("same", 77, 4), new ServiceProcessEntry("SAME", 99, 2) } })
                Throws<InvalidOperationException>(() => ServiceProcessSnapshot.Index(invalid));
            Throws<InvalidOperationException>(() => ServiceProcessSnapshot.Index(null!));

            var batch = new CorrelatedDropBatch(new object(), clock);
            int reads = 0;
            var observed = new List<PromptIdentityKind>();
            for (int pass = 0; pass < 4; ++pass)
            {
                Check(batch.TryAdd(pair.Candidate, pair.Audit), "Later batch admission failed.");
                batch.Process(() => ServiceProcessSnapshot.Index(new[] {
                    new ServiceProcessEntry("stable", 77, 4),
                    new ServiceProcessEntry("changing", ++reads == 1 || reads == 4 ? 99U : 77U, reads == 3 ? 4U : 2U) }),
                    error => throw error, (candidate, audit, snapshot, unavailable) =>
                    {
                        var identity = ServiceAttribution.Resolve(candidate, audit, snapshot.Services[audit.ProcessId],
                            false, unavailable || snapshot.IsUncertain(audit.ProcessId));
                        return () => observed.Add(identity.Kind);
                    });
            }
            Check(reads == 4 && observed.SequenceEqual(new[] { PromptIdentityKind.Service,
                PromptIdentityKind.AmbiguousService, PromptIdentityKind.AmbiguousService, PromptIdentityKind.Service }),
                "Later pending/shared batch reused earlier service authority.");
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
            Check(oldVisible == 7 && enforcement == 7 && durable == 7 && restored && !failedClosed,
                "A failed save changed policy or published success.");
        }

        private static void PersistenceSideEffects()
        {
            foreach (bool replaced in new[] { false, true })
            foreach (string compensation in new[] { "success", "before replacement", "after replacement" })
            {
                const string prior = "prior policy";
                string disk = prior, snapshot = "";
                bool marker = false, grants = true, enforced = false, published = false;
                var events = new List<string>();
                var saveError = new IOException("save failed");
                var restoreError = new IOException("restore failed");
                Exception? caught = null;
                try
                {
                    PolicyChangeTransaction.Apply(
                        () => { events.Add("persist"); if (replaced) disk = "candidate"; throw saveError; },
                        () => enforced = true,
                        () =>
                        {
                            events.Add("restore");
                            if (compensation == "before replacement") throw restoreError;
                            disk = prior;
                            if (compensation == "after replacement") throw restoreError;
                        },
                        () => published = true, () => grants = false,
                        () => { snapshot = prior; marker = true; },
                        () => { events.Add("clear"); marker = false; });
                }
                catch (Exception error) { caught = error; }
                Check(!enforced && !published && snapshot == prior, "Failed save enforced or published a candidate.");
                if (compensation == "success")
                    Check(ReferenceEquals(caught, saveError) && disk == prior && !marker && grants &&
                        events.SequenceEqual(new[] { "persist", "restore", "clear" }), "Successful compensation was skipped or cleared early.");
                else
                {
                    Check(caught is AggregateException aggregate && aggregate.InnerExceptions.SequenceEqual(new[] { saveError, restoreError }) &&
                        marker && !grants && events.SequenceEqual(new[] { "persist", "restore" }), "Failed compensation lost recovery or its errors.");
                    PolicyRecoveryJournal.Recover(marker, () => disk = snapshot, () => marker = false);
                    Check(disk == prior && !marker, "Restart recovered a failed candidate.");
                }
            }
        }

        private static void CompletionSideEffects()
        {
            foreach (bool deleted in new[] { false, true })
            foreach (string preparation in new[] { "success", "before replacement", "after replacement" })
            foreach (bool restoreFails in new[] { false, true })
            {
                const string prior = "prior policy";
                string disk = prior, snapshot = "";
                bool marker = false, grants = true, published = false, restored = false;
                int preparations = 0, clears = 0;
                var completionError = new IOException("completion failed");
                var prepareError = new IOException("snapshot retry failed");
                var restoreError = new IOException("restore failed");
                Exception? caught = null;
                try
                {
                    PolicyChangeTransaction.Apply(() => disk = "candidate", () => grants = true,
                        () =>
                        {
                            Check(!grants, "Committed grants remained active during recovery.");
                            restored = true;
                            if (restoreFails) throw restoreError;
                            disk = prior;
                        }, () => published = true, () => grants = false,
                        () =>
                        {
                            ++preparations;
                            if (preparations == 2 && preparation == "before replacement") throw prepareError;
                            snapshot = prior;
                            marker = true;
                            if (preparations == 2 && preparation == "after replacement") throw prepareError;
                        },
                        () => { ++clears; if (deleted) marker = false; throw completionError; });
                }
                catch (Exception error) { caught = error; }
                Check(!grants && !published && restored && preparations == 2 && clears == 1 && snapshot == prior,
                    "Completion failure skipped revocation/recovery, retried deletion, or replaced the prior snapshot with a candidate.");
                var expectedErrors = new List<Exception> { completionError };
                if (preparation != "success") expectedErrors.Add(prepareError);
                if (restoreFails) expectedErrors.Add(restoreError);
                Check(expectedErrors.Count == 1 ? ReferenceEquals(caught, completionError) :
                    caught is AggregateException aggregate && aggregate.InnerExceptions.SequenceEqual(expectedErrors), "Recovery errors were lost.");
                Check(marker == !(deleted && preparation == "before replacement"), "Snapshot retention did not match storage side effects.");
                if (!restoreFails) Check(disk == prior, "Completion failure left the candidate on disk despite successful compensation.");
                if (marker)
                {
                    PolicyRecoveryJournal.Recover(marker, () => disk = snapshot, () => marker = false);
                    Check(disk == prior, "Completion failure made candidate authoritative at restart.");
                }
                // If deletion happened and both snapshot recreation and restoration fail,
                // storage may still contain the candidate. Revocation is confirmed; recovery is not.
            }
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
                () => events.Add("restore"), () => events.Add("publish"), () => events.Add("fail closed"),
                () => events.Add("prepare"), () => events.Add("complete"));
            Check(events.SequenceEqual(new[] { "prepare", "persist", "commit", "complete", "publish" }), "Incorrect publication order.");
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

        private static void RecoveryWeightBounds()
        {
            Throws<ArgumentOutOfRangeException>(() => EnforcementPolicy.RecoveryBlockWeight(0));
            Throws<ArgumentOutOfRangeException>(() => EnforcementPolicy.RecoveryBlockWeight(1));
            foreach (ulong runtime in new[] { 2UL, 3000000UL, ulong.MaxValue })
                Check(EnforcementPolicy.RecoveryBlockWeight(runtime) < runtime,
                    "Baseline denial must leave runtime default-block prompt authority intact.");
        }

        private static void BulkSnapshotCalls()
        {
            var entries = Enumerable.Range(1, 1000).Select(i => new ServiceProcessEntry("service" + i, (uint)i, 4)).ToArray();
            int calls = 0;
            var complete = ServiceProcessSnapshot.Read(size =>
            {
                ++calls;
                Check(size == 64 * 1024, "Unexpected initial allocation.");
                return new ServiceSnapshotRead(true, entries);
            });
            Check(calls == 1 && ServiceProcessSnapshot.Index(complete).Services.Count == 1000,
                "Service count increased native call count.");
            calls = 0;
            var retried = ServiceProcessSnapshot.Read(size =>
            {
                ++calls;
                return size == 64 * 1024
                    ? new ServiceSnapshotRead(false, new[] { new ServiceProcessEntry("partial", 1, 4) })
                    : new ServiceSnapshotRead(true, entries);
            });
            Check(calls == 2 && ReferenceEquals(retried, entries), "Partial pages leaked into final snapshot.");
            calls = 0;
            Throws<InvalidOperationException>(() => ServiceProcessSnapshot.Read(size =>
            {
                ++calls;
                return new ServiceSnapshotRead(false, entries);
            }));
            Check(calls == 2, "Incomplete SCM enumeration retries are unbounded.");
            calls = 0;
            Throws<IOException>(() => ServiceProcessSnapshot.Read(size => { ++calls; throw new IOException("SCM failure"); }));
            Check(calls == 1, "Native failure was hidden or retried.");
        }

        private static void OverflowDenial()
        {
            var clock = new Clock();
            var candidates = new DropCandidateBuffer(clock, 1, TimeSpan.FromSeconds(1));
            var candidateErrors = new CoalescedDiagnostic();
            var candidate = new DropCandidate(clock.UtcNow, 42, @"C:\apps\app.exe", null,
                "192.0.2.1", 1234, "203.0.113.1", 443, 6);
            Check(candidates.TryAdd(candidate), "First candidate missing.");
            for (int i = 0; i < 100; ++i)
                if (!candidates.TryAdd(candidate)) candidateErrors.Record();
            Check(candidateErrors.Total == 100, "Candidate overflow count lost.");
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            Check(candidates.DrainReady().Count == 1, "Candidate overflow expanded the bounded queue.");

            var queue = new PromptQueue(clock, 1, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(5), Guid.NewGuid);
            var promptErrors = new CoalescedDiagnostic();
            queue.Enqueue(PromptIdentity.ForExecutable(@"C:\apps\first.exe"), "203.0.113.1", 443, 6);
            for (int i = 0; i < 100; ++i)
            {
                var result = queue.Enqueue(PromptIdentity.ForExecutable(@"C:\apps\other.exe"), "203.0.113.1", 443, 6);
                Check(result.Status == PromptEnqueueStatus.CapacityReached && result.Token == Guid.Empty,
                    "Overflow minted a usable token.");
                if (result.Status == PromptEnqueueStatus.CapacityReached) promptErrors.Record();
            }
            bool applied = false;
            queue.Allow(Guid.Empty, _ => { applied = true; return true; });
            Check(promptErrors.Total == 100 && queue.GetPending().Count == 1 && !applied,
                "Overflow altered capacity or reached allow enforcement.");
        }

        private static void BulkSnapshotIdentity()
        {
            var first = new[] { new ServiceProcessEntry("first", 42, 4) };
            var shared = new[] { first[0], new ServiceProcessEntry("newly shared", 42, 7), new ServiceProcessEntry("FIRST", 42, 4) };
            int calls = 0;
            Func<int, ServiceSnapshotRead> read = _ => new ServiceSnapshotRead(true, ++calls == 1 ? first : shared);
            Check(ServiceProcessSnapshot.Index(ServiceProcessSnapshot.Read(read)).Services[42].Count == 1, "Initial identity missing.");
            var current = ServiceProcessSnapshot.Index(ServiceProcessSnapshot.Read(read));
            Check(calls == 2 && current.Services[42].Count == 2, "Newly shared or paused service was omitted or cached.");
            foreach (uint pending in new[] { 2U, 3U })
            {
                var unstable = new[] { first[0], new ServiceProcessEntry("pending", 0, pending) };
                Check(ServiceProcessSnapshot.Index(unstable).IsUncertain(42), "Unknown pending PID lost global uncertainty.");
                Check(ServiceProcessSnapshot.Index(unstable, false).Services[42].Count == 1, "Display-only map broke on transitional services.");
            }
            Throws<InvalidOperationException>(() => ServiceProcessSnapshot.Index(new[] { new ServiceProcessEntry("invalid", 0, 4) }));
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
                () => grants = false, () => { snapshot = "old"; marker = true; },
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

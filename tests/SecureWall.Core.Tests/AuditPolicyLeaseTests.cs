using System;
using System.Collections.Generic;
using System.Linq;
using pylorak.TinyWall;

namespace SecureWall.Core.Tests
{
    internal static class AuditPolicyLeaseTests
    {
        internal static IEnumerable<(string Name, Action Test)> Cases
        {
            get
            {
                yield return ("audit lease journals the original before the backend is set", AcquireJournalsBeforeSet);
                yield return ("audit lease dispose restores the original then clears the journal", DisposeRestoresThenClears);
                yield return ("audit lease leaves the journal in place when the backend set fails", SetFailureLeavesJournal);
                yield return ("audit lease restores a stale journal entry and uses it as the original", StaleJournalRestoredOnAcquire);
                yield return ("audit lease keeps a stale journal entry when its restore fails", StaleJournalKeptWhenRestoreFails);
                yield return ("audit lease does not journal when nothing changes", NoChangeWritesNoJournal);
                yield return ("nested audit leases share one journal entry and unwind in order", NestedLeasesShareJournalEntry);
                yield return ("audit journal restore puts back every entry and clears each", RestoreFromJournalRestoresAll);
                yield return ("audit journal restore with an empty journal touches nothing", RestoreFromJournalEmptyIsNoOp);
                yield return ("audit journal restore keeps a failed entry and restores the rest", RestoreFromJournalKeepsFailedEntry);
                yield return ("nested audit lease journals the true original when only it changes policy", InnerLeaseJournalsTrueOriginal);
                yield return ("audit journal restore recovers a change made only by a nested lease", RestoreFromJournalAfterInnerOnlyChange);
                yield return ("audit journal restore skips a malformed entry and restores the rest", RestoreFromJournalSkipsMalformedEntry);
                yield return ("nested audit lease retries the journal write after a failed write", NestedLeaseRetriesJournalWriteAfterFailure);
            }
        }

        // Journal and backend append to one shared trace so tests can assert
        // ordering across the two ("write" before "set", "set" before "clear").
        internal sealed class InMemoryJournal : IAuditPolicyJournal
        {
            private readonly List<string> _trace;

            internal InMemoryJournal() : this(new List<string>()) { }

            internal InMemoryJournal(List<string> trace) => _trace = trace;

            internal Dictionary<Guid, AuditPolicyFlags> Entries { get; } = new();

            // Names of records the registry journal would fail to decode.
            internal List<string> Malformed { get; } = new();

            public bool TryRead(Guid subcategory, out AuditPolicyFlags original) => Entries.TryGetValue(subcategory, out original);

            // Number of upcoming Write calls that throw before writes succeed again.
            internal int FailNextWrites { get; set; }

            public void Write(Guid subcategory, AuditPolicyFlags original)
            {
                if (FailNextWrites > 0)
                {
                    FailNextWrites--;
                    _trace.Add("write-failed");
                    throw new InvalidOperationException("Journal write failed.");
                }
                Entries[subcategory] = original;
                _trace.Add("write");
            }

            public void Clear(Guid subcategory)
            {
                Entries.Remove(subcategory);
                _trace.Add("clear");
            }

            public IReadOnlyList<KeyValuePair<Guid, AuditPolicyFlags>> ReadAll(out IReadOnlyList<string> malformed)
            {
                malformed = Malformed.ToList();
                return Entries.OrderBy(e => e.Key).ToList();
            }
        }

        private sealed class FakeBackend : IAuditPolicyBackend
        {
            private readonly Dictionary<Guid, AuditPolicyFlags> _policy = new();
            private readonly List<string> _trace;

            internal FakeBackend(List<string> trace) => _trace = trace;

            internal HashSet<Guid> FailSetFor { get; } = new();

            internal AuditPolicyFlags this[Guid subcategory]
            {
                get => _policy[subcategory];
                set => _policy[subcategory] = value;
            }

            public AuditPolicyFlags Query(Guid subcategory) => _policy[subcategory];

            public void Set(Guid subcategory, AuditPolicyFlags flags)
            {
                if (FailSetFor.Contains(subcategory))
                    throw new InvalidOperationException("AuditSetSystemPolicy failed.");
                _policy[subcategory] = flags;
                _trace.Add("set");
            }
        }

        private static (Guid Subcategory, FakeBackend Backend, InMemoryJournal Journal, List<string> Trace) Harness(AuditPolicyFlags live)
        {
            var trace = new List<string>();
            var subcategory = Guid.NewGuid();
            var backend = new FakeBackend(trace) { [subcategory] = live };
            return (subcategory, backend, new InMemoryJournal(trace), trace);
        }

        private static void AcquireJournalsBeforeSet()
        {
            var (subcategory, backend, journal, trace) = Harness(AuditPolicyFlags.Success);

            using var lease = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Failure, journal);

            AssertEx.SequenceEqual(new[] { "write", "set" }, trace);
            AssertEx.Equal(AuditPolicyFlags.Success, journal.Entries[subcategory]);
            AssertEx.Equal(AuditPolicyFlags.Success | AuditPolicyFlags.Failure, backend[subcategory]);
        }

        private static void DisposeRestoresThenClears()
        {
            var (subcategory, backend, journal, trace) = Harness(AuditPolicyFlags.None);
            var lease = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Failure, journal);
            trace.Clear();

            lease.Dispose();
            lease.Dispose();

            AssertEx.SequenceEqual(new[] { "set", "clear" }, trace);
            AssertEx.Equal(AuditPolicyFlags.None, backend[subcategory]);
            AssertEx.Equal(0, journal.Entries.Count);
        }

        private static void SetFailureLeavesJournal()
        {
            var (subcategory, backend, journal, trace) = Harness(AuditPolicyFlags.Unchanged);
            backend.FailSetFor.Add(subcategory);

            AssertEx.Throws<InvalidOperationException>(() =>
                AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Failure, journal));

            AssertEx.SequenceEqual(new[] { "write" }, trace);
            AssertEx.Equal(AuditPolicyFlags.Unchanged, journal.Entries[subcategory]);
            AssertEx.Equal(AuditPolicyFlags.Unchanged, backend[subcategory]);

            // The failed acquire must not count as a live lease: a later acquire
            // treats the record as stale and restores it.
            backend.FailSetFor.Remove(subcategory);
            backend[subcategory] = AuditPolicyFlags.Failure;
            trace.Clear();
            using var lease = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Failure, journal);
            AssertEx.Equal("set", trace[0]);
            AssertEx.Equal(AuditPolicyFlags.Unchanged, journal.Entries[subcategory]);
        }

        private static void StaleJournalRestoredOnAcquire()
        {
            // Previous run journaled Success, set Success|Failure, then died.
            var (subcategory, backend, journal, trace) = Harness(AuditPolicyFlags.Success | AuditPolicyFlags.Failure);
            journal.Entries[subcategory] = AuditPolicyFlags.Success;

            var lease = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Failure, journal);

            AssertEx.SequenceEqual(new[] { "set", "clear", "write", "set" }, trace);
            AssertEx.Equal(AuditPolicyFlags.Success, journal.Entries[subcategory]);
            AssertEx.Equal(AuditPolicyFlags.Success | AuditPolicyFlags.Failure, backend[subcategory]);

            lease.Dispose();
            AssertEx.Equal(AuditPolicyFlags.Success, backend[subcategory], "original comes from the journal, not the modified live value");
            AssertEx.Equal(0, journal.Entries.Count);
        }

        private static void StaleJournalKeptWhenRestoreFails()
        {
            var (subcategory, backend, journal, trace) = Harness(AuditPolicyFlags.Failure);
            journal.Entries[subcategory] = AuditPolicyFlags.Unchanged;
            backend.FailSetFor.Add(subcategory);

            AssertEx.Throws<InvalidOperationException>(() =>
                AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Failure, journal));

            AssertEx.Equal(0, trace.Count);
            AssertEx.Equal(AuditPolicyFlags.Unchanged, journal.Entries[subcategory]);
        }

        private static void NoChangeWritesNoJournal()
        {
            var (subcategory, backend, journal, trace) = Harness(AuditPolicyFlags.Failure);

            var lease = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Failure, journal);
            lease.Dispose();

            AssertEx.Equal(0, trace.Count);
            AssertEx.Equal(0, journal.Entries.Count);
        }

        private static void NestedLeasesShareJournalEntry()
        {
            var (subcategory, backend, journal, trace) = Harness(AuditPolicyFlags.Unchanged);

            var failure = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Failure, journal);
            var learning = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Success, journal);

            AssertEx.SequenceEqual(new[] { "write", "set", "set" }, trace, "inner lease must not re-journal or treat the outer record as stale");
            AssertEx.Equal(AuditPolicyFlags.Unchanged, journal.Entries[subcategory]);
            AssertEx.Equal(AuditPolicyFlags.Success | AuditPolicyFlags.Failure, backend[subcategory]);

            trace.Clear();
            learning.Dispose();
            AssertEx.SequenceEqual(new[] { "set" }, trace, "inner dispose restores the outer lease's value and keeps the journal");
            AssertEx.Equal(AuditPolicyFlags.Failure, backend[subcategory]);
            AssertEx.Equal(AuditPolicyFlags.Unchanged, journal.Entries[subcategory]);

            trace.Clear();
            failure.Dispose();
            AssertEx.SequenceEqual(new[] { "set", "clear" }, trace);
            AssertEx.Equal(AuditPolicyFlags.Unchanged, backend[subcategory]);
            AssertEx.Equal(0, journal.Entries.Count);
        }

        // Machine already audits Failure: the outer failure lease changes nothing,
        // so the inner learning lease is the first to change policy and must journal
        // the true original (Failure), not the value it found.
        private static void InnerLeaseJournalsTrueOriginal()
        {
            var (subcategory, backend, journal, trace) = Harness(AuditPolicyFlags.Failure);

            var failure = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Failure, journal);
            AssertEx.Equal(0, trace.Count, "outer lease finds Failure already on and changes nothing");
            AssertEx.Equal(0, journal.Entries.Count);

            var learning = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Success, journal);
            AssertEx.SequenceEqual(new[] { "write", "set" }, trace, "inner lease journals before it sets");
            AssertEx.Equal(AuditPolicyFlags.Failure, journal.Entries[subcategory]);
            AssertEx.Equal(AuditPolicyFlags.Success | AuditPolicyFlags.Failure, backend[subcategory]);

            trace.Clear();
            learning.Dispose();
            AssertEx.SequenceEqual(new[] { "set", "clear" }, trace, "restoring Failure reaches the true original, so the record clears");
            AssertEx.Equal(AuditPolicyFlags.Failure, backend[subcategory]);
            AssertEx.Equal(0, journal.Entries.Count);

            trace.Clear();
            failure.Dispose();
            AssertEx.Equal(0, trace.Count, "outer lease changed nothing and has nothing to restore or clear");
            AssertEx.Equal(AuditPolicyFlags.Failure, backend[subcategory]);
        }

        // A nested lease whose journal write throws must not leave the subcategory marked
        // as journaled: the next nested acquire has to write the record again.
        private static void NestedLeaseRetriesJournalWriteAfterFailure()
        {
            var (subcategory, backend, journal, trace) = Harness(AuditPolicyFlags.Failure);

            using var failure = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Failure, journal);
            AssertEx.Equal(0, trace.Count, "outer lease changes nothing");

            journal.FailNextWrites = 1;
            AssertEx.Throws<InvalidOperationException>(() =>
                AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Success, journal));
            AssertEx.SequenceEqual(new[] { "write-failed" }, trace, "the backend is untouched when the journal write fails");
            AssertEx.Equal(0, journal.Entries.Count);
            AssertEx.Equal(AuditPolicyFlags.Failure, backend[subcategory]);

            trace.Clear();
            using var learning = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Success, journal);
            AssertEx.SequenceEqual(new[] { "write", "set" }, trace, "the retry writes the journal before setting");
            AssertEx.Equal(AuditPolicyFlags.Failure, journal.Entries[subcategory]);
            AssertEx.Equal(AuditPolicyFlags.Success | AuditPolicyFlags.Failure, backend[subcategory]);
        }

        // Crash after the inner-only change: a fresh backend/journal pair holding the
        // same state (modified live policy, journaled true original) restores Failure.
        private static void RestoreFromJournalAfterInnerOnlyChange()
        {
            var (subcategory, backend, journal, _) = Harness(AuditPolicyFlags.Failure);
            var failure = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Failure, journal);
            var learning = AuditPolicyLease.Acquire(backend, subcategory, AuditPolicyFlags.Success, journal);
            AssertEx.Equal(AuditPolicyFlags.Success | AuditPolicyFlags.Failure, backend[subcategory]);

            var trace = new List<string>();
            var recoveryBackend = new FakeBackend(trace) { [subcategory] = backend[subcategory] };
            var recoveryJournal = new InMemoryJournal(trace);
            recoveryJournal.Entries[subcategory] = journal.Entries[subcategory];

            AuditPolicyLease.RestoreFromJournal(recoveryBackend, recoveryJournal);

            AssertEx.SequenceEqual(new[] { "set", "clear" }, trace);
            AssertEx.Equal(AuditPolicyFlags.Failure, recoveryBackend[subcategory]);
            AssertEx.Equal(0, recoveryJournal.Entries.Count);

            // Unwind the live leases so the static lease table does not leak into other tests.
            learning.Dispose();
            failure.Dispose();
        }

        private static void RestoreFromJournalSkipsMalformedEntry()
        {
            var trace = new List<string>();
            var backend = new FakeBackend(trace);
            var journal = new InMemoryJournal(trace);
            var healthy = Guid.NewGuid();
            backend[healthy] = AuditPolicyFlags.Success | AuditPolicyFlags.Failure;
            journal.Entries[healthy] = AuditPolicyFlags.Failure;
            journal.Malformed.Add("not-a-guid");

            var exception = AssertEx.Throws<InvalidOperationException>(() => AuditPolicyLease.RestoreFromJournal(backend, journal));

            AssertEx.SequenceEqual(new[] { "set", "clear" }, trace, "the healthy entry is restored and cleared");
            AssertEx.Equal(AuditPolicyFlags.Failure, backend[healthy]);
            AssertEx.Equal(0, journal.Entries.Count);
            AssertEx.True(exception.Message.Contains("not-a-guid"), "the skipped record is named: " + exception.Message);
            AssertEx.True(exception.InnerException == null, "no backend failure to report");
        }

        private static void RestoreFromJournalRestoresAll()
        {
            var trace = new List<string>();
            var backend = new FakeBackend(trace);
            var journal = new InMemoryJournal(trace);
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            backend[first] = AuditPolicyFlags.Failure;
            backend[second] = AuditPolicyFlags.Success | AuditPolicyFlags.Failure;
            journal.Entries[first] = AuditPolicyFlags.None;
            journal.Entries[second] = AuditPolicyFlags.Success;

            AuditPolicyLease.RestoreFromJournal(backend, journal);

            AssertEx.SequenceEqual(new[] { "set", "clear", "set", "clear" }, trace);
            AssertEx.Equal(AuditPolicyFlags.None, backend[first]);
            AssertEx.Equal(AuditPolicyFlags.Success, backend[second]);
            AssertEx.Equal(0, journal.Entries.Count);
        }

        private static void RestoreFromJournalEmptyIsNoOp()
        {
            var (subcategory, backend, journal, trace) = Harness(AuditPolicyFlags.Failure);

            AuditPolicyLease.RestoreFromJournal(backend, journal);

            AssertEx.Equal(0, trace.Count);
            AssertEx.Equal(AuditPolicyFlags.Failure, backend[subcategory]);
        }

        private static void RestoreFromJournalKeepsFailedEntry()
        {
            var trace = new List<string>();
            var backend = new FakeBackend(trace);
            var journal = new InMemoryJournal(trace);
            var broken = Guid.NewGuid();
            var healthy = Guid.NewGuid();
            backend[broken] = AuditPolicyFlags.Failure;
            backend[healthy] = AuditPolicyFlags.Failure;
            journal.Entries[broken] = AuditPolicyFlags.Unchanged;
            journal.Entries[healthy] = AuditPolicyFlags.None;
            backend.FailSetFor.Add(broken);

            AssertEx.Throws<InvalidOperationException>(() => AuditPolicyLease.RestoreFromJournal(backend, journal));

            AssertEx.Equal(AuditPolicyFlags.None, backend[healthy]);
            AssertEx.Equal(AuditPolicyFlags.Failure, backend[broken]);
            AssertEx.SequenceEqual(new[] { broken }, journal.Entries.Keys);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;

namespace pylorak.TinyWall.Prompting
{
    internal sealed class CorrelatedDropBatch
    {
        private readonly object guard;
        private readonly IClock clock;
        private readonly int capacity;
        private readonly Queue<Entry> entries = new Queue<Entry>();
        private long generation;
        private bool stopped;
        private int processing;
        internal CoalescedDiagnostic Suppressed { get; } = new CoalescedDiagnostic();
        internal CoalescedDiagnostic Contention { get; } = new CoalescedDiagnostic();
        internal long Generation { get { lock (guard) return generation; } }

        // Native/event callbacks must not wait behind a policy application holding guard.
        internal bool TryAccept(Action accept)
        {
            if (!Monitor.TryEnter(guard)) { Contention.Record(); return false; }
            try
            {
                if (stopped) return false;
                accept();
                return true;
            }
            finally { Monitor.Exit(guard); }
        }

        internal CorrelatedDropBatch(object guard, IClock clock, int capacity = 64)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.guard = guard;
            this.clock = clock;
            this.capacity = capacity;
        }

        // Caller has already matched the full WFP/audit tuple. Recheck at this boundary.
        internal bool TryAdd(DropCandidate candidate, BlockedConnectionAuditEvent audit)
        {
            lock (guard)
            {
                if (stopped || !DropCorrelator.IsMatch(candidate, audit)) return false;
                if (entries.Count == capacity) { Suppressed.Record(); return false; }
                entries.Enqueue(new Entry(candidate, audit, clock.UtcNow));
                return true;
            }
        }

        internal void Reset(bool stop = false)
        {
            lock (guard)
            {
                ++generation;
                stopped |= stop;
                entries.Clear();
            }
        }

        internal void Publish(long expectedGeneration, Action publish)
        {
            lock (guard)
                if (!stopped && generation == expectedGeneration) publish();
        }

        internal void Process<T>(Func<T> snapshot, Action<Exception> failure,
            Func<DropCandidate, BlockedConnectionAuditEvent, T, bool, Action> prepare)
        {
            if (Interlocked.CompareExchange(ref processing, 1, 0) != 0) return;
            try
            {
                Entry[] batch;
                long epoch;
                lock (guard)
                {
                    if (stopped || entries.Count == 0) return;
                    batch = entries.ToArray();
                    entries.Clear();
                    epoch = generation;
                }
                // Read only after draining. Nothing is cached for the next batch.
                T current = default!;
                bool unavailable = false;
                try { current = snapshot(); }
                catch (Exception error) { unavailable = true; failure(error); }
                foreach (Entry entry in batch)
                {
                    // Potentially slow identity/catalog work stays outside the queue lock.
                    Action publish = prepare(entry.Candidate, entry.Audit, current, unavailable);
                    Publish(epoch, () =>
                    {
                        if (clock.UtcNow - entry.QueuedUtc > TimeSpan.FromSeconds(2))
                            Suppressed.Record();
                        else
                            publish();
                    });
                }
            }
            finally { Volatile.Write(ref processing, 0); }
        }

        private sealed record Entry(DropCandidate Candidate, BlockedConnectionAuditEvent Audit, DateTimeOffset QueuedUtc);
    }
}

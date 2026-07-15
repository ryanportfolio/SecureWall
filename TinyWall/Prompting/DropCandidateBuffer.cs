using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    internal sealed class DropCandidateBuffer
    {
        private readonly object _guard = new object();
        private readonly IClock _clock;
        private readonly int _capacity;
        private readonly TimeSpan _enrichmentDelay;
        private readonly LinkedList<Entry> _entries = new LinkedList<Entry>();

        internal DropCandidateBuffer(IClock clock)
            : this(clock, 64, TimeSpan.FromMilliseconds(1_200))
        {
        }

        internal DropCandidateBuffer(IClock clock, int capacity, TimeSpan enrichmentDelay)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            if (enrichmentDelay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(enrichmentDelay));

            _capacity = capacity;
            _enrichmentDelay = enrichmentDelay;
        }

        internal bool TryAdd(DropCandidate candidate)
        {
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));

            lock (_guard)
            {
                if (_entries.Count >= _capacity)
                    return false;

                _entries.AddLast(new Entry(candidate, _clock.UtcNow));
                return true;
            }
        }

        internal bool TryMatch(
            BlockedConnectionAuditEvent auditEvent,
            out DropCandidate? candidate)
        {
            if (auditEvent == null)
                throw new ArgumentNullException(nameof(auditEvent));

            lock (_guard)
            {
                LinkedListNode<Entry>? node = _entries.First;
                while (node != null)
                {
                    LinkedListNode<Entry>? next = node.Next;
                    if (DropCorrelator.IsMatch(node.Value.Candidate, auditEvent))
                    {
                        candidate = node.Value.Candidate;
                        _entries.Remove(node);
                        return true;
                    }

                    node = next;
                }
            }

            candidate = null;
            return false;
        }

        internal IReadOnlyList<DropCandidate> DrainReady()
        {
            var ready = new List<DropCandidate>();
            lock (_guard)
            {
                DateTimeOffset cutoff = _clock.UtcNow.Subtract(_enrichmentDelay);
                while (_entries.First != null && _entries.First.Value.AddedUtc <= cutoff)
                {
                    ready.Add(_entries.First.Value.Candidate);
                    _entries.RemoveFirst();
                }
            }

            return ready.AsReadOnly();
        }

        private sealed class Entry
        {
            internal Entry(DropCandidate candidate, DateTimeOffset addedUtc)
            {
                Candidate = candidate;
                AddedUtc = addedUtc;
            }

            internal DropCandidate Candidate { get; }
            internal DateTimeOffset AddedUtc { get; }
        }
    }
}

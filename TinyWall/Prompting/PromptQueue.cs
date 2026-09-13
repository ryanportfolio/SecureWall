using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    internal enum PromptEnqueueStatus
    {
        Added,
        Coalesced,
        AlreadyPending,
        SuppressedByCooldown,
        CapacityReached,
    }

    internal enum PromptActionStatus
    {
        Allowed,
        Dismissed,
        UnknownToken,
        Expired,
        NotAllowable,
        ApplyFailed,
        Locked,
    }

    internal sealed class PromptEnqueueResult
    {
        internal PromptEnqueueResult(PromptEnqueueStatus status, Guid token)
        {
            Status = status;
            Token = token;
        }

        internal PromptEnqueueStatus Status { get; }
        internal Guid Token { get; }
    }

    internal sealed class PromptActionResult
    {
        internal PromptActionResult(PromptActionStatus status, BlockedConnectionPrompt? prompt)
        {
            Status = status;
            Prompt = prompt;
        }

        internal PromptActionStatus Status { get; }
        internal BlockedConnectionPrompt? Prompt { get; }
    }

    internal sealed class PromptQueue
    {
        private static readonly TimeSpan DefaultCoalesceWindow = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan DefaultIgnoreCooldown = TimeSpan.FromMinutes(5);

        private readonly object _guard = new object();
        private readonly IClock _clock;
        private readonly Func<Guid> _tokenFactory;
        private readonly int _capacity;
        private readonly TimeSpan _coalesceWindow;
        private readonly TimeSpan _tokenLifetime;
        private readonly TimeSpan _ignoreCooldown;
        private readonly Dictionary<Guid, Entry> _byToken = new Dictionary<Guid, Entry>();
        private readonly Dictionary<string, Guid> _byIdentity =
            new Dictionary<string, Guid>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _cooldowns =
            new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        private readonly LinkedList<Guid> _fifo = new LinkedList<Guid>();

        internal PromptQueue(IClock clock)
            : this(
                clock,
                32,
                DefaultCoalesceWindow,
                DefaultTokenLifetime,
                DefaultIgnoreCooldown,
                Guid.NewGuid)
        {
        }

        internal PromptQueue(
            IClock clock,
            int capacity,
            TimeSpan coalesceWindow,
            TimeSpan tokenLifetime,
            TimeSpan ignoreCooldown,
            Func<Guid> tokenFactory)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _tokenFactory = tokenFactory ?? throw new ArgumentNullException(nameof(tokenFactory));
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            if (coalesceWindow < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(coalesceWindow));
            if (tokenLifetime <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(tokenLifetime));
            if (ignoreCooldown < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(ignoreCooldown));

            _capacity = capacity;
            _coalesceWindow = coalesceWindow;
            _tokenLifetime = tokenLifetime;
            _ignoreCooldown = ignoreCooldown;
        }

        internal PromptEnqueueResult Enqueue(
            PromptIdentity identity,
            string remoteAddress,
            int remotePort,
            byte protocol)
        {
            if (identity == null)
                throw new ArgumentNullException(nameof(identity));
            if (remotePort < 0 || remotePort > 65535)
                throw new ArgumentOutOfRangeException(nameof(remotePort));

            lock (_guard)
            {
                DateTimeOffset now = _clock.UtcNow;
                PurgeExpired(now);

                if (_cooldowns.TryGetValue(identity.Key, out DateTimeOffset cooldownUntil))
                {
                    if (cooldownUntil > now)
                    {
                        return new PromptEnqueueResult(
                            PromptEnqueueStatus.SuppressedByCooldown,
                            Guid.Empty);
                    }

                    _cooldowns.Remove(identity.Key);
                }

                if (_byIdentity.TryGetValue(identity.Key, out Guid existingToken) &&
                    _byToken.TryGetValue(existingToken, out Entry? existing))
                {
                    if ((now - existing.LastSeenUtc) <= _coalesceWindow)
                    {
                        existing.LastSeenUtc = now;
                        existing.RemoteAddress = remoteAddress ?? string.Empty;
                        existing.RemotePort = remotePort;
                        existing.Protocol = protocol;
                        existing.OccurrenceCount++;
                        return new PromptEnqueueResult(PromptEnqueueStatus.Coalesced, existingToken);
                    }

                    return new PromptEnqueueResult(PromptEnqueueStatus.AlreadyPending, existingToken);
                }

                if (_byToken.Count >= _capacity)
                    return new PromptEnqueueResult(PromptEnqueueStatus.CapacityReached, Guid.Empty);

                Guid token = CreateUniqueToken();
                var entry = new Entry(
                    token,
                    identity,
                    now,
                    now.Add(_tokenLifetime),
                    remoteAddress ?? string.Empty,
                    remotePort,
                    protocol);
                _byToken.Add(token, entry);
                _byIdentity.Add(identity.Key, token);
                entry.QueueNode = _fifo.AddLast(token);
                return new PromptEnqueueResult(PromptEnqueueStatus.Added, token);
            }
        }

        internal IReadOnlyList<BlockedConnectionPrompt> GetPending()
        {
            lock (_guard)
            {
                PurgeExpired(_clock.UtcNow);
                var snapshots = new List<BlockedConnectionPrompt>(_byToken.Count);
                foreach (Guid token in _fifo)
                {
                    if (_byToken.TryGetValue(token, out Entry? entry))
                        snapshots.Add(entry.ToSnapshot());
                }

                return snapshots.AsReadOnly();
            }
        }

        // Shared only with the service's candidate publication/reset boundary.
        internal object SyncRoot => _guard;
        internal void Clear()
        {
            lock (_guard)
            {
                // Allow's policy callback can replace policy and clear this queue.
                // Its captured entry must remain safe to remove when that callback returns.
                foreach (Entry entry in _byToken.Values) entry.QueueNode = null;
                _byToken.Clear();
                _byIdentity.Clear();
                _fifo.Clear();
            }
        }

        internal PromptActionResult Allow(Guid token) => Allow(token, _ => true);

        internal PromptActionResult Allow(
            Guid token,
            Func<BlockedConnectionPrompt, bool> applyPolicy)
        {
            if (applyPolicy == null)
                throw new ArgumentNullException(nameof(applyPolicy));

            lock (_guard)
            {
                if (!_byToken.TryGetValue(token, out Entry? entry))
                    return new PromptActionResult(PromptActionStatus.UnknownToken, null);

                if (IsExpired(entry, _clock.UtcNow))
                {
                    BlockedConnectionPrompt expired = entry.ToSnapshot();
                    Remove(entry);
                    return new PromptActionResult(PromptActionStatus.Expired, expired);
                }

                if (entry.Identity.Kind == PromptIdentityKind.AmbiguousService)
                {
                    return new PromptActionResult(
                        PromptActionStatus.NotAllowable,
                        entry.ToSnapshot());
                }

                BlockedConnectionPrompt prompt = entry.ToSnapshot();
                try
                {
                    if (!applyPolicy(prompt))
                        return new PromptActionResult(PromptActionStatus.ApplyFailed, prompt);
                }
                catch
                {
                    return new PromptActionResult(PromptActionStatus.ApplyFailed, prompt);
                }

                Remove(entry);
                return new PromptActionResult(PromptActionStatus.Allowed, prompt);
            }
        }

        internal PromptActionResult Dismiss(Guid token)
        {
            lock (_guard)
            {
                if (!_byToken.TryGetValue(token, out Entry? entry))
                    return new PromptActionResult(PromptActionStatus.UnknownToken, null);

                DateTimeOffset now = _clock.UtcNow;
                if (IsExpired(entry, now))
                {
                    BlockedConnectionPrompt expired = entry.ToSnapshot();
                    Remove(entry);
                    return new PromptActionResult(PromptActionStatus.Expired, expired);
                }

                BlockedConnectionPrompt prompt = entry.ToSnapshot();
                Remove(entry);
                _cooldowns[entry.Identity.Key] = now.Add(_ignoreCooldown);
                return new PromptActionResult(PromptActionStatus.Dismissed, prompt);
            }
        }

        private Guid CreateUniqueToken()
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                Guid token = _tokenFactory();
                if (token != Guid.Empty && !_byToken.ContainsKey(token))
                    return token;
            }

            throw new InvalidOperationException("Unable to create a unique prompt token.");
        }

        private void PurgeExpired(DateTimeOffset now)
        {
            var expired = new List<Entry>();
            foreach (Entry entry in _byToken.Values)
            {
                if (IsExpired(entry, now))
                    expired.Add(entry);
            }

            foreach (Entry entry in expired)
                Remove(entry);

            var endedCooldowns = new List<string>();
            foreach (KeyValuePair<string, DateTimeOffset> cooldown in _cooldowns)
            {
                if (cooldown.Value <= now)
                    endedCooldowns.Add(cooldown.Key);
            }

            foreach (string key in endedCooldowns)
                _cooldowns.Remove(key);
        }

        private static bool IsExpired(Entry entry, DateTimeOffset now) => now > entry.ExpiresUtc;

        private void Remove(Entry entry)
        {
            _byToken.Remove(entry.Token);
            _byIdentity.Remove(entry.Identity.Key);
            if (entry.QueueNode != null)
            {
                _fifo.Remove(entry.QueueNode);
                entry.QueueNode = null;
            }
        }

        private sealed class Entry
        {
            internal Entry(
                Guid token,
                PromptIdentity identity,
                DateTimeOffset firstSeenUtc,
                DateTimeOffset expiresUtc,
                string remoteAddress,
                int remotePort,
                byte protocol)
            {
                Token = token;
                Identity = identity;
                FirstSeenUtc = firstSeenUtc;
                LastSeenUtc = firstSeenUtc;
                ExpiresUtc = expiresUtc;
                RemoteAddress = remoteAddress;
                RemotePort = remotePort;
                Protocol = protocol;
                OccurrenceCount = 1;
            }

            internal Guid Token { get; }
            internal PromptIdentity Identity { get; }
            internal DateTimeOffset FirstSeenUtc { get; }
            internal DateTimeOffset LastSeenUtc { get; set; }
            internal DateTimeOffset ExpiresUtc { get; }
            internal string RemoteAddress { get; set; }
            internal int RemotePort { get; set; }
            internal byte Protocol { get; set; }
            internal int OccurrenceCount { get; set; }
            internal LinkedListNode<Guid>? QueueNode { get; set; }

            internal BlockedConnectionPrompt ToSnapshot() =>
                new BlockedConnectionPrompt(
                    Token,
                    Identity,
                    FirstSeenUtc,
                    LastSeenUtc,
                    ExpiresUtc,
                    RemoteAddress,
                    RemotePort,
                    Protocol,
                    OccurrenceCount);
        }
    }
}

using System;

namespace pylorak.TinyWall.Prompting
{
    internal sealed class BlockedConnectionPrompt
    {
        internal BlockedConnectionPrompt(
            Guid token,
            PromptIdentity identity,
            DateTimeOffset firstSeenUtc,
            DateTimeOffset lastSeenUtc,
            DateTimeOffset expiresUtc,
            string remoteAddress,
            int remotePort,
            byte protocol,
            int occurrenceCount)
        {
            Token = token;
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            FirstSeenUtc = firstSeenUtc;
            LastSeenUtc = lastSeenUtc;
            ExpiresUtc = expiresUtc;
            RemoteAddress = remoteAddress ?? string.Empty;
            RemotePort = remotePort;
            Protocol = protocol;
            OccurrenceCount = occurrenceCount;
        }

        internal Guid Token { get; }
        internal PromptIdentity Identity { get; }
        internal DateTimeOffset FirstSeenUtc { get; }
        internal DateTimeOffset LastSeenUtc { get; }
        internal DateTimeOffset ExpiresUtc { get; }
        internal string RemoteAddress { get; }
        internal int RemotePort { get; }
        internal byte Protocol { get; }
        internal int OccurrenceCount { get; }
        internal bool CanAllow => Identity.Kind != PromptIdentityKind.AmbiguousService;
    }
}

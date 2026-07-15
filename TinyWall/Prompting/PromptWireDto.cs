using System;

namespace pylorak.TinyWall.Prompting
{
    // Display-only data. The service never accepts these identity fields back as authority;
    // controller actions carry only Token.
    internal sealed class PromptWireDto
    {
        public Guid Token { get; set; }
        public PromptIdentityKind SubjectKind { get; set; }
        public bool CanAllow { get; set; }
        public string? ExecutablePath { get; set; }
        public string? PackageSid { get; set; }
        public string? ServiceName { get; set; }
        public string[] AmbiguousServiceNames { get; set; } = Array.Empty<string>();
        public DateTimeOffset FirstSeenUtc { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
        public string RemoteAddress { get; set; } = string.Empty;
        public int RemotePort { get; set; }
        public byte Protocol { get; set; }
        public int OccurrenceCount { get; set; }

        internal static PromptWireDto FromPrompt(BlockedConnectionPrompt prompt)
        {
            if (prompt == null)
                throw new ArgumentNullException(nameof(prompt));

            return new PromptWireDto
            {
                Token = prompt.Token,
                SubjectKind = prompt.Identity.Kind,
                CanAllow = prompt.CanAllow,
                ExecutablePath = prompt.Identity.ExecutablePath,
                PackageSid = prompt.Identity.PackageSid,
                ServiceName = prompt.Identity.ServiceName,
                AmbiguousServiceNames = prompt.Identity.AmbiguousServiceNames is string[] names
                    ? (string[])names.Clone()
                    : new System.Collections.Generic.List<string>(prompt.Identity.AmbiguousServiceNames).ToArray(),
                FirstSeenUtc = prompt.FirstSeenUtc,
                LastSeenUtc = prompt.LastSeenUtc,
                ExpiresUtc = prompt.ExpiresUtc,
                RemoteAddress = prompt.RemoteAddress,
                RemotePort = prompt.RemotePort,
                Protocol = prompt.Protocol,
                OccurrenceCount = prompt.OccurrenceCount,
            };
        }
    }
}

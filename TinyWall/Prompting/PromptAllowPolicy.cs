using System;

namespace pylorak.TinyWall.Prompting
{
    internal sealed class PromptAllowPolicy
    {
        private PromptAllowPolicy(PromptIdentity identity)
        {
            Identity = identity;
            AllowedRemoteTcpConnectPorts = "*";
            AllowedRemoteUdpConnectPorts = "*";
        }

        internal PromptIdentity Identity { get; }
        internal string AllowedRemoteTcpConnectPorts { get; }
        internal string AllowedRemoteUdpConnectPorts { get; }
        internal string? AllowedLocalTcpListenerPorts { get; }
        internal string? AllowedLocalUdpListenerPorts { get; }

        internal static PromptAllowPolicy Create(PromptIdentity identity)
        {
            if (!TryCreate(identity, out PromptAllowPolicy? policy))
                throw new InvalidOperationException("This identity cannot receive an outbound allow policy.");

            return policy!;
        }

        internal static bool TryCreate(PromptIdentity? identity, out PromptAllowPolicy? policy)
        {
            policy = null;
            if (identity == null || identity.Kind == PromptIdentityKind.AmbiguousService)
                return false;
            if (identity.Kind != PromptIdentityKind.Package &&
                string.IsNullOrWhiteSpace(identity.ExecutablePath))
            {
                return false;
            }

            policy = new PromptAllowPolicy(identity);
            return true;
        }
    }
}

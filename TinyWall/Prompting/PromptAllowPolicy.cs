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

        internal static bool TryCreate(PromptIdentity? identity, out PromptAllowPolicy? policy) =>
            TryCreate(identity, executableExists: null, out policy, out _);

        // `executableExists` lets the service recheck the file right before it writes the
        // exception: a path that vanished between the block and the click is refused with
        // `refusalReason` set. Package identities carry no path and skip the check.
        internal static bool TryCreate(
            PromptIdentity? identity,
            Func<string, bool>? executableExists,
            out PromptAllowPolicy? policy,
            out string? refusalReason)
        {
            policy = null;
            refusalReason = null;
            if (identity == null || identity.Kind == PromptIdentityKind.AmbiguousService)
            {
                refusalReason = "The identity is ambiguous or missing.";
                return false;
            }
            if (identity.Kind != PromptIdentityKind.Package)
            {
                if (string.IsNullOrWhiteSpace(identity.ExecutablePath))
                {
                    refusalReason = "The identity has no executable path.";
                    return false;
                }
                if (executableExists != null && !executableExists(identity.ExecutablePath!))
                {
                    refusalReason = "The executable no longer exists at " + identity.ExecutablePath + ".";
                    return false;
                }
            }

            policy = new PromptAllowPolicy(identity);
            return true;
        }
    }
}

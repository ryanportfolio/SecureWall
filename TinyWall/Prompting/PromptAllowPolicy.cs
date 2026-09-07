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
        // `refusalReason` set. Package identities carry no path and skip the check, and so
        // do subjects that were never files on disk (see RequiresExistenceCheck).
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
                if (executableExists != null &&
                    RequiresExistenceCheck(identity.ExecutablePath!) &&
                    !executableExists(identity.ExecutablePath!))
                {
                    refusalReason = "The executable no longer exists at " + identity.ExecutablePath + ".";
                    return false;
                }
            }

            policy = new PromptAllowPolicy(identity);
            return true;
        }

        // The kernel reports its own traffic under this pseudo-path (PathMapper.SYSTEM_CONST).
        private const string SystemPseudoPath = "System";

        // Only paths in Win32 form can be checked with File.Exists. The kernel pseudo-path
        // "System" and paths PathMapper could not map out of NT form (\Device\..., \??\...)
        // were allowed before the recheck existed and still are: a false "gone" verdict there
        // would lock the user out of allowing kernel or unmapped-volume traffic.
        internal static bool RequiresExistenceCheck(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (string.Equals(path, SystemPseudoPath, StringComparison.OrdinalIgnoreCase))
                return false;

            // Drive-letter form: C:\...
            if (IsDriveLetterPath(path))
                return true;

            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // Extended form: \\?\C:\... or \\?\UNC\server\share\...
                if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    string rest = path.Substring(4);
                    return IsDriveLetterPath(rest) || rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase);
                }
                // \\.\ is the device namespace, not a file path.
                if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
                    return false;
                // Plain UNC: \\server\share\...
                return true;
            }

            return false;
        }

        private static bool IsDriveLetterPath(string path) =>
            path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

        private static bool IsAsciiLetter(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    }
}

using System;

namespace pylorak.TinyWall.Prompting
{
    internal sealed class DropCandidate
    {
        internal DropCandidate(
            DateTimeOffset timestampUtc,
            ulong filterRuntimeId,
            string applicationPath,
            string? packageSid,
            string localAddress,
            int localPort,
            string remoteAddress,
            int remotePort,
            byte protocol)
        {
            if (string.IsNullOrWhiteSpace(applicationPath))
                throw new ArgumentException("Application path is required.", nameof(applicationPath));
            if (localPort < 0 || localPort > 65535)
                throw new ArgumentOutOfRangeException(nameof(localPort));
            if (remotePort < 0 || remotePort > 65535)
                throw new ArgumentOutOfRangeException(nameof(remotePort));

            TimestampUtc = timestampUtc;
            FilterRuntimeId = filterRuntimeId;
            ApplicationPath = applicationPath.Trim();
            global::pylorak.TinyWall.Prompting.PackageSid.TryNormalize(
                packageSid,
                out string? normalizedPackageSid);
            PackageSid = normalizedPackageSid;
            LocalAddress = localAddress ?? string.Empty;
            LocalPort = localPort;
            RemoteAddress = remoteAddress ?? string.Empty;
            RemotePort = remotePort;
            Protocol = protocol;
        }

        internal DateTimeOffset TimestampUtc { get; }
        internal ulong FilterRuntimeId { get; }
        internal string ApplicationPath { get; }
        internal string? PackageSid { get; }
        internal string LocalAddress { get; }
        internal int LocalPort { get; }
        internal string RemoteAddress { get; }
        internal int RemotePort { get; }
        internal byte Protocol { get; }
    }
}

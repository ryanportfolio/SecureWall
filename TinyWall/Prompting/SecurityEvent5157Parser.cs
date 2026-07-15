using System;
using System.Collections.Generic;
using System.Globalization;

namespace pylorak.TinyWall.Prompting
{
    internal enum ConnectionDirection
    {
        Inbound,
        Outbound,
    }

    internal sealed class BlockedConnectionAuditEvent
    {
        internal BlockedConnectionAuditEvent(
            DateTimeOffset timestampUtc,
            uint processId,
            string applicationPath,
            ConnectionDirection direction,
            string localAddress,
            int localPort,
            string remoteAddress,
            int remotePort,
            byte protocol,
            ulong filterRuntimeId,
            string? packageSid)
        {
            TimestampUtc = timestampUtc;
            ProcessId = processId;
            ApplicationPath = applicationPath;
            Direction = direction;
            LocalAddress = localAddress;
            LocalPort = localPort;
            RemoteAddress = remoteAddress;
            RemotePort = remotePort;
            Protocol = protocol;
            FilterRuntimeId = filterRuntimeId;
            global::pylorak.TinyWall.Prompting.PackageSid.TryNormalize(
                packageSid,
                out string? normalizedPackageSid);
            PackageSid = normalizedPackageSid;
        }

        internal DateTimeOffset TimestampUtc { get; }
        internal uint ProcessId { get; }
        internal string ApplicationPath { get; }
        internal ConnectionDirection Direction { get; }
        internal string LocalAddress { get; }
        internal int LocalPort { get; }
        internal string RemoteAddress { get; }
        internal int RemotePort { get; }
        internal byte Protocol { get; }
        internal ulong FilterRuntimeId { get; }
        internal string? PackageSid { get; }
    }

    internal static class SecurityEvent5157Parser
    {
        internal static bool TryParse(
            IReadOnlyDictionary<string, string> fields,
            DateTimeOffset timestampUtc,
            out BlockedConnectionAuditEvent parsed)
        {
            parsed = null!;
            if (fields == null ||
                !TryGet(fields, "ProcessID", out string processText) ||
                !uint.TryParse(processText, NumberStyles.None, CultureInfo.InvariantCulture, out uint processId) ||
                !TryGet(fields, "Application", out string applicationPath) ||
                !TryGet(fields, "Direction", out string directionText) ||
                !TryDirection(directionText, out ConnectionDirection direction) ||
                !TryGet(fields, "SourceAddress", out string sourceAddress) ||
                !TryPort(fields, "SourcePort", out int sourcePort) ||
                !TryGet(fields, "DestAddress", out string destinationAddress) ||
                !TryPort(fields, "DestPort", out int destinationPort) ||
                !TryGet(fields, "Protocol", out string protocolText) ||
                !byte.TryParse(protocolText, NumberStyles.None, CultureInfo.InvariantCulture, out byte protocol) ||
                !TryGet(fields, "FilterRTID", out string filterText) ||
                !ulong.TryParse(filterText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong filterRuntimeId))
            {
                return false;
            }

            string localAddress;
            int localPort;
            string remoteAddress;
            int remotePort;
            if (direction == ConnectionDirection.Outbound)
            {
                localAddress = sourceAddress;
                localPort = sourcePort;
                remoteAddress = destinationAddress;
                remotePort = destinationPort;
            }
            else
            {
                localAddress = destinationAddress;
                localPort = destinationPort;
                remoteAddress = sourceAddress;
                remotePort = sourcePort;
            }

            fields.TryGetValue("PackageId", out string? packageSid);
            parsed = new BlockedConnectionAuditEvent(
                timestampUtc,
                processId,
                applicationPath,
                direction,
                localAddress,
                localPort,
                remoteAddress,
                remotePort,
                protocol,
                filterRuntimeId,
                string.IsNullOrWhiteSpace(packageSid) ? null : packageSid.Trim());
            return true;
        }

        private static bool TryGet(
            IReadOnlyDictionary<string, string> fields,
            string name,
            out string value)
        {
            if (fields.TryGetValue(name, out string? raw) && !string.IsNullOrWhiteSpace(raw))
            {
                value = raw.Trim();
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static bool TryPort(
            IReadOnlyDictionary<string, string> fields,
            string name,
            out int port)
        {
            port = 0;
            return TryGet(fields, name, out string text) &&
                int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                port >= 0 &&
                port <= 65535;
        }

        private static bool TryDirection(string value, out ConnectionDirection direction)
        {
            if (string.Equals(value, "%%14593", StringComparison.Ordinal) ||
                string.Equals(value, "Outbound", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "2", StringComparison.Ordinal))
            {
                direction = ConnectionDirection.Outbound;
                return true;
            }

            if (string.Equals(value, "%%14592", StringComparison.Ordinal) ||
                string.Equals(value, "Inbound", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "1", StringComparison.Ordinal))
            {
                direction = ConnectionDirection.Inbound;
                return true;
            }

            direction = default;
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;

namespace pylorak.TinyWall.Prompting
{
    internal enum LearningEventKind
    {
        Connection,
        Listen,
        Bind,
    }

    // One Security audit record (5154-5159) as Learning sees it. Connection
    // records carry direction and both endpoints; listen and bind records have
    // only the local endpoint, so their remote side stays empty.
    internal sealed class LearningObservation
    {
        internal LearningObservation(
            int eventId,
            LearningEventKind kind,
            uint processId,
            string applicationPath,
            ConnectionDirection? direction,
            byte protocol,
            string localAddress,
            int localPort,
            string? remoteAddress,
            int remotePort)
        {
            EventId = eventId;
            Kind = kind;
            ProcessId = processId;
            ApplicationPath = applicationPath;
            Direction = direction;
            Protocol = protocol;
            LocalAddress = localAddress;
            LocalPort = localPort;
            RemoteAddress = remoteAddress;
            RemotePort = remotePort;
        }

        internal int EventId { get; }
        internal LearningEventKind Kind { get; }
        internal uint ProcessId { get; }
        internal string ApplicationPath { get; }
        internal ConnectionDirection? Direction { get; }
        internal byte Protocol { get; }
        internal string LocalAddress { get; }
        internal int LocalPort { get; }
        internal string? RemoteAddress { get; }
        internal int RemotePort { get; }
    }

    // What one observation may add to a learned policy. Outbound opens remote
    // TCP/UDP connect ports; a listener port opens exactly that local port.
    internal sealed class LearningGrant
    {
        internal LearningGrant(bool outbound, int? tcpListenerPort, int? udpListenerPort)
        {
            Outbound = outbound;
            TcpListenerPort = tcpListenerPort;
            UdpListenerPort = udpListenerPort;
        }

        internal bool Outbound { get; }
        internal int? TcpListenerPort { get; }
        internal int? UdpListenerPort { get; }
    }

    internal static class LearningEventParser
    {
        // Only permitted operations teach Learning. Learning mode permits all
        // traffic, so a blocked record (5155, 5157, 5159) means a higher-priority rule
        // (user block, blocklist) denied it; that attempt must not earn other access.
        internal static LearningEventKind? KindOf(int eventId)
        {
            switch (eventId)
            {
                case 5154:
                    return LearningEventKind.Listen;
                case 5156:
                    return LearningEventKind.Connection;
                case 5158:
                    return LearningEventKind.Bind;
                default:
                    return null;
            }
        }

        // Connection records must name direction and both endpoints; a record
        // without them is dropped rather than learned with an unknown remote.
        internal static bool TryParse(
            int eventId,
            IReadOnlyDictionary<string, string> fields,
            out LearningObservation observation)
        {
            observation = null!;
            LearningEventKind? kind = KindOf(eventId);
            if (kind == null || fields == null ||
                !TryGet(fields, "ProcessID", out string processText) ||
                !uint.TryParse(processText, NumberStyles.None, CultureInfo.InvariantCulture, out uint processId) ||
                !TryGet(fields, "Application", out string applicationPath) ||
                !TryGet(fields, "SourceAddress", out string sourceAddress) ||
                !TryPort(fields, "SourcePort", out int sourcePort) ||
                !TryGet(fields, "Protocol", out string protocolText) ||
                !byte.TryParse(protocolText, NumberStyles.None, CultureInfo.InvariantCulture, out byte protocol))
            {
                return false;
            }

            if (kind != LearningEventKind.Connection)
            {
                observation = new LearningObservation(eventId, kind.Value, processId, applicationPath,
                    null, protocol, sourceAddress, sourcePort, null, 0);
                return true;
            }

            if (!TryGet(fields, "Direction", out string directionText) ||
                !TryDirection(directionText, out ConnectionDirection direction) ||
                !TryGet(fields, "DestAddress", out string destinationAddress) ||
                !TryPort(fields, "DestPort", out int destinationPort))
            {
                return false;
            }

            // Same endpoint mapping as SecurityEvent5157Parser. Learning grants
            // nothing for inbound records, and the loopback check reads both
            // endpoints, so the inbound mapping cannot widen a learned rule.
            observation = direction == ConnectionDirection.Outbound
                ? new LearningObservation(eventId, kind.Value, processId, applicationPath, direction, protocol,
                    sourceAddress, sourcePort, destinationAddress, destinationPort)
                : new LearningObservation(eventId, kind.Value, processId, applicationPath, direction, protocol,
                    destinationAddress, destinationPort, sourceAddress, sourcePort);
            return true;
        }

        private static bool TryGet(IReadOnlyDictionary<string, string> fields, string name, out string value)
        {
            if (fields.TryGetValue(name, out string? raw) && !string.IsNullOrWhiteSpace(raw))
            {
                value = raw.Trim();
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static bool TryPort(IReadOnlyDictionary<string, string> fields, string name, out int port)
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
                string.Equals(value, "Outbound", StringComparison.OrdinalIgnoreCase))
            {
                direction = ConnectionDirection.Outbound;
                return true;
            }

            if (string.Equals(value, "%%14592", StringComparison.Ordinal) ||
                string.Equals(value, "Inbound", StringComparison.OrdinalIgnoreCase))
            {
                direction = ConnectionDirection.Inbound;
                return true;
            }

            direction = default;
            return false;
        }
    }

    // Learning-mode rules: learn only observed, non-loopback behavior, and never
    // an executable-wide svchost.exe rule.
    internal static class LearningPolicy
    {
        internal const byte TcpProtocol = 6;
        internal const byte UdpProtocol = 17;

        // Windows' default dynamic port range starts here. A listen or bind on an
        // ephemeral port is almost always a client socket, not a server.
        internal const int FirstDynamicPort = 49152;

        // Upper bound on learned listener ports per protocol and subject.
        internal const int MaxListenerPorts = 16;

        // An SCM snapshot taken later than this after the event no longer proves
        // which service owned the process ID when the event happened.
        internal static readonly TimeSpan MaxServiceAttributionAge = TimeSpan.FromSeconds(2);

        private const string SystemPseudoPath = "System";

        internal static bool IsServiceHost(string? applicationPath)
        {
            if (string.IsNullOrWhiteSpace(applicationPath))
                return false;
            try
            {
                return string.Equals(Path.GetFileName(applicationPath), "svchost.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                // Unreadable name: require exact service attribution rather than
                // risk learning an executable-wide rule.
                return true;
            }
        }

        internal static bool IsLearnablePath(string? applicationPath) =>
            !string.IsNullOrWhiteSpace(applicationPath) &&
            !string.Equals(applicationPath, SystemPseudoPath, StringComparison.OrdinalIgnoreCase);

        // Returns false when the observation grants nothing: loopback traffic,
        // inbound connections, non-TCP/UDP protocols, unparseable or unspecified
        // connection endpoints, and listens or binds on loopback or ephemeral ports.
        internal static bool TryGetGrant(
            LearningEventKind kind,
            ConnectionDirection? direction,
            byte protocol,
            string? localAddress,
            int localPort,
            string? remoteAddress,
            out LearningGrant grant)
        {
            grant = null!;
            if (protocol != TcpProtocol && protocol != UdpProtocol)
                return false;
            if (!TryParseAddress(localAddress, out IPAddress? local))
                return false;

            if (kind == LearningEventKind.Connection)
            {
                if (direction != ConnectionDirection.Outbound)
                    return false;
                if (!TryParseAddress(remoteAddress, out IPAddress? remote) ||
                    IsUnspecified(remote!) || IsLoopback(remote!) || IsLoopback(local!))
                    return false;
                grant = new LearningGrant(outbound: true, tcpListenerPort: null, udpListenerPort: null);
                return true;
            }

            if (IsLoopback(local!) || localPort <= 0 || localPort >= FirstDynamicPort)
                return false;

            if (kind == LearningEventKind.Listen && protocol == TcpProtocol)
            {
                grant = new LearningGrant(outbound: false, tcpListenerPort: localPort, udpListenerPort: null);
                return true;
            }

            // UDP has no listen step; a bind to a fixed port is how it receives.
            // A TCP bind precedes both connect and listen, so it proves nothing.
            if (kind == LearningEventKind.Bind && protocol == UdpProtocol)
            {
                grant = new LearningGrant(outbound: false, tcpListenerPort: null, udpListenerPort: localPort);
                return true;
            }

            return false;
        }

        // svchost.exe hosts unrelated services under one image path. Learning
        // accepts it only as exactly one stable service in the event's process.
        internal static bool TryResolveServiceHost(
            IEnumerable<string>? serviceNames,
            bool snapshotUncertain,
            TimeSpan eventAge,
            out string? serviceName,
            out string refusalReason)
        {
            serviceName = null;
            if (serviceNames == null)
            {
                refusalReason = "the service snapshot failed";
                return false;
            }
            if (snapshotUncertain)
            {
                refusalReason = "a service in the process was starting or stopping";
                return false;
            }
            if (eventAge < TimeSpan.Zero || eventAge > MaxServiceAttributionAge)
            {
                refusalReason = "the event was too old to attribute";
                return false;
            }

            string[] names = serviceNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length == 0)
            {
                refusalReason = "no running service owns the process";
                return false;
            }
            if (names.Length > 1)
            {
                refusalReason = "the process hosts several services";
                return false;
            }

            serviceName = names[0];
            refusalReason = string.Empty;
            return true;
        }

        // Adds one port to a comma-separated listener list. A wildcard stays a
        // wildcard; a full list is returned unchanged.
        internal static string AddListenerPort(string? existing, int port)
        {
            string portText = port.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(existing))
                return portText;

            string[] ports = existing!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToArray();
            if (ports.Contains("*") || ports.Contains(portText) || ports.Length >= MaxListenerPorts)
                return existing!;
            return string.Join(",", ports.Concat(new[] { portText }));
        }

        private static bool TryParseAddress(string? text, out IPAddress? address)
        {
            address = null;
            return !string.IsNullOrWhiteSpace(text) && IPAddress.TryParse(text!.Trim(), out address);
        }

        private static IPAddress Unmap(IPAddress address) =>
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        private static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(Unmap(address));

        private static bool IsUnspecified(IPAddress address)
        {
            IPAddress unmapped = Unmap(address);
            return unmapped.Equals(IPAddress.Any) || unmapped.Equals(IPAddress.IPv6Any);
        }
    }
}

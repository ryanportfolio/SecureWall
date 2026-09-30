using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class LearningPolicyTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases
    {
        get
        {
            yield return ("learning skips loopback traffic from a pre-bound socket", LoopbackFromPreBoundSocketLearnsNothing);
            yield return ("learning parses 5156 direction and both endpoints", Event5156KeepsDirectionAndEndpoints);
            yield return ("learning rejects connection records without direction or destination", ConnectionWithoutDestinationIsRejected);
            yield return ("learning grants outbound only for an outbound connection", OutboundConnectionGrantsOutboundOnly);
            yield return ("learning grants nothing for inbound connections", InboundConnectionGrantsNothing);
            yield return ("learning grants nothing for unspecified or unparseable remotes", UnspecifiedRemoteGrantsNothing);
            yield return ("learning grants nothing for non TCP/UDP protocols", OtherProtocolsGrantNothing);
            yield return ("learning grants a TCP listener only for the observed fixed port", ListenGrantsObservedTcpPort);
            yield return ("learning grants a UDP listener only for a fixed-port UDP bind", UdpBindGrantsObservedUdpPort);
            yield return ("learning svchost check matches the file name in any directory", ServiceHostCheckUsesFileName);
            yield return ("learning skips the kernel pseudo-path and empty paths", KernelAndEmptyPathsAreNotLearnable);
            yield return ("learning svchost requires exactly one stable fresh service", ServiceHostNeedsExactAttribution);
            yield return ("learning listener port lists merge, dedupe and stay bounded", ListenerPortListsMerge);
            yield return ("learning ignores blocked listen, connection and bind records", BlockedRecordsTeachNothing);
        }
    }

    private static Dictionary<string, string> Fields(params (string Name, string Value)[] values)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProcessID"] = "4242",
            ["Application"] = @"\device\harddiskvolume3\apps\sample.exe",
            ["Protocol"] = "6",
        };
        foreach (var (name, value) in values)
            fields[name] = value;
        return fields;
    }

    private static Dictionary<string, string> Connection(string direction, string source, string sourcePort,
        string destination, string destinationPort, string protocol = "6") =>
        Fields(("Direction", direction), ("SourceAddress", source), ("SourcePort", sourcePort),
            ("DestAddress", destination), ("DestPort", destinationPort), ("Protocol", protocol));

    private static bool Grants(int eventId, Dictionary<string, string> fields, out LearningGrant grant)
    {
        grant = null!;
        AssertEx.True(LearningEventParser.TryParse(eventId, fields, out LearningObservation observation),
            "The record should parse.");
        return LearningPolicy.TryGetGrant(observation.Kind, observation.Direction, observation.Protocol,
            observation.LocalAddress, observation.LocalPort, observation.RemoteAddress, out grant);
    }

    // A local server bound before Learning started raises no listen or bind event;
    // its loopback clients raise only 5156. Upstream skipped these; the fork's
    // earlier parser dropped the destination and learned them.
    private static void LoopbackFromPreBoundSocketLearnsNothing()
    {
        AssertEx.False(Grants(5156, Connection("%%14593", "127.0.0.1", "52000", "127.0.0.1", "5432"), out _),
            "IPv4 loopback outbound must not be learned.");
        AssertEx.False(Grants(5156, Connection("%%14592", "127.0.0.1", "5432", "127.0.0.1", "52000"), out _),
            "IPv4 loopback inbound must not be learned.");
        AssertEx.False(Grants(5156, Connection("%%14593", "::1", "52000", "::1", "5432"), out _),
            "IPv6 loopback must not be learned.");
        AssertEx.False(Grants(5156, Connection("%%14593", "127.0.0.1", "52000", "127.3.4.5", "5432"), out _),
            "All of 127.0.0.0/8 is loopback.");
        AssertEx.False(Grants(5156, Connection("%%14593", "::ffff:127.0.0.1", "52000", "::ffff:127.0.0.1", "5432"), out _),
            "IPv4-mapped loopback must not be learned.");
        AssertEx.False(Grants(5156, Connection("%%14593", "::1", "52000", "::1", "53", protocol: "17"), out _),
            "UDP loopback must not be learned.");
    }

    private static void Event5156KeepsDirectionAndEndpoints()
    {
        var fields = Connection("%%14593", "192.0.2.10", "52144", "203.0.113.20", "443");
        AssertEx.True(LearningEventParser.TryParse(5156, fields, out var observation));
        AssertEx.Equal(LearningEventKind.Connection, observation.Kind);
        AssertEx.Equal<ConnectionDirection?>(ConnectionDirection.Outbound, observation.Direction);
        AssertEx.Equal("192.0.2.10", observation.LocalAddress);
        AssertEx.Equal(52144, observation.LocalPort);
        AssertEx.Equal("203.0.113.20", observation.RemoteAddress);
        AssertEx.Equal(443, observation.RemotePort);
        AssertEx.Equal<uint>(4242, observation.ProcessId);
        AssertEx.Equal<byte>(6, observation.Protocol);

        var listen = Fields(("SourceAddress", "0.0.0.0"), ("SourcePort", "8080"));
        AssertEx.True(LearningEventParser.TryParse(5154, listen, out var listenObservation));
        AssertEx.Equal(LearningEventKind.Listen, listenObservation.Kind);
        AssertEx.Equal<ConnectionDirection?>(null, listenObservation.Direction);
        AssertEx.Equal<string?>(null, listenObservation.RemoteAddress);

        AssertEx.False(LearningEventParser.TryParse(4688, listen, out _), "Unrelated events are not learning records.");
    }

    private static void ConnectionWithoutDestinationIsRejected()
    {
        var noDirection = Connection("%%14593", "192.0.2.10", "52144", "203.0.113.20", "443");
        noDirection.Remove("Direction");
        AssertEx.False(LearningEventParser.TryParse(5156, noDirection, out _));

        var unknownDirection = Connection("%%99999", "192.0.2.10", "52144", "203.0.113.20", "443");
        AssertEx.False(LearningEventParser.TryParse(5156, unknownDirection, out _));

        var noDestination = Connection("%%14593", "192.0.2.10", "52144", "203.0.113.20", "443");
        noDestination.Remove("DestAddress");
        AssertEx.False(LearningEventParser.TryParse(5156, noDestination, out _));

        var badPort = Connection("%%14593", "192.0.2.10", "52144", "203.0.113.20", "70000");
        AssertEx.False(LearningEventParser.TryParse(5156, badPort, out _));
    }

    private static void OutboundConnectionGrantsOutboundOnly()
    {
        AssertEx.True(Grants(5156, Connection("%%14593", "192.0.2.10", "52144", "203.0.113.20", "443"), out var grant));
        AssertEx.True(grant.Outbound);
        AssertEx.Equal<int?>(null, grant.TcpListenerPort);
        AssertEx.Equal<int?>(null, grant.UdpListenerPort);

        AssertEx.True(Grants(5156, Connection("%%14593", "2001:db8::10", "52144", "2001:db8::20", "53", protocol: "17"), out var udp));
        AssertEx.True(udp.Outbound);
        AssertEx.Equal<int?>(null, udp.UdpListenerPort);
    }

    private static void InboundConnectionGrantsNothing()
    {
        AssertEx.False(Grants(5156, Connection("%%14592", "203.0.113.20", "52144", "192.0.2.10", "3389"), out _));
        AssertEx.False(Grants(5156, Connection("%%14592", "192.0.2.10", "3333", "203.0.113.20", "49278"), out _));
    }

    private static void UnspecifiedRemoteGrantsNothing()
    {
        AssertEx.False(Grants(5156, Connection("%%14593", "192.0.2.10", "52144", "::", "443"), out _));
        AssertEx.False(Grants(5156, Connection("%%14593", "192.0.2.10", "52144", "0.0.0.0", "443"), out _));
        AssertEx.False(Grants(5156, Connection("%%14593", "192.0.2.10", "52144", "not-an-address", "443"), out _));
        AssertEx.False(LearningPolicy.TryGetGrant(LearningEventKind.Connection, ConnectionDirection.Outbound, 6,
            "192.0.2.10", 52144, null, out _));
        AssertEx.False(LearningPolicy.TryGetGrant(LearningEventKind.Connection, null, 6,
            "192.0.2.10", 52144, "203.0.113.20", out _), "A connection without direction grants nothing.");
    }

    private static void OtherProtocolsGrantNothing()
    {
        AssertEx.False(Grants(5156, Connection("%%14593", "192.0.2.10", "0", "203.0.113.20", "0", protocol: "1"), out _));
        AssertEx.False(Grants(5156, Connection("%%14593", "192.0.2.10", "0", "203.0.113.20", "0", protocol: "47"), out _));
    }

    private static void ListenGrantsObservedTcpPort()
    {
        AssertEx.True(Grants(5154, Fields(("SourceAddress", "0.0.0.0"), ("SourcePort", "8080")), out var grant));
        AssertEx.False(grant.Outbound, "A listen does not prove outbound use.");
        AssertEx.Equal<int?>(8080, grant.TcpListenerPort);
        AssertEx.Equal<int?>(null, grant.UdpListenerPort);

        AssertEx.True(Grants(5154, Fields(("SourceAddress", "::"), ("SourcePort", "443")), out var ipv6));
        AssertEx.Equal<int?>(443, ipv6.TcpListenerPort);

        AssertEx.False(Grants(5154, Fields(("SourceAddress", "127.0.0.1"), ("SourcePort", "8080")), out _),
            "A loopback-only listener is local traffic.");
        AssertEx.False(Grants(5154, Fields(("SourceAddress", "::1"), ("SourcePort", "8080")), out _));
        AssertEx.False(Grants(5154, Fields(("SourceAddress", "0.0.0.0"), ("SourcePort", "49152")), out _),
            "Ephemeral ports are not learned as listeners.");
        AssertEx.False(Grants(5154, Fields(("SourceAddress", "0.0.0.0"), ("SourcePort", "0")), out _));
    }

    private static void UdpBindGrantsObservedUdpPort()
    {
        AssertEx.True(Grants(5158, Fields(("SourceAddress", "0.0.0.0"), ("SourcePort", "5353"), ("Protocol", "17")), out var grant));
        AssertEx.False(grant.Outbound);
        AssertEx.Equal<int?>(null, grant.TcpListenerPort);
        AssertEx.Equal<int?>(5353, grant.UdpListenerPort);

        AssertEx.False(Grants(5158, Fields(("SourceAddress", "0.0.0.0"), ("SourcePort", "5353"), ("Protocol", "6")), out _),
            "A TCP bind precedes connect as well as listen, so it grants nothing.");
        AssertEx.False(Grants(5158, Fields(("SourceAddress", "0.0.0.0"), ("SourcePort", "61000"), ("Protocol", "17")), out _),
            "An implicit client bind uses an ephemeral port.");
        AssertEx.False(Grants(5158, Fields(("SourceAddress", "127.0.0.1"), ("SourcePort", "5353"), ("Protocol", "17")), out _));
    }

    private static void ServiceHostCheckUsesFileName()
    {
        AssertEx.True(LearningPolicy.IsServiceHost(@"C:\Windows\System32\svchost.exe"));
        AssertEx.True(LearningPolicy.IsServiceHost(@"C:\WINDOWS\SysWOW64\SVCHOST.EXE"));
        AssertEx.True(LearningPolicy.IsServiceHost(@"D:\Temp\svchost.exe"), "A copy elsewhere is still treated as a service host.");
        AssertEx.True(LearningPolicy.IsServiceHost("svchost.exe"));
        AssertEx.False(LearningPolicy.IsServiceHost(@"C:\Windows\System32\notsvchost.exe"));
        AssertEx.False(LearningPolicy.IsServiceHost(@"C:\Windows\System32\svchost.exe.bak"));
        AssertEx.False(LearningPolicy.IsServiceHost(null));
    }

    private static void KernelAndEmptyPathsAreNotLearnable()
    {
        AssertEx.False(LearningPolicy.IsLearnablePath("System"));
        AssertEx.False(LearningPolicy.IsLearnablePath("system"));
        AssertEx.False(LearningPolicy.IsLearnablePath(""));
        AssertEx.False(LearningPolicy.IsLearnablePath(null));
        AssertEx.True(LearningPolicy.IsLearnablePath(@"C:\Apps\sample.exe"));
    }

    private static void ServiceHostNeedsExactAttribution()
    {
        TimeSpan fresh = TimeSpan.FromMilliseconds(300);

        AssertEx.True(LearningPolicy.TryResolveServiceHost(new[] { "Dnscache" }, false, fresh, out string? name, out _));
        AssertEx.Equal<string?>("Dnscache", name);
        AssertEx.True(LearningPolicy.TryResolveServiceHost(new[] { "Dnscache", "dnscache " }, false, fresh, out name, out _),
            "Duplicate names of one service are one service.");
        AssertEx.Equal<string?>("Dnscache", name);

        AssertEx.False(LearningPolicy.TryResolveServiceHost(new[] { "Dnscache", "NlaSvc" }, false, fresh, out name, out _));
        AssertEx.Equal<string?>(null, name);
        AssertEx.False(LearningPolicy.TryResolveServiceHost(Array.Empty<string>(), false, fresh, out _, out _));
        AssertEx.False(LearningPolicy.TryResolveServiceHost(null, false, fresh, out _, out _), "A failed snapshot grants nothing.");
        AssertEx.False(LearningPolicy.TryResolveServiceHost(new[] { "Dnscache" }, true, fresh, out _, out _));
        AssertEx.False(LearningPolicy.TryResolveServiceHost(new[] { "Dnscache" }, false,
            LearningPolicy.MaxServiceAttributionAge + TimeSpan.FromMilliseconds(1), out _, out _),
            "A late snapshot cannot prove which service owned the process ID.");
        AssertEx.False(LearningPolicy.TryResolveServiceHost(new[] { "Dnscache" }, false,
            TimeSpan.FromSeconds(-5), out _, out _));
    }

    // Learning permits everything, so a block came from an explicit user block or
    // a blocklist; the attempt must not earn the app other access.
    private static void BlockedRecordsTeachNothing()
    {
        AssertEx.Equal<LearningEventKind?>(null, LearningEventParser.KindOf(5155));
        AssertEx.Equal<LearningEventKind?>(null, LearningEventParser.KindOf(5157));
        AssertEx.Equal<LearningEventKind?>(null, LearningEventParser.KindOf(5159));
        AssertEx.False(LearningEventParser.TryParse(5155, Fields(("SourceAddress", "0.0.0.0"), ("SourcePort", "8080")), out _));
        AssertEx.False(LearningEventParser.TryParse(5157, Connection("%%14593", "192.0.2.10", "52144", "203.0.113.20", "443"), out _));
        AssertEx.False(LearningEventParser.TryParse(5159,
            Fields(("SourceAddress", "0.0.0.0"), ("SourcePort", "5353"), ("Protocol", "17")), out _));
    }

    private static void ListenerPortListsMerge()
    {
        AssertEx.Equal("80", LearningPolicy.AddListenerPort(null, 80));
        AssertEx.Equal("80", LearningPolicy.AddListenerPort("", 80));
        AssertEx.Equal("80", LearningPolicy.AddListenerPort("80", 80));
        AssertEx.Equal("80,443", LearningPolicy.AddListenerPort("80", 443));
        AssertEx.Equal("*", LearningPolicy.AddListenerPort("*", 443));

        string? ports = null;
        for (int port = 1000; port < 1000 + LearningPolicy.MaxListenerPorts + 5; ++port)
            ports = LearningPolicy.AddListenerPort(ports, port);
        AssertEx.Equal(LearningPolicy.MaxListenerPorts, ports!.Split(',').Length);
    }
}

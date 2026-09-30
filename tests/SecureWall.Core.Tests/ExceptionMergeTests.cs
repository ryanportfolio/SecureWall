using pylorak.TinyWall;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

// SW-11: a prompt Allow reaches ServerProfileConfiguration.AddExceptions, which calls
// old.Policy.MergeRulesTo(ref promptPolicy) for a permanent exception with the same
// subject. Normalize calls MergeRulesTo in the other direction. A merge may only
// happen when one policy represents the exact union; otherwise both stay separate.
internal static class ExceptionMergeTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("prompt allow never merges into a LAN-only TCP/UDP rule with listeners", PromptAllowKeepsLanOnlyTcpUdpSeparate),
        ("prompt allow never merges into LAN-only full access", PromptAllowKeepsLanOnlyUnrestrictedSeparate),
        ("normalize order never widens a LAN-only rule with an internet rule", ReverseMergeKeepsLanOnlySeparate),
        ("prompt allow merge into an internet rule adds only outbound ports", PromptAllowMergeAddsOnlyOutbound),
        ("prompt allow merge into internet full access changes nothing", PromptAllowMergeIntoInternetUnrestricted),
        ("same-scope TCP/UDP merges keep the union and the LAN restriction", SameScopeMergesKeepUnion),
        ("rule list merges never clear a LAN-only restriction", RuleListMergesKeepLanOnly),
    };

    // Mirrors TinyWallService.ApplyPromptAllow: the rule contains only PromptAllowPolicy fields.
    private static TcpUdpPolicy PromptPolicy()
    {
        var allow = PromptAllowPolicy.Create(PromptIdentity.ForExecutable(@"C:\apps\sample.exe"));
        return new TcpUdpPolicy
        {
            AllowedRemoteTcpConnectPorts = allow.AllowedRemoteTcpConnectPorts,
            AllowedRemoteUdpConnectPorts = allow.AllowedRemoteUdpConnectPorts,
            AllowedLocalTcpListenerPorts = allow.AllowedLocalTcpListenerPorts,
            AllowedLocalUdpListenerPorts = allow.AllowedLocalUdpListenerPorts,
        };
    }

    private static TcpUdpPolicy LanServer() => new()
    {
        LocalNetworkOnly = true,
        AllowedRemoteTcpConnectPorts = "445",
        AllowedLocalTcpListenerPorts = "8080",
        AllowedLocalUdpListenerPorts = "5353",
    };

    private static void AssertLanServerUnchanged(ExceptionPolicy policy)
    {
        var tcpUdp = (TcpUdpPolicy)policy;
        AssertEx.True(tcpUdp.LocalNetworkOnly);
        AssertEx.Equal<string?>("445", tcpUdp.AllowedRemoteTcpConnectPorts);
        AssertEx.Equal<string?>(null, tcpUdp.AllowedRemoteUdpConnectPorts);
        AssertEx.Equal<string?>("8080", tcpUdp.AllowedLocalTcpListenerPorts);
        AssertEx.Equal<string?>("5353", tcpUdp.AllowedLocalUdpListenerPorts);
    }

    private static void AssertPromptPolicyUnchanged(ExceptionPolicy policy)
    {
        var tcpUdp = (TcpUdpPolicy)policy;
        AssertEx.False(tcpUdp.LocalNetworkOnly);
        AssertEx.Equal<string?>("*", tcpUdp.AllowedRemoteTcpConnectPorts);
        AssertEx.Equal<string?>("*", tcpUdp.AllowedRemoteUdpConnectPorts);
        AssertEx.Equal<string?>(null, tcpUdp.AllowedLocalTcpListenerPorts);
        AssertEx.Equal<string?>(null, tcpUdp.AllowedLocalUdpListenerPorts);
    }

    private static void PromptAllowKeepsLanOnlyTcpUdpSeparate()
    {
        ExceptionPolicy old = LanServer();
        ExceptionPolicy target = PromptPolicy();
        AssertEx.False(old.MergeRulesTo(ref target), "LAN-only listeners must not merge into an internet allow.");
        AssertLanServerUnchanged(old);
        AssertPromptPolicyUnchanged(target);
    }

    private static void PromptAllowKeepsLanOnlyUnrestrictedSeparate()
    {
        var old = new UnrestrictedPolicy { LocalNetworkOnly = true };
        ExceptionPolicy target = PromptPolicy();
        AssertEx.False(old.MergeRulesTo(ref target), "LAN-only full access must not become internet full access.");
        AssertEx.True(old.LocalNetworkOnly);
        AssertEx.Equal(PolicyType.TcpUdpOnly, target.PolicyType);
        AssertPromptPolicyUnchanged(target);
    }

    private static void ReverseMergeKeepsLanOnlySeparate()
    {
        ExceptionPolicy lanTcpUdp = LanServer();
        AssertEx.False(PromptPolicy().MergeRulesTo(ref lanTcpUdp));
        AssertLanServerUnchanged(lanTcpUdp);

        ExceptionPolicy lanUnrestricted = new UnrestrictedPolicy { LocalNetworkOnly = true };
        AssertEx.False(PromptPolicy().MergeRulesTo(ref lanUnrestricted));
        AssertEx.True(((UnrestrictedPolicy)lanUnrestricted).LocalNetworkOnly);
    }

    private static void PromptAllowMergeAddsOnlyOutbound()
    {
        var old = new TcpUdpPolicy
        {
            AllowedRemoteTcpConnectPorts = "443",
            AllowedLocalTcpListenerPorts = "8080",
        };
        ExceptionPolicy target = PromptPolicy();
        AssertEx.True(old.MergeRulesTo(ref target));
        var merged = (TcpUdpPolicy)target;
        AssertEx.False(merged.LocalNetworkOnly);
        AssertEx.Equal<string?>("*", merged.AllowedRemoteTcpConnectPorts);
        AssertEx.Equal<string?>("*", merged.AllowedRemoteUdpConnectPorts);
        // Existing listener reach is kept exactly; the prompt adds none.
        AssertEx.Equal<string?>("8080", merged.AllowedLocalTcpListenerPorts);
        AssertEx.Equal<string?>(null, merged.AllowedLocalUdpListenerPorts);
    }

    private static void PromptAllowMergeIntoInternetUnrestricted()
    {
        var old = new UnrestrictedPolicy { LocalNetworkOnly = false };
        ExceptionPolicy target = PromptPolicy();
        AssertEx.True(old.MergeRulesTo(ref target));
        AssertEx.True(ReferenceEquals(old, target));
        AssertEx.False(old.LocalNetworkOnly);

        var lanOld = new UnrestrictedPolicy { LocalNetworkOnly = true };
        ExceptionPolicy lanTarget = new TcpUdpPolicy { LocalNetworkOnly = true, AllowedRemoteTcpConnectPorts = "80" };
        AssertEx.True(lanOld.MergeRulesTo(ref lanTarget));
        AssertEx.True(((UnrestrictedPolicy)lanTarget).LocalNetworkOnly);
    }

    private static void SameScopeMergesKeepUnion()
    {
        var lanA = new TcpUdpPolicy { LocalNetworkOnly = true, AllowedRemoteTcpConnectPorts = "80", AllowedLocalTcpListenerPorts = "8080" };
        ExceptionPolicy lanB = new TcpUdpPolicy { LocalNetworkOnly = true, AllowedRemoteTcpConnectPorts = "443" };
        AssertEx.True(lanA.MergeRulesTo(ref lanB));
        var merged = (TcpUdpPolicy)lanB;
        AssertEx.True(merged.LocalNetworkOnly);
        AssertEx.Equal<string?>("443,80", merged.AllowedRemoteTcpConnectPorts);
        AssertEx.Equal<string?>("8080", merged.AllowedLocalTcpListenerPorts);

        var internetA = new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "80" };
        ExceptionPolicy internetB = new TcpUdpPolicy { AllowedRemoteUdpConnectPorts = "53" };
        AssertEx.True(internetA.MergeRulesTo(ref internetB));
        AssertEx.False(((TcpUdpPolicy)internetB).LocalNetworkOnly);
        AssertEx.Equal<string?>("80", ((TcpUdpPolicy)internetB).AllowedRemoteTcpConnectPorts);
        AssertEx.Equal<string?>("53", ((TcpUdpPolicy)internetB).AllowedRemoteUdpConnectPorts);
    }

    private static void RuleListMergesKeepLanOnly()
    {
        ExceptionPolicy lanUnrestricted = new UnrestrictedPolicy { LocalNetworkOnly = true };
        AssertEx.False(new RuleListPolicy().MergeRulesTo(ref lanUnrestricted));
        AssertEx.True(((UnrestrictedPolicy)lanUnrestricted).LocalNetworkOnly);

        var lanSource = new UnrestrictedPolicy { LocalNetworkOnly = true };
        ExceptionPolicy ruleList = new RuleListPolicy();
        AssertEx.False(lanSource.MergeRulesTo(ref ruleList));
        AssertEx.True(lanSource.LocalNetworkOnly);
        AssertEx.Equal(PolicyType.RuleList, ruleList.PolicyType);

        var internetSource = new UnrestrictedPolicy { LocalNetworkOnly = false };
        ExceptionPolicy other = new RuleListPolicy();
        AssertEx.True(internetSource.MergeRulesTo(ref other));
        AssertEx.True(ReferenceEquals(internetSource, other));
    }
}

using pylorak.TinyWall;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class WfpCoexistenceTests
{
    private static readonly Guid SecureWall = new("{053FC8F9-9052-4B2F-9B24-7DE3A2BED6E0}");
    private static readonly Guid TinyWall = WfpCoexistencePolicy.TinyWallProviderKey;
    private static readonly Guid Other = new("{11111111-2222-3333-4444-555555555555}");
    private static readonly Guid OtherSublayer = new("{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}");

    internal static IEnumerable<(string Name, Action Test)> Cases
    {
        get
        {
            yield return ("SecureWall sublayer keys are distinct from TinyWall's", SublayerKeysDifferFromTinyWall);
            yield return ("WFP coexistence accepts SecureWall-only objects", CleanInventoryIsAccepted);
            yield return ("WFP coexistence refuses TinyWall's provider", TinyWallProviderIsRefused);
            yield return ("WFP coexistence refuses TinyWall-owned legacy sublayers", TinyWallLegacySublayerIsRefused);
            yield return ("WFP coexistence refuses foreign filters in SecureWall sublayers", ForeignFilterInOwnSublayerIsRefused);
            yield return ("WFP coexistence refuses foreign owners of SecureWall sublayer keys", ForeignOwnerOfOwnKeyIsRefused);
            yield return ("WFP coexistence ignores foreign filters in foreign sublayers", ForeignFilterElsewhereIsIgnored);
            yield return ("WFP removal deletes only sublayers SecureWall's provider owns", RemovalDeletesOnlyOwnedSublayers);
            yield return ("WFP removal is blocked only by filters in SecureWall-owned sublayers", RemovalBlockedOnlyByOwnedSublayerFilters);
        }
    }

    private static Guid[] Own() => new[]
    {
        WfpSublayerKeys.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6, WfpSublayerKeys.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4,
        WfpSublayerKeys.FWPM_LAYER_INBOUND_ICMP_ERROR_V6, WfpSublayerKeys.FWPM_LAYER_INBOUND_ICMP_ERROR_V4,
        WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_CONNECT_V6, WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_CONNECT_V4,
        WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_LISTEN_V6, WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_LISTEN_V4,
        WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6, WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4,
        WfpSublayerKeys.FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD, WfpSublayerKeys.FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD,
        WfpSublayerKeys.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6, WfpSublayerKeys.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4,
    };

    private static Guid[] Legacy => WfpSublayerKeys.Legacy;

    private static WfpCoexistenceReport Activation(IEnumerable<Guid> providers, IEnumerable<WfpSublayerRecord> sublayers,
        IEnumerable<WfpFilterRecord> filters) =>
        WfpCoexistencePolicy.FindActivationConflicts(SecureWall, providers, sublayers, filters, Own(), Legacy);

    private static void SublayerKeysDifferFromTinyWall()
    {
        Guid[] own = Own();
        AssertEx.Equal(14, own.Distinct().Count());
        AssertEx.Equal(14, Legacy.Distinct().Count());
        AssertEx.False(own.Intersect(Legacy).Any(), "SecureWall must not reuse a TinyWall sublayer key.");
        // TinyWall 3.5.1 ALE_AUTH_CONNECT_V4 and OUTBOUND_ICMP_ERROR_V6 sublayer keys.
        AssertEx.True(Legacy.Contains(new Guid("{3C6B5A3E-7413-4BA0-8B2D-28C36E39FE52}")));
        AssertEx.True(Legacy.Contains(new Guid("{745777F7-5092-4706-95C1-B74A0092E78A}")));
        AssertEx.False(own.Contains(Guid.Empty));
    }

    private static void CleanInventoryIsAccepted()
    {
        Guid[] own = Own();
        var report = Activation(new[] { SecureWall, Other },
            own.Select(k => new WfpSublayerRecord(k, SecureWall))
                .Append(new WfpSublayerRecord(Legacy[0], SecureWall))
                .Append(new WfpSublayerRecord(OtherSublayer, Other)),
            new[]
            {
                new WfpFilterRecord(Guid.NewGuid(), SecureWall, own[4], "SecureWall recovery default deny"),
                new WfpFilterRecord(Guid.NewGuid(), SecureWall, Legacy[0], "older SecureWall filter"),
            });
        AssertEx.True(report.Clear);
    }

    private static void TinyWallProviderIsRefused()
    {
        var report = Activation(new[] { SecureWall, TinyWall },
            Array.Empty<WfpSublayerRecord>(), Array.Empty<WfpFilterRecord>());
        AssertEx.False(report.Clear);
        AssertEx.True(report.TinyWallResidue);
        AssertEx.False(report.ForeignResidue);
        string message = report.Describe("SecureWall installation");
        AssertEx.True(message.StartsWith("SecureWall installation refused.", StringComparison.Ordinal));
        AssertEx.True(message.Contains("{66CA412C-4453-4F1E-A973-C16E433E34D0}"));
        AssertEx.True(message.Contains("TinyWall with its own uninstaller"));
    }

    private static void TinyWallLegacySublayerIsRefused()
    {
        var withProvider = Activation(Array.Empty<Guid>(),
            new[] { new WfpSublayerRecord(Legacy[5], TinyWall) }, Array.Empty<WfpFilterRecord>());
        AssertEx.Equal(1, withProvider.Conflicts.Count);
        AssertEx.True(withProvider.TinyWallResidue);

        var withoutProvider = Activation(Array.Empty<Guid>(),
            new[] { new WfpSublayerRecord(Legacy[5], null) }, Array.Empty<WfpFilterRecord>());
        AssertEx.Equal(1, withoutProvider.Conflicts.Count);
        AssertEx.True(withoutProvider.Conflicts[0].Contains("(none)"));
    }

    private static void ForeignFilterInOwnSublayerIsRefused()
    {
        Guid own = Own()[5];
        Guid filterKey = Guid.NewGuid();
        var report = Activation(new[] { SecureWall, Other },
            new[] { new WfpSublayerRecord(own, SecureWall) },
            new[] { new WfpFilterRecord(filterKey, Other, own, "third-party permit") });
        AssertEx.Equal(1, report.Conflicts.Count);
        AssertEx.True(report.ForeignResidue);
        AssertEx.False(report.TinyWallResidue);
        string message = report.Describe("SecureWall baseline registration");
        AssertEx.True(message.Contains(filterKey.ToString("B").ToUpperInvariant()));
        AssertEx.True(message.Contains(Other.ToString("B").ToUpperInvariant()));
        AssertEx.True(message.Contains("netsh wfp show filters"));

        // A filter with no provider is foreign too, and TinyWall filters in a
        // SecureWall-owned legacy sublayer name the TinyWall cleanup step.
        var providerless = Activation(Array.Empty<Guid>(), Array.Empty<WfpSublayerRecord>(),
            new[] { new WfpFilterRecord(Guid.NewGuid(), Guid.Empty, own, "no provider") });
        AssertEx.True(providerless.Conflicts.Single().Contains("from provider (none)"));
        var tinyWallFilter = Activation(Array.Empty<Guid>(),
            new[] { new WfpSublayerRecord(Legacy[1], SecureWall) },
            new[] { new WfpFilterRecord(Guid.NewGuid(), TinyWall, Legacy[1], "TinyWall permit") });
        AssertEx.True(tinyWallFilter.TinyWallResidue);
        AssertEx.False(tinyWallFilter.ForeignResidue);
    }

    private static void ForeignOwnerOfOwnKeyIsRefused()
    {
        var report = Activation(Array.Empty<Guid>(),
            new[] { new WfpSublayerRecord(Own()[0], Other) }, Array.Empty<WfpFilterRecord>());
        AssertEx.Equal(1, report.Conflicts.Count);
        AssertEx.True(report.ForeignResidue);
    }

    private static void ForeignFilterElsewhereIsIgnored()
    {
        var report = Activation(new[] { SecureWall, Other },
            new[] { new WfpSublayerRecord(OtherSublayer, Other) },
            new[]
            {
                new WfpFilterRecord(Guid.NewGuid(), Other, OtherSublayer, "Windows Defender Firewall rule"),
                new WfpFilterRecord(Guid.NewGuid(), Guid.Empty, Guid.Empty, "unsublayered filter"),
            });
        AssertEx.True(report.Clear);
    }

    private static void RemovalDeletesOnlyOwnedSublayers()
    {
        Guid[] own = Own();
        var sublayers = new[]
        {
            new WfpSublayerRecord(own[0], SecureWall),
            new WfpSublayerRecord(own[1], Other),
            new WfpSublayerRecord(Legacy[2], SecureWall),
            new WfpSublayerRecord(Legacy[3], TinyWall),
            new WfpSublayerRecord(Legacy[4], null),
            new WfpSublayerRecord(OtherSublayer, SecureWall),
        };
        var remove = WfpCoexistencePolicy.SublayersToRemove(SecureWall, sublayers, own, Legacy);
        AssertEx.SequenceEqual(new[] { own[0], Legacy[2] }, remove);
    }

    private static void RemovalBlockedOnlyByOwnedSublayerFilters()
    {
        Guid[] own = Own();
        var sublayers = new[]
        {
            new WfpSublayerRecord(own[4], SecureWall),
            new WfpSublayerRecord(Legacy[4], SecureWall),
            new WfpSublayerRecord(Legacy[5], TinyWall),
        };

        // TinyWall's own objects never block SecureWall's removal.
        var unblocked = WfpCoexistencePolicy.FindRemovalConflicts(SecureWall, sublayers, new[]
        {
            new WfpFilterRecord(Guid.NewGuid(), SecureWall, own[4], "SecureWall recovery default deny"),
            new WfpFilterRecord(Guid.NewGuid(), TinyWall, Legacy[5], "TinyWall permit"),
        }, own, Legacy);
        AssertEx.True(unblocked.Clear);

        var blocked = WfpCoexistencePolicy.FindRemovalConflicts(SecureWall, sublayers, new[]
        {
            new WfpFilterRecord(Guid.NewGuid(), TinyWall, Legacy[4], "TinyWall permit in SecureWall-owned sublayer"),
        }, own, Legacy);
        AssertEx.Equal(1, blocked.Conflicts.Count);
        AssertEx.True(blocked.TinyWallResidue);
    }
}

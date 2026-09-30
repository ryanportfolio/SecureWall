using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

// Upstream 2ed2d37 shows which filter group blocked a connection. SecureWall derives the
// group from the committed runtime filter ID; anything unmapped shows a plain "Blocked".
internal static class BlockReasonTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("block rule weights map to their filter group", BlockRuleWeightsMapToGroups),
        ("blocked display text names the group only when known", BlockedDisplayTextNamesKnownGroup),
        ("filter group map replaces, extends and fails to unknown", FilterGroupMapPublishesSnapshots),
        ("filter group map overflow clears instead of truncating", FilterGroupMapOverflowClears),
        ("service publishes filter groups only after commit", ServicePublishesFilterGroupsAfterCommit),
    };

    private const ulong Blocklist = 9000000, UserBlock = 6000000, UserPermit = 5000000, DefaultBlock = 3000000;

    private static void BlockRuleWeightsMapToGroups()
    {
        AssertEx.Equal(FilterGroup.Blocklist, FilterGroupClassifier.ForBlockRule(Blocklist, Blocklist, UserBlock, DefaultBlock));
        AssertEx.Equal(FilterGroup.User, FilterGroupClassifier.ForBlockRule(UserBlock, Blocklist, UserBlock, DefaultBlock));
        AssertEx.Equal(FilterGroup.DefaultAction, FilterGroupClassifier.ForBlockRule(DefaultBlock, Blocklist, UserBlock, DefaultBlock));
        AssertEx.Equal(FilterGroup.Unknown, FilterGroupClassifier.ForBlockRule(UserPermit, Blocklist, UserBlock, DefaultBlock));
        AssertEx.Equal(FilterGroup.Unknown, FilterGroupClassifier.ForBlockRule(DefaultBlock - 2, Blocklist, UserBlock, DefaultBlock));
    }

    private static void BlockedDisplayTextNamesKnownGroup()
    {
        string Blocked(FilterGroup group) => NetworkActivityStatusClassifier.ToDisplayText(NetworkActivityStatus.Blocked, group);
        AssertEx.Equal("Blocked", Blocked(FilterGroup.Unknown));
        AssertEx.Equal("Blocked", Blocked((FilterGroup)99));
        AssertEx.Equal("Blocked (default deny)", Blocked(FilterGroup.DefaultAction));
        AssertEx.Equal("Blocked (port scan protection)", Blocked(FilterGroup.PortScan));
        AssertEx.Equal("Blocked (raw socket)", Blocked(FilterGroup.RawSocket));
        AssertEx.Equal("Blocked (port blocklist)", Blocked(FilterGroup.Blocklist));
        AssertEx.Equal("Blocked (user rule)", Blocked(FilterGroup.User));
        AssertEx.Equal("Allowed", NetworkActivityStatusClassifier.ToDisplayText(NetworkActivityStatus.Allowed, FilterGroup.User));
        AssertEx.Equal("Listening (local endpoint)",
            NetworkActivityStatusClassifier.ToDisplayText(NetworkActivityStatus.Listening, FilterGroup.DefaultAction));
    }

    private static KeyValuePair<ulong, FilterGroup> Pair(ulong id, FilterGroup group) => new(id, group);

    private static void FilterGroupMapPublishesSnapshots()
    {
        var map = new FilterGroupMap();
        AssertEx.Equal(FilterGroup.Unknown, map.Lookup(1));

        AssertEx.True(map.Replace(new[] { Pair(1, FilterGroup.DefaultAction), Pair(2, FilterGroup.User), Pair(3, FilterGroup.Unknown) }));
        AssertEx.Equal(FilterGroup.DefaultAction, map.Lookup(1));
        AssertEx.Equal(FilterGroup.User, map.Lookup(2));
        AssertEx.Equal(FilterGroup.Unknown, map.Lookup(3));

        // A full replacement drops IDs of the previous policy generation.
        AssertEx.True(map.Replace(new[] { Pair(10, FilterGroup.PortScan) }));
        AssertEx.Equal(FilterGroup.Unknown, map.Lookup(1));
        AssertEx.Equal(FilterGroup.PortScan, map.Lookup(10));

        // Incremental child-rule commits extend the current generation.
        AssertEx.True(map.Add(new[] { Pair(11, FilterGroup.User) }));
        AssertEx.Equal(FilterGroup.PortScan, map.Lookup(10));
        AssertEx.Equal(FilterGroup.User, map.Lookup(11));

        // Lost pending data (allocation or capacity failure) clears rather than keeping stale groups.
        AssertEx.False(map.Add(null));
        AssertEx.Equal(FilterGroup.Unknown, map.Lookup(10));
        AssertEx.True(map.Replace(new[] { Pair(20, FilterGroup.RawSocket) }));
        AssertEx.False(map.Replace(null));
        AssertEx.Equal(FilterGroup.Unknown, map.Lookup(20));

        AssertEx.True(map.Replace(new[] { Pair(30, FilterGroup.Blocklist) }));
        map.Clear();
        AssertEx.Equal(FilterGroup.Unknown, map.Lookup(30));
    }

    private static void FilterGroupMapOverflowClears()
    {
        var map = new FilterGroupMap();
        AssertEx.True(map.Replace(Enumerable.Range(1, FilterGroupMap.Capacity).Select(i => Pair((ulong)i, FilterGroup.User))));
        AssertEx.Equal(FilterGroup.User, map.Lookup((ulong)FilterGroupMap.Capacity));
        AssertEx.False(map.Add(new[] { Pair(ulong.MaxValue, FilterGroup.User) }));
        AssertEx.Equal(FilterGroup.Unknown, map.Lookup(1));
        AssertEx.False(map.Replace(Enumerable.Range(1, FilterGroupMap.Capacity + 1).Select(i => Pair((ulong)i, FilterGroup.User))));
        AssertEx.Equal(FilterGroup.Unknown, map.Lookup(1));
    }

    private static void ServicePublishesFilterGroupsAfterCommit()
    {
        string service = Read("TinyWallService.cs");

        string full = Section(service, "private void InstallFirewallRulesCore()", "private void EnsureRestrictiveBaseline()");
        int commit = full.IndexOf("trx.Commit();", StringComparison.Ordinal);
        int publish = full.IndexOf("FilterGroups.Replace(PendingFilterGroups);", StringComparison.Ordinal);
        AssertEx.True(commit > 0 && publish > commit, "Full reload must publish filter groups only after the WFP commit.");
        AssertEx.True(Section(full, "finally", "if (!committed)").Contains("PendingFilterGroups = null;"));

        string incremental = Section(service, "private List<ulong> InstallRules(", "private void InstallFirewallRules()");
        commit = incremental.IndexOf("trx?.Commit();", StringComparison.Ordinal);
        publish = incremental.IndexOf("FilterGroups.Add(PendingFilterGroups);", StringComparison.Ordinal);
        AssertEx.True(commit > 0 && publish > commit, "Incremental rules must publish filter groups only after the WFP commit.");

        string install = Section(service, "private IReadOnlyList<ulong> InstallWfpFilter(", "private void ConstructFilter(");
        AssertEx.True(install.IndexOf("WfpFilterPairRegistration.Register(", StringComparison.Ordinal)
            < install.IndexOf("PendingFilterGroups.Add(", StringComparison.Ordinal),
            "Groups are recorded only for filters that registered.");
        AssertEx.True(install.Contains("catch { PendingFilterGroups = null; }"), "Display data must never reject a rule.");

        string callback = Section(service, "private void WfpNetEventCallback(", "lock (FirewallLogEntries)");
        AssertEx.True(System.Text.RegularExpressions.Regex.IsMatch(callback,
            @"if \(eventType == EventLogEvent\.BLOCKED\)\s+entry\.FilterGroup = FilterGroups\.Lookup\(data\.filterId\.Value\);"),
            "Only drops carry a block reason.");

        int clears = 0;
        for (int at = service.IndexOf("FilterGroups.Clear();", StringComparison.Ordinal); at >= 0;
             at = service.IndexOf("FilterGroups.Clear();", at + 1, StringComparison.Ordinal))
            clears++;
        AssertEx.Equal(2, clears, "Runtime withdrawal and disposal must clear block reasons with the promptable IDs.");

        // Upstream moved the net-event subscription into a try/catch after the first rule
        // install; SecureWall keeps a failed subscription fatal to initialization.
        AssertEx.True(service.Contains("Diagnostics.Run(RuntimeEvent.wfp_subscribe, () => RuntimeEventSubscription = WfpEngine.SubscribeNetEvent(WfpNetEventCallback));"));
        AssertEx.False(service.Contains("WfpNetEventSubscription ??="));
    }

    private static string Read(string file)
    {
        DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "TinyWall", "Utils.cs"))) root = root.Parent;
        AssertEx.True(root != null);
        return File.ReadAllText(Path.Combine(root!.FullName, "TinyWall", file));
    }

    private static string Section(string text, string begin, string end)
    {
        int start = text.IndexOf(begin, StringComparison.Ordinal);
        AssertEx.True(start >= 0, "Missing " + begin);
        int stop = text.IndexOf(end, start, StringComparison.Ordinal);
        AssertEx.True(stop > start, "Missing " + end);
        return text.Substring(start, stop - start);
    }
}

using pylorak.TinyWall;

namespace SecureWall.Core.Tests;

// Which ancestor passes "child processes inherit" rules to a process (TinyWall/RuleInheritance.cs),
// against the owner's profile on 2026-11-02.
internal static class RuleInheritanceTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases
    {
        get
        {
            yield return ("inheritance: store app under svchost and services.exe inherits nothing", OwnersStoreAppInheritsNothing);
            yield return ("inheritance: package rule alone skips inheritance", PackageRuleSkipsInheritance);
            yield return ("inheritance: nearest ruled non-inheriting ancestor stops the walk", RuledAncestorStopsWalk);
            yield return ("inheritance: ancestor with only a package rule stops the walk", PackageRuledAncestorStopsWalk);
            yield return ("inheritance: pathless ruleless parent is skipped for inheriting grandparent", PathlessParentIsSkipped);
            yield return ("inheritance: ruled ancestor that inherits still passes rules", RuledInheritingAncestorPasses);
            yield return ("inheritance: exe path rule still skips inheritance", ExeRuleSkipsInheritance);
            yield return ("inheritance: already inherited parent ends the walk", AlreadyInheritedEndsWalk);
            yield return ("inheritance: service-scoped rule on an ancestor does not stop the walk", ServiceRuledAncestorDoesNotStopWalk);
        }
    }

    private const string Services = @"C:\Windows\System32\services.exe";
    private const string SvcHost = @"C:\Windows\System32\svchost.exe";
    private const string StoreApp = @"C:\Program Files\WindowsApps\Microsoft.WindowsStore_22410.1401.5.0_x64__8wekyb3d8bbwe\WinStore.App.exe";
    private const string StoreSid = "S-1-15-2-1609473798-1231923017-684268153-4268514328-882773646-2760585773-1760938157";
    private const string Tool = @"C:\Tools\tool.exe";
    private const string Shell = @"C:\Windows\explorer.exe";

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    private static string? Find(
        string path,
        string? packageSid,
        (string? Path, string? PackageSid)[] ancestors,
        HashSet<string> exeRules,
        HashSet<string> packageRules,
        HashSet<string> inheriting,
        Func<string, bool>? alreadyInherited = null,
        HashSet<string>? ancestorExeRules = null) =>
        RuleInheritance.FindInheritedParent(path, packageSid, ancestors, exeRules, ancestorExeRules ?? exeRules, packageRules,
            inheriting.Contains, alreadyInherited ?? (_ => false));

    // services.exe: HardBlock + ChildProcessesInherit. svchost.exe: own TcpUdpOnly, not inheriting.
    // Microsoft.WindowsStore: package rule. Before the fix the walk passed svchost and gave the
    // store app the services.exe block at UserBlock weight, above its package permit.
    private static void OwnersStoreAppInheritsNothing()
    {
        AssertEx.Equal<string?>(null, Find(StoreApp, StoreSid,
            new (string?, string?)[] { (SvcHost, null), (Services, null) },
            Set(Services, SvcHost), Set(StoreSid), Set(Services)));
    }

    private static void PackageRuleSkipsInheritance()
    {
        AssertEx.Equal<string?>(null, Find(StoreApp, StoreSid,
            new (string?, string?)[] { (Services, null) },
            Set(Services), Set(StoreSid), Set(Services)));
    }

    private static void RuledAncestorStopsWalk()
    {
        AssertEx.Equal<string?>(null, Find(Tool, null,
            new (string?, string?)[] { (SvcHost, null), (Services, null) },
            Set(Services, SvcHost), Set(), Set(Services)));
    }

    private static void PackageRuledAncestorStopsWalk()
    {
        AssertEx.Equal<string?>(null, Find(Tool, null,
            new (string?, string?)[] { (StoreApp, StoreSid), (Services, null) },
            Set(Services), Set(StoreSid), Set(Services)));
    }

    private static void PathlessParentIsSkipped()
    {
        AssertEx.Equal<string?>(Services, Find(Tool, null,
            new (string?, string?)[] { (null, null), (string.Empty, null), (Services, null) },
            Set(Services), Set(), Set(Services)));
    }

    private static void RuledInheritingAncestorPasses()
    {
        AssertEx.Equal<string?>(Services, Find(Tool, null,
            new (string?, string?)[] { (Shell, null), (Services, null) },
            Set(Services), Set(), Set(Services)));
        AssertEx.Equal<string?>(SvcHost, Find(Tool, null,
            new (string?, string?)[] { (SvcHost, null), (Services, null) },
            Set(Services, SvcHost), Set(), Set(Services, SvcHost)));
    }

    private static void ExeRuleSkipsInheritance()
    {
        AssertEx.Equal<string?>(null, Find(Tool, null,
            new (string?, string?)[] { (Services, null) },
            Set(Services, Tool.ToUpperInvariant()), Set(), Set(Services)));
    }

    private static void AlreadyInheritedEndsWalk()
    {
        AssertEx.Equal<string?>(null, Find(Tool, null,
            new (string?, string?)[] { (Services, null) },
            Set(Services), Set(), Set(Services), p => p == Services));
    }

    // A service rule (Windows_Update's wuauserv) puts svchost.exe in the child-skip set but not in the
    // executable-wide set, so svchost-hosted processes still inherit from services.exe.
    private static void ServiceRuledAncestorDoesNotStopWalk()
    {
        AssertEx.Equal<string?>(Services, Find(Tool, null,
            new (string?, string?)[] { (SvcHost, null), (Services, null) },
            Set(Services, SvcHost), Set(), Set(Services), ancestorExeRules: Set(Services)));
        AssertEx.Equal<string?>(null, Find(Tool, null,
            new (string?, string?)[] { (SvcHost, null), (Services, null) },
            Set(Services, SvcHost), Set(), Set(Services), ancestorExeRules: Set(Services, SvcHost)));
    }
}

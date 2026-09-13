using System.Collections.Concurrent;
using System.Text.Json;
using pylorak.TinyWall;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class RuntimeDiagnosticCoverageTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("diagnostic filter snapshots publish exact immutable membership and clear on failure", FilterSnapshots),
        ("port drop aggregation is opt-in bounded and does not enqueue per packet", DropAggregation),
        ("hosts diagnostics observe backup install restoration readback and DNS ordering", HostsCycle),
        ("hosts diagnostic observer failures preserve original policy exception", ObserverFailure),
        ("hosts DNS false and throwing callbacks are visible without failing restoration", DnsFailures),
        ("hosts readback is off by default and skips oversized originals", VerificationBounds),
        ("hosts readback failure is visible and preserves existing cleanup semantics", VerificationFailure),
        ("missing hosts original is an explicit successful no-op without DNS", AbsentOriginal),
    };

    private static void FilterSnapshots()
    {
        var set = new DiagnosticFilterSet();
        var source = new List<ulong> { 7, 9 };
        AssertEx.True(set.Replace(source));
        source.Clear();
        AssertEx.True(set.Contains(7) && set.Contains(9));
        AssertEx.False(set.Contains(8));
        AssertEx.True(set.Replace(new ulong[] { 10 }));
        AssertEx.False(set.Contains(7));
        AssertEx.True(set.Contains(10));
        AssertEx.False(set.Replace(ThrowingIds()));
        AssertEx.False(set.Any || set.Contains(10));
        AssertEx.True(set.Replace(new ulong[] { 20 }));
        set.Clear();
        AssertEx.False(set.Any);
        AssertEx.False(set.Replace(Enumerable.Repeat(1UL, DiagnosticFilterSet.Capacity + 1)));
        AssertEx.False(set.Any);
    }

    private static IEnumerable<ulong> ThrowingIds()
    {
        yield return 30;
        throw new IOException("private diagnostic enumeration failure");
    }

    private sealed class Sink : IRuntimeJournalSink
    {
        internal readonly ConcurrentQueue<string> Lines = new();
        public void Append(string line) => Lines.Enqueue(line);
    }

    private static void DropAggregation()
    {
        var sink = new Sink();
        using var journal = new RuntimeJournal(() => sink, 500, heartbeatMilliseconds: int.MaxValue);
        journal.SetEnabled(false);
        journal.ObservePortBlocklistDrop();
        journal.SetEnabled(true);
        Parallel.For(0, 10000, _ => journal.ObservePortBlocklistDrop());
        journal.Emit(RuntimeEvent.heartbeat, RuntimeResult.observed);
        journal.SetEnabled(false);
        journal.ObservePortBlocklistDrop();
        journal.Dispose();
        AssertEx.True(journal.WaitForExit(6000));
        AssertEx.True(sink.Lines.Count <= 3);
        var rows = sink.Lines.Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            AssertEx.True(rows.Any(row => row.RootElement.GetProperty("observed_port_blocklist_drop").GetInt64() == 10000));
            AssertEx.True(rows.All(row => row.RootElement.GetProperty("observed_port_blocklist_drop").GetInt64() <= 10000));
            AssertEx.Equal(0L, journal.DroppedRecords);
        }
        finally { foreach (var row in rows) row.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "SecureWall-diagnostics-" + Guid.NewGuid().ToString("N"));
        internal string Hosts => Path.Combine(Root, "hosts");
        internal string Original => Path.Combine(Root, "hosts.orig");
        internal string Backup => Path.Combine(Root, "hosts.bck");
        internal readonly List<(RuntimeEvent Event, RuntimeResult Result, int HResult)> Events = new();
        internal readonly HostsFileManager Manager;
        internal int Flushes;
        internal Fixture(Func<bool>? dns = null)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Hosts, "original");
            File.WriteAllText(Backup, "blocklist");
            Manager = new HostsFileManager(Hosts, Root, () => { Flushes++; return dns?.Invoke() ?? true; });
            Manager.DiagnosticObserver = (code, result, hr) => Events.Add((code, result, hr));
            Manager.DiagnosticEnabled = () => true;
        }
        internal bool Has(RuntimeEvent code, RuntimeResult result) => Events.Any(e => e.Event == code && e.Result == result);
        public void Dispose() { Manager.Dispose(); Directory.Delete(Root, true); }
    }

    private static void HostsCycle()
    {
        using var f = new Fixture();
        f.Manager.EnableProtection = true;
        f.Manager.EnableHostsFile();
        AssertEx.True(f.Has(RuntimeEvent.hosts_backup, RuntimeResult.success));
        AssertEx.True(f.Has(RuntimeEvent.hosts_install, RuntimeResult.success));
        f.Events.Clear();
        f.Manager.DisableHostsFile();
        AssertEx.True(f.Has(RuntimeEvent.hosts_restore_verify, RuntimeResult.success));
        AssertEx.True(f.Has(RuntimeEvent.hosts_restore, RuntimeResult.success));
        AssertEx.True(f.Has(RuntimeEvent.dns_flush, RuntimeResult.success));
        AssertEx.True(f.Events.FindIndex(e => e.Event == RuntimeEvent.hosts_restore_verify && e.Result == RuntimeResult.success) <
            f.Events.FindIndex(e => e.Event == RuntimeEvent.dns_flush));
        AssertEx.Equal("original", File.ReadAllText(f.Hosts));
        AssertEx.False(File.Exists(f.Original));
    }

    private static void ObserverFailure()
    {
        using var f = new Fixture();
        f.Manager.DiagnosticObserver = (_, _, _) => throw new InvalidOperationException("diagnostics failed");
        AssertEx.True(f.Manager.EnableHostsFile());
        using (var held = new FileStream(f.Hosts, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            AssertEx.Throws<IOException>(() => f.Manager.DisableHostsFile());
        AssertEx.True(File.Exists(f.Original));
        AssertEx.True(f.Manager.DisableHostsFile());
    }

    private static void DnsFailures()
    {
        var error = new IOException("private DNS error");
        foreach (Func<bool> dns in new Func<bool>[] { () => false, () => throw error })
        {
            using var f = new Fixture(dns);
            AssertEx.True(f.Manager.EnableHostsFile());
            AssertEx.True(f.Manager.DisableHostsFile());
            AssertEx.True(f.Has(RuntimeEvent.dns_flush, RuntimeResult.failure));
            AssertEx.False(f.Has(RuntimeEvent.dns_flush, RuntimeResult.success));
            AssertEx.False(File.Exists(f.Original));
            AssertEx.Equal(2, f.Flushes);
        }
    }

    private static void VerificationBounds()
    {
        using (var f = new Fixture())
        {
            f.Manager.DiagnosticEnabled = null;
            f.Manager.EnableHostsFile();
            f.Manager.DisableHostsFile();
            AssertEx.False(f.Events.Any(e => e.Event == RuntimeEvent.hosts_restore_verify));
        }
        using (var f = new Fixture())
        {
            File.WriteAllText(f.Hosts, new string('x', 1024 * 1024 + 1));
            f.Manager.EnableHostsFile();
            f.Manager.DisableHostsFile();
            AssertEx.True(f.Has(RuntimeEvent.hosts_restore_verify, RuntimeResult.skipped));
            AssertEx.False(f.Has(RuntimeEvent.hosts_restore_verify, RuntimeResult.success));
            AssertEx.False(File.Exists(f.Original));
        }
    }

    private static void VerificationFailure()
    {
        using var f = new Fixture();
        f.Manager.EnableHostsFile();
        f.Manager.DiagnosticObserver = (code, result, hr) =>
        {
            f.Events.Add((code, result, hr));
            if (code == RuntimeEvent.hosts_restore_verify && result == RuntimeResult.attempt)
                File.WriteAllText(f.Hosts, "changed concurrently");
        };
        AssertEx.True(f.Manager.DisableHostsFile());
        AssertEx.True(f.Has(RuntimeEvent.hosts_restore_verify, RuntimeResult.failure));
        AssertEx.True(f.Has(RuntimeEvent.hosts_restore, RuntimeResult.success));
        AssertEx.False(File.Exists(f.Original));
    }

    private static void AbsentOriginal()
    {
        using var f = new Fixture();
        AssertEx.True(f.Manager.DisableHostsFile());
        AssertEx.True(f.Has(RuntimeEvent.hosts_restore, RuntimeResult.absent));
        AssertEx.True(f.Has(RuntimeEvent.hosts_restore, RuntimeResult.success));
        AssertEx.False(f.Events.Any(e => e.Event == RuntimeEvent.hosts_restore_verify || e.Event == RuntimeEvent.dns_flush));
        AssertEx.Equal(0, f.Flushes);
    }
}

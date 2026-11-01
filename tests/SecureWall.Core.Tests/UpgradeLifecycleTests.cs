using System.Text;
using System.Text.RegularExpressions;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class UpgradeLifecycleTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("upgrade transaction argument is exactly one braced GUID", TransactionArgument),
        ("upgrade marker records transaction and stop phase", MarkerFormat),
        ("upgrade stop checks, marks, disables restarts, rechecks, stops, then snapshots", StopOrder),
        ("upgrade stop refuses before any change unless registered, running and checked", StopRefusesEarly),
        ("upgrade stop aborts when the service crashed before restarts were disabled", StopCrashBeforeRecoveryOff),
        ("upgrade stop never snapshots a service that did not stop or started again", StopNeverSnapshotsRunningService),
        ("upgrade start requires registration, stopped service, finished stop and verified snapshot", StartPreconditions),
        ("upgrade start waits for Running and fails fast on Stopped", WaitForRunning),
        ("upgrade marker blocks automatic starts and owns recovery only while an installation runs", MarkerGate),
        ("upgrade restore is decided by the marker phase, not the service state", RestoreDecision),
        ("upgrade snapshot names the transaction and verifies only with a matching hash manifest", SnapshotVerification),
        ("upgrade snapshot restores an exact set with the secret DACL on pwd", SnapshotRoundTrip),
        ("upgrade retry sets an earlier snapshot of the same transaction aside, never restoring it", SnapshotRetry),
        ("upgrade snapshot manifest rejects unknown formats, names and hashes", ManifestValidation),
        ("upgrade snapshot covers every policy file the service writes", SnapshotCoversServiceFiles),
        ("upgrade stale snapshot cleanup keeps only the named transaction", StaleSnapshots),
        ("upgrade commit keeps only a snapshot a foreign marker points at", CommitKeeps),
        ("product registry key goes only after both journals are clear", RegistryRemoval),
        ("upgrade adapters keep the step order and every automatic start gated", AdapterWiring),
    };

    private const string Txn = "{0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0}";
    private const string Other = "{11111111-2222-3333-4444-555555555555}";
    private const string Data = @"C:\ProgramData\SecureWall";

    private sealed class FakeService : IUpgradeService
    {
        internal readonly List<string> Log;
        internal LifecycleServiceState Current = LifecycleServiceState.Running;
        internal bool StopWorks = true;
        internal bool IsRegistered = true;
        internal bool? Restart;
        internal FakeService(List<string> log) => Log = log;
        public bool Registered => IsRegistered;
        public LifecycleServiceState State => Current;
        public void Stop() { Log.Add("stop"); if (StopWorks) Current = LifecycleServiceState.Stopped; }
        public void Start() { Log.Add("start"); Current = LifecycleServiceState.StartPending; }
        public void SetRestartOnFailure(bool enabled) { Log.Add("restart=" + enabled); Restart = enabled; }
    }

    private sealed class FakeFiles : IUpgradeFiles
    {
        internal readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Secret = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Log = new();

        public bool FileExists(string path) => Files.ContainsKey(path);
        public bool DirectoryExists(string path) => Dirs.Contains(path);
        public byte[] ReadFile(string path) => Files.TryGetValue(path, out byte[]? bytes) ? bytes : throw new FileNotFoundException(path);
        public void WriteFile(string path, byte[] contents, bool secret)
        {
            if (!Dirs.Contains(Path.GetDirectoryName(path)!)) throw new DirectoryNotFoundException(path);
            Files[path] = contents.ToArray();
            if (secret) Secret.Add(path); else Secret.Remove(path);
            Log.Add("write " + Path.GetFileName(path));
        }
        public void DeleteFile(string path) { Files.Remove(path); Log.Add("delete " + Path.GetFileName(path)); }
        public void CreateDirectory(string path)
        {
            for (string? dir = path; dir != null && dir.Length > Data.Length - 1; dir = Path.GetDirectoryName(dir)) Dirs.Add(dir);
        }
        public void DeleteDirectory(string path)
        {
            foreach (string file in Files.Keys.Where(f => f.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) Files.Remove(file);
            foreach (string dir in Dirs.Where(d => d.Equals(path, StringComparison.OrdinalIgnoreCase) || d.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) Dirs.Remove(dir);
            Log.Add("rmdir " + Path.GetFileName(path));
        }
        public void MoveDirectory(string path, string destination)
        {
            foreach (string file in Files.Keys.Where(f => f.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                Files[destination + file.Substring(path.Length)] = Files[file];
                Files.Remove(file);
            }
            foreach (string dir in Dirs.Where(d => d.Equals(path, StringComparison.OrdinalIgnoreCase) || d.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                Dirs.Remove(dir);
                Dirs.Add(destination + dir.Substring(path.Length));
            }
            Log.Add("move " + Path.GetFileName(path));
        }
        public string[] Directories(string path) =>
            Dirs.Where(d => string.Equals(Path.GetDirectoryName(d), path, StringComparison.OrdinalIgnoreCase)).ToArray();

        internal static FakeFiles WithData()
        {
            var files = new FakeFiles();
            files.Dirs.Add(Data);
            files.Files[Path.Combine(Data, "config")] = new byte[] { 1, 2, 3 };
            files.Files[Path.Combine(Data, "pwd")] = Encoding.UTF8.GetBytes("hash");
            files.Files[Path.Combine(Data, "profiles.json")] = new byte[] { 9 };
            return files;
        }
    }

    private static void Fails(Action action) => AssertEx.Throws<Exception>(action);

    private static void TransactionArgument()
    {
        string mode = UpgradeLifecyclePolicy.StopMode;
        AssertEx.Equal(Txn, UpgradeLifecyclePolicy.Transaction(new[] { mode, Txn }, mode));
        AssertEx.Equal(Txn, UpgradeLifecyclePolicy.Transaction(new[] { mode, Txn.ToLowerInvariant() }, mode));
        foreach (string[] args in new[]
        {
            new[] { mode }, new[] { mode, "" }, new[] { mode, Txn.Trim('{', '}') }, new[] { mode, "{not-a-guid}" },
            new[] { mode, Txn, Txn }, new[] { Txn, mode }, new[] { mode, mode }, new[] { "/install", Txn },
            new[] { mode, Txn + " " }, new[] { mode, "(" + Txn.Trim('{', '}') + ")" },
        })
            AssertEx.Throws<ArgumentException>(() => UpgradeLifecyclePolicy.Transaction(args, mode));
        AssertEx.Equal(5, UpgradeLifecyclePolicy.Modes.Distinct().Count());
        foreach (string each in UpgradeLifecyclePolicy.Modes)
            AssertEx.True(each.StartsWith("/msi-upgrade-", StringComparison.Ordinal));
    }

    private static void MarkerFormat()
    {
        string marker = UpgradeLifecyclePolicy.FormatMarker(Txn, UpgradePhase.SnapshotComplete);
        AssertEx.Equal(Txn + "|snapshot-complete", marker);
        foreach (UpgradePhase phase in new[] { UpgradePhase.Stopping, UpgradePhase.Stopped, UpgradePhase.SnapshotComplete })
            AssertEx.Equal(phase, UpgradeLifecyclePolicy.ParseMarker(UpgradeLifecyclePolicy.FormatMarker(Txn, phase))!.Value.Phase);
        AssertEx.Equal(Txn, UpgradeLifecyclePolicy.ParseMarker(" " + Txn.ToLowerInvariant() + "|stopped ")!.Value.Transaction);
        AssertEx.Equal(UpgradePhase.Unknown, UpgradeLifecyclePolicy.ParseMarker(Txn)!.Value.Phase);
        AssertEx.Equal(UpgradePhase.Unknown, UpgradeLifecyclePolicy.ParseMarker(Txn + "|later")!.Value.Phase);
        foreach (string? bad in new[] { null, "", "  ", "garbage|stopped", "|stopped" })
            AssertEx.Equal<UpgradeMarkerState?>(null, UpgradeLifecyclePolicy.ParseMarker(bad));
        AssertEx.True(UpgradeLifecyclePolicy.OwnsMarker(marker, Txn));
        AssertEx.False(UpgradeLifecyclePolicy.OwnsMarker(marker, Other));
        AssertEx.False(UpgradeLifecyclePolicy.OwnsMarker("garbage", Txn));
    }

    private sealed class StopRun
    {
        internal readonly List<string> Log = new();
        internal readonly FakeService Service;
        internal Action? Authenticate, Prerequisites, Controllers, Copy;
        internal TimeSpan Clock = TimeSpan.Zero;
        internal StopRun() => Service = new FakeService(Log);

        internal void Run() => UpgradeLifecyclePolicy.Stop(Txn, Service,
            Authenticate ?? (() => Log.Add("authenticate")),
            () => Log.Add("set aside"),
            Prerequisites ?? (() => Log.Add("prerequisites")),
            phase => Log.Add("marker " + phase),
            () => Clock, delay => Clock += delay,
            Controllers ?? (() => Log.Add("controllers")),
            Copy ?? (() => { AssertEx.Equal(LifecycleServiceState.Stopped, Service.Current); Log.Add("copy"); }),
            () => { AssertEx.Equal(LifecycleServiceState.Stopped, Service.Current); Log.Add("manifest"); });
    }

    private static void StopOrder()
    {
        var run = new StopRun();
        run.Run();
        AssertEx.SequenceEqual(new[] { "authenticate", "set aside", "prerequisites", "marker Stopping", "restart=False", "stop",
            "controllers", "marker Stopped", "copy", "manifest", "marker SnapshotComplete" }, run.Log);
        AssertEx.Equal(false, run.Service.Restart);
    }

    private static void StopRefusesEarly()
    {
        var run = new StopRun();
        run.Service.IsRegistered = false;
        Fails(run.Run);
        AssertEx.SequenceEqual(new[] { "authenticate", "set aside" }, run.Log);

        // Stopped after a crash: an SCM restart may be queued that no later setting
        // change cancels. StartPending and StopPending are refused the same way.
        foreach (var state in new[] { LifecycleServiceState.Stopped, LifecycleServiceState.StartPending, LifecycleServiceState.StopPending })
        {
            run = new StopRun();
            run.Service.Current = state;
            Fails(run.Run);
            AssertEx.SequenceEqual(new[] { "authenticate", "set aside" }, run.Log);
        }

        foreach (bool failAuthentication in new[] { true, false })
        {
            run = new StopRun();
            if (failAuthentication) run.Authenticate = () => throw new UnauthorizedAccessException("guard");
            else run.Prerequisites = () => throw new InvalidOperationException("MpsSvc");
            Fails(run.Run);
            AssertEx.False(run.Log.Any(entry => entry.StartsWith("marker", StringComparison.Ordinal) || entry.StartsWith("restart", StringComparison.Ordinal) || entry == "stop"));
            AssertEx.Equal(LifecycleServiceState.Running, run.Service.Current);
        }
    }

    private static void StopCrashBeforeRecoveryOff()
    {
        // The service crashes after the first check but before the failure actions
        // are cleared: a restart may already be queued, so the stop aborts there.
        var run = new StopRun();
        run.Prerequisites = () => { run.Log.Add("prerequisites"); run.Service.Current = LifecycleServiceState.Stopped; };
        Fails(run.Run);
        AssertEx.SequenceEqual(new[] { "authenticate", "set aside", "prerequisites", "marker Stopping", "restart=False" }, run.Log);
    }

    private static void StopNeverSnapshotsRunningService()
    {
        var run = new StopRun();
        run.Service.StopWorks = false;
        Fails(run.Run);
        AssertEx.True(run.Log.Contains("stop") && run.Log.Contains("restart=False"));
        AssertEx.False(run.Log.Contains("controllers") || run.Log.Contains("copy") || run.Log.Contains("manifest"));

        // A controller that read no marker yet starts the service again: caught
        // before the copy, and before the manifest.
        run = new StopRun();
        run.Controllers = () => { run.Log.Add("controllers"); run.Service.Current = LifecycleServiceState.StartPending; };
        Fails(run.Run);
        AssertEx.False(run.Log.Contains("copy") || run.Log.Contains("manifest"));

        run = new StopRun();
        run.Copy = () => { run.Log.Add("copy"); run.Service.Current = LifecycleServiceState.Running; };
        Fails(run.Run);
        AssertEx.False(run.Log.Contains("manifest") || run.Log.Contains("marker SnapshotComplete"));

        // A controller that cannot be closed fails the stop before the copy.
        run = new StopRun();
        run.Controllers = () => throw new InvalidOperationException("controller still running");
        Fails(run.Run);
        AssertEx.False(run.Log.Contains("copy") || run.Log.Contains("marker Stopped"));
    }

    private static void StartPreconditions()
    {
        string done = UpgradeLifecyclePolicy.FormatMarker(Txn, UpgradePhase.SnapshotComplete);
        UpgradeLifecyclePolicy.RequireStartable(Txn, true, LifecycleServiceState.Stopped, true, done);
        UpgradeLifecyclePolicy.RequireStartable(Txn, true, LifecycleServiceState.Stopped, true, done.ToLowerInvariant());
        Fails(() => UpgradeLifecyclePolicy.RequireStartable(Txn, false, LifecycleServiceState.Stopped, true, done));
        foreach (var state in new[] { LifecycleServiceState.Running, LifecycleServiceState.StartPending, LifecycleServiceState.StopPending })
            Fails(() => UpgradeLifecyclePolicy.RequireStartable(Txn, true, state, true, done));
        Fails(() => UpgradeLifecyclePolicy.RequireStartable(Txn, true, LifecycleServiceState.Stopped, false, done));
        foreach (string? marker in new[] { null, "", Txn, UpgradeLifecyclePolicy.FormatMarker(Txn, UpgradePhase.Stopped),
            UpgradeLifecyclePolicy.FormatMarker(Txn, UpgradePhase.Stopping), UpgradeLifecyclePolicy.FormatMarker(Other, UpgradePhase.SnapshotComplete) })
            Fails(() => UpgradeLifecyclePolicy.RequireStartable(Txn, true, LifecycleServiceState.Stopped, true, marker));
    }

    private static void WaitForRunning()
    {
        var clock = TimeSpan.Zero;
        var states = new Queue<LifecycleServiceState>(new[] { LifecycleServiceState.StartPending, LifecycleServiceState.StartPending, LifecycleServiceState.Running });
        AssertEx.True(UpgradeLifecyclePolicy.WaitForRunning(() => states.Count > 1 ? states.Dequeue() : states.Peek(),
            TimeSpan.FromSeconds(120), () => clock, delay => clock += delay));

        clock = TimeSpan.Zero;
        int polls = 0;
        AssertEx.False(UpgradeLifecyclePolicy.WaitForRunning(() => ++polls < 3 ? LifecycleServiceState.StartPending : LifecycleServiceState.Stopped,
            TimeSpan.FromSeconds(120), () => clock, delay => clock += delay));
        AssertEx.True(clock < TimeSpan.FromSeconds(1), "a stopped service must not wait out the deadline");

        clock = TimeSpan.Zero;
        AssertEx.False(UpgradeLifecyclePolicy.WaitForRunning(() => LifecycleServiceState.StartPending,
            TimeSpan.FromSeconds(120), () => clock, delay => clock += delay));
        AssertEx.True(clock >= TimeSpan.FromSeconds(120));
    }

    private static void MarkerGate()
    {
        string marker = UpgradeLifecyclePolicy.FormatMarker(Txn, UpgradePhase.Stopped);
        AssertEx.False(UpgradeLifecyclePolicy.BlocksStart(null, true));
        AssertEx.False(UpgradeLifecyclePolicy.BlocksStart("", true));
        AssertEx.False(UpgradeLifecyclePolicy.BlocksStart(marker, false), "a marker left by a power loss must not block recovery");
        AssertEx.True(UpgradeLifecyclePolicy.BlocksStart(marker, true));
        AssertEx.True(UpgradeLifecyclePolicy.BlocksStart("garbage", true));
        AssertEx.False(UpgradeLifecyclePolicy.BlocksStart(marker, true, Txn), "the upgrade's own starter passes");
        AssertEx.True(UpgradeLifecyclePolicy.BlocksStart(UpgradeLifecyclePolicy.FormatMarker(Other, UpgradePhase.Stopped), true, Txn), "another upgrade's marker still blocks");
        // The service's own EnsureHealth leaves the restart actions to the upgrade
        // under the same condition, and only then.
        AssertEx.True(UpgradeLifecyclePolicy.UpgradeOwnsRecoverySettings(marker, true));
        AssertEx.False(UpgradeLifecyclePolicy.UpgradeOwnsRecoverySettings(marker, false));
        AssertEx.False(UpgradeLifecyclePolicy.UpgradeOwnsRecoverySettings(null, true));
    }

    private static void RestoreDecision()
    {
        string M(UpgradePhase phase) => UpgradeLifecyclePolicy.FormatMarker(Txn, phase);
        foreach (bool running in new[] { false, true })
        foreach (bool verified in new[] { false, true })
        {
            // No marker for this transaction: the old service never stopped.
            AssertEx.Equal(RestorePlan.StartOnly, UpgradeLifecyclePolicy.Restore(Txn, null, running, verified));
            AssertEx.Equal(RestorePlan.StartOnly, UpgradeLifecyclePolicy.Restore(Txn, UpgradeLifecyclePolicy.FormatMarker(Other, UpgradePhase.SnapshotComplete), running, verified));
            // A finished snapshot: whatever runs (it can be the new binary) is stopped first.
            AssertEx.Equal(verified ? RestorePlan.StopRestoreStart : RestorePlan.StopAndHold,
                UpgradeLifecyclePolicy.Restore(Txn, M(UpgradePhase.SnapshotComplete), running, verified));
            AssertEx.Equal(verified ? RestorePlan.StopRestoreStart : RestorePlan.StopAndHold,
                UpgradeLifecyclePolicy.Restore(Txn, Txn, running, verified));
            // Stopped before the snapshot finished: the live files are the old version's own.
            AssertEx.Equal(verified ? RestorePlan.StopRestoreStart : RestorePlan.StopDiscardStart,
                UpgradeLifecyclePolicy.Restore(Txn, M(UpgradePhase.Stopped), running, verified));
        }
        AssertEx.Equal(RestorePlan.StartOnly, UpgradeLifecyclePolicy.Restore(Txn, M(UpgradePhase.Stopping), true, false));
        AssertEx.Equal(RestorePlan.StopDiscardStart, UpgradeLifecyclePolicy.Restore(Txn, M(UpgradePhase.Stopping), false, false));
    }

    private static void SnapshotVerification()
    {
        string directory = UpgradeLifecyclePolicy.SnapshotDirectory(Data, Txn.ToLowerInvariant());
        AssertEx.Equal(Path.Combine(Data, "upgrade", Txn), directory);
        Fails(() => UpgradeLifecyclePolicy.SnapshotDirectory(Data, @"..\..\Windows"));
        Fails(() => UpgradeLifecyclePolicy.SnapshotDirectory(Data, ""));
        AssertEx.SequenceEqual(new[] { "config", "config.recovery", "pwd", "hosts.orig" }, UpgradeLifecyclePolicy.SnapshotNames);
        AssertEx.False(UpgradeLifecyclePolicy.SnapshotNames.Intersect(ShippedDataPolicy.Names).Any(), "shipped data follows the binaries, not the snapshot");

        var files = FakeFiles.WithData();
        AssertEx.Equal<IDictionary<string, string>?>(null, UpgradeLifecyclePolicy.VerifiedSnapshot(files, Data, Txn));
        UpgradeLifecyclePolicy.CopySnapshot(files, Data, Txn);
        AssertEx.Equal<IDictionary<string, string>?>(null, UpgradeLifecyclePolicy.VerifiedSnapshot(files, Data, Txn), "a copy without its manifest is unfinished");
        UpgradeLifecyclePolicy.WriteManifest(files, Data, Txn);
        AssertEx.Equal("write " + UpgradeLifecyclePolicy.SnapshotManifest, files.Log.Last(), "the manifest is written last");
        var verified = UpgradeLifecyclePolicy.VerifiedSnapshot(files, Data, Txn);
        AssertEx.SequenceEqual(new[] { "config", "pwd" }, verified!.Keys.OrderBy(k => k, StringComparer.Ordinal));

        // A copy changed, deleted or added after the manifest no longer verifies.
        files.Files[Path.Combine(directory, "config")] = new byte[] { 1, 2, 4 };
        AssertEx.Equal<IDictionary<string, string>?>(null, UpgradeLifecyclePolicy.VerifiedSnapshot(files, Data, Txn));
        UpgradeLifecyclePolicy.TakeSnapshot(files, Data, Txn);
        files.Files.Remove(Path.Combine(directory, "pwd"));
        AssertEx.Equal<IDictionary<string, string>?>(null, UpgradeLifecyclePolicy.VerifiedSnapshot(files, Data, Txn));
        UpgradeLifecyclePolicy.TakeSnapshot(files, Data, Txn);
        files.Files[Path.Combine(directory, UpgradeLifecyclePolicy.SnapshotManifest)] = Encoding.UTF8.GetBytes("garbage");
        AssertEx.Equal<IDictionary<string, string>?>(null, UpgradeLifecyclePolicy.VerifiedSnapshot(files, Data, Txn));
    }

    private static void SnapshotRoundTrip()
    {
        var files = FakeFiles.WithData();
        string config = Path.Combine(Data, "config"), pwd = Path.Combine(Data, "pwd"), orig = Path.Combine(Data, "hosts.orig");
        string journal = Path.Combine(Data, "config.recovery");
        UpgradeLifecyclePolicy.TakeSnapshot(files, Data, Txn);
        string directory = UpgradeLifecyclePolicy.SnapshotDirectory(Data, Txn);
        foreach (string name in new[] { "config", "pwd", UpgradeLifecyclePolicy.SnapshotManifest })
            AssertEx.True(files.Secret.Contains(Path.Combine(directory, name)), "snapshot copy is not protected: " + name);
        AssertEx.False(files.FileExists(Path.Combine(directory, "hosts.orig")));
        AssertEx.False(files.FileExists(Path.Combine(directory, "profiles.json")));

        // The new version rewrites config, leaves a config.recovery, removes the
        // password and creates hosts.orig.
        files.Files[config] = new byte[] { 7, 7 };
        files.Files[journal] = new byte[] { 3 };
        files.Files.Remove(pwd);
        files.Files[orig] = new byte[] { 5 };
        files.Files[Path.Combine(Data, "profiles.json")] = new byte[] { 8 };
        files.Secret.Clear();
        UpgradeLifecyclePolicy.RestoreSnapshot(files, Data, Txn, UpgradeLifecyclePolicy.VerifiedSnapshot(files, Data, Txn)!);
        AssertEx.SequenceEqual(new byte[] { 1, 2, 3 }, files.Files[config]);
        AssertEx.SequenceEqual(Encoding.UTF8.GetBytes("hash"), files.Files[pwd]);
        AssertEx.True(files.Secret.Contains(pwd), "pwd must be restored with the secret DACL");
        AssertEx.False(files.Secret.Contains(config));
        AssertEx.False(files.FileExists(orig), "a covered file absent from the snapshot is deleted");
        AssertEx.False(files.FileExists(journal), "the newer version's config.recovery is deleted");
        AssertEx.SequenceEqual(new byte[] { 8 }, files.Files[Path.Combine(Data, "profiles.json")], "shipped data is left to InstallDefaults");
        AssertEx.False(files.DirectoryExists(directory));
    }

    private static void SnapshotRetry()
    {
        var files = FakeFiles.WithData();
        UpgradeLifecyclePolicy.TakeSnapshot(files, Data, Txn);
        string directory = UpgradeLifecyclePolicy.SnapshotDirectory(Data, Txn);
        files.Files[Path.Combine(Data, "config")] = new byte[] { 6 };

        // A retried MSI (same ProductCode) sets the earlier attempt's snapshot aside
        // before it can fail, so its own rollback never restores the stale copy.
        string? kept = UpgradeLifecyclePolicy.SetAsidePriorSnapshot(files, Data, Txn, "20261101000000");
        AssertEx.Equal<string?>(directory + ".previous-20261101000000", kept);
        AssertEx.Equal<IDictionary<string, string>?>(null, UpgradeLifecyclePolicy.VerifiedSnapshot(files, Data, Txn));
        AssertEx.SequenceEqual(new byte[] { 1, 2, 3 }, files.Files[Path.Combine(kept!, "config")], "the earlier snapshot stays as evidence");
        UpgradeLifecyclePolicy.DeleteStaleSnapshots(files, Data, null);
        AssertEx.True(files.DirectoryExists(kept!), "set-aside snapshots are never cleaned up automatically");
        AssertEx.Equal<string?>(null, UpgradeLifecyclePolicy.SetAsidePriorSnapshot(files, Data, Txn, "x"));

        // A retry's copy never keeps files from an earlier copy.
        UpgradeLifecyclePolicy.CopySnapshot(files, Data, Txn);
        files.Files[Path.Combine(directory, "hosts.orig")] = new byte[] { 4 };
        UpgradeLifecyclePolicy.TakeSnapshot(files, Data, Txn);
        AssertEx.False(files.FileExists(Path.Combine(directory, "hosts.orig")));
    }

    private static void ManifestValidation()
    {
        string hash = new string('a', 64);
        AssertEx.Equal(2, UpgradeLifecyclePolicy.ParseManifest("SecureWall upgrade snapshot 2\nconfig " + hash + "\npwd " + hash + "\n").Count);
        AssertEx.Equal(0, UpgradeLifecyclePolicy.ParseManifest("SecureWall upgrade snapshot 2\n").Count);
        AssertEx.Equal(1, UpgradeLifecyclePolicy.ParseManifest("SecureWall upgrade snapshot 2\r\nconfig " + hash + "\r\n").Count);
        foreach (string bad in new[] { "", "config " + hash + "\n", "SecureWall upgrade snapshot 1\nconfig\n",
            "SecureWall upgrade snapshot 2\nprofiles.json " + hash + "\n", "SecureWall upgrade snapshot 2\n..\\pwd " + hash + "\n",
            "SecureWall upgrade snapshot 2\nconfig " + hash + "\nconfig " + hash + "\n", "SecureWall upgrade snapshot 2\nconfig abc\n",
            "SecureWall upgrade snapshot 2\nconfig\n" })
            Fails(() => UpgradeLifecyclePolicy.ParseManifest(bad));
        AssertEx.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", UpgradeLifecyclePolicy.Sha256(Array.Empty<byte>()));
    }

    // Every file the service or its helpers write under the machine-data path must be
    // in the snapshot set, be shipped data, or be a log or switch the guard ignores.
    private static void SnapshotCoversServiceFiles()
    {
        var root = new DirectoryInfo(Path.GetDirectoryName(PromptTransactionIntegrationTests.SourcePath("TinyWall/TinyWallService.cs"))!);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileInfo file in root.EnumerateFiles("*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file.FullName);
            foreach (Match match in Regex.Matches(source, @"Path\.Combine\((?:Utils\.)?AppDataPath,\s*@?""([^""\\]+)"))
                names.Add(match.Groups[1].Value);
            foreach (Match match in Regex.Matches(source, @"Path\.Combine\(backupDirectory,\s*""([^""]+)""\)"))
                names.Add(match.Groups[1].Value);
            foreach (Match match in Regex.Matches(source, @"ConfigSavePath \+ ""([^""]+)"""))
                names.Add("config" + match.Groups[1].Value);
        }
        AssertEx.True(names.Contains("config") && names.Contains("config.recovery") && names.Contains("pwd") && names.Contains("hosts.orig"),
            "the source scan no longer finds the known policy files: " + string.Join(", ", names));
        string[] ignored = { "enable-timings", "logs" };
        foreach (string name in names)
            AssertEx.True(UpgradeLifecyclePolicy.SnapshotNames.Contains(name) || ShippedDataPolicy.Names.Contains(name) || ignored.Contains(name),
                "the service writes " + name + ", which the upgrade snapshot does not cover");
    }

    private static void StaleSnapshots()
    {
        string root = UpgradeLifecyclePolicy.SnapshotRoot(Data);
        string[] entries = { Path.Combine(root, Txn), Path.Combine(root, Other.ToLowerInvariant()), Path.Combine(root, "notes") };
        AssertEx.SequenceEqual(new[] { entries[1] }, UpgradeLifecyclePolicy.StaleSnapshots(entries, Txn));
        AssertEx.SequenceEqual(new[] { entries[0] }, UpgradeLifecyclePolicy.StaleSnapshots(entries, Other));
        AssertEx.SequenceEqual(new[] { entries[0], entries[1] }, UpgradeLifecyclePolicy.StaleSnapshots(entries, null));

        var files = FakeFiles.WithData();
        UpgradeLifecyclePolicy.TakeSnapshot(files, Data, Txn);
        UpgradeLifecyclePolicy.TakeSnapshot(files, Data, Other);
        UpgradeLifecyclePolicy.DeleteStaleSnapshots(files, Data, Txn);
        AssertEx.True(UpgradeLifecyclePolicy.VerifiedSnapshot(files, Data, Txn) != null);
        AssertEx.False(files.DirectoryExists(UpgradeLifecyclePolicy.SnapshotDirectory(Data, Other)));
        UpgradeLifecyclePolicy.DeleteStaleSnapshots(files, Data, null);
        AssertEx.False(files.DirectoryExists(root), "an empty snapshot root is removed");
        UpgradeLifecyclePolicy.DeleteStaleSnapshots(files, Data, null);
    }

    private static void CommitKeeps()
    {
        AssertEx.Equal<string?>(null, UpgradeLifecyclePolicy.CommitKeeps(null, Txn));
        AssertEx.Equal<string?>(null, UpgradeLifecyclePolicy.CommitKeeps(UpgradeLifecyclePolicy.FormatMarker(Txn, UpgradePhase.SnapshotComplete), Txn));
        AssertEx.Equal<string?>(Other, UpgradeLifecyclePolicy.CommitKeeps(" " + UpgradeLifecyclePolicy.FormatMarker(Other, UpgradePhase.Stopped) + " ", Txn));
        AssertEx.Equal<string?>(null, UpgradeLifecyclePolicy.CommitKeeps("garbage", Txn));
    }

    private static void RegistryRemoval()
    {
        string[] subkeys = { ProductRegistryPolicy.FirewallJournal, ProductRegistryPolicy.AuditJournal };
        string[] values = { "InstallDir", "Platform", UpgradeLifecyclePolicy.MarkerValue, "UpgradeFaultTest" };
        AssertEx.Equal<ProductRegistryPolicy.Removal?>(null, ProductRegistryPolicy.Plan(subkeys, values, 1, 0));
        AssertEx.Equal<ProductRegistryPolicy.Removal?>(null, ProductRegistryPolicy.Plan(subkeys, values, 0, 2));

        ProductRegistryPolicy.Removal removal = ProductRegistryPolicy.Plan(subkeys, values, 0, 0)!.Value;
        AssertEx.SequenceEqual(subkeys, removal.Subkeys);
        AssertEx.SequenceEqual(new[] { UpgradeLifecyclePolicy.MarkerValue, "UpgradeFaultTest" }, removal.Values);
        AssertEx.False(removal.DeleteKey, "the MSI removes its own values; its commit-phase action removes the key");

        removal = ProductRegistryPolicy.Plan(subkeys, new[] { UpgradeLifecyclePolicy.MarkerValue }, 0, 0)!.Value;
        AssertEx.True(removal.DeleteKey, "without MSI values the key is deleted here");
        AssertEx.True(ProductRegistryPolicy.Plan(Array.Empty<string>(), new[] { "installdir" }, 0, 0)!.Value.Values.Length == 0);
    }

    private static void AdapterWiring()
    {
        string doctor = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallDoctor.cs");
        string Body(string signature)
        {
            int begin = doctor.IndexOf(signature, StringComparison.Ordinal);
            AssertEx.True(begin >= 0, "missing " + signature);
            int end = doctor.IndexOf("\n        internal static ", begin + signature.Length, StringComparison.Ordinal);
            return doctor.Substring(begin, (end < 0 ? doctor.Length : end) - begin);
        }
        // Every marker must be present, in this order; a missing call fails.
        void InOrder(string body, string step, params string[] markers)
        {
            int previous = -1;
            foreach (string marker in markers)
            {
                int index = body.IndexOf(marker, previous + 1, StringComparison.Ordinal);
                AssertEx.True(index > previous, step + ": missing or out of order: " + marker);
                previous = index;
            }
        }

        string repair = Body("internal static bool EnsureServiceInstalledAndRunning(");
        int gate = repair.IndexOf("UpgradeLifecyclePolicy.BlocksStart(UpgradeMarker.Read(), UpgradeMarker.InstallerRunning(), upgradeTransaction)", StringComparison.Ordinal);
        AssertEx.True(gate > 0, "repair and /install lack the upgrade gate");
        foreach (string start in new[] { "ManagedInstallerClass.InstallHelper", "sc.Start()", "Utils.StartProcess" })
            AssertEx.True(gate < repair.IndexOf(start, StringComparison.Ordinal), "upgrade gate follows " + start);

        string stop = Body("internal static int UpgradeStopForMsi(string transaction)");
        InOrder(stop, "stop", "UpgradeLifecyclePolicy.Stop(transaction, service,", "InstallationSafety.RequireSystemMaintenance();",
            "InstallationSafety.RequireProtectedMachineData();", "ValidateRegisteredServiceImage(true);",
            "UpgradeLifecyclePolicy.SetAsidePriorSnapshot(files, Utils.AppDataPath,",
            "WindowsFirewall.RequireServiceRunning();", "\"BFE\"", "writeMarker: phase => UpgradeMarker.Write(transaction, phase)",
            "TerminateInstallationControllers();", "RequireNoInstallationControllers();",
            "UpgradeLifecyclePolicy.CopySnapshot(files, Utils.AppDataPath, transaction);",
            "UpgradeLifecyclePolicy.WriteManifest(files, Utils.AppDataPath, transaction);");
        AssertEx.False(stop.Contains("CleanupForMsi") || stop.Contains("RemoveWfpObjects") || stop.Contains("RestoreOwnedState") || stop.Contains("DisableHostsFile"));

        string start7 = Body("internal static int UpgradeStartForMsi(string transaction)");
        InOrder(start7, "start", "UpgradeLifecyclePolicy.RequireStartable(", "UpgradeLifecyclePolicy.VerifiedSnapshot(files, data, transaction) != null",
            "MachineDataGuard.InstallDefaults();", "UpgradeLifecyclePolicy.DeleteStaleSnapshots(files, data, transaction);",
            "EnsureHealth(", "service.Start();", "UpgradeLifecyclePolicy.WaitForRunning(");
        AssertEx.False(start7.Contains("SetRestartOnFailure(true)"), "only commit and rollback turn the restart actions back on");

        InOrder(Body("internal static int UpgradeRestoreForMsi(string transaction)"), "restore",
            "UpgradeLifecyclePolicy.VerifiedSnapshot(files, data, transaction)", "UpgradeLifecyclePolicy.Restore(transaction, UpgradeMarker.Read(),",
            "if (plan != RestorePlan.StartOnly)", "StopGracefully(", "TerminateInstallationControllers();", "RequireNoInstallationControllers();",
            "case RestorePlan.StopRestoreStart:", "UpgradeLifecyclePolicy.RestoreSnapshot(files, data, transaction, verified!);",
            "case RestorePlan.StopDiscardStart:", "case RestorePlan.StopAndHold:", "throw new InvalidOperationException(",
            "MachineDataGuard.InstallDefaults();", "service.SetRestartOnFailure(true);",
            "EnsureServiceInstalledAndRunning(Utils.LOG_ID_INSTALLER, true, transaction)", "CleanUpAfterUpgrade(transaction,");

        string abort = Body("internal static int UpgradeAbortForMsi(string transaction)");
        InOrder(abort, "abort", "service.SetRestartOnFailure(false);", "StopGracefully(", "InstallationSafety.TerminateFailedInstallService();",
            "if (terminated) SetAutomaticStart();", "TerminateInstallationControllers();");
        AssertEx.False(abort.Contains("ServiceStartMode.Disabled"), "the start type stays Automatic so a reboot mid-rollback still starts the service");
        AssertEx.False(abort.Contains("UpgradeMarker.Clear") || abort.Contains("CleanUpAfterUpgrade"), "abort must leave the marker for the restore step");

        string commit = Body("internal static int UpgradeCommitForMsi(string transaction)");
        InOrder(commit, "commit", "service.SetRestartOnFailure(true);", "UpgradeLifecyclePolicy.CommitKeeps(UpgradeMarker.Read(), transaction)",
            "CleanUpAfterUpgrade(transaction,", "private static void CleanUpAfterUpgrade(", "deleteSnapshots();", "UpgradeMarker.Clear(transaction);");

        string health = Body("internal static bool EnsureHealth(string logContext, bool strictGuards)");
        InOrder(health, "health", "UpgradeLifecyclePolicy.UpgradeOwnsRecoverySettings(UpgradeMarker.Read(), UpgradeMarker.InstallerRunning())",
            "else", "scm.SetRestartOnFailure(TinyWallService.SERVICE_NAME,");

        string cleanup = Body("private static int CleanupStoppedInstallation(");
        InOrder(cleanup, "removal", "ProtectionReleasePolicy.Release(", "RemoveProductRegistry();", "return released ? 0 : -1;",
            "private static void RemoveProductRegistry()", "key.DeleteValue(UpgradeLifecyclePolicy.MarkerValue, false);",
            "RegistryAuditPolicyJournal.Instance.ReadAll(", "ProductRegistryPolicy.Plan(");

        string program = PromptTransactionIntegrationTests.Source("TinyWall/Program.cs");
        foreach (string mode in new[] { "StopMode", "RestoreMode", "StartMode", "AbortMode", "CommitMode" })
            AssertEx.True(program.Contains("RunUpgradeStep(args, UpgradeLifecyclePolicy." + mode + ", TinyWallDoctor.Upgrade"), "mode not dispatched: " + mode);

        string controller = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallController.cs");
        InOrder(controller, "controller", "private bool ExitIfUpgrading()", "TinyWallDoctor.ServiceStartBlockedByUpgrade()", "ExitThread();",
            "private void InitController()", "if (ExitIfUpgrading())", "if (ExitIfUpgrading())",
            "TinyWallDoctor.EnsureServiceInstalledAndRunning(Utils.LOG_ID_GUI, false)");

        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        InOrder(service, "WFP cleanup", "internal static void DeleteWfpObjects(", "wfp.GetLayerKeys()",
            "WfpCoexistencePolicy.SublayersToRemove(SECUREWALL_PROVIDER_KEY, sublayers)");

        string fault = PromptTransactionIntegrationTests.Source("TinyWall/Installer/UpgradeFault.cs");
        AssertEx.True(fault.Contains("[Conditional(\"UPGRADE_FAULT_TEST\")]"));
        string project = PromptTransactionIntegrationTests.Source("TinyWall/TinyWall.csproj");
        AssertEx.True(project.Contains("<PropertyGroup Condition=\"'$(Configuration)' == 'UpgradeFaultTest'\">"));
        AssertEx.Equal(1, project.Split("UPGRADE_FAULT_TEST").Length - 1, "only the UpgradeFaultTest configuration defines the fault symbol");
        AssertEx.True(project.Contains("<_Parameter1>SecureWallFaultTest</_Parameter1>"), "the fault build carries its assembly stamp");
    }
}

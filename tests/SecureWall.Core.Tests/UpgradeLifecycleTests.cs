using System.Text;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class UpgradeLifecycleTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("upgrade transaction argument is exactly one braced GUID", TransactionArgument),
        ("upgrade stop runs checks, marker, restart suppression, stop, controllers, then snapshot", StopOrder),
        ("upgrade stop refuses before any change when unregistered, already stopped or checks fail", StopRefusesEarly),
        ("upgrade stop never snapshots a service that did not stop", StopTimeoutSkipsSnapshot),
        ("upgrade start requires registration, stopped service, snapshot and own marker", StartPreconditions),
        ("upgrade start waits for Running and fails fast on Stopped", WaitForRunning),
        ("upgrade marker blocks automatic starts only while an installation runs", MarkerGate),
        ("upgrade restore refuses a foreign marker and restarts without a snapshot", RestoreDecision),
        ("upgrade snapshot names the transaction and is complete only with its manifest", SnapshotNaming),
        ("upgrade snapshot round trip restores bytes, absent files and secret protection", SnapshotRoundTrip),
        ("upgrade retry sets an earlier snapshot of the same transaction aside, never restoring it", SnapshotRetry),
        ("upgrade snapshot manifest rejects unknown formats and names", ManifestValidation),
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

    private static void RunStop(List<string> log, FakeService service, Action? authenticate = null, Action? prerequisites = null)
    {
        var clock = TimeSpan.Zero;
        UpgradeLifecyclePolicy.Stop(Txn, service,
            authenticate ?? (() => log.Add("authenticate")),
            () => log.Add("set aside"),
            prerequisites ?? (() => log.Add("prerequisites")),
            txn => log.Add("marker " + txn),
            () => clock, delay => clock += delay,
            () => log.Add("controllers"),
            () => { AssertEx.Equal(LifecycleServiceState.Stopped, service.Current); log.Add("snapshot"); });
    }

    private static void StopOrder()
    {
        var log = new List<string>();
        var service = new FakeService(log);
        RunStop(log, service);
        AssertEx.SequenceEqual(new[] { "authenticate", "set aside", "prerequisites", "marker " + Txn, "restart=False", "stop", "controllers", "snapshot" }, log);
        AssertEx.Equal(false, service.Restart);

        // A service still starting (a restart that just fired) settles and is stopped.
        log.Clear();
        int polls = 0;
        service = new FakeService(log) { Current = LifecycleServiceState.StartPending };
        var clock = TimeSpan.Zero;
        UpgradeLifecyclePolicy.Stop(Txn, service, () => { }, () => { }, () => { }, _ => { },
            () => { if (++polls == 3) service.Current = LifecycleServiceState.Running; return clock; }, delay => clock += delay,
            () => { }, () => log.Add("snapshot"));
        AssertEx.SequenceEqual(new[] { "restart=False", "stop", "snapshot" }, log);
    }

    private static void StopRefusesEarly()
    {
        var log = new List<string>();
        var service = new FakeService(log) { IsRegistered = false };
        Fails(() => RunStop(log, service));
        AssertEx.SequenceEqual(new[] { "authenticate", "set aside" }, log);

        // A stopped service may have an SCM restart queued that clearing the
        // failure actions does not cancel: refuse before any change.
        log.Clear();
        service = new FakeService(log) { Current = LifecycleServiceState.Stopped };
        Fails(() => RunStop(log, service));
        AssertEx.SequenceEqual(new[] { "authenticate", "set aside" }, log);

        foreach (bool failAuthentication in new[] { true, false })
        {
            log.Clear();
            service = new FakeService(log);
            Fails(() => RunStop(log, service,
                failAuthentication ? () => throw new UnauthorizedAccessException("guard") : null,
                failAuthentication ? null : () => throw new InvalidOperationException("MpsSvc")));
            AssertEx.False(log.Any(entry => entry.StartsWith("marker", StringComparison.Ordinal) || entry.StartsWith("restart", StringComparison.Ordinal) || entry == "stop"));
            AssertEx.Equal(LifecycleServiceState.Running, service.Current);
        }
    }

    private static void StopTimeoutSkipsSnapshot()
    {
        var log = new List<string>();
        var service = new FakeService(log) { StopWorks = false };
        Fails(() => RunStop(log, service));
        AssertEx.True(log.Contains("stop") && log.Contains("restart=False"));
        AssertEx.False(log.Contains("controllers") || log.Contains("snapshot"));
    }

    private static void StartPreconditions()
    {
        UpgradeLifecyclePolicy.RequireStartable(Txn, true, LifecycleServiceState.Stopped, true, Txn);
        UpgradeLifecyclePolicy.RequireStartable(Txn, true, LifecycleServiceState.Stopped, true, " " + Txn.ToLowerInvariant() + " ");
        Fails(() => UpgradeLifecyclePolicy.RequireStartable(Txn, false, LifecycleServiceState.Stopped, true, Txn));
        foreach (var state in new[] { LifecycleServiceState.Running, LifecycleServiceState.StartPending, LifecycleServiceState.StopPending })
            Fails(() => UpgradeLifecyclePolicy.RequireStartable(Txn, true, state, true, Txn));
        Fails(() => UpgradeLifecyclePolicy.RequireStartable(Txn, true, LifecycleServiceState.Stopped, false, Txn));
        foreach (string? marker in new[] { null, "", Other })
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
        AssertEx.False(UpgradeLifecyclePolicy.BlocksStart(null, true));
        AssertEx.False(UpgradeLifecyclePolicy.BlocksStart("", true));
        AssertEx.False(UpgradeLifecyclePolicy.BlocksStart(Txn, false), "a marker left by a power loss must not block recovery");
        AssertEx.True(UpgradeLifecyclePolicy.BlocksStart(Txn, true));
        AssertEx.True(UpgradeLifecyclePolicy.BlocksStart("garbage", true));
        AssertEx.False(UpgradeLifecyclePolicy.BlocksStart(Txn, true, Txn), "the upgrade's own starter passes");
        AssertEx.True(UpgradeLifecyclePolicy.BlocksStart(Other, true, Txn), "another upgrade's marker still blocks");
        AssertEx.False(UpgradeLifecyclePolicy.OwnsMarker(null, Txn));
        AssertEx.True(UpgradeLifecyclePolicy.OwnsMarker(Txn.ToLowerInvariant(), Txn));
    }

    private static void RestoreDecision()
    {
        AssertEx.Equal(UpgradeLifecyclePolicy.RestoreAction.RestoreAndStart, UpgradeLifecyclePolicy.Restore(Txn, Txn, true));
        AssertEx.Equal(UpgradeLifecyclePolicy.RestoreAction.StartOnly, UpgradeLifecyclePolicy.Restore(Txn, Txn, false));
        AssertEx.Equal(UpgradeLifecyclePolicy.RestoreAction.StartOnly, UpgradeLifecyclePolicy.Restore(Txn, null, false));
        AssertEx.Equal(UpgradeLifecyclePolicy.RestoreAction.RestoreAndStart, UpgradeLifecyclePolicy.Restore(Txn, null, true));
        AssertEx.Equal(UpgradeLifecyclePolicy.RestoreAction.Refuse, UpgradeLifecyclePolicy.Restore(Txn, Other, true));
        AssertEx.Equal(UpgradeLifecyclePolicy.RestoreAction.StartOnly, UpgradeLifecyclePolicy.Restore(Txn, Other, false),
            "with nothing to restore, another upgrade's marker must not stop the service from starting");
    }

    private static void SnapshotNaming()
    {
        string directory = UpgradeLifecyclePolicy.SnapshotDirectory(Data, Txn.ToLowerInvariant());
        AssertEx.Equal(Path.Combine(Data, "upgrade", Txn), directory);
        Fails(() => UpgradeLifecyclePolicy.SnapshotDirectory(Data, @"..\..\Windows"));
        Fails(() => UpgradeLifecyclePolicy.SnapshotDirectory(Data, ""));
        AssertEx.SequenceEqual(new[] { "config", "config.recovery", "pwd", "hosts.orig" }, UpgradeLifecyclePolicy.SnapshotNames);
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        AssertEx.True(service.Contains("Path.Combine(Utils.AppDataPath, \"config\")") && service.Contains("ConfigSavePath + \".recovery\""),
            "the service's policy files moved; update UpgradeLifecyclePolicy.SnapshotNames");
        AssertEx.False(UpgradeLifecyclePolicy.SnapshotNames.Intersect(ShippedDataPolicy.Names).Any(), "shipped data follows the binaries, not the snapshot");

        var files = FakeFiles.WithData();
        AssertEx.False(UpgradeLifecyclePolicy.SnapshotComplete(files, Data, Txn));
        files.CreateDirectory(directory);
        files.WriteFile(Path.Combine(directory, "config"), new byte[] { 1 }, true);
        AssertEx.False(UpgradeLifecyclePolicy.SnapshotComplete(files, Data, Txn), "a snapshot without its manifest is incomplete");
        UpgradeLifecyclePolicy.TakeSnapshot(files, Data, Txn);
        AssertEx.True(UpgradeLifecyclePolicy.SnapshotComplete(files, Data, Txn));
        AssertEx.Equal("write snapshot.complete", files.Log.Last(), "the manifest is written last");
    }

    private static void SnapshotRoundTrip()
    {
        var files = FakeFiles.WithData();
        string config = Path.Combine(Data, "config"), pwd = Path.Combine(Data, "pwd"), orig = Path.Combine(Data, "hosts.orig");
        UpgradeLifecyclePolicy.TakeSnapshot(files, Data, Txn);
        string directory = UpgradeLifecyclePolicy.SnapshotDirectory(Data, Txn);
        foreach (string name in new[] { "config", "pwd", UpgradeLifecyclePolicy.SnapshotManifest })
            AssertEx.True(files.Secret.Contains(Path.Combine(directory, name)), "snapshot copy is not protected: " + name);
        AssertEx.False(files.FileExists(Path.Combine(directory, "hosts.orig")));
        AssertEx.False(files.FileExists(Path.Combine(directory, "profiles.json")));

        // The new version rewrites config, removes the password, creates hosts.orig.
        files.Files[config] = new byte[] { 7, 7 };
        files.Files.Remove(pwd);
        files.Files[orig] = new byte[] { 5 };
        files.Files[Path.Combine(Data, "profiles.json")] = new byte[] { 8 };
        files.Secret.Clear();
        UpgradeLifecyclePolicy.RestoreSnapshot(files, Data, Txn);
        AssertEx.SequenceEqual(new byte[] { 1, 2, 3 }, files.Files[config]);
        AssertEx.SequenceEqual(Encoding.UTF8.GetBytes("hash"), files.Files[pwd]);
        AssertEx.True(files.Secret.Contains(pwd), "pwd must be restored with the secret DACL");
        AssertEx.False(files.Secret.Contains(config));
        AssertEx.False(files.FileExists(orig), "a file absent from the snapshot is deleted");
        AssertEx.SequenceEqual(new byte[] { 8 }, files.Files[Path.Combine(Data, "profiles.json")], "shipped data is left to InstallDefaults");
        AssertEx.False(files.DirectoryExists(directory));
        AssertEx.False(UpgradeLifecyclePolicy.SnapshotComplete(files, Data, Txn));
    }

    private static void SnapshotRetry()
    {
        var files = FakeFiles.WithData();
        UpgradeLifecyclePolicy.TakeSnapshot(files, Data, Txn);
        string directory = UpgradeLifecyclePolicy.SnapshotDirectory(Data, Txn);
        files.Files[Path.Combine(directory, "hosts.orig")] = new byte[] { 4 };
        files.Files[Path.Combine(Data, "config")] = new byte[] { 6 };
        UpgradeLifecyclePolicy.TakeSnapshot(files, Data, Txn);
        AssertEx.False(files.FileExists(Path.Combine(directory, "hosts.orig")), "a retry must not keep files from the earlier snapshot");
        AssertEx.SequenceEqual(new byte[] { 6 }, files.Files[Path.Combine(directory, "config")]);

        // A retried MSI (same ProductCode) sets the earlier attempt's snapshot aside
        // before it can fail, so its own rollback never restores the stale copy.
        string? kept = UpgradeLifecyclePolicy.SetAsidePriorSnapshot(files, Data, Txn, "20261101000000");
        AssertEx.Equal<string?>(directory + ".previous-20261101000000", kept);
        AssertEx.False(UpgradeLifecyclePolicy.SnapshotComplete(files, Data, Txn));
        AssertEx.Equal(UpgradeLifecyclePolicy.RestoreAction.StartOnly, UpgradeLifecyclePolicy.Restore(Txn, Txn, UpgradeLifecyclePolicy.SnapshotComplete(files, Data, Txn)));
        AssertEx.SequenceEqual(new byte[] { 6 }, files.Files[Path.Combine(kept!, "config")], "the earlier snapshot stays as evidence");
        UpgradeLifecyclePolicy.DeleteStaleSnapshots(files, Data, null);
        AssertEx.True(files.DirectoryExists(kept!), "set-aside snapshots are never cleaned up automatically");
        AssertEx.Equal<string?>(null, UpgradeLifecyclePolicy.SetAsidePriorSnapshot(files, Data, Txn, "x"));
    }

    private static void ManifestValidation()
    {
        AssertEx.SequenceEqual(new[] { "config", "pwd" },
            UpgradeLifecyclePolicy.ParseManifest(UpgradeLifecyclePolicy.FormatManifest(new[] { "config", "pwd" })).OrderBy(n => n, StringComparer.Ordinal));
        AssertEx.Equal(0, UpgradeLifecyclePolicy.ParseManifest(UpgradeLifecyclePolicy.FormatManifest(Array.Empty<string>())).Count);
        AssertEx.Equal(1, UpgradeLifecyclePolicy.ParseManifest("SecureWall upgrade snapshot 1\r\nconfig\r\n").Count);
        foreach (string bad in new[] { "", "config\n", "SecureWall upgrade snapshot 2\nconfig\n",
            "SecureWall upgrade snapshot 1\nprofiles.json\n", "SecureWall upgrade snapshot 1\n..\\pwd\n", "SecureWall upgrade snapshot 1\nconfig\nconfig\n" })
            Fails(() => UpgradeLifecyclePolicy.ParseManifest(bad));
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
        AssertEx.True(UpgradeLifecyclePolicy.SnapshotComplete(files, Data, Txn));
        AssertEx.False(UpgradeLifecyclePolicy.SnapshotComplete(files, Data, Other));
        UpgradeLifecyclePolicy.DeleteStaleSnapshots(files, Data, null);
        AssertEx.False(files.DirectoryExists(root), "an empty snapshot root is removed");
        UpgradeLifecyclePolicy.DeleteStaleSnapshots(files, Data, null);
    }

    private static void CommitKeeps()
    {
        AssertEx.Equal<string?>(null, UpgradeLifecyclePolicy.CommitKeeps(null, Txn));
        AssertEx.Equal<string?>(null, UpgradeLifecyclePolicy.CommitKeeps(Txn, Txn));
        AssertEx.Equal<string?>(Other, UpgradeLifecyclePolicy.CommitKeeps(" " + Other + " ", Txn));
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
        AssertEx.False(removal.DeleteKey, "the MSI removes its own values and the emptied key");

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

        string repair = Body("internal static bool EnsureServiceInstalledAndRunning(");
        int gate = repair.IndexOf("UpgradeLifecyclePolicy.BlocksStart(UpgradeMarker.Read(), UpgradeMarker.InstallerRunning(), upgradeTransaction)", StringComparison.Ordinal);
        AssertEx.True(gate > 0, "repair and /install lack the upgrade gate");
        foreach (string start in new[] { "ManagedInstallerClass.InstallHelper", "sc.Start()", "Utils.StartProcess" })
            AssertEx.True(gate < repair.IndexOf(start, StringComparison.Ordinal), "upgrade gate follows " + start);

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

        string stop = Body("internal static int UpgradeStopForMsi(string transaction)");
        InOrder(stop, "stop", "UpgradeLifecyclePolicy.Stop(transaction, service,", "InstallationSafety.RequireSystemMaintenance();",
            "InstallationSafety.RequireProtectedMachineData();", "ValidateRegisteredServiceImage(true);",
            "UpgradeLifecyclePolicy.SetAsidePriorSnapshot(new MachineDataUpgradeFiles(), Utils.AppDataPath,",
            "WindowsFirewall.RequireServiceRunning();", "\"BFE\"", "writeMarker: UpgradeMarker.Write",
            "terminateControllers: TerminateInstallationControllers", "UpgradeLifecyclePolicy.TakeSnapshot(new MachineDataUpgradeFiles(), Utils.AppDataPath, transaction);");
        AssertEx.False(stop.Contains("CleanupForMsi") || stop.Contains("RemoveWfpObjects") || stop.Contains("RestoreOwnedState") || stop.Contains("DisableHostsFile"));

        InOrder(Body("internal static int UpgradeStartForMsi(string transaction)"), "start",
            "UpgradeLifecyclePolicy.RequireStartable(", "MachineDataGuard.InstallDefaults();",
            "UpgradeLifecyclePolicy.DeleteStaleSnapshots(files, data, transaction);", "service.Start();",
            "UpgradeLifecyclePolicy.WaitForRunning(", "service.SetRestartOnFailure(true);", "EnsureHealth(");

        InOrder(Body("internal static int UpgradeRestoreForMsi(string transaction)"), "restore",
            "UpgradeLifecyclePolicy.Restore(transaction, marker,", "StopGracefully(", "UpgradeLifecyclePolicy.RestoreSnapshot(files, data, transaction);",
            "MachineDataGuard.InstallDefaults();", "SetAutomaticStart();", "service.SetRestartOnFailure(true);",
            "EnsureServiceInstalledAndRunning(Utils.LOG_ID_INSTALLER, true, transaction)", "CleanUpAfterUpgrade(transaction,",
            "finally", "if (ServiceExists()) SetAutomaticStart();");

        string abort = Body("internal static int UpgradeAbortForMsi(string transaction)");
        InOrder(abort, "abort", "service.SetRestartOnFailure(false);", "ServiceStartMode.Disabled", "StopGracefully(",
            "InstallationSafety.TerminateFailedInstallService();", "TerminateInstallationControllers();");
        AssertEx.False(abort.Contains("ServiceStartMode.Automatic"), "abort must leave the service disabled for the restore step");
        AssertEx.False(abort.Contains("UpgradeMarker.Clear") || abort.Contains("CleanUpAfterUpgrade"), "abort must leave the marker for the restore step");

        string commit = Body("internal static int UpgradeCommitForMsi(string transaction)");
        InOrder(commit, "commit", "UpgradeLifecyclePolicy.CommitKeeps(UpgradeMarker.Read(), transaction)", "CleanUpAfterUpgrade(transaction,",
            "private static void CleanUpAfterUpgrade(", "deleteSnapshots();", "UpgradeMarker.Clear(transaction);");

        string cleanup = Body("private static int CleanupStoppedInstallation(");
        InOrder(cleanup, "removal", "ProtectionReleasePolicy.Release(", "RemoveProductRegistry();", "return released ? 0 : -1;",
            "private static void RemoveProductRegistry()", "key.DeleteValue(UpgradeLifecyclePolicy.MarkerValue, false);",
            "RegistryAuditPolicyJournal.Instance.ReadAll(", "ProductRegistryPolicy.Plan(");

        string program = PromptTransactionIntegrationTests.Source("TinyWall/Program.cs");
        foreach (string mode in new[] { "StopMode", "RestoreMode", "StartMode", "AbortMode", "CommitMode" })
            AssertEx.True(program.Contains("RunUpgradeStep(args, UpgradeLifecyclePolicy." + mode + ", TinyWallDoctor.Upgrade"), "mode not dispatched: " + mode);

        string controller = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallController.cs");
        int notice = controller.IndexOf("TinyWallDoctor.ServiceStartBlockedByUpgrade()", StringComparison.Ordinal);
        AssertEx.True(notice > 0 && notice < controller.IndexOf("TinyWallDoctor.EnsureServiceInstalledAndRunning(Utils.LOG_ID_GUI, false)", StringComparison.Ordinal));

        string fault = PromptTransactionIntegrationTests.Source("TinyWall/Installer/UpgradeFault.cs");
        AssertEx.True(fault.Contains("[Conditional(\"UPGRADE_FAULT_TEST\")]"));
        string project = PromptTransactionIntegrationTests.Source("TinyWall/TinyWall.csproj");
        AssertEx.True(project.Contains("<PropertyGroup Condition=\"'$(Configuration)' == 'UpgradeFaultTest'\">"));
        AssertEx.Equal(1, project.Split("UPGRADE_FAULT_TEST").Length - 1, "only the UpgradeFaultTest configuration defines the fault symbol");
    }
}

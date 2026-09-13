using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class RuntimeJournalTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("diagnostics waits for trusted opt-in and retains only enabled intervals", OptIn),
        ("diagnostics toggle follows successful policy commit and preserves exceptions", CommitToggle),
        ("diagnostics captures lifecycle stages without secret-bearing exception text", PrivacyAndLifecycle),
        ("diagnostics concurrent overflow never waits for blocked disk", ConcurrentOverflow),
        ("diagnostics disk failure recovers with loss counters", DiskFailure),
        ("diagnostics rotates within file and generation bounds", Rotation),
        ("diagnostics guard failures prevent unvalidated filesystem writes", GuardFailure),
        ("diagnostics shutdown waits at most a bounded interval", ShutdownTimeout),
        ("diagnostics prompt statuses disclose no prompt authority", PromptStatuses),
        ("diagnostics adapter uses uncached read-only guard and committed settings", AdapterWiring),
    };

    private sealed class MemorySink : IRuntimeJournalSink
    {
        internal readonly ConcurrentQueue<string> Lines = new();
        public void Append(string line) => Lines.Enqueue(line);
    }

    private sealed class BlockingSink : IRuntimeJournalSink, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        internal readonly MemorySink Memory = new();
        public void Append(string line)
        {
            Entered.Set();
            Release.Wait();
            Memory.Append(line);
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class FaultSink : IRuntimeJournalSink
    {
        private int attempts;
        internal readonly MemorySink Memory = new();
        public void Append(string line)
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("sensitive-path-or-device-message");
            Memory.Append(line);
        }
    }

    private static void Check(bool value, string message = "Diagnostic invariant failed.")
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Wait(Func<bool> condition) => Check(SpinWait.SpinUntil(condition, 6000), "Diagnostic writer did not make expected progress.");

    private static bool Has(MemorySink sink, string eventCode, string? result = null) => sink.Lines.Any(line =>
    {
        using var json = JsonDocument.Parse(line);
        return json.RootElement.GetProperty("event").GetString() == eventCode &&
            (result == null || json.RootElement.GetProperty("result").GetString() == result);
    });

    private static void Stop(RuntimeJournal journal)
    {
        journal.Dispose();
        Check(journal.WaitForExit(6000));
    }

    private static void OptIn()
    {
        var sink = new MemorySink();
        int factories = 0;
        using var journal = new RuntimeJournal(() => { Interlocked.Increment(ref factories); return sink; }, 321, heartbeatMilliseconds: 20);
        journal.Emit(RuntimeEvent.service_start, RuntimeResult.attempt);
        Thread.Sleep(120); // Writer has time to poll; pending configuration must never open a sink.
        Check(factories == 0 && sink.Lines.IsEmpty);
        journal.SetEnabled(false);
        journal.Emit(RuntimeEvent.policy_publish, RuntimeResult.success);
        journal.ObserveDecision(true);
        Thread.Sleep(120);
        Check(factories == 0 && sink.Lines.IsEmpty);
        journal.SetEnabled(true);
        journal.ObserveDecision(true);
        journal.ObserveDecision(false);
        journal.SetAuditAvailable(true);
        Wait(() => Has(sink, "heartbeat"));
        Check(!Has(sink, "service_start") && !Has(sink, "policy_publish"));
        journal.SetEnabled(false);
        Wait(() => Has(sink, "diagnostics_disabled"));
        int disabledCount = sink.Lines.Count;
        journal.ObserveDecision(true);
        journal.Emit(RuntimeEvent.policy_publish, RuntimeResult.success);
        Thread.Sleep(120);
        Check(sink.Lines.Count == disabledCount);
        journal.SetEnabled(true);
        Wait(() => sink.Lines.Count > disabledCount);
        Stop(journal);
        Check(factories == 1, "Enable cycles must reuse the same writer/sink.");
        var runIds = new HashSet<string>();
        foreach (string line in sink.Lines)
        {
            using var json = JsonDocument.Parse(line);
            var row = json.RootElement;
            runIds.Add(row.GetProperty("run_id").GetString()!);
            Check(row.GetProperty("observed_allow").GetInt64() <= 1);
            Check(row.GetProperty("observed_drop").GetInt64() <= 1);
        }
        Check(runIds.Count == 1);
    }

    private static void CommitToggle()
    {
        var sink = new MemorySink();
        using var journal = new RuntimeJournal(() => sink, 322);
        journal.SetEnabled(false);
        var failure = new IOException("secret config content");
        foreach (bool start in new[] { false, true })
        {
            journal.SetEnabled(start);
            foreach (bool failPersistence in new[] { false, true })
            {
                int rollback = 0, published = 0;
                try
                {
                    journal.CommitConfiguration(!start, () => PolicyChangeTransaction.Apply(
                        () => { if (failPersistence) throw failure; },
                        () => throw failure, () => rollback++, () => published++,
                        () => throw new InvalidOperationException("Unexpected fail-closed.")));
                    throw new InvalidOperationException("Expected policy failure.");
                }
                catch (IOException caught) { Check(ReferenceEquals(failure, caught)); }
                Check(journal.Enabled == start && rollback == 1 && published == 0);
            }
        }
        journal.CommitConfiguration(false, () => { });
        Check(!journal.Enabled);
        journal.CommitConfiguration(true, () => { });
        Check(journal.Enabled);
        Stop(journal);
    }

    private static void PrivacyAndLifecycle()
    {
        using var blockedWriter = new BlockingSink();
        var sink = blockedWriter.Memory;
        using var journal = new RuntimeJournal(() => blockedWriter, 323);
        journal.SetEnabled(true);
        Wait(() => blockedWriter.Entered.IsSet);
        // Deliberately hold the writer outside the queue lock so this schema/exception
        // test does not race the journal's intentional nonblocking contention drops.
        journal.Emit(RuntimeEvent.service_start, RuntimeResult.attempt);
        journal.Run(RuntimeEvent.baseline_register, () => { });
        journal.SetEnabled(true);
        journal.Run(RuntimeEvent.policy_journal, () => { });
        journal.Run(RuntimeEvent.policy_persist, () => { });
        journal.Run(RuntimeEvent.policy_enforce, () => { });
        journal.Run(RuntimeEvent.policy_publish, () => { });
        journal.Emit(RuntimeEvent.service_ready, RuntimeResult.success);
        const string secret = "C:\\Users\\private\\token-123.example:443 password=hidden";
        var error = new IOException(secret);
        error.Data["credential"] = secret;
        try { journal.Run(RuntimeEvent.policy_recovery, () => throw error); }
        catch (IOException caught) { Check(ReferenceEquals(caught, error)); }
        journal.Run(RuntimeEvent.fail_closed, () => { });
        journal.Emit(RuntimeEvent.service_shutdown, RuntimeResult.success);
        journal.Emit((RuntimeEvent)999, RuntimeResult.success);
        blockedWriter.Release.Set();
        Stop(journal);
        // Optional integration fixture: exercise the real producer against the PowerShell
        // collector without exposing production settings, WFP, or a service process.
        string? fixtureDirectory = Environment.GetEnvironmentVariable("SECUREWALL_DIAGNOSTIC_TEST_OUTPUT");
        if (!string.IsNullOrEmpty(fixtureDirectory))
        {
            Directory.CreateDirectory(fixtureDirectory);
            File.WriteAllLines(Path.Combine(fixtureDirectory, "runtime.jsonl"), sink.Lines, new UTF8Encoding(false));
        }
        Check(Has(sink, "baseline_register", "success") && Has(sink, "policy_recovery", "failure") && Has(sink, "service_shutdown"));
        string[] fields = { "schema", "run_id", "process_id", "sequence", "utc", "uptime_ms", "event", "result", "hresult",
            "dropped_records", "write_failures", "observed_allow", "observed_drop", "audit_available", "observed_port_blocklist_drop" };
        long previousSequence = 0, previousUptime = 0;
        foreach (string line in sink.Lines)
        {
            Check(!line.Contains(secret) && !line.Contains("credential") && !line.Contains("IOException"));
            Check(Encoding.UTF8.GetByteCount(line) + 1 <= RuntimeJournal.MaxRecordBytes);
            using var json = JsonDocument.Parse(line);
            var row = json.RootElement;
            Check(row.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(fields.Order()));
            Check(row.GetProperty("schema").GetInt32() == 2 && row.GetProperty("process_id").GetInt32() == 323);
            Check(Guid.TryParseExact(row.GetProperty("run_id").GetString(), "D", out _));
            Check(row.GetProperty("utc").GetString()!.EndsWith("Z", StringComparison.Ordinal));
            long sequence = row.GetProperty("sequence").GetInt64(), uptime = row.GetProperty("uptime_ms").GetInt64();
            Check(sequence > previousSequence && uptime >= previousUptime);
            previousSequence = sequence; previousUptime = uptime;
            if (row.GetProperty("event").GetString() == "policy_recovery" && row.GetProperty("result").GetString() == "failure")
                Check(row.GetProperty("hresult").GetInt32() == error.HResult);
        }
        Check(journal.DroppedRecords >= 1);
    }

    private static void ConcurrentOverflow()
    {
        using var sink = new BlockingSink();
        using var journal = new RuntimeJournal(() => sink, 324, capacity: 8);
        journal.SetEnabled(true);
        Check(sink.Entered.Wait(6000));
        try
        {
            var clock = Stopwatch.StartNew();
            Parallel.For(0, 2048, _ => { journal.ObserveDecision(true); journal.Emit(RuntimeEvent.policy_enforce, RuntimeResult.success); });
            Check(clock.Elapsed < TimeSpan.FromSeconds(3));
            Check(journal.DroppedRecords >= 2040);
        }
        finally { sink.Release.Set(); }
        Stop(journal);
        Check(sink.Memory.Lines.Count <= 9);
        using var last = JsonDocument.Parse(sink.Memory.Lines.Last());
        Check(last.RootElement.GetProperty("observed_allow").GetInt64() == 2048);
        Check(last.RootElement.GetProperty("dropped_records").GetInt64() >= 2040);
    }

    private static void DiskFailure()
    {
        var sink = new FaultSink();
        using var journal = new RuntimeJournal(() => sink, 325, heartbeatMilliseconds: 20);
        journal.SetEnabled(true);
        Wait(() => !sink.Memory.Lines.IsEmpty);
        Stop(journal);
        Check(journal.WriteFailures == 1 && journal.DroppedRecords >= 1);
        using var json = JsonDocument.Parse(sink.Memory.Lines.First());
        Check(json.RootElement.GetProperty("write_failures").GetInt64() == 1);
        Check(json.RootElement.GetProperty("dropped_records").GetInt64() >= 1);
        Check(!sink.Memory.Lines.Any(line => line.Contains("sensitive")));
    }

    private static void Rotation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SecureWall-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            int guards = 0;
            var sink = new RotatingRuntimeJournalSink(directory, () => guards++, 2048);
            for (int i = 0; i < 40; i++) sink.Append("{\"n\":" + i + ",\"padding\":\"" + new string('x', 1000) + "\"}");
            var files = Directory.GetFiles(directory);
            Check(files.Length == 4 && guards == 80, "Every append performs entry and pre-open full guard validation, including rotation.");
            Check(files.All(file => new FileInfo(file).Length <= 2048));
            Check(files.Sum(file => new FileInfo(file).Length) <= 8192);
            foreach (string file in files)
                foreach (string line in File.ReadAllLines(file)) { using var json = JsonDocument.Parse(line); }
            Check(File.ReadAllText(Path.Combine(directory, "runtime.jsonl")).Contains("\"n\":39"));
            foreach (string invalid in new[] { new string('\u00e9', 1024), "{}\n{}", "{}\r{}" })
            {
                bool failed = false;
                try { sink.Append(invalid); } catch (InvalidDataException) { failed = true; }
                Check(failed);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void GuardFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SecureWall-journal-" + Guid.NewGuid().ToString("N"));
        var sink = new RotatingRuntimeJournalSink(directory, () => throw new UnauthorizedAccessException("private-tree-path"));
        try { sink.Append("{}"); } catch (UnauthorizedAccessException) { }
        Check(!Directory.Exists(directory));
        try
        {
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "runtime.jsonl");
            File.WriteAllText(file, "original");
            try { sink.Append("{}"); } catch (UnauthorizedAccessException) { }
            Check(File.ReadAllText(file) == "original" && Directory.GetFiles(directory).Length == 1);

            foreach (bool rotate in new[] { false, true })
            {
                string original = rotate ? new string('x', 2048) : "original";
                File.WriteAllText(file, original);
                int guards = 0;
                var finalGuardSink = new RotatingRuntimeJournalSink(directory, () =>
                {
                    if (++guards == 2) throw new UnauthorizedAccessException("private-tree-path");
                }, 2048);
                bool rejected = false;
                try { finalGuardSink.Append("{\"unsafe_append\":true}"); }
                catch (UnauthorizedAccessException) { rejected = true; }
                Check(rejected && guards == 2);
                if (rotate)
                    Check(!File.Exists(file) && File.ReadAllText(Path.Combine(directory, "runtime.1.jsonl")) == original);
                else
                    Check(File.ReadAllText(file) == original);
                Check(Directory.GetFiles(directory).All(path => !File.ReadAllText(path).Contains("unsafe_append")));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void ShutdownTimeout()
    {
        using var sink = new BlockingSink();
        using var journal = new RuntimeJournal(() => sink, 326);
        journal.SetEnabled(true);
        Check(sink.Entered.Wait(6000));
        try
        {
            var clock = Stopwatch.StartNew();
            journal.FinishShutdown();
            Check(clock.Elapsed < TimeSpan.FromSeconds(2), "Shutdown must not wait indefinitely for IO.");
        }
        finally { sink.Release.Set(); }
        Check(journal.WaitForExit(6000));
    }

    private static void PromptStatuses()
    {
        Check(RuntimeJournal.PromptResult(PromptActionStatus.Allowed) == RuntimeResult.success);
        Check(RuntimeJournal.PromptResult(PromptActionStatus.Dismissed) == RuntimeResult.success);
        Check(RuntimeJournal.PromptResult(PromptActionStatus.Expired) == RuntimeResult.expired);
        Check(RuntimeJournal.PromptResult(PromptActionStatus.UnknownToken) == RuntimeResult.unknown_token);
        Check(RuntimeJournal.PromptResult(PromptActionStatus.NotAllowable) == RuntimeResult.not_allowable);
        Check(RuntimeJournal.PromptResult(PromptActionStatus.ApplyFailed) == RuntimeResult.failure);
    }

    private static void AdapterWiring()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "TinyWall", "TinyWallService.cs"))) root = Directory.GetParent(root)?.FullName;
        Check(root != null);
        string Read(string file) => File.ReadAllText(Path.Combine(root!, "TinyWall", file));
        string adapter = Read("ServiceRuntimeDiagnostics.cs"), guard = Read(Path.Combine("Installer", "MachineDataGuard.cs"));
        Check(adapter.Contains("MachineDataGuard.RequireForDiagnostics") && !adapter.Contains("LocalApplicationData"));
        Check(guard.Contains("RequireForDiagnostics() => Validate(create: false)"));
        string service = Read("TinyWallService.cs");
        Check(service.Contains("Diagnostics.CommitConfiguration(candidate.EnableDiagnosticLogging, () => PolicyChangeTransaction.Apply("));
        Check(service.Contains("Diagnostics.ObserveDecision(eventType == EventLogEvent.ALLOWED)"));
        Check(service.Contains("Diagnostics.SetAuditAvailable(LogWatcher.AuditEnrichmentAvailable)"));
        string settings = Read("SettingsForm.cs"), configuration = Read("ServerConfiguration.cs");
        Check(configuration.Contains("public bool EnableDiagnosticLogging { get; set; } = false;"));
        Check(settings.Contains("chkEnableDiagnosticLogging.Checked = TmpConfig.Service.EnableDiagnosticLogging"));
        Check(settings.Contains("TmpConfig.Service.EnableDiagnosticLogging = chkEnableDiagnosticLogging.Checked"));
    }
}

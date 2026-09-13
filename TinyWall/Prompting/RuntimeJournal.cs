using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace pylorak.TinyWall.Prompting
{
    // Deliberately no arbitrary text fields. These identifiers are the on-disk vocabulary.
    internal enum RuntimeEvent
    {
        service_start, service_ready, service_failure, service_stop_requested, service_shutdown,
        baseline_register, policy_journal, policy_persist, policy_enforce, policy_rollback,
        policy_publish, policy_recovery, policy_recovery_clear, fail_closed, heartbeat,
        diagnostics_enabled, diagnostics_disabled, prompt_allow, prompt_ignore, network_reload, display_reload,
        hosts_backup, hosts_update, hosts_install, hosts_restore, hosts_restore_verify, hosts_protection, dns_flush,
        port_blocklist_state, hosts_blocklist_state, port_blocklist_rules, configuration_load, database_load,
        wfp_subscribe, wfp_unsubscribe, windows_firewall_start, windows_firewall_stop, rule_expiry,
        audit_lease_start, audit_lease_stop, audit_subscribe, audit_unsubscribe, audit_health, audit_record_error,
        audit_recovery, prompt_suppression, attribution_snapshot, unavailable_rule_paths
    }
    internal enum RuntimeResult { attempt, success, failure, observed, absent, unknown_token, expired, not_allowable, locked, enabled, disabled, present, fallback, skipped }

    internal interface IRuntimeJournalSink
    {
        void Append(string line);
    }

    // One background writer for the entire service run, including repeated enable/disable cycles.
    // Producers never wait for disk or guard validation. Early records stay in bounded memory
    // until the recovered, trusted configuration decides whether diagnostics are enabled.
    internal sealed class RuntimeJournal : IDisposable
    {
        internal const int QueueCapacity = 256;
        internal const int MaxRecordBytes = 2048;
        private readonly object gate = new object();
        private readonly Queue<Record> queue = new Queue<Record>();
        private readonly Func<IRuntimeJournalSink> createSink;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread worker;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly string runId = Guid.NewGuid().ToString("D");
        private readonly int processId;
        private readonly int capacity;
        private readonly int heartbeatMilliseconds;
        private long sequence, dropped, writeFailures, observedAllow, observedDrop, observedPortBlocklistDrop;
        private int enabled = -1; // -1 = startup decision pending; 0 = disabled; 1 = enabled
        private int auditAvailable = -1;
        private volatile bool stopping;

        private sealed class Record
        {
            internal RuntimeEvent Event;
            internal RuntimeResult Result;
            internal DateTime Utc;
            internal long Uptime, Sequence;
            internal int HResult;
        }

        internal RuntimeJournal(Func<IRuntimeJournalSink> createSink, int processId,
            int capacity = QueueCapacity, int heartbeatMilliseconds = 60000)
        {
            if (capacity < 1 || capacity > QueueCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (heartbeatMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(heartbeatMilliseconds));
            this.createSink = createSink ?? throw new ArgumentNullException(nameof(createSink));
            this.processId = processId;
            this.capacity = capacity;
            this.heartbeatMilliseconds = heartbeatMilliseconds;
            worker = new Thread(WriteLoop) { IsBackground = true, Name = "RuntimeDiagnostics" };
            try { worker.Start(); }
            catch { wake.Dispose(); throw; }
        }

        internal long DroppedRecords => Interlocked.Read(ref dropped);
        internal long WriteFailures => Interlocked.Read(ref writeFailures);
        internal bool Enabled => Volatile.Read(ref enabled) == 1;

        internal void SetEnabled(bool value)
        {
            try
            {
                // Queue synchronization is short and contains no IO, callbacks, or policy code.
                lock (gate)
                {
                    if (stopping || enabled == (value ? 1 : 0)) return;
                    if (enabled == -1 && !value) queue.Clear();
                    bool wasEnabled = enabled == 1;
                    Volatile.Write(ref enabled, value ? 1 : 0);
                    if (value || wasEnabled)
                        Enqueue(value ? RuntimeEvent.diagnostics_enabled : RuntimeEvent.diagnostics_disabled,
                            RuntimeResult.success, 0);
                }
                wake.Set();
            }
            catch { Interlocked.Increment(ref dropped); }
        }

        internal void ObserveDecision(bool allowed)
        {
            if (!Enabled || stopping) return;
            if (allowed) Interlocked.Increment(ref observedAllow);
            else Interlocked.Increment(ref observedDrop);
        }

        internal void ObservePortBlocklistDrop()
        {
            if (Enabled && !stopping) Interlocked.Increment(ref observedPortBlocklistDrop);
        }

        internal void SetAuditAvailable(bool available)
        {
            Volatile.Write(ref auditAvailable, available ? 1 : 0);
        }

        internal void Emit(RuntimeEvent eventCode, RuntimeResult result, int hresult = 0)
        {
            bool entered = false;
            try
            {
                if (stopping || Volatile.Read(ref enabled) == 0) return;
                if (!Enum.IsDefined(typeof(RuntimeEvent), eventCode) || !Enum.IsDefined(typeof(RuntimeResult), result))
                { Interlocked.Increment(ref dropped); return; }
                entered = Monitor.TryEnter(gate);
                if (!entered) { Interlocked.Increment(ref dropped); return; }
                if (stopping || enabled == 0) return;
                Enqueue(eventCode, result, hresult);
            }
            catch { Interlocked.Increment(ref dropped); }
            finally { if (entered) Monitor.Exit(gate); }
            try { wake.Set(); } catch { }
        }

        private void Enqueue(RuntimeEvent eventCode, RuntimeResult result, int hresult)
        {
            long next = ++sequence;
            if (queue.Count >= capacity) { Interlocked.Increment(ref dropped); return; }
            queue.Enqueue(new Record { Event = eventCode, Result = result, HResult = hresult,
                Sequence = next, Utc = DateTime.UtcNow, Uptime = clock.ElapsedMilliseconds });
        }

        // Preserve the original exception and policy ordering, even when logging is unavailable.
        internal void Run(RuntimeEvent eventCode, Action action)
        {
            Emit(eventCode, RuntimeResult.attempt);
            try { action(); }
            catch (Exception exception) { Emit(eventCode, RuntimeResult.failure, exception.HResult); throw; }
            Emit(eventCode, RuntimeResult.success);
        }

        internal static RuntimeResult PromptResult(PromptActionStatus status)
        {
            switch (status)
            {
                case PromptActionStatus.Allowed:
                case PromptActionStatus.Dismissed: return RuntimeResult.success;
                case PromptActionStatus.UnknownToken: return RuntimeResult.unknown_token;
                case PromptActionStatus.Expired: return RuntimeResult.expired;
                case PromptActionStatus.NotAllowable: return RuntimeResult.not_allowable;
                default: return RuntimeResult.failure;
            }
        }

        internal void CommitConfiguration(bool enableDiagnostics, Action commit)
        {
            commit();
            SetEnabled(enableDiagnostics);
        }

        private string Serialize(Record record)
        {
            // All strings come from fixed enums, a GUID, or the fixed timestamp format.
            return string.Format(CultureInfo.InvariantCulture,
                "{{\"schema\":2,\"run_id\":\"{0}\",\"process_id\":{1},\"sequence\":{2},\"utc\":\"{3}\",\"uptime_ms\":{4}," +
                "\"event\":\"{5}\",\"result\":\"{6}\",\"hresult\":{7},\"dropped_records\":{8},\"write_failures\":{9}," +
                "\"observed_allow\":{10},\"observed_drop\":{11},\"audit_available\":{12},\"observed_port_blocklist_drop\":{13}}}",
                runId, processId, record.Sequence, record.Utc.ToString("O", CultureInfo.InvariantCulture), record.Uptime,
                record.Event, record.Result, record.HResult, DroppedRecords, WriteFailures,
                Interlocked.Read(ref observedAllow), Interlocked.Read(ref observedDrop), Volatile.Read(ref auditAvailable), Interlocked.Read(ref observedPortBlocklistDrop));
        }

        private void WriteLoop()
        {
            IRuntimeJournalSink? sink = null;
            long nextHeartbeat = heartbeatMilliseconds, nextRetry = 0;
            try
            {
                while (true)
                {
                    long now = clock.ElapsedMilliseconds;
                    if (!stopping && Enabled && now >= nextHeartbeat)
                    {
                        Emit(RuntimeEvent.heartbeat, RuntimeResult.observed);
                        nextHeartbeat = now + heartbeatMilliseconds;
                    }
                    Record? record = null;
                    lock (gate)
                    {
                        if (enabled != -1 && queue.Count != 0 && now >= nextRetry)
                            record = queue.Dequeue();
                        else if (stopping)
                        {
                            Interlocked.Add(ref dropped, queue.Count);
                            queue.Clear();
                            break;
                        }
                    }
                    if (record == null) { wake.WaitOne(100); continue; }
                    try
                    {
                        string line = Serialize(record);
                        if (Encoding.UTF8.GetByteCount(line) + 1 > MaxRecordBytes)
                        { Interlocked.Increment(ref dropped); continue; }
                        if (sink == null) sink = createSink();
                        sink.Append(line);
                    }
                    catch
                    {
                        Interlocked.Increment(ref writeFailures);
                        Interlocked.Increment(ref dropped);
                        sink = null;
                        nextRetry = clock.ElapsedMilliseconds + 1000;
                    }
                }
            }
            catch { Interlocked.Increment(ref writeFailures); }
            finally { wake.Dispose(); }
        }

        public void Dispose()
        {
            // Never join the writer from a service/policy thread. A stuck disk cannot delay
            // withdrawing runtime grants or stopping the service. Final records are best effort.
            stopping = true;
            try { wake.Set(); } catch { }
        }

        internal bool WaitForExit(int milliseconds) => worker.Join(milliseconds); // Pure harness only.

        internal void FinishShutdown()
        {
            Dispose();
            try { worker.Join(250); } catch { }
        }
    }

    internal sealed class RotatingRuntimeJournalSink : IRuntimeJournalSink
    {
        internal const int MaxFileBytes = 1024 * 1024;
        internal const int RetainedGenerations = 3;
        private readonly string directory;
        private readonly Action guard;
        private readonly int maxFileBytes;

        internal RotatingRuntimeJournalSink(string directory, Action guard, int maxFileBytes = MaxFileBytes)
        {
            if (maxFileBytes < RuntimeJournal.MaxRecordBytes || maxFileBytes > MaxFileBytes)
                throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
            this.directory = directory;
            this.guard = guard;
            this.maxFileBytes = maxFileBytes;
        }

        private string FileName(int generation) => Path.Combine(directory,
            generation == 0 ? "runtime.jsonl" : "runtime." + generation.ToString(CultureInfo.InvariantCulture) + ".jsonl");

        public void Append(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            if (bytes.Length > RuntimeJournal.MaxRecordBytes || line.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new InvalidDataException("Invalid diagnostic record size or framing.");
            guard(); // No path inspection, creation, truncation, or opening precedes validation.
            Directory.CreateDirectory(directory); // Inherits only the already protected parent ACL.
            string active = FileName(0);
            long existing = 0;
            try { existing = new FileInfo(active).Length; }
            catch (FileNotFoundException) { }
            if (existing > maxFileBytes)
                throw new IOException("Existing diagnostic file exceeds the retention bound.");
            if (existing + bytes.Length > maxFileBytes)
            {
                for (int generation = RetainedGenerations; generation >= 1; --generation)
                {
                    string target = FileName(generation);
                    File.Delete(target);
                    try { File.Move(FileName(generation - 1), target); }
                    catch (FileNotFoundException) { }
                }
            }
            guard(); // Revalidate the full tree immediately before opening the append target.
            using (var stream = new FileStream(active, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.Write(bytes, 0, bytes.Length);
        }
    }
}

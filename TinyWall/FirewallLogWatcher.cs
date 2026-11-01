using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Samples;
using pylorak.TinyWall.Prompting;
using pylorak.Utilities;
using pylorak.Windows;

namespace pylorak.TinyWall
{
    // A blocked connection (5157) or bind (5159) from the Security log. RemotePort is null
    // for a bind, which has no remote endpoint.
    internal sealed record ActivityAudit(DateTimeOffset TimestampUtc, uint ProcessId, string ApplicationPath,
        byte Protocol, int LocalPort, int? RemotePort, ulong FilterRuntimeId);

    internal sealed class FirewallLogWatcher : Disposable
    {
        private static readonly Guid ConnectionLoggingAuditSubcategory =
            new Guid("{0CCE9226-69AE-11D9-BED3-505054503030}");

        private readonly AuditWatcherLifetime _lifetime = new AuditWatcherLifetime();
        private readonly AuditSubscriptionHealth _health = new AuditSubscriptionHealth();
        private readonly CoalescedDiagnostic _subscriptionErrors = new CoalescedDiagnostic();
        private readonly CoalescedDiagnostic _recordErrors = new CoalescedDiagnostic();
        private volatile EventLogWatcher? _logWatcher;
        private AuditPolicyLease? _failureAuditLease;
        private volatile bool _failureLeaseOwned;

        internal delegate void BlockedConnectionDelegate(FirewallLogWatcher sender, BlockedConnectionAuditEvent blockedConnection);
        internal event BlockedConnectionDelegate? BlockedConnection;
        internal delegate void ActivityAuditDelegate(FirewallLogWatcher sender, ActivityAudit audit);
        // Every blocked connection or bind, for naming the process behind a Network Activity row.
        internal event ActivityAuditDelegate? ActivityAudited;

        private readonly Action<RuntimeEvent, RuntimeResult, int>? diagnostic;
        private int subscriptionHResult;
        internal int SubscriptionHResult => Volatile.Read(ref subscriptionHResult);
        internal long RecordErrors => _recordErrors.Total;

        private void Report(RuntimeEvent code, RuntimeResult result, int hresult = 0)
        {
            try { diagnostic?.Invoke(code, result, hresult); } catch { }
        }

        internal FirewallLogWatcher(Action<RuntimeEvent, RuntimeResult, int>? diagnostic = null)
        {
            this.diagnostic = diagnostic;
            try
            {
                _failureAuditLease = AcquireAuditLease(AuditPolicyFlags.Failure);
                _failureLeaseOwned = true;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref subscriptionHResult, exception.HResult);
                Utils.Log("Cannot enable filtering-platform failure auditing; service attribution is unavailable.", Utils.LOG_ID_SERVICE);
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
            StartSubscription();
        }

        private void StartSubscription()
        {
            if (_health.Stopped) return;
            Report(RuntimeEvent.audit_subscribe, RuntimeResult.attempt);
            try
            {
                // Prompt attribution reads only 5157 (connection blocked by WFP). Network Activity
                // also uses 5159 (bind blocked, including raw sockets) to name the blocking process.
                var query = new EventLogQuery("Security", PathType.LogName, "*[System[(EventID=5157 or EventID=5159)]]");
                var watcher = new EventLogWatcher(query);
                if (!_lifetime.Attach(watcher)) { watcher.Dispose(); return; }
                _logWatcher = watcher;
                watcher.EventRecordWritten += LogWatcherEventRecordWritten;
                // Set before enabling: a synchronous failure callback must win.
                _health.Starting();
                watcher.Enabled = true;
                bool subscribed = _health.SubscriptionAvailable;
                Report(RuntimeEvent.audit_subscribe, subscribed ? RuntimeResult.success : RuntimeResult.failure,
                    subscribed ? 0 : SubscriptionHResult);
            }
            catch (Exception exception)
            {
                ReportSubscriptionFailure(_logWatcher, exception);
                Report(RuntimeEvent.audit_subscribe, RuntimeResult.failure, exception.HResult);
            }
        }

        private void ReportSubscriptionFailure(object? sender, Exception exception)
        {
            if (sender != null && !_lifetime.Fail(sender)) return;
            Volatile.Write(ref subscriptionHResult, exception.HResult);
            _health.Failed();
            _subscriptionErrors.Record();
            if (_subscriptionErrors.TryReport(DateTimeOffset.UtcNow, out long count))
            {
                Utils.Log("Windows Security event subscription failed (" + count +
                    " failures since the previous report). Service attribution is unavailable; firewall enforcement is unchanged. Restart the service to restore attribution after correcting event access.", Utils.LOG_ID_SERVICE);
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
        }

        internal bool AuditEnrichmentAvailable => _health.Available(_failureLeaseOwned);

        protected override void Dispose(bool disposing)
        {
            EventLogWatcher? watcher = null;
            _lifetime.Stop(() =>
            {
                _health.Stop();
                _failureLeaseOwned = false;
                watcher = _logWatcher;
                _logWatcher = null;
                if (watcher != null) watcher.EventRecordWritten -= LogWatcherEventRecordWritten;
            }, () =>
            {
                // EventLogWatcher.Dispose waits for callbacks. The lifetime seam releases
                // its lock before this drain, so a callback blocked in Accept can finish.
                try { watcher?.Dispose(); Report(RuntimeEvent.audit_unsubscribe, RuntimeResult.success); }
                catch (Exception exception) { Report(RuntimeEvent.audit_unsubscribe, RuntimeResult.failure, exception.HResult); Utils.LogException(exception, Utils.LOG_ID_SERVICE); }
                try { DisposeAuditLease(ref _failureAuditLease); }
                catch (Exception exception) { Utils.LogException(exception, Utils.LOG_ID_SERVICE); }
                base.Dispose(disposing);
            });
        }

        private void LogWatcherEventRecordWritten(object? sender, EventRecordWrittenEventArgs eventArgs)
        {
            EventRecord? record = eventArgs.EventRecord;
            try
            {
                if (!_lifetime.Accept(sender)) return;
                if (eventArgs.EventException != null)
                {
                    ReportSubscriptionFailure(sender, eventArgs.EventException);
                    return;
                }
                if (record == null || !_health.SubscriptionAvailable) return;
                IReadOnlyDictionary<string, string> fields = ReadNamedFields(record);
                DateTimeOffset timestamp = record.TimeCreated.HasValue
                    ? new DateTimeOffset(record.TimeCreated.Value) : DateTimeOffset.UtcNow;
                if (record.Id == 5157 &&
                    SecurityEvent5157Parser.TryParse(fields, timestamp.ToUniversalTime(), out BlockedConnectionAuditEvent parsed))
                {
                    BlockedConnectionAuditEvent normalized = Normalize(parsed);
                    if (_lifetime.Accept(sender) && AuditEnrichmentAvailable)
                    {
                        BlockedConnection?.Invoke(this, normalized);
                        ActivityAudited?.Invoke(this, new ActivityAudit(normalized.TimestampUtc, normalized.ProcessId,
                            normalized.ApplicationPath, normalized.Protocol, normalized.LocalPort, normalized.RemotePort,
                            normalized.FilterRuntimeId));
                    }
                }
                else if (record.Id == 5159 && TryParseBind(fields, timestamp.ToUniversalTime(), out ActivityAudit? bind))
                {
                    if (_lifetime.Accept(sender) && AuditEnrichmentAvailable) ActivityAudited?.Invoke(this, bind!);
                }
            }
            catch (Exception exception)
            {
                if (!_lifetime.Accept(sender)) return;
                _recordErrors.Record();
                if (_recordErrors.TryReport(DateTimeOffset.UtcNow, out long count))
                {
                    Utils.Log("Could not process " + count + " Security event records since the previous report.", Utils.LOG_ID_SERVICE);
                    Utils.LogException(exception, Utils.LOG_ID_SERVICE);
                }
            }
            finally { record?.Dispose(); }
        }
        private static IReadOnlyDictionary<string, string> ReadNamedFields(EventRecord record)
        {
            XDocument document = XDocument.Parse(record.ToXml(), LoadOptions.None);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement element in document.Descendants().Where(
                node => string.Equals(node.Name.LocalName, "Data", StringComparison.Ordinal)))
            {
                string? name = element.Attribute("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                    fields[name!] = element.Value;
            }

            return fields;
        }

        // 5159: a bind blocked by WFP. It has no remote endpoint or direction.
        private static bool TryParseBind(IReadOnlyDictionary<string, string> fields, DateTimeOffset timestampUtc, out ActivityAudit? parsed)
        {
            parsed = null;
            if (!fields.TryGetValue("ProcessId", out string? processText) ||
                !uint.TryParse(processText, NumberStyles.None, CultureInfo.InvariantCulture, out uint processId) ||
                !fields.TryGetValue("Application", out string? application) || string.IsNullOrWhiteSpace(application) ||
                !fields.TryGetValue("SourcePort", out string? portText) ||
                !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int localPort) ||
                !fields.TryGetValue("Protocol", out string? protocolText) ||
                !byte.TryParse(protocolText, NumberStyles.None, CultureInfo.InvariantCulture, out byte protocol) ||
                !fields.TryGetValue("FilterRTID", out string? filterText) ||
                !ulong.TryParse(filterText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong filterRuntimeId))
                return false;
            parsed = new ActivityAudit(timestampUtc, processId, NormalizePath(application!), protocol, localPort, null, filterRuntimeId);
            return true;
        }

        private static BlockedConnectionAuditEvent Normalize(BlockedConnectionAuditEvent parsed)
        {
            string applicationPath = NormalizePath(parsed.ApplicationPath);
            return new BlockedConnectionAuditEvent(
                parsed.TimestampUtc,
                parsed.ProcessId,
                applicationPath,
                parsed.Direction,
                parsed.LocalAddress,
                parsed.LocalPort,
                parsed.RemoteAddress,
                parsed.RemotePort,
                parsed.Protocol,
                parsed.FilterRuntimeId,
                parsed.PackageSid);
        }

        private static string NormalizePath(string applicationPath)
        {
            string path = PathMapper.Instance.ConvertPathIgnoreErrors(applicationPath, PathFormat.Win32);
            return Utils.GetExactPath(path) ?? path;
        }

        private AuditPolicyLease AcquireAuditLease(AuditPolicyFlags requiredFlags)
        {
            AuditPolicyLease? lease = null;
            Report(RuntimeEvent.audit_lease_start, RuntimeResult.attempt);
            try
            {
                Privilege.RunWithPrivilege(Privilege.Security, true, delegate (object? state)
                {
                    lease = AuditPolicyLease.Acquire(
                        WindowsAuditPolicyBackend.Instance,
                        ConnectionLoggingAuditSubcategory,
                        requiredFlags,
                        RegistryAuditPolicyJournal.Instance);
                }, null);
                if (lease == null) throw new InvalidOperationException("Audit policy lease acquisition did not complete.");
                Report(RuntimeEvent.audit_lease_start, RuntimeResult.success);
                return lease;
            }
            catch (Exception error)
            {
                Report(RuntimeEvent.audit_lease_start, RuntimeResult.failure, error.HResult);
                if (lease != null)
                {
                    try
                    {
                        Privilege.RunWithPrivilege(
                            Privilege.Security,
                            true,
                            _ => lease.Dispose(),
                            null);
                    }
                    catch
                    {
                    }
                }

                throw;
            }
        }

        // Crash recovery for audit policy left behind by an unclean exit (crash,
        // FailFast, Process.Kill, MSI cleanup). Reads only the registry journal;
        // needs no MpsSvc, so callers can run it before any other startup work.
        // Throws AuditPolicyRestoreException when a healthy entry failed to restore;
        // malformed records are only reported in the result.
        internal static AuditPolicyRestoreResult RestoreAuditPolicyFromJournal()
        {
            AuditPolicyRestoreResult? result = null;
            Privilege.RunWithPrivilege(Privilege.Security, true, delegate (object? state)
            {
                result = AuditPolicyLease.RestoreFromJournal(
                    WindowsAuditPolicyBackend.Instance,
                    RegistryAuditPolicyJournal.Instance);
            }, null);
            return result ?? throw new InvalidOperationException("Audit policy journal restoration did not run.");
        }

        private void DisposeAuditLease(ref AuditPolicyLease? lease)
        {
            AuditPolicyLease? captured = lease;
            lease = null;
            if (captured == null)
                return;

            Report(RuntimeEvent.audit_lease_stop, RuntimeResult.attempt);
            try
            {
                Privilege.RunWithPrivilege(Privilege.Security, true, delegate (object? state)
                {
                    captured.Dispose();
                }, null);
                Report(RuntimeEvent.audit_lease_stop, RuntimeResult.success);
            }
            catch (Exception error) { Report(RuntimeEvent.audit_lease_stop, RuntimeResult.failure, error.HResult); throw; }
        }
    }
}

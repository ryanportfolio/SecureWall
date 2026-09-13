using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Samples;
using pylorak.TinyWall.Prompting;
using pylorak.Utilities;
using pylorak.Windows;

namespace pylorak.TinyWall
{
    internal sealed class FirewallLogWatcher : Disposable
    {
        private static readonly Guid ConnectionLoggingAuditSubcategory =
            new Guid("{0CCE9226-69AE-11D9-BED3-505054503030}");

        private readonly AuditWatcherLifetime _lifetime = new AuditWatcherLifetime();
        private object _lifecycle => _lifetime.SyncRoot;
        private readonly AuditSubscriptionHealth _health = new AuditSubscriptionHealth();
        private readonly CoalescedDiagnostic _subscriptionErrors = new CoalescedDiagnostic();
        private readonly CoalescedDiagnostic _recordErrors = new CoalescedDiagnostic();
        private volatile EventLogWatcher? _logWatcher;
        private AuditPolicyLease? _failureAuditLease;
        private AuditPolicyLease? _learningAuditLease;
        private volatile bool _learningEnabled;
        private volatile bool _failureLeaseOwned;

        internal delegate void NewLogEntryDelegate(FirewallLogWatcher sender, FirewallLogEntry entry);
        internal event NewLogEntryDelegate? NewLogEntry;
        internal delegate void BlockedConnectionDelegate(FirewallLogWatcher sender, BlockedConnectionAuditEvent blockedConnection);
        internal event BlockedConnectionDelegate? BlockedConnection;

        internal FirewallLogWatcher()
        {
            try
            {
                _failureAuditLease = AcquireAuditLease(AuditPolicyFlags.Failure);
                _failureLeaseOwned = true;
            }
            catch (Exception exception)
            {
                Utils.Log("Cannot enable filtering-platform failure auditing; service attribution is unavailable.", Utils.LOG_ID_SERVICE);
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
            StartSubscription();
        }

        private void StartSubscription()
        {
            if (_health.Stopped) return;
            try
            {
                var query = new EventLogQuery("Security", PathType.LogName,
                    "*[System[(EventID=5154 or EventID=5155 or EventID=5156 or EventID=5157 or EventID=5158 or EventID=5159)]]");
                var watcher = new EventLogWatcher(query);
                if (!_lifetime.Attach(watcher)) { watcher.Dispose(); return; }
                _logWatcher = watcher;
                watcher.EventRecordWritten += LogWatcherEventRecordWritten;
                // Set before enabling: a synchronous failure callback must win.
                _health.Starting();
                watcher.Enabled = true;
            }
            catch (Exception exception)
            {
                ReportSubscriptionFailure(_logWatcher, exception);
            }
        }

        private void ReportSubscriptionFailure(object? sender, Exception exception)
        {
            if (sender != null && !_lifetime.Fail(sender)) return;
            _health.Failed();
            _subscriptionErrors.Record();
            if (_subscriptionErrors.TryReport(DateTimeOffset.UtcNow, out long count))
            {
                Utils.Log("Windows Security event subscription failed (" + count +
                    " failures since the previous report). Service attribution is unavailable; firewall enforcement is unchanged. Restart the service to restore attribution after correcting event access.", Utils.LOG_ID_SERVICE);
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
        }

        internal bool LearningEnabled
        {
            get => _learningEnabled;
            set
            {
                lock (_lifecycle)
                {
                    if (_health.Stopped) throw new ObjectDisposedException(nameof(FirewallLogWatcher));
                    if (value == _learningEnabled) return;
                    if (value)
                    {
                        _learningAuditLease = AcquireAuditLease(AuditPolicyFlags.Success);
                        _learningEnabled = true;
                    }
                    else
                    {
                        _learningEnabled = false;
                        DisposeAuditLease(ref _learningAuditLease);
                    }
                }
            }
        }

        internal bool AuditEnrichmentAvailable => _health.Available(_failureLeaseOwned);

        protected override void Dispose(bool disposing)
        {
            EventLogWatcher? watcher = null;
            _lifetime.Stop(() =>
            {
                _health.Stop();
                _learningEnabled = false;
                _failureLeaseOwned = false;
                watcher = _logWatcher;
                _logWatcher = null;
                if (watcher != null) watcher.EventRecordWritten -= LogWatcherEventRecordWritten;
            }, () =>
            {
                // EventLogWatcher.Dispose waits for callbacks. A callback may be waiting
                // for LearningNewExceptions while a policy transition takes _lifecycle.
                // The lifetime seam releases that lock before entering this drain.
                try { watcher?.Dispose(); }
                catch (Exception exception) { Utils.LogException(exception, Utils.LOG_ID_SERVICE); }
                try { DisposeAuditLease(ref _learningAuditLease); }
                catch (Exception exception) { Utils.LogException(exception, Utils.LOG_ID_SERVICE); }
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
                    if (_lifetime.Accept(sender) && AuditEnrichmentAvailable) BlockedConnection?.Invoke(this, normalized);
                    if (_lifetime.Accept(sender) && _learningEnabled) NewLogEntry?.Invoke(this, ToFirewallLogEntry(normalized));
                    return;
                }
                if (_lifetime.Accept(sender) && _learningEnabled && TryParseLearningEntry(record.Id, fields, timestamp, out FirewallLogEntry entry))
                    NewLogEntry?.Invoke(this, entry);
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

        private static FirewallLogEntry ToFirewallLogEntry(BlockedConnectionAuditEvent parsed)
        {
            return new FirewallLogEntry
            {
                Timestamp = parsed.TimestampUtc.LocalDateTime,
                Event = EventLogEvent.BLOCKED_CONNECTION,
                ProcessId = parsed.ProcessId,
                AppPath = parsed.ApplicationPath,
                Direction = parsed.Direction == ConnectionDirection.Outbound
                    ? RuleDirection.Out
                    : RuleDirection.In,
                LocalIp = EmptyAddress(parsed.LocalAddress),
                LocalPort = parsed.LocalPort,
                RemoteIp = EmptyAddress(parsed.RemoteAddress),
                RemotePort = parsed.RemotePort,
                Protocol = (Protocol)parsed.Protocol,
                PackageId = parsed.PackageSid,
                FilterRuntimeId = parsed.FilterRuntimeId,
            };
        }

        private static bool TryParseLearningEntry(
            int eventId,
            IReadOnlyDictionary<string, string> fields,
            DateTimeOffset timestamp,
            out FirewallLogEntry entry)
        {
            entry = null!;
            if (!TryUInt32(fields, "ProcessID", out uint processId) ||
                !TryGet(fields, "Application", out string applicationPath) ||
                !TryGet(fields, "SourceAddress", out string localAddress) ||
                !TryPort(fields, "SourcePort", out int localPort) ||
                !TryUInt32(fields, "Protocol", out uint protocol))
            {
                return false;
            }

            entry = new FirewallLogEntry
            {
                Timestamp = timestamp.LocalDateTime,
                Event = (EventLogEvent)eventId,
                ProcessId = processId,
                AppPath = NormalizePath(applicationPath),
                LocalIp = EmptyAddress(localAddress),
                LocalPort = localPort,
                RemoteIp = "::",
                RemotePort = 0,
                Protocol = (Protocol)protocol,
            };
            return true;
        }

        private static string NormalizePath(string applicationPath)
        {
            string path = PathMapper.Instance.ConvertPathIgnoreErrors(applicationPath, PathFormat.Win32);
            return Utils.GetExactPath(path) ?? path;
        }

        private static string EmptyAddress(string value) =>
            string.IsNullOrEmpty(value) ? "::" : value;

        private static bool TryGet(
            IReadOnlyDictionary<string, string> fields,
            string name,
            out string value)
        {
            if (fields.TryGetValue(name, out string? raw) && !string.IsNullOrWhiteSpace(raw))
            {
                value = raw.Trim();
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static bool TryUInt32(
            IReadOnlyDictionary<string, string> fields,
            string name,
            out uint value)
        {
            value = 0;
            return TryGet(fields, name, out string text) &&
                uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryPort(
            IReadOnlyDictionary<string, string> fields,
            string name,
            out int port)
        {
            port = 0;
            return TryGet(fields, name, out string text) &&
                int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                port >= 0 &&
                port <= 65535;
        }

        private static AuditPolicyLease AcquireAuditLease(AuditPolicyFlags requiredFlags)
        {
            AuditPolicyLease? lease = null;
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
                return lease ?? throw new InvalidOperationException("Audit policy lease acquisition did not complete.");
            }
            catch
            {
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

        private static void DisposeAuditLease(ref AuditPolicyLease? lease)
        {
            AuditPolicyLease? captured = lease;
            lease = null;
            if (captured == null)
                return;

            Privilege.RunWithPrivilege(Privilege.Security, true, delegate (object? state)
            {
                captured.Dispose();
            }, null);
        }
    }
}

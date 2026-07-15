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

        private readonly EventLogWatcher _logWatcher;
        private AuditPolicyLease? _failureAuditLease;
        private AuditPolicyLease? _learningAuditLease;
        private bool _learningEnabled;

        internal delegate void NewLogEntryDelegate(FirewallLogWatcher sender, FirewallLogEntry entry);
        internal event NewLogEntryDelegate? NewLogEntry;

        internal delegate void BlockedConnectionDelegate(
            FirewallLogWatcher sender,
            BlockedConnectionAuditEvent blockedConnection);
        internal event BlockedConnectionDelegate? BlockedConnection;

        internal FirewallLogWatcher()
        {
            var query = new EventLogQuery(
                "Security",
                PathType.LogName,
                "*[System[(EventID=5154 or EventID=5155 or EventID=5156 or EventID=5157 or EventID=5158 or EventID=5159)]]");
            _logWatcher = new EventLogWatcher(query) { Enabled = false };
            _logWatcher.EventRecordWritten += LogWatcherEventRecordWritten;

            try
            {
                _logWatcher.Enabled = true;
                try
                {
                    _failureAuditLease = AcquireAuditLease(AuditPolicyFlags.Failure);
                }
                catch (Exception exception)
                {
                    Utils.Log("Cannot enable filtering-platform failure auditing; service attribution will be degraded.", Utils.LOG_ID_SERVICE);
                    Utils.LogException(exception, Utils.LOG_ID_SERVICE);
                }
            }
            catch (Exception exception)
            {
                _logWatcher.EventRecordWritten -= LogWatcherEventRecordWritten;
                _logWatcher.Dispose();
                throw new InvalidOperationException("Cannot start the Windows Security event watcher.", exception);
            }
        }

        internal bool LearningEnabled
        {
            get => _learningEnabled;
            set
            {
                if (value == _learningEnabled)
                    return;

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

        internal bool AuditEnrichmentAvailable => _failureAuditLease != null;

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            if (disposing)
            {
                try
                {
                    _logWatcher.Enabled = false;
                    _logWatcher.EventRecordWritten -= LogWatcherEventRecordWritten;
                    _logWatcher.Dispose();
                }
                catch (Exception exception)
                {
                    Utils.Log("Cannot stop the Windows Security event watcher cleanly.", Utils.LOG_ID_SERVICE);
                    Utils.LogException(exception, Utils.LOG_ID_SERVICE);
                }
            }

            try
            {
                _learningEnabled = false;
                DisposeAuditLease(ref _learningAuditLease);
                DisposeAuditLease(ref _failureAuditLease);
            }
            catch (Exception exception)
            {
                Utils.Log("Cannot restore the previous filtering-platform audit policy.", Utils.LOG_ID_SERVICE);
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }

            base.Dispose(disposing);
        }

        private void LogWatcherEventRecordWritten(object? sender, EventRecordWrittenEventArgs eventArgs)
        {
            EventRecord? record = eventArgs.EventRecord;
            try
            {
                if (eventArgs.EventException != null || record == null)
                    return;

                IReadOnlyDictionary<string, string> fields = ReadNamedFields(record);
                DateTimeOffset timestamp = record.TimeCreated.HasValue
                    ? new DateTimeOffset(record.TimeCreated.Value)
                    : DateTimeOffset.UtcNow;

                if (record.Id == 5157 &&
                    SecurityEvent5157Parser.TryParse(fields, timestamp.ToUniversalTime(), out BlockedConnectionAuditEvent parsed))
                {
                    BlockedConnectionAuditEvent normalized = Normalize(parsed);
                    BlockedConnection?.Invoke(this, normalized);
                    if (_learningEnabled)
                        NewLogEntry?.Invoke(this, ToFirewallLogEntry(normalized));
                    return;
                }

                if (_learningEnabled && TryParseLearningEntry(record.Id, fields, timestamp, out FirewallLogEntry entry))
                    NewLogEntry?.Invoke(this, entry);
            }
            catch (Exception exception)
            {
                Utils.LogException(exception, Utils.LOG_ID_SERVICE);
            }
            finally
            {
                record?.Dispose();
            }
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
                        requiredFlags);
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

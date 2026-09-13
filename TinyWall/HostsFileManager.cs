using System;
using System.IO;
using pylorak.Utilities;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    internal class HostsFileManager : Disposable
    {
        // Active system hosts file
        private readonly string HOSTS_PATH;
        // Local copy of active hosts file
        private readonly string HOSTS_BACKUP;
        // User's original hosts file
        private readonly string HOSTS_ORIGINAL;
        private readonly Func<bool> flushDns;
        internal Action<RuntimeEvent, RuntimeResult, int>? DiagnosticObserver { get; set; }
        internal Func<bool>? DiagnosticEnabled { get; set; }

        private void Report(RuntimeEvent eventCode, RuntimeResult result, int hresult = 0)
        {
            try { DiagnosticObserver?.Invoke(eventCode, result, hresult); } catch { }
        }

        private void Observe(RuntimeEvent eventCode, Action action)
        {
            Report(eventCode, RuntimeResult.attempt);
            try { action(); }
            catch (Exception error) { Report(eventCode, RuntimeResult.failure, error.HResult); throw; }
            Report(eventCode, RuntimeResult.success);
        }

        public HostsFileManager() : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts"),
            Utils.AppDataPath, Utils.TryFlushDnsCache) { }

        // Tests use isolated files and a non-mutating DNS callback. Production uses
        // the constructor above, including the guarded machine-data path.
        internal HostsFileManager(string hostsPath, string backupDirectory, Action flushDns)
            : this(hostsPath, backupDirectory, () => { flushDns(); return true; }) { }

        internal HostsFileManager(string hostsPath, string backupDirectory, Func<bool> flushDns)
        {
            HOSTS_PATH = hostsPath;
            HOSTS_BACKUP = Path.Combine(backupDirectory, "hosts.bck");
            HOSTS_ORIGINAL = Path.Combine(backupDirectory, "hosts.orig");
            this.flushDns = flushDns;
        }

        public readonly FileLocker FileLocker = new();

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            if (disposing)
            {
                FileLocker.Dispose();
            }

            base.Dispose(disposing);
        }


        private bool _EnableProtection;
        public bool EnableProtection
        {
            get => _EnableProtection;
            set
            {
                Observe(RuntimeEvent.hosts_protection, () =>
                {
                    if (ExistsChecked(HOSTS_BACKUP)) RequireLock(HOSTS_BACKUP);
                    if (HasOriginalBackup()) RequireLock(HOSTS_ORIGINAL);
                    if (value)
                        RequireLock(HOSTS_PATH);
                    else
                        FileLocker.Unlock(HOSTS_PATH);
                    _EnableProtection = value;
                });
                Report(RuntimeEvent.hosts_protection, value ? RuntimeResult.enabled : RuntimeResult.disabled);
            }
        }

        private void CreateOriginalBackup()
        {
            Observe(RuntimeEvent.hosts_backup, () =>
            {
                FileLocker.Unlock(HOSTS_ORIGINAL);
                WriteAndRelock(() => AtomicFileWriter.CopyFrom(HOSTS_ORIGINAL, HOSTS_PATH),
                    () => { if (HasOriginalBackup()) RequireLock(HOSTS_ORIGINAL); });
            });
        }

        public void UpdateHostsFile(Stream newHostsStream)
        {
            // We keep a copy of the hosts file for ourself, so that
            // we can re-install it any time without a net connection.
            // The new content arrives as a stream so it never sits in a
            // world-accessible temp folder before landing next to the target.
            Observe(RuntimeEvent.hosts_update, () =>
            {
                FileLocker.Unlock(HOSTS_BACKUP);
                WriteAndRelock(() => AtomicFileWriter.WriteFrom(HOSTS_BACKUP, newHostsStream),
                    () => RequireLock(HOSTS_BACKUP));
            });
        }

        public static string GetHostsHash()
        {
            string HOSTS_BACKUP = Path.Combine(Utils.AppDataPath, "hosts.bck");
            if (File.Exists(HOSTS_BACKUP))
                return Hasher.HashFile(HOSTS_BACKUP);
            else
                return string.Empty;
        }

        public bool EnableHostsFile()
        {
            return HostsRestorationPolicy.Enable(HasOriginalBackup, CreateOriginalBackup,
                () => Observe(RuntimeEvent.hosts_install, () => InstallHostsFile(HOSTS_BACKUP)), FlushDNSCache);
        }

        public bool DisableHostsFile()
        {
            bool result = true;
            Observe(RuntimeEvent.hosts_restore, () =>
            {
                result = HostsRestorationPolicy.Disable(() =>
                    {
                        bool present = HasOriginalBackup();
                        if (!present) Report(RuntimeEvent.hosts_restore, RuntimeResult.absent);
                        return present;
                    },
                    () => { InstallHostsFile(HOSTS_ORIGINAL); VerifyRestoration(); },
                    () =>
                    {
                        FileLocker.Unlock(HOSTS_ORIGINAL);
                        File.Delete(HOSTS_ORIGINAL);
                    }, FlushDNSCache);
            });
            return result;
        }

        // Opt-in, byte-bounded diagnostic readback. OS file I/O has no hard latency bound.
        // Verification never changes the existing restore/backup cleanup semantics.
        private void VerifyRestoration()
        {
            try
            {
                if (DiagnosticEnabled?.Invoke() != true) return;
                Report(RuntimeEvent.hosts_restore_verify, RuntimeResult.attempt);
                using var expected = File.OpenRead(HOSTS_ORIGINAL);
                using var actual = File.OpenRead(HOSTS_PATH);
                if (expected.Length > 1024 * 1024 || actual.Length > 1024 * 1024)
                {
                    Report(RuntimeEvent.hosts_restore_verify, RuntimeResult.skipped);
                    return;
                }
                bool equal = expected.Length == actual.Length;
                var left = new byte[4096];
                var right = new byte[4096];
                long remaining = expected.Length;
                while (equal && remaining > 0)
                {
                    int count = expected.Read(left, 0, (int)Math.Min(left.Length, remaining));
                    if (count == 0) { equal = false; break; }
                    int received = 0;
                    while (received < count)
                    {
                        int read = actual.Read(right, received, count - received);
                        if (read == 0) { equal = false; break; }
                        received += read;
                    }
                    for (int i = 0; equal && i < count; i++) equal = left[i] == right[i];
                    remaining -= count;
                }
                Report(RuntimeEvent.hosts_restore_verify, equal ? RuntimeResult.success : RuntimeResult.failure);
            }
            catch (Exception error) { Report(RuntimeEvent.hosts_restore_verify, RuntimeResult.failure, error.HResult); }
        }

        private bool HasOriginalBackup() => ExistsChecked(HOSTS_ORIGINAL);

        private static bool ExistsChecked(string path)
        {
            try
            {
                // File.Exists also returns false for access errors. Those must abort.
                File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException) { return false; }
        }

        private void FlushDNSCache()
        {
            try
            {
                // Flush DNS cache
                Report(RuntimeEvent.dns_flush, RuntimeResult.attempt);
                Report(RuntimeEvent.dns_flush, flushDns() ? RuntimeResult.success : RuntimeResult.failure);
            }
            catch (Exception error)
            {
                Report(RuntimeEvent.dns_flush, RuntimeResult.failure, error.HResult);
                // We just want to block exceptions.
            }
        }

        private void RequireLock(string path)
        {
            // FileLocker uses OpenOrCreate. Required files must already exist;
            // missing hosts and inspection failures must not become successful locks.
            File.GetAttributes(path);
            if (!FileLocker.IsLocked(path) && !FileLocker.Lock(path, FileAccess.Read, FileShare.Read))
                throw new IOException("Could not protect the hosts file: " + path);
        }

        private void InstallHostsFile(string sourcePath)
        {
            WriteAndRelock(() =>
            {
                FileLocker.Unlock(HOSTS_PATH);
                // Opening the source must throw if missing or unreadable.
                AtomicFileWriter.CopyFrom(HOSTS_PATH, sourcePath);
            }, () =>
            {
                if (_EnableProtection)
                    RequireLock(HOSTS_PATH);
                else
                    FileLocker.Unlock(HOSTS_PATH);
            });
        }

        private static void WriteAndRelock(Action write, Action relock)
        {
            try { write(); }
            catch (Exception primary)
            {
                try { relock(); }
                catch (Exception protectionError) { primary.Data["HostsProtectionFailure"] = protectionError; }
                throw;
            }
            relock();
        }

    }
}

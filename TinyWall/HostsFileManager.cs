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
                    if (!value)
                        FileLocker.Unlock(HOSTS_PATH);
                    else if (ExistsChecked(HOSTS_PATH))
                        RequireLock(HOSTS_PATH);
                    else
                        // Windows runs without a hosts file. Do not create one to lock it;
                        // a later hosts install relocks the file it writes.
                        Report(RuntimeEvent.hosts_protection, RuntimeResult.absent);
                    _EnableProtection = value;
                });
                Report(RuntimeEvent.hosts_protection, value ? RuntimeResult.enabled : RuntimeResult.disabled);
            }
        }

        // Service entry point. Hosts protection is best-effort: it must never gate WFP
        // enforcement, so failures are returned for logging and a controller warning.
        internal bool TryApplyProtection(bool value, out Exception? error)
        {
            error = null;
            try
            {
                EnableProtection = value;
            }
            catch (Exception failure)
            {
                error = failure;
                if (!value)
                {
                    // Turning protection off still releases the hosts file when a
                    // backup lock failed first.
                    FileLocker.Unlock(HOSTS_PATH);
                    _EnableProtection = false;
                }
            }
            return error == null;
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
            }, RelockHosts);
        }

        private void RelockHosts()
        {
            if (!_EnableProtection)
            {
                FileLocker.Unlock(HOSTS_PATH);
                return;
            }
            try { RequireLock(HOSTS_PATH); }
            catch (Exception error)
            {
                // The hosts content operation decides success. Losing the lock only
                // clears EnableProtection, which the service reports as a warning.
                _EnableProtection = false;
                Report(RuntimeEvent.hosts_protection, RuntimeResult.failure, error.HResult);
            }
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

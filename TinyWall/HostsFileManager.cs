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
        private readonly Action flushDns;

        public HostsFileManager() : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts"),
            Utils.AppDataPath, Utils.FlushDnsCache) { }

        // Tests use isolated files and a non-mutating DNS callback. Production uses
        // the constructor above, including the guarded machine-data path.
        internal HostsFileManager(string hostsPath, string backupDirectory, Action flushDns)
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
                if (ExistsChecked(HOSTS_BACKUP)) RequireLock(HOSTS_BACKUP);
                if (HasOriginalBackup()) RequireLock(HOSTS_ORIGINAL);
                if (value)
                    RequireLock(HOSTS_PATH);
                else
                    FileLocker.Unlock(HOSTS_PATH);
                _EnableProtection = value;
            }
        }

        private void CreateOriginalBackup()
        {
            FileLocker.Unlock(HOSTS_ORIGINAL);
            WriteAndRelock(() => AtomicFileWriter.CopyFrom(HOSTS_ORIGINAL, HOSTS_PATH),
                () => { if (HasOriginalBackup()) RequireLock(HOSTS_ORIGINAL); });
        }

        public void UpdateHostsFile(Stream newHostsStream)
        {
            // We keep a copy of the hosts file for ourself, so that
            // we can re-install it any time without a net connection.
            // The new content arrives as a stream so it never sits in a
            // world-accessible temp folder before landing next to the target.
            FileLocker.Unlock(HOSTS_BACKUP);
            WriteAndRelock(() => AtomicFileWriter.WriteFrom(HOSTS_BACKUP, newHostsStream),
                () => RequireLock(HOSTS_BACKUP));
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
                () => InstallHostsFile(HOSTS_BACKUP), FlushDNSCache);
        }

        public bool DisableHostsFile()
        {
            return HostsRestorationPolicy.Disable(HasOriginalBackup,
                () => InstallHostsFile(HOSTS_ORIGINAL),
                () =>
                {
                    FileLocker.Unlock(HOSTS_ORIGINAL);
                    File.Delete(HOSTS_ORIGINAL);
                }, FlushDNSCache);
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
                flushDns();
            }
            catch
            {
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

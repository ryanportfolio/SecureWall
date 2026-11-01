using System;
using System.IO;
using System.Security.AccessControl;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;
using pylorak.TinyWall.Prompting;
using pylorak.Utilities;
using pylorak.Windows.Services;

namespace pylorak.TinyWall.Installer
{
    // HKLM\SOFTWARE\SecureWall\UpgradeInProgress. Inherits the HKLM\SOFTWARE ACL, so
    // only administrators and SYSTEM can write it and every user can read it; the
    // value only delays service starts and grants nothing.
    internal static class UpgradeMarker
    {
        private const string InstallerMutex = @"Global\_MSIExecute";

        internal static string? Read()
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = machine.OpenSubKey(ProductRegistryPolicy.KeyPath);
                return key?.GetValue(UpgradeLifecyclePolicy.MarkerValue) as string;
            }
            catch
            {
                // Unreadable means unknown: the marker only defers starts, never permits them.
                return null;
            }
        }

        internal static void Write(string transaction)
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.CreateSubKey(ProductRegistryPolicy.KeyPath, true);
            key.SetValue(UpgradeLifecyclePolicy.MarkerValue, transaction, RegistryValueKind.String);
            key.Flush();
        }

        internal static void Clear(string transaction)
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(ProductRegistryPolicy.KeyPath, true);
            if (key == null || !UpgradeLifecyclePolicy.OwnsMarker(key.GetValue(UpgradeLifecyclePolicy.MarkerValue) as string, transaction)) return;
            key.DeleteValue(UpgradeLifecyclePolicy.MarkerValue, false);
            key.Flush();
        }

        // Windows Installer holds this mutex while it runs an installation. A caller
        // denied access to it still learns that it exists.
        internal static bool InstallerRunning()
        {
            try
            {
                using var mutex = Mutex.OpenExisting(InstallerMutex, MutexRights.Synchronize);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; }
        }

        internal static bool BlocksAutomaticStart()
        {
            try { return UpgradeLifecyclePolicy.BlocksStart(Read(), InstallerRunning()); }
            catch { return false; }
        }
    }

    internal sealed class ScmUpgradeService : IUpgradeService, IDisposable
    {
        private readonly ServiceController service = new(TinyWallService.SERVICE_NAME);
        private readonly Func<bool> registered;

        internal ScmUpgradeService(Func<bool> registered) => this.registered = registered;

        public bool Registered => registered();

        public LifecycleServiceState State
        {
            get
            {
                service.Refresh();
                return (LifecycleServiceState)service.Status;
            }
        }

        public void Stop()
        {
            try { service.Stop(); }
            // Stop() also throws when the service exits on its own first.
            catch (InvalidOperationException) when (State == LifecycleServiceState.Stopped || State == LifecycleServiceState.StopPending) { }
        }

        public void Start() => service.Start();

        public void SetRestartOnFailure(bool enabled)
        {
            using var scm = new ServiceControlManager();
            if (enabled)
                scm.SetRestartOnFailure(TinyWallService.SERVICE_NAME, ServiceLifecyclePolicy.RestartDelays, ServiceLifecyclePolicy.RestartResetPeriod);
            else
                scm.SetRestartOnFailure(TinyWallService.SERVICE_NAME, false);
        }

        public void Dispose() => service.Dispose();
    }

    // The upgrade snapshot lives in the guarded machine-data tree. Callers validate
    // the tree before and after; directories inherit its protected ACL, and every
    // snapshot file and a restored pwd get the secret-file DACL from creation on.
    internal sealed class MachineDataUpgradeFiles : IUpgradeFiles
    {
        public bool FileExists(string path) => Exists(path, false);
        public bool DirectoryExists(string path) => Exists(path, true);

        private static bool Exists(string path, bool directory)
        {
            try { return ((File.GetAttributes(path) & FileAttributes.Directory) != 0) == directory; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        public byte[] ReadFile(string path) => File.ReadAllBytes(path);

        public void WriteFile(string path, byte[] contents, bool secret)
        {
            if (!secret)
            {
                AtomicFileWriter.Write(path, stream => stream.Write(contents, 0, contents.Length));
                return;
            }
            // Same sequence as PasswordLock.SetPass: the target is protected before the
            // replacement and the temporary file is created protected.
            SecretFileProtection.Ensure(path);
            using var updater = new AtomicFileUpdater(path);
            using (var stream = SecretFileProtection.CreateNew(updater.TemporaryFilePath))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush(true);
            }
            updater.Commit();
        }

        public void DeleteFile(string path) => File.Delete(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void DeleteDirectory(string path) => Directory.Delete(path, true);
        public string[] Directories(string path) => Directory.GetDirectories(path);
    }
}

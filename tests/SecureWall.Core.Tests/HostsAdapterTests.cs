using System.Text;
using pylorak.TinyWall;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class HostsAdapterTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("hosts adapter refuses protection under exclusive writer with no original", ContendedHost),
        ("hosts adapter locks never-enabled hosts and permits disable no-op", NeverEnabled),
        ("hosts adapter rejects missing required hosts without creating it", MissingHost),
        ("hosts adapter propagates backup and original lock failures", ContendedBackups),
        ("hosts adapter propagates invalid optional backup paths", InvalidBackup),
        ("hosts adapter updates and relocks downloaded backup", UpdateSuccess),
        ("hosts adapter preserves primary update error and relock error", UpdateFailure),
        ("hosts adapter failed restore retains original and supports retry", RestoreFailure),
        ("hosts adapter preserves original through enable restore cycle", RestoreCycle),
        ("hosts adapter lock error enters configuration compensation", ConfigurationFailure),
        ("hosts adapter native constructor and checked lock wiring remain explicit", NativeWiring),
    };

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "SecureWall-r7-" + Guid.NewGuid().ToString("N"));
        internal string Hosts => Path.Combine(Root, "hosts");
        internal string Backup => Path.Combine(Root, "hosts.bck");
        internal string Original => Path.Combine(Root, "hosts.orig");
        internal readonly HostsFileManager Manager;
        internal int Flushes;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Hosts, "original");
            Manager = new HostsFileManager(Hosts, Root, () => Flushes++);
        }
        public void Dispose()
        {
            Manager.Dispose();
            Directory.Delete(Root, true);
        }
        internal static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static void ContendedHost()
    {
        using var f = new Fixture();
        using (Fixture.Hold(f.Hosts))
        {
            AssertEx.Throws<IOException>(() => { f.Manager.EnableProtection = true; f.Manager.DisableHostsFile(); });
            AssertEx.False(f.Manager.EnableProtection);
            AssertEx.False(f.Manager.FileLocker.IsLocked(f.Hosts));
            AssertEx.False(File.Exists(f.Original));
        }
        f.Manager.EnableProtection = true;
        AssertEx.True(f.Manager.DisableHostsFile());
        AssertEx.True(f.Manager.FileLocker.IsLocked(f.Hosts));
    }

    private static void NeverEnabled()
    {
        using var f = new Fixture();
        f.Manager.EnableProtection = true;
        f.Manager.EnableProtection = true; // idempotent existing lock
        AssertEx.True(f.Manager.DisableHostsFile());
        AssertEx.True(f.Manager.EnableProtection && f.Manager.FileLocker.IsLocked(f.Hosts));
        AssertEx.Equal(0, f.Flushes);
        f.Manager.EnableProtection = false;
        AssertEx.False(f.Manager.FileLocker.IsLocked(f.Hosts));
    }

    private static void MissingHost()
    {
        using var f = new Fixture();
        File.Delete(f.Hosts);
        AssertEx.Throws<FileNotFoundException>(() => f.Manager.EnableProtection = true);
        AssertEx.False(f.Manager.EnableProtection);
        AssertEx.False(File.Exists(f.Hosts));
    }

    private static void ContendedBackups()
    {
        foreach (bool original in new[] { false, true })
        {
            using var f = new Fixture();
            string path = original ? f.Original : f.Backup;
            File.WriteAllText(path, "saved");
            using (Fixture.Hold(path))
            {
                AssertEx.Throws<IOException>(() => f.Manager.EnableProtection = true);
                AssertEx.False(f.Manager.EnableProtection);
            }
            AssertEx.Equal("saved", File.ReadAllText(path));
            f.Manager.EnableProtection = true;
            AssertEx.True(f.Manager.FileLocker.IsLocked(path));
        }
    }

    private static void InvalidBackup()
    {
        using var f = new Fixture();
        // A directory is present, not an absent optional file. No ACL changes needed.
        Directory.CreateDirectory(f.Original);
        AssertEx.Throws<IOException>(() => f.Manager.EnableProtection = true);
        AssertEx.False(f.Manager.EnableProtection);
    }

    private static void UpdateSuccess()
    {
        using var f = new Fixture();
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("blocklist"));
        f.Manager.UpdateHostsFile(source);
        AssertEx.True(f.Manager.FileLocker.IsLocked(f.Backup));
        AssertEx.Equal("blocklist", File.ReadAllText(f.Backup));
    }

    private sealed class FailingStream : MemoryStream
    {
        private readonly Action beforeFailure;
        internal readonly IOException Error = new("primary stream failure");
        internal FailingStream(Action beforeFailure) { this.beforeFailure = beforeFailure; }
        public override void CopyTo(Stream destination, int bufferSize)
        {
            beforeFailure();
            throw Error;
        }
    }

    private static void UpdateFailure()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Backup, "old blocklist");
        FileStream? holder = null;
        using var source = new FailingStream(() => holder = Fixture.Hold(f.Backup));
        try
        {
            IOException error = AssertEx.Throws<IOException>(() => f.Manager.UpdateHostsFile(source));
            AssertEx.True(ReferenceEquals(source.Error, error));
            AssertEx.True(error.Data["HostsProtectionFailure"] is IOException);
        }
        finally { holder?.Dispose(); }
        AssertEx.Equal("old blocklist", File.ReadAllText(f.Backup));
        using var retry = new MemoryStream(Encoding.UTF8.GetBytes("new blocklist"));
        f.Manager.UpdateHostsFile(retry);
        AssertEx.True(f.Manager.FileLocker.IsLocked(f.Backup));
    }

    private static void RestoreFailure()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Original, "saved original");
        using (Fixture.Hold(f.Hosts))
            AssertEx.Throws<IOException>(() => f.Manager.DisableHostsFile());
        AssertEx.Equal("saved original", File.ReadAllText(f.Original));
        AssertEx.Equal(0, f.Flushes);
        AssertEx.True(f.Manager.DisableHostsFile());
        AssertEx.Equal("saved original", File.ReadAllText(f.Hosts));
        AssertEx.False(File.Exists(f.Original));
    }

    private static void RestoreCycle()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Backup, "blocklist");
        f.Manager.EnableProtection = true;
        AssertEx.True(f.Manager.EnableHostsFile());
        AssertEx.Equal("original", File.ReadAllText(f.Original));
        AssertEx.True(f.Manager.FileLocker.IsLocked(f.Original));
        AssertEx.Equal("blocklist", File.ReadAllText(f.Hosts));
        AssertEx.True(f.Manager.DisableHostsFile());
        AssertEx.Equal("original", File.ReadAllText(f.Hosts));
        AssertEx.True(f.Manager.FileLocker.IsLocked(f.Hosts));
        AssertEx.False(File.Exists(f.Original));
    }

    private static void ConfigurationFailure()
    {
        using var f = new Fixture();
        bool restored = false, published = false, stopped = false;
        using (Fixture.Hold(f.Hosts))
            AssertEx.Throws<IOException>(() => PolicyChangeTransaction.Apply(() => { },
                () => { f.Manager.EnableProtection = true; f.Manager.DisableHostsFile(); },
                () => { f.Manager.EnableProtection = false; f.Manager.DisableHostsFile(); restored = true; },
                () => published = true, () => stopped = true));
        AssertEx.True(restored && !published && !stopped);
    }

    private static void NativeWiring()
    {
        string source = PromptTransactionIntegrationTests.Source("TinyWall/HostsFileManager.cs");
        AssertEx.True(source.Contains("Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @\"drivers\\etc\\hosts\"),\n            Utils.AppDataPath, Utils.FlushDnsCache)"));
        AssertEx.Equal(1, source.Split("FileLocker.Lock(").Length - 1);
        AssertEx.True(source.Contains("!FileLocker.IsLocked(path) && !FileLocker.Lock(path, FileAccess.Read, FileShare.Read)"));
        AssertEx.True(source.Contains("catch (FileNotFoundException) { return false; }"));
        AssertEx.False(source.Contains("catch (UnauthorizedAccessException)"));
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        AssertEx.True(service.Contains("HostsFileManager.EnableProtection = PolicyConfiguration.LockHostsFile;"));
    }
}

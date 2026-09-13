using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class HostsRestorationTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases
    {
        get
        {
            yield return ("hosts enable succeeds only after backup and install", EnableOrder);
            yield return ("hosts install failures propagate with original retained", InstallFailure);
            yield return ("hosts original backup failure prevents installation", BackupFailure);
            yield return ("hosts absent original is a successful disable no-op", AbsentOriginal);
            yield return ("hosts restoration failure retains backup and aborts cleanup", RestoreFailure);
            yield return ("hosts restoration precedes backup deletion", RestoreOrder);
            yield return ("hosts backup inspection failures abort", InspectionFailure);
            yield return ("hosts backup deletion failure propagates", DeleteFailure);
            yield return ("hosts failure rolls back the service policy without publishing", PolicyRollback);
            yield return ("hosts recovery failure withdraws runtime permissions", PolicyRecoveryFailure);
        }
    }

    private static void EnableOrder()
    {
        var calls = new List<string>();
        AssertEx.True(HostsRestorationPolicy.Enable(() => false, () => calls.Add("backup"),
            () => calls.Add("install"), () => calls.Add("flush")));
        AssertEx.SequenceEqual(new[] { "backup", "install", "flush" }, calls);
        calls.Clear();
        AssertEx.True(HostsRestorationPolicy.Enable(() => true, () => calls.Add("backup"),
            () => calls.Add("install"), () => calls.Add("flush")));
        AssertEx.SequenceEqual(new[] { "install", "flush" }, calls);
    }

    private static void InstallFailure()
    {
        bool original = false;
        AssertEx.Throws<IOException>(() => HostsRestorationPolicy.Enable(() => original,
            () => original = true, () => throw new IOException("install"),
            () => throw new InvalidOperationException("must not flush")));
        AssertEx.True(original);
    }

    private static void BackupFailure()
    {
        bool installed = false;
        AssertEx.Throws<IOException>(() => HostsRestorationPolicy.Enable(() => false,
            () => throw new IOException("backup"), () => installed = true, () => { }));
        AssertEx.False(installed);
    }

    private static void AbsentOriginal()
    {
        void Unexpected() => throw new InvalidOperationException("no I/O expected");
        AssertEx.True(HostsRestorationPolicy.Disable(() => false, Unexpected, Unexpected, Unexpected));
    }

    private static void RestoreFailure()
    {
        bool original = true;
        bool cleanupReached = false;
        AssertEx.Throws<IOException>(() => {
            HostsRestorationPolicy.Disable(() => original, () => throw new IOException("restore or post-swap failure"),
                () => original = false, () => { });
            cleanupReached = true;
        });
        AssertEx.True(original);
        AssertEx.False(cleanupReached);
    }

    private static void RestoreOrder()
    {
        var calls = new List<string>();
        AssertEx.True(HostsRestorationPolicy.Disable(() => true, () => calls.Add("restore"),
            () => calls.Add("delete"), () => calls.Add("flush")));
        AssertEx.SequenceEqual(new[] { "restore", "delete", "flush" }, calls);
    }

    private static void InspectionFailure()
    {
        void Unexpected() => throw new InvalidOperationException("must not perform I/O");
        bool Unreadable() => throw new UnauthorizedAccessException("inspection");
        AssertEx.Throws<UnauthorizedAccessException>(() => HostsRestorationPolicy.Disable(Unreadable, Unexpected, Unexpected, Unexpected));
        AssertEx.Throws<UnauthorizedAccessException>(() => HostsRestorationPolicy.Enable(Unreadable, Unexpected, Unexpected, Unexpected));
    }

    private static void DeleteFailure()
    {
        bool restored = false;
        AssertEx.Throws<IOException>(() => HostsRestorationPolicy.Disable(() => true,
            () => restored = true, () => throw new IOException("delete"), () => { }));
        AssertEx.True(restored);
    }

    private static void PolicyRollback()
    {
        var calls = new List<string>();
        AssertEx.Throws<IOException>(() => PolicyChangeTransaction.Apply(
            () => calls.Add("persist"),
            () => HostsRestorationPolicy.Enable(() => true, () => { },
                () => throw new IOException("hosts install"), () => { }),
            () => calls.Add("restore"), () => calls.Add("publish"), () => calls.Add("failClosed")));
        AssertEx.SequenceEqual(new[] { "persist", "restore" }, calls);
    }

    private static void PolicyRecoveryFailure()
    {
        bool published = false;
        bool closed = false;
        void RestoreHosts() => HostsRestorationPolicy.Disable(() => true,
            () => throw new IOException("hosts restore"), () => { }, () => { });
        AssertEx.Throws<AggregateException>(() => PolicyChangeTransaction.Apply(
            () => { }, RestoreHosts, RestoreHosts, () => published = true, () => closed = true));
        AssertEx.True(closed);
        AssertEx.False(published);
    }
}

using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class InstallerDiagnosticsTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("log rotation keeps one previous file past the limit", LogRotationKeepsPrevious),
        ("maintenance failure reports are one bounded line", FailureReportsAreOneLine),
        ("emergency release reports name the step and no log path", EmergencyReportsNameSteps),
        ("shipped data seeds absent files and replaces stale content", ShippedDataSeedsAndReplaces),
        ("shipped data leaves identical files and user state untouched", ShippedDataPreservesCurrent),
        ("shipped data failures leave the target unchanged", ShippedDataFailuresLeaveTarget),
        ("guard and maintenance failures reach the event log without machine data", EventLogWiring),
    };

    private static string Fixture(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "SecureWall-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    private static void LogRotationKeepsPrevious()
    {
        string dir = Fixture("log-rotation");
        try
        {
            string log = Path.Combine(dir, "service.log");
            string previous = Path.Combine(dir, "service.old.log");
            AssertEx.Equal(previous, LogRotationPolicy.PreviousPath(log));
            AssertEx.Equal(512L * 1024, LogRotationPolicy.Limit);
            AssertEx.False(LogRotationPolicy.RotateIfNeeded(log, 100), "absent log rotated");

            File.WriteAllText(log, new string('a', 100));
            AssertEx.False(LogRotationPolicy.RotateIfNeeded(log, 100), "log at the limit rotated");
            AssertEx.Equal(100L, new FileInfo(log).Length);

            File.WriteAllText(log, "first error" + new string('a', 200));
            AssertEx.True(LogRotationPolicy.RotateIfNeeded(log, 100));
            AssertEx.False(File.Exists(log), "rotated log still present");
            AssertEx.True(File.ReadAllText(previous).StartsWith("first error", StringComparison.Ordinal),
                "first error was not preserved");

            File.WriteAllText(log, "second" + new string('b', 200));
            AssertEx.True(LogRotationPolicy.RotateIfNeeded(log, 100));
            AssertEx.True(File.ReadAllText(previous).StartsWith("second", StringComparison.Ordinal),
                "previous file was not replaced by the newer full log");
            AssertEx.Equal(1, Directory.GetFiles(dir).Length);
        }
        finally { Cleanup(dir); }
    }

    private static void FailureReportsAreOneLine()
    {
        AssertEx.Equal("/msi-cleanup", MaintenanceFailureReport.Mode(new[] { "/msi-cleanup" }));
        AssertEx.Equal("/install", MaintenanceFailureReport.Mode(new[] { "/foo", "/install" }));
        AssertEx.Equal("/service", MaintenanceFailureReport.Mode(new[] { "/service" }));
        AssertEx.Equal("controller", MaintenanceFailureReport.Mode(new[] { "/autowhitelist" }));

        var error = new UnauthorizedAccessException("Unsafe machine data path: C:\\ProgramData\\SecureWall\\x\r\nsecond line",
            new InvalidOperationException("Machine data grants untrusted mutation rights.\n\tat frame"));
        string guard = MaintenanceFailureReport.GuardFailure("/install", @"C:\ProgramData\SecureWall", error);
        AssertEx.True(guard.StartsWith("SecureWall /install failed: ", StringComparison.Ordinal), guard);
        AssertEx.True(guard.Contains(@"C:\ProgramData\SecureWall did not validate"), guard);
        AssertEx.True(guard.Contains("UnauthorizedAccessException: Unsafe machine data path: C:\\ProgramData\\SecureWall\\x second line"), guard);
        AssertEx.True(guard.Contains("InvalidOperationException: Machine data grants untrusted mutation rights. at frame"), guard);
        AssertEx.False(guard.Any(char.IsControl), "report contains control characters");

        string exit = MaintenanceFailureReport.ExitFailure("/msi-cleanup", -1, @"C:\ProgramData\SecureWall\logs\installer.log");
        AssertEx.Equal(@"SecureWall /msi-cleanup failed with exit code -1. Details: C:\ProgramData\SecureWall\logs\installer.log", exit);
        AssertEx.Equal("SecureWall /install failed: IOException: disk", MaintenanceFailureReport.Unhandled("/install", new IOException("disk")));

        string bounded = MaintenanceFailureReport.OneLine(new string('x', 10000) + "\n" + new string('y', 10));
        AssertEx.Equal(MaintenanceFailureReport.MaxLength, bounded.Length);
        AssertEx.Equal("a b", MaintenanceFailureReport.OneLine("\r\n a \r\n\t b \r\n"));
    }

    private static void EmergencyReportsNameSteps()
    {
        var error = new InvalidOperationException("Windows Firewall rules could not be restored.\r\n\tat frame",
            new System.Runtime.InteropServices.COMException("The service has not been started."));
        string step = MaintenanceFailureReport.EmergencyStepFailure("/msi-cleanup", "Windows Firewall compatibility restore", error);
        AssertEx.Equal("SecureWall /msi-cleanup emergency release step failed: Windows Firewall compatibility restore. " +
            "InvalidOperationException: Windows Firewall rules could not be restored. at frame " +
            "(COMException: The service has not been started.)", step);

        string summary = MaintenanceFailureReport.EmergencyExitFailure("/msi-rollback-install", -1,
            new[] { "audit policy restore", "WFP object removal" });
        AssertEx.Equal("SecureWall /msi-rollback-install emergency release failed with exit code -1. " +
            "Failed steps: audit policy restore, WFP object removal. The preceding SecureWall events give each exception. " +
            "Emergency release reads no file contents from the rejected data directory and does not repair, move or delete anything in it.", summary);
        AssertEx.False(summary.Contains("not read") || summary.Contains("not changed") || summary.Contains("or changed"),
            "summary claims more than the release guarantees: " + summary);
        AssertEx.True(MaintenanceFailureReport.EmergencyExitFailure("/msi-cleanup", -1, Array.Empty<string>())
            .Contains("No step reported an exception."), "empty step list not stated");

        string longStep = MaintenanceFailureReport.EmergencyStepFailure("/msi-cleanup", "WFP object removal",
            new InvalidOperationException(new string('x', 10000)));
        AssertEx.Equal(MaintenanceFailureReport.MaxLength, longStep.Length);
        // One event per failed step: a nested catch or a follow-on deadline
        // error in the same step adds no second line; the first cause wins.
        var log = new EmergencyFailureLog("/msi-cleanup");
        string? first = log.Record("service stop", new InvalidOperationException("Cannot stop SecureWall service."));
        AssertEx.True(first != null && first.Contains("step failed: service stop. InvalidOperationException: Cannot stop SecureWall service."), first ?? "null");
        AssertEx.True(log.Record("service stop", new InvalidOperationException("Service did not stop within the maintenance deadline.")) == null,
            "second failure of one step produced another event");
        AssertEx.True(log.Record("WFP object removal", new IOException("filter")) != null, "a different step was suppressed");
        AssertEx.True(log.Record("WFP object removal", new IOException("filter again")) == null, "repeat step failure produced another event");
        AssertEx.True(log.FailedSteps.SequenceEqual(new[] { "service stop", "WFP object removal" }), string.Join("|", log.FailedSteps));
        string logged = log.Summary(-1);
        AssertEx.True(logged.StartsWith("SecureWall /msi-cleanup emergency release failed with exit code -1. Failed steps: service stop, WFP object removal. ", StringComparison.Ordinal), logged);
        AssertEx.Equal(1, System.Text.RegularExpressions.Regex.Matches(logged, "service stop").Count);

        foreach (string line in new[] { step, summary, longStep, first!, logged })
        {
            AssertEx.False(line.Any(char.IsControl), "report contains control characters: " + line);
            AssertEx.False(line.IndexOf("installer.log", StringComparison.OrdinalIgnoreCase) >= 0, "report points to installer.log: " + line);
            AssertEx.False(line.IndexOf("ProgramData", StringComparison.OrdinalIgnoreCase) >= 0, "report names the data directory: " + line);
        }
    }

    private static void ShippedDataSeedsAndReplaces()
    {
        string source = Fixture("shipped-source");
        string target = Fixture("shipped-target");
        try
        {
            AssertEx.SequenceEqual(new[] { "profiles.json", "hosts.bck" }, ShippedDataPolicy.Names);
            File.WriteAllText(Path.Combine(source, "profiles.json"), "profiles v2");
            File.WriteAllText(Path.Combine(source, "hosts.bck"), "hosts v2");

            AssertEx.Equal(ShippedDataResult.Seeded, ShippedDataPolicy.Refresh(source, target, "profiles.json"));
            AssertEx.Equal("profiles v2", File.ReadAllText(Path.Combine(target, "profiles.json")));

            string hosts = Path.Combine(target, "hosts.bck");
            File.WriteAllText(hosts, "hosts v1 stale and longer");
            File.SetAttributes(hosts, FileAttributes.ReadOnly);
            AssertEx.Equal(ShippedDataResult.Replaced, ShippedDataPolicy.Refresh(source, target, "hosts.bck"));
            AssertEx.Equal("hosts v2", File.ReadAllText(hosts));
            AssertEx.True((File.GetAttributes(hosts) & FileAttributes.ReadOnly) != 0, "read-only attribute lost");

            File.SetAttributes(hosts, FileAttributes.Normal);
            File.WriteAllText(hosts, "hosts v3");
            AssertEx.Equal(ShippedDataResult.Replaced, ShippedDataPolicy.Refresh(source, target, "hosts.bck"));
            AssertEx.Equal("hosts v2", File.ReadAllText(hosts));
            // No temporary or backup files remain after success.
            AssertEx.Equal(2, Directory.GetFileSystemEntries(target).Length);
        }
        finally { Cleanup(source); Cleanup(target); }
    }

    private static void ShippedDataPreservesCurrent()
    {
        string source = Fixture("shipped-source");
        string target = Fixture("shipped-target");
        try
        {
            File.WriteAllText(Path.Combine(source, "profiles.json"), "profiles v2");
            string profiles = Path.Combine(target, "profiles.json");
            File.WriteAllText(profiles, "profiles v2");
            var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(profiles, stamp);
            string[] user = { "config", "pwd", "hosts.orig", "firewall-recovery.json" };
            foreach (string name in user) File.WriteAllText(Path.Combine(target, name), "user " + name);

            AssertEx.Equal(ShippedDataResult.Current, ShippedDataPolicy.Refresh(source, target, "profiles.json"));
            AssertEx.Equal(stamp, File.GetLastWriteTimeUtc(profiles), "identical file was rewritten");
            foreach (string name in user)
                AssertEx.Equal("user " + name, File.ReadAllText(Path.Combine(target, name)));
            AssertEx.Equal(user.Length + 1, Directory.GetFileSystemEntries(target).Length);
        }
        finally { Cleanup(source); Cleanup(target); }
    }

    private static void ShippedDataFailuresLeaveTarget()
    {
        string source = Fixture("shipped-source");
        string target = Fixture("shipped-target");
        try
        {
            string profiles = Path.Combine(target, "profiles.json");
            File.WriteAllText(profiles, "profiles v1");
            AssertEx.Throws<FileNotFoundException>(() => ShippedDataPolicy.Refresh(source, target, "profiles.json"));
            AssertEx.Equal("profiles v1", File.ReadAllText(profiles));

            File.WriteAllText(Path.Combine(source, "hosts.bck"), "hosts v2");
            string hostsDirectory = Path.Combine(target, "hosts.bck");
            Directory.CreateDirectory(hostsDirectory);
            AssertEx.Throws<UnauthorizedAccessException>(() => ShippedDataPolicy.Refresh(source, target, "hosts.bck"));
            AssertEx.True(Directory.Exists(hostsDirectory), "unexpected directory was replaced");
            AssertEx.Equal(2, Directory.GetFileSystemEntries(target).Length);
        }
        finally { Cleanup(source); Cleanup(target); }
    }

    private static void EventLogWiring()
    {
        DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "TinyWall", "Program.cs"))) root = root.Parent;
        AssertEx.True(root != null, "repository root not found");
        string Read(string file) => File.ReadAllText(Path.Combine(root!.FullName, file));

        string program = Read(Path.Combine("TinyWall", "Program.cs"));
        int guard = program.IndexOf("Installer.MachineDataGuard.InstallDefaults();", StringComparison.Ordinal);
        int catchStart = program.IndexOf("catch (Exception exception)", guard, StringComparison.Ordinal);
        int catchEnd = program.IndexOf("return -1;", catchStart, StringComparison.Ordinal);
        AssertEx.True(guard > 0 && catchStart > guard && catchEnd > catchStart, "guard catch not found");
        string guardCatch = program.Substring(catchStart, catchEnd - catchStart);
        AssertEx.True(guardCatch.Contains("Console.Error.WriteLine(diagnostic);"), "stderr diagnostic removed");
        AssertEx.True(guardCatch.Contains("MaintenanceEventLog.ReportError(Installer.MaintenanceEventLog.GuardFailureId"), "guard failure not reported");
        AssertEx.False(guardCatch.Contains("Utils.Log") || guardCatch.Contains("AppDataPath"), "guard failure uses machine-data logging");
        foreach (string mode in new[] { "/install", "/uninstall", "/msi-cleanup", "/msi-rollback-install" })
            AssertEx.True(program.Contains("RunMaintenance(\"" + mode + "\""), "maintenance mode not reported: " + mode);

        string writer = Read(Path.Combine("TinyWall", "Installer", "MaintenanceEventLog.cs"));
        AssertEx.True(writer.Contains("RegisterEventSourceW(null, SecureWallProduct.Name)") && writer.Contains("ReportEventW("));
        foreach (string forbidden in new[] { "MachineDataGuard", "AppDataPath", "Utils.", "File.", "Directory.", "EventLog.CreateEventSource" })
            AssertEx.False(writer.Contains(forbidden), "event log writer depends on " + forbidden);

        string utils = Read(Path.Combine("TinyWall", "Utils.cs"));
        AssertEx.True(utils.Contains("LogRotationPolicy.RotateIfNeeded(logfile)"), "log writer does not rotate");
        AssertEx.False(utils.Contains("FileMode.Truncate"), "log writer still truncates");

        string guardSource = Read(Path.Combine("TinyWall", "Installer", "MachineDataGuard.cs"));
        int install = guardSource.IndexOf("internal static void InstallDefaults()", StringComparison.Ordinal);
        string defaults = guardSource.Substring(install);
        int first = defaults.IndexOf("Require(true, true);", StringComparison.Ordinal);
        int refresh = defaults.IndexOf("ShippedDataPolicy.Refresh(source, PathName, name);", StringComparison.Ordinal);
        int last = defaults.IndexOf("Require(false, true);", StringComparison.Ordinal);
        AssertEx.True(first >= 0 && first < refresh && refresh < last, "refresh is not bracketed by full-tree validation");

        string product = Read(Path.Combine("MsiSetup", "Product.wxs"));
        AssertEx.True(product.Contains("<util:EventSource Name=\"SecureWall\" Log=\"Application\""), "MSI does not register the event source");
        AssertEx.True(product.Contains("<ComponentRef Id='EventLogSource' />"), "event source component not in feature");
    }
}

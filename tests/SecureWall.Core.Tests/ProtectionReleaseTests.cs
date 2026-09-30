using System.Text.RegularExpressions;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class ProtectionReleaseTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("ordinary removal restores state before releasing baseline and registration", OrdinaryOrder),
        ("ordinary removal stops at the first restoration failure", OrdinaryStopsAtFirstFailure),
        ("ordinary baseline removal failure keeps service registration", OrdinaryWfpFailureKeepsRegistration),
        ("emergency release never reads hosts backup from rejected machine data", EmergencySkipsHosts),
        ("emergency release keeps baseline when compatibility rules remain", EmergencyCompatibilityFailureFailsClosed),
        ("failed-install rollback continues past hosts and audit failures", RollbackContinuesPastRestorationFailures),
        ("failed-install rollback keeps baseline while compatibility rules may remain", RollbackKeepsBaselineWithRules),
        ("failed-install rollback releases baseline once compatibility rules are confirmed gone", RollbackReleasesAfterConfirmedAbsence),
        ("failed-install rollback removes registration when baseline removal fails", RollbackWfpFailureStillRemovesRegistration),
        ("failed-install rollback with rejected machine data skips hosts and still cleans up", RollbackWithRejectedMachineData),
        ("controller termination failure stops ordinary removal but not rollback", ControllerTerminationFailure),
        ("baseline provider and emergency entry are wired for start-type independence", AdapterWiring),
        ("emergency release writes each failed step and a failed result to the event log", EmergencyEventWiring),
    };

    private sealed class Run
    {
        internal readonly List<string> Steps = new();
        internal readonly List<string> Warnings = new();
        internal int Failures;
        internal bool Result;
        internal string FailAt = "";
        internal bool RulesAbsent;
        internal bool ProbeThrows;
        internal bool RegistrationSucceeds = true;

        internal Run Execute(bool rollback, bool trusted)
        {
            Action Step(string name) => () =>
            {
                Steps.Add(name);
                if (FailAt.Split(',').Contains(name)) throw new InvalidOperationException(name + " failed");
            };
            Result = ProtectionReleasePolicy.Release(rollback, trusted,
                Step("hosts"), Step("compatibility"),
                () => { Steps.Add("probe"); if (ProbeThrows) throw new InvalidOperationException("probe failed"); return RulesAbsent; },
                Step("audit"), Step("controllers"), Step("wfp"),
                () => { Steps.Add("registration"); return RegistrationSucceeds; },
                Warnings.Add, _ => Failures++);
            return this;
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Protection release assertion failed.");
    }

    private static void OrdinaryOrder()
    {
        var run = new Run().Execute(false, true);
        Check(run.Result && run.Failures == 0 && run.Warnings.Count == 0);
        Check(run.Steps.SequenceEqual(new[] { "hosts", "compatibility", "audit", "controllers", "wfp", "registration" }));

        run = new Run { RegistrationSucceeds = false }.Execute(false, true);
        Check(!run.Result && run.Steps.Last() == "registration");
    }

    private static void OrdinaryStopsAtFirstFailure()
    {
        foreach (var (failAt, expected) in new[] {
            ("hosts", new[] { "hosts" }),
            ("compatibility", new[] { "hosts", "compatibility" }),
            ("audit", new[] { "hosts", "compatibility", "audit" }) })
        {
            var run = new Run { FailAt = failAt, RulesAbsent = true }.Execute(false, true);
            Check(!run.Result && run.Failures == 1);
            // No probe: ordinary removal never second-guesses a failed restore.
            Check(run.Steps.SequenceEqual(expected));
        }
    }

    private static void OrdinaryWfpFailureKeepsRegistration()
    {
        var run = new Run { FailAt = "wfp" }.Execute(false, true);
        Check(!run.Result && run.Failures == 1);
        Check(run.Steps.SequenceEqual(new[] { "hosts", "compatibility", "audit", "controllers", "wfp" }));
    }

    private static void EmergencySkipsHosts()
    {
        var run = new Run().Execute(false, false);
        Check(run.Result && run.Failures == 0);
        Check(run.Steps.SequenceEqual(new[] { "compatibility", "audit", "controllers", "wfp", "registration" }));
        Check(run.Warnings.SequenceEqual(new[] { ProtectionReleasePolicy.HostsSkipped }));
    }

    private static void EmergencyCompatibilityFailureFailsClosed()
    {
        foreach (string failAt in new[] { "compatibility", "audit", "wfp" })
        {
            var run = new Run { FailAt = failAt, RulesAbsent = true }.Execute(false, false);
            Check(!run.Result);
            Check(!run.Steps.Contains("registration") && !run.Steps.Contains("hosts") && !run.Steps.Contains("probe"));
            if (failAt != "wfp") Check(!run.Steps.Contains("wfp"));
        }
    }

    private static void RollbackContinuesPastRestorationFailures()
    {
        var run = new Run { FailAt = "hosts,audit" }.Execute(true, true);
        Check(!run.Result && run.Failures == 2);
        Check(run.Steps.SequenceEqual(new[] { "hosts", "compatibility", "audit", "controllers", "wfp", "registration" }));
        Check(run.Warnings.SequenceEqual(new[] { ProtectionReleasePolicy.HostsRetained, ProtectionReleasePolicy.AuditRetained }));

        run = new Run().Execute(true, true);
        Check(run.Result && run.Warnings.Count == 0 && !run.Steps.Contains("probe"));
    }

    private static void RollbackKeepsBaselineWithRules()
    {
        foreach (bool probeThrows in new[] { false, true })
        {
            var run = new Run { FailAt = "compatibility", RulesAbsent = false, ProbeThrows = probeThrows }.Execute(true, true);
            Check(!run.Result);
            Check(run.Steps.SequenceEqual(new[] { "hosts", "compatibility", "probe", "audit", "controllers", "registration" }));
            Check(run.Warnings.Contains(ProtectionReleasePolicy.BaselineRetained));
            Check(run.Failures == (probeThrows ? 2 : 1));
        }
    }

    private static void RollbackReleasesAfterConfirmedAbsence()
    {
        var run = new Run { FailAt = "compatibility", RulesAbsent = true }.Execute(true, true);
        Check(!run.Result);
        Check(run.Steps.SequenceEqual(new[] { "hosts", "compatibility", "probe", "audit", "controllers", "wfp", "registration" }));
        Check(!run.Warnings.Contains(ProtectionReleasePolicy.BaselineRetained));
    }

    private static void RollbackWfpFailureStillRemovesRegistration()
    {
        var run = new Run { FailAt = "wfp" }.Execute(true, true);
        Check(!run.Result && run.Steps.Last() == "registration");
        Check(run.Warnings.SequenceEqual(new[] { ProtectionReleasePolicy.BaselineRemovalFailed }));
    }

    private static void RollbackWithRejectedMachineData()
    {
        var run = new Run().Execute(true, false);
        Check(run.Result);
        Check(run.Steps.SequenceEqual(new[] { "compatibility", "audit", "controllers", "wfp", "registration" }));
        Check(run.Warnings.SequenceEqual(new[] { ProtectionReleasePolicy.HostsSkipped }));
    }

    private static void ControllerTerminationFailure()
    {
        foreach (bool trusted in new[] { true, false })
        {
            var ordinary = new Run { FailAt = "controllers" }.Execute(false, trusted);
            Check(!ordinary.Result && ordinary.Failures == 1);
            Check(!ordinary.Steps.Contains("wfp") && !ordinary.Steps.Contains("registration"));

            var rollback = new Run { FailAt = "controllers" }.Execute(true, trusted);
            Check(!rollback.Result && rollback.Failures == 1);
            Check(rollback.Steps.SkipWhile(step => step != "controllers").SequenceEqual(new[] { "controllers", "wfp", "registration" }));
        }
    }

    private static void AdapterWiring()
    {
        DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "TinyWall", "TinyWallDoctor.cs"))) root = root.Parent;
        Check(root != null);
        string Read(string file) => File.ReadAllText(Path.Combine(root!.FullName, "TinyWall", file));

        string service = Read("TinyWallService.cs");
        int begin = service.IndexOf("private void RegisterRestrictiveBaseline()", StringComparison.Ordinal);
        string baseline = service.Substring(begin, service.IndexOf("transaction.Commit();", begin, StringComparison.Ordinal) - begin);
        Check(baseline.Contains("FWPM_PROVIDER_FLAG_PERSISTENT") && baseline.Contains("SECUREWALL_PROVIDER_KEY"));
        Check(!Regex.IsMatch(baseline, @"serviceName\s*="));

        string program = Read("Program.cs");
        int guard = program.IndexOf("Installer.MachineDataGuard.Require();", StringComparison.Ordinal);
        int catchStart = program.IndexOf("catch (Exception exception)", guard, StringComparison.Ordinal);
        string rejected = program.Substring(catchStart, program.IndexOf("#endif", catchStart, StringComparison.Ordinal) - catchStart);
        Check(rejected.Contains("return TinyWallDoctor.ReleaseForMsiWithRejectedMachineData(false);"));
        Check(rejected.Contains("return TinyWallDoctor.ReleaseForMsiWithRejectedMachineData(true);"));
        Check(rejected.IndexOf("Console.Error.WriteLine(diagnostic);", StringComparison.Ordinal) <
            rejected.IndexOf("ReleaseForMsiWithRejectedMachineData", StringComparison.Ordinal));
        Check(!Regex.IsMatch(rejected, @"""/(uninstall|service)""\)\)\s*return TinyWallDoctor"));

        string doctor = Read("TinyWallDoctor.cs");
        Check(doctor.Contains("int result = CleanupForMsi(failedInstallRollback, false);"));
        int cleanup = doctor.IndexOf("private static int CleanupForMsi(bool failedInstallRollback, bool machineDataTrusted = true)", StringComparison.Ordinal);
        string msi = doctor.Substring(cleanup, doctor.IndexOf("private static void Warn(", cleanup, StringComparison.Ordinal) - cleanup);
        Check(msi.IndexOf("InstallationSafety.RequireSystemMaintenance();", StringComparison.Ordinal) <
            msi.IndexOf("ServiceExists()", StringComparison.Ordinal));
        Check(msi.Contains("if (machineDataTrusted) InstallationSafety.RequireProtectedMachineData();"));
        Check(msi.Contains("ValidateRegisteredServiceImage(!machineDataTrusted);"));
        Check(msi.Contains("if (failedInstallRollback || !machineDataTrusted)") && msi.Contains("ServiceStartMode.Disabled"));
        int stopped = doctor.IndexOf("private static int CleanupStoppedInstallation(", StringComparison.Ordinal);
        string teardown = doctor.Substring(stopped, doctor.IndexOf("private static void RestoreAuditPolicy()", stopped, StringComparison.Ordinal) - stopped);
        Check(teardown.Contains("if (machineDataTrusted) InstallationSafety.RequireProtectedMachineData();"));
        Check(teardown.Contains("ProtectionReleasePolicy.Release(failedInstallRollback, machineDataTrusted,"));
        Check(teardown.Contains("WindowsFirewall.OwnedRulesAbsent"));

        string firewall = Read("WindowsFirewall.cs");
        int probe = firewall.IndexOf("internal static bool OwnedRulesAbsent()", StringComparison.Ordinal);
        Check(probe > 0 && firewall.IndexOf("return false;", probe, StringComparison.Ordinal) > probe);
        Check(firewall.IndexOf("CanSkipStoppedServiceRecovery", probe, StringComparison.Ordinal) > probe);
    }

    // Utils.Log drops entries and MSI discards stderr while machine data is
    // rejected; removing these event writes leaves the failure reason nowhere.
    private static void EmergencyEventWiring()
    {
        DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "TinyWall", "TinyWallDoctor.cs"))) root = root.Parent;
        Check(root != null);
        string doctor = File.ReadAllText(Path.Combine(root!.FullName, "TinyWall", "TinyWallDoctor.cs"));
        string Body(string signature, string next)
        {
            int begin = doctor.IndexOf(signature, StringComparison.Ordinal);
            Check(begin > 0);
            return doctor.Substring(begin, doctor.IndexOf(next, begin + signature.Length, StringComparison.Ordinal) - begin);
        }

        string release = Body("internal static int ReleaseForMsiWithRejectedMachineData(bool failedInstallRollback)", "private static int CleanupForMsi(");
        Check(release.Contains("emergency = log;"));
        Check(release.IndexOf("emergency = log;", StringComparison.Ordinal) < release.IndexOf("CleanupForMsi(failedInstallRollback, false)", StringComparison.Ordinal));
        Check(Regex.IsMatch(release, @"if \(result != 0\)\s*MaintenanceEventLog\.ReportError\(MaintenanceEventLog\.MaintenanceFailureId, log\.Summary\(result\)\);"));
        Check(release.Contains("return result;") && release.Contains("finally { emergency = null; }"));

        // Every step event goes through EmergencyFailureLog.Record, which
        // returns null for a step that already reported; no direct writes.
        string logFailure = Body("private static void LogFailure(Exception exception)", "private static bool ServiceExists()");
        Check(Regex.IsMatch(logFailure, @"string\? line = emergency\?\.Record\(emergencyStep, exception\);\s*if \(line != null\)\s*MaintenanceEventLog\.ReportError\(MaintenanceEventLog\.MaintenanceFailureId, line\);"));
        Check(!logFailure.Contains("EmergencyStepFailure("));
        Check(Regex.Matches(doctor, @"EmergencyStepFailure\(|MaintenanceEventLog\.ReportError\(").Count == 2);

        // Stop() throwing while the service stops anyway is not a failure: only
        // a service that is not Stopped reaches LogFailure (and its Error event).
        string stop = Body("bool StopGracefully()", "int result = -1;");
        string stopCatch = stop.Substring(stop.IndexOf("catch (InvalidOperationException exception)", StringComparison.Ordinal));
        Check(Regex.IsMatch(stopCatch, @"try \{ stopped = State\(\) == LifecycleServiceState\.Stopped; \}\s*catch \{ LogFailure\(exception\); throw; \}\s*if \(stopped\) Utils\.LogException\(exception, Utils\.LOG_ID_INSTALLER\);\s*else LogFailure\(exception\);\s*return stopped;"));
        Check(Regex.Matches(stopCatch, @"LogFailure\(exception\)").Count == 2);

        foreach (string body in new[] { release, logFailure })
            Check(!body.Contains("installer.log") && !body.Contains("\".log\"") && !body.Contains("MaintenanceFailureReport.ExitFailure("));

        string teardown = Body("private static int CleanupStoppedInstallation(", "private static void RestoreAuditPolicy()");
        foreach (string step in new[] { "Windows Firewall compatibility restore", "audit policy restore", "controller termination", "WFP object removal" })
            Check(teardown.Contains("Staged(\"" + step + "\", "));
        Check(teardown.Contains("Stage(\"stopped-service check\");"));
        string registration = Body("private static bool RemoveRegistration()", "internal static bool EnsureHealth(");
        Check(registration.Contains("Stage(\"scheduled task removal\");") && registration.Contains("Stage(\"service registration removal\");"));
    }
}

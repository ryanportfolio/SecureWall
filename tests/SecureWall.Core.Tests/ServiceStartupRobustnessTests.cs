using pylorak.TinyWall.Prompting;
using pylorak.Windows.WFP;

namespace SecureWall.Core.Tests;

// SW-04, SW-05 and SW-22: post-commit health, hosts and WSL problems degrade with a
// log or controller warning instead of withdrawing committed WFP policy.
internal static class ServiceStartupRobustnessTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("service health warnings set and clear independently", HealthWarningFlags),
        ("service health notification shows each warning once until cleared", HealthNotificationGate),
        ("service health warnings fit one balloon", HealthWarningLength),
        ("post-ready health guard failure warns instead of throwing", PostReadyHealthGuardWiring),
        ("hosts protection result sets the controller warning", HostsWarningWiring),
        ("WSL adapter classifier accepts NAT aliases and reports unknown WSL names", WslAdapterClassifier),
        ("WSL permit failures are isolated per adapter", WslPermitFailuresAreIsolated),
        ("WSL block failures still abort replacement", WslBlockFailuresPropagate),
        ("WSL filters install only in the full reload by enumerated LUID", WslFullReloadOnly),
        ("interface enumeration reads aliases and LUIDs at the native row stride", InterfaceEnumerationLayout),
    };

    private static void HealthWarningFlags()
    {
        var w = ServiceHealthPolicy.Set(ServiceHealthWarning.None, ServiceHealthWarning.HostsProtection, true);
        w = ServiceHealthPolicy.Set(w, ServiceHealthWarning.InstallationGuard, true);
        AssertEx.Equal(ServiceHealthWarning.InstallationGuard | ServiceHealthWarning.HostsProtection, w);
        w = ServiceHealthPolicy.Set(w, ServiceHealthWarning.HostsProtection, false);
        AssertEx.Equal(ServiceHealthWarning.InstallationGuard, w);
    }

    private static void HealthNotificationGate()
    {
        var gate = new ServiceHealthNotificationGate();
        AssertEx.True(gate.Update(ServiceHealthWarning.None) == null);
        AssertEx.Equal(ServiceHealthText.HostsProtection, gate.Update(ServiceHealthWarning.HostsProtection));
        AssertEx.True(gate.Update(ServiceHealthWarning.HostsProtection) == null);
        string? both = gate.Update(ServiceHealthWarning.HostsProtection | ServiceHealthWarning.InstallationGuard);
        AssertEx.Equal(ServiceHealthText.InstallationGuard, both);
        AssertEx.True(gate.Update(ServiceHealthWarning.InstallationGuard) == null);
        AssertEx.Equal(ServiceHealthText.HostsProtection, gate.Update(ServiceHealthWarning.InstallationGuard | ServiceHealthWarning.HostsProtection));
        var fresh = new ServiceHealthNotificationGate();
        string? combined = fresh.Update(ServiceHealthWarning.InstallationGuard | ServiceHealthWarning.HostsProtection);
        AssertEx.True(combined != null && combined.Contains(ServiceHealthText.InstallationGuard) && combined.Contains(ServiceHealthText.HostsProtection));
    }

    private static void HealthWarningLength()
    {
        var gate = new ServiceHealthNotificationGate();
        string? combined = gate.Update(ServiceHealthWarning.InstallationGuard | ServiceHealthWarning.HostsProtection);
        AssertEx.True(combined != null && combined.Length <= 255);
    }

    private static void PostReadyHealthGuardWiring()
    {
        string doctor = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallDoctor.cs");
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int body = doctor.IndexOf("internal static bool EnsureHealth(string logContext, bool strictGuards)", StringComparison.Ordinal);
        AssertEx.True(body >= 0);
        string guards = doctor.Substring(body, doctor.IndexOf("// Ensure that TinyWall's dependencies", body, StringComparison.Ordinal) - body);
        AssertEx.True(guards.Contains("InstallationSafety.RequireNoTinyWall();"));
        AssertEx.True(guards.Contains("InstallationSafety.RequireProtectedInstallation();"));
        AssertEx.True(guards.Contains("catch (Exception e) when (!strictGuards)"));
        AssertEx.True(guards.Contains("return false;"));
        // Install/repair keeps the hard refusal; only the running service tolerates.
        AssertEx.True(doctor.Contains("EnsureHealth(logContext, strictGuards: true);"));
        AssertEx.Equal(1, service.Split("TinyWallDoctor.EnsureHealth(").Length - 1);
        AssertEx.True(service.Contains("if (!TinyWallDoctor.EnsureHealth(Utils.LOG_ID_SERVICE, strictGuards: false))\n                VisibleState.HealthWarnings = ServiceHealthPolicy.Set(VisibleState.HealthWarnings, ServiceHealthWarning.InstallationGuard, true);"));
        // The pre-start service refusal in Program.StartService is unchanged.
        string program = PromptTransactionIntegrationTests.Source("TinyWall/Program.cs");
        AssertEx.True(program.Contains("Installer.InstallationSafety.RequireNoTinyWall();"));
    }

    private static void HostsWarningWiring()
    {
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int start = service.IndexOf("private void ReapplySettings()", StringComparison.Ordinal);
        string reapply = service.Substring(start, service.IndexOf("private void ReportEffectiveDiagnosticState()", start, StringComparison.Ordinal) - start);
        AssertEx.True(reapply.IndexOf("TryApplyProtection", StringComparison.Ordinal) < reapply.IndexOf("EnableHostsFile()", StringComparison.Ordinal));
        AssertEx.True(reapply.Contains("ServiceHealthWarning.HostsProtection,\n                lockHosts && (!protectionApplied || !HostsFileManager.EnableProtection));"));
        string controller = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallController.cs");
        AssertEx.True(controller.Contains("HealthNotifications.Update(poll.State.HealthWarnings)"));
    }

    private static void WslAdapterClassifier()
    {
        AssertEx.Equal(Wsl2AdapterKind.Wsl, Wsl2AdapterPolicy.Classify("vEthernet (WSL)"));
        AssertEx.Equal(Wsl2AdapterKind.Wsl, Wsl2AdapterPolicy.Classify("vethernet (wsl)"));
        AssertEx.Equal(Wsl2AdapterKind.Wsl, Wsl2AdapterPolicy.Classify("vEthernet (WSL (Hyper-V firewall))"));
        AssertEx.Equal(Wsl2AdapterKind.UnrecognizedWsl, Wsl2AdapterPolicy.Classify("vEthernet (WSLCore)"));
        AssertEx.Equal(Wsl2AdapterKind.UnrecognizedWsl, Wsl2AdapterPolicy.Classify("vEthernet (WSL) 2"));
        AssertEx.Equal(Wsl2AdapterKind.Other, Wsl2AdapterPolicy.Classify("vEthernet (Default Switch)"));
        AssertEx.Equal(Wsl2AdapterKind.Other, Wsl2AdapterPolicy.Classify("Ethernet"));
        AssertEx.Equal(Wsl2AdapterKind.Other, Wsl2AdapterPolicy.Classify(null));
    }

    private static void WslPermitFailuresAreIsolated()
    {
        var installed = new List<string>();
        var reported = new List<string>();
        int count = Wsl2AdapterPolicy.Install(new[] { "a", "b", "c" }, permit: true,
            adapter => { if (adapter == "b") throw new InvalidOperationException("adapter vanished"); installed.Add(adapter); },
            (adapter, error) => reported.Add(adapter + ":" + error.Message));
        AssertEx.Equal(2, count);
        AssertEx.SequenceEqual(new[] { "a", "c" }, installed);
        AssertEx.SequenceEqual(new[] { "b:adapter vanished" }, reported);
    }

    private static void WslBlockFailuresPropagate()
    {
        var reported = new List<string>();
        AssertEx.Throws<InvalidOperationException>(() => Wsl2AdapterPolicy.Install(new[] { "a" }, permit: false,
            _ => throw new InvalidOperationException("registration failed"),
            (adapter, _) => reported.Add(adapter)));
        AssertEx.Equal(0, reported.Count);
    }

    private static void WslFullReloadOnly()
    {
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        AssertEx.Equal(1, service.Split("InstallWsl2Filters(EnforcementPolicy.OptionalPermitEnabled(").Length - 1);
        int rulesStart = service.IndexOf("private List<ulong> InstallRules(", StringComparison.Ordinal);
        string installRules = service.Substring(rulesStart, service.IndexOf("private void InstallFirewallRules()", rulesStart, StringComparison.Ordinal) - rulesStart);
        AssertEx.False(installRules.Contains("InstallWsl2Filters"));
        int coreStart = service.IndexOf("private void InstallFirewallRulesCore()", StringComparison.Ordinal);
        string core = service.Substring(coreStart, service.IndexOf("private void EnsureRestrictiveBaseline()", coreStart, StringComparison.Ordinal) - coreStart);
        int wsl = core.IndexOf("InstallWsl2Filters(", StringComparison.Ordinal);
        AssertEx.True(wsl > core.IndexOf("InstallRules(rules, rawSocketExceptions, false);", StringComparison.Ordinal));
        AssertEx.True(wsl < core.IndexOf("trx.Commit();", StringComparison.Ordinal));
        AssertEx.False(service.Contains("\"vEthernet (WSL)\""));
        AssertEx.True(service.Contains("LocalInterfaceCondition.EnumerateInterfaces()"));
        AssertEx.True(service.Contains("new LocalInterfaceCondition(interfaceLuid)"));
    }

    private static void InterfaceEnumerationLayout()
    {
        // Read-only IP Helper query. Every enumerated alias must resolve back through
        // ConvertInterfaceAliasToLuid, which catches a wrong row stride or alias offset.
        var interfaces = LocalInterfaceCondition.EnumerateInterfaces();
        AssertEx.True(interfaces.Count > 0);
        AssertEx.True(interfaces.Any(i => i.Alias.StartsWith("Loopback", StringComparison.OrdinalIgnoreCase)));
        foreach (var (alias, luid) in interfaces)
        {
            AssertEx.True(alias.Length > 0 && luid != 0, "Empty interface row.");
            AssertEx.True(LocalInterfaceCondition.InterfaceAliasExists(alias), "Alias does not resolve: " + alias);
        }
        AssertEx.Equal(interfaces.Count, interfaces.Select(i => i.Luid).Distinct().Count());
        // NDIS filter-module rows (for example "Ethernet-WFP Native MAC Layer LightWeight
        // Filter-0000") are skipped by the row flag byte, not by name.
        AssertEx.False(interfaces.Any(i => i.Alias.Contains("LightWeight Filter-", StringComparison.OrdinalIgnoreCase)));
    }
}

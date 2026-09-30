using pylorak.TinyWall;

namespace SecureWall.Core.Tests;

// ExecutableSubject.Equals accepts a ServiceSubject on the same path, while
// ServiceSubject.Equals rejects an ExecutableSubject. These cases run the real
// ServerProfileConfiguration.AddExceptions and Normalize with the real subjects, in both
// list orders, so a call site that went back to one-way Subject.Equals fails here: an
// executable-wide svchost rule and a per-service rule must never merge.
internal static class ProfileMergeTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("add exceptions keeps an executable-wide svchost block apart from a new service rule", AddServiceRuleBesideExecutableBlock),
        ("add exceptions keeps a service rule apart from a new executable-wide svchost block", AddExecutableBlockBesideServiceRule),
        ("normalize keeps executable-wide and service rules apart in either order", NormalizeKeepsExecutableAndServiceApart),
        ("add exceptions and normalize still merge rules for the same service", SameServiceRulesStillMerge),
    };

    private const string SvcHost = @"C:\Windows\System32\svchost.exe";

    private static FirewallExceptionV3 ExecutableBlock() =>
        new(new ExecutableSubject(SvcHost), new HardBlockPolicy());

    private static FirewallExceptionV3 ServiceAllow(string service = "Dnscache", string ports = "53") =>
        new(new ServiceSubject(SvcHost, service), new TcpUdpPolicy { AllowedRemoteUdpConnectPorts = ports });

    private static void AssertSeparate(List<FirewallExceptionV3> list)
    {
        AssertEx.Equal(2, list.Count, "Executable-wide and service rules were merged.");
        FirewallExceptionV3 executable = list.Single(e => e.Subject.GetType() == typeof(ExecutableSubject));
        FirewallExceptionV3 service = list.Single(e => e.Subject is ServiceSubject);
        AssertEx.True(executable.Policy is HardBlockPolicy, "The executable-wide svchost block was lost.");
        AssertEx.True(service.Policy is TcpUdpPolicy, "The service rule took another rule's policy.");
        AssertEx.Equal<string?>("53", ((TcpUdpPolicy)service.Policy).AllowedRemoteUdpConnectPorts);
    }

    // AddExceptions compares each old entry with each new one; an old executable rule
    // one-way-equals a new service rule.
    private static void AddServiceRuleBesideExecutableBlock()
    {
        var profile = new ServerProfileConfiguration("test");
        profile.AppExceptions.Add(ExecutableBlock());
        profile.AddExceptions(new List<FirewallExceptionV3> { ServiceAllow() });
        AssertSeparate(profile.AppExceptions);
    }

    // The reverse order catches a call site that compares new against old.
    private static void AddExecutableBlockBesideServiceRule()
    {
        var profile = new ServerProfileConfiguration("test");
        profile.AppExceptions.Add(ServiceAllow());
        profile.AddExceptions(new List<FirewallExceptionV3> { ExecutableBlock() });
        AssertSeparate(profile.AppExceptions);
    }

    private static void NormalizeKeepsExecutableAndServiceApart()
    {
        var executableFirst = new ServerProfileConfiguration("test");
        executableFirst.AppExceptions.Add(ExecutableBlock());
        executableFirst.AppExceptions.Add(ServiceAllow());
        executableFirst.Normalize();
        AssertSeparate(executableFirst.AppExceptions);

        var serviceFirst = new ServerProfileConfiguration("test");
        serviceFirst.AppExceptions.Add(ServiceAllow());
        serviceFirst.AppExceptions.Add(ExecutableBlock());
        serviceFirst.Normalize();
        AssertSeparate(serviceFirst.AppExceptions);

        // ServerConfiguration.Normalize runs the same check on every profile.
        var config = new ServerConfiguration();
        var profile = new ServerProfileConfiguration("test");
        profile.AppExceptions.Add(ExecutableBlock());
        profile.AppExceptions.Add(ServiceAllow());
        config.Profiles.Add(profile);
        config.Normalize();
        AssertSeparate(profile.AppExceptions);
    }

    // Guards the tests above against passing only because nothing merges at all.
    private static void SameServiceRulesStillMerge()
    {
        var profile = new ServerProfileConfiguration("test");
        profile.AppExceptions.Add(ServiceAllow("Dnscache", "53"));
        profile.AddExceptions(new List<FirewallExceptionV3> { ServiceAllow("dnscache", "853") });
        AssertEx.Equal(1, profile.AppExceptions.Count, "Rules for the same service no longer merge.");

        var normalized = new ServerProfileConfiguration("test");
        normalized.AppExceptions.Add(ServiceAllow("Dnscache", "53"));
        normalized.AppExceptions.Add(ServiceAllow("DNSCACHE", "853"));
        normalized.AppExceptions.Add(ServiceAllow("NlaSvc", "80"));
        normalized.Normalize();
        AssertEx.Equal(2, normalized.AppExceptions.Count, "Normalize no longer merges the same service, or merged different services.");
    }
}

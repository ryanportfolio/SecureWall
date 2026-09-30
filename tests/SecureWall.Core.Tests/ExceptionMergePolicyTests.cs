using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

// ExceptionSubject.cs pulls in UWP, JSON and certificate dependencies the core
// test project does not link. These doubles copy the Equals bodies of
// ExecutableSubject and ServiceSubject (TinyWall/ExceptionSubject.cs) exactly,
// including the one-way match of an executable subject against a service subject.
internal static class ExceptionMergePolicyTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases
    {
        get
        {
            yield return ("merge keeps an executable-wide rule apart from a service rule", ExecutableAndServiceNeverMerge);
            yield return ("merge still joins same-type subjects", SameTypeSubjectsStillMerge);
            yield return ("merge simulation keeps the executable-wide svchost block", MergeLoopKeepsExecutableWideBlock);
        }
    }

    private abstract class Subject : IEquatable<Subject>
    {
        public abstract bool Equals(Subject? other);
        public override bool Equals(object? obj) => obj is Subject other && Equals(other);
        public override int GetHashCode() => 0;
    }

    private class Executable : Subject
    {
        internal Executable(string path) => Path = path;
        internal string Path { get; }

        public override bool Equals(Subject? other) =>
            other is Executable o && string.Equals(Path, o.Path, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Service : Executable
    {
        internal Service(string path, string name) : base(path) => Name = name;
        internal string Name { get; }

        public override bool Equals(Subject? other) =>
            base.Equals(other) && other is Service o && string.Equals(Name, o.Name, StringComparison.OrdinalIgnoreCase);
    }

    private const string SvcHost = @"C:\Windows\System32\svchost.exe";

    private static void ExecutableAndServiceNeverMerge()
    {
        Subject executable = new Executable(SvcHost);
        Subject service = new Service(SvcHost, "Dnscache");

        AssertEx.True(executable.Equals(service), "The double must reproduce the one-way match.");
        AssertEx.False(ExceptionMergePolicy.SameSubject(executable, service));
        AssertEx.False(ExceptionMergePolicy.SameSubject(service, executable));
        AssertEx.False(ExceptionMergePolicy.SameSubject<Subject>(null, service));
        AssertEx.False(ExceptionMergePolicy.SameSubject<Subject>(executable, null));
    }

    private static void SameTypeSubjectsStillMerge()
    {
        AssertEx.True(ExceptionMergePolicy.SameSubject<Subject>(new Executable(SvcHost), new Executable(SvcHost.ToUpperInvariant())));
        AssertEx.True(ExceptionMergePolicy.SameSubject<Subject>(new Service(SvcHost, "Dnscache"), new Service(SvcHost, "dnscache")));
        AssertEx.False(ExceptionMergePolicy.SameSubject<Subject>(new Service(SvcHost, "Dnscache"), new Service(SvcHost, "NlaSvc")));
        AssertEx.False(ExceptionMergePolicy.SameSubject<Subject>(new Executable(SvcHost), new Executable(@"C:\Apps\a.exe")));
    }

    // Mirrors ApplicationExceptionSettings.AddExceptions: a merged old entry is removed.
    private static void MergeLoopKeepsExecutableWideBlock()
    {
        var block = (Subject: (Subject)new Executable(SvcHost), Policy: "HardBlock");
        var learned = (Subject: (Subject)new Service(SvcHost, "Dnscache"), Policy: "udp listener 5353");
        var profile = new List<(Subject Subject, string Policy)> { block };

        foreach (var old in profile.ToList())
        {
            if (ExceptionMergePolicy.SameSubject(old.Subject, learned.Subject))
                profile.Remove(old);
        }
        profile.Add(learned);

        AssertEx.Equal(2, profile.Count);
        AssertEx.True(profile.Contains(block), "The executable-wide block must survive.");
    }
}

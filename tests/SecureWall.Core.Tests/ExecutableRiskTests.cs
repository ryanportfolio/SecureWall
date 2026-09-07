using System;
using System.Collections.Generic;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests
{
    internal static class ExecutableRiskTests
    {
        private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

        internal static IEnumerable<(string Name, Action Test)> Cases
        {
            get
            {
                yield return ("executable risk raises nothing for a trusted old system-owned file", NoFlagsForTrustedOldSystemFile);
                yield return ("executable risk flags every non-trusted signature status as unsigned", NonTrustedSignatureIsUnsigned);
                yield return ("executable risk flags a user-writable location alone", UserWritableAlone);
                yield return ("executable risk flags a recent change alone", RecentlyModifiedAlone);
                yield return ("executable risk combines all three flags", AllThreeCombine);
                yield return ("executable risk window is exclusive at exactly 24 hours", WindowBoundaryIsExclusive);
                yield return ("executable risk treats a future timestamp as recent", FutureTimestampIsRecent);
                yield return ("executable risk ignores a missing timestamp", MissingTimestampIsNotRecent);
                yield return ("executable risk describes flags in a fixed order", DescribeIsOrderedAndComplete);
                yield return ("prompt allow policy refuses when the executable no longer exists", AllowPolicyRefusesMissingExecutable);
                yield return ("prompt allow policy skips the file check for packages", AllowPolicySkipsFileCheckForPackages);
                yield return ("prompt allow policy classifies which paths get the file check", AllowPolicyClassifiesCheckablePaths);
                yield return ("prompt allow policy allows the kernel pseudo-path without a file check", AllowPolicyAllowsSystemPseudoPath);
                yield return ("prompt allow policy allows an unmapped NT path without a file check", AllowPolicyAllowsNtFormPathWithoutCheck);
                yield return ("prompt allow policy still refuses a missing Win32 path", AllowPolicyStillRefusesMissingWin32Path);
            }
        }

        private static void NoFlagsForTrustedOldSystemFile()
        {
            var flags = ExecutableRiskAssessment.Assess(
                ExecutableSignatureStatus.Trusted,
                userCanWriteLocation: false,
                lastWriteUtc: Now.AddDays(-30),
                nowUtc: Now);
            AssertEx.Equal(ExecutableRiskFlags.None, flags);
            AssertEx.Equal(0, ExecutableRiskAssessment.Describe(flags).Count);
        }

        private static void NonTrustedSignatureIsUnsigned()
        {
            foreach (var status in new[]
            {
                ExecutableSignatureStatus.Missing,
                ExecutableSignatureStatus.Invalid,
                ExecutableSignatureStatus.Unknown,
            })
            {
                var flags = ExecutableRiskAssessment.Assess(status, false, Now.AddDays(-30), Now);
                AssertEx.Equal(ExecutableRiskFlags.Unsigned, flags, status.ToString());
            }
        }

        private static void UserWritableAlone()
        {
            var flags = ExecutableRiskAssessment.Assess(ExecutableSignatureStatus.Trusted, true, Now.AddDays(-30), Now);
            AssertEx.Equal(ExecutableRiskFlags.UserWritableLocation, flags);
        }

        private static void RecentlyModifiedAlone()
        {
            var flags = ExecutableRiskAssessment.Assess(ExecutableSignatureStatus.Trusted, false, Now.AddHours(-1), Now);
            AssertEx.Equal(ExecutableRiskFlags.RecentlyModified, flags);
        }

        private static void AllThreeCombine()
        {
            var flags = ExecutableRiskAssessment.Assess(ExecutableSignatureStatus.Missing, true, Now.AddMinutes(-5), Now);
            AssertEx.Equal(
                ExecutableRiskFlags.Unsigned | ExecutableRiskFlags.UserWritableLocation | ExecutableRiskFlags.RecentlyModified,
                flags);
        }

        private static void WindowBoundaryIsExclusive()
        {
            TimeSpan window = ExecutableRiskAssessment.RecentlyModifiedWindow;
            AssertEx.Equal(TimeSpan.FromHours(24), window);

            AssertEx.False(
                ExecutableRiskAssessment.IsRecent(Now - window, Now, window),
                "exactly 24 h old is not recent");
            AssertEx.True(
                ExecutableRiskAssessment.IsRecent(Now - window + TimeSpan.FromTicks(1), Now, window),
                "one tick younger than 24 h is recent");
            AssertEx.False(
                ExecutableRiskAssessment.IsRecent(Now - window - TimeSpan.FromTicks(1), Now, window),
                "one tick older than 24 h is not recent");

            var flags = ExecutableRiskAssessment.Assess(ExecutableSignatureStatus.Trusted, false, Now - window, Now);
            AssertEx.Equal(ExecutableRiskFlags.None, flags);
        }

        private static void FutureTimestampIsRecent()
        {
            var flags = ExecutableRiskAssessment.Assess(ExecutableSignatureStatus.Trusted, false, Now.AddDays(3), Now);
            AssertEx.Equal(ExecutableRiskFlags.RecentlyModified, flags);
        }

        private static void MissingTimestampIsNotRecent()
        {
            var flags = ExecutableRiskAssessment.Assess(ExecutableSignatureStatus.Trusted, false, null, Now);
            AssertEx.Equal(ExecutableRiskFlags.None, flags);
        }

        private static void DescribeIsOrderedAndComplete()
        {
            var lines = ExecutableRiskAssessment.Describe(
                ExecutableRiskFlags.RecentlyModified | ExecutableRiskFlags.Unsigned | ExecutableRiskFlags.UserWritableLocation);
            AssertEx.SequenceEqual(
                new[]
                {
                    ExecutableRiskAssessment.UnsignedText,
                    ExecutableRiskAssessment.UserWritableLocationText,
                    ExecutableRiskAssessment.RecentlyModifiedText,
                },
                lines);

            AssertEx.SequenceEqual(
                new[] { ExecutableRiskAssessment.RecentlyModifiedText },
                ExecutableRiskAssessment.Describe(ExecutableRiskFlags.RecentlyModified));
        }

        private static void AllowPolicyRefusesMissingExecutable()
        {
            var identity = PromptIdentity.ForExecutable(@"C:\apps\gone.exe");
            var asked = new List<string>();

            bool created = PromptAllowPolicy.TryCreate(
                identity,
                path => { asked.Add(path); return false; },
                out PromptAllowPolicy? policy,
                out string? reason);

            AssertEx.False(created);
            AssertEx.True(policy == null);
            AssertEx.SequenceEqual(new[] { @"C:\apps\gone.exe" }, asked);
            AssertEx.True(reason != null && reason.Contains("no longer exists"), reason);

            created = PromptAllowPolicy.TryCreate(identity, path => true, out policy, out reason);
            AssertEx.True(created);
            AssertEx.True(policy != null);
            AssertEx.True(reason == null);

            // The predicate-free overload keeps its old behaviour: no file check at all.
            AssertEx.True(PromptAllowPolicy.TryCreate(identity, out policy));
        }

        private static void AllowPolicySkipsFileCheckForPackages()
        {
            var identity = PromptIdentity.FromAttribution(
                @"C:\Program Files\WindowsApps\sample.exe",
                "S-1-15-2-1234",
                Array.Empty<string>());
            AssertEx.Equal(PromptIdentityKind.Package, identity.Kind);

            bool asked = false;
            bool created = PromptAllowPolicy.TryCreate(
                identity,
                _ => { asked = true; return false; },
                out PromptAllowPolicy? policy,
                out string? reason);

            AssertEx.True(created);
            AssertEx.True(policy != null);
            AssertEx.False(asked, "package identities carry no path to check");
            AssertEx.True(reason == null);
        }

        private static void AllowPolicyClassifiesCheckablePaths()
        {
            foreach (string checkable in new[]
            {
                @"C:\apps\gone.exe",
                @"d:\x.exe",
                @"\\?\C:\apps\long.exe",
                @"\\?\UNC\server\share\x.exe",
                @"\\server\share\x.exe",
            })
            {
                AssertEx.True(PromptAllowPolicy.RequiresExistenceCheck(checkable), checkable);
            }

            foreach (string skipped in new[]
            {
                "System",
                "system",
                @"\Device\HarddiskVolume3\x.exe",
                @"\??\C:\x.exe",
                @"\SystemRoot\System32\x.exe",
                @"\\.\PhysicalDrive0",
                @"\\?\GLOBALROOT\Device\HarddiskVolume3\x.exe",
                @"\\?\Volume{0000}\x.exe",
                "x.exe",
                "",
                "   ",
            })
            {
                AssertEx.False(PromptAllowPolicy.RequiresExistenceCheck(skipped), skipped);
            }
        }

        private static void AllowPolicyAllowsSystemPseudoPath()
        {
            var identity = PromptIdentity.ForExecutable("System");
            bool asked = false;

            bool created = PromptAllowPolicy.TryCreate(
                identity,
                _ => { asked = true; return false; },
                out PromptAllowPolicy? policy,
                out string? reason);

            AssertEx.True(created);
            AssertEx.True(policy != null);
            AssertEx.False(asked, "the kernel pseudo-path is never a file");
            AssertEx.True(reason == null);
        }

        private static void AllowPolicyAllowsNtFormPathWithoutCheck()
        {
            var identity = PromptIdentity.ForExecutable(@"\Device\HarddiskVolume3\x.exe");
            bool asked = false;

            bool created = PromptAllowPolicy.TryCreate(
                identity,
                _ => { asked = true; return false; },
                out PromptAllowPolicy? policy,
                out string? reason);

            AssertEx.True(created);
            AssertEx.True(policy != null);
            AssertEx.False(asked, "unmapped NT paths cannot be checked with File.Exists");
            AssertEx.True(reason == null);
        }

        private static void AllowPolicyStillRefusesMissingWin32Path()
        {
            var identity = PromptIdentity.ForService(@"C:\gone.exe", "GoneSvc");
            var asked = new List<string>();

            bool created = PromptAllowPolicy.TryCreate(
                identity,
                path => { asked.Add(path); return false; },
                out PromptAllowPolicy? policy,
                out string? reason);

            AssertEx.False(created);
            AssertEx.True(policy == null);
            AssertEx.SequenceEqual(new[] { @"C:\gone.exe" }, asked);
            AssertEx.True(reason != null && reason.Contains(@"C:\gone.exe"), reason);
        }
    }
}

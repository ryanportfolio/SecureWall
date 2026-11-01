using System;
using System.Globalization;
using System.IO;

namespace pylorak.TinyWall.Prompting
{
    // Decisions for the elevated `/apply-update` helper. The files come from the user's own
    // folder, so it copies them into a folder only SYSTEM and Administrators can change,
    // checks the copied installer against the copied manifest, checks the package's identity
    // and only then starts msiexec.
    internal static class UpdateApplyPolicy
    {
        internal const string Switch = "/apply-update";
        internal const string StagingFolderName = "SecureWall Updates";
        internal const string StagedPackage = "SecureWall.msi";
        internal const string StagedManifest = "SHA256SUMS.txt";
        internal const string InstallLog = "install.log";
        internal const string UpgradeCode = "{D257A00C-9D9B-4948-910F-5524F20819D0}";
        internal const string SuppressUserActionsProperty = "SECUREWALL_SUPPRESS_USER_ACTIONS";
        internal static readonly TimeSpan StagingMaxAge = TimeSpan.FromHours(24);
        internal const int MaxPackageBytes = 100 * 1024 * 1024;

        // Protected staging parent: SYSTEM and Administrators only, inherited by every child.
        internal const string StagingSddl = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

        internal sealed class Arguments
        {
            internal Arguments(string package, string manifest, string userSid)
            {
                Package = package;
                Manifest = manifest;
                UserSid = userSid;
            }

            internal string Package { get; }
            internal string Manifest { get; }
            internal string UserSid { get; }
        }

        // SecureWall.exe /apply-update "<msi>" "<manifest>" <requesting user SID>
        internal static bool TryParseArguments(string[] args, out Arguments? parsed)
        {
            parsed = null;
            if (args == null || args.Length != 4 || !string.Equals(args[0], Switch, StringComparison.OrdinalIgnoreCase))
                return false;
            for (int i = 1; i < 4; ++i)
            {
                if (string.IsNullOrWhiteSpace(args[i]))
                    return false;
            }
            if (!UpdateNetworkGrantPolicy.IsAllowedUser(args[3]))
                return false;
            parsed = new Arguments(args[1], args[2], args[3]);
            return true;
        }

        // The folder the controller downloads into: %LOCALAPPDATA%\SecureWall\updates.
        internal static string UserUpdatesRoot(string localAppData) =>
            Path.Combine(localAppData, SecureWallProduct.AppDataFolderName, "updates");

        // A source path must be a plain absolute path on a local drive letter, already in
        // canonical form, and below the requesting user's updates folder. UNC paths, device
        // paths (\\?\, \\.\), alternate data streams and relative segments are refused.
        internal static bool IsAcceptableSourcePath(string? path, string updatesRoot, out string error)
        {
            error = "The update file path is not acceptable: " + (path ?? "(none)");
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(updatesRoot) || path!.Length > 240)
                return false;
            if (path.Length < 4 || !IsDriveLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                return false;
            if (path.IndexOf(':', 2) >= 0 || path.IndexOf('/') >= 0 || path.IndexOf("\\\\", StringComparison.Ordinal) >= 0 ||
                path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOf('*') >= 0 || path.IndexOf('?') >= 0)
                return false;
            foreach (string segment in path.Substring(3).Split('\\'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith(" ", StringComparison.Ordinal))
                    return false;
            }
            string root = updatesRoot.TrimEnd('\\') + "\\";
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.Length == root.Length)
                return false;
            error = string.Empty;
            return true;
        }

        private static bool IsDriveLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        // The tag the helper takes from the manifest names the staging folder's
        // contents only through fixed file names; it is still held to the release form.
        internal static bool IsReleaseTag(string? tag) => ReleaseVersion.TryParseTag(tag, out _);

        internal static bool IsExpiredStaging(DateTime createdUtc, DateTime nowUtc) =>
            nowUtc - createdUtc > StagingMaxAge;

        internal static string NewStagingName(DateTime nowUtc, Guid id) =>
            nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + id.ToString("N");

        // MSI Template summary property: "<platform>;<languages>".
        internal static string? TemplatePlatform(string? template)
        {
            if (string.IsNullOrEmpty(template))
                return null;
            string platform = template!.Split(';')[0].Trim();
            if (string.Equals(platform, "x64", StringComparison.OrdinalIgnoreCase)) return "x64";
            if (string.Equals(platform, "Arm64", StringComparison.OrdinalIgnoreCase)) return "arm64";
            if (string.Equals(platform, "Intel", StringComparison.OrdinalIgnoreCase)) return "x86";
            return null;
        }

        // The package must be SecureWall (UpgradeCode), for the installed platform, at the
        // manifest's version (ProductVersion has four fields, the last 0), and never an
        // upgrade fault-test build.
        internal static bool PackageMatches(string? upgradeCode, string? template, string? productVersion,
            bool faultTestProperty, Version manifestVersion, string installedPlatform, out string error)
        {
            if (!string.Equals(upgradeCode, UpgradeCode, StringComparison.OrdinalIgnoreCase))
                error = "The downloaded package is not a SecureWall installer.";
            else if (TemplatePlatform(template) != installedPlatform)
                error = "The downloaded package is for another processor architecture.";
            else if (productVersion != ReleaseVersion.Format(manifestVersion) + ".0")
                error = "The downloaded package's version does not match the release manifest.";
            else if (faultTestProperty)
                error = "The downloaded package is a test build.";
            else
            {
                error = string.Empty;
                return true;
            }
            return false;
        }

        // Full UI, verbose log beside the staged package. An over-the-shoulder elevation (the
        // helper runs as another user than the one who asked) suppresses the package's
        // impersonated user actions: the failure page's log buttons and the tray restart.
        internal static string MsiexecArguments(string package, string log, bool suppressUserActions) =>
            "/i \"" + package + "\" /l*v \"" + log + "\"" + (suppressUserActions ? " " + SuppressUserActionsProperty + "=1" : string.Empty);
    }
}

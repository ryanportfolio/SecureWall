#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace pylorak.TinyWall.Prompting
{
    // SHA256SUMS.txt with the header that Build-SecureWallRelease.ps1 writes and release.yml
    // attaches to each GitHub release:
    //
    //   # SecureWall-Release-Manifest: 1
    //   # Repository: ryanportfolio/SecureWall
    //   # Tag: v0.5.1
    //   # Version: 0.5.1
    //   # Prerelease: true
    //   # Commit: <40 lowercase hex>
    //   <64 lowercase hex>  SecureWall_x64.msi
    //
    // The manifest is not signed: it is trusted as far as the GitHub release it comes from.
    // It binds the installer hashes to one tag and version. The parser accepts this layout
    // only; anything else is "manifest does not match".
    internal sealed class ReleaseManifest
    {
        internal const string Repository = "ryanportfolio/SecureWall";
        internal const int MaxManifestBytes = 64 * 1024;

        private const string FormatLine = "# SecureWall-Release-Manifest: 1";
        private const string RepositoryPrefix = "# Repository: ";
        private const string TagPrefix = "# Tag: ";
        private const string VersionPrefix = "# Version: ";
        private const string PrereleasePrefix = "# Prerelease: ";
        private const string CommitPrefix = "# Commit: ";

        internal static readonly string[] Platforms = { "x86", "x64", "arm64" };

        internal string Tag { get; }
        internal Version Version { get; }
        internal bool Prerelease { get; }
        internal string Commit { get; }
        // Asset file name to lowercase hex SHA-256.
        internal IReadOnlyDictionary<string, string> Hashes { get; }

        private ReleaseManifest(string tag, Version version, bool prerelease, string commit, Dictionary<string, string> hashes)
        {
            Tag = tag;
            Version = version;
            Prerelease = prerelease;
            Commit = commit;
            Hashes = hashes;
        }

        internal static string AssetName(string platform) => "SecureWall_" + platform + ".msi";

        internal string? HashFor(string platform) =>
            Array.IndexOf(Platforms, platform) >= 0 && Hashes.TryGetValue(AssetName(platform), out string? hash) ? hash : null;

        internal static bool TryParse(byte[] bytes, out ReleaseManifest? manifest, out string error)
        {
            manifest = null;
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxManifestBytes)
            {
                error = "The manifest is empty or too large.";
                return false;
            }
            foreach (byte b in bytes)
            {
                if ((b < 0x20 || b > 0x7E) && b != (byte)'\r' && b != (byte)'\n')
                {
                    error = "The manifest contains characters other than printable ASCII.";
                    return false;
                }
            }

            string[] raw = Encoding.ASCII.GetString(bytes).Split('\n');
            var lines = new List<string>(raw.Length);
            for (int i = 0; i < raw.Length; ++i)
            {
                string line = raw[i].EndsWith("\r", StringComparison.Ordinal) ? raw[i].Substring(0, raw[i].Length - 1) : raw[i];
                // One trailing newline is allowed; no other line may be empty.
                if (line.Length == 0 && i == raw.Length - 1)
                    break;
                if (line.Length == 0 || line.IndexOf('\r') >= 0)
                {
                    error = "The manifest has an empty or malformed line.";
                    return false;
                }
                lines.Add(line);
            }

            if (lines.Count < 7 || lines[0] != FormatLine)
            {
                error = "The manifest header is missing or has an unknown format.";
                return false;
            }
            if (!TryValue(lines[1], RepositoryPrefix, out string repository) || repository != Repository)
            {
                error = "The manifest names another repository.";
                return false;
            }
            if (!TryValue(lines[2], TagPrefix, out string tag) || !ReleaseVersion.TryParseTag(tag, out Version? tagVersion))
            {
                error = "The manifest tag is not a release tag.";
                return false;
            }
            if (!TryValue(lines[3], VersionPrefix, out string versionText) ||
                !ReleaseVersion.TryParse(versionText, out Version? version) || version != tagVersion)
            {
                error = "The manifest version does not match its tag.";
                return false;
            }
            bool prerelease;
            if (!TryValue(lines[4], PrereleasePrefix, out string prereleaseText) ||
                !(prereleaseText == "true" || prereleaseText == "false"))
            {
                error = "The manifest prerelease line is malformed.";
                return false;
            }
            prerelease = prereleaseText == "true";
            if (!TryValue(lines[5], CommitPrefix, out string commit) || !IsLowerHex(commit, 40))
            {
                error = "The manifest commit line is malformed.";
                return false;
            }

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 6; i < lines.Count; ++i)
            {
                string line = lines[i];
                if (line.Length < 67 || line[64] != ' ' || line[65] != ' ' || !IsLowerHex(line.Substring(0, 64), 64))
                {
                    error = "The manifest has a malformed hash line.";
                    return false;
                }
                string name = line.Substring(66);
                if (hashes.ContainsKey(name))
                {
                    error = "The manifest names a file twice.";
                    return false;
                }
                // Lines for other files are allowed, so a later release can list more assets
                // without every installed version rejecting its manifest; only installers count.
                hashes.Add(name, line.Substring(0, 64));
            }

            manifest = new ReleaseManifest(tag, version!, prerelease, commit, hashes);
            error = string.Empty;
            return true;
        }

        internal static string Sha256Hex(byte[] data)
        {
            using SHA256 sha = SHA256.Create();
            return ToLowerHex(sha.ComputeHash(data));
        }

        internal static string ToLowerHex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                text.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private static bool TryValue(string line, string prefix, out string value)
        {
            value = string.Empty;
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            value = line.Substring(prefix.Length);
            return value.Length > 0 && value.Trim() == value;
        }

        internal static bool IsLowerHex(string text, int length)
        {
            if (text.Length != length)
                return false;
            foreach (char c in text)
            {
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))
                    return false;
            }
            return true;
        }
    }

    // Three-field release versions, the fields Windows Installer compares. Tags are
    // v<major>.<minor>.<patch> without leading zeros.
    internal static class ReleaseVersion
    {
        internal static bool TryParseTag(string? tag, out Version? version)
        {
            version = null;
            return tag != null && tag.Length > 1 && tag[0] == 'v' && TryParse(tag.Substring(1), out version);
        }

        internal static bool TryParse(string? text, out Version? version)
        {
            version = null;
            if (string.IsNullOrEmpty(text))
                return false;
            string[] parts = text!.Split('.');
            if (parts.Length != 3)
                return false;
            var numbers = new int[3];
            for (int i = 0; i < 3; ++i)
            {
                string part = parts[i];
                if (part.Length == 0 || part.Length > 9 || (part.Length > 1 && part[0] == '0'))
                    return false;
                foreach (char c in part)
                {
                    if (c < '0' || c > '9')
                        return false;
                }
                numbers[i] = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
            }
            version = new Version(numbers[0], numbers[1], numbers[2]);
            return true;
        }

        // The first three fields of an assembly or file version.
        internal static Version Normalize(Version version) =>
            new Version(Math.Max(version.Major, 0), Math.Max(version.Minor, 0), Math.Max(version.Build, 0));

        internal static string Format(Version version) =>
            version.Major + "." + version.Minor + "." + version.Build;
    }
}

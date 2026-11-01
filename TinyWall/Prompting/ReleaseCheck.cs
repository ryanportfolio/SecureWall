using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace pylorak.TinyWall.Prompting
{
    // One GitHub release as the releases API lists it. Only fields the check uses.
    // Nothing here is trusted: the signed manifest decides what installs.
    internal sealed class GitHubRelease
    {
        internal string Tag { get; set; } = string.Empty;
        internal Version? Version { get; set; }
        internal bool Draft { get; set; }
        internal bool Prerelease { get; set; }
        internal DateTimeOffset? PublishedAt { get; set; }
        internal string Notes { get; set; } = string.Empty;
        // Asset name to download URL, for assets whose URL is the exact release path.
        internal Dictionary<string, Uri> Assets { get; } = new(StringComparer.Ordinal);
    }

    // The outcome of one HTTPS GET, after redirects.
    internal sealed class ReleaseFetch
    {
        internal byte[]? Body { get; private set; }
        internal int Status { get; private set; }
        internal bool RateLimited { get; private set; }
        internal string Error { get; private set; } = string.Empty;
        internal bool Succeeded => Body != null;

        internal static ReleaseFetch Ok(byte[] body) => new() { Body = body, Status = 200 };
        internal static ReleaseFetch Fail(string error, int status = 0, bool rateLimited = false) =>
            new() { Error = error, Status = status, RateLimited = rateLimited };
    }

    internal interface IReleaseSource
    {
        // Returns at most maxBytes; a larger body is a failure, never a truncated success.
        Task<ReleaseFetch> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken);
    }

    internal enum ReleaseCheckOutcome
    {
        UpdateAvailable,
        UpToDate,
        NothingEligible,
        Failed,
    }

    internal sealed class ReleaseCheckResult
    {
        internal ReleaseCheckOutcome Outcome { get; private set; }
        internal string Message { get; private set; } = string.Empty;
        internal GitHubRelease? Release { get; private set; }
        internal SignedReleaseManifest? Manifest { get; private set; }
        internal byte[] ManifestBytes { get; private set; } = Array.Empty<byte>();
        internal byte[] SignatureBytes { get; private set; } = Array.Empty<byte>();

        internal static ReleaseCheckResult Available(GitHubRelease release, SignedReleaseManifest manifest, byte[] manifestBytes, byte[] signatureBytes) =>
            new() { Outcome = ReleaseCheckOutcome.UpdateAvailable, Release = release, Manifest = manifest, ManifestBytes = manifestBytes, SignatureBytes = signatureBytes };
        internal static ReleaseCheckResult Current(string message) => new() { Outcome = ReleaseCheckOutcome.UpToDate, Message = message };
        internal static ReleaseCheckResult Skipped(string message) => new() { Outcome = ReleaseCheckOutcome.NothingEligible, Message = message };
        internal static ReleaseCheckResult Fail(string message) => new() { Outcome = ReleaseCheckOutcome.Failed, Message = message };
    }

    internal enum ReleaseCandidateVerdict
    {
        Accepted,
        SignatureInvalid,
        ManifestMismatch,
        PrereleaseNotWanted,
        Unavailable,
    }

    // "Check for updates": the only remote source is GitHub's release list for
    // ryanportfolio/SecureWall and the assets it names. Runs in the controller under the
    // user's token; the service never contacts the network for updates.
    internal static class ReleaseCheck
    {
        internal const string ApiHost = "api.github.com";
        internal const string ReleasesApi = "https://api.github.com/repos/ryanportfolio/SecureWall/releases";
        internal const string DownloadPrefix = "https://github.com/ryanportfolio/SecureWall/releases/download/";
        internal const string ManifestAsset = "SHA256SUMS.txt";
        internal const string SignatureAsset = "SHA256SUMS.txt.sig";

        // Hosts a request or redirect may reach. Asset downloads redirect from github.com to
        // GitHub's object storage.
        internal static readonly string[] AllowedHosts =
        {
            "api.github.com",
            "github.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com",
        };

        internal const int PerPage = 30;
        internal const int MaxPages = 3;
        internal const int MaxCandidates = 5;
        internal const int MaxRedirects = 5;
        internal const int MaxApiBytes = 1024 * 1024;
        internal const int MaxPackageBytes = UpdateApplyPolicy.MaxPackageBytes;
        internal const int MaxNotesChars = 20000;
        internal static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);

        internal const string RateLimitMessage =
            "GitHub's limit for update checks from this network was reached (60 an hour). Try again later.";
        internal const string SignatureMessage = "signature does not verify";
        internal const string MismatchMessage = "manifest does not match this release";
        internal const string PrereleaseMessage = "only prereleases are newer";

        internal static Uri ReleasesPage(int page) =>
            new(ReleasesApi + "?per_page=" + PerPage.ToString(CultureInfo.InvariantCulture) +
                "&page=" + page.ToString(CultureInfo.InvariantCulture));

        internal static bool IsAllowedEndpoint(Uri? uri) =>
            uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 &&
            string.IsNullOrEmpty(uri.UserInfo) && AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

        // A redirect is followed only to another allowed HTTPS endpoint, at most MaxRedirects
        // times per request. Relative locations resolve against the current URL.
        internal static bool TryFollowRedirect(Uri current, string? location, int redirectsSoFar, out Uri? next, out string error)
        {
            next = null;
            if (redirectsSoFar >= MaxRedirects)
            {
                error = "Too many redirects.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(current, location, out Uri? target))
            {
                error = "A redirect had no usable location.";
                return false;
            }
            if (!IsAllowedEndpoint(target))
            {
                error = "A redirect pointed outside GitHub's HTTPS hosts.";
                return false;
            }
            next = target;
            error = string.Empty;
            return true;
        }

        // Asset URLs come from unsigned JSON, so they must be exactly the release download path.
        internal static bool IsAllowedAssetUrl(string? url, string tag, string name) =>
            url != null && ReleaseVersion.TryParseTag(tag, out _) && IsSafeAssetName(name) &&
            string.Equals(url, DownloadPrefix + tag + "/" + name, StringComparison.Ordinal);

        private static bool IsSafeAssetName(string name) =>
            name.Length > 0 && name.Length <= 64 && name.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-') && name.IndexOf("..", StringComparison.Ordinal) < 0;

        // Parses one page of GET /repos/{owner}/{repo}/releases. entries counts every array
        // element, so a short page ends the listing even when some entries were unusable.
        internal static List<GitHubRelease> ParseReleasesPage(byte[] json, out int entries)
        {
            entries = 0;
            var releases = new List<GitHubRelease>();
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("The release list is not an array.");
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                entries++;
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var release = new GitHubRelease
                {
                    Tag = ReadString(item, "tag_name"),
                    Draft = ReadBool(item, "draft"),
                    Prerelease = ReadBool(item, "prerelease"),
                    Notes = ReadString(item, "body"),
                };
                if (ReleaseVersion.TryParseTag(release.Tag, out Version? version))
                    release.Version = version;
                if (DateTimeOffset.TryParse(ReadString(item, "published_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out DateTimeOffset published))
                    release.PublishedAt = published;
                if (item.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        if (asset.ValueKind != JsonValueKind.Object)
                            continue;
                        string name = ReadString(asset, "name");
                        string url = ReadString(asset, "browser_download_url");
                        if (!release.Assets.ContainsKey(name) && IsAllowedAssetUrl(url, release.Tag, name))
                            release.Assets.Add(name, new Uri(url));
                    }
                }
                releases.Add(release);
            }
            return releases;
        }

        // Releases worth verifying, highest version first. GitHub orders the list by creation
        // date, and its prerelease flag is unsigned, so neither decides anything here.
        internal static List<GitHubRelease> Candidates(IEnumerable<GitHubRelease> releases, Version installed,
            string platform, Version? skipped)
        {
            Version current = ReleaseVersion.Normalize(installed);
            var seen = new HashSet<Version>();
            return releases
                .Where(r => !r.Draft && r.Version != null && r.Version > current && r.Version != skipped &&
                    r.Assets.ContainsKey(SignedReleaseManifest.AssetName(platform)) &&
                    r.Assets.ContainsKey(ManifestAsset) && r.Assets.ContainsKey(SignatureAsset))
                .OrderByDescending(r => r.Version)
                .Where(r => seen.Add(r.Version!))
                .ToList();
        }

        // Decides one candidate from its downloaded manifest and signature. Only the signed
        // Prerelease line decides whether a prerelease is offered.
        internal static ReleaseCandidateVerdict Evaluate(GitHubRelease release, byte[] manifestBytes, byte[] signatureBytes,
            IEnumerable<string> trustedKeys, string platform, bool includePrereleases, out SignedReleaseManifest? manifest)
        {
            manifest = null;
            if (!SignedReleaseManifest.VerifySignature(manifestBytes, signatureBytes, trustedKeys))
                return ReleaseCandidateVerdict.SignatureInvalid;
            if (!SignedReleaseManifest.TryParse(manifestBytes, out SignedReleaseManifest? parsed, out _) || parsed == null ||
                parsed.Tag != release.Tag || parsed.Version != release.Version || parsed.HashFor(platform) == null)
                return ReleaseCandidateVerdict.ManifestMismatch;
            if (parsed.Prerelease && !includePrereleases)
                return ReleaseCandidateVerdict.PrereleaseNotWanted;
            manifest = parsed;
            return ReleaseCandidateVerdict.Accepted;
        }

        internal static async Task<ReleaseCheckResult> FindAsync(IReleaseSource source, Version installed, string platform,
            bool includePrereleases, Version? skipped, IReadOnlyList<string> trustedKeys, CancellationToken cancellationToken)
        {
            if (Array.IndexOf(SignedReleaseManifest.Platforms, platform) < 0)
                return ReleaseCheckResult.Fail("SecureWall's installed package type is not recorded, so no update can be matched to it. Install the next version by hand from the website.");
            if (trustedKeys.Count == 0)
                return ReleaseCheckResult.Fail("This build of SecureWall has no release signing key, so it cannot verify updates.");

            var releases = new List<GitHubRelease>();
            for (int page = 1; page <= MaxPages; ++page)
            {
                ReleaseFetch fetch = await source.FetchAsync(ReleasesPage(page), MaxApiBytes, cancellationToken).ConfigureAwait(false);
                if (!fetch.Succeeded)
                    return ReleaseCheckResult.Fail(fetch.RateLimited ? RateLimitMessage : "Could not read SecureWall's releases from GitHub: " + fetch.Error);
                int entries;
                try { releases.AddRange(ParseReleasesPage(fetch.Body!, out entries)); }
                catch (JsonException) { return ReleaseCheckResult.Fail("GitHub returned a release list SecureWall could not read."); }
                if (entries < PerPage)
                    break;
            }

            List<GitHubRelease> candidates = Candidates(releases, installed, platform, skipped);
            if (candidates.Count == 0)
            {
                string current = ReleaseVersion.Format(ReleaseVersion.Normalize(installed));
                return ReleaseCheckResult.Current(skipped != null && Candidates(releases, installed, platform, null).Count > 0
                    ? "SecureWall " + current + " is installed. Version " + ReleaseVersion.Format(skipped) + " is newer, but you chose to skip it."
                    : "SecureWall " + current + " is the newest version.");
            }

            var verdicts = new List<ReleaseCandidateVerdict>();
            foreach (GitHubRelease candidate in candidates.Take(MaxCandidates))
            {
                ReleaseFetch manifest = await source.FetchAsync(candidate.Assets[ManifestAsset], SignedReleaseManifest.MaxManifestBytes, cancellationToken).ConfigureAwait(false);
                if (!manifest.Succeeded)
                {
                    if (manifest.RateLimited)
                        return ReleaseCheckResult.Fail(RateLimitMessage);
                    verdicts.Add(ReleaseCandidateVerdict.Unavailable);
                    continue;
                }
                ReleaseFetch signature = await source.FetchAsync(candidate.Assets[SignatureAsset], SignedReleaseManifest.MaxSignatureBytes, cancellationToken).ConfigureAwait(false);
                if (!signature.Succeeded)
                {
                    if (signature.RateLimited)
                        return ReleaseCheckResult.Fail(RateLimitMessage);
                    verdicts.Add(ReleaseCandidateVerdict.Unavailable);
                    continue;
                }
                ReleaseCandidateVerdict verdict = Evaluate(candidate, manifest.Body!, signature.Body!, trustedKeys, platform,
                    includePrereleases, out SignedReleaseManifest? accepted);
                if (verdict == ReleaseCandidateVerdict.Accepted)
                    return ReleaseCheckResult.Available(candidate, accepted!, manifest.Body!, signature.Body!);
                verdicts.Add(verdict);
            }
            return ReleaseCheckResult.Skipped(DescribeSkipped(verdicts));
        }

        internal static string DescribeSkipped(IReadOnlyCollection<ReleaseCandidateVerdict> verdicts)
        {
            var reasons = new List<string>();
            if (verdicts.Contains(ReleaseCandidateVerdict.PrereleaseNotWanted))
                reasons.Add(PrereleaseMessage + " (turn on \"Include prereleases\" in Manage > Maintenance to see them)");
            if (verdicts.Contains(ReleaseCandidateVerdict.SignatureInvalid))
                reasons.Add(SignatureMessage);
            if (verdicts.Contains(ReleaseCandidateVerdict.ManifestMismatch))
                reasons.Add(MismatchMessage);
            if (verdicts.Contains(ReleaseCandidateVerdict.Unavailable))
                reasons.Add("a release's files could not be downloaded");
            return "No update was installed. Newer releases were found but skipped: " + string.Join("; ", reasons) + ".";
        }

        // Release notes are shown as plain text only, capped, with control characters other
        // than line breaks and tabs removed.
        internal static string PlainNotes(string? notes)
        {
            if (string.IsNullOrEmpty(notes))
                return "(This release has no notes.)";
            var text = new System.Text.StringBuilder(Math.Min(notes!.Length, MaxNotesChars) + 2);
            foreach (char c in notes)
            {
                if (text.Length >= MaxNotesChars)
                {
                    text.Append(" [...]");
                    break;
                }
                // Format characters include the bidirectional overrides that can make text
                // read differently from what it says.
                if (c == '\n')
                    text.Append("\r\n");
                else if (c == '\t' || (!char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format))
                    text.Append(c);
            }
            return text.ToString();
        }

        // The version the user chose to skip, or null.
        internal static Version? ParseSkipped(string? skipped) =>
            ReleaseVersion.TryParse(skipped, out Version? version) ? version : null;

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static bool ReadBool(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }
}

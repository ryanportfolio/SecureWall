using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using pylorak.TinyWall;
using pylorak.TinyWall.Prompting;
using pylorak.Windows.WFP;

namespace SecureWall.Core.Tests;

// Check for updates: the signed manifest, release selection, transport policy, the service's
// transient permit leases and the elevated helper's decisions.
internal static class ReleaseUpdateTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("release manifest parses the signed header and hash lines", ManifestParses),
        ("release manifest rejects every other layout", ManifestRejectsOtherLayouts),
        ("release signature verifies only P1363 signatures over the exact bytes", SignatureVerifiesExactBytes),
        ("release versions are three canonical fields", ReleaseVersionsAreCanonical),
        ("release candidate verdict uses the signed tag, version, platform and prerelease line", CandidateVerdictUsesSignedFields),
        ("release list parsing keeps only exact release download URLs", ReleaseListKeepsExactAssetUrls),
        ("release candidates drop drafts, older, skipped and incomplete releases and sort by version", CandidatesFilterAndSort),
        ("release check falls back past a newer prerelease and a newer bad signature", CheckFallsBackPastIneligible),
        ("release check offers a signed prerelease when prereleases are included", CheckOffersPrereleaseWhenIncluded),
        ("release check reads at most three pages and stops at a short page", CheckPageLimits),
        ("release check verifies at most five candidates", CheckCandidateLimit),
        ("release check reports rate limits, skipped versions and missing keys", CheckReportsSpecialCases),
        ("release redirects stay on GitHub HTTPS hosts and are bounded", RedirectPolicy),
        ("release notes are capped plain text without format characters", NotesArePlainText),
        ("update permit decision follows the firewall mode and display block", GrantDecisionFollowsMode),
        ("update permit leases expire, end on release and never pass to a reused process ID", LeaseLifetime),
        ("update permit names only the requesting user and denies service accounts", PermitNamesOnlyRequestingUser),
        ("update messages sit in the matching privilege ranges", MessagesSitInPrivilegeRanges),
        ("service removes update permits on lock, failed reloads and revocation", ServiceRemovesPermits),
        ("apply-update arguments and source paths are strict", ApplyArgumentsAndPaths),
        ("apply-update package checks UpgradeCode, platform, version and test builds", ApplyPackageChecks),
        ("apply-update staging is protected, aged and launched with the suppress property", ApplyStagingAndLaunch),
        ("embedded release keys equal tools/release/keys", EmbeddedKeysMatchFiles),
        ("protocol self-test round trips update permit messages", ProtocolSelfTestCoversUpdateMessages),
    };

    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string HashX64 = new('a', 64);
    private static readonly string HashArm = new('b', 64);
    private static readonly string HashX86 = new('c', 64);

    private static string ManifestText(string tag = "v0.6.0", string version = "0.6.0", bool prerelease = false,
        string repository = "ryanportfolio/SecureWall", string newline = "\r\n", params string[] platforms)
    {
        if (platforms.Length == 0) platforms = new[] { "x86", "x64", "arm64" };
        var text = new StringBuilder();
        text.Append("# SecureWall-Release-Manifest: 1").Append(newline)
            .Append("# Repository: ").Append(repository).Append(newline)
            .Append("# Tag: ").Append(tag).Append(newline)
            .Append("# Version: ").Append(version).Append(newline)
            .Append("# Prerelease: ").Append(prerelease ? "true" : "false").Append(newline)
            .Append("# Commit: ").Append(Commit).Append(newline);
        foreach (string platform in platforms)
        {
            string hash = platform == "x64" ? HashX64 : platform == "arm64" ? HashArm : HashX86;
            text.Append(hash).Append("  SecureWall_").Append(platform).Append(".msi").Append(newline);
        }
        return text.ToString();
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private sealed class Key : IDisposable
    {
        internal readonly ECDsa Ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal string Public
        {
            get
            {
                ECPoint q = Ecdsa.ExportParameters(false).Q;
                return Convert.ToBase64String(q.X!.Concat(q.Y!).ToArray());
            }
        }
        internal byte[] Sign(byte[] data) =>
            Ascii(Convert.ToBase64String(Ecdsa.SignData(data, HashAlgorithmName.SHA256)) + "\n");
        public void Dispose() => Ecdsa.Dispose();
    }

    private static void ManifestParses()
    {
        foreach (string newline in new[] { "\r\n", "\n" })
        {
            AssertEx.True(SignedReleaseManifest.TryParse(Ascii(ManifestText(prerelease: true, newline: newline)), out var manifest, out string error), error);
            AssertEx.Equal("v0.6.0", manifest!.Tag);
            AssertEx.Equal(new Version(0, 6, 0), manifest.Version);
            AssertEx.True(manifest.Prerelease);
            AssertEx.Equal(Commit, manifest.Commit);
            AssertEx.Equal(HashX64, manifest.HashFor("x64"));
            AssertEx.Equal(HashArm, manifest.HashFor("arm64"));
            AssertEx.Equal(HashX86, manifest.HashFor("x86"));
        }
        // Lines for other files are ignored.
        AssertEx.True(SignedReleaseManifest.TryParse(Ascii(ManifestText().Replace("SecureWall_x64.msi", "SecureWall_x64.zip")), out var other, out _));
        AssertEx.True(other!.HashFor("x64") == null && other.HashFor("x86") == HashX86);
        // Without a trailing newline, and with one platform only.
        AssertEx.True(SignedReleaseManifest.TryParse(Ascii(ManifestText(platforms: "x64").TrimEnd()), out var single, out _));
        AssertEx.True(single!.HashFor("arm64") == null);
    }

    private static void ManifestRejectsOtherLayouts()
    {
        string good = ManifestText();
        string[] bad =
        {
            good.Replace("Manifest: 1", "Manifest: 2"),
            ManifestText(repository: "someone/SecureWall"),
            ManifestText(tag: "v0.6.1"),
            ManifestText(tag: "0.6.0"),
            ManifestText(tag: "v0.06.0", version: "0.06.0"),
            good.Replace("Prerelease: false", "Prerelease: no"),
            good.Replace(Commit, Commit.ToUpperInvariant()),
            good.Replace("SecureWall_x86.msi", "SecureWall_x64.msi"),
            good.Replace(HashX64, HashX64.ToUpperInvariant()),
            good.Replace(HashX64 + "  ", HashX64 + " "),
            good.Replace("\r\n# Tag", "\r\n\r\n# Tag"),
            good + "extra line\r\n",
            good.Replace("SecureWall-Release", "SecureWall\u2011Release"),
            good.Replace("# Repository", "#  Repository"),
            ManifestText(platforms: new string[0]).Split("\r\n").Take(6).Aggregate((a, b) => a + "\r\n" + b),
        };
        foreach (string text in bad)
            AssertEx.False(SignedReleaseManifest.TryParse(Encoding.UTF8.GetBytes(text), out _, out _), text);
        AssertEx.False(SignedReleaseManifest.TryParse(new byte[SignedReleaseManifest.MaxManifestBytes + 1], out _, out _));
        AssertEx.False(SignedReleaseManifest.TryParse(Array.Empty<byte>(), out _, out _));
    }

    private static void SignatureVerifiesExactBytes()
    {
        using var key = new Key();
        using var other = new Key();
        byte[] manifest = Ascii(ManifestText());
        byte[] signature = key.Sign(manifest);
        AssertEx.True(SignedReleaseManifest.VerifySignature(manifest, signature, new[] { key.Public }));
        AssertEx.True(SignedReleaseManifest.VerifySignature(manifest, signature, new[] { other.Public, key.Public }), "either trusted key verifies");
        AssertEx.False(SignedReleaseManifest.VerifySignature(manifest, signature, new[] { other.Public }), "wrong key");
        AssertEx.False(SignedReleaseManifest.VerifySignature(manifest, signature, Array.Empty<string>()), "no keys");

        byte[] edited = (byte[])manifest.Clone();
        edited[^5] ^= 1;
        AssertEx.False(SignedReleaseManifest.VerifySignature(edited, signature, new[] { key.Public }), "edited manifest");

        byte[] der = Ascii(Convert.ToBase64String(key.Ecdsa.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
        AssertEx.False(SignedReleaseManifest.VerifySignature(manifest, der, new[] { key.Public }), "DER signature");

        string base64 = Encoding.ASCII.GetString(signature).Trim();
        byte[] raw = Convert.FromBase64String(base64);
        AssertEx.False(SignedReleaseManifest.VerifySignature(manifest, Ascii(Convert.ToBase64String(raw.Take(63).ToArray())), new[] { key.Public }), "truncated");
        AssertEx.False(SignedReleaseManifest.VerifySignature(manifest, Ascii(base64.Substring(0, base64.Length - 4)), new[] { key.Public }), "cut base64");
        AssertEx.False(SignedReleaseManifest.VerifySignature(manifest, Ascii(base64 + " junk"), new[] { key.Public }), "trailing text");
        AssertEx.False(SignedReleaseManifest.VerifySignature(manifest, new byte[SignedReleaseManifest.MaxSignatureBytes + 1], new[] { key.Public }), "oversized");
        AssertEx.False(SignedReleaseManifest.VerifySignature(manifest, signature, new[] { "not base64", Convert.ToBase64String(new byte[64]) }), "invalid keys");
        AssertEx.True(SignedReleaseManifest.VerifySignature(manifest, Ascii("  " + base64 + "\r\n"), new[] { key.Public }), "surrounding whitespace");
    }

    private static void ReleaseVersionsAreCanonical()
    {
        AssertEx.True(ReleaseVersion.TryParseTag("v10.0.12", out Version? version) && version == new Version(10, 0, 12));
        foreach (string tag in new[] { "", "v", "0.6.0", "v0.6", "v0.6.0.1", "v0.6.0-rc1", "v01.6.0", "v0.6.x", "V0.6.0", "v 0.6.0", "v1234567890.0.0" })
            AssertEx.False(ReleaseVersion.TryParseTag(tag, out _), tag);
        AssertEx.Equal(new Version(0, 5, 0), ReleaseVersion.Normalize(new Version(0, 5, 0, 0)));
        AssertEx.Equal("0.5.0", ReleaseVersion.Format(new Version(0, 5, 0, 0)));
    }

    private static GitHubRelease Release(string tag, bool prerelease = false, bool draft = false, bool complete = true)
    {
        var release = new GitHubRelease { Tag = tag, Prerelease = prerelease, Draft = draft };
        if (ReleaseVersion.TryParseTag(tag, out Version? version))
            release.Version = version;
        foreach (string name in complete
            ? new[] { "SecureWall_x64.msi", "SecureWall_x86.msi", "SecureWall_arm64.msi", ReleaseCheck.ManifestAsset, ReleaseCheck.SignatureAsset }
            : new[] { "SecureWall_x64.msi", ReleaseCheck.ManifestAsset })
            release.Assets[name] = new Uri(ReleaseCheck.DownloadPrefix + tag + "/" + name);
        return release;
    }

    private static void CandidateVerdictUsesSignedFields()
    {
        using var key = new Key();
        string[] keys = { key.Public };
        ReleaseCandidateVerdict Verdict(GitHubRelease release, string manifest, bool includePrereleases, string platform = "x64")
        {
            byte[] bytes = Ascii(manifest);
            return ReleaseCheck.Evaluate(release, bytes, key.Sign(bytes), keys, platform, includePrereleases, out _);
        }

        GitHubRelease v6 = Release("v0.6.0");
        AssertEx.Equal(ReleaseCandidateVerdict.Accepted, Verdict(v6, ManifestText(), false));
        AssertEx.Equal(ReleaseCandidateVerdict.ManifestMismatch, Verdict(v6, ManifestText(tag: "v0.6.1", version: "0.6.1"), true), "replayed tag");
        AssertEx.Equal(ReleaseCandidateVerdict.ManifestMismatch, Verdict(Release("v0.7.0"), ManifestText(), true), "older signed release under a newer tag");
        AssertEx.Equal(ReleaseCandidateVerdict.ManifestMismatch, Verdict(v6, ManifestText(platforms: "x86"), true), "no line for this platform");
        AssertEx.Equal(ReleaseCandidateVerdict.ManifestMismatch, Verdict(v6, ManifestText(repository: "other/SecureWall"), true), "repository");
        // GitHub's flag is unsigned: the signed line decides, in both directions.
        AssertEx.Equal(ReleaseCandidateVerdict.PrereleaseNotWanted, Verdict(Release("v0.6.0", prerelease: false), ManifestText(prerelease: true), false));
        AssertEx.Equal(ReleaseCandidateVerdict.Accepted, Verdict(Release("v0.6.0", prerelease: true), ManifestText(prerelease: false), false));
        AssertEx.Equal(ReleaseCandidateVerdict.Accepted, Verdict(v6, ManifestText(prerelease: true), true));

        byte[] manifest = Ascii(ManifestText());
        using var stranger = new Key();
        AssertEx.Equal(ReleaseCandidateVerdict.SignatureInvalid, ReleaseCheck.Evaluate(v6, manifest, stranger.Sign(manifest), keys, "x64", true, out _));
    }

    private static byte[] ReleaseListJson(IEnumerable<GitHubRelease> releases, Func<GitHubRelease, string, string>? url = null)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartArray();
            foreach (GitHubRelease release in releases)
            {
                json.WriteStartObject();
                json.WriteString("tag_name", release.Tag);
                json.WriteBoolean("draft", release.Draft);
                json.WriteBoolean("prerelease", release.Prerelease);
                json.WriteString("published_at", "2026-11-05T10:00:00Z");
                json.WriteString("body", "Notes for " + release.Tag);
                json.WriteStartArray("assets");
                foreach (string name in release.Assets.Keys)
                {
                    json.WriteStartObject();
                    json.WriteString("name", name);
                    json.WriteString("browser_download_url", url?.Invoke(release, name) ?? ReleaseCheck.DownloadPrefix + release.Tag + "/" + name);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        return stream.ToArray();
    }

    private static void ReleaseListKeepsExactAssetUrls()
    {
        GitHubRelease source = Release("v0.6.0");
        byte[] json = ReleaseListJson(new[] { source }, (release, name) => name switch
        {
            "SecureWall_x64.msi" => "https://evil.example/ryanportfolio/SecureWall/releases/download/v0.6.0/SecureWall_x64.msi",
            "SecureWall_x86.msi" => "https://github.com/ryanportfolio/SecureWall/releases/download/v0.5.0/SecureWall_x86.msi",
            "SecureWall_arm64.msi" => "http://github.com/ryanportfolio/SecureWall/releases/download/v0.6.0/SecureWall_arm64.msi",
            _ => ReleaseCheck.DownloadPrefix + release.Tag + "/" + name,
        });
        List<GitHubRelease> parsed = ReleaseCheck.ParseReleasesPage(json, out int entries);
        AssertEx.Equal(1, entries);
        GitHubRelease release = parsed.Single();
        AssertEx.Equal(new Version(0, 6, 0), release.Version);
        AssertEx.Equal(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), release.PublishedAt);
        AssertEx.SequenceEqual(new[] { ReleaseCheck.ManifestAsset, ReleaseCheck.SignatureAsset }, release.Assets.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        AssertEx.False(ReleaseCheck.IsAllowedAssetUrl(ReleaseCheck.DownloadPrefix + "v0.6.0/../x.msi", "v0.6.0", "../x.msi"));
        AssertEx.False(ReleaseCheck.IsAllowedAssetUrl(ReleaseCheck.DownloadPrefix + "main/x.msi", "main", "x.msi"));
        AssertEx.Throws<JsonException>(() => ReleaseCheck.ParseReleasesPage(Encoding.UTF8.GetBytes("{\"message\":\"x\"}"), out _));
    }

    private static void CandidatesFilterAndSort()
    {
        var releases = new[]
        {
            Release("v0.5.1"),                    // listed first: created last
            Release("v0.7.0", draft: true),
            Release("v0.5.0"),                    // installed version
            Release("v0.4.2"),
            Release("v0.6.0"),
            Release("v0.6.5", complete: false),   // not signed yet
            Release("v0.8.0"),                    // skipped
            Release("main"),
        };
        List<GitHubRelease> candidates = ReleaseCheck.Candidates(releases, new Version(0, 5, 0, 0), "x64", new Version(0, 8, 0));
        AssertEx.SequenceEqual(new[] { "v0.6.0", "v0.5.1" }, candidates.Select(r => r.Tag).ToArray());
        AssertEx.Equal(0, ReleaseCheck.Candidates(releases, new Version(0, 5, 0), "arm64", null).Count(r => r.Tag == "v0.6.5"));
    }

    private sealed class FakeSource : IReleaseSource
    {
        internal readonly Dictionary<string, ReleaseFetch> Responses = new(StringComparer.Ordinal);
        internal readonly List<string> Requested = new();

        public Task<ReleaseFetch> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
        {
            Requested.Add(uri.AbsoluteUri);
            if (!Responses.TryGetValue(uri.AbsoluteUri, out ReleaseFetch? fetch))
                fetch = ReleaseFetch.Fail("not found", 404);
            if (fetch.Body != null && fetch.Body.Length > maxBytes)
                fetch = ReleaseFetch.Fail("too large");
            return Task.FromResult(fetch);
        }

        internal void Page(int page, IEnumerable<GitHubRelease> releases) =>
            Responses[ReleaseCheck.ReleasesPage(page).AbsoluteUri] = ReleaseFetch.Ok(ReleaseListJson(releases));

        internal void Signed(GitHubRelease release, Key key, string manifest, Key? signer = null)
        {
            byte[] bytes = Ascii(manifest);
            Responses[release.Assets[ReleaseCheck.ManifestAsset].AbsoluteUri] = ReleaseFetch.Ok(bytes);
            Responses[release.Assets[ReleaseCheck.SignatureAsset].AbsoluteUri] = ReleaseFetch.Ok((signer ?? key).Sign(bytes));
        }
    }

    private static ReleaseCheckResult Check(FakeSource source, Key key, bool includePrereleases, Version? skipped = null, string platform = "x64") =>
        ReleaseCheck.FindAsync(source, new Version(0, 5, 0, 0), platform, includePrereleases, skipped, new[] { key.Public }, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static void CheckFallsBackPastIneligible()
    {
        using var key = new Key();
        using var stranger = new Key();
        var full = Release("v0.6.0");
        var pre = Release("v0.8.0", prerelease: false);   // GitHub says full; the signature says prerelease
        var forged = Release("v0.9.0");
        var source = new FakeSource();
        source.Page(1, new[] { pre, full, forged });
        source.Signed(full, key, ManifestText());
        source.Signed(pre, key, ManifestText("v0.8.0", "0.8.0", prerelease: true));
        source.Signed(forged, key, ManifestText("v0.9.0", "0.9.0"), signer: stranger);

        ReleaseCheckResult result = Check(source, key, includePrereleases: false);
        AssertEx.Equal(ReleaseCheckOutcome.UpdateAvailable, result.Outcome, result.Message);
        AssertEx.Equal("v0.6.0", result.Release!.Tag);
        AssertEx.Equal(HashX64, result.Manifest!.HashFor("x64"));
        AssertEx.True(result.ManifestBytes.Length > 0 && result.SignatureBytes.Length > 0);

        // Nothing eligible: the reasons are named.
        var onlyBad = new FakeSource();
        onlyBad.Page(1, new[] { pre, forged });
        onlyBad.Signed(pre, key, ManifestText("v0.8.0", "0.8.0", prerelease: true));
        onlyBad.Signed(forged, key, ManifestText("v0.9.0", "0.9.0"), signer: stranger);
        ReleaseCheckResult none = Check(onlyBad, key, includePrereleases: false);
        AssertEx.Equal(ReleaseCheckOutcome.NothingEligible, none.Outcome);
        AssertEx.True(none.Message.Contains(ReleaseCheck.PrereleaseMessage) && none.Message.Contains(ReleaseCheck.SignatureMessage), none.Message);
    }

    private static void CheckOffersPrereleaseWhenIncluded()
    {
        using var key = new Key();
        var full = Release("v0.6.0");
        var pre = Release("v0.7.0", prerelease: true);
        var source = new FakeSource();
        source.Page(1, new[] { full, pre });
        source.Signed(full, key, ManifestText());
        source.Signed(pre, key, ManifestText("v0.7.0", "0.7.0", prerelease: true));
        AssertEx.Equal("v0.7.0", Check(source, key, includePrereleases: true).Release!.Tag);
        AssertEx.Equal("v0.6.0", Check(source, key, includePrereleases: false).Release!.Tag);
    }

    private static void CheckPageLimits()
    {
        using var key = new Key();
        IEnumerable<GitHubRelease> Old(int count, int start) =>
            Enumerable.Range(start, count).Select(i => Release("v0.4." + i));

        // The only eligible release is on page 2.
        var wanted = Release("v0.6.0");
        var source = new FakeSource();
        source.Page(1, Old(ReleaseCheck.PerPage, 0));
        source.Page(2, Old(5, 100).Append(wanted));
        source.Signed(wanted, key, ManifestText());
        AssertEx.Equal("v0.6.0", Check(source, key, false).Release!.Tag);
        AssertEx.False(source.Requested.Contains(ReleaseCheck.ReleasesPage(3).AbsoluteUri), "a short page ends the listing");

        // Never more than three pages.
        var deep = Release("v0.6.0");
        var many = new FakeSource();
        for (int page = 1; page <= 4; ++page)
            many.Page(page, page == 4 ? new[] { deep } : Old(ReleaseCheck.PerPage, page * 100));
        many.Signed(deep, key, ManifestText());
        AssertEx.Equal(ReleaseCheckOutcome.UpToDate, Check(many, key, false).Outcome);
        AssertEx.False(many.Requested.Contains(ReleaseCheck.ReleasesPage(4).AbsoluteUri));
    }

    private static void CheckCandidateLimit()
    {
        using var key = new Key();
        using var stranger = new Key();
        var releases = Enumerable.Range(1, 7).Select(i => Release("v0." + (5 + i) + ".0")).ToList();
        var source = new FakeSource();
        source.Page(1, releases);
        foreach (GitHubRelease release in releases)
        {
            string version = ReleaseVersion.Format(release.Version!);
            // Only the lowest one is properly signed, and it is the seventh candidate.
            source.Signed(release, key, ManifestText(release.Tag, version), signer: release.Tag == "v0.6.0" ? key : stranger);
        }
        ReleaseCheckResult result = Check(source, key, false);
        AssertEx.Equal(ReleaseCheckOutcome.NothingEligible, result.Outcome);
        AssertEx.Equal(ReleaseCheck.MaxCandidates, source.Requested.Count(uri => uri.EndsWith("/" + ReleaseCheck.ManifestAsset, StringComparison.Ordinal)));
    }

    private static void CheckReportsSpecialCases()
    {
        using var key = new Key();
        var limited = new FakeSource();
        limited.Responses[ReleaseCheck.ReleasesPage(1).AbsoluteUri] = ReleaseFetch.Fail("limit", 403, rateLimited: true);
        AssertEx.Equal(ReleaseCheck.RateLimitMessage, Check(limited, key, false).Message);

        var skippedSource = new FakeSource();
        var only = Release("v0.6.0");
        skippedSource.Page(1, new[] { only });
        skippedSource.Signed(only, key, ManifestText());
        ReleaseCheckResult skipped = Check(skippedSource, key, false, skipped: new Version(0, 6, 0));
        AssertEx.Equal(ReleaseCheckOutcome.UpToDate, skipped.Outcome);
        AssertEx.True(skipped.Message.Contains("skip"), skipped.Message);

        // A newer release that is published but not signed yet is named, never "up to date".
        var unsigned = new FakeSource();
        unsigned.Page(1, new[] { Release("v0.6.0", complete: false) });
        ReleaseCheckResult notReady = Check(unsigned, key, false);
        AssertEx.Equal(ReleaseCheckOutcome.NothingEligible, notReady.Outcome);
        AssertEx.True(notReady.Message.Contains("0.6.0") && notReady.Message.Contains("not ready"), notReady.Message);

        ReleaseCheckResult noKeys = ReleaseCheck.FindAsync(skippedSource, new Version(0, 5, 0), "x64", false, null,
            Array.Empty<string>(), CancellationToken.None).GetAwaiter().GetResult();
        AssertEx.Equal(ReleaseCheckOutcome.Failed, noKeys.Outcome);
        AssertEx.Equal(ReleaseCheckOutcome.Failed, Check(skippedSource, key, false, platform: string.Empty).Outcome);
        AssertEx.Equal(ReleaseCheckOutcome.Failed, Check(new FakeSource(), key, false).Outcome);
    }

    private static void RedirectPolicy()
    {
        var api = ReleaseCheck.ReleasesPage(1);
        var asset = new Uri(ReleaseCheck.DownloadPrefix + "v0.6.0/SecureWall_x64.msi");
        AssertEx.True(ReleaseCheck.IsAllowedEndpoint(api) && ReleaseCheck.IsAllowedEndpoint(asset));
        AssertEx.True(ReleaseCheck.TryFollowRedirect(asset, "https://release-assets.githubusercontent.com/github-production-release-asset/1?sig=x", 0, out Uri? next, out _));
        AssertEx.Equal("release-assets.githubusercontent.com", next!.Host);
        AssertEx.True(ReleaseCheck.TryFollowRedirect(api, "/repos/ryanportfolio/SecureWall/releases?page=2", 0, out Uri? relative, out _));
        AssertEx.Equal("api.github.com", relative!.Host);
        foreach (string location in new[]
        {
            "http://github.com/ryanportfolio/SecureWall/releases/download/v0.6.0/SecureWall_x64.msi",
            "https://evil.example/file.msi",
            "https://github.com.evil.example/file.msi",
            "https://github.com:8443/file.msi",
            "https://user:pass@github.com/file.msi",
            "file:///C:/Windows/notepad.exe",
            "",
        })
            AssertEx.False(ReleaseCheck.TryFollowRedirect(asset, location, 0, out _, out _), location);
        AssertEx.True(ReleaseCheck.TryFollowRedirect(asset, "https://github.com/x", ReleaseCheck.MaxRedirects - 1, out _, out _));
        AssertEx.False(ReleaseCheck.TryFollowRedirect(asset, "https://github.com/x", ReleaseCheck.MaxRedirects, out _, out _));

        // The client file holds no other URLs, and its hosts are GitHub's.
        AssertEx.SequenceEqual(new[] { "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" },
            ReleaseCheck.AllowedHosts);
    }

    private static void NotesArePlainText()
    {
        AssertEx.Equal("a\r\nb\tc", ReleaseCheck.PlainNotes("a\nb\tc\u0007\u202E"));
        string capped = ReleaseCheck.PlainNotes(new string('x', ReleaseCheck.MaxNotesChars * 2));
        AssertEx.True(capped.Length <= ReleaseCheck.MaxNotesChars + 6 && capped.EndsWith("[...]"));
        AssertEx.True(ReleaseCheck.PlainNotes(null).Length > 0);
    }

    private static void GrantDecisionFollowsMode()
    {
        AssertEx.Equal(UpdateGrantStatus.Granted, UpdateNetworkGrantPolicy.Decide(FirewallMode.Normal, false, false));
        AssertEx.Equal(UpdateGrantStatus.Blocked, UpdateNetworkGrantPolicy.Decide(FirewallMode.Normal, true, false));
        AssertEx.Equal(UpdateGrantStatus.Blocked, UpdateNetworkGrantPolicy.Decide(FirewallMode.BlockAll, false, false));
        AssertEx.Equal(UpdateGrantStatus.NotNeeded, UpdateNetworkGrantPolicy.Decide(FirewallMode.AllowOutgoing, false, false));
        AssertEx.Equal(UpdateGrantStatus.NotNeeded, UpdateNetworkGrantPolicy.Decide(FirewallMode.Disabled, false, false));
        AssertEx.Equal(UpdateGrantStatus.Failed, UpdateNetworkGrantPolicy.Decide(FirewallMode.Normal, false, true));
        AssertEx.Equal(UpdateGrantStatus.Blocked, UpdateNetworkGrantPolicy.Decide(FirewallMode.Unknown, false, false));
        AssertEx.True(UpdateNetworkGrantPolicy.ShouldInstall(true, false));
        AssertEx.False(UpdateNetworkGrantPolicy.ShouldInstall(true, true));
        AssertEx.False(UpdateNetworkGrantPolicy.ShouldInstall(false, false));
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Handle : IDisposable
    {
        internal bool Exited;
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private const string User = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OtherUser = "S-1-5-21-1111111111-2222222222-3333333333-1002";

    private static void LeaseLifetime()
    {
        var clock = new Clock();
        var leases = new UpdateNetworkLeases(clock);
        var handle = new Handle();
        UpdateNetworkLease lease = leases.Grant(100, 5000, User, handle, out var replaced);
        AssertEx.Equal(0, replaced.Count);
        AssertEx.Equal(clock.UtcNow + TimeSpan.FromMinutes(10), lease.ExpiresUtc);
        AssertEx.Equal(TimeSpan.FromMinutes(10), leases.Remaining(lease));

        bool Exited(UpdateNetworkLease l) => ((Handle)l.Process!).Exited;
        clock.UtcNow += TimeSpan.FromMinutes(9);
        AssertEx.Equal(0, leases.RemoveDue(Exited).Count);
        clock.UtcNow += TimeSpan.FromMinutes(1);
        AssertEx.Equal(1, leases.RemoveDue(Exited).Count, "expired after ten minutes");

        // A reused process ID with another creation time cannot end or inherit the lease.
        lease = leases.Grant(100, 5000, User, new Handle(), out _);
        AssertEx.Equal(0, leases.Release(100, 9999).Count);
        AssertEx.Equal(1, leases.Active.Count);
        AssertEx.Equal(1, leases.Release(100, 5000).Count);

        // The requesting process exits.
        var exiting = new Handle();
        leases.Grant(200, 1, User, exiting, out _);
        exiting.Exited = true;
        AssertEx.Equal(200u, leases.RemoveDue(Exited).Single().ProcessId);

        // A fresh grant for the same process replaces its lease; LOCK clears everything.
        leases.Grant(300, 1, User, new Handle(), out _);
        leases.Grant(300, 1, User, new Handle(), out var renewed);
        AssertEx.Equal(1, renewed.Count);
        for (uint pid = 400; pid < 410; ++pid)
            leases.Grant(pid, 1, OtherUser, new Handle(), out _);
        AssertEx.Equal(UpdateNetworkGrantPolicy.MaxLeases, leases.Active.Count);
        AssertEx.Equal(UpdateNetworkGrantPolicy.MaxLeases, leases.Clear().Count);
        AssertEx.Equal(0, leases.Active.Count);
        AssertEx.Throws<ArgumentException>(() => leases.Grant(500, 1, "S-1-5-18", new Handle(), out _));
    }

    private static void PermitNamesOnlyRequestingUser()
    {
        foreach (string account in new[] { "S-1-5-18", "S-1-5-19", "S-1-5-20" })
            AssertEx.False(UpdateNetworkGrantPolicy.IsAllowedUser(account), account);
        AssertEx.False(UpdateNetworkGrantPolicy.IsAllowedUser(null));
        AssertEx.False(UpdateNetworkGrantPolicy.IsAllowedUser("S-1-5-21-1)(A;;CC;;;WD"));
        AssertEx.True(UpdateNetworkGrantPolicy.IsAllowedUser(User));

        string sddl = UserIdFilterCondition.BuildSddl(new[] { User }, AiExplainEgressPolicy.DeniedUserSids);
        AssertEx.Equal("O:LSD:(D;;CC;;;S-1-5-18)(D;;CC;;;S-1-5-19)(D;;CC;;;S-1-5-20)(A;;CC;;;" + User + ")", sddl);

        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int start = service.IndexOf("private void InstallUpdatePermit(UpdateNetworkLease lease, string ntImagePath, LayerKeyEnum layer)", StringComparison.Ordinal);
        string permit = service.Substring(start, service.IndexOf("InstallWfpFilter(f, FilterGroup.Unknown);", start, StringComparison.Ordinal) - start);
        AssertEx.True(permit.Contains("new UserIdFilterCondition(new[] { lease.UserSid },\n                AiExplainEgressPolicy.DeniedUserSids, RemoteOrLocal.Local)"), "user condition");
        AssertEx.True(permit.Contains("new AppIdFilterCondition(ntImagePath, false, true)"), "SecureWall image only");
        AssertEx.True(permit.Contains("AiExplainEgressPolicy.TcpProtocol") && permit.Contains("AiExplainEgressPolicy.RemotePort, AiExplainEgressPolicy.RemotePort, RemoteOrLocal.Remote"), "TCP 443 only");
        AssertEx.True(permit.Contains("FWP_CONDITION_FLAG_IS_LOOPBACK"), "no loopback");
        AssertEx.True(permit.Contains("FilterWeights.UserPermit"), "user blocks still win");
    }

    private static void MessagesSitInPrivilegeRanges()
    {
        int grant = (int)MessageType.UPDATE_NETWORK_GRANT, release = (int)MessageType.UPDATE_NETWORK_RELEASE, review = (int)MessageType.UPDATE_NETWORK_REVIEW;
        AssertEx.True(grant > 2047 && grant < (int)MessageType.ALLOW_PROMPT, "the grant is password-gated");
        AssertEx.True(release > 1023 && release < 2048, "release works while locked");
        AssertEx.True(review > 4095, "review is service-internal");
        // Earlier values keep their numbers on the wire.
        AssertEx.Equal(2056, (int)MessageType.DATABASE_UPDATED);
        AssertEx.Equal(1027, (int)MessageType.IGNORE_PROMPT);
        AssertEx.Equal(4099, (int)MessageType.AUTO_ALLOW_SIGNED);
    }

    private static void ServiceRemovesPermits()
    {
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        string Body(string start, string end)
        {
            int from = service.IndexOf(start, StringComparison.Ordinal);
            AssertEx.True(from >= 0, start);
            return service.Substring(from, service.IndexOf(end, from + start.Length, StringComparison.Ordinal) - from);
        }
        AssertEx.True(Body("case MessageType.LOCK:", "return args.CreateResponse();").Contains("RemoveUpdateLeases(UpdateLeases.Clear());"), "LOCK");
        AssertEx.True(Body("ControllerActivity.LockIfExpired(", "Periodically reload").Contains("RemoveUpdateLeases(UpdateLeases.Clear());"), "inactivity lock");
        AssertEx.True(Body("ChildInheritedSubjectExes = previousInheritedSubjects;", "}").Contains("RemoveUpdateLeases(UpdateLeases.Clear());"), "failed reload");
        AssertEx.True(Body("private void FailClosedCore()", "PasswordLock.Locked = true;").Contains("UpdateLeases.Clear()"), "revocation");
        AssertEx.True(Body("var updatePermits = InstallUpdatePermits();", "CommittedRuleExecutables").Contains("CommitUpdatePermits(updatePermits);"), "full reload");
        // A lease that expired while the reload committed gets its new filters removed.
        AssertEx.True(Body("private void CommitUpdatePermits(", "// Enforcement thread only.").Contains("RemoveUpdateLeases(ended);"), "expiry during a reload");
        AssertEx.True(Body("private void RetainCommittedPolicy(", "ReloadRetry.RecordFailure").Contains("RemoveUpdateLeases(UpdateLeases.Clear());"), "retained failed reload");
        AssertEx.True(Body("case MessageType.UPDATE_NETWORK_GRANT:", "GrantUpdateNetwork(").Contains("if (PasswordLock.Locked)\n                            return TwMessageLocked.Instance;"), "locked since the pipe check");
        AssertEx.True(service.Contains("UpdateNetworkLeases UpdateLeases = new(new MonotonicClock());"), "expiry follows elapsed time");
        AssertEx.True(Body("private List<KeyValuePair<UpdateNetworkLease, List<Guid>>> InstallUpdatePermits()", "foreach").Contains("UpdateNetworkGrantPolicy.ShouldInstall(PolicyMode == FirewallMode.Normal, displayOffBlockActive)"), "mode and display");
        AssertEx.True(Body("private void RemoveUpdateLeases(", "private void InstallRawSocketPermits").Contains("FailClosed();"), "a permit that cannot be removed withdraws everything");
        AssertEx.True(Body("private UpdateGrantStatus GrantUpdateNetwork(", "private void ReleaseUpdateNetwork").Contains("UpdateNetworkClient.TryOpen(clientProcessId, Utils.ExecutablePath"), "identity from the pipe client");
        AssertEx.False(Body("private UpdateGrantStatus GrantUpdateNetwork(", "private void ReleaseUpdateNetwork").Contains("Save("), "never saved");
        string pipe = PromptTransactionIntegrationTests.Source("TinyWall/PipeServerEndpoint.cs");
        AssertEx.True(pipe.Contains("AuthAsServer(pipeServer, out uint clientPid)") && pipe.Contains("m_RcvCallback(req, clientPid)"));
    }

    private const string Root = @"C:\Users\alice\AppData\Local\SecureWall\updates";

    private static void ApplyArgumentsAndPaths()
    {
        AssertEx.True(UpdateApplyPolicy.TryParseArguments(new[] { "/apply-update", Root + @"\v0.6.0\a.msi", "m", "s", User }, out var args));
        AssertEx.Equal(User, args!.UserSid);
        AssertEx.False(UpdateApplyPolicy.TryParseArguments(new[] { "/apply-update", "a", "m", "s" }, out _));
        AssertEx.False(UpdateApplyPolicy.TryParseArguments(new[] { "/apply-update", "a", "m", "s", "S-1-5-18" }, out _));
        AssertEx.False(UpdateApplyPolicy.TryParseArguments(new[] { "/service", "a", "m", "s", User }, out _));
        AssertEx.Equal(Root, UpdateApplyPolicy.UserUpdatesRoot(@"C:\Users\alice\AppData\Local"));

        AssertEx.True(UpdateApplyPolicy.IsAcceptableSourcePath(Root + @"\v0.6.0\SecureWall_x64.msi", Root, out string error), error);
        AssertEx.True(UpdateApplyPolicy.IsAcceptableSourcePath(@"c:\users\ALICE\appdata\local\securewall\updates\v0.6.0\SHA256SUMS.txt", Root, out _));
        foreach (string path in new[]
        {
            @"\\server\share\SecureWall_x64.msi",
            @"\\?\C:\Users\alice\AppData\Local\SecureWall\updates\v0.6.0\a.msi",
            @"\\.\C:\Users\alice\AppData\Local\SecureWall\updates\v0.6.0\a.msi",
            @"SecureWall\updates\v0.6.0\a.msi",
            Root + @"\v0.6.0\..\..\..\..\Temp\a.msi",
            Root + @"\.\a.msi",
            Root + @"\v0.6.0\a.msi:stream",
            Root + @"\v0.6.0\a.msi.",
            Root + @"\v0.6.0\a.msi ",
            Root + @"\v0.6.0\\a.msi",
            Root + "/v0.6.0/a.msi",
            Root,
            Root + @"\",
            @"C:\Users\bob\AppData\Local\SecureWall\updates\v0.6.0\a.msi",
            @"C:\Users\alice\AppData\Local\SecureWall\updates-other\a.msi",
            @"C:\Windows\Temp\a.msi",
        })
            AssertEx.False(UpdateApplyPolicy.IsAcceptableSourcePath(path, Root, out _), path);
        AssertEx.True(UpdateApplyPolicy.IsReleaseTag("v0.6.0") && !UpdateApplyPolicy.IsReleaseTag("v0.6.0/.."));
    }

    private static void ApplyPackageChecks()
    {
        var version = new Version(0, 6, 0);
        bool Matches(string? code = UpdateApplyPolicy.UpgradeCode, string? template = "x64;1033", string? productVersion = "0.6.0.0",
            bool faultTest = false, string platform = "x64") =>
            UpdateApplyPolicy.PackageMatches(code, template, productVersion, faultTest, version, platform, out _);
        AssertEx.True(Matches());
        AssertEx.True(Matches(code: UpdateApplyPolicy.UpgradeCode.ToLowerInvariant()));
        AssertEx.True(Matches(template: "Arm64;1033", platform: "arm64"));
        AssertEx.True(Matches(template: "Intel;1033", platform: "x86"));
        AssertEx.False(Matches(code: "{00000000-0000-0000-0000-000000000000}"), "another product");
        AssertEx.False(Matches(template: "Intel;1033"), "another platform");
        AssertEx.False(Matches(template: null), "no template");
        AssertEx.False(Matches(productVersion: "0.6.1.0"), "another version");
        AssertEx.False(Matches(productVersion: "0.6.0"), "three-field ProductVersion");
        AssertEx.False(Matches(faultTest: true), "fault-test build");
        string product = PromptTransactionIntegrationTests.Source("MsiSetup/Product.wxs");
        AssertEx.True(product.Contains("UpgradeCode=\"" + UpdateApplyPolicy.UpgradeCode + "\""), "UpgradeCode constant matches Product.wxs");
    }

    private static void ApplyStagingAndLaunch()
    {
        var created = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        AssertEx.False(UpdateApplyPolicy.IsExpiredStaging(created, created.AddHours(23.9)));
        AssertEx.True(UpdateApplyPolicy.IsExpiredStaging(created, created.AddHours(24.1)));
        AssertEx.Equal("20261101-120000-" + Guid.Empty.ToString("N"), UpdateApplyPolicy.NewStagingName(created, Guid.Empty));
        // Only SYSTEM and Administrators, protected from inheritance.
        AssertEx.Equal("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", UpdateApplyPolicy.StagingSddl);
        AssertEx.Equal("/i \"C:\\p\\SecureWall.msi\" /l*v \"C:\\p\\install.log\"",
            UpdateApplyPolicy.MsiexecArguments(@"C:\p\SecureWall.msi", @"C:\p\install.log", false));
        AssertEx.Equal("/i \"C:\\p\\SecureWall.msi\" /l*v \"C:\\p\\install.log\" SECUREWALL_SUPPRESS_USER_ACTIONS=1",
            UpdateApplyPolicy.MsiexecArguments(@"C:\p\SecureWall.msi", @"C:\p\install.log", true));

        string applier = PromptTransactionIntegrationTests.Source("TinyWall/Installer/UpdateApplier.cs");
        int verify = applier.IndexOf("SignedReleaseManifest manifest = Verify(manifestBytes, signatureBytes);", StringComparison.Ordinal);
        int stage = applier.IndexOf("string parent = PrepareStagingParent();", StringComparison.Ordinal);
        int reverify = applier.IndexOf("SignedReleaseManifest staged = Verify(File.ReadAllBytes(stagedManifest)", StringComparison.Ordinal);
        int package = applier.IndexOf("CheckPackage(stagedPackage, staged.Version, platform);", StringComparison.Ordinal);
        int launch = applier.IndexOf("Path.Combine(Environment.SystemDirectory, \"msiexec.exe\")", StringComparison.Ordinal);
        AssertEx.True(verify > 0 && verify < stage && stage < reverify && reverify < package && package < launch, "verify, stage, verify again, check the package, then launch");
        AssertEx.True(applier.Contains("identity.IsSystem") && applier.Contains("WindowsBuiltInRole.Administrator"), "never as SYSTEM, only elevated");
        // The requesting user is the session's user; the suppression compares against it.
        AssertEx.True(applier.Contains("if (!string.Equals(sessionSid, request.UserSid, StringComparison.OrdinalIgnoreCase))"), "session user check");
        AssertEx.True(applier.Contains("suppressUserActions: !string.Equals(helperSid, sessionSid, StringComparison.OrdinalIgnoreCase)"), "suppress by session user");

        string product = PromptTransactionIntegrationTests.Source("MsiSetup/Product.wxs");
        AssertEx.True(product.Contains("<Property Id=\"SECUREWALL_SUPPRESS_USER_ACTIONS\" Secure=\"yes\" />"));
    }

    private static void EmbeddedKeysMatchFiles()
    {
        string KeyFile(string name)
        {
            string path = PromptTransactionIntegrationTests.SourcePath("tools/release/keys/" + name + ".pub");
            AssertEx.True(File.Exists(path), "tools/release/keys/" + name + ".pub is missing; run tools/release/New-SecureWallReleaseKeys.ps1");
            return SignedReleaseManifest.ReadPublicKeyFile(File.ReadAllText(path)) ?? throw new InvalidOperationException(name + ".pub has no single key line.");
        }
        AssertEx.Equal(KeyFile("primary"), ReleaseKeys.Primary);
        AssertEx.Equal(KeyFile("backup"), ReleaseKeys.Backup);
        AssertEx.SequenceEqual(new[] { ReleaseKeys.Primary, ReleaseKeys.Backup }, ReleaseKeys.Trusted.ToArray());
        foreach (string key in ReleaseKeys.Trusted)
            AssertEx.True(SignedReleaseManifest.TryDecodePublicKey(key, out ECParameters parameters) && ECDsa.Create(parameters).KeySize == 256, "valid P-256 point");
        string testPath = PromptTransactionIntegrationTests.SourcePath("tools/release/keys/test.pub");
        if (File.Exists(testPath))
        {
            string test = SignedReleaseManifest.ReadPublicKeyFile(File.ReadAllText(testPath))!;
            AssertEx.False(PromptTransactionIntegrationTests.Source("TinyWall/Prompting/ReleaseKeys.cs").Contains(test), "the updater test key is never trusted by product builds");
        }
    }

    private static void ProtocolSelfTestCoversUpdateMessages()
    {
        string program = PromptTransactionIntegrationTests.Source("TinyWall/Program.cs");
        AssertEx.True(program.Contains("TwMessageUpdateNetwork.CreateGrantRequest(),") && program.Contains("TwMessageUpdateNetwork.CreateReleaseRequest(),"));
        string message = PromptTransactionIntegrationTests.Source("TinyWall/Message.cs");
        AssertEx.True(message.Contains("MessageType.UPDATE_NETWORK_GRANT => (TwMessage?)JsonSerializer.Deserialize<TwMessageUpdateNetwork>"));
        AssertEx.True(message.Contains("MessageType.UPDATE_NETWORK_RELEASE => (TwMessage?)JsonSerializer.Deserialize<TwMessageUpdateNetwork>"));
        AssertEx.False(message.Contains("MessageType.UPDATE_NETWORK_REVIEW =>"), "the service-internal review never arrives over the pipe");
    }
}

using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using pylorak.TinyWall;

namespace SecureWall.Core.Tests;

// The TinyWall updater stays deleted: no TinyWall feed host, update descriptor, /updatenow
// switch or WebClient download anywhere in the product, and legacy settings that still carry
// its AutoUpdateCheck flag load with the flag ignored. SecureWall's own Check for updates
// reads only GitHub releases of ryanportfolio/SecureWall, from the controller, through the
// two files named below; the service and installer code never reach the release client.
internal static class UpdateFeedRemovalTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("product sources contain no update feed host or update download path", ProductSourcesHaveNoUpdateFeed),
        ("HttpClient appears only in the AI client and the release client", HttpClientOnlyInNamedClients),
        ("service and installer sources never reference the release client", ServiceNeverReferencesReleaseClient),
        ("release client URLs are GitHub's only", ReleaseClientUrlsAreGitHubOnly),
        ("product sources link to SecureWall, not the upstream TinyWall repository", ProductSourcesHaveNoUpstreamLinks),
        ("legacy JSON settings with AutoUpdateCheck deserialize and ignore it", LegacyJsonAutoUpdateCheck),
        ("legacy XML settings with AutoUpdateCheck deserialize and ignore it", LegacyXmlAutoUpdateCheck),
    };

    private static readonly string[] ProductDirectories =
    {
        "TinyWall", "pylorak.Utilities", "pylorak.Windows", "pylorak.Windows.Services",
        "pylorak.Windows.WFP", "Microsoft.Samples", "MsiSetup",
    };

    private static readonly string[] TextExtensions =
    {
        ".cs", ".resx", ".csproj", ".wxs", ".wixproj", ".wxl", ".ps1", ".json", ".xml",
        ".html", ".txt", ".config", ".manifest", ".bck",
    };

    // The TinyWall feed host, its descriptor path, the deleted updater types and switch,
    // and the WebClient download calls that fetched descriptors, installers and data files.
    private static readonly string[] ForbiddenPatterns =
    {
        "pados.hu", "UpdVer", "update.json", "UpdateChecker", "UpdateDescriptor", "UpdateModule",
        "UpdaterMethod", "GetCompressedUpdate", "/updatenow", "UpdateFeedEnabled", "AutoUpdateCheck",
        "WebClient", "DownloadFile", "DownloadData", "DownloadString",
    };

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TinyWall", "TinyWallService.cs"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository source not found.");
    }

    // Links in the product point at SecureWall's own repository.
    private static readonly string[] UpstreamLinkPatterns = { "github.com/pylorak" };

    private static void ProductSourcesHaveNoUpdateFeed() =>
        AssertNoProductMatches(ForbiddenPatterns, "Update feed code remains");

    // The only product files that may make HTTP requests.
    private static readonly string[] HttpClientFiles =
    {
        Path.Combine("TinyWall", "OpenAiCompatibleExplainClient.cs"),
        Path.Combine("TinyWall", "ReleaseCheckClient.cs"),
    };

    private static void HttpClientOnlyInNamedClients()
    {
        string root = RepositoryRoot();
        var hits = ProductFiles(root)
            .Where(file => !HttpClientFiles.Contains(Path.GetRelativePath(root, file), StringComparer.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains("HttpClient", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file)).ToList();
        AssertEx.True(hits.Count == 0, "HttpClient outside the named clients:\n" + string.Join("\n", hits));
        foreach (string file in HttpClientFiles)
            AssertEx.True(File.ReadAllText(Path.Combine(root, file)).Contains("new HttpClient(", StringComparison.Ordinal), file);
    }

    private static void ServiceNeverReferencesReleaseClient()
    {
        string root = RepositoryRoot();
        var serviceSide = new List<string>
        {
            Path.Combine(root, "TinyWall", "TinyWallService.cs"),
            Path.Combine(root, "TinyWall", "TinyWallDoctor.cs"),
            Path.Combine(root, "TinyWall", "UpdateNetworkClient.cs"),
        };
        serviceSide.AddRange(Directory.GetFiles(Path.Combine(root, "TinyWall", "Installer"), "*.cs"));
        foreach (string file in serviceSide)
        {
            string text = File.ReadAllText(file);
            foreach (string type in new[] { "ReleaseCheck", "IReleaseSource", "GitHubRelease", "HttpClient", "System.Net.Http" })
                AssertEx.False(text.Contains(type, StringComparison.Ordinal), Path.GetRelativePath(root, file) + " references " + type);
        }
    }

    private static void ReleaseClientUrlsAreGitHubOnly()
    {
        string root = RepositoryRoot();
        var allowed = new[] { "https://api.github.com/", "https://github.com/", "https://objects.githubusercontent.com/", "https://release-assets.githubusercontent.com/" };
        foreach (string file in new[] { Path.Combine("TinyWall", "ReleaseCheckClient.cs"), Path.Combine("TinyWall", "Prompting", "ReleaseCheck.cs") })
        {
            string text = File.ReadAllText(Path.Combine(root, file));
            var urls = System.Text.RegularExpressions.Regex.Matches(text, "[a-z]+://[^\"\\s]*").Select(m => m.Value).ToList();
            foreach (string url in urls)
                AssertEx.True(allowed.Any(prefix => url.StartsWith(prefix, StringComparison.Ordinal)) ||
                    url == "https://api.github.com" || url == "https://github.com", file + ": " + url);
            AssertEx.False(text.Contains("http://", StringComparison.OrdinalIgnoreCase), file + " names a plain HTTP URL");
        }
        AssertEx.True(pylorak.TinyWall.Prompting.ReleaseCheck.ReleasesApi.StartsWith("https://api.github.com/repos/ryanportfolio/SecureWall/", StringComparison.Ordinal));
        AssertEx.True(pylorak.TinyWall.Prompting.ReleaseCheck.DownloadPrefix.StartsWith("https://github.com/ryanportfolio/SecureWall/releases/download/", StringComparison.Ordinal));
    }

    private static IEnumerable<string> ProductFiles(string root)
    {
        foreach (string directory in ProductDirectories)
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories))
            {
                string[] parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
                if (parts.Contains("bin") || parts.Contains("obj")) continue;
                if (TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    yield return file;
            }
    }

    private static void ProductSourcesHaveNoUpstreamLinks() =>
        AssertNoProductMatches(UpstreamLinkPatterns, "Upstream TinyWall links remain");

    private static void AssertNoProductMatches(string[] patterns, string failure)
    {
        string root = RepositoryRoot();
        var hits = new List<string>();
        int scanned = 0;
        foreach (string directory in ProductDirectories)
        {
            string top = Path.Combine(root, directory);
            AssertEx.True(Directory.Exists(top), "Product directory missing: " + directory);
            foreach (string file in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);
                string[] parts = relative.Split(Path.DirectorySeparatorChar);
                if (parts.Contains("bin") || parts.Contains("obj")) continue;
                if (!TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                scanned++;
                string text = File.ReadAllText(file);
                foreach (string pattern in patterns)
                    if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        hits.Add(relative + ": " + pattern);
            }
        }
        AssertEx.True(scanned > 100, "Source scan found too few product files: " + scanned);
        AssertEx.True(hits.Count == 0, "Update feed code remains:\n" + string.Join("\n", hits));
    }

    private static void LegacyJsonAutoUpdateCheck()
    {
        // The product's source-generated context keeps System.Text.Json's default of
        // skipping unmapped members; these options mirror its field and null handling.
        string helper = PromptTransactionIntegrationTests.Source("TinyWall/SerializationHelper.cs");
        AssertEx.False(helper.Contains("UnmappedMemberHandling"));
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        const string legacy = "{\"ActiveProfileName\":\"Default\",\"AutoUpdateCheck\":true,\"LockHostsFile\":false,\"EnableDiagnosticLogging\":true}";
        ServerConfiguration? config = JsonSerializer.Deserialize<ServerConfiguration>(legacy, options);
        AssertEx.True(config != null);
        AssertEx.False(config!.LockHostsFile);
        AssertEx.True(config.EnableDiagnosticLogging);
        AssertEx.Equal("Default", config.ActiveProfileName);
        AssertEx.False(JsonSerializer.Serialize(config, options).Contains("AutoUpdateCheck"));
    }

    private static void LegacyXmlAutoUpdateCheck()
    {
        // Pre-3.0 configurations and exports load through DataContractSerializer
        // (SerializationHelper.DeserializeDC). Unknown elements are skipped.
        const string legacy =
            "<ServerConfiguration xmlns=\"TinyWall\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            "<AutoUpdateCheck>true</AutoUpdateCheck>" +
            "<EnableDiagnosticLogging>true</EnableDiagnosticLogging>" +
            "<LockHostsFile>false</LockHostsFile>" +
            "</ServerConfiguration>";
        var serializer = new DataContractSerializer(typeof(ServerConfiguration));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(legacy));
        var config = (ServerConfiguration?)serializer.ReadObject(stream);
        AssertEx.True(config != null);
        AssertEx.False(config!.LockHostsFile);
        AssertEx.True(config.EnableDiagnosticLogging);
    }
}

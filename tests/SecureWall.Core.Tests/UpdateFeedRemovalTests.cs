using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using pylorak.TinyWall;

namespace SecureWall.Core.Tests;

// SecureWall has no update feed. The TinyWall updater was deleted, and legacy
// settings that still carry its AutoUpdateCheck flag must load with the flag ignored.
internal static class UpdateFeedRemovalTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("product sources contain no update feed host or update download path", ProductSourcesHaveNoUpdateFeed),
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

    private static void ProductSourcesHaveNoUpdateFeed()
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
                foreach (string pattern in ForbiddenPatterns)
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

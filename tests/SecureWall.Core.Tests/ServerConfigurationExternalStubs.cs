using System.Text.Json.Serialization.Metadata;

// The real ServerConfiguration, FirewallException and ExceptionSubject are linked into this
// harness to test profile merging. These doubles cover what those files reference for
// serialization, file hashing, signatures, path resolution and UWP packages; merge tests
// use none of it, so every member throws.
namespace pylorak.TinyWall
{
    public interface ISerializable<T>
    {
        JsonTypeInfo<T> GetJsonTypeInfo();
    }

    internal static class SerializationHelper
    {
        internal static T DeserializeFromEncryptedFile<T>(string filepath, string key, string iv, T defInst) where T : ISerializable<T> =>
            throw ServerConfigurationStubs.Forbidden();

        internal static void SerializeToEncryptedFile<T>(T obj, string filePath, string key, string iv) where T : ISerializable<T> =>
            throw ServerConfigurationStubs.Forbidden();
    }

    public class UwpPackageList
    {
        public sealed class Package
        {
            public string Sid => throw ServerConfigurationStubs.Forbidden();
            public string Name => throw ServerConfigurationStubs.Forbidden();
            public string Publisher => throw ServerConfigurationStubs.Forbidden();
            public string PublisherId => throw ServerConfigurationStubs.Forbidden();
        }
    }

    internal static class ServerConfigurationStubs
    {
        internal static InvalidOperationException Forbidden() =>
            new("Serialization, file, signature and package access are outside the core merge tests.");
    }
}

namespace pylorak.TinyWall.Parser
{
    public static class RecursiveParser
    {
        public static string ResolveString(string str) => throw pylorak.TinyWall.ServerConfigurationStubs.Forbidden();
    }
}

namespace pylorak.Windows
{
    public static class NetworkPath
    {
        public static bool IsNetworkPath(string path) => throw pylorak.TinyWall.ServerConfigurationStubs.Forbidden();
        public static bool IsUncPath(string path) => throw pylorak.TinyWall.ServerConfigurationStubs.Forbidden();
        public static string GetUncPath(string path) => throw pylorak.TinyWall.ServerConfigurationStubs.Forbidden();
    }

    public static class WinTrust
    {
        public enum VerifyResult
        {
            SIGNATURE_VALID,
            SIGNATURE_MISSING,
        }

        public static VerifyResult VerifyFileAuthenticode(string filePath) => throw pylorak.TinyWall.ServerConfigurationStubs.Forbidden();
    }
}

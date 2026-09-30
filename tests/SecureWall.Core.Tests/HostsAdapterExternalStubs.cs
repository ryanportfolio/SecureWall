// The real HostsFileManager and FileLocker are linked into this harness. These
// external adapters must never be used: tests supply fixture paths and DNS callbacks.
namespace pylorak.TinyWall
{
    internal static class Utils
    {
        internal static string AppDataPath => throw new InvalidOperationException("Native machine-data access is forbidden in core tests.");
        internal static bool TryFlushDnsCache() => throw new InvalidOperationException("Native DNS mutation is forbidden in core tests.");
    }

    internal static class Hasher
    {
        internal static string HashFileSha1(string path) => throw new InvalidOperationException("Executable hashing is outside the core tests.");
        internal static string HashString(string text) => throw new InvalidOperationException("Configuration encryption is outside the core tests.");
    }
}

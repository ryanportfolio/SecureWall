// The real HostsFileManager and FileLocker are linked into this harness. These
// external adapters must never be used: tests supply fixture paths and DNS callbacks.
namespace pylorak.TinyWall
{
    internal static class Utils
    {
        internal static string AppDataPath => throw new InvalidOperationException("Native machine-data access is forbidden in core tests.");
        internal static void FlushDnsCache() => throw new InvalidOperationException("Native DNS mutation is forbidden in core tests.");
    }

    internal static class Hasher
    {
        internal static string HashFile(string path) => throw new InvalidOperationException("Native hosts hash adapter is outside this fixture test.");
    }
}

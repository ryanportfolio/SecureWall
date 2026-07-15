using System;

namespace pylorak.TinyWall.Prompting
{
    internal static class PipeClientAuthorization
    {
        internal static bool IsExpectedExecutable(string? clientPath, string? serverPath) =>
            !string.IsNullOrWhiteSpace(clientPath) &&
            !string.IsNullOrWhiteSpace(serverPath) &&
            string.Equals(clientPath, serverPath, StringComparison.OrdinalIgnoreCase);
    }
}

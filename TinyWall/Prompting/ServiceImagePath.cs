using System;
using System.IO;

namespace pylorak.TinyWall.Prompting
{
    internal static class ServiceImagePath
    {
        internal static string? TryExtractExecutable(string? imagePath, string windowsDirectory)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(windowsDirectory))
                return null;

            string command = Environment.ExpandEnvironmentVariables(imagePath!).Trim();
            if (command.StartsWith(@"\??\", StringComparison.Ordinal))
                command = command.Substring(4);

            string executable;
            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                int closingQuote = command.IndexOf('\"', 1);
                if (closingQuote <= 1)
                    return null;
                executable = command.Substring(1, closingQuote - 1);
            }
            else
            {
                int extension = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (extension < 0)
                    return null;
                executable = command.Substring(0, extension + 4).Trim();
            }

            if (executable.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                executable = Path.Combine(windowsDirectory, executable.Substring(12));
            else if (executable.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
                executable = Path.Combine(windowsDirectory, executable);
            else if (!Path.IsPathRooted(executable))
                return null;

            try
            {
                return Path.GetFullPath(executable).TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is PathTooLongException)
            {
                return null;
            }
        }
    }
}

using System;
using System.IO;

namespace pylorak.TinyWall.Prompting
{
    internal static class ServiceImagePath
    {
        // The SCM passes ImagePath to CreateProcess as a command line with no separate
        // application name. A quoted first token names the executable exactly. An unquoted
        // command is split at whitespace, and CreateProcess tries each prefix in turn
        // ("C:\Program Files\x.exe" first tries "C:\Program.exe"), so which file runs depends
        // on what exists on disk. Resolve only commands whose first whitespace-delimited token
        // is the executable without doubt; return null when the start point is uncertain so
        // the caller treats the registration as unresolved instead of guessing.
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
                int end = IndexOfWhitespace(command, 0);
                executable = end < 0 ? command : command.Substring(0, end);
                bool endsWithExe = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                // Only a ".exe" token, or a whole command without an extension, is known.
                if (!endsWithExe && (end >= 0 || !HasNoExtension(executable)))
                    return null;
                // After a ".exe" first token, a later ".exe" means a longer prefix could
                // also be the executable ("C:\a.exe dir\b.exe").
                if (end >= 0 && command.IndexOf(".exe", end, StringComparison.OrdinalIgnoreCase) >= 0)
                    return null;
            }

            executable = executable.Trim();
            if (executable.Length == 0 || executable.EndsWith(".", StringComparison.Ordinal))
                return null;
            // CreateProcess appends ".exe" to a command-line file name without an extension.
            if (HasNoExtension(executable))
                executable += ".exe";

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

        // Manual check: Path.GetExtension throws on some invalid characters in .NET Framework.
        private static bool HasNoExtension(string path)
        {
            int separator = path.LastIndexOfAny(new[] { '\\', '/' });
            return path.IndexOf('.', separator + 1) < 0;
        }

        private static int IndexOfWhitespace(string value, int start)
        {
            for (int i = start; i < value.Length; ++i)
            {
                if (char.IsWhiteSpace(value[i]))
                    return i;
            }
            return -1;
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;

namespace pylorak.TinyWall
{
    /// <summary>
    /// Decisions behind the Network Activity "Copy path" and "Open folder" menu items.
    /// Kept free of UI and file system access so they can be tested.
    /// </summary>
    internal static class ProcessPathActions
    {
        private const string KernelSubject = "System";

        internal static bool CanCopyPath(string? path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && !string.Equals(path, KernelSubject, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True for a drive-absolute (C:\...) or UNC (\\server\share\...) path that Explorer can show.
        /// "System", NT device paths (\Device\...), relative paths and the \\?\ and \\.\ namespaces are rejected,
        /// as is any character that could break out of the quoted Explorer argument.
        /// </summary>
        internal static bool IsFileSystemPath(string? path)
        {
            if (path is null || path.Length < 3)
                return false;

            foreach (char c in path)
            {
                if ((c < ' ') || (c == '"') || (c == '<') || (c == '>') || (c == '|') || (c == '*') || (c == '?'))
                    return false;
            }

            bool driveAbsolute = (((path[0] >= 'A') && (path[0] <= 'Z')) || ((path[0] >= 'a') && (path[0] <= 'z')))
                && (path[1] == ':')
                && (path[2] == '\\');
            bool unc = (path[0] == '\\') && (path[1] == '\\') && (path[2] != '\\') && (path[2] != '.');
            return driveAbsolute || unc;
        }

        /// <summary>
        /// Builds the start info that shows <paramref name="path"/> in Explorer without executing it.
        /// An existing file is selected in its folder. If the file is gone but its folder remains, the folder
        /// is opened through the "explore" verb, which only folders register, so a path that turned into a
        /// file in the meantime fails instead of running. Returns null when there is nothing to show.
        /// </summary>
        internal static ProcessStartInfo? CreateOpenFolderStartInfo(
            string? path,
            string windowsDirectory,
            Func<string, bool> fileExists,
            Func<string, bool> directoryExists)
        {
            if (!IsFileSystemPath(path))
                return null;

            if (fileExists(path!))
            {
                return new ProcessStartInfo(Path.Combine(windowsDirectory, "explorer.exe"), "/select,\"" + path + "\"")
                {
                    UseShellExecute = false,
                };
            }

            string? folder = Path.GetDirectoryName(path);
            if (IsFileSystemPath(folder) && directoryExists(folder!))
            {
                return new ProcessStartInfo(folder!)
                {
                    UseShellExecute = true,
                    Verb = "explore",
                };
            }

            return null;
        }
    }
}

using System;
using System.IO;

namespace pylorak.TinyWall
{
    /// <summary>
    /// Replaces a file's content in one step. The new content is written to a randomly named
    /// temporary file in the target's own directory, so it inherits that directory's ACL and
    /// never touches a shared temp folder, then swaps in over the target. Readers see either
    /// the old file or the new one, never a partially written one. The temporary file is
    /// removed whether the write succeeds or fails.
    /// </summary>
    internal static class AtomicFileWriter
    {
        public static void Write(string targetPath, Action<Stream> writeContent)
        {
            if (targetPath is null)
                throw new ArgumentNullException(nameof(targetPath));
            if (writeContent is null)
                throw new ArgumentNullException(nameof(writeContent));

            string fullTarget = Path.GetFullPath(targetPath);
            string targetDir = Path.GetDirectoryName(fullTarget) ?? throw new ArgumentException("Target path has no directory.", nameof(targetPath));
            string tempPath = Path.Combine(targetDir, Path.GetRandomFileName());

            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    writeContent(stream);
                    stream.Flush(true);
                }

                Replace(tempPath, fullTarget);
            }
            finally
            {
                try { File.Delete(tempPath); }
                catch { }
            }
        }

        public static void WriteFrom(string targetPath, Stream source)
        {
            if (source is null)
                throw new ArgumentNullException(nameof(source));

            Write(targetPath, stream => source.CopyTo(stream));
        }

        public static void CopyFrom(string targetPath, string sourcePath)
        {
            Write(targetPath, stream =>
            {
                using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                source.CopyTo(stream);
            });
        }

        private static void Replace(string tempPath, string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath);
                return;
            }

            // File.Replace needs delete access to the target, which the read-only
            // attribute denies. Clear it for the swap and put it back on whichever
            // file ends up at the target path.
            var attributes = File.GetAttributes(targetPath);
            bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;
            if (readOnly)
                File.SetAttributes(targetPath, attributes & ~FileAttributes.ReadOnly);

            try
            {
                File.Replace(tempPath, targetPath, null, true);
            }
            finally
            {
                if (readOnly)
                {
                    try { File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly); }
                    catch { }
                }
            }
        }
    }
}

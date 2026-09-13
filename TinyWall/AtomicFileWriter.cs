using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace pylorak.TinyWall
{
    /// <summary>
    /// Replaces a file's content in one step. The new content is written to a randomly named
    /// temporary file in the target's own directory, so it inherits that directory's ACL and
    /// never touches a shared temp folder, then swaps in over the target. Readers see either
    /// the old file or the new one, never a partially written one. The temporary file is
    /// retained when a failed replacement needs recovery evidence. Content is flushed before replacement
    /// and the installed file is flushed afterward. A post-swap failure can leave the new
    /// content installed. This does not promise power-loss durability on every filesystem
    /// or storage device; callers must retain recovery state when a write fails.
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

            bool handedToReplacement = false;
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    writeContent(stream);
                    stream.Flush(true);
                }

                handedToReplacement = true;
                pylorak.Utilities.AtomicFileReplacement.Install(tempPath, fullTarget);
            }
            finally
            {
                try { if (!handedToReplacement) File.Delete(tempPath); }
                catch { }
            }
        }

        public static void WriteEncrypted(string targetPath, Action<Stream> writeContent, string key, string iv)
        {
            if (writeContent is null)
                throw new ArgumentNullException(nameof(writeContent));
            using var algorithm = Aes.Create();
            algorithm.Mode = CipherMode.CBC;
            algorithm.Key = Encoding.ASCII.GetBytes(key);
            algorithm.IV = Encoding.ASCII.GetBytes(iv);
            Write(targetPath, stream =>
            {
                // Disposal emits final padding before Write flushes the underlying file.
                using var crypto = new CryptoStream(stream, algorithm.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
                writeContent(crypto);
            });
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

    }
}

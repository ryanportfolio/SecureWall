using System;
using System.IO;

namespace pylorak.Utilities
{
    public sealed class AtomicFileUpdater : Disposable
    {
        public AtomicFileUpdater(string targetFile)
        {
            // File.Replace needs the target and temporary files to be on the same volume.
            // To ensure this, we create our temporary file in the same folder as our target.
            TargetFilePath = Path.GetFullPath(targetFile);
            TemporaryFilePath = RandomFileInSameDir(TargetFilePath);
        }

        private static string RandomFileInSameDir(string file)
        {
            string targetDir = Path.GetDirectoryName(file) ?? throw new ArgumentException("Target path has no directory.", nameof(file));
            return Path.Combine(targetDir, Path.GetRandomFileName());
        }

        public string TemporaryFilePath { get; }
        public string TargetFilePath { get; }

        public void Commit()
        {
            if (replacementOwnsTemporary)
                throw new InvalidOperationException("A replacement was already attempted; retain its recovery evidence.");

            // Finalize and close all payload writers before Commit.
            using (var temporary = new FileStream(TemporaryFilePath, FileMode.Open, FileAccess.Write, FileShare.None))
                temporary.Flush(true);

            replacementOwnsTemporary = true;
            AtomicFileReplacement.Install(TemporaryFilePath, TargetFilePath);
        }

        private bool replacementOwnsTemporary;
        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            if (disposing)
            {
                try
                {
                    if (!replacementOwnsTemporary)
                        File.Delete(TemporaryFilePath);
                }
                catch { }
            }

            base.Dispose(disposing);
        }
    }
}

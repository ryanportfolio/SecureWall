using System;
using System.IO;

namespace pylorak.Utilities
{
    // Callers must protect the parent directory and serialize their own writes.
    // This is process-error recovery, not a power-loss transaction protocol.
    public static class AtomicFileReplacement
    {
        internal interface IStorage
        {
            bool Exists(string path);
            FileAttributes GetAttributes(string path);
            void SetAttributes(string path, FileAttributes attributes);
            void Replace(string temporary, string target, string backup);
            void Move(string source, string target);
            void Delete(string path);
            void Flush(string path);
        }

        private sealed class FileStorage : IStorage
        {
            public bool Exists(string path) => File.Exists(path);
            public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
            public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);
            public void Replace(string temporary, string target, string backup) => File.Replace(temporary, target, backup, false);
            public void Move(string source, string target) => File.Move(source, target);
            public void Delete(string path) => File.Delete(path);
            public void Flush(string path)
            {
                // ReplaceFile does not support WRITE_THROUGH. This flush stays checked.
                using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                file.Flush(true);
            }
        }

        public static void Install(string temporary, string target) => Install(temporary, target, new FileStorage());

        internal static void Install(string temporary, string target, IStorage storage)
        {
            string backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, Path.GetRandomFileName());
            bool readOnly = false;
            bool replacementAttempted = false;
            bool installed = false;
            try
            {
                if (storage.Exists(target))
                {
                    var attributes = storage.GetAttributes(target);
                    readOnly = (attributes & FileAttributes.ReadOnly) != 0;
                    if (readOnly)
                        storage.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
                    replacementAttempted = true;
                    storage.Replace(temporary, target, backup);
                }
                else
                    storage.Move(temporary, target);

                installed = true;
                storage.Flush(target);
                if (readOnly)
                    SetReadOnly(storage, target);
                // No backup accumulation on successful writes. Cleanup errors are visible.
                storage.Delete(backup);
            }
            catch (Exception primary)
            {
                primary.Data["AtomicFileReplacement.BackupPath"] = backup;
                primary.Data["AtomicFileReplacement.TemporaryPath"] = temporary;
                primary.Data["AtomicFileReplacement.TargetPath"] = target;
                try
                {
                    // Never replace an installed or concurrently created target. Move also
                    // refuses overwrite if a new target arrives after the existence check.
                    if (!installed && replacementAttempted && !storage.Exists(target) && storage.Exists(backup))
                    {
                        storage.Move(backup, target);
                        storage.Flush(target);
                    }
                }
                catch (Exception recovery)
                {
                    primary.Data["AtomicFileReplacement.RecoveryError"] = recovery;
                }
                if (readOnly)
                {
                    try
                    {
                        if (storage.Exists(target)) SetReadOnly(storage, target);
                        if (storage.Exists(backup)) SetReadOnly(storage, backup);
                    }
                    catch (Exception attributes)
                    {
                        primary.Data["AtomicFileReplacement.AttributeError"] = attributes;
                    }
                }
                // A partial replacement can leave the temporary file as the only copy.
                // Retain it whenever a backup survives or the target is unconfirmed.
                try
                {
                    if (storage.Exists(target) && !storage.Exists(backup))
                        storage.Delete(temporary);
                }
                catch (Exception cleanup)
                {
                    primary.Data["AtomicFileReplacement.CleanupError"] = cleanup;
                }
                throw;
            }
        }

        private static void SetReadOnly(IStorage storage, string path) =>
            storage.SetAttributes(path, storage.GetAttributes(path) | FileAttributes.ReadOnly);
    }
}

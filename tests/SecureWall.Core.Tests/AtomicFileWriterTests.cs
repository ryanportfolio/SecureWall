using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using pylorak.TinyWall;

namespace SecureWall.Core.Tests
{
    internal static class AtomicFileWriterTests
    {
        internal static IEnumerable<(string Name, Action Test)> Cases
        {
            get
            {
                yield return ("replacement 1176 with backup retains old target", () => ReplacementOutcome("1176"));
                yield return ("replacement before-swap error preserves old target", () => ReplacementOutcome("before"));
                yield return ("replacement 1177 restores renamed old target", () => ReplacementOutcome("1177"));
                yield return ("replacement recovery failure retains both copies", () => ReplacementOutcome("1177", recoveryFails: true));
                yield return ("replacement recovery never overwrites newer target", () => ReplacementOutcome("1177", race: true));
                yield return ("replacement post-swap flush failure retains backup", () => ReplacementOutcome("flush"));
                yield return ("replacement successful checked flush cleans backup", ReplacementSuccess);
                yield return ("replacement retains sole surviving temporary copy", ReplacementSoleCopy);
                yield return ("production updater preserves read-only and cleans backup", UpdaterFixture);
                yield return ("atomic writer creates a missing target and leaves no temp file", CreatesMissingTarget);
                yield return ("atomic writer replaces an existing target and leaves no temp file", ReplacesExistingTarget);
                yield return ("atomic writer keeps old content visible until the swap", OldContentVisibleDuringWrite);
                yield return ("atomic writer keeps target and removes temp when the callback throws", CallbackFailureLeavesTargetIntact);
                yield return ("atomic writer replaces a read-only target and restores the attribute", ReadOnlyTargetHandled);
                yield return ("atomic writer copies a source stream in full", WriteFromCopiesStream);
                yield return ("production encrypted writer finalizes padding and replaces existing content", EncryptedRoundTrip);
                yield return ("production encrypted writer preserves old content on serialization failure", EncryptedFailure);
                yield return ("atomic writer reports a locked target without losing old content", LockedTargetFailure);
            }
        }

        private sealed class TempDir : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sw-afw-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(Path);

            public string File(string name) => System.IO.Path.Combine(Path, name);

            public string[] Entries() => Directory.GetFiles(Path);

            public void Dispose()
            {
                foreach (var file in Directory.GetFiles(Path))
                    System.IO.File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Path, true);
            }
        }

        private static void WriteText(Stream stream, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        private const string Key = "0123456789abcdef0123456789abcdef";
        private const string Iv = "0123456789abcdef";

        private static void EncryptedRoundTrip()
        {
            using var dir = new TempDir();
            var target = dir.File("config.json");
            foreach (int length in new[] { 0, 1, 16, 17, 70001 })
            {
                var payload = new byte[length];
                new Random(9).NextBytes(payload);
                AtomicFileWriter.WriteEncrypted(target, stream => stream.Write(payload, 0, payload.Length), Key, Iv);
                using var aes = Aes.Create();
                aes.Key = Encoding.ASCII.GetBytes(Key);
                aes.IV = Encoding.ASCII.GetBytes(Iv);
                using var file = File.OpenRead(target);
                using var crypto = new CryptoStream(file, aes.CreateDecryptor(), CryptoStreamMode.Read);
                using var decoded = new MemoryStream();
                crypto.CopyTo(decoded);
                AssertEx.SequenceEqual(payload, decoded.ToArray());
                AssertEx.Equal((length / 16 + 1) * 16, (int)file.Length);
                AssertEx.SequenceEqual(new[] { target }, dir.Entries());
            }
        }

        private static void EncryptedFailure()
        {
            using var dir = new TempDir();
            var target = dir.File("config.json");
            File.WriteAllText(target, "old");
            AssertEx.Throws<InvalidOperationException>(() => AtomicFileWriter.WriteEncrypted(target, stream =>
            {
                WriteText(stream, "partial serialized config");
                throw new InvalidOperationException("serialization failed");
            }, Key, Iv));
            AssertEx.Equal("old", File.ReadAllText(target));
            AssertEx.SequenceEqual(new[] { target }, dir.Entries());
        }

        private static void LockedTargetFailure()
        {
            if (!OperatingSystem.IsWindows()) return;
            using var dir = new TempDir();
            var target = dir.File("config.json");
            File.WriteAllText(target, "old");
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                AssertEx.Throws<IOException>(() => AtomicFileWriter.Write(target, stream => WriteText(stream, "new")));
            AssertEx.Equal("old", File.ReadAllText(target));
            AssertEx.SequenceEqual(new[] { target }, dir.Entries());
        }

        private static void CreatesMissingTarget()
        {
            using var dir = new TempDir();
            var target = dir.File("hosts");

            AtomicFileWriter.Write(target, s => WriteText(s, "new"));

            AssertEx.Equal("new", File.ReadAllText(target));
            AssertEx.SequenceEqual(new[] { target }, dir.Entries());
        }

        private static void ReplacesExistingTarget()
        {
            using var dir = new TempDir();
            var target = dir.File("hosts");
            File.WriteAllText(target, "old content that is longer");

            AtomicFileWriter.Write(target, s => WriteText(s, "new"));

            AssertEx.Equal("new", File.ReadAllText(target));
            AssertEx.SequenceEqual(new[] { target }, dir.Entries());
        }

        private static void OldContentVisibleDuringWrite()
        {
            using var dir = new TempDir();
            var target = dir.File("hosts");
            File.WriteAllText(target, "old");
            string? seenDuringWrite = null;
            int entriesDuringWrite = 0;

            AtomicFileWriter.Write(target, s =>
            {
                WriteText(s, "partial");
                s.Flush();
                seenDuringWrite = File.ReadAllText(target);
                entriesDuringWrite = dir.Entries().Length;
            });

            AssertEx.Equal("old", seenDuringWrite);
            AssertEx.Equal(2, entriesDuringWrite, "temp file must live next to the target");
            AssertEx.Equal("partial", File.ReadAllText(target));
        }

        private static void CallbackFailureLeavesTargetIntact()
        {
            using var dir = new TempDir();
            var target = dir.File("hosts");
            File.WriteAllText(target, "old");

            var thrown = AssertEx.Throws<InvalidOperationException>(() =>
                AtomicFileWriter.Write(target, s =>
                {
                    WriteText(s, "half");
                    throw new InvalidOperationException("simulated");
                }));

            AssertEx.Equal("simulated", thrown.Message);
            AssertEx.Equal("old", File.ReadAllText(target));
            AssertEx.SequenceEqual(new[] { target }, dir.Entries());
        }

        private static void ReadOnlyTargetHandled()
        {
            using var dir = new TempDir();
            var target = dir.File("hosts");
            File.WriteAllText(target, "old");
            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);

            AtomicFileWriter.Write(target, s => WriteText(s, "new"));

            AssertEx.Equal("new", File.ReadAllText(target));
            AssertEx.True((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0, "read-only attribute must be restored");
            AssertEx.SequenceEqual(new[] { target }, dir.Entries());
        }

        private static void WriteFromCopiesStream()
        {
            using var dir = new TempDir();
            var target = dir.File("hosts");
            var payload = new byte[70000];
            new Random(7).NextBytes(payload);
            using var source = new MemoryStream(payload, false);

            AtomicFileWriter.WriteFrom(target, source);

            AssertEx.SequenceEqual(payload, File.ReadAllBytes(target));
            AssertEx.SequenceEqual(new[] { target }, dir.Entries());
        }
        private sealed class FaultStorage : pylorak.Utilities.AtomicFileReplacement.IStorage
        {
            internal readonly Dictionary<string, string> Files = new Dictionary<string, string>();
            internal readonly Dictionary<string, FileAttributes> Attributes = new Dictionary<string, FileAttributes>();
            internal readonly IOException Primary = new IOException("injected storage failure");
            internal string Outcome = "success";
            internal string Backup = "";
            internal bool RecoveryFails;
            internal bool Race;
            internal int Flushes;
            public bool Exists(string path) => Files.ContainsKey(path);
            public FileAttributes GetAttributes(string path) => Attributes.TryGetValue(path, out var value) ? value : FileAttributes.Normal;
            public void SetAttributes(string path, FileAttributes attributes) => Attributes[path] = attributes;
            public void Replace(string temporary, string target, string backup)
            {
                Backup = backup;
                AssertEx.Equal(Path.GetDirectoryName(Path.GetFullPath(target)), Path.GetDirectoryName(backup));
                AssertEx.True(backup != target && backup != temporary);
                if (Outcome == "1176" || Outcome == "before") throw Primary;
                Files[backup] = Files[target];
                Files.Remove(target);
                if (Outcome == "sole") { Files.Remove(backup); throw Primary; }
                if (Outcome == "1177") throw Primary;
                Files[target] = Files[temporary];
                Files.Remove(temporary);
            }
            public void Move(string source, string target)
            {
                if (source == Backup)
                {
                    if (Race) Files[target] = "newer";
                    if (RecoveryFails) throw new IOException("recovery failed");
                }
                if (Files.ContainsKey(target)) throw new IOException("destination already exists");
                Files[target] = Files[source];
                Files.Remove(source);
            }
            public void Delete(string path) => Files.Remove(path);
            public void Flush(string path)
            {
                Flushes++;
                if (Outcome == "flush") throw Primary;
            }
        }

        private static void ReplacementOutcome(string outcome, bool recoveryFails = false, bool race = false)
        {
            string target = Path.GetFullPath("r9c-target");
            string temporary = Path.GetFullPath("r9c-temporary");
            var storage = new FaultStorage { Outcome = outcome, RecoveryFails = recoveryFails, Race = race };
            storage.Files[target] = "old";
            storage.Files[temporary] = "candidate";
            storage.Attributes[target] = FileAttributes.ReadOnly;
            var error = AssertEx.Throws<IOException>(() => pylorak.Utilities.AtomicFileReplacement.Install(temporary, target, storage));
            AssertEx.True(ReferenceEquals(storage.Primary, error), "primary exception must survive recovery");
            AssertEx.Equal(storage.Backup, (string)error.Data["AtomicFileReplacement.BackupPath"]!);
            AssertEx.Equal(temporary, (string)error.Data["AtomicFileReplacement.TemporaryPath"]!);
            if (recoveryFails || race)
            {
                AssertEx.Equal("old", storage.Files[storage.Backup]);
                AssertEx.Equal("candidate", storage.Files[temporary]);
                AssertEx.True(error.Data.Contains("AtomicFileReplacement.RecoveryError"));
                AssertEx.True((storage.GetAttributes(storage.Backup) & FileAttributes.ReadOnly) != 0);
                if (race) AssertEx.Equal("newer", storage.Files[target]);
                else AssertEx.True(!storage.Files.ContainsKey(target));
            }
            else if (outcome == "flush")
            {
                AssertEx.Equal("candidate", storage.Files[target]);
                AssertEx.Equal("old", storage.Files[storage.Backup]);
                AssertEx.Equal(1, storage.Flushes);
            }
            else
            {
                AssertEx.Equal("old", storage.Files[target]);
                AssertEx.Equal(1, storage.Files.Count);
                AssertEx.Equal(outcome == "1177" ? 1 : 0, storage.Flushes);
            }
            if (storage.Exists(target))
                AssertEx.True((storage.GetAttributes(target) & FileAttributes.ReadOnly) != 0);
        }

        private static void ReplacementSoleCopy()
        {
            string target = Path.GetFullPath("r9c-target");
            string temporary = Path.GetFullPath("r9c-temporary");
            var storage = new FaultStorage { Outcome = "sole" };
            storage.Files[target] = "old";
            storage.Files[temporary] = "candidate";
            var error = AssertEx.Throws<IOException>(() => pylorak.Utilities.AtomicFileReplacement.Install(temporary, target, storage));
            AssertEx.True(ReferenceEquals(storage.Primary, error));
            AssertEx.Equal(1, storage.Files.Count);
            AssertEx.Equal("candidate", storage.Files[temporary]);
        }
        private static void ReplacementSuccess()
        {
            string target = Path.GetFullPath("r9c-target");
            string temporary = Path.GetFullPath("r9c-temporary");
            var storage = new FaultStorage();
            storage.Files[target] = "old";
            storage.Files[temporary] = "candidate";
            storage.Attributes[target] = FileAttributes.ReadOnly;
            pylorak.Utilities.AtomicFileReplacement.Install(temporary, target, storage);
            AssertEx.Equal("candidate", storage.Files[target]);
            AssertEx.Equal(1, storage.Files.Count);
            AssertEx.Equal(1, storage.Flushes);
            AssertEx.True((storage.GetAttributes(target) & FileAttributes.ReadOnly) != 0);
        }

        private static void UpdaterFixture()
        {
            using var dir = new TempDir();
            string target = dir.File("updater");
            File.WriteAllText(target, "old");
            File.SetAttributes(target, FileAttributes.ReadOnly);
            using (var updater = new pylorak.Utilities.AtomicFileUpdater(target))
            {
                File.WriteAllText(updater.TemporaryFilePath, "candidate");
                updater.Commit();
            }
            AssertEx.Equal("candidate", File.ReadAllText(target));
            AssertEx.True((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0);
            AssertEx.SequenceEqual(new[] { target }, dir.Entries());
        }
    }
}

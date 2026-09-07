using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using pylorak.TinyWall;

namespace SecureWall.Core.Tests
{
    internal static class AtomicFileWriterTests
    {
        internal static IEnumerable<(string Name, Action Test)> Cases
        {
            get
            {
                yield return ("atomic writer creates a missing target and leaves no temp file", CreatesMissingTarget);
                yield return ("atomic writer replaces an existing target and leaves no temp file", ReplacesExistingTarget);
                yield return ("atomic writer keeps old content visible until the swap", OldContentVisibleDuringWrite);
                yield return ("atomic writer keeps target and removes temp when the callback throws", CallbackFailureLeavesTargetIntact);
                yield return ("atomic writer replaces a read-only target and restores the attribute", ReadOnlyTargetHandled);
                yield return ("atomic writer copies a source stream in full", WriteFromCopiesStream);
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
    }
}

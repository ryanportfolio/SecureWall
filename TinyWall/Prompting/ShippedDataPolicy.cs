using System.IO;

namespace pylorak.TinyWall.Prompting
{
    internal enum ShippedDataResult { Seeded, Current, Replaced }

    // Program-owned files that the MSI ships in INSTALLDIR/data-defaults. Nothing
    // else writes them, so an install brings them to the shipped content. Never add
    // configuration, passwords, hosts.orig or journals here.
    internal static class ShippedDataPolicy
    {
        internal static readonly string[] Names = { "profiles.json", "hosts.bck" };

        // The caller validates the protected target directory before and after.
        // A missing or unreadable source fails before the target is touched.
        internal static ShippedDataResult Refresh(string sourceDirectory, string targetDirectory, string name)
        {
            byte[] shipped = File.ReadAllBytes(Path.Combine(sourceDirectory, name));
            string target = Path.Combine(targetDirectory, name);
            bool present;
            try
            {
                // File.Exists also returns false for access errors. Those must abort.
                File.GetAttributes(target);
                present = true;
            }
            catch (FileNotFoundException) { present = false; }
            if (present && SameContent(File.ReadAllBytes(target), shipped)) return ShippedDataResult.Current;
            // Temporary file in the protected directory, then one replace or a
            // no-overwrite move, so readers never see partial content.
            AtomicFileWriter.Write(target, stream => stream.Write(shipped, 0, shipped.Length));
            return present ? ShippedDataResult.Replaced : ShippedDataResult.Seeded;
        }

        private static bool SameContent(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }
    }
}

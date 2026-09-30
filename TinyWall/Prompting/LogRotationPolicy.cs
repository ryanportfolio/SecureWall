using System.IO;

namespace pylorak.TinyWall.Prompting
{
    // Keeps the current log plus one previous file. Truncating to zero would erase
    // the first, most useful exception of a restart loop.
    internal static class LogRotationPolicy
    {
        internal const long Limit = 512 * 1024;

        internal static string PreviousPath(string logfile) => Path.ChangeExtension(logfile, ".old.log");

        internal static bool RotateIfNeeded(string logfile, long limit = Limit)
        {
            var current = new FileInfo(logfile);
            if (!current.Exists || current.Length <= limit) return false;
            string previous = PreviousPath(logfile);
            File.Delete(previous);
            File.Move(logfile, previous);
            return true;
        }
    }
}

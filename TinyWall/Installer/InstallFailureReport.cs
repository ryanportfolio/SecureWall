using System;
using System.IO;
using System.Text;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall.Installer
{
    // A failed /install used to leave only "timed out waiting for Running" in
    // installer.log while the cause sat in service.log. This report appends the SCM
    // exit codes and the service log entries written during the attempt, so the
    // installer log alone explains the failure. The MSI failure page opens that file.
    internal static class InstallFailureReport
    {
        private const int MaxServiceLogChars = 32 * 1024;

        private static string ServiceLogPath => Path.Combine(MachineDataGuard.PathName, "logs", Utils.LOG_ID_SERVICE + ".log");

        // Where the service log ended when the attempt began.
        internal readonly struct ServiceLogMark
        {
            internal readonly long Length;
            internal readonly DateTime TimeUtc;
            internal ServiceLogMark(long length, DateTime timeUtc) { Length = length; TimeUtc = timeUtc; }
        }

        internal static ServiceLogMark MarkServiceLog() => new(ServiceLogLength(), DateTime.UtcNow);

        private static long ServiceLogLength()
        {
            try
            {
                var log = new FileInfo(ServiceLogPath);
                return log.Exists ? log.Length : 0;
            }
            catch { return 0; }
        }

        internal static string Build(ServiceLogMark start)
        {
            var report = new StringBuilder();
            report.AppendLine("SecureWall /install failed. Service diagnostics for this attempt:");
            report.AppendLine("Service status: " + InstallationSafety.DescribeServiceStatus());
            report.AppendLine("Service log entries written during this attempt (" + ServiceLogPath + "):");
            report.Append(ServiceLogSince(start));
            return report.ToString();
        }

        private static string ServiceLogSince(ServiceLogMark start)
        {
            try
            {
                MachineDataGuard.Require();
                // A rotation during the attempt moved the file that held the mark to the
                // previous path: either the current file shrank below the mark, or the
                // previous file took a write after the attempt began.
                string previous = LogRotationPolicy.PreviousPath(ServiceLogPath);
                var previousLog = new FileInfo(previous);
                bool rotated = ServiceLogLength() < start.Length ||
                    (previousLog.Exists && previousLog.LastWriteTimeUtc >= start.TimeUtc);
                string text = rotated
                    ? ReadFrom(previous, start.Length) + ReadFrom(ServiceLogPath, 0)
                    : ReadFrom(ServiceLogPath, start.Length);
                text = text.Trim();
                if (text.Length == 0) return "(none)";
                if (text.Length > MaxServiceLogChars)
                    text = "(earlier entries omitted)" + Environment.NewLine + text.Substring(text.Length - MaxServiceLogChars);
                return text;
            }
            catch (Exception exception)
            {
                return "(could not read: " + exception.Message + ")";
            }
        }

        private static string ReadFrom(string path, long offset)
        {
            if (!File.Exists(path)) return string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset >= stream.Length) return string.Empty;
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
    }
}

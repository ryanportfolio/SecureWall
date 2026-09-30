using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace pylorak.TinyWall.Prompting
{
    // Bounds and sanitizes everything that crosses the AI boundary. Subject fields come from
    // the blocked program (file name, certificate subject, service name) and can carry control
    // characters or injected instructions; responses come from a remote endpoint that may be
    // hostile or broken. Nothing here affects the allow/block decision.
    internal static class AiExplainText
    {
        internal const int MaxResponseBytes = 64 * 1024;
        internal const int MaxFieldLength = 160;
        internal const int MaxExplanationLength = 4000;
        internal const int MaxErrorLength = 600;
        internal const int MaxApiErrorLength = 300;

        internal const string AiGeneratedLabel =
            "AI-generated. It can be wrong and is not a SecureWall verdict; the file name and publisher it saw are unverified.";
        internal const string TruncatedMarker = "[truncated]";

        // Single-line prompt field: C0/C1 controls (including CR, LF and TAB) become spaces,
        // runs of whitespace collapse, and the result is capped. Returns null when nothing is left.
        internal static string? SanitizeField(string? value, int maxLength = MaxFieldLength)
        {
            if (value == null)
                return null;

            var sb = new StringBuilder(Math.Min(value.Length, maxLength + 1));
            bool pendingSpace = false;
            foreach (char c in value)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(c);
                if (sb.Length > maxLength)
                    break;
            }

            if (sb.Length == 0)
                return null;
            return sb.Length > maxLength ? Truncate(sb.ToString(), maxLength, "...") : sb.ToString();
        }

        // Prompt fields are quoted so the model sees where each value ends. Embedded double
        // quotes become single quotes so a value cannot close its own delimiter.
        internal static string Quote(string value) => "\"" + value.Replace('"', '\'') + "\"";

        // Multi-line display text: line breaks normalize to CRLF for the WinForms TextBox, tabs
        // become spaces, other C0/C1 controls are dropped, and the result is capped.
        internal static string ForDisplay(string? text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var sb = new StringBuilder(Math.Min(text!.Length, maxLength) + 8);
            for (int i = 0; i < text.Length && sb.Length <= maxLength; ++i)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        ++i;
                    sb.Append("\r\n");
                }
                else if (c == '\n')
                {
                    sb.Append("\r\n");
                }
                else if (c == '\t')
                {
                    sb.Append(' ');
                }
                else if (!char.IsControl(c))
                {
                    sb.Append(c);
                }
            }

            string result = sb.ToString().Trim();
            return result.Length > maxLength
                ? Truncate(result, maxLength, "\r\n" + TruncatedMarker)
                : result;
        }

        internal static string FormatExplanation(string? text) =>
            AiGeneratedLabel + "\r\n\r\n" + ForDisplay(text, MaxExplanationLength);

        internal static string FormatError(string? text) => ForDisplay(text, MaxErrorLength);

        // Reads at most maxBytes from the response body. Returns null when the body is larger,
        // so an oversized answer is discarded instead of buffered.
        internal static async Task<byte[]?> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));

            var buffer = new byte[maxBytes + 1];
            int total = 0;
            while (total < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(buffer, total, buffer.Length - total, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
            }

            if (total > maxBytes)
                return null;

            var result = new byte[total];
            Buffer.BlockCopy(buffer, 0, result, 0, total);
            return result;
        }

        private static string Truncate(string value, int maxLength, string marker)
        {
            int keep = Math.Max(0, maxLength - marker.Length);
            // Do not split a surrogate pair.
            if (keep > 0 && char.IsHighSurrogate(value[keep - 1]))
                --keep;
            return value.Substring(0, keep) + marker;
        }
    }
}

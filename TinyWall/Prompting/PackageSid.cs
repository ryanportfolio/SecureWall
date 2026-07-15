using System;
using System.Globalization;

namespace pylorak.TinyWall.Prompting
{
    internal static class PackageSid
    {
        internal static bool TryNormalize(string? value, out string? normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string candidate = value!.Trim();
            string[] parts = candidate.Split('-');
            if (parts.Length < 5 ||
                !string.Equals(parts[0], "S", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parts[1], "1", StringComparison.Ordinal) ||
                !string.Equals(parts[2], "15", StringComparison.Ordinal) ||
                !string.Equals(parts[3], "2", StringComparison.Ordinal))
            {
                return false;
            }

            for (int index = 4; index < parts.Length; index++)
            {
                if (!uint.TryParse(
                    parts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _))
                {
                    return false;
                }
            }

            normalized = "S-1-15-2-" + string.Join("-", parts, 4, parts.Length - 4);
            return true;
        }
    }
}

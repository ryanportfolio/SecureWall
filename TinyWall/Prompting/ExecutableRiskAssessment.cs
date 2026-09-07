using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    [Flags]
    internal enum ExecutableRiskFlags
    {
        None = 0,
        Unsigned = 1,
        UserWritableLocation = 2,
        RecentlyModified = 4,
    }

    // Outcome of an Authenticode check on the blocked executable. Only Trusted counts as
    // signed; every other value (no signature, broken chain, unreadable file) is reported
    // to the user as "not signed by a trusted publisher".
    internal enum ExecutableSignatureStatus
    {
        Unknown = 0,
        Trusted,
        Missing,
        Invalid,
    }

    // Pure classifier: turns already-gathered facts about the blocked executable into the
    // warning flags the block popup shows. No I/O here; see ExecutableRiskProbe for the
    // gatherer that runs in the controller process at display time.
    internal static class ExecutableRiskAssessment
    {
        internal static readonly TimeSpan RecentlyModifiedWindow = TimeSpan.FromHours(24);

        internal const string UnsignedText = "Not signed by a trusted publisher";
        internal const string UserWritableLocationText = "Located in a folder your user account can modify";
        internal const string RecentlyModifiedText = "File changed within the last 24 hours";

        internal static ExecutableRiskFlags Assess(
            ExecutableSignatureStatus signature,
            bool userCanWriteLocation,
            DateTimeOffset? lastWriteUtc,
            DateTimeOffset nowUtc)
        {
            return Assess(signature, userCanWriteLocation, lastWriteUtc, nowUtc, RecentlyModifiedWindow);
        }

        internal static ExecutableRiskFlags Assess(
            ExecutableSignatureStatus signature,
            bool userCanWriteLocation,
            DateTimeOffset? lastWriteUtc,
            DateTimeOffset nowUtc,
            TimeSpan recentWindow)
        {
            if (recentWindow < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(recentWindow));

            var flags = ExecutableRiskFlags.None;
            if (signature != ExecutableSignatureStatus.Trusted)
                flags |= ExecutableRiskFlags.Unsigned;
            if (userCanWriteLocation)
                flags |= ExecutableRiskFlags.UserWritableLocation;
            if (lastWriteUtc.HasValue && IsRecent(lastWriteUtc.Value, nowUtc, recentWindow))
                flags |= ExecutableRiskFlags.RecentlyModified;
            return flags;
        }

        // "Within the window" means strictly younger than the window: a file exactly 24 h
        // old is not recent. A timestamp in the future (clock skew or tampering) counts as
        // recent, since it cannot be older than the window.
        internal static bool IsRecent(DateTimeOffset lastWriteUtc, DateTimeOffset nowUtc, TimeSpan recentWindow)
        {
            TimeSpan age = nowUtc - lastWriteUtc;
            return age < recentWindow;
        }

        // One plain-text line per raised flag, in a fixed order.
        internal static IReadOnlyList<string> Describe(ExecutableRiskFlags flags)
        {
            var lines = new List<string>(3);
            if ((flags & ExecutableRiskFlags.Unsigned) != 0)
                lines.Add(UnsignedText);
            if ((flags & ExecutableRiskFlags.UserWritableLocation) != 0)
                lines.Add(UserWritableLocationText);
            if ((flags & ExecutableRiskFlags.RecentlyModified) != 0)
                lines.Add(RecentlyModifiedText);
            return lines;
        }
    }
}

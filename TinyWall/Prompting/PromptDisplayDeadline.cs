using System;

namespace pylorak.TinyWall.Prompting
{
    // The popup stays until the owner answers it or the service token expires. There is no
    // shorter display timeout: an unattended popup must not disappear before it was seen.
    // Nothing extends the token expiry.
    internal sealed class PromptDisplayDeadline
    {
        private readonly DateTimeOffset _expiresUtc;

        internal PromptDisplayDeadline(DateTimeOffset expiresUtc)
        {
            _expiresUtc = expiresUtc;
        }

        internal bool ShouldClose(DateTimeOffset now) => now >= _expiresUtc;
    }

    // Allow stays disabled until the popup has been visible and unmoved for ArmDelay and the
    // executable risk probe has finished, so a click already in progress or a keystroke cannot
    // land on a freshly shown or shifted Allow, and the owner sees the warnings first. Uses a
    // monotonic elapsed clock supplied by the caller. This is a timing mitigation only: a
    // same-user process can still wait out the delay and click; the password lock is the
    // control for that.
    internal sealed class PromptAllowArming
    {
        internal static readonly TimeSpan ArmDelay = TimeSpan.FromSeconds(1);

        private TimeSpan? _stableSince;
        private bool _riskReady;

        // New prompt: disarm until it is shown and its risk probe completes.
        internal void Reset()
        {
            _stableSince = null;
            _riskReady = false;
        }

        // The popup became visible, moved or changed size: restart the delay.
        internal void NoteShownOrMoved(TimeSpan now) => _stableSince = now;

        internal void NoteRiskReady() => _riskReady = true;

        internal bool IsArmed(TimeSpan now) =>
            _riskReady && _stableSince is TimeSpan since && now - since >= ArmDelay;
    }
}

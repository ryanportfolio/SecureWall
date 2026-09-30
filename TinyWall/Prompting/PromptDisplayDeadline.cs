using System;

namespace pylorak.TinyWall.Prompting
{
    internal sealed class PromptDisplayDeadline
    {
        private readonly DateTimeOffset _expiresUtc;
        private DateTimeOffset _dismissUtc;

        internal PromptDisplayDeadline(DateTimeOffset shownUtc, DateTimeOffset expiresUtc)
        {
            _expiresUtc = expiresUtc;
            _dismissUtc = shownUtc.AddSeconds(30);
        }

        internal void PauseForReading() => _dismissUtc = _expiresUtc;

        // A locked Allow waits for the password dialog. Like reading, it can hold the popup
        // open only until the service token expires, so the automatic dismissal (and its
        // ignore cooldown) cannot fire while the owner is unlocking.
        internal void PauseForUnlock() => _dismissUtc = _expiresUtc;

        internal bool ShouldClose(DateTimeOffset now) => now >= _expiresUtc || now >= _dismissUtc;
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

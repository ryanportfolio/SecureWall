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
        internal bool ShouldClose(DateTimeOffset now) => now >= _expiresUtc || now >= _dismissUtc;
    }
}

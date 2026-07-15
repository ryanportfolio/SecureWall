using System;

namespace pylorak.TinyWall.Prompting
{
    internal interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }

    internal sealed class SystemClock : IClock
    {
        internal static SystemClock Instance { get; } = new SystemClock();

        private SystemClock()
        {
        }

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}

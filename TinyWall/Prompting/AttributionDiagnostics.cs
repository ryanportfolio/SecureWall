using System;
using System.Threading;

namespace pylorak.TinyWall.Prompting
{
    // Health reads do not acquire the watcher's lifecycle lock.
    internal sealed class AuditSubscriptionHealth
    {
        private int healthy;
        private int stopped;
        internal bool SubscriptionAvailable => Volatile.Read(ref stopped) == 0 && Volatile.Read(ref healthy) != 0;
        internal bool Stopped => Volatile.Read(ref stopped) != 0;
        internal bool Available(bool leaseOwned) => leaseOwned && SubscriptionAvailable;
        internal void Starting() => Interlocked.Exchange(ref healthy, 1);
        internal void Failed() => Interlocked.Exchange(ref healthy, 0);
        internal void Stop() { Interlocked.Exchange(ref stopped, 1); Failed(); }
    }

    internal sealed class CoalescedDiagnostic
    {
        private readonly object guard = new object();
        private long total, reported;
        private DateTimeOffset nextReport;
        internal long Total { get { lock (guard) return total; } }
        internal void Record() { lock (guard) { if (total < long.MaxValue) ++total; } }
        internal bool TryReport(DateTimeOffset now, out long count)
        {
            lock (guard)
            {
                count = 0;
                if (total == reported || now < nextReport) return false;
                count = total - reported;
                reported = total;
                nextReport = now.AddMinutes(1);
                return true;
            }
        }
    }

    internal static class AttributionStatusText
    {
        internal const string Unavailable = "Service attribution is unavailable. Some blocked connections cannot offer Allow. Firewall enforcement is unchanged.";
        internal const string Overflow = "Some blocked connections could not be shown because attribution or prompt processing reached its limits. Those connections remain blocked.";
    }

    // Controller/UI thread only. Recovery arms a future warning, but flapping and
    // continuous overload still share a one-minute notification budget.
    internal sealed class AttributionNotificationGate
    {
        private bool warnedUnavailable;
        private long candidates, prompts;
        private DateTimeOffset nextNotification;
        internal string? Update(bool available, long droppedCandidates, long droppedPrompts, DateTimeOffset now)
        {
            if (available) warnedUnavailable = false;
            // Service restart resets lifetime counters; zero is not a new overflow.
            if (droppedCandidates < candidates) candidates = 0;
            if (droppedPrompts < prompts) prompts = 0;
            if (now < nextNotification) return null;
            string? message = null;
            if (!available && !warnedUnavailable)
            {
                warnedUnavailable = true;
                message = AttributionStatusText.Unavailable;
            }
            else if (droppedCandidates != candidates || droppedPrompts != prompts)
            {
                candidates = droppedCandidates;
                prompts = droppedPrompts;
                message = AttributionStatusText.Overflow;
            }
            if (message != null) nextNotification = now.AddMinutes(1);
            return message;
        }
    }
}

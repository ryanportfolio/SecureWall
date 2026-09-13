using System;

namespace pylorak.TinyWall.Prompting
{
    // One subscription per lifetime. Failure is terminal until service restart.
    // Never invoke a consumer or drain callbacks while holding SyncRoot.
    internal sealed class AuditWatcherLifetime
    {
        internal object SyncRoot { get; } = new object();
        private object? current;
        private bool stopped, failed;

        internal bool Attach(object watcher)
        {
            lock (SyncRoot)
            {
                if (stopped || current != null || failed) return false;
                current = watcher;
                return true;
            }
        }

        internal bool Accept(object? sender)
        {
            lock (SyncRoot) return !stopped && !failed && current != null && ReferenceEquals(sender, current);
        }

        internal bool Fail(object? sender)
        {
            lock (SyncRoot)
            {
                if (!Accept(sender)) return false;
                failed = true;
                return true;
            }
        }

        internal void Stop(Action detach, Action drain)
        {
            lock (SyncRoot)
            {
                if (stopped) return;
                stopped = true;
                current = null;
                detach();
            }
            drain();
        }
    }
}

using System;

namespace pylorak.TinyWall.Prompting
{
    // Values match SERVICE_STATUS. No SCM or process mutation lives in this policy.
    internal enum LifecycleServiceState { Stopped = 1, StartPending = 2, StopPending = 3, Running = 4 }

    internal static class ServiceLifecyclePolicy
    {
        // The service requests 120 seconds from SCM during initialization.
        internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
        internal static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(10);

        internal static bool WaitUntil(Func<bool> ready, TimeSpan timeout,
            Func<TimeSpan> elapsed, Action<TimeSpan> delay)
        {
            TimeSpan start = elapsed();
            while (!ready())
            {
                TimeSpan remaining = timeout - (elapsed() - start);
                if (remaining <= TimeSpan.Zero) return false;
                delay(remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200));
            }
            return true;
        }

        internal static bool StopGracefully(Func<LifecycleServiceState> state, Action stop,
            Func<TimeSpan> elapsed, Action<TimeSpan> delay)
        {
            if (!WaitUntil(() => state() != LifecycleServiceState.StartPending, CleanupGrace, elapsed, delay))
                return false;
            LifecycleServiceState current = state();
            if (current == LifecycleServiceState.Stopped) return true;
            if (current != LifecycleServiceState.StopPending) stop();
            return WaitUntil(() => state() == LifecycleServiceState.Stopped, CleanupGrace, elapsed, delay);
        }

        internal static void Cleanup(bool failedInstallRollback, Func<bool> stopGracefully,
            Action terminateOwnedProcess, Func<bool> confirmStopped, Action cleanup)
        {
            if (!stopGracefully())
            {
                if (!failedInstallRollback)
                    throw new InvalidOperationException("Service did not stop within the maintenance deadline.");
                terminateOwnedProcess();
            }
            if (!confirmStopped())
                throw new InvalidOperationException("Service must be stopped before cleanup.");
            cleanup();
        }

        internal static bool IsRollbackProcess(uint pid, uint ownPid, uint servicePid,
            uint serviceType, string path, string expectedPath, bool system)
            => pid != 0 && pid != ownPid && pid == servicePid && serviceType == 0x10 && system &&
               string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase);

        internal static void RequireStoppedAfterProcessOpenFailure(Func<LifecycleServiceState> state, Exception error)
        {
            if (state() != LifecycleServiceState.Stopped) throw error;
        }
    }
}

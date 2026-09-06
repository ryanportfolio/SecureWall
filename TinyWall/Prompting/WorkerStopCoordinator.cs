using System;

namespace pylorak.TinyWall.Prompting
{
    // OnStop already runs on a ServiceBase thread-pool callback. Never let a
    // cleanup failure escape it: ServiceBase would restore the Running state.
    internal static class WorkerStopCoordinator
    {
        internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        internal static void Stop(Action requestStop, Func<TimeSpan, bool> join,
            Action<int> reportProgress, Action complete, Action<Exception> terminate,
            Func<TimeSpan> elapsed)
        {
            Exception failure;
            try
            {
                TimeSpan start = elapsed();
                // RequestStop can wait up to five seconds for space in the queue.
                reportProgress(6000);
                requestStop();
                while (true)
                {
                    TimeSpan remaining = Deadline - (elapsed() - start);
                    if (remaining <= TimeSpan.Zero)
                        throw new TimeoutException("The firewall worker did not finish stopping within the cleanup deadline.");
                    reportProgress(2000);
                    if (join(remaining < PollInterval ? remaining : PollInterval))
                    {
                        // Only the completed worker can prove disposal has finished.
                        complete();
                        return;
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
            }

            // Production must not return: process rundown withdraws dynamic grants
            // while retaining the persistent deny baseline and restoration journal.
            terminate(failure);
        }
    }
}

using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    // Event lines for one emergency release. Only the first failure of a step
    // produces a line, so nested catches that log the same failure again (or a
    // follow-on deadline error) add no second event. That first line carries
    // the root cause.
    internal sealed class EmergencyFailureLog
    {
        private readonly string mode;
        private readonly List<string> failedSteps = new();

        internal EmergencyFailureLog(string mode) => this.mode = mode;

        internal IReadOnlyList<string> FailedSteps => failedSteps;

        // Returns the event line, or null when this step already reported.
        internal string? Record(string step, Exception error)
        {
            if (failedSteps.Contains(step)) return null;
            failedSteps.Add(step);
            return MaintenanceFailureReport.EmergencyStepFailure(mode, step, error);
        }

        internal string Summary(int exitCode) => MaintenanceFailureReport.EmergencyExitFailure(mode, exitCode, failedSteps);
    }
}

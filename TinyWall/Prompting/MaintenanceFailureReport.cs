using System;
using System.Collections.Generic;
using System.Text;

namespace pylorak.TinyWall.Prompting
{
    // One-line reasons for the Application event log. MSI EXE custom actions and
    // SCM discard stderr, so this line is what the owner can find after error 1722.
    internal static class MaintenanceFailureReport
    {
        internal const int MaxLength = 2000;

        internal static string Mode(string[] args)
        {
            foreach (string mode in new[] { "/install", "/uninstall", "/msi-cleanup", "/msi-rollback-install", "/service" })
                foreach (string arg in args)
                    if (string.Equals(arg, mode, StringComparison.OrdinalIgnoreCase)) return mode;
            return "controller";
        }

        internal static string GuardFailure(string mode, string dataPath, Exception error) => OneLine(
            $"SecureWall {mode} failed: the protected data directory {dataPath} did not validate. {Describe(error)} " +
            "Use trusted local-console recovery; do not loosen the directory permissions.");

        internal static string ExitFailure(string mode, int exitCode, string logPath) => OneLine(
            $"SecureWall {mode} failed with exit code {exitCode}. Details: {logPath}");

        internal static string Unhandled(string mode, Exception error) => OneLine(
            $"SecureWall {mode} failed: {Describe(error)}");

        // The emergency release runs while machine data is rejected, so no text
        // log is written; these lines name the step instead of a log path.
        internal static string EmergencyStepFailure(string mode, string step, Exception error) => OneLine(
            $"SecureWall {mode} emergency release step failed: {step}. {Describe(error)}");

        internal static string EmergencyExitFailure(string mode, int exitCode, IEnumerable<string> failedSteps)
        {
            string steps = string.Join(", ", failedSteps);
            return OneLine($"SecureWall {mode} emergency release failed with exit code {exitCode}. " +
                (steps.Length == 0 ? "No step reported an exception." : $"Failed steps: {steps}. The preceding SecureWall events give each exception.") +
                " The rejected data directory was not read or changed.");
        }

        private static string Describe(Exception error)
        {
            string text = error.GetType().Name + ": " + error.Message;
            if (error.InnerException != null)
                text += " (" + error.InnerException.GetType().Name + ": " + error.InnerException.Message + ")";
            return text;
        }

        internal static string OneLine(string text)
        {
            var line = new StringBuilder(Math.Min(text.Length, MaxLength));
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c)) { space = line.Length > 0; continue; }
                if (space) { line.Append(' '); space = false; }
                line.Append(c);
                if (line.Length >= MaxLength) break;
            }
            return line.ToString();
        }
    }
}

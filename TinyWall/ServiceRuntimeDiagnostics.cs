using System;
using System.Diagnostics;
using System.IO;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    internal sealed class ServiceRuntimeDiagnostics : IDisposable
    {
        private readonly RuntimeJournal? journal;

        internal ServiceRuntimeDiagnostics()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                journal = new RuntimeJournal(() => new RotatingRuntimeJournalSink(
                    Path.Combine(Installer.MachineDataGuard.PathName, "logs"),
                    Installer.MachineDataGuard.RequireForDiagnostics), process.Id);
            }
            catch { } // Diagnostics resource failure cannot prevent policy initialization.
        }

        internal void Emit(RuntimeEvent eventCode, RuntimeResult result, int hresult = 0) => journal?.Emit(eventCode, result, hresult);
        internal void SetEnabled(bool enabled) => journal?.SetEnabled(enabled);
        internal void ObserveDecision(bool allowed) => journal?.ObserveDecision(allowed);
        internal void SetAuditAvailable(bool available) => journal?.SetAuditAvailable(available);

        internal void Run(RuntimeEvent eventCode, Action action)
        {
            if (journal == null) { action(); return; }
            journal.Run(eventCode, action);
        }

        internal void CommitConfiguration(bool enableDiagnostics, Action commit)
        {
            if (journal == null) { commit(); return; }
            journal.CommitConfiguration(enableDiagnostics, commit);
        }

        internal void FinishShutdown() => journal?.FinishShutdown();

        public void Dispose() => journal?.Dispose();
    }
}

using System;

namespace pylorak.TinyWall.Prompting
{
    // Ordering and failure policy for teardown after the service has stopped.
    // Adapters supply the Windows operations; no I/O lives here.
    //
    // The persistent deny baseline is released only after SecureWall's Windows
    // Firewall allow-all compatibility rules are gone, so removal never leaves
    // those rules active without the baseline in front of them.
    internal static class ProtectionReleasePolicy
    {
        internal const string HostsSkipped =
            "Hosts restoration skipped: the original hosts backup lives in the rejected machine-data directory and cannot be trusted. The directory is preserved unchanged. Review %SystemRoot%\\System32\\drivers\\etc\\hosts manually.";
        internal const string HostsRetained =
            "Failed-install rollback could not restore the hosts file. Continuing removal because the payload that could retry is being deleted. Any original hosts backup is retained in the machine-data directory.";
        internal const string AuditRetained =
            "Failed-install rollback could not restore the audit policy. Continuing removal; the audit recovery journal is retained in the registry.";
        internal const string BaselineRetained =
            "Failed-install rollback kept the SecureWall deny baseline because SecureWall's Windows Firewall compatibility rules could not be confirmed removed. External traffic stays blocked. Start the Windows Defender Firewall service (MpsSvc), then install SecureWall from trusted media at the local console and uninstall it to release the baseline.";
        internal const string BaselineRemovalFailed =
            "Failed-install rollback could not remove the SecureWall deny baseline. External traffic stays blocked. Install SecureWall from trusted media at the local console and uninstall it to release the baseline.";

        // Returns true only when every step completed. Ordinary removal stops at
        // the first failure and keeps the baseline and service registration so it
        // can be retried. Failed first-install rollback continues: its payload is
        // about to be deleted, so it always removes the service registration and
        // removes the baseline whenever the compatibility rules are gone.
        internal static bool Release(bool failedInstallRollback, bool machineDataTrusted,
            Action restoreHosts, Action restoreCompatibility, Func<bool> compatibilityRulesAbsent,
            Action restoreAudit, Action terminateControllers, Action removeWfpObjects,
            Func<bool> removeRegistration, Action<string> warn, Action<Exception> fail)
        {
            bool Step(Action action)
            {
                try { action(); return true; }
                catch (Exception exception) { fail(exception); return false; }
            }

            bool complete = true;
            if (!machineDataTrusted) warn(HostsSkipped);
            else if (!Step(restoreHosts))
            {
                if (!failedInstallRollback) return false;
                complete = false;
                warn(HostsRetained);
            }

            bool compatibilityReleased = Step(restoreCompatibility);
            if (!compatibilityReleased)
            {
                if (!failedInstallRollback) return false;
                complete = false;
                // Notification restore can fail after the rules were removed.
                // Only a confirmed absence permits baseline removal.
                try { compatibilityReleased = compatibilityRulesAbsent(); }
                catch (Exception exception) { fail(exception); compatibilityReleased = false; }
            }

            if (!Step(restoreAudit))
            {
                if (!failedInstallRollback) return false;
                complete = false;
                warn(AuditRetained);
            }

            if (!Step(terminateControllers))
            {
                if (!failedInstallRollback) return false;
                complete = false;
            }

            if (!compatibilityReleased)
            {
                complete = false;
                warn(BaselineRetained);
            }
            else if (!Step(removeWfpObjects))
            {
                if (!failedInstallRollback) return false;
                complete = false;
                warn(BaselineRemovalFailed);
            }

            if (!removeRegistration()) complete = false;
            return complete;
        }
    }
}

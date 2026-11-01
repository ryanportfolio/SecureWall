using System;
using System.Collections.Generic;

namespace pylorak.TinyWall.Prompting
{
    internal enum UpdateGrantStatus
    {
        // The permit is in place for the requesting user's SecureWall controller.
        Granted,
        // The firewall mode already allows outbound connections; no permit was added.
        NotNeeded,
        // Block all, or display-off blocking: no permit is added.
        Blocked,
        // The requesting process could not be identified as an interactive SecureWall controller.
        Refused,
        Failed,
    }

    // Network access for "Check for updates". WFP cannot match host names, and SecureWall.exe
    // is also the LocalSystem service image, so the check gets a transient permit shaped like
    // the AI assistant's: the SecureWall image, outbound TCP to remote port 443, and a user
    // condition that allows only the requesting user's SID and denies the service accounts.
    // It lives only in the dynamic WFP session, is never saved, and ends after at most
    // MaxLifetime, on the controller's release, when the requesting process exits, on LOCK,
    // on a failed reload, in Block all and under display-off blocking.
    internal static class UpdateNetworkGrantPolicy
    {
        internal static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);
        internal const int MaxLeases = 4;

        internal static UpdateGrantStatus Decide(FirewallMode mode, bool displayOffBlockActive, bool runtimeRevoked)
        {
            if (runtimeRevoked)
                return UpdateGrantStatus.Failed;
            switch (mode)
            {
                case FirewallMode.AllowOutgoing:
                case FirewallMode.Disabled:
                    return UpdateGrantStatus.NotNeeded;
                case FirewallMode.Normal:
                    return displayOffBlockActive ? UpdateGrantStatus.Blocked : UpdateGrantStatus.Granted;
                default:
                    return UpdateGrantStatus.Blocked;
            }
        }

        // Permits exist only in Normal mode with the display-off block inactive; in any
        // other state every lease ends.
        internal static bool ShouldInstall(bool normalMode, bool displayOffBlockActive) =>
            normalMode && !displayOffBlockActive;

        // A user SID the permit may name: a well-formed SID that is not one of the service
        // accounts the AI permit also denies.
        internal static bool IsAllowedUser(string? sid)
        {
            if (string.IsNullOrEmpty(sid) || !sid!.StartsWith("S-1-", StringComparison.Ordinal))
                return false;
            foreach (char c in sid)
            {
                if (!(c >= '0' && c <= '9') && c != '-' && c != 'S')
                    return false;
            }
            foreach (string denied in AiExplainEgressPolicy.DeniedUserSids)
            {
                if (string.Equals(denied, sid, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        internal static string Describe(UpdateGrantStatus status) => status switch
        {
            UpdateGrantStatus.Blocked =>
                "SecureWall is blocking all traffic (Block all mode, or blocking while the display is off), so it cannot check for updates. Switch to Normal mode and try again.",
            UpdateGrantStatus.Refused =>
                "The SecureWall service did not recognize this program as the SecureWall tray app, so it gave it no network access.",
            UpdateGrantStatus.Failed =>
                "The SecureWall service could not give the update check network access. Try again in a minute.",
            _ => string.Empty,
        };
    }

    // Lease expiry follows elapsed time, so setting the system clock back cannot extend a permit.
    internal sealed class MonotonicClock : IClock
    {
        private readonly DateTimeOffset Start = DateTimeOffset.UtcNow;
        private readonly System.Diagnostics.Stopwatch Elapsed = System.Diagnostics.Stopwatch.StartNew();

        public DateTimeOffset UtcNow => Start + Elapsed.Elapsed;
    }

    internal sealed class UpdateNetworkLease
    {
        internal UpdateNetworkLease(uint processId, long creationTime, string userSid, DateTimeOffset expiresUtc, IDisposable? process)
        {
            ProcessId = processId;
            CreationTime = creationTime;
            UserSid = userSid;
            ExpiresUtc = expiresUtc;
            Process = process;
        }

        internal uint ProcessId { get; }
        // Process creation time: a reused process ID belongs to another process.
        internal long CreationTime { get; }
        internal string UserSid { get; }
        internal DateTimeOffset ExpiresUtc { get; }
        // The service's SYNCHRONIZE handle to the requesting process. It signals the exit
        // and pins the process object, so the ID cannot be reused while the lease exists.
        internal IDisposable? Process { get; }
        // Committed WFP filter keys for this lease's permit.
        internal List<Guid> FilterKeys { get; set; } = new();

        internal bool SameProcess(uint processId, long creationTime) =>
            ProcessId == processId && CreationTime == creationTime;
    }

    // Leases held by the service's enforcement thread. Pure bookkeeping: callers remove the
    // returned leases' filters and dispose their process handles.
    internal sealed class UpdateNetworkLeases
    {
        private readonly IClock Clock;
        private readonly List<UpdateNetworkLease> Leases = new();

        internal UpdateNetworkLeases(IClock clock)
        {
            Clock = clock;
        }

        internal IReadOnlyList<UpdateNetworkLease> Active => Leases;

        // A fresh lease for the requesting process, valid for MaxLifetime from now. Leases this
        // one replaces (the same process ID, whatever its creation time, or the oldest lease
        // once MaxLeases are held) are removed and returned in `replaced`.
        internal UpdateNetworkLease Grant(uint processId, long creationTime, string userSid, IDisposable? process,
            out List<UpdateNetworkLease> replaced)
        {
            if (!UpdateNetworkGrantPolicy.IsAllowedUser(userSid))
                throw new ArgumentException("The permit cannot name this account.", nameof(userSid));
            replaced = RemoveWhere(lease => lease.ProcessId == processId);
            while (Leases.Count >= UpdateNetworkGrantPolicy.MaxLeases)
            {
                replaced.Add(Leases[0]);
                Leases.RemoveAt(0);
            }
            var lease = new UpdateNetworkLease(processId, creationTime, userSid,
                Clock.UtcNow + UpdateNetworkGrantPolicy.MaxLifetime, process);
            Leases.Add(lease);
            return lease;
        }

        // The controller's release ends only its own process's lease.
        internal List<UpdateNetworkLease> Release(uint processId, long creationTime) =>
            RemoveWhere(lease => lease.SameProcess(processId, creationTime));

        // Expired leases and leases whose process has exited.
        internal List<UpdateNetworkLease> RemoveDue(Func<UpdateNetworkLease, bool> processExited) =>
            RemoveWhere(lease => lease.ExpiresUtc <= Clock.UtcNow || processExited(lease));

        internal List<UpdateNetworkLease> Clear() => RemoveWhere(_ => true);

        internal TimeSpan Remaining(UpdateNetworkLease lease)
        {
            TimeSpan left = lease.ExpiresUtc - Clock.UtcNow;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }

        private List<UpdateNetworkLease> RemoveWhere(Func<UpdateNetworkLease, bool> predicate)
        {
            var removed = new List<UpdateNetworkLease>();
            for (int i = Leases.Count - 1; i >= 0; --i)
            {
                if (predicate(Leases[i]))
                {
                    removed.Insert(0, Leases[i]);
                    Leases.RemoveAt(i);
                }
            }
            return removed;
        }
    }
}

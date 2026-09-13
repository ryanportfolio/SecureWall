using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using System.Net;
using System.Globalization;

namespace pylorak.TinyWall.Prompting
{
    internal static class TemporaryExceptionEnforcement
    {
        internal static void Enqueue(Action acquire, Action enqueue, Action release)
        {
            acquire();
            try { enqueue(); }
            catch
            {
                release();
                throw;
            }
        }

        internal static T Run<T>(Func<T> enforce, Action release)
        {
            try { return enforce(); }
            finally { release(); }
        }
    }

    internal static class EnforcementPolicy
    {
        internal static T LoadConfiguration<T>(Action probe, Func<T> load, Func<T> defaults)
        {
            // File.Exists hides access and I/O errors. Only a missing path is first run.
            try { probe(); }
            catch (FileNotFoundException) { return defaults(); }
            catch (DirectoryNotFoundException) { return defaults(); }
            // A deletion race or corrupt content after a successful probe is a failed load.
            return load();
        }

        internal static string? NormalizeApplicationPath(string? path, Func<string, string> toNative)
        {
            if (string.IsNullOrEmpty(path) || string.Equals(path, "System", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "Registry", StringComparison.OrdinalIgnoreCase))
                return path;
            return toNative(path);
        }

        internal static List<T> NormalizeRules<T>(IEnumerable<T> rules, Func<T, string?> application,
            Action<T, string?> setApplication, Func<string, string> toNative, Action<string> unavailable)
        {
            var resolved = new List<T>();
            foreach (T rule in rules)
            {
                string? path = application(rule);
                string? normalized;
                try { normalized = NormalizeApplicationPath(path, toNative); }
                catch (DriveNotFoundException) when (IsVolumeSubject(path))
                {
                    // Do not replace an unresolved subject with null (a wildcard), and do
                    // not remove it from saved policy. A later reload retries the mapping.
                    unavailable(path!);
                    continue;
                }
                setApplication(rule, normalized);
                resolved.Add(rule);
            }
            return resolved;
        }

        private static bool IsVolumeSubject(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string value = path!;
            if (value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                value.StartsWith(@"\??\", StringComparison.Ordinal)) value = value.Substring(4);
            int rootLength;
            if (value.Length >= 3 && ((value[0] >= 'A' && value[0] <= 'Z') ||
                (value[0] >= 'a' && value[0] <= 'z')) && value[1] == ':' && value[2] == '\\')
                rootLength = 3;
            else if (value.Length >= 45 && value.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase) &&
                value[43] == '}' && value[44] == '\\' && Guid.TryParseExact(value.Substring(7, 36), "D", out _))
                rootLength = 45;
            else return false;
            // PathMapper classifies even malformed Volume{...} input as a missing
            // drive. Only a syntactically valid volume subject may become dormant.
            for (int i = rootLength; i < value.Length; ++i)
                if (value[i] < 32 || "<>\"|:*?".IndexOf(value[i]) >= 0) return false;
            return value.Length > rootLength;
        }

        internal static bool IsExpired(DateTime created, int minutes, DateTime now) =>
            minutes > 0 && now.ToUniversalTime().Ticks - created.ToUniversalTime().Ticks >=
                (long)minutes * TimeSpan.TicksPerMinute;

        internal static void ValidateRemoteAddresses(string? addresses)
        {
            if (addresses is null || addresses.Length == 0) return;
            foreach (string value in addresses.Split(','))
            {
                if (value == "LocalSubnet" || value == "DefaultGateway" || value == "DNS") continue;
                string[] parts = value.Split('/');
                if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address))
                    throw new FormatException("Invalid remote address: " + value);
                int maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix) ||
                    prefix < 0 || prefix > maxPrefix))
                    throw new FormatException("Invalid remote address prefix: " + value);
            }
        }

        internal static bool OptionalPermitEnabled(bool blockAll, bool configured) => !blockAll && configured;
        // Deny-only baseline remains below runtime default block for prompt authority.
        internal static ulong RecoveryBlockWeight(ulong runtimeDefaultBlockWeight)
        {
            if (runtimeDefaultBlockWeight < 2)
                throw new ArgumentOutOfRangeException(nameof(runtimeDefaultBlockWeight));
            return runtimeDefaultBlockWeight - 2;
        }


    }

    // Used by the minute tick before optional housekeeping. Revocation is mandatory even
    // when cloning, journaling, saving, installing, or rolling back a policy fails.
    internal static class ExpiringPolicyMaintenance
    {
        internal static void Run<T>(Func<T> clone, Func<T, bool> prune, Action<T> apply, Action failClosed)
        {
            try
            {
                T candidate = clone();
                if (prune(candidate)) apply(candidate);
            }
            catch
            {
                failClosed();
                throw;
            }
        }
    }

    internal static class EnvironmentalPolicyReload
    {
        internal static bool EnumerationChanged(bool succeeded, Func<bool> readChanges)
        {
            if (!succeeded) throw new InvalidOperationException("Active network adapter enumeration failed.");
            return readChanges();
        }

        internal static void Run(Action reload, Action failClosed)
        {
            try { reload(); }
            catch
            {
                failClosed();
                throw;
            }
        }
    }

    internal static class RuntimeSessionRevocation
    {
        // Called only by the enforcement thread after stopping work and disposing the
        // subscription. A failed unsubscribe/transaction abort may retain SafeHandle refs.
        // Native close bypasses those refs, aborts outstanding transactions, and cancels
        // subscriptions. SetHandleAsInvalid prevents any subsequent managed double-close.
        internal static void Close(SafeHandle engine, Func<IntPtr, uint> closeNative, Action<Exception> terminate,
            Action? disposeSubscription = null)
        {
            // Consume its SafeHandle while the engine is still valid. Failure is handled
            // by closing the native session, which also cancels the remaining subscription.
            try { disposeSubscription?.Invoke(); } catch { }
            bool borrowed = false;
            try
            {
                engine.DangerousAddRef(ref borrowed);
                if (engine.IsClosed || engine.IsInvalid)
                    throw new InvalidOperationException("Cannot confirm native WFP session closure from a disposed handle.");
                uint error = closeNative(engine.DangerousGetHandle());
                if (error != 0)
                    throw new InvalidOperationException("FwpmEngineClose0 failed: 0x" + error.ToString("X8"));
                engine.SetHandleAsInvalid();
            }
            catch (Exception error)
            {
                // Production terminates the process: RPC rundown withdraws dynamic grants.
                // An ordinary error response must never escape with unconfirmed revocation.
                terminate(error);
                throw new InvalidOperationException("Process termination unexpectedly returned after WFP revocation failure.", error);
            }
            finally
            {
                if (borrowed) engine.DangerousRelease();
            }
        }
    }

    // Persistence uses AtomicFileUpdater; enforcement must itself be transactional.
    // Publish only after both succeed. A failed compensating write disables runtime grants.
    internal static class PolicyChangeTransaction
    {
        internal static void Apply(Action persist, Action enforce, Action restore,
            Action publish, Action failClosed, Action? prepareRecovery = null, Action? completeRecovery = null)
        {
            // The old-policy snapshot must be saved before overwriting the active configuration.
            // Preparation may be retried after uncertain completion: always save the captured
            // prior policy, never the candidate or a fresh read of the active configuration.
            prepareRecovery?.Invoke();
            try
            {
                persist();
                enforce();
            }
            catch (Exception applyError)
            {
                try
                {
                    // A write can replace the active file and then throw while flushing it.
                    // Invocation, not successful return, is the compensation boundary.
                    restore();
                    completeRecovery?.Invoke();
                }
                catch (Exception restoreError)
                {
                    failClosed();
                    throw new AggregateException("Policy application and recovery failed; runtime permissions were withdrawn.", applyError, restoreError);
                }
                throw;
            }
            try { completeRecovery?.Invoke(); }
            catch (Exception completionError)
            {
                // WFP already committed. Withdraw grants before attempting more storage work.
                // Completion may have removed the snapshot before throwing. Re-establish the
                // prior snapshot, then restore; attempt both even when storage keeps failing.
                // Do not retry deletion on this path or ever overwrite the snapshot with a candidate.
                failClosed();
                var errors = new List<Exception> { completionError };
                try { prepareRecovery?.Invoke(); }
                catch (Exception prepareError) { errors.Add(prepareError); }
                try { restore(); }
                catch (Exception restoreError) { errors.Add(restoreError); }
                if (errors.Count > 1)
                    throw new AggregateException("Policy completion and recovery failed; runtime permissions were withdrawn.", errors);
                throw;
            }
            publish();
        }
    }

    internal static class PolicyRecoveryJournal
    {
        internal static void Recover(bool exists, Action restore, Action clear)
        {
            if (!exists)
                return;
            // Never clear the marker or proceed to normal config loading unless restoration succeeds.
            // File-content flushes and ordinary deletion do not prove power-loss ordering of
            // directory entries. A lost deletion can replay an older policy; this is process-failure
            // recovery, not a crash-tested filesystem commit protocol.
            restore();
            clear();
        }
    }

    internal sealed class UserActivityTimeout
    {
        private readonly IClock clock;
        private readonly TimeSpan timeout;
        private long lastActivityUtcTicks;

        internal UserActivityTimeout(IClock clock, TimeSpan timeout)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.timeout = timeout;
            lastActivityUtcTicks = clock.UtcNow.UtcDateTime.Ticks;
        }

        internal void Record(bool userInitiated)
        {
            if (userInitiated)
                Interlocked.Exchange(ref lastActivityUtcTicks, clock.UtcNow.UtcDateTime.Ticks);
        }

        internal void LockIfExpired(Action lockNow)
        {
            if (Expired) lockNow();
        }

        internal bool Expired => clock.UtcNow.UtcDateTime.Ticks - Interlocked.Read(ref lastActivityUtcTicks) > timeout.Ticks;
    }
}

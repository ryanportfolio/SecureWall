using System;
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
        // Recovery baseline ordering inside the SecureWall sublayer:
        //   runtime default block (defaultBlock) > recovery permit (defaultBlock - 1) > recovery deny (defaultBlock - 2).
        // Runtime filters always outrank the baseline, so BlockAll and explicit user blocks
        // still cover DHCP and DNS while the service runs. The permits only matter when the
        // dynamic session is gone (service stopped, crashed, or the boot window).
        internal static ulong RecoveryBlockWeight(ulong runtimeDefaultBlockWeight)
        {
            if (runtimeDefaultBlockWeight < 2)
                throw new ArgumentOutOfRangeException(nameof(runtimeDefaultBlockWeight));
            return runtimeDefaultBlockWeight - 2;
        }

        internal static ulong RecoveryPermitWeight(ulong runtimeDefaultBlockWeight)
        {
            if (runtimeDefaultBlockWeight < 2)
                throw new ArgumentOutOfRangeException(nameof(runtimeDefaultBlockWeight));
            return runtimeDefaultBlockWeight - 1;
        }

        private const byte ProtocolTcp = 6;
        private const byte ProtocolUdp = 17;

        // Minimum a machine needs to obtain a lease and resolve names without the service.
        internal static IReadOnlyList<RecoveryPermitRule> RecoveryPermitRules() => new[]
        {
            new RecoveryPermitRule("SecureWall recovery permit DHCPv4 request", false, false, ProtocolUdp, 68, 67),
            new RecoveryPermitRule("SecureWall recovery permit DHCPv6 request", true, false, ProtocolUdp, 546, 547),
            new RecoveryPermitRule("SecureWall recovery permit DNS UDP v4", false, false, ProtocolUdp, null, 53),
            new RecoveryPermitRule("SecureWall recovery permit DNS UDP v6", true, false, ProtocolUdp, null, 53),
            new RecoveryPermitRule("SecureWall recovery permit DNS TCP v4", false, false, ProtocolTcp, null, 53),
            new RecoveryPermitRule("SecureWall recovery permit DNS TCP v6", true, false, ProtocolTcp, null, 53),
            new RecoveryPermitRule("SecureWall recovery permit DHCPv4 reply", false, true, ProtocolUdp, 68, 67),
            new RecoveryPermitRule("SecureWall recovery permit DHCPv6 reply", true, true, ProtocolUdp, 546, 547),
        };
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
            // The durable old-policy snapshot must exist before overwriting the active configuration.
            prepareRecovery?.Invoke();
            bool persisted = false;
            try
            {
                persist();
                persisted = true;
                enforce();
            }
            catch (Exception applyError)
            {
                try
                {
                    if (persisted)
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
            catch
            {
                // Enforcement succeeded but recovery bookkeeping is uncertain. Keep the old snapshot
                // for startup recovery and withdraw grants before returning failure.
                failClosed();
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

        internal bool Expired => clock.UtcNow.UtcDateTime.Ticks - Interlocked.Read(ref lastActivityUtcTicks) > timeout.Ticks;
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace pylorak.TinyWall
{
    [Flags]
    internal enum AuditPolicyFlags : uint
    {
        Unchanged = 0,
        Success = 1,
        Failure = 2,
        None = 4,
    }

    internal interface IAuditPolicyBackend
    {
        AuditPolicyFlags Query(Guid subcategory);
        void Set(Guid subcategory, AuditPolicyFlags flags);
    }

    // Durable record of the machine's original audit policy, written before the
    // first mutation and cleared only after restoration succeeds. Survives crashes,
    // FailFast, and Process.Kill so a later start or uninstall can put the original
    // value back.
    internal interface IAuditPolicyJournal
    {
        bool TryRead(Guid subcategory, out AuditPolicyFlags original);
        void Write(Guid subcategory, AuditPolicyFlags original);
        void Clear(Guid subcategory);
        // Healthy entries only; names of records that could not be decoded go to
        // malformed so the caller can restore everything else and still report them.
        IReadOnlyList<KeyValuePair<Guid, AuditPolicyFlags>> ReadAll(out IReadOnlyList<string> malformed);
    }

    internal sealed class AuditPolicyLease : IDisposable
    {
        // Leases on one subcategory nest inside this process (failure auditing plus
        // learning-mode success auditing). The outermost lease captures the machine's
        // true original; whichever lease first changes the live policy journals that
        // true original, so a journal entry exists whenever live differs from it.
        // An inner lease restores to the value it found on acquire; the entry is
        // cleared once a restore puts the true original back. Dispose in LIFO order.
        private static readonly object Sync = new object();
        private static readonly Dictionary<Guid, int> LiveLeases = new Dictionary<Guid, int>();
        private static readonly Dictionary<Guid, AuditPolicyFlags> TrueOriginals = new Dictionary<Guid, AuditPolicyFlags>();
        private static readonly HashSet<Guid> Journaled = new HashSet<Guid>();

        private readonly IAuditPolicyBackend _backend;
        private readonly IAuditPolicyJournal _journal;
        private readonly Guid _subcategory;
        private readonly AuditPolicyFlags _restoreValue;
        private readonly bool _changed;
        private int _disposed;

        private AuditPolicyLease(
            IAuditPolicyBackend backend,
            IAuditPolicyJournal journal,
            Guid subcategory,
            AuditPolicyFlags restoreValue,
            bool changed)
        {
            _backend = backend;
            _journal = journal;
            _subcategory = subcategory;
            _restoreValue = restoreValue;
            _changed = changed;
        }

        internal static AuditPolicyLease Acquire(
            IAuditPolicyBackend backend,
            Guid subcategory,
            AuditPolicyFlags requiredFlags,
            IAuditPolicyJournal journal)
        {
            if (backend == null)
                throw new ArgumentNullException(nameof(backend));
            if (journal == null)
                throw new ArgumentNullException(nameof(journal));
            if ((requiredFlags & ~(AuditPolicyFlags.Success | AuditPolicyFlags.Failure)) != 0)
                throw new ArgumentOutOfRangeException(nameof(requiredFlags));

            lock (Sync)
            {
                LiveLeases.TryGetValue(subcategory, out int live);
                bool nested = live > 0;

                // The value this lease restores on dispose: the machine's original for
                // the outermost lease, the outer lease's live value for a nested one.
                AuditPolicyFlags restoreValue;
                if (!nested && journal.TryRead(subcategory, out AuditPolicyFlags journaled))
                {
                    // Stale record from an unclean exit: the live value is already ours.
                    // Put the original back before snapshotting; keep the record until
                    // the backend accepted it.
                    backend.Set(subcategory, journaled);
                    journal.Clear(subcategory);
                    restoreValue = journaled;
                }
                else
                {
                    restoreValue = backend.Query(subcategory);
                }

                if (!nested)
                {
                    TrueOriginals[subcategory] = restoreValue;
                    Journaled.Remove(subcategory);
                }

                AuditPolicyFlags enabled = restoreValue & (AuditPolicyFlags.Success | AuditPolicyFlags.Failure);
                AuditPolicyFlags target = enabled | requiredFlags;
                if (target == AuditPolicyFlags.Unchanged)
                    target = AuditPolicyFlags.None;

                bool changed = target != restoreValue;
                try
                {
                    if (changed)
                    {
                        // Invariant: a journal entry holding the true original exists
                        // whenever the live policy differs from it, no matter which
                        // lease made the change (an outer lease may have changed nothing).
                        if (Journaled.Add(subcategory))
                            journal.Write(subcategory, TrueOriginals[subcategory]);
                        backend.Set(subcategory, target);
                    }
                }
                catch
                {
                    if (!nested)
                        ForgetSubcategory(subcategory);
                    throw;
                }

                LiveLeases[subcategory] = live + 1;
                return new AuditPolicyLease(backend, journal, subcategory, restoreValue, changed);
            }
        }

        private static void ForgetSubcategory(Guid subcategory)
        {
            LiveLeases.Remove(subcategory);
            TrueOriginals.Remove(subcategory);
            Journaled.Remove(subcategory);
        }

        // Crash recovery: restore every healthy journaled subcategory and clear each
        // record once its backend write succeeded. Failed and malformed entries stay
        // journaled; after every entry was attempted, one exception reports the first
        // failure and names the records that could not be decoded.
        internal static void RestoreFromJournal(IAuditPolicyBackend backend, IAuditPolicyJournal journal)
        {
            if (backend == null)
                throw new ArgumentNullException(nameof(backend));
            if (journal == null)
                throw new ArgumentNullException(nameof(journal));

            lock (Sync)
            {
                Exception? first = null;
                IReadOnlyList<KeyValuePair<Guid, AuditPolicyFlags>> entries = journal.ReadAll(out IReadOnlyList<string> malformed);
                foreach (var entry in entries)
                {
                    try
                    {
                        backend.Set(entry.Key, entry.Value);
                        journal.Clear(entry.Key);
                    }
                    catch (Exception exception)
                    {
                        first ??= exception;
                    }
                }

                if (first != null || malformed.Count > 0)
                {
                    string message = "Audit policy journal restoration did not complete.";
                    if (malformed.Count > 0)
                        message += " Skipped malformed records: " + string.Join(", ", malformed) + ".";
                    throw new InvalidOperationException(message, first);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (Sync)
            {
                try
                {
                    if (_changed)
                        _backend.Set(_subcategory, _restoreValue);

                    // Clear only once the live policy is back at the true original;
                    // an inner lease restoring to an outer lease's value leaves it.
                    if (Journaled.Contains(_subcategory) &&
                        TrueOriginals.TryGetValue(_subcategory, out AuditPolicyFlags trueOriginal) &&
                        _restoreValue == trueOriginal)
                    {
                        _journal.Clear(_subcategory);
                        Journaled.Remove(_subcategory);
                    }
                }
                finally
                {
                    // A failed restore leaves the journal in place; the next Acquire or
                    // RestoreFromJournal retries it, so this lease must stop counting as live.
                    if (LiveLeases.TryGetValue(_subcategory, out int live) && live > 1)
                        LiveLeases[_subcategory] = live - 1;
                    else
                        ForgetSubcategory(_subcategory);
                }
            }
        }
    }

    internal sealed class WindowsAuditPolicyBackend : IAuditPolicyBackend
    {
        internal static WindowsAuditPolicyBackend Instance { get; } = new WindowsAuditPolicyBackend();

        private WindowsAuditPolicyBackend()
        {
        }

        public AuditPolicyFlags Query(Guid subcategory)
        {
            IntPtr policyBuffer = IntPtr.Zero;
            try
            {
                if (!NativeMethods.AuditQuerySystemPolicy(ref subcategory, 1, out policyBuffer))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (policyBuffer == IntPtr.Zero)
                    throw new InvalidOperationException("AuditQuerySystemPolicy returned no policy data.");

                NativeMethods.AUDIT_POLICY_INFORMATION policy =
                    Marshal.PtrToStructure<NativeMethods.AUDIT_POLICY_INFORMATION>(policyBuffer);
                return policy.AuditingInformation;
            }
            finally
            {
                if (policyBuffer != IntPtr.Zero)
                    NativeMethods.AuditFree(policyBuffer);
            }
        }

        public void Set(Guid subcategory, AuditPolicyFlags flags)
        {
            var policy = new NativeMethods.AUDIT_POLICY_INFORMATION
            {
                AuditSubCategoryGuid = subcategory,
                AuditingInformation = flags,
                AuditCategoryGuid = Guid.Empty,
            };

            if (!NativeMethods.AuditSetSystemPolicy(ref policy, 1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct AUDIT_POLICY_INFORMATION
            {
                internal Guid AuditSubCategoryGuid;
                internal AuditPolicyFlags AuditingInformation;
                internal Guid AuditCategoryGuid;
            }

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool AuditQuerySystemPolicy(
                [In] ref Guid pSubCategoryGuids,
                uint policyCount,
                out IntPtr ppAuditPolicy);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool AuditSetSystemPolicy(
                [In] ref AUDIT_POLICY_INFORMATION pAuditPolicy,
                uint policyCount);

            [DllImport("advapi32.dll")]
            [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool AuditFree(IntPtr buffer);
        }
    }
}

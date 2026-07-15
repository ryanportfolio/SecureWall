using System;
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

    internal sealed class AuditPolicyLease : IDisposable
    {
        private readonly IAuditPolicyBackend _backend;
        private readonly Guid _subcategory;
        private readonly AuditPolicyFlags _original;
        private readonly bool _changed;
        private int _disposed;

        private AuditPolicyLease(
            IAuditPolicyBackend backend,
            Guid subcategory,
            AuditPolicyFlags original,
            bool changed)
        {
            _backend = backend;
            _subcategory = subcategory;
            _original = original;
            _changed = changed;
        }

        internal static AuditPolicyLease Acquire(
            IAuditPolicyBackend backend,
            Guid subcategory,
            AuditPolicyFlags requiredFlags)
        {
            if (backend == null)
                throw new ArgumentNullException(nameof(backend));
            if ((requiredFlags & ~(AuditPolicyFlags.Success | AuditPolicyFlags.Failure)) != 0)
                throw new ArgumentOutOfRangeException(nameof(requiredFlags));

            AuditPolicyFlags original = backend.Query(subcategory);
            AuditPolicyFlags enabled = original & (AuditPolicyFlags.Success | AuditPolicyFlags.Failure);
            AuditPolicyFlags target = enabled | requiredFlags;
            if (target == AuditPolicyFlags.Unchanged)
                target = AuditPolicyFlags.None;

            bool changed = target != original;
            if (changed)
                backend.Set(subcategory, target);

            return new AuditPolicyLease(backend, subcategory, original, changed);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (_changed)
                _backend.Set(_subcategory, _original);
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

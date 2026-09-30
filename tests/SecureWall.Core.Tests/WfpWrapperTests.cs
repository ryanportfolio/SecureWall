using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using pylorak.Windows.WFP;
using WfpInterop = pylorak.Windows.WFP.Interop;

namespace SecureWall.Core.Tests
{
    internal static class WfpWrapperTests
    {
        internal static IEnumerable<(string Name, Action Test)> Cases
        {
            get
            {
                yield return ("condition list indexer setter transfers references", IndexerSetterTransfersReferences);
                yield return ("condition list indexer self-assignment keeps the reference", IndexerSelfAssignmentKeepsReference);
                yield return ("condition list indexer bad index leaves references unchanged", IndexerBadIndexLeavesReferences);
                yield return ("condition list indexer rejects writes after dispose", IndexerRejectsWritesAfterDispose);
                yield return ("user id condition builds a well-formed SDDL", UserIdConditionBuildsWellFormedSddl);
                yield return ("native SID copy preserves the SID bytes", NativeSidCopyPreservesBytes);
                yield return ("filter enumerator rejects a provider key without a template", FilterEnumeratorRejectsProviderWithoutTemplate);
            }
        }

        private static void IndexerSetterTransfersReferences()
        {
            var first = new ProtocolFilterCondition(6);
            var second = new ProtocolFilterCondition(17);
            var list = new FilterConditionList();
            list.Add(first);
            list[0] = second;

            AssertEx.Equal(0, first.ReferenceCount);
            AssertEx.Equal(1, second.ReferenceCount);
            AssertEx.True(ReferenceEquals(second, list[0]));

            // Dispose only after the counts check out: with an unbalanced count, Clear()
            // trips FilterCondition.RemoveRef's Debug.Assert, which fail-fasts the process.
            list.Dispose();
            AssertEx.Equal(0, second.ReferenceCount);
        }

        private static void IndexerSelfAssignmentKeepsReference()
        {
            var item = new ProtocolFilterCondition(6);
            using var list = new FilterConditionList();
            list.Add(item);

            list[0] = item;

            AssertEx.Equal(1, item.ReferenceCount);
            AssertEx.True(ReferenceEquals(item, list[0]));
        }

        private static void IndexerBadIndexLeavesReferences()
        {
            var item = new ProtocolFilterCondition(6);
            var replacement = new ProtocolFilterCondition(17);
            using var list = new FilterConditionList();
            list.Add(item);

            AssertEx.Throws<ArgumentOutOfRangeException>(() => list[1] = replacement);

            AssertEx.Equal(1, item.ReferenceCount);
            AssertEx.Equal(0, replacement.ReferenceCount);
        }

        private static void IndexerRejectsWritesAfterDispose()
        {
            var replacement = new ProtocolFilterCondition(17);
            var list = new FilterConditionList();
            list.Add(new ProtocolFilterCondition(6));
            list.Dispose();

            AssertEx.Throws<ObjectDisposedException>(() => list[0] = replacement);
            AssertEx.Equal(0, replacement.ReferenceCount);
        }

        // Upstream a56488f removed an unmatched closing parenthesis from the SDDL. This
        // Windows parser tolerated it, so the test pins the resulting descriptor rather
        // than distinguishing the two strings.
        private static void UserIdConditionBuildsWellFormedSddl()
        {
            if (!OperatingSystem.IsWindows()) return;

            using var condition = new UserIdFilterCondition("S-1-5-18", RemoteOrLocal.Local);

            AssertEx.Equal(ConditionKeys.FWPM_CONDITION_ALE_USER_ID, condition.FieldKey);
            var value = condition.ConditionValue;
            AssertEx.Equal(WfpInterop.FWP_DATA_TYPE.FWP_SECURITY_DESCRIPTOR_TYPE, value.type);

            var blob = Marshal.PtrToStructure<WfpInterop.FWP_BYTE_BLOB>(value.value.sd);
            var bytes = new byte[blob.size];
            Marshal.Copy(blob.data, bytes, 0, bytes.Length);
            var sd = new RawSecurityDescriptor(bytes, 0);

            AssertEx.Equal("O:LSD:(A;;CC;;;SY)", sd.GetSddlForm(AccessControlSections.All));
        }

        private static void NativeSidCopyPreservesBytes()
        {
            // Binary S-1-5-32-544: revision, subauthority count, big-endian authority, little-endian subauthorities.
            var expected = new byte[] { 1, 2, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0, 0x20, 0x02, 0, 0 };

            IntPtr source = Marshal.AllocHGlobal(expected.Length);
            try
            {
                Marshal.Copy(expected, 0, source, expected.Length);
                using var copy = PInvokeHelper.CopyNativeSid(source);

                var actual = new byte[expected.Length];
                Marshal.Copy(copy.DangerousGetHandle(), actual, 0, actual.Length);
                AssertEx.SequenceEqual(expected, actual);
            }
            finally
            {
                Marshal.FreeHGlobal(source);
            }
        }

        // The argument check runs before the engine is touched, so no WFP session is needed.
        private static void FilterEnumeratorRejectsProviderWithoutTemplate()
        {
            AssertEx.Throws<ArgumentNullException>(() => new FilterKeyEnumerator(null!, null, Guid.NewGuid()));
            AssertEx.Throws<ArgumentNullException>(() => new FilterEnumerator(null!, null, false, Guid.NewGuid()));
        }
    }
}

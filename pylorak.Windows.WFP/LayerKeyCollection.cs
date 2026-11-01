using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Security;

namespace pylorak.Windows.WFP
{
    // Keys of every WFP layer on this system, so a caller can find its own objects
    // in layers it was not compiled to know about.
    public class LayerKeyCollection : System.Collections.ObjectModel.ReadOnlyCollection<Guid>
    {
        [SuppressUnmanagedCodeSecurity]
        internal static class NativeMethods
        {
            [DllImport("FWPUClnt.dll", EntryPoint = "FwpmLayerCreateEnumHandle0")]
            [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
            internal static extern uint FwpmLayerCreateEnumHandle0(
                [In] FwpmEngineSafeHandle engineHandle,
                IntPtr enumTemplate,
                out IntPtr enumHandle);

            [DllImport("FWPUClnt.dll", EntryPoint = "FwpmLayerEnum0")]
            internal static extern uint FwpmLayerEnum0(
                [In] FwpmEngineSafeHandle engineHandle,
                [In] FwpmLayerEnumSafeHandle enumHandle,
                uint numEntriesRequested,
                out FwpmMemorySafeHandle entries,
                out uint numEntriesReturned);
        }

        internal LayerKeyCollection(Engine engine)
            : base(new List<Guid>())
        {
            FwpmLayerEnumSafeHandle? enumSafeHandle = null;

            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                var err = NativeMethods.FwpmLayerCreateEnumHandle0(engine.NativePtr, IntPtr.Zero, out IntPtr outHndl);
                if (0 == err)
                    enumSafeHandle = new FwpmLayerEnumSafeHandle(outHndl, engine.NativePtr);
                else
                    throw new WfpException(err, "FwpmLayerCreateEnumHandle0");

                while (true)
                {
                    const uint numEntriesRequested = 32;

                    FwpmMemorySafeHandle? entries = null;
                    try
                    {
                        // FwpmLayerEnum0() returns a list of pointers to FWPM_LAYER0 in batches.
                        err = NativeMethods.FwpmLayerEnum0(engine.NativePtr, enumSafeHandle, numEntriesRequested, out entries, out uint numEntriesReturned);
                        if (0 != err)
                            throw new WfpException(err, "FwpmLayerEnum0");

                        // layerKey is the first member of FWPM_LAYER0.
                        IntPtr[] ptrList = PInvokeHelper.PtrToStructureArray<IntPtr>(entries.DangerousGetHandle(), numEntriesReturned, (uint)IntPtr.Size);
                        for (int i = 0; i < numEntriesReturned; ++i)
                            Items.Add(Marshal.PtrToStructure<Guid>(ptrList[i]));

                        if (numEntriesReturned < numEntriesRequested)
                            break;
                    }
                    finally
                    {
                        entries?.Dispose();
                    }
                }
            }
            finally
            {
                enumSafeHandle?.Dispose();
            }
        }
    }

    public sealed class FwpmLayerEnumSafeHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        [SuppressUnmanagedCodeSecurity]
        private static class NativeMethods
        {
            [DllImport("FWPUClnt.dll", EntryPoint = "FwpmLayerDestroyEnumHandle0")]
            [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
            internal static extern uint FwpmLayerDestroyEnumHandle0(
                [In] FwpmEngineSafeHandle engineHandle,
                [In] IntPtr enumHandle);
        }

        private readonly FwpmEngineSafeHandle _safeEngineHandle;

        public FwpmLayerEnumSafeHandle(IntPtr wrappedHndl, FwpmEngineSafeHandle engineHndl) : base(true)
        {
            bool success = false;
            engineHndl.DangerousAddRef(ref success);
            if (!success)
            {
                _ = NativeMethods.FwpmLayerDestroyEnumHandle0(engineHndl, wrappedHndl);
                throw new Exception("Failed to add reference.");
            }

            _safeEngineHandle = engineHndl;
            SetHandle(wrappedHndl);
        }

        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        [PrePrepareMethod]
        protected override bool ReleaseHandle()
        {
            if (0 == NativeMethods.FwpmLayerDestroyEnumHandle0(_safeEngineHandle, handle))
            {
                _safeEngineHandle.DangerousRelease();
                return true;
            }
            return false;
        }
    }
}

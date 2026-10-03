using System;

namespace pylorak.Windows.WFP
{
    public class WfpException : Exception
    {
        public readonly uint ErrorCode;

        // Hex plus the system message text, so a log line names the WFP error
        // (for example 0x8032000B: The call is not allowed from within a dynamic session).
        private static string MakeErrorMsg(uint errCode, string wfpFunction)
        { return $"{wfpFunction} returned error code 0x{errCode:X8}: {new System.ComponentModel.Win32Exception(unchecked((int)errCode)).Message}"; }

        public WfpException(uint errCode, string wfpFunction)
            : base(MakeErrorMsg(errCode, wfpFunction))
        {
            ErrorCode = errCode;
        }
    }

}

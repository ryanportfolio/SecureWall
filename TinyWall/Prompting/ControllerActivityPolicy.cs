namespace pylorak.TinyWall.Prompting
{
    // Decides which controller requests extend the ten-minute password unlock window.
    // Background reads, automatic popup timeouts, rejected requests and prompt actions on
    // unknown or expired tokens are not owner activity: counting them would let blocked
    // traffic or any same-user process keep the service unlocked indefinitely.
    internal static class ControllerActivityPolicy
    {
        internal static bool IsUserActivity(MessageType request, MessageType responseType, PromptActionStatus? promptStatus)
        {
            if (responseType == MessageType.RESPONSE_ERROR ||
                responseType == MessageType.RESPONSE_LOCKED ||
                responseType == MessageType.COM_ERROR)
                return false;

            switch (request)
            {
                case MessageType.UNLOCK:
                case MessageType.MODE_SWITCH:
                case MessageType.PUT_SETTINGS:
                case MessageType.SET_PASSPHRASE:
                    return true;
                case MessageType.DISMISS_PROMPT:
                    // Only an explicit dismissal that consumed a valid pending token.
                    return responseType == MessageType.DISMISS_PROMPT && promptStatus == PromptActionStatus.Dismissed;
                case MessageType.ALLOW_PROMPT:
                    return responseType == MessageType.ALLOW_PROMPT && promptStatus == PromptActionStatus.Allowed;
                default:
                    // Includes DISMISS_PROMPT_TIMEOUT and every read request.
                    return false;
            }
        }
    }
}

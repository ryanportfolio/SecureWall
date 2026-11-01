namespace pylorak.TinyWall
{
    // Possible message types from controller to service
    public enum MessageType
    {
        // General responses
        INVALID_COMMAND,
        RESPONSE_ERROR,
        RESPONSE_LOCKED,
        COM_ERROR,

        // Read commands (>31)
        GET_SETTINGS = 32,
        GET_PROCESS_PATH,
        READ_FW_LOG,
        IS_LOCKED,
        READ_PENDING_PROMPTS,

        // Unprivileged write commands (>1023)
        UNLOCK = 1024,
        DISMISS_PROMPT,
        // Automatic popup timeout. Same queue effect as DISMISS_PROMPT, never user activity.
        DISMISS_PROMPT_TIMEOUT,
        // The popup's Ignore button: DISMISS_PROMPT, and the identity is also saved to the Ignored
        // list. While locked, an entry to save gets Locked back and the prompt stays pending. Never grants traffic.
        IGNORE_PROMPT,
        // Check for updates: ends the caller's own transient network permit. Never grants.
        UPDATE_NETWORK_RELEASE = 1100,

        // Privileged write commands (>2047)
        MODE_SWITCH = 2048,
        REINIT,
        PUT_SETTINGS,
        LOCK,
        SET_PASSPHRASE,
        STOP_SERVICE,
        MINUTE_TIMER,
        REENUMERATE_ADDRESSES,

        // Service-to-client messages
        DATABASE_UPDATED,

        // Check for updates: a transient outbound TCP 443 permit for the calling user's
        // SecureWall controller (UpdateNetworkGrantPolicy). Password-gated like any policy change.
        UPDATE_NETWORK_GRANT = 2600,

        // Prompt allow changes firewall policy and remains password-gated.
        ALLOW_PROMPT = 3072,
        // Allow, plus an Always allow rule for the file name and its verified signer.
        ALLOW_PROMPT_ALWAYS,

        // Service-to-service only (>4095)
        ADD_TEMPORARY_EXCEPTION = 4096,
        RELOAD_WFP_FILTERS,
        DISPLAY_POWER_EVENT,
        // A blocked file matched an Always allow rule; add exceptions for the queued paths.
        AUTO_ALLOW_SIGNED,
        // An update permit's lifetime ended or its process exited; remove what is due.
        UPDATE_NETWORK_REVIEW = 4200,
    }
}

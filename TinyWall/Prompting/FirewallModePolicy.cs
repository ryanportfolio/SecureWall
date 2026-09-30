namespace pylorak.TinyWall.Prompting
{
    // Which firewall modes the service accepts. Learning mode was removed from
    // SecureWall; FirewallMode.Learning keeps its numeric value only so older
    // messages and configurations still parse. It is never applied.
    internal static class FirewallModePolicy
    {
        // Modes a mode switch may select.
        internal static bool IsRuntimeMode(FirewallMode mode) =>
            mode == FirewallMode.Normal || mode == FirewallMode.BlockAll ||
            mode == FirewallMode.AllowOutgoing || mode == FirewallMode.Disabled;

        // Modes that may be saved as the startup mode. Disabled is runtime-only.
        internal static bool IsStartupMode(FirewallMode mode) =>
            mode == FirewallMode.Normal || mode == FirewallMode.BlockAll || mode == FirewallMode.AllowOutgoing;

        // Stored configuration: any unsupported startup mode, including Learning, starts Normal.
        internal static FirewallMode NormalizeStartupMode(FirewallMode mode) =>
            IsStartupMode(mode) ? mode : FirewallMode.Normal;

        // Imported or submitted configuration: Learning becomes Normal. Other
        // unsupported values pass through so the settings commit still rejects them.
        internal static FirewallMode MigrateStartupMode(FirewallMode mode) =>
            mode == FirewallMode.Learning ? FirewallMode.Normal : mode;
    }
}

namespace pylorak.TinyWall.Prompting
{
    // Popup wording for a prompt whose Allow is disabled. Pure so the text, including the
    // admin command, is testable without WinForms.
    internal static class PromptNoticeText
    {
        internal static string Blocked(PromptAllowBlocker blocker, string? serviceName) => blocker switch
        {
            PromptAllowBlocker.ServiceSidUnavailable =>
                $"{serviceName} has no service SID, so a rule for it cannot match. Admin fix: {SidTypeCommand(serviceName)}, then restart it.",
            PromptAllowBlocker.ServiceSidUnverified =>
                $"SecureWall could not read the service SID type of {serviceName}, so it cannot verify that a rule would match. Allow is disabled.",
            PromptAllowBlocker.ServiceRegistrationUnknown =>
                "SecureWall could not rule out that this program runs as a Windows service, so Allow is disabled. The service log has details.",
            _ => "SecureWall could not identify one exact service. Allow is disabled to avoid broadly permitting a shared host.",
        };

        internal static string BlockedTooltip(PromptAllowBlocker blocker) => blocker switch
        {
            PromptAllowBlocker.ServiceSidUnavailable => "Unavailable because the service has no service SID to match.",
            PromptAllowBlocker.ServiceSidUnverified => "Unavailable because the service SID type could not be read.",
            PromptAllowBlocker.ServiceRegistrationUnknown => "Unavailable because the service inventory could not confirm this program.",
            _ => "Unavailable because the service identity is ambiguous.",
        };

        // Service names may contain spaces ("Steam Client Service"), so the name is always
        // quoted. SCM forbids '/' and '\' in names but not '"'; escape it for sc.exe's
        // command-line parser rather than assume it cannot occur.
        internal static string SidTypeCommand(string? serviceName) =>
            "sc.exe sidtype \"" + (serviceName ?? string.Empty).Replace("\"", "\\\"") + "\" unrestricted";
    }
}

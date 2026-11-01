using System;

namespace pylorak.TinyWall.Prompting
{
    // Synthetic prompts for the Debug-only /promptpreview switch. Pure so the variants are
    // testable; the preview itself starts neither the service nor WFP.
    internal static class PromptPreviewSamples
    {
        internal const string SidNoticeSwitch = "/sidnotice";
        internal const string DefaultSidNoticeService = "MicrosoftEdgeElevationService";
        internal const string NoWarningsSwitch = "/nowarnings";
        internal const string ExecutableSwitch = "/executable";

        // Every warning line by default, whatever the sample executable looks like on disk;
        // "/nowarnings" shows the popup without them.
        internal static ExecutableRiskFlags RiskFlags(string[] args) =>
            HasSwitch(args, NoWarningsSwitch)
                ? ExecutableRiskFlags.None
                : ExecutableRiskFlags.Unsigned | ExecutableRiskFlags.UserWritableLocation | ExecutableRiskFlags.RecentlyModified;

        // "/promptpreview" shows an allowable Dnscache prompt. "/promptpreview /sidnotice"
        // shows the SID-type NONE notice for MicrosoftEdgeElevationService, and
        // "/promptpreview /sidnotice "Steam Client Service"" uses the given name.
        internal static PromptWireDto Create(string[] args, DateTimeOffset now)
        {
            if (args == null)
                throw new ArgumentNullException(nameof(args));

            var prompt = new PromptWireDto
            {
                Token = Guid.NewGuid(),
                SubjectKind = PromptIdentityKind.Service,
                CanAllow = true,
                ExecutablePath = @"C:\Windows\System32\svchost.exe",
                ServiceName = "Dnscache",
                FirstSeenUtc = now,
                LastSeenUtc = now,
                ExpiresUtc = now.AddMinutes(2),
                RemoteAddress = "1.1.1.1",
                RemotePort = 53,
                Protocol = 17,
                OccurrenceCount = 1,
            };

            // "/promptpreview /executable <path>" shows an allowable executable prompt, which
            // offers Always allow when that file has a valid embedded signature.
            int executable = IndexOfSwitch(args, ExecutableSwitch);
            if (executable >= 0 && executable + 1 < args.Length &&
                !args[executable + 1].StartsWith("/", StringComparison.Ordinal))
            {
                prompt.SubjectKind = PromptIdentityKind.Executable;
                prompt.ExecutablePath = args[executable + 1];
                prompt.ServiceName = null;
                prompt.RemoteAddress = "203.0.113.20";
                prompt.RemotePort = 443;
                prompt.Protocol = 6;
                return prompt;
            }

            int sidNotice = IndexOfSwitch(args, SidNoticeSwitch);
            if (sidNotice < 0)
                return prompt;

            string? name = sidNotice + 1 < args.Length && !args[sidNotice + 1].StartsWith("/", StringComparison.Ordinal)
                ? args[sidNotice + 1]
                : null;
            prompt.CanAllow = false;
            prompt.AllowBlocker = PromptAllowBlocker.ServiceSidUnavailable;
            prompt.ServiceName = string.IsNullOrWhiteSpace(name) ? DefaultSidNoticeService : name;
            prompt.ExecutablePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\elevation_service.exe";
            prompt.RemoteAddress = "203.0.113.20";
            prompt.RemotePort = 443;
            prompt.Protocol = 6;
            return prompt;
        }

        private static bool HasSwitch(string[] args, string name) => IndexOfSwitch(args, name) >= 0;

        private static int IndexOfSwitch(string[] args, string name)
        {
            if (args == null)
                throw new ArgumentNullException(nameof(args));
            return Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}

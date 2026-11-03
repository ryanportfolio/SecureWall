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
        internal const string WarningsSwitch = "/warnings";
        internal const string ExecutableSwitch = "/executable";
        internal const string ServiceExeSwitch = "/serviceexe";
        internal const string SharedHostSwitch = "/sharedhost";
        internal const string SampleServiceExecutable = @"C:\Program Files (x86)\Example\Updater\1.0.0.0\updater.exe";
        internal static readonly string[] SampleCoveredServices = { "ExampleUpdaterInternalService1.0.0.0", "ExampleUpdaterService1.0.0.0" };

        // Every warning line by default, whatever the sample executable looks like on disk;
        // "/nowarnings" shows the popup without them, and "/warnings writable,recent" shows
        // only the named ones (unsigned, writable, recent).
        internal static ExecutableRiskFlags RiskFlags(string[] args)
        {
            if (HasSwitch(args, NoWarningsSwitch))
                return ExecutableRiskFlags.None;
            int warnings = IndexOfSwitch(args, WarningsSwitch);
            if (warnings < 0 || warnings + 1 >= args.Length)
                return ExecutableRiskFlags.Unsigned | ExecutableRiskFlags.UserWritableLocation | ExecutableRiskFlags.RecentlyModified;

            var flags = ExecutableRiskFlags.None;
            foreach (string name in args[warnings + 1].Split(','))
            {
                switch (name.Trim().ToLowerInvariant())
                {
                    case "unsigned": flags |= ExecutableRiskFlags.Unsigned; break;
                    case "writable": flags |= ExecutableRiskFlags.UserWritableLocation; break;
                    case "recent": flags |= ExecutableRiskFlags.RecentlyModified; break;
                }
            }
            return flags;
        }

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

            // "/promptpreview /serviceexe" shows an executable that is also the image of two
            // services, with an Allow that covers them; "/sharedhost" shows the same file when
            // the services could not all be named, with Allow disabled.
            bool serviceExe = HasSwitch(args, ServiceExeSwitch);
            if (serviceExe || HasSwitch(args, SharedHostSwitch))
            {
                prompt.SubjectKind = serviceExe ? PromptIdentityKind.Executable : PromptIdentityKind.AmbiguousService;
                prompt.CanAllow = serviceExe;
                prompt.AllowBlocker = serviceExe ? PromptAllowBlocker.None : PromptAllowBlocker.AmbiguousService;
                prompt.ExecutablePath = SampleServiceExecutable;
                prompt.ServiceName = null;
                prompt.CoveredServiceNames = serviceExe ? (string[])SampleCoveredServices.Clone() : Array.Empty<string>();
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

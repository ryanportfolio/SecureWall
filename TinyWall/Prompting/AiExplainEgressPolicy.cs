using System;
using System.Collections.Generic;
using System.IO;

namespace pylorak.TinyWall.Prompting
{
    // SecureWall.exe is both the interactive controller and the LocalSystem service image, so
    // a path-based exception for it also gives the service network access. The optional AI
    // lookup instead gets a service-owned permit scoped to the controller: the SecureWall
    // image, outbound TCP to remote port 443 only, and an ALE user condition whose DACL
    // denies the service accounts before allowing interactive logon tokens. The service
    // installs it only while the machine setting is on and the firewall is in Normal mode.
    internal static class AiExplainEgressPolicy
    {
        internal const byte TcpProtocol = 6;
        internal const ushort RemotePort = 443;

        // WFP weight of the permits for SecureWall's own image (this one and Check for updates):
        // above user blocks (6000000), below raw-socket rules and blocklists. A block on
        // SecureWall.exe also comes from child-process inheritance: the service is a child of
        // services.exe, and a block on services.exe with "child processes inherit" yields a
        // block on the shared image. At user-permit weight that block hid the permit, and a
        // feature the user had turned on failed with no visible reason.
        internal const ulong PermitWeight = 6500000;

        // The row in Settings > Special Exceptions > Optional that shows and sets the machine
        // setting. It is not a database profile and is never saved in SpecialExceptions.
        internal const string SpecialExceptionName = "SecureWall AI help";

        // Deny ACEs come first in the DACL, so these never match even if a token also carries
        // an allowed group. LocalSystem, LocalService and NetworkService tokens are excluded.
        internal static readonly IReadOnlyList<string> DeniedUserSids = new[]
        {
            "S-1-5-18", // NT AUTHORITY\SYSTEM
            "S-1-5-19", // NT AUTHORITY\LOCAL SERVICE
            "S-1-5-20", // NT AUTHORITY\NETWORK SERVICE
        };

        // NT AUTHORITY\INTERACTIVE: present in console and Remote Desktop logon tokens (and so
        // in the controller started by the logon task), absent from service tokens.
        internal static readonly IReadOnlyList<string> AllowedUserSids = new[] { "S-1-5-4" };

        // Where the switch is, for messages that tell the user to turn it on.
        internal const string SwitchLocation =
            "open Manage from the tray menu, go to Special Exceptions and tick \"" + SpecialExceptionName +
            "\" under Optional (or click ? on a blocked-connection popup and choose Yes)";

        internal const string ServiceAccessOffMessage =
            "The AI lookup did not run: AI help's network access is off, so the request would be blocked. " +
            "To turn it on, click ? again and choose Yes, or " + SwitchLocation + ". SecureWall must be unlocked. " +
            "Do not add an exception for SecureWall.exe: that would also give the SecureWall service network access.";

        internal const string EnablePrompt =
            "The AI assistant needs SecureWall's own program to reach the AI service.\n\n" +
            "Turn on the narrow permit? It allows only SecureWall.exe running in a signed-in " +
            "(interactive) account to make outbound TCP connections to port 443. It is added in " +
            "Normal mode only. The SecureWall service itself never gets network access. " +
            "Turning the assistant off in its settings removes the permit.";

        // BlockAll isolates everything; AllowOutgoing and Disabled already permit
        // outbound traffic. Display-off blocking restricts every allow to the local subnet, and
        // this permit has no local-subnet form, so it is withdrawn while that block is active.
        internal static bool ShouldInstallPermit(bool enabled, bool normalMode, bool displayOffBlockActive) =>
            enabled && normalMode && !displayOffBlockActive;

        internal enum ServiceAccessPlan { NoChange, Send, NotConnected }

        // `freshServiceFlag` comes from a settings read made just now, or is null when that read
        // failed. A failed read never counts as "already set" and never sends a cached copy.
        internal static ServiceAccessPlan PlanServiceAccessChange(bool requested, bool? freshServiceFlag)
        {
            if (freshServiceFlag == null)
                return ServiceAccessPlan.NotConnected;
            return freshServiceFlag.Value == requested ? ServiceAccessPlan.NoChange : ServiceAccessPlan.Send;
        }

        // True when a settings change toggles only the AI egress flag in the same firewall mode.
        // Such a reload adds or removes only the SecureWall.exe permit, whose own prompts are
        // never allowable, so pending prompt tokens for other subjects stay valid and are kept.
        // Without this, turning the permit on from a prompt would revoke that prompt.
        // `serializeWithFlag` must return the complete settings with the flag forced to the
        // given value, so any other difference keeps the normal revocation.
        internal static bool IsEgressOnlyChange<T>(T previous, T candidate, bool modeUnchanged,
            Func<T, bool> getFlag, Func<T, bool, byte[]> serializeWithFlag)
        {
            if (getFlag == null)
                throw new ArgumentNullException(nameof(getFlag));
            if (serializeWithFlag == null)
                throw new ArgumentNullException(nameof(serializeWithFlag));
            if (!modeUnchanged || previous == null || candidate == null || getFlag(previous) == getFlag(candidate))
                return false;

            byte[] a = serializeWithFlag(previous, false);
            byte[] b = serializeWithFlag(candidate, false);
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; ++i)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        internal const string OwnImageExceptionWarning =
            "This exception targets SecureWall's own program file.\n" +
            "SecureWall.exe also runs the SecureWall service as LocalSystem, so this rule would give the firewall service " +
            "the same network access.\n\nAI help does not need an exception. To give it network access, " + SwitchLocation +
            ". That permit allows only TCP port 443 for signed-in accounts and never the service.\n\nSave this exception anyway?";

        // Every path that saves an exception (editor and tray whitelisting) asks before a
        // non-block rule for SecureWall's own image.
        internal static bool RequiresOwnImageWarning(bool isBlock, string? candidatePath, string? ownImagePath) =>
            !isBlock && TargetsOwnImage(candidatePath, ownImagePath);

        // True when a user-entered executable path names SecureWall's own image. Such a rule
        // also matches the LocalSystem service process.
        internal static bool TargetsOwnImage(string? candidatePath, string? ownImagePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(ownImagePath))
                return false;

            try
            {
                string candidate = Path.GetFullPath(candidatePath!.Trim());
                string own = Path.GetFullPath(ownImagePath!.Trim());
                return string.Equals(candidate, own, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException
                || exception is PathTooLongException || exception is System.Security.SecurityException)
            {
                return false;
            }
        }
    }
}

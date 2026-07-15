using System;

namespace pylorak.TinyWall.Prompting
{
    internal enum NetworkActivityStatus
    {
        Allowed,
        Blocked,
        Listening,
    }

    internal static class NetworkActivityStatusClassifier
    {
        internal const bool ShowAllowedByDefault = true;
        internal const bool ShowBlockedByDefault = true;
        internal const bool ShowListeningByDefault = true;

        internal static NetworkActivityStatus FromFirewallDecision(bool allowed)
        {
            return allowed
                ? NetworkActivityStatus.Allowed
                : NetworkActivityStatus.Blocked;
        }

        internal static string ToDisplayText(NetworkActivityStatus status)
        {
            return status switch
            {
                NetworkActivityStatus.Allowed => "Allowed",
                NetworkActivityStatus.Blocked => "Blocked",
                NetworkActivityStatus.Listening => "Listening (local endpoint)",
                _ => throw new ArgumentOutOfRangeException(nameof(status)),
            };
        }

        internal static bool IsDecisionVisible(
            NetworkActivityStatus status,
            bool showAllowed,
            bool showBlocked)
        {
            return status switch
            {
                NetworkActivityStatus.Allowed => showAllowed,
                NetworkActivityStatus.Blocked => showBlocked,
                _ => throw new ArgumentException(
                    "Only observed firewall decisions can be filtered here.",
                    nameof(status)),
            };
        }
    }
}

using System;
using System.Net;

namespace pylorak.TinyWall.Prompting
{
    internal static class DropCorrelator
    {
        private static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(1);

        internal static bool IsMatch(
            DropCandidate candidate,
            BlockedConnectionAuditEvent auditEvent) =>
            IsMatch(candidate, auditEvent, DefaultTolerance);

        internal static bool IsMatch(
            DropCandidate candidate,
            BlockedConnectionAuditEvent auditEvent,
            TimeSpan tolerance)
        {
            if (candidate == null || auditEvent == null || tolerance < TimeSpan.Zero)
                return false;

            TimeSpan skew = candidate.TimestampUtc - auditEvent.TimestampUtc;
            if (skew < TimeSpan.Zero)
                skew = -skew;

            return auditEvent.Direction == ConnectionDirection.Outbound &&
                candidate.FilterRuntimeId == auditEvent.FilterRuntimeId &&
                string.Equals(
                    candidate.ApplicationPath,
                    auditEvent.ApplicationPath,
                    StringComparison.OrdinalIgnoreCase) &&
                candidate.Protocol == auditEvent.Protocol &&
                candidate.LocalPort == auditEvent.LocalPort &&
                candidate.RemotePort == auditEvent.RemotePort &&
                AddressesEqual(candidate.LocalAddress, auditEvent.LocalAddress) &&
                AddressesEqual(candidate.RemoteAddress, auditEvent.RemoteAddress) &&
                skew <= tolerance;
        }

        private static bool AddressesEqual(string first, string second)
        {
            if (IPAddress.TryParse(first, out IPAddress? firstAddress) &&
                IPAddress.TryParse(second, out IPAddress? secondAddress))
            {
                return firstAddress.Equals(secondAddress);
            }

            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }
}

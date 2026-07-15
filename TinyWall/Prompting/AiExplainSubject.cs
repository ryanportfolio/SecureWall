using System;
using System.IO;

namespace pylorak.TinyWall.Prompting
{
    // Minimal, privacy-preserving description of a blocked subject, assembled on the
    // controller for an optional "what is this?" lookup. By design it carries only the
    // executable's file name (never the full path, which can leak the Windows user name)
    // and, when available, the Authenticode publisher. The remote endpoint is included
    // only when the user explicitly opts in.
    internal sealed class AiExplainSubject
    {
        internal AiExplainSubject(
            PromptIdentityKind kind,
            string executableName,
            string? publisher,
            string? serviceName,
            string? packageSid,
            string? remoteEndpoint)
        {
            if (string.IsNullOrWhiteSpace(executableName))
                throw new ArgumentException("Executable name is required.", nameof(executableName));

            Kind = kind;
            ExecutableName = executableName.Trim();
            Publisher = Normalize(publisher);
            ServiceName = Normalize(serviceName);
            PackageSid = Normalize(packageSid);
            RemoteEndpoint = Normalize(remoteEndpoint);
        }

        internal PromptIdentityKind Kind { get; }
        internal string ExecutableName { get; }
        internal string? Publisher { get; }
        internal string? ServiceName { get; }
        internal string? PackageSid { get; }
        internal string? RemoteEndpoint { get; }

        // Builds a subject from the display-only wire DTO. `publisher` is resolved by the
        // caller (Authenticode lookup is file I/O and stays out of this pure type).
        // `includeRemoteEndpoint` defaults to the Minimal privacy posture (excluded).
        internal static AiExplainSubject FromPrompt(
            PromptWireDto prompt,
            string? publisher,
            bool includeRemoteEndpoint)
        {
            if (prompt == null)
                throw new ArgumentNullException(nameof(prompt));

            string executableName = FileName(prompt.ExecutablePath, prompt);
            string? remoteEndpoint = includeRemoteEndpoint
                ? FormatEndpoint(prompt.Protocol, prompt.RemoteAddress, prompt.RemotePort)
                : null;

            return new AiExplainSubject(
                prompt.SubjectKind,
                executableName,
                publisher,
                prompt.ServiceName,
                prompt.PackageSid,
                remoteEndpoint);
        }

        private static string FileName(string? executablePath, PromptWireDto prompt)
        {
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                try
                {
                    string name = Path.GetFileName(executablePath!.Trim());
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
                catch (ArgumentException)
                {
                    // Fall through to the identity-derived fallbacks below.
                }
            }

            if (!string.IsNullOrWhiteSpace(prompt.ServiceName))
                return prompt.ServiceName!.Trim();
            if (!string.IsNullOrWhiteSpace(prompt.PackageSid))
                return "app package";
            return "unknown application";
        }

        private static string? FormatEndpoint(byte protocol, string? remoteAddress, int remotePort)
        {
            if (string.IsNullOrWhiteSpace(remoteAddress))
                return null;

            string proto = protocol switch
            {
                6 => "TCP",
                17 => "UDP",
                _ => "protocol " + protocol.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            return proto + " " + remoteAddress!.Trim() + ":"
                + remotePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string? Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }
}

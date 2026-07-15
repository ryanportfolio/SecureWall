using System;
using System.Text;

namespace pylorak.TinyWall.Prompting
{
    internal sealed class AiExplainPrompt
    {
        internal AiExplainPrompt(string systemPrompt, string userPrompt)
        {
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
        }

        internal string SystemPrompt { get; }
        internal string UserPrompt { get; }
    }

    // Turns a minimal subject into the two chat messages sent to an OpenAI-compatible
    // endpoint. The system prompt deliberately forbids the model from making the allow/block
    // decision: the answer is an advisory hint shown next to the prompt, never wired to the
    // firewall action. Subject fields are attacker-influenceable (a process can name itself
    // "Google Update"), so they are presented as untrusted claims to be assessed.
    internal static class AiExplainComposer
    {
        internal const string SystemPrompt =
            "You are a security assistant embedded in a desktop firewall. " +
            "The user was just prompted because an application tried to open an outbound " +
            "network connection that the firewall blocked by default. " +
            "Given the identifying details below, briefly explain what this program most " +
            "likely is and its typical legitimate purpose, and note anything that looks " +
            "unusual or worth caution. " +
            "Treat every provided value as an unverified claim: a file name or publisher can " +
            "be spoofed, so never assert certainty. " +
            "You cannot see the network or the machine; do not invent specific facts. " +
            "Do NOT tell the user whether to allow or block it and do not issue a verdict; " +
            "the decision is theirs. Keep the answer under 120 words.";

        internal static AiExplainPrompt Compose(AiExplainSubject subject)
        {
            if (subject == null)
                throw new ArgumentNullException(nameof(subject));

            var sb = new StringBuilder();
            sb.Append("An application was blocked initiating an outbound connection.\n");
            sb.Append("Reported identity type: ").Append(KindText(subject.Kind)).Append('\n');
            sb.Append("Executable file name: ").Append(subject.ExecutableName).Append('\n');
            sb.Append("Code-signing publisher: ")
              .Append(subject.Publisher ?? "none (unsigned or unverified)")
              .Append('\n');

            if (!string.IsNullOrWhiteSpace(subject.ServiceName))
                sb.Append("Windows service name: ").Append(subject.ServiceName).Append('\n');
            if (!string.IsNullOrWhiteSpace(subject.PackageSid))
                sb.Append("App package SID: ").Append(subject.PackageSid).Append('\n');
            if (!string.IsNullOrWhiteSpace(subject.RemoteEndpoint))
                sb.Append("Attempted destination: ").Append(subject.RemoteEndpoint).Append('\n');

            sb.Append("\nWhat is this program most likely, and what does it typically do?");
            return new AiExplainPrompt(SystemPrompt, sb.ToString());
        }

        private static string KindText(PromptIdentityKind kind) => kind switch
        {
            PromptIdentityKind.Executable => "standalone executable",
            PromptIdentityKind.Package => "packaged (Store/AppContainer) app",
            PromptIdentityKind.Service => "Windows service",
            PromptIdentityKind.AmbiguousService => "shared service host (ambiguous)",
            _ => "unknown",
        };
    }
}

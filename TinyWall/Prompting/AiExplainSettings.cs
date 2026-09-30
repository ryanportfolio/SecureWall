using System;

namespace pylorak.TinyWall.Prompting
{
    // Pure validation and endpoint construction for the optional AI lookup. The API key is
    // handled elsewhere (DPAPI-protected, never in this type). Defaults target OpenAI but the
    // base URL and model are user-editable so any OpenAI-compatible endpoint works.
    internal static class AiExplainSettings
    {
        internal const string DefaultBaseUrl = "https://api.openai.com/v1";
        internal const string DefaultModel = "gpt-4o-mini";
        internal const string ChatCompletionsPath = "chat/completions";

        internal static bool Validate(string? baseUrl, string? model, out string? error)
        {
            if (!TryBuildChatCompletionsUri(baseUrl, out _))
            {
                error = UsesOtherHttpsPort(baseUrl)
                    ? "The AI endpoint must use the standard HTTPS port 443. SecureWall's permit for the assistant " +
                      "covers only TCP port 443, so other ports (and a system proxy on another port) would be blocked."
                    : "Enter a valid https base URL, for example " + DefaultBaseUrl + ".";
                return false;
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                error = "Enter a model name, for example " + DefaultModel + ".";
                return false;
            }

            error = null;
            return true;
        }

        internal static bool TryBuildChatCompletionsUri(string? baseUrl, out Uri? uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(baseUrl))
                return false;

            string trimmed = baseUrl!.Trim().TrimEnd('/');
            if (!Uri.TryCreate(trimmed + "/" + ChatCompletionsPath, UriKind.Absolute, out Uri? built))
                return false;

            if (built!.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(built.UserInfo) ||
                !string.IsNullOrEmpty(built.Query) || !string.IsNullOrEmpty(built.Fragment))
                return false;

            // The controller's egress permit allows remote TCP 443 only (AiExplainEgressPolicy).
            if (built.Port != AiExplainEgressPolicy.RemotePort)
                return false;

            uri = built;
            return true;
        }

        private static bool UsesOtherHttpsPort(string? baseUrl) =>
            !string.IsNullOrWhiteSpace(baseUrl)
            && Uri.TryCreate(baseUrl!.Trim(), UriKind.Absolute, out Uri? parsed)
            && parsed!.Scheme == Uri.UriSchemeHttps
            && parsed.Port != AiExplainEgressPolicy.RemotePort;
    }
}

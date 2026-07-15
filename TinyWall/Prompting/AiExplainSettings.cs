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
                error = "Enter a valid https base URL, for example " + DefaultBaseUrl + ".";
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

            if (built!.Scheme != Uri.UriSchemeHttp && built.Scheme != Uri.UriSchemeHttps)
                return false;

            uri = built;
            return true;
        }
    }
}

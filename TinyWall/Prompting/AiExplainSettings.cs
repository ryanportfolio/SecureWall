using System;

namespace pylorak.TinyWall.Prompting
{
    // Pure validation and endpoint construction for the optional AI lookup. The API key is
    // handled elsewhere (DPAPI-protected, never in this type). Defaults target OpenAI but the
    // base URL and model are user-editable so any OpenAI-compatible endpoint works.
    internal static class AiExplainSettings
    {
        internal const string DefaultBaseUrl = "https://api.openai.com/v1";
        internal const string DefaultModel = "gpt-6.1-sol";
        internal const string ChatCompletionsPath = "chat/completions";

        // Sent as reasoning_effort with DefaultModel on OpenAI's API. Medium is also the model's
        // own default; sending it keeps cost and latency fixed if that default changes.
        internal const string DefaultReasoningEffort = "medium";
        internal const string OpenAiHost = "api.openai.com";

        // Earlier defaults. Before ControllerSettings.AiExplainModelSaved existed, the settings
        // form saved whatever model box it showed, so in such a config a stored value equal to
        // one of these was never typed by the user and follows the current default.
        private static readonly string[] RetiredDefaultModels = { "gpt-4o-mini" };

        // The model to request for a stored setting. Empty means DefaultModel. A retired default
        // means DefaultModel only in a config the current form has not saved (modelSaved false);
        // once it has, the stored value is the user's choice and is kept, gpt-4o-mini included.
        internal static string EffectiveModel(string? storedModel, bool modelSaved)
        {
            string trimmed = storedModel?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
                return DefaultModel;
            if (!modelSaved && Array.IndexOf(RetiredDefaultModels, trimmed) >= 0)
                return DefaultModel;
            return trimmed;
        }

        // The value to store for a model typed in the settings form. The default is stored as
        // empty so a later default change reaches users who never picked a model.
        internal static string ModelToStore(string? typedModel)
        {
            string trimmed = typedModel?.Trim() ?? string.Empty;
            return trimmed == DefaultModel ? string.Empty : trimmed;
        }

        // reasoning_effort for this endpoint and model, or null to leave it out. Only the default
        // model on OpenAI's own API gets it: other OpenAI models (gpt-4o-mini, for example) and
        // other OpenAI-compatible endpoints can reject the field.
        internal static string? ReasoningEffortFor(string? baseUrl, string? model)
        {
            if (model?.Trim() != DefaultModel || !TryBuildChatCompletionsUri(baseUrl, out Uri? uri))
                return null;
            return string.Equals(uri!.Host, OpenAiHost, StringComparison.OrdinalIgnoreCase)
                ? DefaultReasoningEffort
                : null;
        }

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

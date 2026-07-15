using System;
using System.Text.Json;

namespace pylorak.TinyWall.Prompting
{
    internal sealed class AiExplainResult
    {
        private AiExplainResult(bool success, string? text, string? error)
        {
            Success = success;
            Text = text;
            Error = error;
        }

        internal bool Success { get; }
        internal string? Text { get; }
        internal string? Error { get; }

        internal static AiExplainResult Ok(string text) => new AiExplainResult(true, text, null);
        internal static AiExplainResult Fail(string error) => new AiExplainResult(false, null, error);
    }

    // Parses an OpenAI-compatible /chat/completions response. Kept pure and separate from the
    // HTTP client so it can be unit-tested against captured payloads, and so any malformed or
    // hostile body degrades to a friendly error rather than throwing into the UI thread.
    internal static class AiExplainResponseParser
    {
        internal static AiExplainResult Parse(int statusCode, string? body)
        {
            if (statusCode < 200 || statusCode >= 300)
                return AiExplainResult.Fail(HttpError(statusCode, body));

            if (string.IsNullOrWhiteSpace(body))
                return AiExplainResult.Fail("The assistant returned an empty response.");

            try
            {
                using var doc = JsonDocument.Parse(body!);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return AiExplainResult.Fail("The assistant returned an unexpected response.");

                if (doc.RootElement.TryGetProperty("choices", out JsonElement choices)
                    && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0)
                {
                    JsonElement first = choices[0];
                    if (first.TryGetProperty("message", out JsonElement message)
                        && message.TryGetProperty("content", out JsonElement content)
                        && content.ValueKind == JsonValueKind.String)
                    {
                        string? text = content.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            return AiExplainResult.Ok(text!.Trim());
                    }
                }

                return AiExplainResult.Fail("The assistant returned no explanation.");
            }
            catch (JsonException)
            {
                return AiExplainResult.Fail("The assistant returned a response that could not be read.");
            }
        }

        private static string HttpError(int statusCode, string? body)
        {
            string? apiMessage = TryReadApiErrorMessage(body);
            string prefix = statusCode switch
            {
                401 => "The API key was rejected (401).",
                403 => "Access was denied (403).",
                404 => "The endpoint or model was not found (404).",
                429 => "Rate limit or quota reached (429).",
                >= 500 => "The AI service reported a server error (" + statusCode + ").",
                _ => "The AI request failed (HTTP " + statusCode + ").",
            };
            return apiMessage == null ? prefix : prefix + " " + apiMessage;
        }

        private static string? TryReadApiErrorMessage(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(body!);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out JsonElement error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    string? text = message.GetString();
                    return string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
                }
            }
            catch (JsonException)
            {
                // Non-JSON error body; the generic prefix is enough.
            }

            return null;
        }
    }
}

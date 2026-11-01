using System.IO;
using System.Text;
using System.Text.Json;

namespace pylorak.TinyWall.Prompting
{
    // Chat Completions request body for the AI lookup, written with Utf8JsonWriter (no
    // reflection). Kept apart from the HTTP client so the request shape is testable.
    internal static class AiExplainRequest
    {
        internal const double Temperature = 0.2;

        // With a reasoning effort the body carries reasoning_effort and no temperature:
        // OpenAI's reasoning models accept only the default temperature, and the GPT Sol model
        // docs do not list temperature as supported. Without one the body is unchanged from
        // the original OpenAI-compatible request (model, temperature, messages).
        internal static string BuildJson(string model, string? reasoningEffort, AiExplainPrompt prompt)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("model", model);
                if (reasoningEffort != null)
                    writer.WriteString("reasoning_effort", reasoningEffort);
                else
                    writer.WriteNumber("temperature", Temperature);
                writer.WriteStartArray("messages");

                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", prompt.SystemPrompt);
                writer.WriteEndObject();

                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteString("content", prompt.UserPrompt);
                writer.WriteEndObject();

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}

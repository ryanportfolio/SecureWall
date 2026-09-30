using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    // Controller-side HTTP client for an OpenAI-compatible /chat/completions endpoint.
    // Reflection-free: the request body is written with Utf8JsonWriter and the response is
    // parsed by AiExplainResponseParser (JsonDocument), so no source-gen/trim setup is needed.
    // This type lives in the controller process only; the service never makes this call and
    // never sees the API key.
    internal sealed class OpenAiCompatibleExplainClient
    {
        private readonly string _baseUrl;
        private readonly string _model;
        private readonly string _apiKey;

        internal OpenAiCompatibleExplainClient(string baseUrl, string model, string apiKey)
        {
            _baseUrl = baseUrl;
            _model = model;
            _apiKey = apiKey;
        }

        internal async Task<AiExplainResult> ExplainAsync(AiExplainSubject subject, CancellationToken cancellationToken)
        {
            if (subject == null)
                throw new ArgumentNullException(nameof(subject));

            if (!AiExplainSettings.TryBuildChatCompletionsUri(_baseUrl, out Uri? uri) || uri == null)
                return AiExplainResult.Fail("The configured base URL is not valid.");
            if (string.IsNullOrWhiteSpace(_model))
                return AiExplainResult.Fail("No model is configured.");
            if (string.IsNullOrWhiteSpace(_apiKey))
                return AiExplainResult.Fail("No API key is configured.");

            AiExplainPrompt prompt = AiExplainComposer.Compose(subject);
            string requestJson = BuildRequestJson(_model, prompt);

            // One deadline covers connect, headers and body. HttpClient.Timeout alone stops at the
            // headers when the body is read as a stream.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(RequestTimeout);
            CancellationToken token = deadline.Token;

            try
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var http = new HttpClient(handler)
                {
                    Timeout = RequestTimeout,
                    MaxResponseContentBufferSize = AiExplainText.MaxResponseBytes,
                };
                using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);

                using HttpResponseMessage response = await http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                long? declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength > AiExplainText.MaxResponseBytes)
                    return TooLarge();

                // A stalled body read may ignore the token on .NET Framework; disposing the
                // response aborts the connection so the read fails instead of hanging.
                using CancellationTokenRegistration abort = token.Register(response.Dispose);
                using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                byte[]? bytes = await AiExplainText
                    .ReadBoundedAsync(stream, AiExplainText.MaxResponseBytes, token)
                    .ConfigureAwait(false);
                if (bytes == null)
                    return TooLarge();

                // RFC 8259 requires UTF-8 for JSON exchanged between systems.
                string body = Encoding.UTF8.GetString(bytes);
                return AiExplainResponseParser.Parse((int)response.StatusCode, body);
            }
            catch (OperationCanceledException)
            {
                return AiExplainResult.Fail("The AI request was cancelled or timed out.");
            }
            catch (Exception exception) when (token.IsCancellationRequested
                && (exception is ObjectDisposedException || exception is IOException || exception is HttpRequestException))
            {
                return AiExplainResult.Fail("The AI request was cancelled or timed out.");
            }
            catch (HttpRequestException exception)
            {
                return AiExplainResult.Fail("Could not reach the AI service: " + exception.Message);
            }
            catch (IOException exception)
            {
                return AiExplainResult.Fail("The AI service connection failed: " + exception.Message);
            }
        }

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private static AiExplainResult TooLarge() => AiExplainResult.Fail(
            "The AI response was larger than " + (AiExplainText.MaxResponseBytes / 1024) +
            " KiB and was discarded.");

        private static string BuildRequestJson(string model, AiExplainPrompt prompt)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("model", model);
                writer.WriteNumber("temperature", 0.2);
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

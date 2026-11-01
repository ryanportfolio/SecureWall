using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    // HTTPS for Check for updates, in the controller under the user's token. Every URL it
    // requests, and every redirect it follows, must pass ReleaseCheck's GitHub-only policy;
    // redirects are followed by hand, never automatically. Bodies are capped and requests have
    // a deadline, so a slow or oversized response fails instead of hanging or filling memory.
    // The LocalSystem service never uses this class.
    internal sealed class ReleaseCheckClient : IReleaseSource, IDisposable
    {
        private readonly HttpClientHandler Handler;
        private readonly HttpClient Http;

        internal ReleaseCheckClient()
        {
            Handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
            };
            Http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "SecureWall/" + ReleaseVersion.Format(ReleaseVersion.Normalize(Utils.TinyWallVersion)));
        }

        public void Dispose()
        {
            Http.Dispose();
            Handler.Dispose();
        }

        public async Task<ReleaseFetch> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ReleaseCheck.RequestDeadline);
            try
            {
                using HttpResponseMessage? response = await SendAsync(uri, deadline.Token).ConfigureAwait(false);
                if (response == null)
                    return ReleaseFetch.Fail(LastRedirectError);
                ReleaseFetch? failure = Failure(response);
                if (failure != null)
                    return failure;
                if (response.Content.Headers.ContentLength > maxBytes)
                    return ReleaseFetch.Fail("the response was larger than expected");
                // A stalled body read may ignore the token on .NET Framework; disposing the
                // response aborts the connection so the read fails instead of hanging.
                using CancellationTokenRegistration abort = deadline.Token.Register(response.Dispose);
                using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                byte[]? body = await AiExplainText.ReadBoundedAsync(stream, maxBytes, deadline.Token).ConfigureAwait(false);
                return body == null ? ReleaseFetch.Fail("the response was larger than expected") : ReleaseFetch.Ok(body);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                return ReleaseFetch.Fail(Describe(exception, cancellationToken));
            }
        }

        // Streams an asset to `path` (created new), at most maxBytes, within `deadline`.
        // Returns an error, or null on success. A failed download leaves no file behind.
        internal async Task<string?> FetchToFileAsync(Uri uri, string path, long maxBytes, TimeSpan deadline,
            IProgress<(long Received, long Total)>? progress, CancellationToken cancellationToken)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(deadline);
            bool complete = false;
            try
            {
                using HttpResponseMessage? response = await SendAsync(uri, limit.Token).ConfigureAwait(false);
                if (response == null)
                    return LastRedirectError;
                ReleaseFetch? failure = Failure(response);
                if (failure != null)
                    return failure.RateLimited ? ReleaseCheck.RateLimitMessage : failure.Error;
                long? length = response.Content.Headers.ContentLength;
                if (length > maxBytes)
                    return "the installer is larger than expected";
                using CancellationTokenRegistration abort = limit.Token.Register(response.Dispose);
                using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, limit.Token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > maxBytes)
                            return "the installer is larger than expected";
                        await output.WriteAsync(buffer, 0, read, limit.Token).ConfigureAwait(false);
                        progress?.Report((total, length ?? 0));
                    }
                }
                complete = true;
                return null;
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                return Describe(exception, cancellationToken);
            }
            finally
            {
                if (!complete)
                {
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        private string LastRedirectError = string.Empty;

        // GET with redirects followed by hand under ReleaseCheck's policy. Returns null when a
        // redirect is refused (LastRedirectError says why).
        private async Task<HttpResponseMessage?> SendAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (!ReleaseCheck.IsAllowedEndpoint(uri))
            {
                LastRedirectError = "the address is not one of GitHub's HTTPS hosts";
                return null;
            }
            for (int redirects = 0; ; ++redirects)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Accept", uri.Host == ReleaseCheck.ApiHost
                    ? "application/vnd.github+json" : "application/octet-stream");
                HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status < 300 || status > 399 || status == 304)
                    return response;
                string? location = response.Headers.Location?.OriginalString;
                response.Dispose();
                if (!ReleaseCheck.TryFollowRedirect(uri, location, redirects, out Uri? next, out string error))
                {
                    LastRedirectError = error;
                    return null;
                }
                uri = next!;
            }
        }

        private static ReleaseFetch? Failure(HttpResponseMessage response)
        {
            int status = (int)response.StatusCode;
            if (status == 200)
                return null;
            bool exhausted = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.Contains("0");
            if (status == 429 || (status == 403 && exhausted))
                return ReleaseFetch.Fail("GitHub's rate limit was reached", status, rateLimited: true);
            return ReleaseFetch.Fail("GitHub answered with HTTP " + status, status);
        }

        private static bool IsTransportFailure(Exception exception) =>
            exception is HttpRequestException || exception is IOException || exception is OperationCanceledException ||
            exception is ObjectDisposedException || exception is WebException || exception is UnauthorizedAccessException;

        private static string Describe(Exception exception, CancellationToken userCancellation)
        {
            if (userCancellation.IsCancellationRequested)
                return "the check was cancelled";
            if (exception is OperationCanceledException || exception is ObjectDisposedException)
                return "GitHub did not answer in time";
            string message = exception.InnerException?.Message ?? exception.Message;
            return "the connection failed (" + message + ")";
        }
    }
}

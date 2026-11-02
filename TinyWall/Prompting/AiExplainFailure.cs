using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace pylorak.TinyWall.Prompting
{
    // Turns a failed AI request into a message that names the likely cause and what to do,
    // followed by the exception chain. The HTTP stack on .NET Framework reports every transport
    // failure as "An error occurred while sending the request."; the cause is in the inner
    // exceptions (a firewall drop is a SocketException with WSAEACCES).
    internal static class AiExplainFailure
    {
        // `permitOn` is the service's AiAssistantEgress flag read after the failure, or null
        // when it could not be read. `blockAllMode` is true when the service reports Block all.
        internal static string Describe(Exception exception, bool? permitOn, bool blockAllMode)
        {
            if (exception == null)
                throw new ArgumentNullException(nameof(exception));
            return Cause(exception, permitOn, blockAllMode) + "\r\n\r\nDetails: " + Details(exception);
        }

        internal static string Cause(Exception exception, bool? permitOn, bool blockAllMode)
        {
            SocketError? socket = null;
            WebExceptionStatus? web = null;
            bool tls = false;
            for (Exception? e = exception; e != null; e = e.InnerException)
            {
                if (e is SocketException s && socket == null)
                    socket = s.SocketErrorCode;
                else if (e is WebException w && web == null)
                    web = w.Status;
                else if (e is AuthenticationException)
                    tls = true;
            }

            if (socket == SocketError.AccessDenied)
            {
                if (blockAllMode)
                    return "SecureWall blocked the AI request because the firewall is in Block all mode. " +
                        "Switch to Normal mode from the tray menu, then try again.";
                if (permitOn == false)
                    return "SecureWall blocked the AI request because AI help's network access is off. To turn it on, " +
                        AiExplainEgressPolicy.SwitchLocation + ", then try again.";
                return "SecureWall or another firewall blocked the AI request. AI help's network access works in Normal mode " +
                    "while the display is on, and only for port 443: check that the base URL and any system proxy use port 443.";
            }
            if (web == WebExceptionStatus.ProxyNameResolutionFailure)
                return "The system proxy's name could not be resolved. Check the proxy settings in Windows.";
            if (web == WebExceptionStatus.NameResolutionFailure || socket == SocketError.HostNotFound
                || socket == SocketError.TryAgain || socket == SocketError.NoData)
                return "The AI service's host name could not be resolved. Check the base URL in AI help settings and your internet connection.";
            if (tls || web == WebExceptionStatus.TrustFailure || web == WebExceptionStatus.SecureChannelFailure)
                return "The secure (TLS) connection to the AI service failed. Check the base URL and that the system date and time are correct.";
            if (socket == SocketError.ConnectionRefused || socket == SocketError.TimedOut
                || socket == SocketError.NetworkUnreachable || socket == SocketError.HostUnreachable
                || socket == SocketError.NetworkDown || web == WebExceptionStatus.ConnectFailure || web == WebExceptionStatus.Timeout)
                return "The AI service could not be reached. Check your internet connection and the base URL.";
            return "Could not reach the AI service.";
        }

        // Distinct messages along the exception chain, outermost first, with the socket error
        // number when there is one.
        internal static string Details(Exception exception)
        {
            var parts = new List<string>();
            for (Exception? e = exception; e != null && parts.Count < 6; e = e.InnerException)
            {
                string message = e.Message?.Trim() ?? string.Empty;
                if (e is SocketException s)
                    message += " (socket error " + s.ErrorCode + ")";
                if (message.Length != 0 && !parts.Contains(message))
                    parts.Add(message);
            }

            var sb = new StringBuilder();
            foreach (string part in parts)
            {
                if (sb.Length != 0)
                    sb.Append(' ');
                sb.Append(part);
                if (!part.EndsWith(".", StringComparison.Ordinal) && !part.EndsWith(")", StringComparison.Ordinal))
                    sb.Append('.');
            }
            return sb.ToString();
        }
    }
}

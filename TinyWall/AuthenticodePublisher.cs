using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace pylorak.TinyWall
{
    // Reads the Authenticode signer's common name from a file, to give the AI lookup a
    // "publisher" hint. NOTE: this reads the embedded certificate subject only; it does NOT
    // verify the signature is valid or trusted. The value is therefore a *claimed* publisher,
    // which is exactly how the AI prompt treats it (an unverified hint, never proof).
    internal static class AuthenticodePublisher
    {
        internal static string? TryGetPublisher(string? executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return null;

            try
            {
                if (!File.Exists(executablePath))
                    return null;

                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(executablePath!));
                string name = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch (CryptographicException)
            {
                // Unsigned or unreadable signature.
                return null;
            }
            catch (Exception)
            {
                // Any other I/O or platform error: no publisher hint, not a fatal condition.
                return null;
            }
        }
    }
}

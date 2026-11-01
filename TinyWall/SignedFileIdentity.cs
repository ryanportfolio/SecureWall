using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using pylorak.Windows;

namespace pylorak.TinyWall
{
    // The signer of a file's embedded Authenticode signature, read only after WinVerifyTrust
    // accepted that signature. Catalog-signed files (most Windows binaries) have no embedded
    // signature and return null, as do unsigned files and files whose signature fails.
    public sealed class SignedFileIdentity
    {
        private SignedFileIdentity(string subject, string name)
        {
            Subject = subject;
            Name = name;
        }

        // Full certificate subject, for example CN="Anthropic, PBC", O="Anthropic, PBC", ...
        public string Subject { get; }

        // The subject's common name, for display.
        public string Name { get; }

        internal static SignedFileIdentity? TryRead(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                if (!File.Exists(path))
                    return null;
                if (WinTrust.VerifyFileAuthenticode(path!) != WinTrust.VerifyResult.SIGNATURE_VALID)
                    return null;
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path!));
                string subject = cert.Subject;
                if (string.IsNullOrWhiteSpace(subject))
                    return null;
                string name = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                return new SignedFileIdentity(subject, string.IsNullOrWhiteSpace(name) ? subject : name.Trim());
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

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

#if DEBUG
        // A made-up signer for the Debug-only preview switches; never read from a file.
        internal static SignedFileIdentity ForPreview(string name) =>
            new SignedFileIdentity($"CN={name}, O={name}", name);
#endif

        // Only drive-letter paths on local fixed drives are read: opening a UNC or WebDAV path
        // from the LocalSystem service would authenticate to whatever host the path names. No
        // folder or file along the path may be a reparse point either, since a symbolic link
        // can point at a network host. The attributes are read without following links; a link
        // swapped in between this check and the read is not caught (see SECURITY.md).
        internal static bool IsLocalFixedPath(string? path)
        {
            if (path == null || path.Length < 4 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                return false;
            try
            {
                if (new DriveInfo(path.Substring(0, 1)).DriveType != DriveType.Fixed)
                    return false;
                string current = path.Substring(0, 3);
                foreach (string part in path.Substring(3).Split('\\'))
                {
                    if (part.Length == 0 || part == "." || part == "..")
                        return false;
                    current = Path.Combine(current, part);
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static SignedFileIdentity? TryRead(string? path)
        {
            if (!IsLocalFixedPath(path))
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

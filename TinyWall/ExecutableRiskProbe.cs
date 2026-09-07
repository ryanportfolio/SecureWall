using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    // Gathers the facts behind the block popup's warnings for one executable. Runs in the
    // controller process at display time (file I/O, certificate chain building, DACL reads),
    // then hands the facts to the pure ExecutableRiskAssessment classifier.
    //
    // Known limits:
    // - Signature: reads the embedded Authenticode certificate and validates its chain
    //   (offline, no revocation). It does not hash the file against the signature, so a
    //   tampered file that still carries its original certificate reads as signed
    //   (WinVerifyTrust is not used in this codebase). Catalog-signed files, which most
    //   Windows system binaries are, carry no embedded certificate and are reported as
    //   unsigned.
    // - Writability: the DACL check uses the current token's user and groups, minus the
    //   Administrators group, so an elevated controller does not report every
    //   admin-writable folder. Path heuristics (profile, temp, Public, removable drives)
    //   are OR-ed in. Access granted through groups absent from the token, or via
    //   junctions, is missed.
    internal static class ExecutableRiskProbe
    {
        private static readonly string[] AlwaysWritableSidPrefixes =
        {
            "S-1-1-0",      // Everyone
            "S-1-5-11",     // Authenticated Users
            "S-1-5-32-545", // Users
            "S-1-5-4",      // Interactive
        };

        private const string AdministratorsSid = "S-1-5-32-544";

        private const FileSystemRights WriteRights =
            FileSystemRights.WriteData | FileSystemRights.CreateFiles | FileSystemRights.AppendData |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.Modify | FileSystemRights.FullControl;

        // Returns null when there is no file to assess (no path reported, or the file is
        // missing), so the popup shows no warnings rather than three false ones.
        internal static ExecutableRiskFlags? Assess(string? executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return null;

            string path = executablePath!.Trim();
            try
            {
                if (!File.Exists(path))
                    return null;
            }
            catch (Exception)
            {
                return null;
            }

            ExecutableSignatureStatus signature = SignatureStatus(path);
            bool userCanWrite = UserCanWriteLocation(path);
            DateTimeOffset? lastWrite = LastWriteUtc(path);
            return ExecutableRiskAssessment.Assess(signature, userCanWrite, lastWrite, DateTimeOffset.UtcNow);
        }

        internal static ExecutableSignatureStatus SignatureStatus(string path)
        {
            X509Certificate2? cert = null;
            try
            {
                cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            }
            catch (Exception)
            {
                return ExecutableSignatureStatus.Missing;
            }

            try
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(2);
                // A signing certificate that has since expired is still fine when the
                // signature was timestamped; the chain check here has no access to the
                // timestamp, so an expired-but-otherwise-valid chain is accepted.
                bool built = chain.Build(cert);
                if (built)
                    return ExecutableSignatureStatus.Trusted;

                foreach (X509ChainStatus status in chain.ChainStatus)
                {
                    if (status.Status != X509ChainStatusFlags.NotTimeValid &&
                        status.Status != X509ChainStatusFlags.NotTimeNested)
                    {
                        return ExecutableSignatureStatus.Invalid;
                    }
                }
                return ExecutableSignatureStatus.Trusted;
            }
            catch (Exception)
            {
                return ExecutableSignatureStatus.Invalid;
            }
            finally
            {
                cert.Dispose();
            }
        }

        internal static bool UserCanWriteLocation(string path)
        {
            string? directory;
            try
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(path));
            }
            catch (Exception)
            {
                return false;
            }
            if (string.IsNullOrEmpty(directory))
                return false;

            return IsHeuristicallyUserWritable(directory!) || DaclGrantsWrite(directory!);
        }

        private static bool IsHeuristicallyUserWritable(string directory)
        {
            foreach (string root in UserWritableRoots())
            {
                if (IsUnder(directory, root))
                    return true;
            }

            try
            {
                string? rootPath = Path.GetPathRoot(directory);
                if (!string.IsNullOrEmpty(rootPath))
                {
                    var drive = new DriveInfo(rootPath!);
                    if (drive.DriveType == DriveType.Removable)
                        return true;
                }
            }
            catch (Exception)
            {
                // Unknown drive type: fall through to the DACL check.
            }
            return false;
        }

        private static IEnumerable<string> UserWritableRoots()
        {
            string?[] candidates =
            {
                Environment.GetEnvironmentVariable("USERPROFILE"),
                Environment.GetEnvironmentVariable("TEMP"),
                Environment.GetEnvironmentVariable("TMP"),
                Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                Environment.GetEnvironmentVariable("APPDATA"),
                Environment.GetEnvironmentVariable("PUBLIC"),
                Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", @"Users\Public"),
            };
            foreach (string? candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                    yield return candidate!;
            }
        }

        private static bool IsUnder(string directory, string root)
        {
            try
            {
                string full = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
                string fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool DaclGrantsWrite(string directory)
        {
            try
            {
                HashSet<string> sids = CurrentUserSids();
                DirectorySecurity security = Directory.GetAccessControl(directory, AccessControlSections.Access);
                AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

                bool allowed = false;
                foreach (AuthorizationRule rule in rules)
                {
                    if (rule is not FileSystemAccessRule access)
                        continue;
                    if ((access.FileSystemRights & WriteRights) == 0)
                        continue;
                    if (!sids.Contains(access.IdentityReference.Value))
                        continue;
                    // Rules that apply only to subfolders/files still let the user replace
                    // the executable, so inheritance-only entries count too.
                    if (access.AccessControlType == AccessControlType.Deny)
                        return false;
                    allowed = true;
                }
                return allowed;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static HashSet<string> CurrentUserSids()
        {
            var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sid in AlwaysWritableSidPrefixes)
                sids.Add(sid);
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                if (identity.User != null)
                    sids.Add(identity.User.Value);
                if (identity.Groups != null)
                {
                    foreach (IdentityReference group in identity.Groups)
                    {
                        // The elevated controller token lists Administrators as enabled;
                        // the user's ordinary token has it deny-only. Exclude it so an
                        // elevated run does not flag every admin-writable folder.
                        if (!string.Equals(group.Value, AdministratorsSid, StringComparison.Ordinal))
                            sids.Add(group.Value);
                    }
                }
            }
            catch (Exception)
            {
                // Token unreadable: the well-known SIDs above still apply.
            }
            return sids;
        }

        private static DateTimeOffset? LastWriteUtc(string path)
        {
            try
            {
                return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

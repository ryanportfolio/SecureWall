using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    // Gathers the facts behind the block popup's warnings for one executable. Runs in the
    // controller process at display time (file I/O, WinVerifyTrust, DACL reads), then hands
    // the facts to the pure ExecutableRiskAssessment classifier.
    //
    // Known limits:
    // - Signature: WinVerifyTrust binds the file hash to the embedded signature, then falls
    //   back to the system catalogs (most Windows binaries are catalog-signed). Revocation
    //   is not checked and no URL is fetched, so a revoked certificate still reads Trusted.
    //   An expired certificate whose signature carries no timestamp reads Invalid.
    // - Timestamps: the last-write time comes from the file system and any process with
    //   write access can rewrite it (SetFileTime), so "recently modified" can be hidden.
    // - Writability: inspect both file and parent ownership/DACL mutation rights.
    //   Trusted administrators, SYSTEM and TrustedInstaller are excluded regardless
    //   of elevation. This conservative warning is not a Windows AccessCheck.
    internal static class ExecutableRiskProbe
    {
        // Returns null when there is no file to assess (no path reported, or the file is
        // missing), so the popup shows no warnings rather than three false ones.
        internal static ExecutableRiskFlags? Assess(string? executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return null;

            string path = executablePath!.Trim();
            try
            {
                File.GetAttributes(path);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception)
            {
                // Unreadable is not missing. Continue so failed inspection warns.
            }

            ExecutableSignatureStatus signature = SignatureStatus(path);
            bool userCanWrite = UserCanWriteLocation(path);
            DateTimeOffset? lastWrite = LastWriteUtc(path);
            return ExecutableRiskAssessment.Assess(signature, userCanWrite, lastWrite, DateTimeOffset.UtcNow);
        }

        // Embedded Authenticode first; a file without one is looked up in the catalog store
        // by its SHA-256 and the catalog signature is verified the same way. Any HRESULT
        // other than "no signature" (bad digest, untrusted root, expired without timestamp,
        // explicit distrust) is Invalid. Exceptions (P/Invoke failure, unreadable file)
        // are reported as Missing.
        internal static ExecutableSignatureStatus SignatureStatus(string path)
        {
            try
            {
                int embedded = VerifyEmbeddedSignature(path);
                if (embedded == 0)
                    return ExecutableSignatureStatus.Trusted;
                if (embedded != NativeMethods.TRUST_E_NOSIGNATURE)
                    return ExecutableSignatureStatus.Invalid;

                int? catalog = VerifyCatalogSignature(path);
                if (catalog == null)
                    return ExecutableSignatureStatus.Missing;
                return catalog.Value == 0 ? ExecutableSignatureStatus.Trusted : ExecutableSignatureStatus.Invalid;
            }
            catch (Exception)
            {
                return ExecutableSignatureStatus.Missing;
            }
        }

        private static int VerifyEmbeddedSignature(string path)
        {
            IntPtr pathPtr = IntPtr.Zero;
            IntPtr infoPtr = IntPtr.Zero;
            try
            {
                pathPtr = Marshal.StringToHGlobalUni(path);
                var info = new NativeMethods.WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(NativeMethods.WINTRUST_FILE_INFO)),
                    pcwszFilePath = pathPtr,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero,
                };
                infoPtr = Marshal.AllocHGlobal((int)info.cbStruct);
                Marshal.StructureToPtr(info, infoPtr, false);
                return VerifyTrust(NativeMethods.WTD_CHOICE_FILE, infoPtr);
            }
            finally
            {
                if (infoPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(infoPtr);
                if (pathPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(pathPtr);
            }
        }

        // Returns null when no catalog contains the file's hash, else the WinVerifyTrust
        // result for the catalog member.
        private static int? VerifyCatalogSignature(string path)
        {
            IntPtr catAdmin = IntPtr.Zero;
            IntPtr catInfo = IntPtr.Zero;
            IntPtr hashPtr = IntPtr.Zero;
            IntPtr catalogPathPtr = IntPtr.Zero;
            IntPtr memberTagPtr = IntPtr.Zero;
            IntPtr memberPathPtr = IntPtr.Zero;
            IntPtr infoPtr = IntPtr.Zero;
            try
            {
                Guid driverActionVerify = NativeMethods.DRIVER_ACTION_VERIFY;
                if (!NativeMethods.CryptCATAdminAcquireContext2(out catAdmin, ref driverActionVerify, "SHA256", IntPtr.Zero, 0))
                    return null;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                IntPtr fileHandle = stream.SafeFileHandle.DangerousGetHandle();

                uint hashLength = 0;
                NativeMethods.CryptCATAdminCalcHashFromFileHandle2(catAdmin, fileHandle, ref hashLength, IntPtr.Zero, 0);
                if (hashLength == 0 || hashLength > 128)
                    return null;
                hashPtr = Marshal.AllocHGlobal((int)hashLength);
                if (!NativeMethods.CryptCATAdminCalcHashFromFileHandle2(catAdmin, fileHandle, ref hashLength, hashPtr, 0))
                    return null;
                var hash = new byte[hashLength];
                Marshal.Copy(hashPtr, hash, 0, hash.Length);

                catInfo = NativeMethods.CryptCATAdminEnumCatalogFromHash(catAdmin, hashPtr, hashLength, 0, IntPtr.Zero);
                if (catInfo == IntPtr.Zero)
                    return null;

                var catalog = new NativeMethods.CATALOG_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(NativeMethods.CATALOG_INFO)),
                };
                if (!NativeMethods.CryptCATCatalogInfoFromContext(catInfo, ref catalog, 0))
                    return null;

                string memberTag = BitConverter.ToString(hash).Replace("-", string.Empty).ToUpperInvariant();
                catalogPathPtr = Marshal.StringToHGlobalUni(catalog.wszCatalogFile);
                memberTagPtr = Marshal.StringToHGlobalUni(memberTag);
                memberPathPtr = Marshal.StringToHGlobalUni(path);
                var info = new NativeMethods.WINTRUST_CATALOG_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(NativeMethods.WINTRUST_CATALOG_INFO)),
                    dwCatalogVersion = 0,
                    pcwszCatalogFilePath = catalogPathPtr,
                    pcwszMemberTag = memberTagPtr,
                    pcwszMemberFilePath = memberPathPtr,
                    hMemberFile = fileHandle,
                    pbCalculatedFileHash = hashPtr,
                    cbCalculatedFileHash = hashLength,
                    pcCatalogContext = IntPtr.Zero,
                    hCatAdmin = catAdmin,
                };
                infoPtr = Marshal.AllocHGlobal((int)info.cbStruct);
                Marshal.StructureToPtr(info, infoPtr, false);
                return VerifyTrust(NativeMethods.WTD_CHOICE_CATALOG, infoPtr);
            }
            finally
            {
                if (infoPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(infoPtr);
                if (memberPathPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(memberPathPtr);
                if (memberTagPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(memberTagPtr);
                if (catalogPathPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(catalogPathPtr);
                if (catInfo != IntPtr.Zero)
                    NativeMethods.CryptCATAdminReleaseCatalogContext(catAdmin, catInfo, 0);
                if (hashPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(hashPtr);
                if (catAdmin != IntPtr.Zero)
                    NativeMethods.CryptCATAdminReleaseContext(catAdmin, 0);
            }
        }

        // WTD_CACHE_ONLY_URL_RETRIEVAL and WTD_REVOKE_NONE keep the call offline. The
        // verify call is always paired with a close call so the provider state is freed.
        private static int VerifyTrust(uint unionChoice, IntPtr unionData)
        {
            Guid action = NativeMethods.WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var data = new NativeMethods.WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(NativeMethods.WINTRUST_DATA)),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = NativeMethods.WTD_UI_NONE,
                fdwRevocationChecks = NativeMethods.WTD_REVOKE_NONE,
                dwUnionChoice = unionChoice,
                pUnion = unionData,
                dwStateAction = NativeMethods.WTD_STATEACTION_VERIFY,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = NativeMethods.WTD_CACHE_ONLY_URL_RETRIEVAL,
                dwUIContext = NativeMethods.WTD_UICONTEXT_EXECUTE,
                pSignatureSettings = IntPtr.Zero,
            };

            int result;
            try
            {
                result = NativeMethods.WinVerifyTrust(NativeMethods.INVALID_HANDLE_VALUE, ref action, ref data);
            }
            finally
            {
                data.dwStateAction = NativeMethods.WTD_STATEACTION_CLOSE;
                NativeMethods.WinVerifyTrust(NativeMethods.INVALID_HANDLE_VALUE, ref action, ref data);
            }
            return result;
        }

        internal static bool UserCanWriteLocation(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string? directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(directory)) return true;
                return IsHeuristicallyUserWritable(directory!) ||
                    DaclGrantsWrite(fullPath, false) || DaclGrantsWrite(directory!, true);
            }
            catch (Exception)
            {
                return ExecutableMutationPolicy.HasRisk(null, null, false, false);
            }
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

        private static bool DaclGrantsWrite(string path, bool directory)
        {
            try
            {
                FileSystemSecurity security = directory
                    ? (FileSystemSecurity)Directory.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner)
                    : File.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner);
                var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
                // A null DACL permits everyone; an empty DACL does not.
                if (descriptor.DiscretionaryAcl == null)
                    return ExecutableMutationPolicy.HasRisk(null, null, directory, false);
                var entries = new List<ExecutableAccessEntry>();
                foreach (GenericAce ace in descriptor.DiscretionaryAcl)
                {
                    // Object/callback ACEs need richer evaluation. Do not silently
                    // drop unsupported entries and call the executable safe.
                    if (!(ace is CommonAce common) || common.IsCallback ||
                        (common.AceQualifier != AceQualifier.AccessAllowed && common.AceQualifier != AceQualifier.AccessDenied))
                        return ExecutableMutationPolicy.HasRisk(null, null, directory, false);
                    entries.Add(new ExecutableAccessEntry(common.SecurityIdentifier.Value,
                        unchecked((uint)common.AccessMask), common.AceQualifier == AceQualifier.AccessAllowed,
                        (common.AceFlags & AceFlags.InheritOnly) != 0));
                }
                return ExecutableMutationPolicy.HasRisk(descriptor.Owner?.Value, entries, directory);
            }
            catch (Exception)
            {
                return ExecutableMutationPolicy.HasRisk(null, null, directory, false);
            }
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

        private static class NativeMethods
        {
            internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

            internal static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
                new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            internal static readonly Guid DRIVER_ACTION_VERIFY =
                new Guid("F750E6C3-38EE-11d1-85E5-00C04FC295EE");

            internal const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);

            internal const uint WTD_UI_NONE = 2;
            internal const uint WTD_REVOKE_NONE = 0;
            internal const uint WTD_CHOICE_FILE = 1;
            internal const uint WTD_CHOICE_CATALOG = 2;
            internal const uint WTD_STATEACTION_VERIFY = 1;
            internal const uint WTD_STATEACTION_CLOSE = 2;
            internal const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
            internal const uint WTD_UICONTEXT_EXECUTE = 0;

            [StructLayout(LayoutKind.Sequential)]
            internal struct WINTRUST_FILE_INFO
            {
                public uint cbStruct;
                public IntPtr pcwszFilePath;
                public IntPtr hFile;
                public IntPtr pgKnownSubject;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct WINTRUST_CATALOG_INFO
            {
                public uint cbStruct;
                public uint dwCatalogVersion;
                public IntPtr pcwszCatalogFilePath;
                public IntPtr pcwszMemberTag;
                public IntPtr pcwszMemberFilePath;
                public IntPtr hMemberFile;
                public IntPtr pbCalculatedFileHash;
                public uint cbCalculatedFileHash;
                public IntPtr pcCatalogContext;
                public IntPtr hCatAdmin;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct WINTRUST_DATA
            {
                public uint cbStruct;
                public IntPtr pPolicyCallbackData;
                public IntPtr pSIPClientData;
                public uint dwUIChoice;
                public uint fdwRevocationChecks;
                public uint dwUnionChoice;
                public IntPtr pUnion;
                public uint dwStateAction;
                public IntPtr hWVTStateData;
                public IntPtr pwszURLReference;
                public uint dwProvFlags;
                public uint dwUIContext;
                public IntPtr pSignatureSettings;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct CATALOG_INFO
            {
                public uint cbStruct;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
                public string wszCatalogFile;
            }

            [DllImport("wintrust.dll", ExactSpelling = true)]
            internal static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

            [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptCATAdminAcquireContext2(
                out IntPtr phCatAdmin,
                ref Guid pgSubsystem,
                string pwszHashAlgorithm,
                IntPtr pStrongHashPolicy,
                uint dwFlags);

            [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptCATAdminCalcHashFromFileHandle2(
                IntPtr hCatAdmin,
                IntPtr hFile,
                ref uint pcbHash,
                IntPtr pbHash,
                uint dwFlags);

            [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
            internal static extern IntPtr CryptCATAdminEnumCatalogFromHash(
                IntPtr hCatAdmin,
                IntPtr pbHash,
                uint cbHash,
                uint dwFlags,
                IntPtr phPrevCatInfo);

            [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);

            [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

            [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);
        }
    }
}

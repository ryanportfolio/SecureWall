using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Samples.TaskDialog;
using Microsoft.Win32;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall.Installer
{
    // SecureWall.exe /apply-update "<msi>" "<manifest>" "<signature>" <requesting user SID>
    //
    // Started elevated by the controller's Install button. The controller is not a security
    // boundary: any process of the user could have written the downloaded files. So this step
    // verifies the signature itself, copies the files into a folder only SYSTEM and
    // Administrators can change, verifies the copies again, checks the package's identity, and
    // only then starts msiexec with the full setup UI. It exits right after.
    internal static class UpdateApplier
    {
        private const string Title = "SecureWall update";

        internal static int Run(string[] args)
        {
            try
            {
                Apply(args);
                return 0;
            }
            catch (UpdateRefusedException refused)
            {
                Report(refused.Message);
                return 1;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                exception is Win32Exception || exception is InvalidOperationException || exception is ArgumentException ||
                exception is NotSupportedException || exception is System.Security.SecurityException)
            {
                Utils.LogException(exception, Utils.LOG_ID_INSTALLER);
                Report("The update could not be prepared: " + exception.Message);
                return 1;
            }
        }

        private static void Apply(string[] args)
        {
            if (!UpdateApplyPolicy.TryParseArguments(args, out UpdateApplyPolicy.Arguments? request) || request == null)
                throw new UpdateRefusedException("The update step was started with unexpected arguments. Use Check for updates in SecureWall.");

            string helperSid;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (identity.IsSystem || !UpdateNetworkGrantPolicy.IsAllowedUser(identity.User?.Value))
                    throw new UpdateRefusedException("The update step does not run as the system or a service account.");
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UpdateRefusedException("Installing an update needs administrator permission.");
                helperSid = identity.User!.Value;
            }

            // 1. Source paths: plain local paths below the requesting user's updates folder.
            string profile = ProfileOf(request.UserSid);
            string updatesRoot = UpdateApplyPolicy.UserUpdatesRoot(Path.Combine(profile, "AppData", "Local"));
            foreach (string path in new[] { request.Package, request.Manifest, request.Signature })
                RequireSourcePath(path, updatesRoot, profile);

            // 2. Verify first, from bytes read once into memory.
            byte[] manifestBytes = ReadCapped(request.Manifest, SignedReleaseManifest.MaxManifestBytes);
            byte[] signatureBytes = ReadCapped(request.Signature, SignedReleaseManifest.MaxSignatureBytes);
            SignedReleaseManifest manifest = Verify(manifestBytes, signatureBytes);
            string platform = InstalledPlatform();
            string expectedHash = manifest.HashFor(platform)
                ?? throw new UpdateRefusedException("The signed release has no installer for this computer's SecureWall package (" + platform + ").");

            // 3 and 4. The protected staging folder, old staging removed, a fresh folder created.
            string parent = PrepareStagingParent();
            RemoveExpiredStaging(parent);
            string staging = Path.Combine(parent, UpdateApplyPolicy.NewStagingName(DateTime.UtcNow, Guid.NewGuid()));
            Directory.CreateDirectory(staging);
            MachineDataGuard.Check(staging, false);
            string stagedPackage = Path.Combine(staging, UpdateApplyPolicy.StagedPackage);
            string stagedManifest = Path.Combine(staging, UpdateApplyPolicy.StagedManifest);
            string stagedSignature = Path.Combine(staging, UpdateApplyPolicy.StagedSignature);
            WriteNew(stagedManifest, manifestBytes);
            WriteNew(stagedSignature, signatureBytes);
            CopyCapped(request.Package, stagedPackage, UpdateApplyPolicy.MaxPackageBytes);

            // 5. Verify the protected copies again: a swap in the user's folder gains nothing.
            SignedReleaseManifest staged = Verify(File.ReadAllBytes(stagedManifest), File.ReadAllBytes(stagedSignature));
            if (staged.Tag != manifest.Tag || staged.HashFor(platform) != expectedHash)
                throw new UpdateRefusedException("The update files changed while they were being copied. Nothing was installed.");
            if (SignedReleaseManifest.Sha256Hex(File.ReadAllBytes(stagedPackage)) != expectedHash)
                throw new UpdateRefusedException("The downloaded installer does not match the signed release. Nothing was installed.");

            // 6. The package itself: SecureWall, this platform, the signed version.
            CheckPackage(stagedPackage, staged.Version, platform);

            // 7. Windows Installer, full UI. Another user's credentials (over-the-shoulder UAC)
            // suppress the package's actions that would run as that administrator.
            bool suppressUserActions = !string.Equals(helperSid, request.UserSid, StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                Arguments = UpdateApplyPolicy.MsiexecArguments(stagedPackage, Path.Combine(staging, UpdateApplyPolicy.InstallLog), suppressUserActions),
                UseShellExecute = false,
                WorkingDirectory = staging,
            };
            using (Process.Start(start)) { }
            Utils.Log("Started the SecureWall " + staged.Tag + " installer from " + staging + ".", Utils.LOG_ID_INSTALLER);
        }

        private static SignedReleaseManifest Verify(byte[] manifestBytes, byte[] signatureBytes)
        {
            if (!SignedReleaseManifest.VerifySignature(manifestBytes, signatureBytes, ReleaseKeys.Trusted))
                throw new UpdateRefusedException("The update's signature does not verify. Nothing was installed.");
            if (!SignedReleaseManifest.TryParse(manifestBytes, out SignedReleaseManifest? manifest, out string error) || manifest == null)
                throw new UpdateRefusedException("The signed release manifest could not be read: " + error);
            if (!UpdateApplyPolicy.IsReleaseTag(manifest.Tag))
                throw new UpdateRefusedException("The signed release has an unexpected tag.");
            if (manifest.Version <= ReleaseVersion.Normalize(Utils.TinyWallVersion))
                throw new UpdateRefusedException("The signed release (" + manifest.Tag + ") is not newer than the installed SecureWall.");
            return manifest;
        }

        private static void RequireSourcePath(string path, string updatesRoot, string profile)
        {
            if (!UpdateApplyPolicy.IsAcceptableSourcePath(path, updatesRoot, out string error))
                throw new UpdateRefusedException(error);
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (drive.DriveType != DriveType.Fixed)
                throw new UpdateRefusedException("The update files must be on a local fixed drive.");
            // No reparse point anywhere below the user's profile folder down to the file.
            string top = profile.TrimEnd('\\');
            for (string? current = path; current != null && current.Length > top.Length; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UpdateRefusedException("The update files' folder contains a link: " + current);
            }
        }

        // The requesting user's profile folder; their %LOCALAPPDATA% is AppData\Local below it.
        private static string ProfileOf(string sid)
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? profile = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
            string? image = profile?.GetValue("ProfileImagePath") as string;
            if (string.IsNullOrEmpty(image))
                throw new UpdateRefusedException("The requesting account has no local profile.");
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(image!));
        }

        private static string InstalledPlatform()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = hklm.OpenSubKey(ProductRegistryPolicy.KeyPath);
                if (key?.GetValue("Platform") is string platform && Array.IndexOf(SignedReleaseManifest.Platforms, platform) >= 0)
                    return platform;
            }
            throw new UpdateRefusedException("SecureWall's installed package type is not recorded. Install the update by hand.");
        }

        // %ProgramData%\SecureWall Updates. Ordinary users may create folders in ProgramData and
        // own what they create, so an existing folder is used only if nobody but SYSTEM, the
        // Administrators and TrustedInstaller can change it; it is never repaired or deleted.
        private static string PrepareStagingParent()
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string parent = Path.Combine(programData, UpdateApplyPolicy.StagingFolderName);
            var ancestors = new Stack<string>();
            for (DirectoryInfo? dir = Directory.GetParent(parent); dir != null; dir = dir.Parent)
                ancestors.Push(dir.FullName);
            foreach (string ancestor in ancestors)
                MachineDataGuard.Check(ancestor, true);
            if (!Directory.Exists(parent) && !File.Exists(parent))
            {
                var acl = new DirectorySecurity();
                acl.SetSecurityDescriptorSddlForm(UpdateApplyPolicy.StagingSddl);
                Directory.CreateDirectory(parent, acl);
            }
            try
            {
                MachineDataGuard.Check(parent, false);
            }
            catch (UnauthorizedAccessException)
            {
                throw new UpdateRefusedException("The folder " + parent + " is not protected (another account owns it or can change it). " +
                    "An administrator should check and remove it; SecureWall creates it again. Nothing was installed.");
            }
            return parent;
        }

        // Staging folders older than 24 hours go; recent ones stay, including one another
        // helper just handed to msiexec. Links are never followed or removed, and a folder
        // that cannot be deleted is left.
        private static void RemoveExpiredStaging(string parent)
        {
            foreach (string folder in Directory.GetDirectories(parent))
            {
                try
                {
                    var info = new DirectoryInfo(folder);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        !UpdateApplyPolicy.IsExpiredStaging(info.CreationTimeUtc, DateTime.UtcNow))
                        continue;
                    DeleteTree(info);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    Utils.Log("Left an old update folder in place: " + folder + " (" + exception.Message + ")", Utils.LOG_ID_INSTALLER);
                }
            }
        }

        private static void DeleteTree(DirectoryInfo folder)
        {
            foreach (FileSystemInfo entry in folder.GetFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("It contains a link.");
                if (entry is DirectoryInfo child)
                    DeleteTree(child);
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }
            }
            folder.Delete(false);
        }

        private static byte[] ReadCapped(string path, int maxBytes)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0 || stream.Length > maxBytes)
                throw new UpdateRefusedException("An update file has an unexpected size: " + Path.GetFileName(path));
            var bytes = new byte[stream.Length];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = stream.Read(bytes, read, bytes.Length - read);
                if (n == 0)
                    throw new UpdateRefusedException("An update file changed while it was read: " + Path.GetFileName(path));
                read += n;
            }
            return bytes;
        }

        private static void WriteNew(string path, byte[] bytes)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        private static void CopyCapped(string source, string target, long maxBytes)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length == 0 || input.Length > maxBytes)
                throw new UpdateRefusedException("The downloaded installer has an unexpected size.");
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > maxBytes)
                    throw new UpdateRefusedException("The downloaded installer has an unexpected size.");
                output.Write(buffer, 0, n);
            }
            output.Flush(true);
        }

        private static void CheckPackage(string package, Version version, string platform)
        {
            Msi.Open(package, out uint database);
            try
            {
                string? upgradeCode = Msi.Property(database, "UpgradeCode");
                string? productVersion = Msi.Property(database, "ProductVersion");
                bool faultTest = Msi.Property(database, "SECUREWALL_FAULT_TEST") != null;
                string? template = Msi.Template(database);
                if (!UpdateApplyPolicy.PackageMatches(upgradeCode, template, productVersion, faultTest, version, platform, out string error))
                    throw new UpdateRefusedException(error + " Nothing was installed.");
            }
            finally
            {
                Msi.Close(database);
            }
        }

        private static void Report(string message)
        {
            try { Utils.Log("Update not applied: " + message, Utils.LOG_ID_INSTALLER); } catch { }
            Utils.ShowMessageBox(message, Title, TaskDialogCommonButtons.Close, TaskDialogIcon.Warning);
        }

        private sealed class UpdateRefusedException : Exception
        {
            internal UpdateRefusedException(string message) : base(message) { }
        }

        // Read-only access to an MSI database through msi.dll. MSIHANDLE is 32 bits on every platform.
        private static class Msi
        {
            private const int ERROR_SUCCESS = 0;
            private const int ERROR_NO_MORE_ITEMS = 259;
            private const int ERROR_MORE_DATA = 234;
            private const int PID_TEMPLATE = 7;

            internal static void Open(string path, out uint database)
            {
                int result = MsiOpenDatabase(path, IntPtr.Zero, out database);
                if (result != ERROR_SUCCESS)
                    throw new UpdateRefusedException("The downloaded installer could not be opened (error " + result + ").");
            }

            internal static void Close(uint handle)
            {
                if (handle != 0)
                    MsiCloseHandle(handle);
            }

            internal static string? Property(uint database, string name)
            {
                // Property names here are constants; nothing from the package enters the query.
                uint view = 0, record = 0;
                try
                {
                    Check(MsiDatabaseOpenView(database, "SELECT `Value` FROM `Property` WHERE `Property`='" + name + "'", out view));
                    Check(MsiViewExecute(view, 0));
                    int fetched = MsiViewFetch(view, out record);
                    if (fetched == ERROR_NO_MORE_ITEMS)
                        return null;
                    Check(fetched);
                    var value = new StringBuilder(256);
                    int length = value.Capacity;
                    int result = MsiRecordGetString(record, 1, value, ref length);
                    if (result == ERROR_MORE_DATA)
                    {
                        value = new StringBuilder(++length);
                        result = MsiRecordGetString(record, 1, value, ref length);
                    }
                    Check(result);
                    return value.ToString();
                }
                finally
                {
                    Close(record);
                    Close(view);
                }
            }

            internal static string? Template(uint database)
            {
                uint summary = 0;
                try
                {
                    Check(MsiGetSummaryInformation(database, null, 0, out summary));
                    var value = new StringBuilder(256);
                    int length = value.Capacity;
                    long time = 0;
                    Check(MsiSummaryInfoGetProperty(summary, PID_TEMPLATE, out _, out _, ref time, value, ref length));
                    return value.ToString();
                }
                finally
                {
                    Close(summary);
                }
            }

            private static void Check(int result)
            {
                if (result != ERROR_SUCCESS)
                    throw new UpdateRefusedException("The downloaded installer could not be read (error " + result + ").");
            }

            [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
            private static extern int MsiOpenDatabase(string databasePath, IntPtr persist, out uint database);

            [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
            private static extern int MsiDatabaseOpenView(uint database, string query, out uint view);

            [DllImport("msi.dll")]
            private static extern int MsiViewExecute(uint view, uint record);

            [DllImport("msi.dll")]
            private static extern int MsiViewFetch(uint view, out uint record);

            [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
            private static extern int MsiRecordGetString(uint record, int field, StringBuilder value, ref int length);

            [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetSummaryInformationW")]
            private static extern int MsiGetSummaryInformation(uint database, string? databasePath, int updateCount, out uint summary);

            [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiSummaryInfoGetPropertyW")]
            private static extern int MsiSummaryInfoGetProperty(uint summary, int property, out int dataType, out int intValue,
                ref long fileTime, StringBuilder value, ref int length);

            [DllImport("msi.dll")]
            private static extern int MsiCloseHandle(uint handle);
        }
    }
}

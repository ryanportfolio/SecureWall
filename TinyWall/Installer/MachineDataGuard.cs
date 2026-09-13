using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall.Installer
{
    internal static class MachineDataGuard
    {
        internal static string PathName => Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData), SecureWallProduct.AppDataFolderName);

        private static readonly object Gate = new object();
        private static bool validated;

        internal static void Require(bool create = false, bool recheck = false)
        {
            lock (Gate)
            {
                if (validated && !recheck) return;
                // A rejected forced check must also disable subsequent cached IO.
                validated = false;
                Validate(create);
                // All descendants are now protected from unprivileged replacement.
                // Trusted administrators remain outside this security boundary.
                validated = true;
            }
        }

        private static void Validate(bool create)
        {
            string path = PathName;
            MachineDataPolicy.Prepare(() =>
            {
                // Check top down before descending into a potentially redirected parent.
                var ancestors = new Stack<string>();
                for (var parent = Directory.GetParent(path); parent != null; parent = parent.Parent)
                    ancestors.Push(parent.FullName);
                foreach (string ancestor in ancestors) Check(ancestor, true);
            }, () =>
            {
                try { File.GetAttributes(path); return true; }
                catch (FileNotFoundException) { return false; }
                catch (DirectoryNotFoundException) { return false; }
            }, create, () =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (!identity.IsSystem)
                    throw new UnauthorizedAccessException("Machine data creation requires LocalSystem MSI maintenance.");
                var acl = new DirectorySecurity();
                acl.SetSecurityDescriptorSddlForm("O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)");
                Directory.CreateDirectory(path, acl);
            }, () => CheckTree(path));
        }

        private static void CheckTree(string path)
        {
            MachineDataPolicy.CheckTree(path, entry => Check(entry, false), Directory.GetFileSystemEntries);
        }

        private static bool Check(string path, bool ancestor)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Machine data paths must not contain reparse points: " + path);
            FileSystemSecurity security = (attributes & FileAttributes.Directory) != 0
                ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
            var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            var grants = new List<MachineDataPolicy.Grant>();
            if (raw.DiscretionaryAcl != null)
                foreach (GenericAce ace in raw.DiscretionaryAcl)
                {
                    // Unexpected/object/callback ACEs cannot silently escape validation.
                    if (!(ace is CommonAce common) || common.IsCallback)
                        throw new UnauthorizedAccessException("Unsupported machine data ACL entry: " + path);
                    grants.Add(new MachineDataPolicy.Grant(common.SecurityIdentifier.Value,
                        unchecked((uint)common.AccessMask), common.AceQualifier == AceQualifier.AccessAllowed,
                        (common.AceFlags & AceFlags.InheritOnly) != 0));
                }
            try { MachineDataPolicy.Require(raw.Owner?.Value ?? "", false, raw.DiscretionaryAcl == null, ancestor, grants); }
            catch (InvalidOperationException exception)
            { throw new UnauthorizedAccessException("Unsafe machine data path: " + path, exception); }
            return (attributes & FileAttributes.Directory) != 0;
        }

        internal static void InstallDefaults()
        {
            Require(true, true);
            string source = Path.Combine(Path.GetDirectoryName(Utils.ExecutablePath)!, "data-defaults");
            foreach (string name in new[] { "profiles.json", "hosts.bck" })
            {
                string target = Path.Combine(PathName, name);
                Require();
                // Preserve existing state and evidence. The validated protected parent
                // prevents ordinary users from substituting the target after validation.
                if (!File.Exists(target)) File.Copy(Path.Combine(source, name), target, false);
            }
            Require(false, true);
        }
    }
}

using System.IO;
using System.Security.AccessControl;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall.Installer
{
    // The machine data directory grants BUILTIN\Users read to everything below it.
    // Secret files (the password hash) replace that with MachineDataPolicy.SecretFileDacl.
    internal static class SecretFileProtection
    {
        private static FileSecurity Dacl()
        {
            var security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(MachineDataPolicy.SecretFileDacl, AccessControlSections.Access);
            return security;
        }

        // The protected DACL is part of the create call, so no user-readable window exists.
        internal static FileStream CreateNew(string path) =>
            new FileStream(path, FileMode.CreateNew, FileSystemRights.WriteData, FileShare.None, 4096, FileOptions.None, Dacl());

        // Creates an empty protected file, or replaces the DACL of an existing one
        // (for example a pwd written before this protection existed). The owner is kept;
        // MachineDataGuard already requires it to be trusted.
        internal static void Ensure(string path)
        {
            try
            {
                using (CreateNew(path)) { }
                return;
            }
            catch (IOException) when (File.Exists(path)) { }

            File.SetAccessControl(path, Dacl());
        }
    }
}

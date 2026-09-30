using System.Security.AccessControl;
using System.Security.Cryptography;
using pylorak.TinyWall.Prompting;
using pylorak.Utilities;

namespace SecureWall.Core.Tests;

internal static class PasswordStorageTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("legacy 3.5.1 password hash still verifies", LegacyHashVerifies),
        ("new password hash format round trips", NewFormatRoundTrips),
        ("tampered or oversized password hash parameters are rejected", TamperedParametersAreRejected),
        ("password hash comparison is case-sensitive", ComparisonIsCaseSensitive),
        ("successful unlock upgrades legacy and old-parameter hashes", UpgradeProducesNewFormat),
        ("fixed-time comparison checks length and every byte", FixedTimeComparison),
        ("protected pwd DACL passes machine data validation without user read", SecretFileDaclPassesValidation),
        ("password storage adapters protect, lock and upgrade pwd", PasswordAdapterWiring),
    };

    // The client sends Hasher.HashString(passphrase); this is the uppercase SHA-256 of "password".
    private const string Password = "5E884898DA28047151D0E56F8DC6292773603D0D6AABBDD62A11EF721D1542D8";
    // Produced by the pre-port code path (.NET Framework Rfc2898DeriveBytes default SHA-1,
    // 150000 iterations, 16 bytes, alphanumeric 8-character salt).
    private const string LegacyStored = "Rfc2898;k3Q9zLp2;150000;16;23FgEM2W0Skg3HaaWCxQjQ==";
    private const Pbkdf2.HashFunction CurrentAlgorithm = Pbkdf2.HashFunction.SHA_256;
    private const int CurrentIterations = 200_000;

    private static byte[] Salt(byte seed) => Enumerable.Range(0, 16).Select(i => (byte)(seed + i * 7)).ToArray();

    private static string NewStored(string password, int iterations = CurrentIterations,
        Pbkdf2.HashFunction algorithm = CurrentAlgorithm) =>
        new Pbkdf2(algorithm, iterations, Salt(3), password).ToString(Pbkdf2.StorageFormat.Tw352);

    private static void LegacyHashVerifies()
    {
        AssertEx.True(Pbkdf2.VerifyStored(LegacyStored, Password, CurrentAlgorithm, CurrentIterations, out bool upgrade));
        AssertEx.True(upgrade);
        AssertEx.False(Pbkdf2.VerifyStored(LegacyStored, Password.ToLowerInvariant(), CurrentAlgorithm, CurrentIterations, out upgrade));
        AssertEx.False(upgrade, "A failed unlock must not rewrite the stored hash.");
        // The legacy writer still produces the identical string from the parsed values.
        AssertEx.Equal(LegacyStored, Pbkdf2.Parse(LegacyStored, Pbkdf2.StorageFormat.Legacy).ToString(Pbkdf2.StorageFormat.Legacy));
    }

    private static void NewFormatRoundTrips()
    {
        string stored = NewStored(Password);
        string[] parts = stored.Split(';');
        AssertEx.Equal(5, parts.Length);
        AssertEx.Equal("Rfc2898-TWv352", parts[0]);
        AssertEx.Equal("SHA-256", parts[1]);
        AssertEx.SequenceEqual(Salt(3), Convert.FromBase64String(parts[2]));
        AssertEx.Equal("200000", parts[3]);
        // Independent reference derivation: PBKDF2-HMAC-SHA256, 32 bytes.
        AssertEx.SequenceEqual(Rfc2898DeriveBytes.Pbkdf2(Password, Salt(3), CurrentIterations, HashAlgorithmName.SHA256, 32),
            Convert.FromBase64String(parts[4]));

        var parsed = Pbkdf2.Parse(stored, Pbkdf2.StorageFormat.Tw352);
        AssertEx.Equal(stored, parsed.ToString(Pbkdf2.StorageFormat.Tw352));
        AssertEx.True(Pbkdf2.VerifyStored(stored, Password, CurrentAlgorithm, CurrentIterations, out bool upgrade));
        AssertEx.False(upgrade);
        AssertEx.False(Pbkdf2.VerifyStored(stored, Password + "0", CurrentAlgorithm, CurrentIterations, out _));
    }

    private static void TamperedParametersAreRejected()
    {
        string[] valid = NewStored(Password).Split(';');
        string With(int index, string value)
        {
            var copy = (string[])valid.Clone();
            copy[index] = value;
            return string.Join(";", copy);
        }

        var rejected = new List<string>();
        foreach (string iterations in new[] { "99999", "10000001", "99999999999", "2147483648", "-200000", "+200000", " 200000", "200000 ", "2e5", "200,000", "0x30D40", "" })
            rejected.Add(With(3, iterations));
        foreach (string algorithm in new[] { "MD5", "sha-256", "SHA256", "" })
            rejected.Add(With(1, algorithm));
        rejected.Add(With(2, Convert.ToBase64String(new byte[7])));      // salt too short
        rejected.Add(With(2, Convert.ToBase64String(new byte[65])));     // salt too long
        rejected.Add(With(2, "not base64!"));
        rejected.Add(With(4, Convert.ToBase64String(new byte[31])));     // wrong hash length
        rejected.Add(With(4, Convert.ToBase64String(new byte[64])));
        rejected.Add(With(0, "Rfc2898-TWv353"));
        rejected.Add(string.Join(";", valid) + ";extra");
        rejected.Add(With(2, Convert.ToBase64String(new byte[400])));    // over the 512-character bound
        rejected.Add(LegacyStored.Replace(";150000;", ";99999999999;"));
        rejected.Add(LegacyStored.Replace(";150000;", ";1;"));
        rejected.Add(LegacyStored.Replace(";16;", ";20;"));
        rejected.Add(LegacyStored.Replace(";k3Q9zLp2;", ";short;"));
        rejected.Add("");
        rejected.Add(new string('A', 10_000));

        foreach (string stored in rejected)
        {
            AssertEx.Throws<FormatException>(() => Pbkdf2.VerifyStored(stored, Password, CurrentAlgorithm, CurrentIterations, out _));
            AssertEx.Throws<FormatException>(() => Pbkdf2.Parse(stored, Pbkdf2.StorageFormat.Tw352));
            AssertEx.Throws<FormatException>(() => Pbkdf2.Parse(stored, Pbkdf2.StorageFormat.Legacy));
        }

        // A well-formed but altered hash value is a mismatch, not an error.
        byte[] hash = Convert.FromBase64String(valid[4]);
        hash[^1] ^= 1;
        AssertEx.False(Pbkdf2.VerifyStored(With(4, Convert.ToBase64String(hash)), Password, CurrentAlgorithm, CurrentIterations, out _));
    }

    private static string SwapCase(string s) =>
        new string(s.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());

    private static void ComparisonIsCaseSensitive()
    {
        // The pre-port comparison was OrdinalIgnoreCase on the base64 text, so these matched.
        string[] legacy = LegacyStored.Split(';');
        legacy[4] = SwapCase(legacy[4]);
        AssertEx.False(Pbkdf2.VerifyStored(string.Join(";", legacy), Password, CurrentAlgorithm, CurrentIterations, out bool upgrade));
        AssertEx.False(upgrade);

        string[] current = NewStored(Password).Split(';');
        current[4] = SwapCase(current[4]);
        AssertEx.False(Pbkdf2.VerifyStored(string.Join(";", current), Password, CurrentAlgorithm, CurrentIterations, out _));
    }

    private static void UpgradeProducesNewFormat()
    {
        foreach (string old in new[] { LegacyStored, NewStored(Password, 150_000), NewStored(Password, 300_000),
            NewStored(Password, CurrentIterations, Pbkdf2.HashFunction.SHA_1) })
        {
            AssertEx.True(Pbkdf2.VerifyStored(old, Password, CurrentAlgorithm, CurrentIterations, out bool upgrade));
            AssertEx.True(upgrade, "Expected an upgrade for " + old);
            AssertEx.False(Pbkdf2.VerifyStored(old, "wrong", CurrentAlgorithm, CurrentIterations, out upgrade));
            AssertEx.False(upgrade);
        }

        // PasswordLock.SetPass writes this; it verifies without a further upgrade.
        string upgraded = new Pbkdf2(CurrentAlgorithm, CurrentIterations, Salt(11), Password).ToString(Pbkdf2.StorageFormat.Tw352);
        AssertEx.True(upgraded.StartsWith("Rfc2898-TWv352;SHA-256;", StringComparison.Ordinal));
        AssertEx.True(Pbkdf2.VerifyStored(upgraded, Password, CurrentAlgorithm, CurrentIterations, out bool again));
        AssertEx.False(again);
    }

    private static void FixedTimeComparison()
    {
        AssertEx.True(Pbkdf2.FixedTimeEquals(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }));
        AssertEx.True(Pbkdf2.FixedTimeEquals(Array.Empty<byte>(), Array.Empty<byte>()));
        AssertEx.False(Pbkdf2.FixedTimeEquals(new byte[] { 1, 2, 3 }, new byte[] { 1, 2 }));
        AssertEx.False(Pbkdf2.FixedTimeEquals(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }));
        for (int i = 0; i < 3; ++i)
        {
            var other = new byte[] { 1, 2, 3 };
            other[i] ^= 0x80;
            AssertEx.False(Pbkdf2.FixedTimeEquals(new byte[] { 1, 2, 3 }, other));
        }
    }

    private static void SecretFileDaclPassesValidation()
    {
        if (!OperatingSystem.IsWindows()) return;

        var raw = new RawSecurityDescriptor(MachineDataPolicy.SecretFileDacl);
        AssertEx.True((raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, "The pwd DACL must not inherit the directory's Users read.");
        var grants = new List<MachineDataPolicy.Grant>();
        foreach (GenericAce ace in raw.DiscretionaryAcl!)
        {
            // Same conversion as MachineDataGuard.Check.
            var common = (CommonAce)ace;
            grants.Add(new MachineDataPolicy.Grant(common.SecurityIdentifier.Value, unchecked((uint)common.AccessMask),
                common.AceQualifier == AceQualifier.AccessAllowed, (common.AceFlags & AceFlags.InheritOnly) != 0));
        }
        foreach (string owner in new[] { "S-1-5-18", "S-1-5-32-544" })
            MachineDataPolicy.Require(owner, false, false, false, grants);

        const uint readData = 0x1, readEa = 0x8, genericRead = 0x80000000, genericAll = 0x10000000, readControl = 0x20000, readAttributes = 0x80;
        AssertEx.SequenceEqual(new[] { "S-1-5-18", "S-1-5-32-544", "S-1-5-32-545" }, grants.Select(g => g.Sid).ToArray());
        var users = grants.Single(g => g.Sid == "S-1-5-32-545");
        AssertEx.Equal(0u, users.Rights & (readData | readEa | genericRead | genericAll));
        // MachineDataGuard reads each entry's DACL and attributes, also from a standard-user controller.
        AssertEx.Equal(readControl | readAttributes, users.Rights & (readControl | readAttributes));
        // The directory grant it replaces includes FILE_READ_DATA.
        AssertEx.Equal(readData, 0x1200a9u & readData);
    }

    private static void PasswordAdapterWiring()
    {
        DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "TinyWall", "Settings.cs"))) root = root.Parent;
        AssertEx.True(root != null);
        string Read(string file) => File.ReadAllText(Path.Combine(root!.FullName, "TinyWall", file));
        string Section(string text, string begin, string end)
        {
            int start = text.IndexOf(begin, StringComparison.Ordinal);
            AssertEx.True(start >= 0, "Missing " + begin);
            return text.Substring(start, text.IndexOf(end, start, StringComparison.Ordinal) - start);
        }

        string settings = Read("Settings.cs");
        string setPass = Section(settings, "internal static void SetPass(", "internal static bool Unlock(");
        AssertEx.True(setPass.IndexOf("Installer.SecretFileProtection.Ensure(PasswordFilePath);", StringComparison.Ordinal) <
            setPass.IndexOf("new AtomicFileUpdater(PasswordFilePath)", StringComparison.Ordinal));
        AssertEx.True(setPass.Contains("Installer.SecretFileProtection.CreateNew(fileUpdater.TemporaryFilePath)"));
        AssertEx.False(setPass.Contains("File.WriteAllText"), "The temporary pwd file must be created protected.");
        AssertEx.True(setPass.Contains("RandomNumberGenerator.Create()") && setPass.Contains("Pbkdf2.StorageFormat.Tw352"));
        string unlock = Section(settings, "internal static bool Unlock(", "internal static bool HasPassword");
        AssertEx.True(unlock.Contains("if (!_Locked && needsUpgrade)"));

        string service = Read("TinyWallService.cs");
        AssertEx.False(service.Contains("FileLocker.Lock(PasswordLock.PasswordFilePath, FileAccess.Read, FileShare.Read)"));
        AssertEx.Equal(1, service.Split("FileLocker.Lock(PasswordLock.PasswordFilePath").Length - 1);
        AssertEx.True(service.Contains("FileLocker.Lock(PasswordLock.PasswordFilePath, FileAccess.Read, FileShare.None);"));
        string lockHelper = Section(service, "private void LockPasswordFile()", "FileShare.None);");
        AssertEx.True(lockHelper.Contains("Installer.SecretFileProtection.Ensure(PasswordLock.PasswordFilePath);"));
        string unlockCase = Section(service, "case MessageType.UNLOCK:", "case MessageType.LOCK:");
        AssertEx.True(unlockCase.IndexOf("FileLocker.Unlock(PasswordLock.PasswordFilePath);", StringComparison.Ordinal) <
            unlockCase.IndexOf("PasswordLock.Unlock(args.Password)", StringComparison.Ordinal));
        AssertEx.True(unlockCase.Contains("LockPasswordFile();"));
        AssertEx.True(Section(service, "case MessageType.SET_PASSPHRASE:", "case MessageType.STOP_SERVICE:").Contains("LockPasswordFile();"));

        // Password-locked /uninstall shows PasswordForm without controller settings.
        string utils = Read("Utils.cs");
        AssertEx.True(utils.Contains("IsDarkModeActive(ControllerSettings? settings)"));
        AssertEx.False(Section(utils, "IsDarkModeActive(ControllerSettings? settings)", "return !AppsUseLightTheme();").Contains("settings.UiTheme"));
    }
}

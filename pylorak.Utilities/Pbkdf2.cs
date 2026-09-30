using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace pylorak.Utilities
{
    public sealed class Pbkdf2
    {
        // Parsed values are bounded so a malformed or planted hash file cannot request
        // an unbounded derivation cost or allocation.
        public const int ITERATIONS_MIN = 100_000;
        public const int ITERATIONS_MAX = 10_000_000;
        public const int MAX_STORAGE_CHARS = 512;
        public const int MIN_SALT_LENGTH = 8;
        public const int MAX_SALT_LENGTH = 64;

        public enum StorageFormat
        {
            Legacy,     // TinyWall 3.5.1 and SecureWall up to 0.3.0. Verified and upgraded on the next successful unlock.
            Tw352
        }

        public enum HashFunction
        {
            SHA_1,
            SHA_256,
            SHA_512
        }

        public HashFunction Algorithm { get; }
        public int Iterations { get; }
        public byte[] Salt { get; }
        public byte[] Hash { get; }

        public Pbkdf2(HashFunction algorithm, int iterations, byte[] salt, byte[] hash)
        {
            ValidateParameters(algorithm, iterations, salt);
            if (hash is null)
                throw new ArgumentNullException(nameof(hash));
            if (hash.Length != GetExpectedNumHashBytes(algorithm))
                throw new ArgumentException("Hash length does not match the algorithm.", nameof(hash));

            Algorithm = algorithm;
            Iterations = iterations;
            Salt = salt;
            Hash = hash;
        }

        public Pbkdf2(HashFunction algorithm, int iterations, byte[] salt, string clearText)
        {
            ValidateParameters(algorithm, iterations, salt);

            Algorithm = algorithm;
            Iterations = iterations;
            Salt = salt;
            Hash = GetHash(clearText);
        }

        private static void ValidateParameters(HashFunction algorithm, int iterations, byte[] salt)
        {
            if (salt is null)
                throw new ArgumentNullException(nameof(salt));
            if ((salt.Length < MIN_SALT_LENGTH) || (salt.Length > MAX_SALT_LENGTH))
                throw new ArgumentException("Invalid salt length.", nameof(salt));
            if ((iterations < ITERATIONS_MIN) || (iterations > ITERATIONS_MAX))
                throw new ArgumentOutOfRangeException(nameof(iterations));
            _ = GetExpectedNumHashBytes(algorithm);
        }

        public static string Algo2Str(HashFunction algo)
        {
            return algo switch
            {
                HashFunction.SHA_1 => "SHA-1",
                HashFunction.SHA_256 => "SHA-256",
                HashFunction.SHA_512 => "SHA-512",
                _ => throw new ArgumentException("Invalid hash function.", nameof(algo))
            };
        }

        public static HashFunction Str2Algo(string algo)
        {
            return algo switch
            {
                "SHA-1" => HashFunction.SHA_1,
                "SHA-256" => HashFunction.SHA_256,
                "SHA-512" => HashFunction.SHA_512,
                _ => throw new ArgumentException("Invalid hash function.", nameof(algo))
            };
        }

        public static int GetExpectedNumHashBytes(HashFunction algo)
        {
            return algo switch
            {
                HashFunction.SHA_1 => 16,  // 16 (instead of 20) for compatibility with the legacy format
                HashFunction.SHA_256 => 32,
                HashFunction.SHA_512 => 64,
                _ => throw new ArgumentException("Invalid hash function.", nameof(algo))
            };
        }

        // Digits only: no sign, whitespace, separators or exponent.
        private static int ParseCount(string s)
        {
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                throw new FormatException();
            return value;
        }

        private static string[] SplitStored(string storedHash, string tag)
        {
            if (string.IsNullOrEmpty(storedHash) || (storedHash.Length > MAX_STORAGE_CHARS))
                throw new FormatException();

            var parts = storedHash.Split(';');
            if ((parts.Length != 5) || (parts[0] != tag))
                throw new FormatException();
            return parts;
        }

        public static Pbkdf2 Parse_LegacyFormat(string storedHash)
        {
            // Format: Rfc2898;salt;iterations;numHashBytes;hash
            try
            {
                var parts = SplitStored(storedHash, "Rfc2898");
                var algo = HashFunction.SHA_1;  // implicit in this format
                var salt = Encoding.UTF8.GetBytes(parts[1]);  // the legacy salt is alphanumeric text
                var iterations = ParseCount(parts[2]);
                if (ParseCount(parts[3]) != GetExpectedNumHashBytes(algo))
                    throw new FormatException();
                var hash = Convert.FromBase64String(parts[4]);
                return new Pbkdf2(algo, iterations, salt, hash);
            }
            catch (Exception inner)
            {
                throw new FormatException("Invalid password hash.", inner);
            }
        }

        public static Pbkdf2 Parse_Tw352Format(string storedHash)
        {
            // Format: Rfc2898-TWv352;algo;salt;iterations;hash
            try
            {
                var parts = SplitStored(storedHash, "Rfc2898-TWv352");
                var algo = Str2Algo(parts[1]);
                var salt = Convert.FromBase64String(parts[2]);
                var iterations = ParseCount(parts[3]);
                var hash = Convert.FromBase64String(parts[4]);
                return new Pbkdf2(algo, iterations, salt, hash);
            }
            catch (Exception inner)
            {
                throw new FormatException("Invalid password hash.", inner);
            }
        }

        public static Pbkdf2 Parse(string storedHash, StorageFormat format)
        {
            return format switch
            {
                StorageFormat.Legacy => Parse_LegacyFormat(storedHash),
                StorageFormat.Tw352 => Parse_Tw352Format(storedHash),
                _ => throw new ArgumentException("Invalid hash storage format.", nameof(format))
            };
        }

        // Accepts both storage formats. needsUpgrade is set when the stored hash uses the
        // legacy format or parameters other than the current ones; callers rewrite it only
        // after a successful match. Throws FormatException for an unparseable hash.
        public static bool VerifyStored(string storedHash, string clearText,
            HashFunction currentAlgorithm, int currentIterations, out bool needsUpgrade)
        {
            Pbkdf2 hasher;
            needsUpgrade = false;
            if ((storedHash != null) && storedHash.StartsWith("Rfc2898-TWv352;", StringComparison.Ordinal))
                hasher = Parse_Tw352Format(storedHash);
            else
            {
                hasher = Parse_LegacyFormat(storedHash!);
                needsUpgrade = true;
            }

            if (!hasher.IsHashOf(clearText))
            {
                needsUpgrade = false;
                return false;
            }

            needsUpgrade |= (hasher.Algorithm != currentAlgorithm) || (hasher.Iterations != currentIterations);
            return true;
        }

        public byte[] GetHash(string clearText)
        {
            var algo = Algorithm switch
            {
                HashFunction.SHA_1 => HashAlgorithmName.SHA1,
                HashFunction.SHA_256 => HashAlgorithmName.SHA256,
                HashFunction.SHA_512 => HashAlgorithmName.SHA512,
                _ => throw new InvalidOperationException("Invalid algorithm.")
            };
            using var hasher = new Rfc2898DeriveBytes(clearText, Salt, Iterations, algo);
            return hasher.GetBytes(GetExpectedNumHashBytes(Algorithm));
        }

        public bool IsHashOf(string clearText)
        {
            return FixedTimeEquals(Hash, GetHash(clearText));
        }

        // net48 has no CryptographicOperations.FixedTimeEquals. The loop visits every byte
        // regardless of where the first difference is; lengths are not secret.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < left.Length; ++i)
                diff |= left[i] ^ right[i];
            return diff == 0;
        }

        public string ToString(StorageFormat format)
        {
            if ((format == StorageFormat.Legacy) && (Algorithm != HashFunction.SHA_1))
                throw new InvalidOperationException("Legacy format only supports SHA-1.");

            string stored = format switch
            {
                StorageFormat.Legacy => string.Format(CultureInfo.InvariantCulture, "Rfc2898;{0};{1};{2};{3}",
                    Encoding.UTF8.GetString(Salt),
                    Iterations,
                    Hash.Length,
                    Convert.ToBase64String(Hash)),
                StorageFormat.Tw352 => string.Format(CultureInfo.InvariantCulture, "Rfc2898-TWv352;{0};{1};{2};{3}",
                    Algo2Str(Algorithm),
                    Convert.ToBase64String(Salt),
                    Iterations,
                    Convert.ToBase64String(Hash)),
                _ => throw new ArgumentException("Invalid hash storage format.", nameof(format))
            };

            if (stored.Length > MAX_STORAGE_CHARS)
                throw new InvalidOperationException("Stored hash too long.");

            return stored;
        }
    }
}

using System;
using System.Security.Cryptography;
using System.Text;

namespace pylorak.TinyWall
{
    // DPAPI (CurrentUser) protection for the optional OpenAI API key. The key is stored
    // encrypted at rest in the per-user ControllerConfig; it is never written in plaintext,
    // never logged, and never sent to the LocalSystem service over the pipe.
    internal static class AiExplainKeyProtection
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PromptWall.AiExplain.ApiKey.v1");

        internal static string Protect(string? apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
                return string.Empty;

            byte[] plain = Encoding.UTF8.GetBytes(apiKey);
            try
            {
                byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipher);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        internal static bool TryUnprotect(string? protectedBase64, out string apiKey)
        {
            apiKey = string.Empty;
            if (string.IsNullOrWhiteSpace(protectedBase64))
                return false;

            try
            {
                byte[] cipher = Convert.FromBase64String(protectedBase64!);
                byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    apiKey = Encoding.UTF8.GetString(plain);
                    return apiKey.Length > 0;
                }
                finally
                {
                    Array.Clear(plain, 0, plain.Length);
                }
            }
            catch (FormatException)
            {
                return false;
            }
            catch (CryptographicException)
            {
                // Wrong user, corrupted blob, or entropy mismatch. Treat as "no key".
                return false;
            }
        }
    }
}

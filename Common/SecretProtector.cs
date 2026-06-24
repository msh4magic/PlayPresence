using System;
using System.Security.Cryptography;
using System.Text;
using Playnite.SDK;

namespace PlayPresence.Common
{
    /// <summary>
    /// Protects sensitive strings (the IGDB client secret) at rest using the Windows Data Protection
    /// API (DPAPI) scoped to the current user. The encrypted blob is meaningless on any other machine
    /// or account, so the secret never sits in plain text inside the settings JSON file.
    /// </summary>
    /// <remarks>
    /// DPAPI is available on every supported Windows version where Playnite runs. If protection ever
    /// fails (e.g. an unusual environment) we degrade gracefully rather than crash, logging a warning,
    /// so the user experience is never interrupted.
    /// </remarks>
    public static class SecretProtector
    {
        private const string Prefix = "DPAPI:"; // marks values that are genuinely encrypted
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PlayPresence.v1");
        private static readonly ILogger Logger = LogManager.GetLogger();

        /// <summary>Encrypts <paramref name="plainText"/> for storage. Returns "" for empty input.</summary>
        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                return string.Empty;
            }

            try
            {
                var data = Encoding.UTF8.GetBytes(plainText);
                var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(encrypted);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: secret protection failed; storing value unprotected.");
                return plainText;
            }
        }

        /// <summary>Decrypts a value produced by <see cref="Protect"/>. Plain values pass through.</summary>
        public static string Unprotect(string storedValue)
        {
            if (string.IsNullOrEmpty(storedValue))
            {
                return string.Empty;
            }

            if (!storedValue.StartsWith(Prefix, StringComparison.Ordinal))
            {
                // Not encrypted (legacy or fallback); return as-is.
                return storedValue;
            }

            try
            {
                var payload = storedValue.Substring(Prefix.Length);
                var encrypted = Convert.FromBase64String(payload);
                var data = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: secret unprotection failed; the IGDB secret may need re-entering.");
                return string.Empty;
            }
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Server.Utils
{
    /// <summary>
    /// Password hashing. Changed in roadmap P8 under the user-approved override (2026-10-09) of server.md's
    /// "NEVER change Crypto.cs" rule:
    /// <list type="bullet">
    /// <item>v1 (original, still the default for new hashes): PBKDF2-SHA1, 50 iterations, 24 bytes, stored as a UTF-8
    /// string of the raw bytes. <see cref="HashPassword"/> is unchanged, so every existing hash still verifies.</item>
    /// <item>v2: PBKDF2-SHA256, <see cref="V2Iterations"/> iterations, 32 bytes, stored as "v2$" + Base64 in the same string
    /// field (no DB layout change). Written only when [Security] PasswordHashV2 is on; verified always, so a rollback build
    /// that already contains this code still accepts upgraded accounts.</item>
    /// </list>
    /// v2 is deliberately slow: call <see cref="Verify"/> / <see cref="CreateHash"/> off the game thread wherever v2 may be in use
    /// (Envir.Login already does; see roadmap P8 for the remaining call sites before enabling v2).
    /// </summary>
    class Crypto
    {
        public const int SaltSize = 24;
        public const int HashSize = 24;
        public const int Iterations = 50;

        public const string V2Prefix = "v2$";
        public const int V2Iterations = 100_000;
        private const int V2HashSize = 32;

        public static byte[] GenerateSalt()
        {
            var rng = RandomNumberGenerator.Create();
            byte[] salt = new byte[SaltSize];
            rng.GetBytes(salt);

            return salt;
        }

        /// <summary>The original v1 hash. Unchanged byte for byte: existing stored hashes depend on it.</summary>
        public static string HashPassword(string password, byte[] salt)
        {
            Rfc2898DeriveBytes pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA1);
            return Encoding.UTF8.GetString(pbkdf2.GetBytes(HashSize));
        }

        public static string HashPasswordV2(string password, byte[] salt)
        {
            var bytes = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? string.Empty), salt, V2Iterations, HashAlgorithmName.SHA256, V2HashSize);
            return V2Prefix + Convert.ToBase64String(bytes);
        }

        /// <summary>New hash for a password being set: v2 when [Security] PasswordHashV2 is on, else v1.</summary>
        public static string CreateHash(string password, byte[] salt) =>
            Settings.PasswordHashV2 ? HashPasswordV2(password, salt) : HashPassword(password, salt);

        public static bool IsV2(string stored) => stored != null && stored.StartsWith(V2Prefix, StringComparison.Ordinal);

        /// <summary>True when the stored hash should be rewritten (v1 stored while v2 writing is enabled).</summary>
        public static bool NeedsUpgrade(string stored) => Settings.PasswordHashV2 && !IsV2(stored);

        /// <summary>Checks a password against a stored v1 or v2 hash. Never throws.</summary>
        public static bool Verify(string password, byte[] salt, string stored)
        {
            if (password == null || salt == null || string.IsNullOrEmpty(stored)) return false;
            try
            {
                var computed = IsV2(stored) ? HashPasswordV2(password, salt) : HashPassword(password, salt);
                return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(stored));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using Server.Utils;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>Roadmap P8: v1 hashes are unchanged, v2 verifies, and verification accepts both formats.</summary>
[Collection(PersistenceCollection.Name)] // touches Settings (static ctor reads ./Configs)
public class CryptoTests
{
    [Fact]
    public void V1_hash_is_byte_identical_to_the_original_algorithm()
    {
        var salt = new byte[24]; for (var i = 0; i < salt.Length; i++) salt[i] = (byte)i;
        using var legacy = new Rfc2898DeriveBytes("secret1", salt, 50, HashAlgorithmName.SHA1);
        var expected = Encoding.UTF8.GetString(legacy.GetBytes(24));
        Assert.Equal(expected, Crypto.HashPassword("secret1", salt));
    }

    [Fact]
    public void Verify_accepts_v1_and_v2_and_rejects_wrong_passwords()
    {
        var dir = NewTempFolder("crypto");
        InFolder(dir, () =>
        {
            var salt = Crypto.GenerateSalt();
            var v1 = Crypto.HashPassword("pw12345", salt);
            var v2 = Crypto.HashPasswordV2("pw12345", salt);
            Assert.StartsWith(Crypto.V2Prefix, v2);
            Assert.True(Crypto.Verify("pw12345", salt, v1));
            Assert.True(Crypto.Verify("pw12345", salt, v2));
            Assert.False(Crypto.Verify("pw12346", salt, v1));
            Assert.False(Crypto.Verify("pw12346", salt, v2));
            Assert.False(Crypto.Verify("pw12345", salt, ""));

            var old = Settings.PasswordHashV2;
            try
            {
                Settings.PasswordHashV2 = false;
                Assert.False(Crypto.NeedsUpgrade(v1));
                Assert.False(Crypto.IsV2(Crypto.CreateHash("x", salt)));
                Settings.PasswordHashV2 = true;
                Assert.True(Crypto.NeedsUpgrade(v1));
                Assert.False(Crypto.NeedsUpgrade(v2));
                Assert.True(Crypto.IsV2(Crypto.CreateHash("x", salt)));
            }
            finally { Settings.PasswordHashV2 = old; }
            return 0;
        });
        Directory.Delete(dir, true);
    }
}

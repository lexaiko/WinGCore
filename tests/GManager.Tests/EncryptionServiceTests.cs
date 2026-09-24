using System.Security.Cryptography;
using System.Text;
using GManager.Core;
using Xunit;

namespace GManager.Tests;

public class EncryptionServiceTests
{
    private static EncryptionService CreateService()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return new EncryptionService(key);
    }

    [Fact]
    public void EncryptDecrypt_String_RoundTripsSuccessfully()
    {
        var service = CreateService();
        const string input = "John Doe - Secret Google Account Data 🚀 🔒";

        var encrypted = service.Encrypt(input);
        var decrypted = service.Decrypt(encrypted);

        Assert.NotNull(encrypted);
        Assert.NotEmpty(encrypted);
        Assert.Equal(input, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_EmptyString_ReturnsEmptyOrNull()
    {
        var service = CreateService();

        var encrypted = service.Encrypt("");
        var decrypted = service.Decrypt(encrypted);

        Assert.Empty(encrypted);
        Assert.Null(decrypted);
    }

    [Fact]
    public void Encrypt_SamePlaintext_GeneratesDifferentCiphertextsDueToRandomNonce()
    {
        var service = CreateService();
        const string input = "Constant Plaintext";

        var cipher1 = service.Encrypt(input);
        var cipher2 = service.Encrypt(input);

        Assert.NotEqual(cipher1, cipher2);

        // Both still decrypt to the exact same plaintext
        Assert.Equal(input, service.Decrypt(cipher1));
        Assert.Equal(input, service.Decrypt(cipher2));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsAuthenticationTagMismatchException()
    {
        var service = CreateService();
        const string input = "Super Secret Message";

        var cipher = service.Encrypt(input);

        // Tamper with the last byte of the ciphertext
        cipher[^1] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => service.Decrypt(cipher));
    }

    [Fact]
    public void Decrypt_TamperedAuthTag_ThrowsAuthenticationTagMismatchException()
    {
        var service = CreateService();
        const string input = "Super Secret Message";

        var cipher = service.Encrypt(input);

        // Tag is located at bytes 12..27
        cipher[15] ^= 0x01;

        Assert.Throws<AuthenticationTagMismatchException>(() => service.Decrypt(cipher));
    }

    [Fact]
    public void EncryptDecrypt_ByteArrays_RoundTripsSuccessfully()
    {
        var service = CreateService();
        var data = Encoding.UTF8.GetBytes("Binary Data Stream Test");

        var encrypted = service.EncryptBytes(data);
        var decrypted = service.DecryptBytes(encrypted);

        Assert.NotNull(decrypted);
        Assert.Equal(data, decrypted);
    }
}

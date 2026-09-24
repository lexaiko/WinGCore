using System.Security.Cryptography;
using System.Text;

namespace GManager.Core;

/// <summary>
/// Authenticated column encryption service using AES-256-GCM.
/// Formats ciphertext as: [Nonce (12B)] [Tag (16B)] [Ciphertext (NB)]
/// </summary>
public sealed class EncryptionService : IEncryptionService
{
    private const string MasterKeyCredentialName = "_db_master_key";
    private const int NonceSize = 12; // 96 bits for GCM
    private const int TagSize = 16;   // 128 bits
    private const int HeaderSize = NonceSize + TagSize; // 28 bytes
    private const int KeySize = 32;   // 256 bits for AES-256

    private readonly byte[] _key;

    /// <summary>
    /// Constructs encryption service retrieving or generating a master key in credential storage.
    /// </summary>
    public EncryptionService(ICredentialStore credentialStore)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);

        var existingKeyBase64 = credentialStore.GetCredential(MasterKeyCredentialName);
        if (!string.IsNullOrEmpty(existingKeyBase64))
        {
            try
            {
                var decoded = Convert.FromBase64String(existingKeyBase64);
                if (decoded.Length == KeySize)
                {
                    _key = decoded;
                    return;
                }
            }
            catch
            {
                // Fallthrough to generate new key if corrupt
            }
        }

        _key = RandomNumberGenerator.GetBytes(KeySize);
        credentialStore.SetCredential(MasterKeyCredentialName, Convert.ToBase64String(_key));
    }

    /// <summary>
    /// Constructs encryption service directly with a specific 32-byte key (primarily for unit tests).
    /// </summary>
    public EncryptionService(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"AES-256 key must be exactly {KeySize} bytes.", nameof(key));
        }
        _key = (byte[])key.Clone();
    }

    public byte[] Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return [];
        }
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        return EncryptBytes(plainBytes);
    }

    public string? Decrypt(byte[]? encryptedBytes)
    {
        if (encryptedBytes == null || encryptedBytes.Length == 0)
        {
            return null;
        }
        var decryptedBytes = DecryptBytes(encryptedBytes);
        return decryptedBytes != null ? Encoding.UTF8.GetString(decryptedBytes) : null;
    }

    public byte[] EncryptBytes(byte[]? plainBytes)
    {
        if (plainBytes == null || plainBytes.Length == 0)
        {
            return [];
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, ciphertext, tag);
        }

        var result = new byte[HeaderSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, HeaderSize, ciphertext.Length);

        return result;
    }

    public byte[]? DecryptBytes(byte[]? encryptedBytes)
    {
        if (encryptedBytes == null || encryptedBytes.Length < HeaderSize)
        {
            return null;
        }

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipherLength = encryptedBytes.Length - HeaderSize;
        var ciphertext = new byte[cipherLength];
        var plaintext = new byte[cipherLength];

        Buffer.BlockCopy(encryptedBytes, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(encryptedBytes, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(encryptedBytes, HeaderSize, ciphertext, 0, cipherLength);

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return plaintext;
    }
}

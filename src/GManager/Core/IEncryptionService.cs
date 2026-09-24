namespace GManager.Core;

/// <summary>
/// Abstraction for database column-level encryption using AES-256-GCM authenticated encryption.
/// </summary>
public interface IEncryptionService
{
    byte[] Encrypt(string? plaintext);
    string? Decrypt(byte[]? encryptedBytes);
    byte[] EncryptBytes(byte[]? plainBytes);
    byte[]? DecryptBytes(byte[]? encryptedBytes);
}

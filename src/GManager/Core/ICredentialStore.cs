namespace GManager.Core;

/// <summary>
/// Abstraction for storing secrets (OAuth refresh tokens and encryption master keys)
/// backed by the operating system credential manager.
/// </summary>
public interface ICredentialStore
{
    void SetCredential(string key, string secret);
    string? GetCredential(string key);
    bool RemoveCredential(string key);
}

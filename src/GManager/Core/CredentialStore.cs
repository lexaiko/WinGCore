using Windows.Security.Credentials;

namespace GManager.Core;

/// <summary>
/// Credential storage backed by Windows Credential Manager (PasswordVault).
/// Secrets are encrypted with Windows DPAPI bound to the user's Windows SID.
/// </summary>
public sealed class CredentialStore : ICredentialStore
{
    private readonly PasswordVault _vault = new();
    private readonly string _resource;

    public CredentialStore(string resource = AppConfig.CredentialResource)
    {
        _resource = resource;
    }

    public void SetCredential(string key, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(secret);

        // PasswordVault throws an exception if adding a duplicate (Resource, UserName)
        RemoveCredential(key);

        var credential = new PasswordCredential(_resource, key, secret);
        _vault.Add(credential);
    }

    public string? GetCredential(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var credential = _vault.Retrieve(_resource, key);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch
        {
            // Throws COMException (0x80070490 - Element not found) if not present
            return null;
        }
    }

    public bool RemoveCredential(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var credential = _vault.Retrieve(_resource, key);
            _vault.Remove(credential);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

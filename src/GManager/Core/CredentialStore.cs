using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GManager.Core;

/// <summary>
/// Secure credential storage backed by Windows DPAPI (Data Protection API) bound to the current user's SID.
/// Thread-safe, avoids WinRT PasswordVault COM/RPC deadlocks in unpackaged desktop environments.
/// </summary>
public sealed class CredentialStore : ICredentialStore
{
    private readonly string _filePath;
    private readonly byte[] _entropy;
    private readonly object _lock = new();
    private Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    public CredentialStore(string resource = AppConfig.CredentialResource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        var safeName = string.Join("_", resource.Split(Path.GetInvalidFileNameChars()));
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GManager");
        if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);

        _filePath = Path.Combine(appData, $"{safeName}.vault");
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes(resource));

        Load();
    }

    public void SetCredential(string key, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(secret);

        lock (_lock)
        {
            _cache[key] = secret;
            Save();
        }
    }

    public string? GetCredential(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var val))
            {
                return val;
            }

            // Fallback: Check legacy Windows PasswordVault with a short timeout to prevent RPC deadlocks
            var legacy = TryReadLegacyPasswordVault(key);
            if (legacy != null)
            {
                _cache[key] = legacy;
                Save();
                return legacy;
            }

            return null;
        }
    }

    public bool RemoveCredential(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_lock)
        {
            var removed = _cache.Remove(key);
            if (removed)
            {
                Save();
            }

            TryRemoveLegacyPasswordVault(key);
            return removed;
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                _cache = new(StringComparer.Ordinal);
                return;
            }

            try
            {
                var encrypted = File.ReadAllBytes(_filePath);
                if (encrypted.Length == 0)
                {
                    _cache = new(StringComparer.Ordinal);
                    return;
                }

                var decrypted = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(decrypted);
                _cache = map != null ? new(map, StringComparer.Ordinal) : new(StringComparer.Ordinal);
            }
            catch
            {
                _cache = new(StringComparer.Ordinal);
            }
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(_cache);
            var encrypted = ProtectedData.Protect(json, _entropy, DataProtectionScope.CurrentUser);
            var temp = _filePath + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, _filePath, overwrite: true);
        }
        catch
        {
            // Best effort file write
        }
    }

    private string? TryReadLegacyPasswordVault(string key)
    {
        try
        {
            var task = Task.Run(() =>
            {
                try
                {
                    var vault = new Windows.Security.Credentials.PasswordVault();
                    var cred = vault.Retrieve(Path.GetFileNameWithoutExtension(_filePath), key);
                    cred.RetrievePassword();
                    return cred.Password;
                }
                catch
                {
                    return null;
                }
            });

            if (task.Wait(TimeSpan.FromMilliseconds(200)))
            {
                return task.Result;
            }
        }
        catch { }

        return null;
    }

    private void TryRemoveLegacyPasswordVault(string key)
    {
        try
        {
            Task.Run(() =>
            {
                try
                {
                    var vault = new Windows.Security.Credentials.PasswordVault();
                    var cred = vault.Retrieve(Path.GetFileNameWithoutExtension(_filePath), key);
                    vault.Remove(cred);
                }
                catch { }
            }).Wait(TimeSpan.FromMilliseconds(200));
        }
        catch { }
    }
}

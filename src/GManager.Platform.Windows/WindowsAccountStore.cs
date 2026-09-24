using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GManager.Contracts;
using GManager.Runtime;

namespace GManager.Platform.Windows;

public sealed partial class WindowsDeviceStore
{
    public NativeSessionSummary SaveSession(Guid deviceId, NativeCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.Email) || string.IsNullOrWhiteSpace(credential.MasterToken))
            throw new ArgumentException("Account session requires verified email and credential.");
        _ = Get(deviceId);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var accountKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.Email.Trim().ToLowerInvariant())));
        command.CommandText = "SELECT id FROM native_sessions WHERE device_id=$device AND account_key=$key";
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$key", accountKey);
        var id = command.ExecuteScalar() is string existing ? Guid.Parse(existing) : Guid.NewGuid();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credential);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plaintext, SessionEntropy(id), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        command.CommandText = """
            INSERT INTO native_sessions(id,device_id,account_key,credential,status) VALUES($id,$device,$key,$credential,'Active')
            ON CONFLICT(device_id,account_key) DO UPDATE SET credential=excluded.credential,status='Active',last_error=NULL
            """;
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.Parameters.AddWithValue("$credential", encrypted);
        command.ExecuteNonQuery();
        transaction.Commit();
        return GetSession(id).Summary;
    }

    public NativeSession GetSession(Guid sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id,credential,status,last_error FROM native_sessions WHERE id=$id";
        command.Parameters.AddWithValue("$id", sessionId.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException();
        var plaintext = ProtectedData.Unprotect((byte[])reader["credential"], SessionEntropy(sessionId), DataProtectionScope.CurrentUser);
        NativeCredential credential;
        try { credential = JsonSerializer.Deserialize<NativeCredential>(plaintext) ?? throw new InvalidDataException("Invalid native credential."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        return new(new(sessionId, Guid.Parse(reader.GetString(0)), credential.Email, credential.AccountId,
            credential.DisplayName, Enum.Parse<NativeSessionStatus>(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3)), credential);
    }

    public NativeSessionSummary[] ListSessions()
    {
        var ids = new List<Guid>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM native_sessions ORDER BY rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        }
        return ids.Select(x => GetSession(x).Summary).ToArray();
    }

    public void SetSessionStatus(Guid sessionId, NativeSessionStatus status, string? error)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE native_sessions SET status=$status,last_error=$error WHERE id=$id";
        command.Parameters.AddWithValue("$id", sessionId.ToString("N"));
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException();
    }

    public void DeleteSession(Guid sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM native_sessions WHERE id=$id";
        command.Parameters.AddWithValue("$id", sessionId.ToString("N"));
        command.ExecuteNonQuery();
    }

    private static byte[] SessionEntropy(Guid id) => Encoding.UTF8.GetBytes($"GManager.AndroidGoogle.Session.v1/{id:N}");
}

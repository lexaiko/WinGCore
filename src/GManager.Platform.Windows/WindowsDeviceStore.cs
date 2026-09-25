using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GManager.Contracts;
using GManager.Runtime;
using Microsoft.Data.Sqlite;

namespace GManager.Platform.Windows;

public sealed partial class WindowsDeviceStore : IDeviceStore, INativeAccountStore
{
    private readonly string _connectionString;

    public WindowsDeviceStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 2) throw new InvalidOperationException("Runtime database is newer than this application.");
        if (version == 0)
        {
            command.CommandText = """
                CREATE TABLE devices (
                    id TEXT PRIMARY KEY,
                    profile TEXT NOT NULL,
                    logging_id INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    registration BLOB,
                    google_id TEXT,
                    last_accepted_at TEXT,
                    last_outcome TEXT NOT NULL,
                    last_error TEXT
                );
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }
        if (version < 2)
        {
            command.CommandText = """
                CREATE TABLE native_sessions (
                    id TEXT PRIMARY KEY,
                    device_id TEXT NOT NULL REFERENCES devices(id),
                    account_key TEXT NOT NULL,
                    credential BLOB NOT NULL,
                    status TEXT NOT NULL,
                    last_error TEXT,
                    UNIQUE(device_id, account_key)
                );
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON";
        command.ExecuteNonQuery();
        return connection;
    }

    public DeviceSummary Create(DeviceProfile profile)
    {
        profile.Validate();
        var id = Guid.NewGuid();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO devices(id,profile,logging_id,created_at,last_outcome)
            VALUES($id,$profile,$logging,$created,$outcome)
            """;
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.Parameters.AddWithValue("$profile", JsonSerializer.Serialize(profile, RuntimeProtocol.Json));
        command.Parameters.AddWithValue("$logging", BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)));
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$outcome", CheckinOutcome.NeverAttempted.ToString());
        command.ExecuteNonQuery();
        return Get(id).Summary;
    }

    public DeviceSummary[] List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM devices ORDER BY created_at, id LIMIT 100";
        using var reader = command.ExecuteReader();
        var results = new List<DeviceSummary>();
        while (reader.Read()) results.Add(ReadSummary(reader));
        return results.ToArray();
    }

    public DeviceState Get(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM devices WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException();
        Registration? registration = null;
        if (!reader.IsDBNull(reader.GetOrdinal("registration")))
        {
            var plaintext = ProtectedData.Unprotect((byte[])reader["registration"], Entropy(id), DataProtectionScope.CurrentUser);
            try
            {
                registration = JsonSerializer.Deserialize<Registration>(plaintext) ?? throw new InvalidDataException("Invalid registration data.");
                if (registration.AndroidId == 0 || registration.SecurityToken == 0)
                    throw new InvalidDataException("Invalid registration credentials.");
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        return new(ReadSummary(reader), reader.GetInt64(reader.GetOrdinal("logging_id")), registration);
    }

    public void SaveResult(Guid id, CheckinResult result)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (result.Outcome == CheckinOutcome.Accepted)
        {
            var registration = result.Registration;
            if (registration is null || registration.AndroidId == 0 || registration.SecurityToken == 0)
                throw new ArgumentException("Accepted check-in requires registration credentials.");
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(registration);
            byte[] ciphertext;
            try { ciphertext = ProtectedData.Protect(plaintext, Entropy(id), DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            command.CommandText = """
                UPDATE devices SET registration=$registration, google_id=$google,
                    last_accepted_at=$accepted, last_outcome=$outcome, last_error=NULL WHERE id=$id
                """;
            command.Parameters.AddWithValue("$registration", ciphertext);
            command.Parameters.AddWithValue("$google", registration.AndroidId.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$accepted", DateTimeOffset.UtcNow.ToString("O"));
        }
        else
        {
            command.CommandText = "UPDATE devices SET last_outcome=$outcome,last_error=$error WHERE id=$id";
            command.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
        }
        command.Parameters.AddWithValue("$outcome", result.Outcome.ToString());
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException();
    }

    public void Delete(Guid id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM native_sessions WHERE device_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.ExecuteNonQuery();

        command.CommandText = "DELETE FROM devices WHERE id=$id";
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException();
        transaction.Commit();
    }

    private static byte[] Entropy(Guid id) => Encoding.UTF8.GetBytes($"GManager.AndroidGoogle.Registration.v1/{id:N}");

    private static DeviceSummary ReadSummary(SqliteDataReader reader)
    {
        string? Optional(string column) => reader[column] is DBNull ? null : (string)reader[column];
        var accepted = Optional("last_accepted_at");
        var googleId = Optional("google_id");
        return new(Guid.Parse((string)reader["id"]),
            JsonSerializer.Deserialize<DeviceProfile>((string)reader["profile"], RuntimeProtocol.Json)!,
            DateTimeOffset.Parse((string)reader["created_at"], CultureInfo.InvariantCulture),
            googleId is not null, googleId,
            accepted is null ? null : DateTimeOffset.Parse(accepted, CultureInfo.InvariantCulture),
            Enum.Parse<CheckinOutcome>((string)reader["last_outcome"]), Optional("last_error"));
    }
}

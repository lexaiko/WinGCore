using System.Data;
using System.IO;
using GManager.Models;
using Microsoft.Data.Sqlite;

namespace GManager.Core;

/// <summary>
/// SQLite database manager and repository with automatic schema migrations and column-level encryption.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly string _connectionString;
    private readonly IEncryptionService _encryption;

    public Database(string dbPath, IEncryptionService encryption)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(encryption);

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        _encryption = encryption;
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Executes all pending migrations up to the latest version.
    /// </summary>
    public void Initialize()
    {
        using var connection = CreateConnection();
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys=OFF";
            foreignKeys.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();

        // 1. Ensure migrations tracking table exists
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );";
            cmd.ExecuteNonQuery();
        }

        // 2. Check current version
        int currentVersion = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
            {
                currentVersion = Convert.ToInt32(result);
            }
        }

        // Migration V1: Initial Schema
        if (currentVersion < 1)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS accounts (
                        id TEXT PRIMARY KEY,
                        email TEXT NOT NULL UNIQUE,
                        display_name BLOB,
                        given_name BLOB,
                        family_name BLOB,
                        avatar_url BLOB,
                        avatar_local_path TEXT,
                        state INTEGER NOT NULL DEFAULT 0,
                        unread_count INTEGER NOT NULL DEFAULT 0,
                        storage_used INTEGER NOT NULL DEFAULT 0,
                        storage_total INTEGER NOT NULL DEFAULT 0,
                        last_synced_at TEXT,
                        created_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS cached_messages (
                        id TEXT PRIMARY KEY,
                        account_id TEXT NOT NULL,
                        thread_id TEXT NOT NULL,
                        sender_name BLOB,
                        sender_email BLOB,
                        subject BLOB,
                        snippet BLOB,
                        received_at TEXT NOT NULL,
                        is_unread INTEGER NOT NULL DEFAULT 1,
                        is_starred INTEGER NOT NULL DEFAULT 0,
                        FOREIGN KEY (account_id) REFERENCES accounts (id) ON DELETE CASCADE
                    );

                    CREATE INDEX IF NOT EXISTS idx_messages_account ON cached_messages(account_id);
                    CREATE INDEX IF NOT EXISTS idx_messages_received ON cached_messages(account_id, received_at DESC);
                    CREATE INDEX IF NOT EXISTS idx_messages_unread ON cached_messages(account_id, is_unread);

                    INSERT INTO schema_migrations (version, applied_at)
                    VALUES (1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));
                ";
                cmd.ExecuteNonQuery();
            }
        }

        if (currentVersion > 2) throw new InvalidOperationException("Database schema is newer than this application.");
        if (currentVersion < 2)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE accounts_v2 (
                    id TEXT PRIMARY KEY, email TEXT NOT NULL, display_name BLOB, given_name BLOB,
                    family_name BLOB, avatar_url BLOB, avatar_local_path TEXT,
                    state INTEGER NOT NULL DEFAULT 0, unread_count INTEGER NOT NULL DEFAULT 0,
                    storage_used INTEGER NOT NULL DEFAULT 0, storage_total INTEGER NOT NULL DEFAULT 0,
                    last_synced_at TEXT, created_at TEXT NOT NULL
                );
                INSERT INTO accounts_v2 SELECT * FROM accounts;
                DROP TABLE accounts;
                ALTER TABLE accounts_v2 RENAME TO accounts;
                CREATE INDEX idx_accounts_email ON accounts(email);
                CREATE TABLE cached_messages_v2 (
                    id TEXT NOT NULL, account_id TEXT NOT NULL, thread_id TEXT NOT NULL,
                    sender_name BLOB, sender_email BLOB, subject BLOB, snippet BLOB,
                    received_at TEXT NOT NULL, is_unread INTEGER NOT NULL DEFAULT 1,
                    is_starred INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(account_id,id),
                    FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE CASCADE
                );
                INSERT INTO cached_messages_v2 SELECT * FROM cached_messages;
                DROP TABLE cached_messages;
                ALTER TABLE cached_messages_v2 RENAME TO cached_messages;
                CREATE INDEX idx_messages_account ON cached_messages(account_id);
                CREATE INDEX idx_messages_received ON cached_messages(account_id,received_at DESC);
                CREATE INDEX idx_messages_unread ON cached_messages(account_id,is_unread);
                INSERT INTO schema_migrations VALUES(2,strftime('%Y-%m-%dT%H:%M:%SZ','now'));
                """;
            migration.ExecuteNonQuery();
        }
        using (var integrity = connection.CreateCommand())
        {
            integrity.Transaction = transaction;
            integrity.CommandText = "PRAGMA foreign_key_check";
            using var violations = integrity.ExecuteReader();
            if (violations.Read()) throw new InvalidOperationException("Database migration would leave orphaned messages.");
        }
        transaction.Commit();
        using var enableKeys = connection.CreateCommand();
        enableKeys.CommandText = "PRAGMA foreign_keys=ON";
        enableKeys.ExecuteNonQuery();
    }

    #region Accounts Repository

    public async Task<List<GoogleAccount>> GetAllAccountsAsync()
    {
        var list = new List<GoogleAccount>();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT id, email, display_name, given_name, family_name,
                   avatar_url, avatar_local_path, state, unread_count,
                   storage_used, storage_total, last_synced_at, created_at
            FROM accounts
            ORDER BY created_at ASC;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadAccount(reader));
        }

        return list;
    }

    public async Task<GoogleAccount?> GetAccountByIdAsync(string id)
    {
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT id, email, display_name, given_name, family_name,
                   avatar_url, avatar_local_path, state, unread_count,
                   storage_used, storage_total, last_synced_at, created_at
            FROM accounts
            WHERE id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAccount(reader) : null;
    }

    public async Task<GoogleAccount?> GetAccountByEmailAsync(string email)
    {
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT id, email, display_name, given_name, family_name,
                   avatar_url, avatar_local_path, state, unread_count,
                   storage_used, storage_total, last_synced_at, created_at
            FROM accounts
            WHERE LOWER(email) = LOWER(@email) LIMIT 1;";
        cmd.Parameters.AddWithValue("@email", email);

        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAccount(reader) : null;
    }

    public async Task UpsertAccountAsync(GoogleAccount account)
    {
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO accounts (
                id, email, display_name, given_name, family_name,
                avatar_url, avatar_local_path, state, unread_count,
                storage_used, storage_total, last_synced_at, created_at
            )
            VALUES (
                @id, @email, @display_name, @given_name, @family_name,
                @avatar_url, @avatar_local_path, @state, @unread_count,
                @storage_used, @storage_total, @last_synced_at, @created_at
            )
            ON CONFLICT(id) DO UPDATE SET
                email = excluded.email,
                display_name = excluded.display_name,
                given_name = excluded.given_name,
                family_name = excluded.family_name,
                avatar_url = excluded.avatar_url,
                avatar_local_path = COALESCE(excluded.avatar_local_path, accounts.avatar_local_path),
                state = excluded.state,
                unread_count = excluded.unread_count,
                storage_used = excluded.storage_used,
                storage_total = excluded.storage_total,
                last_synced_at = excluded.last_synced_at;";

        cmd.Parameters.AddWithValue("@id", account.Id);
        cmd.Parameters.AddWithValue("@email", account.Email);
        cmd.Parameters.AddWithValue("@display_name", (object?)_encryption.Encrypt(account.DisplayName) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@given_name", (object?)_encryption.Encrypt(account.GivenName) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@family_name", (object?)_encryption.Encrypt(account.FamilyName) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@avatar_url", (object?)_encryption.Encrypt(account.AvatarUrl) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@avatar_local_path", (object?)account.AvatarLocalPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@state", (int)account.State);
        cmd.Parameters.AddWithValue("@unread_count", account.UnreadCount);
        cmd.Parameters.AddWithValue("@storage_used", account.DriveUsedBytes);
        cmd.Parameters.AddWithValue("@storage_total", account.DriveTotalBytes);
        cmd.Parameters.AddWithValue("@last_synced_at", account.LastSyncedAt?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", account.CreatedAt.ToString("o"));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateAccountStateAsync(string id, AccountState state)
    {
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET state = @state WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@state", (int)state);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateAccountSyncStatsAsync(string id, int unreadCount, long usedBytes, long totalBytes, DateTimeOffset syncedAt)
    {
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE accounts
            SET unread_count = @unread,
                storage_used = @used,
                storage_total = @total,
                last_synced_at = @synced
            WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@unread", unreadCount);
        cmd.Parameters.AddWithValue("@used", usedBytes);
        cmd.Parameters.AddWithValue("@total", totalBytes);
        cmd.Parameters.AddWithValue("@synced", syncedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAccountAsync(string id)
    {
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM accounts WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private GoogleAccount ReadAccount(SqliteDataReader reader)
    {
        var account = new GoogleAccount
        {
            Id = reader.GetString(0),
            Email = reader.GetString(1)
        };

        if (!reader.IsDBNull(2))
            account.DisplayName = _encryption.Decrypt((byte[])reader[2]) ?? string.Empty;
        if (!reader.IsDBNull(3))
            account.GivenName = _encryption.Decrypt((byte[])reader[3]) ?? string.Empty;
        if (!reader.IsDBNull(4))
            account.FamilyName = _encryption.Decrypt((byte[])reader[4]) ?? string.Empty;
        if (!reader.IsDBNull(5))
            account.AvatarUrl = _encryption.Decrypt((byte[])reader[5]);
        if (!reader.IsDBNull(6))
            account.AvatarLocalPath = reader.GetString(6);

        account.State = (AccountState)reader.GetInt32(7);
        account.UnreadCount = reader.GetInt32(8);
        account.DriveUsedBytes = reader.GetInt64(9);
        account.DriveTotalBytes = reader.GetInt64(10);

        if (!reader.IsDBNull(11) && DateTimeOffset.TryParse(reader.GetString(11), out var synced))
            account.LastSyncedAt = synced;

        if (DateTimeOffset.TryParse(reader.GetString(12), out var created))
            account.CreatedAt = created;

        return account;
    }

    #endregion

    #region Cached Messages Repository

    public async Task<List<MailMessage>> GetCachedMessagesAsync(string accountId, int limit = 50)
    {
        var list = new List<MailMessage>();
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT id, account_id, thread_id, sender_name, sender_email,
                   subject, snippet, received_at, is_unread, is_starred
            FROM cached_messages
            WHERE account_id = @account_id
            ORDER BY received_at DESC
            LIMIT @limit;";
        cmd.Parameters.AddWithValue("@account_id", accountId);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadMessage(reader));
        }

        return list;
    }

    public async Task UpsertCachedMessagesAsync(IEnumerable<MailMessage> messages)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var msg in messages)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO cached_messages (
                    id, account_id, thread_id, sender_name, sender_email,
                    subject, snippet, received_at, is_unread, is_starred
                )
                VALUES (
                    @id, @account_id, @thread_id, @sender_name, @sender_email,
                    @subject, @snippet, @received_at, @is_unread, @is_starred
                )
                ON CONFLICT(account_id,id) DO UPDATE SET
                    sender_name = excluded.sender_name,
                    sender_email = excluded.sender_email,
                    subject = excluded.subject,
                    snippet = excluded.snippet,
                    received_at = excluded.received_at,
                    is_unread = excluded.is_unread,
                    is_starred = excluded.is_starred;";

            cmd.Parameters.AddWithValue("@id", msg.Id);
            cmd.Parameters.AddWithValue("@account_id", msg.AccountId);
            cmd.Parameters.AddWithValue("@thread_id", msg.ThreadId);
            cmd.Parameters.AddWithValue("@sender_name", (object?)_encryption.Encrypt(msg.SenderName) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sender_email", (object?)_encryption.Encrypt(msg.SenderEmail) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@subject", (object?)_encryption.Encrypt(msg.Subject) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@snippet", (object?)_encryption.Encrypt(msg.Snippet) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@received_at", msg.InternalDate.ToString("o"));
            cmd.Parameters.AddWithValue("@is_unread", msg.IsUnread ? 1 : 0);
            cmd.Parameters.AddWithValue("@is_starred", msg.IsStarred ? 1 : 0);

            await cmd.ExecuteNonQueryAsync();
        }

        transaction.Commit();
    }

    public async Task MarkMessageReadAsync(string accountId, string messageId, bool isUnread = false)
    {
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE cached_messages SET is_unread = @is_unread WHERE account_id=@account AND id = @id;";
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@id", messageId);
        cmd.Parameters.AddWithValue("@is_unread", isUnread ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteMessagesForAccountAsync(string accountId)
    {
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM cached_messages WHERE account_id = @account_id;";
        cmd.Parameters.AddWithValue("@account_id", accountId);
        await cmd.ExecuteNonQueryAsync();
    }

    private MailMessage ReadMessage(SqliteDataReader reader)
    {
        var msg = new MailMessage
        {
            Id = reader.GetString(0),
            AccountId = reader.GetString(1),
            ThreadId = reader.GetString(2)
        };

        if (!reader.IsDBNull(3))
            msg.SenderName = _encryption.Decrypt((byte[])reader[3]) ?? string.Empty;
        if (!reader.IsDBNull(4))
            msg.SenderEmail = _encryption.Decrypt((byte[])reader[4]) ?? string.Empty;
        if (!reader.IsDBNull(5))
            msg.Subject = _encryption.Decrypt((byte[])reader[5]) ?? string.Empty;
        if (!reader.IsDBNull(6))
            msg.Snippet = _encryption.Decrypt((byte[])reader[6]) ?? string.Empty;

        if (DateTimeOffset.TryParse(reader.GetString(7), out var received))
            msg.InternalDate = received;

        msg.IsUnread = reader.GetInt32(8) == 1;
        msg.IsStarred = reader.GetInt32(9) == 1;

        return msg;
    }

    #endregion

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}

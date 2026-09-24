using System.IO;
using System.Security.Cryptography;
using GManager.Core;
using GManager.Models;
using Xunit;

namespace GManager.Tests;

public class DatabaseTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly EncryptionService _encryption;
    private readonly Database _database;

    public DatabaseTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"gmanager_test_{Guid.NewGuid():N}.db");
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        _encryption = new EncryptionService(key);
        _database = new Database(_tempDbPath, _encryption);
        _database.Initialize();
    }

    [Fact]
    public async Task Initialize_CreatesTablesAndSchemaMigration()
    {
        // Re-initializing should be idempotent
        _database.Initialize();

        var accounts = await _database.GetAllAccountsAsync();
        Assert.Empty(accounts);
    }

    [Fact]
    public async Task NativeSessionsWithSameEmailKeepMessagesIsolated()
    {
        await _database.UpsertAccountAsync(new GoogleAccount { Id = "device-one", Email = "same@example.com" });
        await _database.UpsertAccountAsync(new GoogleAccount { Id = "device-two", Email = "same@example.com" });
        await _database.UpsertCachedMessagesAsync([new MailMessage { Id = "shared-message", AccountId = "device-one", Subject = "First", IsUnread = true }]);
        await _database.UpsertCachedMessagesAsync([new MailMessage { Id = "shared-message", AccountId = "device-two", Subject = "Second", IsUnread = true }]);
        await _database.MarkMessageReadAsync("device-one", "shared-message");
        Assert.False(Assert.Single(await _database.GetCachedMessagesAsync("device-one")).IsUnread);
        Assert.True(Assert.Single(await _database.GetCachedMessagesAsync("device-two")).IsUnread);
        await _database.DeleteAccountAsync("device-one");
        Assert.Equal("Second", Assert.Single(await _database.GetCachedMessagesAsync("device-two")).Subject);
    }

    [Fact]
    public async Task V1MigrationPreservesEncryptedAccountsAndMessages()
    {
        var path = _tempDbPath + ".v1";
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY,applied_at TEXT NOT NULL);
                    INSERT INTO schema_migrations VALUES(1,'2026-01-01');
                    CREATE TABLE accounts(id TEXT PRIMARY KEY,email TEXT NOT NULL UNIQUE,display_name BLOB,
                        given_name BLOB,family_name BLOB,avatar_url BLOB,avatar_local_path TEXT,
                        state INTEGER NOT NULL DEFAULT 0,unread_count INTEGER NOT NULL DEFAULT 0,
                        storage_used INTEGER NOT NULL DEFAULT 0,storage_total INTEGER NOT NULL DEFAULT 0,
                        last_synced_at TEXT,created_at TEXT NOT NULL);
                    CREATE TABLE cached_messages(id TEXT PRIMARY KEY,account_id TEXT NOT NULL,
                        thread_id TEXT NOT NULL,sender_name BLOB,sender_email BLOB,subject BLOB,snippet BLOB,
                        received_at TEXT NOT NULL,is_unread INTEGER NOT NULL DEFAULT 1,is_starred INTEGER NOT NULL DEFAULT 0,
                        FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE CASCADE);
                    INSERT INTO accounts(id,email,display_name,created_at) VALUES('legacy','same@example.com',$name,'2026-01-01');
                    INSERT INTO cached_messages(id,account_id,thread_id,subject,received_at) VALUES('message','legacy','thread',$subject,'2026-01-01');
                    """;
                command.Parameters.AddWithValue("$name", _encryption.Encrypt("Existing user"));
                command.Parameters.AddWithValue("$subject", _encryption.Encrypt("Existing message"));
                command.ExecuteNonQuery();
            }
            using var migrated = new Database(path, _encryption);
            migrated.Initialize();
            migrated.Initialize();
            Assert.Equal("Existing user", (await migrated.GetAccountByIdAsync("legacy"))!.DisplayName);
            Assert.Equal("Existing message", Assert.Single(await migrated.GetCachedMessagesAsync("legacy")).Subject);
            await migrated.UpsertAccountAsync(new GoogleAccount { Id = "native-session", Email = "same@example.com" });
            Assert.Equal(2, (await migrated.GetAllAccountsAsync()).Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task UpsertAndGetAccount_EncryptedColumnsDecryptedProperly()
    {
        var account = new GoogleAccount
        {
            Id = "google_sub_12345",
            Email = "user@gmail.com",
            DisplayName = "John Doe",
            GivenName = "John",
            FamilyName = "Doe",
            AvatarUrl = "https://lh3.googleusercontent.com/avatar123",
            AvatarLocalPath = @"C:\AppData\avatar.png",
            State = AccountState.Active,
            UnreadCount = 42,
            DriveUsedBytes = 5_000_000_000,
            DriveTotalBytes = 15_000_000_000,
            LastSyncedAt = DateTimeOffset.UtcNow
        };

        await _database.UpsertAccountAsync(account);

        var retrieved = await _database.GetAccountByIdAsync("google_sub_12345");
        Assert.NotNull(retrieved);
        Assert.Equal("google_sub_12345", retrieved.Id);
        Assert.Equal("user@gmail.com", retrieved.Email);
        Assert.Equal("John Doe", retrieved.DisplayName);
        Assert.Equal("John", retrieved.GivenName);
        Assert.Equal("Doe", retrieved.FamilyName);
        Assert.Equal("https://lh3.googleusercontent.com/avatar123", retrieved.AvatarUrl);
        Assert.Equal(42, retrieved.UnreadCount);
        Assert.Equal(5_000_000_000, retrieved.DriveUsedBytes);
        Assert.Equal(AccountState.Active, retrieved.State);
    }

    [Fact]
    public async Task UpdateAccountState_ChangesStateInDatabase()
    {
        var account = new GoogleAccount
        {
            Id = "sub_state_test",
            Email = "state@example.com",
            DisplayName = "State Tester",
            State = AccountState.Active
        };

        await _database.UpsertAccountAsync(account);
        await _database.UpdateAccountStateAsync("sub_state_test", AccountState.ActionNeeded);

        var updated = await _database.GetAccountByIdAsync("sub_state_test");
        Assert.NotNull(updated);
        Assert.Equal(AccountState.ActionNeeded, updated.State);
    }

    [Fact]
    public async Task CascadeDelete_DeletingAccountRemovesAssociatedMessages()
    {
        var account = new GoogleAccount
        {
            Id = "sub_cascade",
            Email = "cascade@example.com",
            DisplayName = "Cascade User"
        };
        await _database.UpsertAccountAsync(account);

        var messages = new List<MailMessage>
        {
            new()
            {
                Id = "msg_001",
                AccountId = "sub_cascade",
                ThreadId = "th_001",
                SenderName = "Security Alert",
                SenderEmail = "no-reply@google.com",
                Subject = "New login detected",
                Snippet = "Someone logged into your account...",
                IsUnread = true,
                InternalDate = DateTimeOffset.UtcNow
            }
        };

        await _database.UpsertCachedMessagesAsync(messages);

        var cachedBefore = await _database.GetCachedMessagesAsync("sub_cascade");
        Assert.Single(cachedBefore);

        // Delete account
        await _database.DeleteAccountAsync("sub_cascade");

        var cachedAfter = await _database.GetCachedMessagesAsync("sub_cascade");
        Assert.Empty(cachedAfter);
    }

    [Fact]
    public async Task CachedMessages_OrderedByReceivedAtDesc()
    {
        var account = new GoogleAccount
        {
            Id = "sub_ordering",
            Email = "order@example.com",
            DisplayName = "Order User"
        };
        await _database.UpsertAccountAsync(account);

        var now = DateTimeOffset.UtcNow;
        var messages = new List<MailMessage>
        {
            new()
            {
                Id = "msg_old",
                AccountId = "sub_ordering",
                ThreadId = "th_1",
                SenderName = "Alice",
                SenderEmail = "alice@example.com",
                Subject = "Older Email",
                Snippet = "Old snippet",
                InternalDate = now.AddHours(-2)
            },
            new()
            {
                Id = "msg_new",
                AccountId = "sub_ordering",
                ThreadId = "th_2",
                SenderName = "Bob",
                SenderEmail = "bob@example.com",
                Subject = "Newer Email",
                Snippet = "New snippet",
                InternalDate = now
            }
        };

        await _database.UpsertCachedMessagesAsync(messages);

        var list = await _database.GetCachedMessagesAsync("sub_ordering");
        Assert.Equal(2, list.Count);
        Assert.Equal("msg_new", list[0].Id);
        Assert.Equal("msg_old", list[1].Id);
    }

    public void Dispose()
    {
        _database.Dispose();
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { }
        }
    }
}

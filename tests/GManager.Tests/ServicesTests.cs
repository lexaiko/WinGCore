using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using GManager.Auth;
using GManager.Core;
using GManager.Models;
using GManager.Services;
using Xunit;

namespace GManager.Tests;

public class ServicesTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly Database _database;
    private readonly InMemoryCredentialStore _credentialStore;
    private readonly EventAggregator _eventAggregator;
    private readonly AppConfig _config;

    public ServicesTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"gmanager_services_test_{Guid.NewGuid():N}.db");
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var encryption = new EncryptionService(key);
        _database = new Database(_tempDbPath, encryption);
        _database.Initialize();

        _credentialStore = new InMemoryCredentialStore();
        _eventAggregator = new EventAggregator();
        _config = new AppConfig { ClientId = "mock_client" };
    }

    [Fact]
    public async Task PeopleService_FetchProfileAsync_ParsesNamesAndEmailCorrectly()
    {
        var jsonResponse = @"{
            ""names"": [{ ""displayName"": ""Jane Doe"", ""givenName"": ""Jane"", ""familyName"": ""Doe"" }],
            ""emailAddresses"": [{ ""value"": ""jane.doe@gmail.com"" }],
            ""photos"": [{ ""url"": ""https://lh3.googleusercontent.com/a/photo_123"" }]
        }";

        var mockHttp = new HttpClient(new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
        }));

        var oauth = new OAuthClient(mockHttp, _config);
        var tokenManager = new TokenManager(oauth, _credentialStore, _database, _eventAggregator);
        tokenManager.StoreTokens("jane_id", "jane.doe@gmail.com", new OAuthTokens { AccessToken = "valid_tok", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

        var peopleService = new PeopleService(mockHttp, tokenManager, _config, _database);

        var account = await peopleService.FetchProfileAsync("jane_id");

        Assert.NotNull(account);
        Assert.Equal("Jane Doe", account.DisplayName);
        Assert.Equal("Jane", account.GivenName);
        Assert.Equal("Doe", account.FamilyName);
        Assert.Equal("jane.doe@gmail.com", account.Email);
    }

    [Fact]
    public async Task DriveService_FetchStorageQuotaAsync_ParsesUsageAndLimit()
    {
        var jsonResponse = @"{
            ""storageQuota"": {
                ""usage"": ""5368709120"",
                ""limit"": ""16106127360""
            }
        }";

        var mockHttp = new HttpClient(new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
        }));

        var oauth = new OAuthClient(mockHttp, _config);
        var tokenManager = new TokenManager(oauth, _credentialStore, _database, _eventAggregator);
        tokenManager.StoreTokens("acc_drive", "drive@gmail.com", new OAuthTokens { AccessToken = "tok", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

        var account = new GoogleAccount { Id = "acc_drive", Email = "drive@gmail.com" };
        await _database.UpsertAccountAsync(account);

        var driveService = new DriveService(mockHttp, tokenManager, _database);
        var quota = await driveService.FetchStorageQuotaAsync("acc_drive");

        Assert.Equal(5368709120L, quota.UsedBytes);
        Assert.Equal(16106127360L, quota.TotalBytes);

        var updated = await _database.GetAccountByIdAsync("acc_drive");
        Assert.NotNull(updated);
        Assert.Equal(5368709120L, updated.DriveUsedBytes);
        Assert.Equal(16106127360L, updated.DriveTotalBytes);
    }

    [Fact]
    public async Task GmailService_GetUnreadCountAsync_ParsesInboxUnread()
    {
        var jsonResponse = @"{
            ""id"": ""INBOX"",
            ""messagesUnread"": 7
        }";

        var mockHttp = new HttpClient(new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
        }));

        var oauth = new OAuthClient(mockHttp, _config);
        var tokenManager = new TokenManager(oauth, _credentialStore, _database, _eventAggregator);
        tokenManager.StoreTokens("acc_gmail", "gmail@gmail.com", new OAuthTokens { AccessToken = "tok", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

        var gmailService = new GmailService(mockHttp, tokenManager, _database);
        var count = await gmailService.GetUnreadCountAsync("acc_gmail");

        Assert.Equal(7, count);
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

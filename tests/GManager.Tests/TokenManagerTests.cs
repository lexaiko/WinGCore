using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using GManager.Auth;
using GManager.Core;
using GManager.Models;
using Xunit;

namespace GManager.Tests;

public class TokenManagerTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly Database _database;
    private readonly InMemoryCredentialStore _credentialStore;
    private readonly EventAggregator _eventAggregator;

    public TokenManagerTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"gmanager_token_test_{Guid.NewGuid():N}.db");
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var encryption = new EncryptionService(key);
        _database = new Database(_tempDbPath, encryption);
        _database.Initialize();

        _credentialStore = new InMemoryCredentialStore();
        _eventAggregator = new EventAggregator();
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_WhenTokenIsCachedAndValid_ReturnsCachedTokenImmediately()
    {
        var mockHttp = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var config = new AppConfig { ClientId = "test_client_id" };
        var oauthClient = new OAuthClient(mockHttp, config);
        var tokenManager = new TokenManager(oauthClient, _credentialStore, _database, _eventAggregator);

        var tokens = new OAuthTokens
        {
            AccessToken = "ya29.valid_cached_token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        tokenManager.StoreTokens("acc_001", "user@gmail.com", tokens);

        var token = await tokenManager.GetValidAccessTokenAsync("acc_001");

        Assert.Equal("ya29.valid_cached_token", token);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_WhenTokenIsExpired_RefreshesTokenSuccessfully()
    {
        const string expectedNewToken = "ya29.brand_new_refreshed_token";
        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"access_token\": \"{expectedNewToken}\", \"expires_in\": 3600, \"token_type\": \"Bearer\"}}",
                Encoding.UTF8,
                "application/json")
        });

        var mockHttp = new HttpClient(mockHandler);
        var config = new AppConfig { ClientId = "test_client_id" };
        var oauthClient = new OAuthClient(mockHttp, config);
        var tokenManager = new TokenManager(oauthClient, _credentialStore, _database, _eventAggregator);

        // Setup account in database and refresh token in store
        var account = new GoogleAccount
        {
            Id = "acc_expired",
            Email = "expired@gmail.com",
            State = AccountState.Active
        };
        await _database.UpsertAccountAsync(account);
        _credentialStore.SetCredential("expired@gmail.com", "1//04mock_refresh_token");

        // Request token (cache is empty, triggers refresh)
        var resultToken = await tokenManager.GetValidAccessTokenAsync("acc_expired");

        Assert.Equal(expectedNewToken, resultToken);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_WhenRefreshTokenRevoked_MarksAccountActionNeeded()
    {
        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\": \"invalid_grant\", \"error_description\": \"Token has been expired or revoked.\"}",
                Encoding.UTF8,
                "application/json")
        });

        var mockHttp = new HttpClient(mockHandler);
        var config = new AppConfig { ClientId = "test_client_id" };
        var oauthClient = new OAuthClient(mockHttp, config);
        var tokenManager = new TokenManager(oauthClient, _credentialStore, _database, _eventAggregator);

        var account = new GoogleAccount
        {
            Id = "acc_revoked",
            Email = "revoked@gmail.com",
            State = AccountState.Active
        };
        await _database.UpsertAccountAsync(account);
        _credentialStore.SetCredential("revoked@gmail.com", "1//04revoked_refresh_token");

        AccountStateChangedEvent? receivedEvent = null;
        _eventAggregator.Subscribe<AccountStateChangedEvent>(e => receivedEvent = e);

        // Expect InvalidGrantException
        await Assert.ThrowsAsync<InvalidGrantException>(() => tokenManager.GetValidAccessTokenAsync("acc_revoked"));

        // Verify account state transitioned to ActionNeeded in database
        var updatedAccount = await _database.GetAccountByIdAsync("acc_revoked");
        Assert.NotNull(updatedAccount);
        Assert.Equal(AccountState.ActionNeeded, updatedAccount.State);

        // Verify event was published
        Assert.NotNull(receivedEvent);
        Assert.Equal("acc_revoked", receivedEvent.AccountId);
        Assert.Equal(AccountState.ActionNeeded, receivedEvent.NewState);
    }

    [Fact]
    public void ParseIdToken_ValidJwt_ExtractsClaimsCorrectly()
    {
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\"}"));
        var payloadJson = "{\"sub\":\"1234567890\",\"email\":\"user@gmail.com\",\"name\":\"John Doe\",\"given_name\":\"John\",\"family_name\":\"Doe\",\"picture\":\"https://avatar.test/pic.jpg\"}";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var dummySignature = "dummySig";
        var jwt = $"{header}.{payload}.{dummySignature}";

        var tokens = new OAuthTokens { IdToken = jwt };
        var claims = tokens.ParseIdToken();

        Assert.NotNull(claims);
        Assert.Equal("1234567890", claims.Sub);
        Assert.Equal("user@gmail.com", claims.Email);
        Assert.Equal("John Doe", claims.Name);
        Assert.Equal("John", claims.GivenName);
        Assert.Equal("Doe", claims.FamilyName);
        Assert.Equal("https://avatar.test/pic.jpg", claims.Picture);
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

/// <summary>
/// Lightweight in-memory credential store for tests.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _store = new(StringComparer.OrdinalIgnoreCase);

    public void SetCredential(string key, string secret) => _store[key] = secret;
    public string? GetCredential(string key) => _store.TryGetValue(key, out var val) ? val : null;
    public bool RemoveCredential(string key) => _store.Remove(key);
}

/// <summary>
/// Mock HTTP handler for routing simulated requests in tests.
/// </summary>
public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_handler(request));
    }
}

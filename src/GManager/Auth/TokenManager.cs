using System.Collections.Concurrent;
using GManager.Core;
using GManager.Models;

namespace GManager.Auth;

/// <summary>
/// Manages OAuth access token lifecycles in memory with proactive/lazy refreshing
/// and automatic error state synchronization with the database and UI event bus.
/// Access tokens are never written to disk.
/// </summary>
public sealed class TokenManager
{
    private readonly OAuthClient _oauthClient;
    private readonly ICredentialStore _credentialStore;
    private readonly Database _database;
    private readonly IEventAggregator _eventAggregator;
    private readonly NativeRuntimeClient? _native;

    // In-memory token cache: AccountId -> OAuthTokens
    private readonly ConcurrentDictionary<string, OAuthTokens> _tokenCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public TokenManager(
        OAuthClient oauthClient,
        ICredentialStore credentialStore,
        Database database,
        IEventAggregator eventAggregator,
        NativeRuntimeClient? native = null)
    {
        _oauthClient = oauthClient ?? throw new ArgumentNullException(nameof(oauthClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _native = native;
    }

    /// <summary>
    /// Gets a valid, non-expired access token for the given account.
    /// Automatically performs lazy refresh if the token is within 5 minutes of expiration.
    /// </summary>
    public async Task<string> GetValidAccessTokenAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        // Fast path: check in-memory cache without locking
        if (_tokenCache.TryGetValue(accountId, out var cached) && !cached.IsExpired(TimeSpan.FromMinutes(5)))
        {
            return cached.AccessToken;
        }

        // Slow path: synchronize refresh per account to prevent duplicate refresh requests
        var accountLock = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync(cancellationToken);

        try
        {
            // Recheck cache after acquiring lock
            if (_tokenCache.TryGetValue(accountId, out cached) && !cached.IsExpired(TimeSpan.FromMinutes(5)))
            {
                return cached.AccessToken;
            }

            var account = await _database.GetAccountByIdAsync(accountId);
            if (account == null)
            {
                throw new KeyNotFoundException($"Account '{accountId}' does not exist in local database.");
            }

            var refreshToken = _credentialStore.GetCredential(account.Email);
            if (string.IsNullOrEmpty(refreshToken))
            {
                await MarkAccountActionNeededAsync(account.Id, "No refresh token available in Windows Credential Manager.");
                throw new InvalidOperationException($"No refresh token found for account '{account.Email}'.");
            }

            try
            {
                var refreshed = await _oauthClient.RefreshTokenAsync(refreshToken, cancellationToken);

                // If Google rotated the refresh token, persist the new one
                if (!string.IsNullOrEmpty(refreshed.RefreshToken) && refreshed.RefreshToken != refreshToken)
                {
                    _credentialStore.SetCredential(account.Email, refreshed.RefreshToken);
                }

                _tokenCache[accountId] = refreshed;

                // Ensure account state is Active if it was previously ActionNeeded
                if (account.State != AccountState.Active)
                {
                    await _database.UpdateAccountStateAsync(accountId, AccountState.Active);
                    _eventAggregator.Publish(new AccountStateChangedEvent(accountId, AccountState.Active));
                }

                return refreshed.AccessToken;
            }
            catch (InvalidGrantException ex)
            {
                // The user revoked access or changed their password
                await MarkAccountActionNeededAsync(account.Id, ex.Message);
                throw;
            }
        }
        finally
        {
            accountLock.Release();
        }
    }

    public async Task<string> GetServiceTokenAsync(string accountId, GManager.Contracts.NativeService service,
        bool forceRefresh, CancellationToken cancellationToken)
    {
        if (NativeAccountCoordinator.TryGetSession(accountId, out var session))
        {
            if (_native is null) throw new InvalidOperationException("Native runtime is unavailable.");
            try { return (await _native.GrantAsync(session, service, forceRefresh, cancellationToken)).AccessToken; }
            catch (NativeRuntimeException ex) when (ex.Code == "ActionNeeded")
            {
                await MarkAccountActionNeededAsync(accountId, ex.Message);
                throw new InvalidGrantException(ex.Message);
            }
        }
        if (forceRefresh) _tokenCache.TryRemove(accountId, out _);
        return await GetValidAccessTokenAsync(accountId, cancellationToken);
    }

    /// <summary>
    /// Caches new tokens received from initial OAuth authorization code exchange.
    /// Stores the refresh token securely in Windows Credential Manager.
    /// </summary>
    public void StoreTokens(string accountId, string email, OAuthTokens tokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(tokens);

        if (!string.IsNullOrEmpty(tokens.RefreshToken))
        {
            _credentialStore.SetCredential(email, tokens.RefreshToken);
        }

        _tokenCache[accountId] = tokens;
    }

    /// <summary>
    /// Completely removes an account: revokes tokens at Google, removes from PasswordVault,
    /// clears cache, deletes from SQLite, and announces AccountRemovedEvent.
    /// </summary>
    public async Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (NativeAccountCoordinator.TryGetSession(accountId, out var session))
        {
            if (_native is null) throw new InvalidOperationException("Native runtime is unavailable.");
            await _native.SendAsync(new(1, "forget-session", SessionId: session), cancellationToken);
            await _database.DeleteAccountAsync(accountId);
            _eventAggregator.Publish(new AccountRemovedEvent(accountId));
            return;
        }

        var account = await _database.GetAccountByIdAsync(accountId);
        if (account == null) return;

        // 1. Revoke token at Google (best effort)
        var refreshToken = _credentialStore.GetCredential(account.Email);
        if (!string.IsNullOrEmpty(refreshToken))
        {
            await _oauthClient.RevokeTokenAsync(refreshToken, cancellationToken);
        }

        // 2. Remove credential from Windows Credential Manager
        _credentialStore.RemoveCredential(account.Email);

        // 3. Clear in-memory token cache
        _tokenCache.TryRemove(accountId, out _);
        _locks.TryRemove(accountId, out _);

        // 4. Cascade delete from SQLite
        await _database.DeleteAccountAsync(accountId);

        // 5. Announce removal to UI bus
        _eventAggregator.Publish(new AccountRemovedEvent(accountId));
    }

    private async Task MarkAccountActionNeededAsync(string accountId, string reason)
    {
        await _database.UpdateAccountStateAsync(accountId, AccountState.ActionNeeded);
        _eventAggregator.Publish(new AccountStateChangedEvent(accountId, AccountState.ActionNeeded));
    }
}

using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using GManager.Auth;
using GManager.Core;
using GManager.Models;

namespace GManager.Services;

/// <summary>
/// Fetches profile information (names, email, avatar) using the Google People API (v1).
/// Automatically caches avatars to %LOCALAPPDATA%\GManager\avatars.
/// </summary>
public sealed class PeopleService : GoogleApiBase
{
    private const string PeopleEndpoint = "https://people.googleapis.com/v1/people/me?personFields=names,emailAddresses,photos,metadata";
    private readonly AppConfig _config;
    private readonly Database _database;

    public PeopleService(HttpClient httpClient, TokenManager tokenManager, AppConfig config, Database database)
        : base(httpClient, tokenManager)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    // OpenID Connect UserInfo — works with ANY client ID and openid+profile+email scopes.
    // No Google Cloud API activation required.
    private const string UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";

    /// <summary>
    /// Ensures account profile is stored in local database using OpenID claims and avatar download.
    /// </summary>
    public async Task<GoogleAccount> EnsureProfileLoadedAsync(
        string accountId,
        string email,
        GoogleIdTokenClaims? claims,
        CancellationToken cancellationToken = default)
    {
        AppLogger.Log("PeopleService", $"EnsureProfileLoadedAsync for {email} ({accountId})");

        var existing = await _database.GetAccountByIdAsync(accountId) ?? new GoogleAccount
        {
            Id = accountId,
            Email = email
        };

        existing.Id = accountId;
        existing.Email = !string.IsNullOrWhiteSpace(email) ? email : existing.Email;

        if (claims != null)
        {
            existing.DisplayName = !string.IsNullOrWhiteSpace(claims.Name) ? claims.Name : existing.DisplayName;
            existing.GivenName   = !string.IsNullOrWhiteSpace(claims.GivenName) ? claims.GivenName : existing.GivenName;
            existing.FamilyName  = !string.IsNullOrWhiteSpace(claims.FamilyName) ? claims.FamilyName : existing.FamilyName;
            existing.AvatarUrl   = !string.IsNullOrWhiteSpace(claims.Picture) ? claims.Picture : existing.AvatarUrl;
        }

        if (string.IsNullOrWhiteSpace(existing.DisplayName))
        {
            existing.DisplayName = existing.Email;
        }

        if (!string.IsNullOrWhiteSpace(existing.AvatarUrl))
        {
            try
            {
                var path = await DownloadAvatarAsync(existing.Id, existing.AvatarUrl, cancellationToken);
                if (!string.IsNullOrEmpty(path)) existing.AvatarLocalPath = path;
            }
            catch (Exception ex)
            {
                AppLogger.LogError("PeopleService", "Failed downloading avatar", ex);
            }
        }

        await _database.UpsertAccountAsync(existing);
        AppLogger.Log("PeopleService", $"Account {email} saved to database.");
        return existing;
    }

    /// <summary>
    /// Direct query to /userinfo with Bearer token without needing TokenManager cache.
    /// </summary>
    public async Task<UserInfoDto> FetchUserInfoDirectAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<UserInfoDto>(json)
            ?? throw new InvalidOperationException("Failed to parse userinfo response.");
    }

    /// <summary>
    /// Fetches the profile for the authenticated account and updates local database cache.
    /// First tries Google People API for richer data; falls back to OpenID Connect UserInfo.
    /// </summary>
    public async Task<GoogleAccount> FetchProfileAsync(string accountId, CancellationToken cancellationToken = default)
    {
        // Try People API first (richer data)
        GoogleAccount? account = null;
        try
        {
            var response = await GetJsonAsync<PeopleApiResponse>(accountId, PeopleEndpoint, null, cancellationToken);
            if (response != null)
            {
                account = await MapPeopleResponseAsync(accountId, response, cancellationToken);
            }
        }
        catch
        {
            // People API unavailable — fall through to OpenID Connect UserInfo
        }

        // Fallback: OpenID Connect /userinfo — always available for openid+profile+email tokens
        if (account == null)
        {
            var userInfo = await GetJsonAsync<UserInfoDto>(accountId, UserInfoEndpoint, null, cancellationToken)
                ?? throw new InvalidOperationException("Failed to retrieve profile from Google.");

            var existing = await _database.GetAccountByIdAsync(accountId) ?? new GoogleAccount { Id = accountId };
            existing.Id          = accountId;
            existing.DisplayName = userInfo.Name ?? existing.DisplayName;
            existing.GivenName   = userInfo.GivenName ?? existing.GivenName;
            existing.FamilyName  = userInfo.FamilyName ?? existing.FamilyName;
            existing.Email       = userInfo.Email ?? existing.Email;
            existing.AvatarUrl   = userInfo.Picture ?? existing.AvatarUrl;

            if (!string.IsNullOrWhiteSpace(userInfo.Picture))
            {
                try
                {
                    var path = await DownloadAvatarAsync(existing.Id, userInfo.Picture, cancellationToken);
                    if (!string.IsNullOrEmpty(path)) existing.AvatarLocalPath = path;
                }
                catch { /* avatar download is non-fatal */ }
            }

            await _database.UpsertAccountAsync(existing);
            account = existing;
        }

        return account;
    }

    private async Task<GoogleAccount> MapPeopleResponseAsync(string accountId, PeopleApiResponse response, CancellationToken cancellationToken)
    {
        var existing = await _database.GetAccountByIdAsync(accountId) ?? new GoogleAccount { Id = accountId };

        var primaryName = response.Names?.FirstOrDefault();
        if (primaryName != null)
        {
            existing.DisplayName = primaryName.DisplayName ?? existing.DisplayName;
            existing.GivenName   = primaryName.GivenName   ?? existing.GivenName;
            existing.FamilyName  = primaryName.FamilyName  ?? existing.FamilyName;
        }

        var primaryEmail = response.EmailAddresses?.FirstOrDefault()?.Value;
        if (!string.IsNullOrWhiteSpace(primaryEmail))
        {
            existing.Email = primaryEmail;
        }

        var photoUrl = response.Photos?.FirstOrDefault()?.Url;
        if (!string.IsNullOrWhiteSpace(photoUrl))
        {
            existing.AvatarUrl = photoUrl;
            try
            {
                var localPath = await DownloadAvatarAsync(existing.Id, photoUrl, cancellationToken);
                if (!string.IsNullOrEmpty(localPath)) existing.AvatarLocalPath = localPath;
            }
            catch { }
        }

        await _database.UpsertAccountAsync(existing);
        return existing;
    }

    /// <summary>
    /// Downloads the user's avatar image to local storage.
    /// </summary>
    public async Task<string?> DownloadAvatarAsync(string accountId, string avatarUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl)) return null;

        _config.EnsureDirectories();
        var destinationPath = Path.Combine(_config.AvatarsDirectory, $"{accountId}.jpg");

        using var response = await HttpClient.GetAsync(avatarUrl, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);

        return destinationPath;
    }

    #region People API DTOs

    private sealed class PeopleApiResponse
    {
        [JsonPropertyName("names")]
        public List<PersonName>? Names { get; set; }

        [JsonPropertyName("emailAddresses")]
        public List<PersonEmail>? EmailAddresses { get; set; }

        [JsonPropertyName("photos")]
        public List<PersonPhoto>? Photos { get; set; }
    }

    private sealed class PersonName
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("givenName")]
        public string? GivenName { get; set; }

        [JsonPropertyName("familyName")]
        public string? FamilyName { get; set; }
    }

    private sealed class PersonEmail
    {
        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    private sealed class PersonPhoto
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    /// <summary>
    /// OpenID Connect /userinfo response — always available for openid+profile+email tokens,
    /// no Google Cloud API activation required.
    /// </summary>
    public sealed class UserInfoDto
    {
        [JsonPropertyName("sub")]
        public string Sub { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("given_name")]
        public string? GivenName { get; set; }

        [JsonPropertyName("family_name")]
        public string? FamilyName { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("picture")]
        public string? Picture { get; set; }
    }

    #endregion
}

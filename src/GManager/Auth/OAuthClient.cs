using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using GManager.Core;
using GManager.Models;

namespace GManager.Auth;

/// <summary>
/// Handles OAuth 2.0 PKCE authorization, token exchange, and token refresh with Google Identity.
/// </summary>
public sealed class OAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;

    public OAuthClient(HttpClient httpClient, AppConfig config)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Starts the complete interactive OAuth 2.0 login flow with system browser.
    ///
    /// Uses the standard Desktop App loopback redirect (http://127.0.0.1:PORT),
    /// which is registered for the built-in client ID and fully supported by Google.
    /// Scopes are identity-only (openid + profile + email) so Google never shows
    /// the "app blocked" screen.
    /// </summary>
    public async Task<OAuthTokens> LoginAsync(CancellationToken cancellationToken = default)
    {
        using var loopback = new LoopbackServer();
        loopback.Start();
        AppLogger.Log("OAuth", $"Loopback server started on {loopback.RedirectUri}");

        var (verifier, challenge, method) = PkceGenerator.CreatePkcePair();
        var state = Guid.NewGuid().ToString("N");

        var authUrl = BuildAuthorizationUrl(
            _config.ClientId,
            loopback.RedirectUri,
            challenge,
            method,
            state,
            AppConfig.ScopeString);

        AppLogger.Log("OAuth", $"Opening browser with URL: {authUrl}");

        // Open the system browser — user sees the real Google Sign-In page
        Process.Start(new ProcessStartInfo
        {
            FileName        = authUrl,
            UseShellExecute = true
        });

        // Wait for the browser to redirect back to our loopback listener (max 3 min)
        AppLogger.Log("OAuth", "Waiting for callback on loopback listener...");
        var callbackResult = await loopback.WaitForCallbackAsync(
            state, TimeSpan.FromMinutes(3), cancellationToken);

        if (!string.IsNullOrEmpty(callbackResult.Error))
        {
            AppLogger.LogError("OAuth", $"Callback error: {callbackResult.Error} - {callbackResult.ErrorDescription}");
            throw new InvalidOperationException(
                $"OAuth error: {callbackResult.Error} — {callbackResult.ErrorDescription}");
        }

        if (string.IsNullOrEmpty(callbackResult.Code))
        {
            AppLogger.LogError("OAuth", "No authorization code returned.");
            throw new InvalidOperationException("OAuth failed: No authorization code was returned.");
        }

        AppLogger.Log("OAuth", "Authorization code received. Exchanging for tokens...");
        return await ExchangeCodeForTokensAsync(
            callbackResult.Code, verifier, loopback.RedirectUri, cancellationToken);
    }

    /// <summary>
    /// Exchanges an authorization code and PKCE verifier for access and refresh tokens.
    /// </summary>
    public async Task<OAuthTokens> ExchangeCodeForTokensAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = _config.ClientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        };

        if (!string.IsNullOrWhiteSpace(_config.ClientSecret))
        {
            parameters["client_secret"] = _config.ClientSecret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, AppConfig.OAuthTokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Token exchange failed ({response.StatusCode}): {responseJson}");
        }

        var tokens = JsonSerializer.Deserialize<OAuthTokens>(responseJson)
            ?? throw new JsonException("Failed to parse OAuth tokens response.");

        tokens.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
        return tokens;
    }

    /// <summary>
    /// Uses a refresh token to obtain a fresh access token without user prompt.
    /// </summary>
    public async Task<OAuthTokens> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = _config.ClientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };

        if (!string.IsNullOrWhiteSpace(_config.ClientSecret))
        {
            parameters["client_secret"] = _config.ClientSecret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, AppConfig.OAuthTokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Detect invalid_grant (revoked consent or expired refresh token)
            if (responseJson.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidGrantException("Refresh token is invalid or has been revoked by the user.");
            }

            throw new HttpRequestException($"Token refresh failed ({response.StatusCode}): {responseJson}");
        }

        var tokens = JsonSerializer.Deserialize<OAuthTokens>(responseJson)
            ?? throw new JsonException("Failed to parse refreshed OAuth tokens.");

        // Some providers do not re-issue refresh_token on refresh; preserve existing
        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            tokens.RefreshToken = refreshToken;
        }

        tokens.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
        return tokens;
    }

    /// <summary>
    /// Revokes an access or refresh token on Google's servers.
    /// </summary>
    public async Task<bool> RevokeTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return true;

        try
        {
            var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]);
            using var response = await _httpClient.PostAsync(AppConfig.OAuthRevokeEndpoint, content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildAuthorizationUrl(
        string clientId,
        string redirectUri,
        string codeChallenge,
        string codeChallengeMethod,
        string state,
        string scopes)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scopes,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = codeChallengeMethod,
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "select_account consent"
        };

        var queryString = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{AppConfig.OAuthAuthEndpoint}?{queryString}";
    }
}

/// <summary>
/// Thrown when Google returns an 'invalid_grant' error indicating the user revoked app access
/// or the refresh token is expired.
/// </summary>
public sealed class InvalidGrantException : Exception
{
    public InvalidGrantException(string message) : base(message) { }
}

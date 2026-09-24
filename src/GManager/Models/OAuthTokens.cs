using System.Text.Json.Serialization;

namespace GManager.Models;

/// <summary>
/// OAuth 2.0 token response and runtime state.
/// </summary>
public sealed class OAuthTokens
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    /// <summary>
    /// Calculated UTC timestamp when the access token expires.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Checks if the token is expired or within a safety buffer (default 5 minutes).
    /// </summary>
    public bool IsExpired(TimeSpan? buffer = null)
    {
        var safetyBuffer = buffer ?? TimeSpan.FromMinutes(5);
        return DateTimeOffset.UtcNow >= (ExpiresAt - safetyBuffer);
    }

    /// <summary>
    /// Parses the OIDC id_token JWT payload without requiring external libraries.
    /// </summary>
    public GoogleIdTokenClaims? ParseIdToken()
    {
        if (string.IsNullOrWhiteSpace(IdToken)) return null;

        try
        {
            var parts = IdToken.Split('.');
            if (parts.Length < 2) return null;

            var base64 = parts[1].Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }

            var jsonBytes = Convert.FromBase64String(base64);
            using var doc = System.Text.Json.JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;

            return new GoogleIdTokenClaims
            {
                Sub = root.TryGetProperty("sub", out var sub) ? sub.GetString() ?? string.Empty : string.Empty,
                Email = root.TryGetProperty("email", out var email) ? email.GetString() ?? string.Empty : string.Empty,
                Name = root.TryGetProperty("name", out var name) ? name.GetString() : null,
                GivenName = root.TryGetProperty("given_name", out var gn) ? gn.GetString() : null,
                FamilyName = root.TryGetProperty("family_name", out var fn) ? fn.GetString() : null,
                Picture = root.TryGetProperty("picture", out var pic) ? pic.GetString() : null
            };
        }
        catch
        {
            return null;
        }
    }
}

public sealed class GoogleIdTokenClaims
{
    public string Sub { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? Picture { get; set; }
}

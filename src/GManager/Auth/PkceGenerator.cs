using System.Security.Cryptography;
using System.Text;

namespace GManager.Auth;

/// <summary>
/// Implements RFC 7636 Proof Key for Code Exchange (PKCE) by OAuth Public Clients.
/// </summary>
public static class PkceGenerator
{
    private const string UnreservedCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";

    /// <summary>
    /// Generates a high-entropy cryptographic random code verifier (default 64 characters, RFC allows 43-128).
    /// </summary>
    public static string GenerateCodeVerifier(int length = 64)
    {
        if (length < 43 || length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "PKCE code verifier length must be between 43 and 128 characters.");
        }

        var randomBytes = new byte[length];
        RandomNumberGenerator.Fill(randomBytes);

        var sb = new StringBuilder(length);
        foreach (var b in randomBytes)
        {
            sb.Append(UnreservedCharacters[b % UnreservedCharacters.Length]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates the S256 code challenge from a code verifier according to RFC 7636.
    /// code_challenge = Base64UrlEncode(SHA256(ASCII(code_verifier)))
    /// </summary>
    public static string GenerateCodeChallenge(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        var bytes = Encoding.ASCII.GetBytes(codeVerifier);
        var hash = SHA256.HashData(bytes);
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// Generates a matched pair of PKCE code verifier and S256 code challenge.
    /// </summary>
    public static (string Verifier, string Challenge, string Method) CreatePkcePair(int length = 64)
    {
        var verifier = GenerateCodeVerifier(length);
        var challenge = GenerateCodeChallenge(verifier);
        return (verifier, challenge, "S256");
    }

    /// <summary>
    /// Encodes bytes to standard Base64Url string (without padding, with '-' and '_').
    /// </summary>
    public static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

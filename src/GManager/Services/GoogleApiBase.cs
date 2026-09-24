using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using GManager.Auth;

namespace GManager.Services;

/// <summary>
/// Base service for Google REST API requests.
/// Handles token injection, 401 re-auth retry, and exponential backoff for transient errors.
/// </summary>
public abstract class GoogleApiBase
{
    protected readonly HttpClient HttpClient;
    protected readonly TokenManager TokenManager;
    protected virtual GManager.Contracts.NativeService NativeService => GManager.Contracts.NativeService.Identity;

    protected GoogleApiBase(HttpClient httpClient, TokenManager tokenManager)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        TokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
    }

    /// <summary>
    /// Sends an HTTP request with automatic Google OAuth Bearer authorization and retry policy.
    /// </summary>
    protected async Task<HttpResponseMessage> SendWithAuthAsync(
        string accountId,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken = default)
    {
        int maxRetries = 3;
        int delayMs = 1000;
        bool refreshRequired = false;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            var token = await TokenManager.GetServiceTokenAsync(accountId, NativeService, attempt == 2 && refreshRequired, cancellationToken);
            using var request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try
            {
                response = await HttpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                await Task.Delay(delayMs, cancellationToken);
                delayMs *= 2;
                continue;
            }

            // If 401 Unauthorized, force token refresh and retry once
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
            {
                response.Dispose();
                refreshRequired = true;
                continue;
            }

            // Retry on 429 Too Many Requests or 5xx Server Errors
            if ((response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500) && attempt < maxRetries)
            {
                response.Dispose();
                await Task.Delay(delayMs, cancellationToken);
                delayMs *= 2;
                continue;
            }

            return response;
        }

        // Final attempt
        var finalToken = await TokenManager.GetServiceTokenAsync(accountId, NativeService, false, cancellationToken);
        using var finalRequest = requestFactory();
        finalRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", finalToken);
        return await HttpClient.SendAsync(finalRequest, cancellationToken);
    }

    /// <summary>
    /// Sends an authenticated GET request and deserializes the JSON response into T.
    /// </summary>
    protected async Task<T?> GetJsonAsync<T>(
        string accountId,
        string url,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendWithAuthAsync(accountId, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, options ?? DefaultJsonOptions, cancellationToken);
    }

    protected static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

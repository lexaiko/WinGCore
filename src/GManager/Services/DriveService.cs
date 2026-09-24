using System.Net.Http;
using System.Text.Json.Serialization;
using GManager.Auth;
using GManager.Core;

namespace GManager.Services;

public record DriveStorageQuota(long UsedBytes, long TotalBytes);

/// <summary>
/// Client for the Google Drive REST API (v3) to query account storage quota.
/// </summary>
public sealed class DriveService : GoogleApiBase
{
    protected override GManager.Contracts.NativeService NativeService => GManager.Contracts.NativeService.Drive;
    private const string DriveAboutEndpoint = "https://www.googleapis.com/drive/v3/about?fields=storageQuota";
    private readonly Database _database;

    public DriveService(HttpClient httpClient, TokenManager tokenManager, Database database)
        : base(httpClient, tokenManager)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>
    /// Fetches the user's storage quota from Google Drive API and updates local database.
    /// </summary>
    public async Task<DriveStorageQuota> FetchStorageQuotaAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync<DriveAboutResponse>(accountId, DriveAboutEndpoint, null, cancellationToken);
        if (response?.StorageQuota == null)
        {
            throw new InvalidOperationException("Failed to retrieve storage quota from Google Drive API.");
        }

        long.TryParse(response.StorageQuota.Usage, out var used);
        long.TryParse(response.StorageQuota.Limit, out var limit);

        // If unlimited or unconstrained, default limit to at least used
        if (limit <= 0)
        {
            limit = Math.Max(used, 15L * 1024 * 1024 * 1024); // 15 GB default baseline
        }

        var account = await _database.GetAccountByIdAsync(accountId);
        if (account != null)
        {
            await _database.UpdateAccountSyncStatsAsync(
                accountId,
                account.UnreadCount,
                used,
                limit,
                DateTimeOffset.UtcNow);
        }

        return new DriveStorageQuota(used, limit);
    }

    #region Drive API DTOs

    private sealed class DriveAboutResponse
    {
        [JsonPropertyName("storageQuota")]
        public DriveStorageQuotaDto? StorageQuota { get; set; }
    }

    private sealed class DriveStorageQuotaDto
    {
        [JsonPropertyName("limit")]
        public string? Limit { get; set; }

        [JsonPropertyName("usage")]
        public string? Usage { get; set; }

        [JsonPropertyName("usageInDrive")]
        public string? UsageInDrive { get; set; }

        [JsonPropertyName("usageInDriveTrash")]
        public string? UsageInDriveTrash { get; set; }
    }

    #endregion
}

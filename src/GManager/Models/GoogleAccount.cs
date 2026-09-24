namespace GManager.Models;

/// <summary>
/// Domain model for an enrolled Google account in GManager.
/// </summary>
public sealed class GoogleAccount
{
    /// <summary>
    /// Google unique subject identifier (sub).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string GivenName { get; set; } = string.Empty;

    public string FamilyName { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Local cached path to user avatar on disk.
    /// </summary>
    public string? AvatarLocalPath { get; set; }

    public AccountState State { get; set; } = AccountState.Active;

    public int UnreadCount { get; set; }

    public long DriveUsedBytes { get; set; }

    public long DriveTotalBytes { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Human-friendly initials if avatar is not yet loaded (e.g. "JD" for John Doe).
    /// </summary>
    public string Initials
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(GivenName) && !string.IsNullOrWhiteSpace(FamilyName))
            {
                return $"{GivenName[0]}{FamilyName[0]}".ToUpperInvariant();
            }
            if (!string.IsNullOrWhiteSpace(DisplayName))
            {
                var parts = DisplayName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
                }
                if (parts.Length == 1 && parts[0].Length > 0)
                {
                    return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
                }
            }
            return !string.IsNullOrWhiteSpace(Email) ? Email[0..1].ToUpperInvariant() : "G";
        }
    }

    /// <summary>
    /// Formatted used storage string (e.g. "11.2 GB / 15 GB").
    /// </summary>
    public string FormattedStorage
    {
        get
        {
            if (DriveTotalBytes <= 0) return "0 GB / 0 GB";
            double usedGb = DriveUsedBytes / (1024.0 * 1024.0 * 1024.0);
            double totalGb = DriveTotalBytes / (1024.0 * 1024.0 * 1024.0);
            return $"{usedGb:F1} GB of {totalGb:F0} GB used";
        }
    }

    /// <summary>
    /// Percentage of storage used (0.0 to 100.0).
    /// </summary>
    public double StoragePercentage
    {
        get
        {
            if (DriveTotalBytes <= 0) return 0.0;
            return Math.Clamp((double)DriveUsedBytes / DriveTotalBytes * 100.0, 0.0, 100.0);
        }
    }
}

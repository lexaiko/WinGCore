namespace GManager.Models;

/// <summary>
/// Domain model for a cached Gmail message item.
/// </summary>
public sealed class MailMessage
{
    public string Id { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public string ThreadId { get; set; } = string.Empty;

    public string SenderName { get; set; } = string.Empty;

    public string SenderEmail { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Snippet { get; set; } = string.Empty;

    public DateTimeOffset InternalDate { get; set; } = DateTimeOffset.UtcNow;

    public bool IsUnread { get; set; }

    public bool IsStarred { get; set; }
    public string RecipientTo { get; set; } = string.Empty;
    public string RecipientCc { get; set; } = string.Empty;
    public string? BodyText { get; set; }
    public string? BodyHtml { get; set; }
    public bool HasFullBody { get; set; }
    public List<MailAttachment> Attachments { get; set; } = [];

    /// <summary>
    /// Relative or friendly formatted time (e.g. "10:45 AM", "Yesterday", "Sep 22").
    /// </summary>
    public string FormattedTime
    {
        get
        {
            var localTime = InternalDate.ToLocalTime();
            var now = DateTimeOffset.Now;

            if (localTime.Date == now.Date)
            {
                return localTime.ToString("t"); // e.g. 10:45 AM
            }
            if (localTime.Date == now.Date.AddDays(-1))
            {
                return "Yesterday";
            }
            if (localTime.Year == now.Year)
            {
                return localTime.ToString("MMM d"); // e.g. Sep 22
            }
            return localTime.ToString("MM/dd/yy");
        }
    }

    /// <summary>
    /// Sender initials for avatar representation.
    /// </summary>
    public string SenderInitials
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SenderName))
            {
                var parts = SenderName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
                }
                if (parts.Length == 1 && parts[0].Length > 0)
                {
                    return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
                }
            }
            return !string.IsNullOrWhiteSpace(SenderEmail) ? SenderEmail[0..1].ToUpperInvariant() : "M";
        }
    }
}

public sealed class MailAttachment
{
    public string AttachmentId { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public string FormattedSize
    {
        get
        {
            if (SizeBytes < 1024) return $"{SizeBytes} B";
            if (SizeBytes < 1024 * 1024) return $"{SizeBytes / 1024.0:F1} KB";
            return $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
        }
    }
}

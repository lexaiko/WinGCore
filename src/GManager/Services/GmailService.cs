using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GManager.Auth;
using GManager.Core;
using GManager.Models;

namespace GManager.Services;

/// <summary>
/// Client for the Gmail REST API (v1).
/// Fetches inbox statistics, latest messages, and performs message state updates.
/// </summary>
public sealed class GmailService : GoogleApiBase
{
    protected override GManager.Contracts.NativeService NativeService => GManager.Contracts.NativeService.Gmail;
    private const string BaseGmailUrl = "https://gmail.googleapis.com/gmail/v1/users/me";
    private readonly Database _database;

    public GmailService(HttpClient httpClient, TokenManager tokenManager, Database database)
        : base(httpClient, tokenManager)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>
    /// Gets the current unread message count for the INBOX label.
    /// </summary>
    public async Task<int> GetUnreadCountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseGmailUrl}/labels/INBOX";
        var label = await GetJsonAsync<GmailLabelDto>(accountId, url, null, cancellationToken);
        return label?.MessagesUnread ?? 0;
    }

    /// <summary>
    /// Fetches recent messages from INBOX, parses headers & snippets, and persists to local database.
    /// </summary>
    public async Task<List<MailMessage>> FetchRecentInboxMessagesAsync(
        string accountId,
        int maxResults = 25,
        CancellationToken cancellationToken = default)
    {
        var listUrl = $"{BaseGmailUrl}/messages?q=in:inbox&maxResults={maxResults}";
        var listResponse = await GetJsonAsync<GmailListMessagesResponse>(accountId, listUrl, null, cancellationToken);

        if (listResponse?.Messages == null || listResponse.Messages.Count == 0)
        {
            return [];
        }

        var results = new List<MailMessage>();

        // Fetch metadata for each message concurrently with throttled concurrency (max 5)
        using var semaphore = new SemaphoreSlim(5, 5);
        var tasks = listResponse.Messages.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var detailUrl = $"{BaseGmailUrl}/messages/{item.Id}?format=metadata&metadataHeaders=From&metadataHeaders=Subject&metadataHeaders=Date";
                var detail = await GetJsonAsync<GmailMessageDto>(accountId, detailUrl, null, cancellationToken);
                if (detail != null)
                {
                    return ParseMailMessage(accountId, detail);
                }
            }
            catch
            {
                // Skip problematic message on individual fetch error
            }
            finally
            {
                semaphore.Release();
            }
            return null;
        });

        var messageResults = await Task.WhenAll(tasks);
        foreach (var msg in messageResults)
        {
            if (msg != null) results.Add(msg);
        }

        // Cache in local database
        if (results.Count > 0)
        {
            await _database.UpsertCachedMessagesAsync(results);
        }

        return results;
    }

    /// <summary>
    /// Marks a Gmail message as read both on Google servers and in local database cache.
    /// </summary>
    public async Task MarkAsReadAsync(string accountId, string messageId, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseGmailUrl}/messages/{messageId}/modify";
        var payload = new { removeLabelIds = new[] { "UNREAD" } };
        var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await SendWithAuthAsync(accountId, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = jsonContent
        }, cancellationToken);

        response.EnsureSuccessStatusCode();

        // Update local DB
        await _database.MarkMessageReadAsync(accountId, messageId, isUnread: false);
    }

    private static MailMessage ParseMailMessage(string accountId, GmailMessageDto dto)
    {
        var msg = new MailMessage
        {
            Id = dto.Id ?? string.Empty,
            AccountId = accountId,
            ThreadId = dto.ThreadId ?? string.Empty,
            Snippet = System.Net.WebUtility.HtmlDecode(dto.Snippet ?? string.Empty),
            IsUnread = dto.LabelIds?.Contains("UNREAD") ?? false,
            IsStarred = dto.LabelIds?.Contains("STARRED") ?? false
        };

        // Parse InternalDate from timestamp ms
        if (long.TryParse(dto.InternalDate, out var ms))
        {
            msg.InternalDate = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        // Extract Headers
        if (dto.Payload?.Headers != null)
        {
            foreach (var header in dto.Payload.Headers)
            {
                if (string.Equals(header.Name, "Subject", StringComparison.OrdinalIgnoreCase))
                {
                    msg.Subject = header.Value ?? "(No subject)";
                }
                else if (string.Equals(header.Name, "From", StringComparison.OrdinalIgnoreCase))
                {
                    ParseSender(header.Value, out var name, out var email);
                    msg.SenderName = name;
                    msg.SenderEmail = email;
                }
            }
        }

        return msg;
    }

    private static void ParseSender(string? fromHeader, out string name, out string email)
    {
        name = string.Empty;
        email = string.Empty;

        if (string.IsNullOrWhiteSpace(fromHeader))
        {
            name = "Unknown";
            email = "";
            return;
        }

        // Typical format: "John Doe <johndoe@example.com>" or just "johndoe@example.com"
        var match = Regex.Match(fromHeader, @"^(?:""?([^""<]+)""?\s*)?<?([^>]+)>?$");
        if (match.Success)
        {
            name = match.Groups[1].Value.Trim();
            email = match.Groups[2].Value.Trim();

            if (string.IsNullOrEmpty(name))
            {
                name = email;
            }
        }
        else
        {
            name = fromHeader;
            email = fromHeader;
        }
    }

    /// <summary>
    /// Fetches the complete message details including full body (HTML/Text), recipients, and attachments.
    /// </summary>
    public async Task<MailMessage?> GetMessageDetailAsync(
        string accountId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{BaseGmailUrl}/messages/{messageId}?format=full";
        var dto = await GetJsonAsync<GmailMessageDto>(accountId, url, null, cancellationToken);
        if (dto == null) return null;

        var msg = ParseMailMessage(accountId, dto);

        string? bodyText = null;
        string? bodyHtml = null;
        var attachments = new List<MailAttachment>();

        ExtractBodyAndAttachments(dto.Payload, ref bodyText, ref bodyHtml, attachments);

        msg.BodyText = bodyText;
        msg.BodyHtml = bodyHtml;
        msg.Attachments = attachments;
        msg.HasFullBody = true;

        if (dto.Payload?.Headers != null)
        {
            foreach (var header in dto.Payload.Headers)
            {
                if (string.Equals(header.Name, "To", StringComparison.OrdinalIgnoreCase))
                {
                    msg.RecipientTo = header.Value ?? string.Empty;
                }
                else if (string.Equals(header.Name, "Cc", StringComparison.OrdinalIgnoreCase))
                {
                    msg.RecipientCc = header.Value ?? string.Empty;
                }
            }
        }

        return msg;
    }

    /// <summary>
    /// Stars or unstars a message.
    /// </summary>
    public async Task ToggleStarAsync(string accountId, string messageId, bool isStarred, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseGmailUrl}/messages/{messageId}/modify";
        var payload = isStarred
            ? new { addLabelIds = new[] { "STARRED" }, removeLabelIds = Array.Empty<string>() }
            : new { addLabelIds = Array.Empty<string>(), removeLabelIds = new[] { "STARRED" } };

        var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await SendWithAuthAsync(accountId, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = jsonContent
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Moves a message to the Trash.
    /// </summary>
    public async Task TrashMessageAsync(string accountId, string messageId, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseGmailUrl}/messages/{messageId}/trash";
        using var response = await SendWithAuthAsync(accountId, () => new HttpRequestMessage(HttpMethod.Post, url), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Downloads an attachment's binary content.
    /// </summary>
    public async Task<byte[]> DownloadAttachmentAsync(string accountId, string messageId, string attachmentId, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseGmailUrl}/messages/{messageId}/attachments/{attachmentId}";
        var dto = await GetJsonAsync<GmailAttachmentDto>(accountId, url, null, cancellationToken);
        return DecodeBase64UrlBytes(dto?.Data);
    }

    /// <summary>
    /// Sends an inline reply in the same thread.
    /// </summary>
    public async Task SendReplyAsync(
        string accountId,
        string threadId,
        string inReplyTo,
        string to,
        string subject,
        string bodyText,
        CancellationToken cancellationToken = default)
    {
        var replySubject = subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subject : $"Re: {subject}";
        var rfc2822 = new StringBuilder();
        rfc2822.AppendLine($"To: {to}");
        rfc2822.AppendLine($"Subject: {replySubject}");
        if (!string.IsNullOrWhiteSpace(inReplyTo))
        {
            rfc2822.AppendLine($"In-Reply-To: {inReplyTo}");
            rfc2822.AppendLine($"References: {inReplyTo}");
        }
        rfc2822.AppendLine("Content-Type: text/plain; charset=\"UTF-8\"");
        rfc2822.AppendLine("MIME-Version: 1.0");
        rfc2822.AppendLine();
        rfc2822.AppendLine(bodyText);

        var rawBase64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(rfc2822.ToString()))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var payload = new
        {
            raw = rawBase64Url,
            threadId = threadId
        };

        var url = $"{BaseGmailUrl}/messages/send";
        var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await SendWithAuthAsync(accountId, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = jsonContent
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static void ExtractBodyAndAttachments(
        GmailPayloadDto? payload,
        ref string? bodyText,
        ref string? bodyHtml,
        List<MailAttachment> attachments)
    {
        if (payload == null) return;

        if (!string.IsNullOrWhiteSpace(payload.Filename) && payload.Body?.AttachmentId != null)
        {
            attachments.Add(new MailAttachment
            {
                AttachmentId = payload.Body.AttachmentId,
                Filename = payload.Filename,
                MimeType = payload.MimeType ?? "application/octet-stream",
                SizeBytes = payload.Body.Size
            });
            return;
        }

        var mime = payload.MimeType?.ToLowerInvariant();
        if (mime == "text/plain" && string.IsNullOrEmpty(bodyText) && !string.IsNullOrEmpty(payload.Body?.Data))
        {
            bodyText = DecodeBase64Url(payload.Body.Data);
        }
        else if (mime == "text/html" && string.IsNullOrEmpty(bodyHtml) && !string.IsNullOrEmpty(payload.Body?.Data))
        {
            bodyHtml = DecodeBase64Url(payload.Body.Data);
        }

        if (payload.Parts != null)
        {
            foreach (var part in payload.Parts)
            {
                ExtractBodyAndAttachments(part, ref bodyText, ref bodyHtml, attachments);
            }
        }
    }

    public static string DecodeBase64Url(string? base64Url)
    {
        if (string.IsNullOrWhiteSpace(base64Url)) return string.Empty;
        var incoming = base64Url.Replace('-', '+').Replace('_', '/');
        switch (incoming.Length % 4)
        {
            case 2: incoming += "=="; break;
            case 3: incoming += "="; break;
        }
        var bytes = Convert.FromBase64String(incoming);
        return Encoding.UTF8.GetString(bytes);
    }

    public static byte[] DecodeBase64UrlBytes(string? base64Url)
    {
        if (string.IsNullOrWhiteSpace(base64Url)) return [];
        var incoming = base64Url.Replace('-', '+').Replace('_', '/');
        switch (incoming.Length % 4)
        {
            case 2: incoming += "=="; break;
            case 3: incoming += "="; break;
        }
        return Convert.FromBase64String(incoming);
    }

    #region Gmail API DTOs

    private sealed class GmailLabelDto
    {
        [JsonPropertyName("messagesUnread")]
        public int MessagesUnread { get; set; }
    }

    private sealed class GmailListMessagesResponse
    {
        [JsonPropertyName("messages")]
        public List<GmailMessageRefDto>? Messages { get; set; }
    }

    private sealed class GmailMessageRefDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("threadId")]
        public string? ThreadId { get; set; }
    }

    private sealed class GmailMessageDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("threadId")]
        public string? ThreadId { get; set; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; set; }

        [JsonPropertyName("internalDate")]
        public string? InternalDate { get; set; }

        [JsonPropertyName("labelIds")]
        public List<string>? LabelIds { get; set; }

        [JsonPropertyName("payload")]
        public GmailPayloadDto? Payload { get; set; }
    }

    private sealed class GmailPayloadDto
    {
        [JsonPropertyName("partId")]
        public string? PartId { get; set; }

        [JsonPropertyName("mimeType")]
        public string? MimeType { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("headers")]
        public List<GmailHeaderDto>? Headers { get; set; }

        [JsonPropertyName("body")]
        public GmailBodyDto? Body { get; set; }

        [JsonPropertyName("parts")]
        public List<GmailPayloadDto>? Parts { get; set; }
    }

    private sealed class GmailBodyDto
    {
        [JsonPropertyName("attachmentId")]
        public string? AttachmentId { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }

    private sealed class GmailHeaderDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    private sealed class GmailAttachmentDto
    {
        [JsonPropertyName("attachmentId")]
        public string? AttachmentId { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }

    #endregion
}


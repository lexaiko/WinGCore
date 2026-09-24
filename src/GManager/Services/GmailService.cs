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
        [JsonPropertyName("headers")]
        public List<GmailHeaderDto>? Headers { get; set; }
    }

    private sealed class GmailHeaderDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    #endregion
}

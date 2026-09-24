using System.Collections.Concurrent;
using GManager.Auth;
using GManager.Core;
using GManager.Models;

namespace GManager.Services;

/// <summary>
/// Background synchronization service.
/// Uses PeriodicTimer to periodically update account profiles, storage quotas, and inbox messages.
/// Detects newly arrived unread messages and raises NewMailReceivedEvent.
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly Database _database;
    private readonly PeopleService _peopleService;
    private readonly GmailService _gmailService;
    private readonly DriveService _driveService;
    private readonly IEventAggregator _eventAggregator;
    private readonly AppConfig _config;

    private CancellationTokenSource? _syncCts;
    private Task? _backgroundTask;
    private readonly ConcurrentDictionary<string, HashSet<string>> _knownMessageIds = new();

    public bool IsRunning => _backgroundTask != null && !_backgroundTask.IsCompleted;

    public SyncService(
        Database database,
        PeopleService peopleService,
        GmailService gmailService,
        DriveService driveService,
        IEventAggregator eventAggregator,
        AppConfig config)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _peopleService = peopleService ?? throw new ArgumentNullException(nameof(peopleService));
        _gmailService = gmailService ?? throw new ArgumentNullException(nameof(gmailService));
        _driveService = driveService ?? throw new ArgumentNullException(nameof(driveService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Starts the background periodic synchronization loop.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        _syncCts = new CancellationTokenSource();
        _backgroundTask = Task.Run(() => RunSyncLoopAsync(_syncCts.Token));
    }

    /// <summary>
    /// Stops the background periodic synchronization loop.
    /// </summary>
    public void Stop()
    {
        _syncCts?.Cancel();
        try
        {
            _backgroundTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Ignore cancellation wait exceptions
        }
        finally
        {
            _syncCts?.Dispose();
            _syncCts = null;
            _backgroundTask = null;
        }
    }

    /// <summary>
    /// Immediately triggers synchronization for all active accounts.
    /// </summary>
    public async Task SyncAllNowAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await _database.GetAllAccountsAsync();
        var activeAccounts = accounts.Where(a => a.State == AccountState.Active).ToList();

        foreach (var account in activeAccounts)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await SyncAccountAsync(account, isFirstRun: false, cancellationToken);
        }
    }

    /// <summary>
    /// Immediately synchronizes a single account.
    /// </summary>
    public async Task SyncAccountNowAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = await _database.GetAccountByIdAsync(accountId);
        if (account != null && account.State == AccountState.Active)
        {
            await SyncAccountAsync(account, isFirstRun: false, cancellationToken);
        }
    }

    private async Task RunSyncLoopAsync(CancellationToken cancellationToken)
    {
        // Initial run on startup
        try
        {
            var accounts = await _database.GetAllAccountsAsync();
            foreach (var account in accounts.Where(a => a.State == AccountState.Active))
            {
                await SyncAccountAsync(account, isFirstRun: true, cancellationToken);
            }
        }
        catch
        {
            // Ignore startup sync errors
        }

        var intervalMinutes = Math.Max(1, _config.SyncIntervalMinutes);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
                await SyncAllNowAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue loop on unexpected worker error
            }
        }
    }

    private async Task SyncAccountAsync(GoogleAccount account, bool isFirstRun, CancellationToken cancellationToken)
    {
        try
        {
            // 1. Sync Profile & Avatar (if missing or periodically)
            try
            {
                if (string.IsNullOrEmpty(account.DisplayName) || string.IsNullOrEmpty(account.AvatarLocalPath))
                {
                    account = await _peopleService.FetchProfileAsync(account.Id, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Sync", $"Profile sync failed for {account.Email}", ex);
            }

            // 2. Sync Storage Quota
            try
            {
                var quota = await _driveService.FetchStorageQuotaAsync(account.Id, cancellationToken);
                account.DriveUsedBytes = quota.UsedBytes;
                account.DriveTotalBytes = quota.TotalBytes;
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Sync", $"Drive quota sync failed for {account.Email}", ex);
            }

            // 3. Sync Gmail Unread Count & Messages
            try
            {
                var unreadCount = await _gmailService.GetUnreadCountAsync(account.Id, cancellationToken);
                account.UnreadCount = unreadCount;

                var messages = await _gmailService.FetchRecentInboxMessagesAsync(account.Id, 25, cancellationToken);

                var knownSet = _knownMessageIds.GetOrAdd(account.Id, _ => new HashSet<string>());

                if (!isFirstRun && knownSet.Count > 0)
                {
                    foreach (var msg in messages)
                    {
                        if (msg.IsUnread && !knownSet.Contains(msg.Id))
                        {
                            // New unread email detected!
                            _eventAggregator.Publish(new NewMailReceivedEvent(account.Id, account.Email, msg));
                        }
                    }
                }

                // Update known set
                foreach (var msg in messages)
                {
                    knownSet.Add(msg.Id);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Sync", $"Gmail sync failed for {account.Email}", ex);
            }

            // Update Account in Database
            account.LastSyncedAt = DateTimeOffset.UtcNow;
            await _database.UpdateAccountSyncStatsAsync(
                account.Id,
                account.UnreadCount,
                account.DriveUsedBytes,
                account.DriveTotalBytes,
                account.LastSyncedAt.Value);

            _eventAggregator.Publish(new SyncCompletedEvent(account.Id));
            _eventAggregator.Publish(new AccountUpdatedEvent(account));
        }
        catch (InvalidGrantException)
        {
            // TokenManager handles setting ActionNeeded
            _eventAggregator.Publish(new SyncFailedEvent(account.Id, "Session expired. Sign-in required."));
        }
        catch (Exception ex)
        {
            _eventAggregator.Publish(new SyncFailedEvent(account.Id, ex.Message));
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

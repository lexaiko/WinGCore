using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using GManager.Auth;
using GManager.Core;
using GManager.Models;
using Microsoft.Win32;

namespace GManager.Services;

/// <summary>
/// Event-driven background synchronization service.
/// Uses native OS events (Power Resume, Network Connectivity Restoration) and Push Notification triggers
/// instead of continuous aggressive polling, preserving battery life and system resources.
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
    private Task? _maintenanceTask;
    private readonly ConcurrentDictionary<string, HashSet<string>> _knownMessageIds = new();

    private CancellationTokenSource? _debounceCts;
    private readonly object _debounceLock = new();
    private bool _isDisposed;

    public bool IsRunning => _maintenanceTask != null && !_maintenanceTask.IsCompleted;

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
    /// Starts event-driven synchronization listeners and low-frequency idle maintenance.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        // 1. Hook native OS events for true event-driven reactivity
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        _syncCts = new CancellationTokenSource();
        _maintenanceTask = Task.Run(() => RunEventDrivenMaintenanceLoopAsync(_syncCts.Token));
    }

    /// <summary>
    /// Stops the service and unhooks all OS event listeners.
    /// </summary>
    public void Stop()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        _syncCts?.Cancel();
        try
        {
            _maintenanceTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Ignore cancellation wait exceptions
        }
        finally
        {
            _syncCts?.Dispose();
            _syncCts = null;
            _maintenanceTask = null;
        }
    }

    /// <summary>
    /// Debounces rapid network or system events before triggering full sync.
    /// </summary>
    private void TriggerDebouncedSync(int delayMs = 2500)
    {
        if (_isDisposed) return;

        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token);
                    if (!token.IsCancellationRequested)
                    {
                        AppLogger.Log("Sync", "Triggering event-driven sync after OS event.");
                        await SyncAllNowAsync(token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppLogger.LogError("Sync", "Event-driven sync failed", ex);
                }
            }, token);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            AppLogger.Log("Sync", "System resumed from sleep. Triggering immediate reconciliation.");
            TriggerDebouncedSync(1500);
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            AppLogger.Log("Sync", "Network connection restored. Triggering reconciliation.");
            TriggerDebouncedSync(3000);
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        AppLogger.Log("Sync", "Network interface address changed.");
        TriggerDebouncedSync(3000);
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
    /// Immediately synchronizes a single account (triggered on Push notification or user action).
    /// </summary>
    public async Task SyncAccountNowAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = await _database.GetAccountByIdAsync(accountId);
        if (account != null && account.State == AccountState.Active)
        {
            await SyncAccountAsync(account, isFirstRun: false, cancellationToken);
        }
    }

    /// <summary>
    /// Low-frequency idle maintenance loop (every 30 minutes) purely as a health check and quota refresh.
    /// Does not aggressively wake up the CPU every few minutes.
    /// </summary>
    private async Task RunEventDrivenMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        // 1. Initial sync on startup
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

        // 2. Idle maintenance timer: 30 minutes (battery-friendly fallback)
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));

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
                // Continue loop on unexpected maintenance error
            }
        }
    }

    private async Task SyncAccountAsync(GoogleAccount account, bool isFirstRun, CancellationToken cancellationToken)
    {
        try
        {
            // 1. Sync Profile & Avatar (if missing)
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
            _eventAggregator.Publish(new SyncFailedEvent(account.Id, "Session expired. Sign-in required."));
        }
        catch (Exception ex)
        {
            _eventAggregator.Publish(new SyncFailedEvent(account.Id, ex.Message));
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}

using CommunityToolkit.Mvvm.Input;
using GManager.Core;
using GManager.Helpers;
using GManager.Models;
using GManager.Services;

namespace GManager.ViewModels;

public sealed partial class DriveViewModel : ViewModelBase
{
    private readonly Database _database;
    private readonly DriveService _driveService;
    private readonly IEventAggregator _eventAggregator;

    private string? _currentAccountId;
    private string? _currentAccountEmail;

    private long _usedBytes;
    public long UsedBytes
    {
        get => _usedBytes;
        set
        {
            if (SetProperty(ref _usedBytes, value))
            {
                OnPropertyChanged(nameof(FormattedUsage));
                OnPropertyChanged(nameof(FormattedFree));
                OnPropertyChanged(nameof(UsagePercentage));
            }
        }
    }

    private long _totalBytes = 15L * 1024 * 1024 * 1024;
    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetProperty(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(FormattedUsage));
                OnPropertyChanged(nameof(FormattedFree));
                OnPropertyChanged(nameof(UsagePercentage));
            }
        }
    }

    public string FormattedUsage
    {
        get
        {
            double totalGb = TotalBytes / (1024.0 * 1024.0 * 1024.0);
            if (UsedBytes < 1024L * 1024 * 1024)
            {
                double usedMb = UsedBytes / (1024.0 * 1024.0);
                return $"{usedMb:F1} MB of {totalGb:F0} GB used";
            }
            double usedGb = UsedBytes / (1024.0 * 1024.0 * 1024.0);
            return $"{usedGb:F1} GB of {totalGb:F0} GB used";
        }
    }

    public string FormattedFree
    {
        get
        {
            long freeBytes = Math.Max(0, TotalBytes - UsedBytes);
            double freeGb = freeBytes / (1024.0 * 1024.0 * 1024.0);
            return $"{freeGb:F1} GB free";
        }
    }

    public double UsagePercentage
    {
        get
        {
            if (TotalBytes <= 0) return 0.0;
            return Math.Clamp((double)UsedBytes / TotalBytes * 100.0, 0.0, 100.0);
        }
    }

    public DriveViewModel(Database database, DriveService driveService, IEventAggregator eventAggregator)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _driveService = driveService ?? throw new ArgumentNullException(nameof(driveService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));

        _eventAggregator.Subscribe<NavigateToAccountEvent>(e =>
        {
            if (!string.IsNullOrEmpty(e.AccountId))
            {
                _ = LoadAccountStorageAsync(e.AccountId);
            }
        });

        _eventAggregator.Subscribe<SyncCompletedEvent>(e =>
        {
            if (e.AccountId == _currentAccountId)
            {
                _ = LoadAccountStorageAsync(e.AccountId);
            }
        });

        _eventAggregator.Subscribe<AccountUpdatedEvent>(e =>
        {
            if (e.Account.Id == _currentAccountId)
            {
                UsedBytes = e.Account.DriveUsedBytes;
                TotalBytes = e.Account.DriveTotalBytes > 0 ? e.Account.DriveTotalBytes : 15L * 1024 * 1024 * 1024;
            }
        });
    }

    public async Task LoadAccountStorageAsync(string accountId)
    {
        _currentAccountId = accountId;
        IsBusy = true;
        try
        {
            var account = await _database.GetAccountByIdAsync(accountId);
            if (account != null)
            {
                _currentAccountEmail = account.Email;
                UsedBytes = account.DriveUsedBytes;
                TotalBytes = account.DriveTotalBytes > 0 ? account.DriveTotalBytes : 15L * 1024 * 1024 * 1024;

                if (account.LastSyncedAt == null && account.State == AccountState.Active)
                {
                    _ = RefreshQuotaAsync();
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RefreshQuotaAsync()
    {
        if (string.IsNullOrEmpty(_currentAccountId)) return;

        IsBusy = true;
        try
        {
            var quota = await _driveService.FetchStorageQuotaAsync(_currentAccountId);
            UsedBytes = quota.UsedBytes;
            TotalBytes = quota.TotalBytes;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to refresh storage: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void OpenDriveInBrowser()
    {
        BrowserLauncher.OpenService(_currentAccountEmail, "drive");
    }

    [RelayCommand]
    public void OpenGoogleOneInBrowser()
    {
        var authUser = !string.IsNullOrEmpty(_currentAccountEmail) ? $"?authuser={Uri.EscapeDataString(_currentAccountEmail)}" : "";
        BrowserLauncher.OpenUrl($"https://one.google.com/storage{authUser}");
    }
}

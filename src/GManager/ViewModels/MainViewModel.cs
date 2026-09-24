using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GManager.Auth;
using GManager.Core;
using GManager.Helpers;
using GManager.Models;
using GManager.Services;

namespace GManager.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly Database _database;
    private readonly TokenManager _tokenManager;
    private readonly OAuthClient _oauthClient;
    private readonly PeopleService _peopleService;
    private readonly SyncService _syncService;
    private readonly IEventAggregator _eventAggregator;
    private readonly AppConfig _config;
    private readonly NativeRuntimeClient _native;
    private readonly NativeAccountCoordinator _nativeAccounts;

    public ObservableCollection<GoogleAccount> Accounts { get; } = [];

    private GoogleAccount? _activeAccount;
    public GoogleAccount? ActiveAccount
    {
        get => _activeAccount;
        set
        {
            if (SetProperty(ref _activeAccount, value))
            {
                OnPropertyChanged(nameof(HasActiveAccount));
                _eventAggregator.Publish(new NavigateToAccountEvent(value?.Id ?? string.Empty));
            }
        }
    }

    public bool HasActiveAccount => ActiveAccount != null;

    private bool _isAccountSwitcherOpen;
    public bool IsAccountSwitcherOpen
    {
        get => _isAccountSwitcherOpen;
        set => SetProperty(ref _isAccountSwitcherOpen, value);
    }

    private bool _isOAuthSetupDialogOpen;
    public bool IsOAuthSetupDialogOpen
    {
        get => _isOAuthSetupDialogOpen;
        set => SetProperty(ref _isOAuthSetupDialogOpen, value);
    }

    private string _setupClientId = string.Empty;
    public string SetupClientId
    {
        get => _setupClientId;
        set => SetProperty(ref _setupClientId, value);
    }

    private string _setupClientSecret = string.Empty;
    public string SetupClientSecret
    {
        get => _setupClientSecret;
        set => SetProperty(ref _setupClientSecret, value);
    }

    private int _totalUnreadCount;
    public int TotalUnreadCount
    {
        get => _totalUnreadCount;
        set => SetProperty(ref _totalUnreadCount, value);
    }

    public MainViewModel(
        Database database,
        TokenManager tokenManager,
        OAuthClient oauthClient,
        PeopleService peopleService,
        SyncService syncService,
        IEventAggregator eventAggregator,
        AppConfig config,
        NativeRuntimeClient native,
        NativeAccountCoordinator nativeAccounts)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
        _oauthClient = oauthClient ?? throw new ArgumentNullException(nameof(oauthClient));
        _peopleService = peopleService ?? throw new ArgumentNullException(nameof(peopleService));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _native = native;
        _nativeAccounts = nativeAccounts;

        SubscribeEvents();
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading accounts...";
        try
        {
            try { await _nativeAccounts.RefreshAsync(); }
            catch { StatusMessage = "Native runtime is unavailable. Open Devices to retry."; }
            await RefreshAccountsListAsync();

            if (Accounts.Count > 0 && ActiveAccount == null)
            {
                ActiveAccount = Accounts[0];
            }

            if (ActiveAccount != null && ActiveAccount.State == AccountState.Active)
            {
                _ = _syncService.SyncAccountNowAsync(ActiveAccount.Id);
            }
        }
        finally
        {
            IsBusy = false;
            if (StatusMessage == "Loading accounts...") StatusMessage = null;
        }
    }

    public async Task RefreshAccountsListAsync()
    {
        var list = await _database.GetAllAccountsAsync();
        Accounts.Clear();
        int unread = 0;
        foreach (var acc in list)
        {
            Accounts.Add(acc);
            unread += acc.UnreadCount;
        }
        TotalUnreadCount = unread;

        if (ActiveAccount != null)
        {
            ActiveAccount = Accounts.FirstOrDefault(a => a.Id == ActiveAccount.Id) ?? Accounts.FirstOrDefault();
        }
    }

    [RelayCommand]
    public void SelectAccount(GoogleAccount account)
    {
        if (account == null) return;
        ActiveAccount = account;
        IsAccountSwitcherOpen = false;
        if (account.State == AccountState.Active)
        {
            _ = _syncService.SyncAccountNowAsync(account.Id);
        }
    }

    [RelayCommand]
    public void ToggleAccountSwitcher()
    {
        IsAccountSwitcherOpen = !IsAccountSwitcherOpen;
    }

    [RelayCommand]
    public async Task AddNewAccountAsync()
    {
        await OpenDevicesAsync();
    }

    [RelayCommand]
    public async Task OpenDevicesAsync()
    {
        IsAccountSwitcherOpen = false;
        IsBusy = true;
        try
        {
            var window = new GManager.Views.NativeDevicesWindow(_native) { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
            await _nativeAccounts.RefreshAsync();
            await RefreshAccountsListAsync();
            if (window.SelectedSession is { } selected)
                ActiveAccount = Accounts.FirstOrDefault(x => x.Id == NativeAccountCoordinator.LocalId(selected.Id));
            else if (ActiveAccount == null && Accounts.Count > 0)
                ActiveAccount = Accounts[0];

            if (ActiveAccount != null && ActiveAccount.State == AccountState.Active)
            {
                _ = _syncService.SyncAccountNowAsync(ActiveAccount.Id);
            }
            StatusMessage = "Native sessions refreshed.";
        }
        catch (Exception) { StatusMessage = "Could not refresh native sessions. Open Devices to retry."; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task AddLegacyAccountAsync()
    {
        IsAccountSwitcherOpen = false;
        IsBusy = true;
        StatusMessage = "Opening Google Sign-In in browser...";
        AppLogger.Log("Auth", "AddNewAccountAsync started.");

        try
        {
            StatusMessage = "Waiting for Google Sign-In...";
            var tokens = await _oauthClient.LoginAsync();
            AppLogger.Log("Auth", "LoginAsync completed successfully.");

            StatusMessage = "Signed in! Loading your profile...";

            // 1. Try extracting profile directly from OIDC id_token JWT (instant, reliable)
            var claims = tokens.ParseIdToken();
            var accountId = claims?.Sub;
            var email = claims?.Email;

            // If id_token didn't provide email/sub, fallback to userinfo endpoint with access token
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(email))
            {
                AppLogger.Log("Auth", "id_token missing sub or email, fetching via UserInfo API...");
                var userInfo = await _peopleService.FetchUserInfoDirectAsync(tokens.AccessToken);
                accountId = userInfo.Sub;
                email = userInfo.Email;
            }

            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(email))
            {
                throw new InvalidOperationException("Could not obtain user identity (sub/email) from Google.");
            }

            AppLogger.Log("Auth", $"Identity confirmed: {email} (ID: {accountId})");

            // 2. Securely store tokens under the user's permanent Google ID and Email
            _tokenManager.StoreTokens(accountId, email, tokens);

            // 3. Upsert account profile into database and download avatar
            var profile = await _peopleService.EnsureProfileLoadedAsync(accountId, email, claims);

            StatusMessage = $"Welcome, {profile.GivenName ?? profile.DisplayName}!";
            await RefreshAccountsListAsync();
            ActiveAccount = Accounts.FirstOrDefault(a => a.Id == profile.Id);
            IsAccountSwitcherOpen = false;

            AppLogger.Log("Auth", $"Account {email} successfully added and activated!");

            // 4. Trigger background sync for the new account
            if (ActiveAccount != null)
            {
                _ = _syncService.SyncAccountNowAsync(ActiveAccount.Id);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Auth", "AddNewAccountAsync failed", ex);
            StatusMessage = $"Sign in failed: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"Google Sign-In failed:\n\n{ex.Message}\n\nCheck gmanager.log in %LOCALAPPDATA%\\GManager for details.",
                "GManager Sign-In Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SaveOAuthSetupAndLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(SetupClientId)) return;

        _config.ClientId = SetupClientId.Trim();
        _config.ClientSecret = SetupClientSecret?.Trim() ?? string.Empty;
        _config.SaveConfig();

        IsOAuthSetupDialogOpen = false;
        await AddLegacyAccountAsync();
    }

    [RelayCommand]
    public void CloseOAuthSetupDialog()
    {
        IsOAuthSetupDialogOpen = false;
    }

    [RelayCommand]
    public void OpenGoogleCloudConsole()
    {
        BrowserLauncher.OpenUrl("https://console.cloud.google.com/apis/credentials");
    }

    [RelayCommand]
    public async Task RemoveAccountAsync(GoogleAccount? account)
    {
        var target = account ?? ActiveAccount;
        if (target == null) return;

        IsBusy = true;
        try
        {
            await _tokenManager.RemoveAccountAsync(target.Id);
            await RefreshAccountsListAsync();
            if (ActiveAccount?.Id == target.Id)
            {
                ActiveAccount = Accounts.FirstOrDefault();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SyncCurrentAccountAsync()
    {
        if (ActiveAccount == null) return;
        IsBusy = true;
        StatusMessage = "Synchronizing account...";
        try
        {
            await _syncService.SyncAccountNowAsync(ActiveAccount.Id);
        }
        finally
        {
            IsBusy = false;
            StatusMessage = null;
        }
    }

    [RelayCommand]
    public void OpenInBrowser(string? service)
    {
        BrowserLauncher.OpenService(ActiveAccount?.Email, service ?? "myaccount");
    }

    private void SubscribeEvents()
    {
        _eventAggregator.Subscribe<AccountUpdatedEvent>(evt =>
        {
            _ = RefreshAccountsListAsync();
        });

        _eventAggregator.Subscribe<AccountRemovedEvent>(evt =>
        {
            _ = RefreshAccountsListAsync();
        });

        _eventAggregator.Subscribe<NewMailReceivedEvent>(e =>
        {
            var acc = Accounts.FirstOrDefault(a => a.Id == e.AccountId);
            if (acc != null)
            {
                NotificationHelper.ShowNewMailToast(acc, e.Message);
            }
        });
    }
}

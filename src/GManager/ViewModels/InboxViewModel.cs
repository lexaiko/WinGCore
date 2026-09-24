using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GManager.Core;
using GManager.Helpers;
using GManager.Models;
using GManager.Services;

namespace GManager.ViewModels;

public sealed partial class InboxViewModel : ViewModelBase
{
    private readonly Database _database;
    private readonly GmailService _gmailService;
    private readonly IEventAggregator _eventAggregator;

    private string? _currentAccountId;
    private string? _currentAccountEmail;
    private List<MailMessage> _allMessages = [];

    public ObservableCollection<MailMessage> FilteredMessages { get; } = [];

    private MailMessage? _selectedMessage;
    public MailMessage? SelectedMessage
    {
        get => _selectedMessage;
        set => SetProperty(ref _selectedMessage, value);
    }

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilter();
            }
        }
    }

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetProperty(ref _isRefreshing, value);
    }

    public InboxViewModel(Database database, GmailService gmailService, IEventAggregator eventAggregator)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _gmailService = gmailService ?? throw new ArgumentNullException(nameof(gmailService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));

        _eventAggregator.Subscribe<NavigateToAccountEvent>(e =>
        {
            if (!string.IsNullOrEmpty(e.AccountId))
            {
                _ = LoadAccountMessagesAsync(e.AccountId);
            }
        });

        _eventAggregator.Subscribe<SyncCompletedEvent>(e =>
        {
            if (e.AccountId == _currentAccountId)
            {
                _ = LoadAccountMessagesAsync(e.AccountId, isBackground: true);
            }
        });
    }

    public async Task LoadAccountMessagesAsync(string accountId, bool isBackground = false)
    {
        _currentAccountId = accountId;

        if (!isBackground)
        {
            IsBusy = true;
        }

        try
        {
            var account = await _database.GetAccountByIdAsync(accountId);
            _currentAccountEmail = account?.Email;

            _allMessages = await _database.GetCachedMessagesAsync(accountId, limit: 50);
            ApplyFilter();

            if (!isBackground && _allMessages.Count == 0 && account?.State == AccountState.Active)
            {
                _ = RefreshAsync();
            }
        }
        finally
        {
            if (!isBackground)
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_currentAccountId)) return;

        IsRefreshing = true;
        try
        {
            var newMessages = await _gmailService.FetchRecentInboxMessagesAsync(_currentAccountId, maxResults: 25);
            _allMessages = await _database.GetCachedMessagesAsync(_currentAccountId, limit: 50);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to fetch emails: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    public async Task MarkAsReadAsync(MailMessage? message)
    {
        var target = message ?? SelectedMessage;
        if (target == null || string.IsNullOrEmpty(_currentAccountId)) return;

        target.IsUnread = false;
        await _gmailService.MarkAsReadAsync(_currentAccountId, target.Id);
        ApplyFilter();
    }

    [RelayCommand]
    public void OpenInBrowser(MailMessage? message)
    {
        var target = message ?? SelectedMessage;
        if (target == null)
        {
            BrowserLauncher.OpenService(_currentAccountEmail, "gmail");
            return;
        }

        BrowserLauncher.OpenMail(_currentAccountEmail, target.ThreadId);
    }

    private void ApplyFilter()
    {
        FilteredMessages.Clear();

        var query = SearchQuery?.Trim();
        IEnumerable<MailMessage> source = _allMessages;

        if (!string.IsNullOrEmpty(query))
        {
            source = source.Where(m =>
                (m.Subject?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.SenderName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.SenderEmail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.Snippet?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        foreach (var msg in source)
        {
            FilteredMessages.Add(msg);
        }
    }
}

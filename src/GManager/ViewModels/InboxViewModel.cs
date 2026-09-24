using System.Collections.ObjectModel;
using System.IO;
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
        set
        {
            if (SetProperty(ref _selectedMessage, value))
            {
                OnPropertyChanged(nameof(HasSelectedMessage));
                IsReplying = false;
                ReplyText = string.Empty;
                if (value != null)
                {
                    _ = LoadMessageDetailAsync(value);
                }
            }
        }
    }

    public bool HasSelectedMessage => SelectedMessage != null;

    private bool _isLoadingDetail;
    public bool IsLoadingDetail
    {
        get => _isLoadingDetail;
        set => SetProperty(ref _isLoadingDetail, value);
    }

    private string _activeFilter = "All";
    public string ActiveFilter
    {
        get => _activeFilter;
        set
        {
            if (SetProperty(ref _activeFilter, value))
            {
                ApplyFilter();
                OnPropertyChanged(nameof(IsAllFilter));
                OnPropertyChanged(nameof(IsUnreadFilter));
                OnPropertyChanged(nameof(IsStarredFilter));
            }
        }
    }

    public bool IsAllFilter => ActiveFilter == "All";
    public bool IsUnreadFilter => ActiveFilter == "Unread";
    public bool IsStarredFilter => ActiveFilter == "Starred";

    [RelayCommand]
    public void SetFilter(string filter) => ActiveFilter = filter;


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

    private bool _isReplying;
    public bool IsReplying
    {
        get => _isReplying;
        set => SetProperty(ref _isReplying, value);
    }

    private string _replyText = string.Empty;
    public string ReplyText
    {
        get => _replyText;
        set => SetProperty(ref _replyText, value);
    }

    private bool _isSendingReply;
    public bool IsSendingReply
    {
        get => _isSendingReply;
        set => SetProperty(ref _isSendingReply, value);
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

    public async Task LoadMessageDetailAsync(MailMessage message)
    {
        if (string.IsNullOrEmpty(_currentAccountId)) return;

        if (message.IsUnread)
        {
            _ = MarkAsReadAsync(message);
        }

        if (message.HasFullBody) return;

        IsLoadingDetail = true;
        try
        {
            var detail = await _gmailService.GetMessageDetailAsync(_currentAccountId, message.Id);
            if (detail != null && SelectedMessage?.Id == message.Id)
            {
                SelectedMessage.BodyText = detail.BodyText;
                SelectedMessage.BodyHtml = detail.BodyHtml;
                SelectedMessage.RecipientTo = detail.RecipientTo;
                SelectedMessage.RecipientCc = detail.RecipientCc;
                SelectedMessage.Attachments = detail.Attachments;
                SelectedMessage.HasFullBody = true;
                OnPropertyChanged(nameof(SelectedMessage));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load message body: {ex.Message}";
        }
        finally
        {
            IsLoadingDetail = false;
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
        OnPropertyChanged(nameof(SelectedMessage));
        await _gmailService.MarkAsReadAsync(_currentAccountId, target.Id);
        ApplyFilter();
    }

    [RelayCommand]
    public async Task ToggleStarAsync(MailMessage? message)
    {
        var target = message ?? SelectedMessage;
        if (target == null || string.IsNullOrEmpty(_currentAccountId)) return;

        var newState = !target.IsStarred;
        target.IsStarred = newState;
        OnPropertyChanged(nameof(SelectedMessage));
        ApplyFilter();

        try
        {
            await _gmailService.ToggleStarAsync(_currentAccountId, target.Id, newState);
        }
        catch (Exception ex)
        {
            target.IsStarred = !newState;
            StatusMessage = $"Failed to update star: {ex.Message}";
            OnPropertyChanged(nameof(SelectedMessage));
        }
    }

    [RelayCommand]
    public async Task TrashMessageAsync(MailMessage? message)
    {
        var target = message ?? SelectedMessage;
        if (target == null || string.IsNullOrEmpty(_currentAccountId)) return;

        _allMessages.Remove(target);
        if (SelectedMessage?.Id == target.Id)
        {
            SelectedMessage = null;
        }
        ApplyFilter();

        try
        {
            await _gmailService.TrashMessageAsync(_currentAccountId, target.Id);
            StatusMessage = "Message moved to trash.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to trash message: {ex.Message}";
        }
    }

    [RelayCommand]
    public void StartReply()
    {
        IsReplying = true;
        ReplyText = string.Empty;
    }

    [RelayCommand]
    public void CancelReply()
    {
        IsReplying = false;
        ReplyText = string.Empty;
    }

    [RelayCommand]
    public async Task SendReplyAsync()
    {
        if (SelectedMessage == null || string.IsNullOrEmpty(_currentAccountId) || string.IsNullOrWhiteSpace(ReplyText))
            return;

        IsSendingReply = true;
        try
        {
            var to = !string.IsNullOrWhiteSpace(SelectedMessage.SenderEmail) ? SelectedMessage.SenderEmail : SelectedMessage.SenderName;
            await _gmailService.SendReplyAsync(
                _currentAccountId,
                SelectedMessage.ThreadId,
                SelectedMessage.Id,
                to,
                SelectedMessage.Subject,
                ReplyText);

            StatusMessage = "Reply sent successfully.";
            IsReplying = false;
            ReplyText = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to send reply: {ex.Message}";
        }
        finally
        {
            IsSendingReply = false;
        }
    }

    [RelayCommand]
    public async Task DownloadAttachmentAsync(MailAttachment? attachment)
    {
        if (attachment == null || SelectedMessage == null || string.IsNullOrEmpty(_currentAccountId)) return;

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = attachment.Filename,
            Filter = "All Files (*.*)|*.*",
            Title = "Save Attachment"
        };

        if (saveDialog.ShowDialog() == true)
        {
            try
            {
                StatusMessage = $"Downloading {attachment.Filename}...";
                var bytes = await _gmailService.DownloadAttachmentAsync(
                    _currentAccountId,
                    SelectedMessage.Id,
                    attachment.AttachmentId);

                await File.WriteAllBytesAsync(saveDialog.FileName, bytes);
                StatusMessage = $"Saved {attachment.Filename} successfully.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Download failed: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    public void CloseDetail()
    {
        SelectedMessage = null;
        IsReplying = false;
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

        if (ActiveFilter == "Unread")
        {
            source = source.Where(m => m.IsUnread);
        }
        else if (ActiveFilter == "Starred")
        {
            source = source.Where(m => m.IsStarred);
        }

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


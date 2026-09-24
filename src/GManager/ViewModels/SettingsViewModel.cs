using CommunityToolkit.Mvvm.Input;
using GManager.Core;
using GManager.Helpers;
using Wpf.Ui.Appearance;

namespace GManager.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppConfig _config;

    private string _clientId = string.Empty;
    public string ClientId
    {
        get => _clientId;
        set => SetProperty(ref _clientId, value);
    }

    private string _clientSecret = string.Empty;
    public string ClientSecret
    {
        get => _clientSecret;
        set => SetProperty(ref _clientSecret, value);
    }

    private int _syncIntervalMinutes = 5;
    public int SyncIntervalMinutes
    {
        get => _syncIntervalMinutes;
        set => SetProperty(ref _syncIntervalMinutes, value);
    }

    private bool _autoStartEnabled;
    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        set => SetProperty(ref _autoStartEnabled, value);
    }

    private string _selectedTheme = "System";
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                ApplyTheme(value);
            }
        }
    }

    private bool _saveSuccess;
    public bool SaveSuccess
    {
        get => _saveSuccess;
        set => SetProperty(ref _saveSuccess, value);
    }

    public SettingsViewModel(AppConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        LoadSettings();
    }

    public void LoadSettings()
    {
        ClientId = _config.ClientId;
        ClientSecret = _config.ClientSecret;
        SyncIntervalMinutes = _config.SyncIntervalMinutes;
        AutoStartEnabled = AutoStartHelper.IsEnabled();
        SelectedTheme = _config.ThemeMode;
    }

    [RelayCommand]
    public void SaveSettings()
    {
        _config.ClientId = ClientId.Trim();
        _config.ClientSecret = ClientSecret.Trim();
        _config.SyncIntervalMinutes = Math.Clamp(SyncIntervalMinutes, 1, 60);
        _config.AutoStartEnabled = AutoStartEnabled;
        _config.ThemeMode = SelectedTheme;

        _config.SaveConfig();
        AutoStartHelper.SetEnabled(AutoStartEnabled);

        SaveSuccess = true;
        StatusMessage = "Settings saved successfully.";
    }

    private static void ApplyTheme(string theme)
    {
        try
        {
            var applicationTheme = theme switch
            {
                "Light" => ApplicationTheme.Light,
                "Dark" => ApplicationTheme.Dark,
                _ => ApplicationTheme.Unknown
            };

            if (applicationTheme != ApplicationTheme.Unknown)
            {
                ApplicationThemeManager.Apply(applicationTheme);
            }
        }
        catch
        {
            // Ignore during design-time or test runner
        }
    }
}

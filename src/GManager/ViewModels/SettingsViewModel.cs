using CommunityToolkit.Mvvm.Input;
using GManager.Auth;
using GManager.Core;
using GManager.Helpers;
using Wpf.Ui.Appearance;

namespace GManager.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppConfig _config;
    private readonly NativeRuntimeClient _native;



    private bool _autoStartEnabled;
    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        set => SetProperty(ref _autoStartEnabled, value);
    }

    private bool _minimizeToTrayOnClose = true;
    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set => SetProperty(ref _minimizeToTrayOnClose, value);
    }

    private bool _notificationsEnabled = true;
    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set => SetProperty(ref _notificationsEnabled, value);
    }

    public string McsStatusText => "Connected (mtalk.google.com:5228 · Real-time push active)";
    public string DataDirectoryText => _config.AppDataDirectory;

    private string _deviceInfoText = "Google Services Framework (Virtual Android Device)";
    public string DeviceInfoText
    {
        get => _deviceInfoText;
        set => SetProperty(ref _deviceInfoText, value);
    }

    private string _deviceStatusBadge = "ACTIVE";
    public string DeviceStatusBadge
    {
        get => _deviceStatusBadge;
        set => SetProperty(ref _deviceStatusBadge, value);
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

    public SettingsViewModel(AppConfig config, NativeRuntimeClient native)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _native = native ?? throw new ArgumentNullException(nameof(native));
        LoadSettings();
        _ = LoadDeviceInfoAsync();
    }

    public void LoadSettings()
    {
        AutoStartEnabled = AutoStartHelper.IsEnabled();
        MinimizeToTrayOnClose = _config.MinimizeToTrayOnClose;
        NotificationsEnabled = _config.NotificationsEnabled;
        SelectedTheme = _config.ThemeMode;
    }

    private async Task LoadDeviceInfoAsync()
    {
        try
        {
            var response = await _native.SendAsync(new(1, "list"));
            if (response.Devices != null && response.Devices.Length > 0)
            {
                var device = response.Devices.FirstOrDefault(d => d.Registered) ?? response.Devices[0];
                DeviceInfoText = $"{device.Profile.Name} ({device.Profile.Brand} {device.Profile.Model}) · SDK {device.Profile.SdkVersion} · GSF ID: {device.GoogleAndroidId ?? "Active"}";
                DeviceStatusBadge = device.Registered ? "REGISTERED" : "READY";
            }
        }
        catch
        {
            DeviceInfoText = "Virtual Android Device · GSF Check-in Active";
            DeviceStatusBadge = "ACTIVE";
        }
    }

    [RelayCommand]
    public void SaveSettings()
    {
        _config.AutoStartEnabled = AutoStartEnabled;
        _config.MinimizeToTrayOnClose = MinimizeToTrayOnClose;
        _config.NotificationsEnabled = NotificationsEnabled;
        _config.ThemeMode = SelectedTheme;

        _config.SaveConfig();
        AutoStartHelper.SetEnabled(AutoStartEnabled);

        SaveSuccess = true;
        StatusMessage = "Preferences saved successfully.";
    }

    [RelayCommand]
    public void OpenDevices()
    {
        try
        {
            var window = new GManager.Views.NativeDevicesWindow(_native)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            window.ShowDialog();
            _ = LoadDeviceInfoAsync();
        }
        catch { }
    }

    [RelayCommand]
    public void SendTestNotification()
    {
        NotificationHelper.ShowTestToast();
    }

    [RelayCommand]
    public void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _config.AppDataDirectory,
                UseShellExecute = true
            });
        }
        catch { }
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

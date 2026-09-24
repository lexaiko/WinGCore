using System.Windows;
using GManager.ViewModels;
using GManager.Views.Pages;
using Wpf.Ui.Controls;

namespace GManager;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _mainViewModel;
    private readonly InboxPage _inboxPage;
    private readonly DrivePage _drivePage;
    private readonly SettingsPage _settingsPage;

    public MainWindow(
        MainViewModel mainViewModel,
        InboxPage inboxPage,
        DrivePage drivePage,
        SettingsPage settingsPage)
    {
        InitializeComponent();

        _mainViewModel = mainViewModel;
        _inboxPage = inboxPage;
        _drivePage = drivePage;
        _settingsPage = settingsPage;

        DataContext = _mainViewModel;

        // Set default page
        PageContentHost.Content = _inboxPage;

        Loaded += OnLoaded;
        Closing += (s, e) => LogStep($"MainWindow.Closing: Cancel={e.Cancel}");
        Closed += (s, e) => LogStep("MainWindow.Closed");
    }

    private static void LogStep(string msg)
    {
        try
        {
            var logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GManager", "startup.log");
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LogStep("Entering MainWindow.OnLoaded");
        try
        {
            await _mainViewModel.InitializeAsync();
            LogStep("MainWindow.OnLoaded finished successfully");
        }
        catch (Exception ex)
        {
            LogStep($"MainWindow.OnLoaded exception: {ex}");
        }
    }

    private void OnNavInboxClicked(object sender, RoutedEventArgs e)
    {
        PageContentHost.Content = _inboxPage;
    }

    private void OnNavStorageClicked(object sender, RoutedEventArgs e)
    {
        PageContentHost.Content = _drivePage;
    }

    private void OnNavSettingsClicked(object sender, RoutedEventArgs e)
    {
        PageContentHost.Content = _settingsPage;
    }
}

using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using GManager.Auth;
using GManager.Core;
using GManager.Helpers;
using GManager.Services;
using GManager.ViewModels;
using GManager.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Uwp.Notifications;
using Wpf.Ui.Appearance;

namespace GManager;

/// <summary>
/// Application entry point with Dependency Injection, Single Instance enforcement, and Toast lifecycle handling.
/// </summary>
public partial class App : Application
{
    private IServiceProvider? _serviceProvider;
    private SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GManager", "startup.log");
        void LogStep(string msg)
        {
            try
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }

        LogStep("Entering OnStartup");

        // Register gmanager:// as a Windows URL scheme (iOS-style protocol handler)
        RegistryProtocolHelper.Register();
        LogStep("Protocol handler registered");

        // If this process was launched BY Windows as a protocol handler (OAuth callback),
        // forward the URI to the running instance and exit immediately — do not show a window.
        if (RegistryProtocolHelper.IsProtocolActivation(e.Args, out var callbackUri))
        {
            LogStep($"Protocol activation detected: {callbackUri}");
            SingleInstance.SendMessageToExistingInstanceAsync($"OAUTH:{callbackUri}").GetAwaiter().GetResult();
            Environment.Exit(0);
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Global unhandled exception handler
        DispatcherUnhandledException += (sender, args) =>
        {
            LogStep($"DispatcherUnhandledException: {args.Exception}");
            var crashPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GManager", "crash.log");
            try
            {
                File.AppendAllText(crashPath, $"{DateTime.Now:O}\n{args.Exception}\n\n");
            }
            catch { }

            MessageBox.Show($"An unexpected error occurred:\n\n{args.Exception.Message}\n\nDetails saved to crash.log.", "GManager Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            LogStep($"AppDomain UnhandledException: {args.ExceptionObject}");
        };

        // 1. Single Instance Check via Mutex & Named Pipe
        LogStep("Checking SingleInstance");
        _singleInstance = new SingleInstance();
        LogStep($"IsFirstInstance = {_singleInstance.IsFirstInstance}");
        if (!_singleInstance.IsFirstInstance)
        {
            LogStep("Not first instance. Sending ACTIVATE to existing instance...");
            bool sent = false;
            try
            {
                sent = SingleInstance.SendMessageToExistingInstanceAsync("ACTIVATE").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogStep($"Error sending ACTIVATE: {ex.Message}");
            }

            if (sent)
            {
                LogStep("ACTIVATE sent successfully. Exiting secondary process.");
                Environment.Exit(0);
                return;
            }
            else
            {
                LogStep("Existing instance did not respond on named pipe (stuck or zombie). Continuing launch as primary!");
            }
        }

        try
        {
            _singleInstance.StartIpcServer();
            _singleInstance.MessageReceived += msg =>
            {
                LogStep($"IpcServer MessageReceived: {msg}");

                // iOS-style OAuth callback routed back from the second (protocol-activated) instance
                if (msg.StartsWith("OAUTH:", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = msg["OAUTH:".Length..];
                    LogStep($"Delivering OAuth URI to ProtocolActivationHandler: {uri}");
                    ProtocolActivationHandler.DeliverUri(uri);
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    if (MainWindow != null)
                    {
                        if (MainWindow.WindowState == WindowState.Minimized)
                        {
                            MainWindow.WindowState = WindowState.Normal;
                        }
                        MainWindow.Show();
                        MainWindow.Activate();
                        MainWindow.Focus();
                    }
                });
            };
        }
        catch (Exception ex)
        {
            LogStep($"StartIpcServer warning: {ex.Message}");
        }

        // 2. Build Dependency Injection Container
        LogStep("Building DI container");
        var services = new ServiceCollection();
        ConfigureServices(services, _singleInstance);
        _serviceProvider = services.BuildServiceProvider();

        // 3. Initialize SQLite Database & Migrations
        LogStep("Initializing SQLite Database");
        var database = _serviceProvider.GetRequiredService<Database>();
        database.Initialize();

        // 4. Configure Toast Notification Deep Linking
        LogStep("Configuring Toast Notifications");
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        }
        catch (Exception ex)
        {
            LogStep($"Toast notification config failed: {ex.Message}");
        }

        // 5. Apply Configured Theme
        LogStep("Applying App Theme");
        var config = _serviceProvider.GetRequiredService<AppConfig>();
        ApplyAppTheme(config.ThemeMode);

        // 6. Start Background Synchronization & Push Notification Coordinator
        LogStep("Starting SyncService");
        var syncService = _serviceProvider.GetRequiredService<SyncService>();
        syncService.Start();

        LogStep("Starting McsPushCoordinator");
        var mcsCoordinator = _serviceProvider.GetRequiredService<McsPushCoordinator>();
        _ = mcsCoordinator.StartAsync();

        // 7. Launch Main Window & System Tray
        LogStep("Resolving MainWindow from DI");
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;

        LogStep("Initializing SystemTrayService");
        var trayService = _serviceProvider.GetRequiredService<SystemTrayService>();
        trayService.Initialize(mainWindow);

        bool startMinimized = e.Args.Contains("--minimized");
        LogStep($"startMinimized = {startMinimized}");
        if (!startMinimized)
        {
            LogStep("Calling mainWindow.Show()");
            mainWindow.Show();
            var handle = new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
            LogStep($"mainWindow.Show() returned. Handle={handle}, Visibility={mainWindow.Visibility}, IsVisible={mainWindow.IsVisible}, WindowState={mainWindow.WindowState}, Left={mainWindow.Left}, Top={mainWindow.Top}, Width={mainWindow.ActualWidth}, Height={mainWindow.ActualHeight}");
        }
        LogStep("OnStartup completed successfully");
    }

    private static void ConfigureServices(IServiceCollection services, SingleInstance singleInstance)
    {
        // Core Singletons
        services.AddSingleton<AppConfig>();
        services.AddSingleton<ICredentialStore, CredentialStore>();
        services.AddSingleton<IEncryptionService, EncryptionService>();
        services.AddSingleton(sp => new Database(sp.GetRequiredService<AppConfig>().DatabasePath, sp.GetRequiredService<IEncryptionService>()));
        services.AddSingleton<IEventAggregator, EventAggregator>();
        services.AddSingleton(singleInstance);

        // Network & Auth
        services.AddSingleton(sp =>
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseProxy = false, // Critical: bypasses Windows WPAD proxy search delay
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                EnableMultipleHttp2Connections = true
            };
            return new HttpClient(handler);
        });
        services.AddSingleton<OAuthClient>();
        services.AddSingleton<TokenManager>();
        services.AddSingleton<NativeRuntimeClient>();
        services.AddSingleton<NativeAccountCoordinator>();

        // Google Services
        services.AddSingleton<PeopleService>();
        services.AddSingleton<GmailService>();
        services.AddSingleton<DriveService>();
        services.AddSingleton<SyncService>();
        services.AddSingleton<McsPushCoordinator>();
        services.AddSingleton<SystemTrayService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<InboxViewModel>();
        services.AddSingleton<DriveViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // Views & Pages
        services.AddSingleton<InboxPage>();
        services.AddSingleton<DrivePage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<MainWindow>();
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat toastArgs)
    {
        Dispatcher.Invoke(() =>
        {
            var args = ToastArguments.Parse(toastArgs.Argument);

            if (args.TryGetValue("action", out var action))
            {
                if (action == "open_browser" && args.TryGetValue("email", out var email))
                {
                    args.TryGetValue("threadId", out var threadId);
                    BrowserLauncher.OpenMail(email, threadId);
                }
                else if (action == "view_message" && args.TryGetValue("accountId", out var accountId))
                {
                    var tray = _serviceProvider?.GetService<SystemTrayService>();
                    tray?.RestoreMainWindow();

                    if (MainWindow != null)
                    {
                        if (MainWindow.WindowState == WindowState.Minimized)
                        {
                            MainWindow.WindowState = WindowState.Normal;
                        }
                        MainWindow.Show();
                        MainWindow.Activate();

                        var eventBus = _serviceProvider?.GetService<IEventAggregator>();
                        args.TryGetValue("messageId", out var msgId);
                        eventBus?.Publish(new NavigateToAccountEvent(accountId, msgId));
                    }
                }
                else if (action == "test_toast")
                {
                    var tray = _serviceProvider?.GetService<SystemTrayService>();
                    tray?.RestoreMainWindow();
                }
            }
        });
    }

    private static void ApplyAppTheme(string theme)
    {
        try
        {
            var appTheme = theme switch
            {
                "Light" => ApplicationTheme.Light,
                "Dark" => ApplicationTheme.Dark,
                _ => ApplicationTheme.Unknown
            };

            if (appTheme != ApplicationTheme.Unknown)
            {
                ApplicationThemeManager.Apply(appTheme);
            }
        }
        catch
        {
            // Ignore during startup on unsupported visual styles
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GManager", "startup.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] Entering App.OnExit with code: {e.ApplicationExitCode}\n");
        }
        catch { }

        try
        {
            ToastNotificationManagerCompat.Uninstall();
        }
        catch { }

        if (_serviceProvider != null)
        {
            var sync = _serviceProvider.GetService<SyncService>();
            sync?.Stop();

            var tray = _serviceProvider.GetService<SystemTrayService>();
            tray?.Dispose();

            var mcs = _serviceProvider.GetService<McsPushCoordinator>();
            mcs?.Dispose();

            var db = _serviceProvider.GetService<Database>();
            db?.Dispose();
        }

        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}

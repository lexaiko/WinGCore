using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using GManager.Auth;
using GManager.Contracts;
using Microsoft.Web.WebView2.Core;

namespace GManager.Views;

public partial class NativeLoginWindow : Window
{
    private readonly NativeRuntimeClient _client;
    private readonly DeviceSummary _device;
    private readonly CancellationTokenSource _lifetime = new();
    private NativeLoginTicket? _ticket;
    private bool _finishing;
    public NativeSessionSummary? Session { get; private set; }

    public NativeLoginWindow(NativeRuntimeClient client, DeviceSummary device)
    {
        InitializeComponent();
        _client = client;
        _device = device;
        Loaded += Initialize;
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            Browser.Dispose();
            if (_ticket is not null) _ = CancelTicketAsync(_ticket.Id);
        };
    }

    private async Task CancelTicketAsync(string id)
    {
        try { await _client.SendAsync(new(1, "cancel-login", LoginTicket: id)); }
        catch { /* Closing a login window must not bring down the application. */ }
    }

    private async void Initialize(object sender, RoutedEventArgs e)
    {
        try
        {
            _ticket = (await _client.SendAsync(new(1, "begin-login", DeviceId: _device.Id), _lifetime.Token)).Login!;
            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-quic --winhttp-proxy-resolver"
            };
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(_client.DataDirectory, "LoginBrowser"),
                options: envOptions);
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "NativeLogin";
            await Browser.EnsureCoreWebView2Async(environment, options);
            if (_lifetime.IsCancellationRequested) return;
            var core = Browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            var androidVer = _device.Profile.AndroidRelease;
            var model = string.IsNullOrWhiteSpace(_device.Profile.Model) ? "Pixel 9 Pro XL" : _device.Profile.Model;
            core.Settings.UserAgent = $"Mozilla/5.0 (Linux; Android {androidVer}; {model}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36 MinuteMaid";
            core.AddWebResourceRequestedFilter("https://accounts.google.com/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                args.Request.Headers.SetHeader("sec-ch-ua-platform", "\"Android\"");
                args.Request.Headers.SetHeader("sec-ch-ua-model", $"\"{model}\"");
                args.Request.Headers.SetHeader("sec-ch-ua-mobile", "?1");
            };
            core.PermissionRequested += (_, args) =>
            {
                if (args.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Geolocation)
                {
                    args.State = CoreWebView2PermissionState.Deny;
                }
                else
                {
                    args.State = CoreWebView2PermissionState.Allow;
                }
            };
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) =>
            {
                if (IsGoogleLoginOrigin(args.Uri))
                {
                    args.Handled = true;
                    core.Navigate(args.Uri);
                }
                else
                {
                    args.Handled = false;
                }
            };
            core.NavigationStarting += (_, args) =>
            {
                if (!IsGoogleLoginOrigin(args.Uri))
                {
                    args.Cancel = true;
                    Status.Text = "This sign-in requires an unsupported external page. Close this window and choose another sign-in method.";
                }
            };
            core.WebMessageReceived += async (_, args) =>
            {
                if (!IsGoogleLoginOrigin(args.Source) || !IsGoogleLoginOrigin(core.Source)) return;
                try
                {
                    var message = args.TryGetWebMessageAsString();
                    if (message == "finish") await CompleteAsync();
                    else if (message == "cancel") Close();
                    else if (message == "unsupported") Status.Text = "Google requested an Android-specific challenge. Try another verification method; device attestation is not available here.";
                }
                catch (ArgumentException) { }
            };
            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess) { Status.Text = "Google sign-in could not load. Check your connection and reopen sign-in."; return; }
                if (IsLoginCompletionUri(core.Source))
                    await CompleteAsync();
            };
            // Google can finish by changing only #close, without loading another document.
            core.SourceChanged += async (_, _) =>
            {
                if (!_lifetime.IsCancellationRequested && IsLoginCompletionUri(core.Source))
                    await CompleteAsync();
            };
            var deviceAccounts = (await _client.SessionsAsync(_lifetime.Token)).Where(x => x.DeviceId == _device.Id).Select(x => x.Email).ToArray();
            var metadata = JsonSerializer.Serialize(new
            {
                androidId = ulong.Parse(_device.GoogleAndroidId!, CultureInfo.InvariantCulture).ToString("x"),
                sdk = _device.Profile.SdkVersion,
                accounts = deviceAccounts
            });
            await core.AddScriptToExecuteOnDocumentCreatedAsync("""
                (() => {
                  if (location.origin !== 'https://accounts.google.com' || window !== window.top) return;
                  const meta =
                """ + metadata + ";" + """
                  const notify = value => window.chrome.webview.postMessage(value);
                  const noop = () => {};

                  window.mm = {
                    getAndroidId: () => meta.androidId, getBuildVersionSdk: () => meta.sdk,
                    getAuthModuleVersionCode: () => 250000000, getPlayServicesVersionCode: () => 250000000,
                    getAccounts: () => JSON.stringify(meta.accounts), getAllowedDomains: () => '[]', getFactoryResetChallenges: () => '[]',
                    getDeviceContactsCount: () => -1, getDeviceDataVersionInfo: () => 1,
                    getPhoneNumber: () => null, getSimSerial: () => null, getSimState: () => 0,
                    fetchVerifiedPhoneNumber: () => null, hasPhoneNumber: () => false, hasTelephony: () => false,
                    isUserOwner: () => true, closeView: () => notify('finish'), skipLogin: () => notify('cancel'),
                    showView: noop, showKeyboard: noop, hideKeyboard: noop,
                    addAccount: noop, attemptLogin: noop, backupSyncOptIn: noop, clearOldLoginAttempts: noop,
                    goBack: noop, log: noop,
                    notifyOnTermsOfServiceAccepted: noop, setAccountIdentifier: noop, setAllActionsEnabled: noop,
                    setBackButtonEnabled: noop, setNewAccountCreated: noop, setPrimaryActionEnabled: noop,
                    setPrimaryActionLabel: noop, setSecondaryActionEnabled: noop, setSecondaryActionLabel: noop,
                    startAfw: () => notify('unsupported'), launchEmergencyDialer: noop
                  };
                })();
                """);
            core.Navigate(_ticket.Url);
            Status.Text = $"{_device.Profile.Name} · Enter your credentials directly on Google's page.";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_lifetime.IsCancellationRequested) Status.Text = "Could not prepare native sign-in. Check that Microsoft Edge WebView2 Runtime is installed, then reopen this window."; }
    }

    private async Task CompleteAsync()
    {
        if (_finishing || _ticket is null || Browser.CoreWebView2 is not { } core || !IsGoogleLoginOrigin(core.Source)) return;
        _finishing = true;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync("https://accounts.google.com/EmbeddedSetup");
            var cookie = cookies.FirstOrDefault(x => x.Name == "oauth_token" && x.IsSecure);
            if (cookie is null && IsGoogleLoginOrigin(core.Source))
            {
                var altCookies = await core.CookieManager.GetCookiesAsync(core.Source);
                cookie = altCookies.FirstOrDefault(x => x.Name == "oauth_token" && x.IsSecure);
            }
            if (cookie is null) { Status.Text = "Complete Google sign-in first, then choose Finish sign-in."; return; }
            Status.Text = "Creating your native account session…";
            RuntimeResponse result;
            try
            {
                result = await _client.SendAsync(new(1, "complete-login", LoginTicket: _ticket.Id, LoginToken: cookie.Value), _lifetime.Token);
            }
            catch (NativeRuntimeException ex) when (ex.Code == "LoginExpired")
            {
                // Ticket rejection happens before provider exchange. Keep this browser's result
                // and renew the local handoff once, without navigating away from Google's page.
                _ticket = (await _client.SendAsync(new(1, "begin-login", DeviceId: _device.Id), _lifetime.Token)).Login
                    ?? throw new InvalidOperationException("Runtime did not return a login ticket.");
                result = await _client.SendAsync(new(1, "complete-login", LoginTicket: _ticket.Id, LoginToken: cookie.Value), _lifetime.Token);
            }
            _ticket = null;
            Session = result.Sessions?.SingleOrDefault();
            if (Session is null) throw new InvalidOperationException();
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested)
                Status.Text = "Native sign-in timed out. Close and reopen sign-in to retry.";
        }
        catch (NativeRuntimeException ex) { Status.Text = ex.Message + " Close and reopen sign-in to retry."; }
        catch (Exception) { Status.Text = "Native sign-in could not finish. Close and reopen this window to retry."; }
        finally { _finishing = false; }
    }

    public static bool IsGoogleLoginOrigin(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        parsed.Scheme == "https" && parsed.Host == "accounts.google.com" && parsed.Port == 443 && parsed.UserInfo.Length == 0;

    public static bool IsLoginCompletionUri(string uri)
    {
        if (!IsGoogleLoginOrigin(uri)) return false;
        var parsed = new Uri(uri);
        return parsed.Fragment == "#close" || parsed.AbsolutePath == "/o/oauth2/programmatic_auth";
    }
    private void Back(object sender, RoutedEventArgs e) { if (Browser.CanGoBack) Browser.GoBack(); }
    private async void Finish(object sender, RoutedEventArgs e) => await CompleteAsync();
    private void Cancel(object sender, RoutedEventArgs e) => Close();
}

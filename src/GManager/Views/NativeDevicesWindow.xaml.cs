using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GManager.Auth;
using GManager.Contracts;
using Microsoft.Win32;

namespace GManager.Views;

public partial class NativeDevicesWindow : Window
{
    private readonly NativeRuntimeClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private NativeSessionSummary[] _sessions = [];
    public NativeSessionSummary? SelectedSession { get; private set; }
    public NativeDevicesWindow(NativeRuntimeClient client)
    {
        InitializeComponent();
        _client = client;
        Loaded += async (_, _) => await RunAsync(LoadAsync);
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async Task RunAsync(Func<Task> operation)
    {
        Workspace.IsEnabled = false;
        Status.Text = "Working…";
        try { await operation(); }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) Status.Text = "Operation timed out. Refresh to check the current state."; }
        catch (NativeRuntimeException ex) { Status.Text = ex.Message; }
        catch (ArgumentException ex) { Status.Text = ex.Message; }
        catch (Exception) { Status.Text = "Could not finish this operation. Check the runtime and connection, then retry."; }
        finally { Workspace.IsEnabled = true; }
    }

    private async Task LoadAsync()
    {
        var selected = (Devices.SelectedItem as DeviceSummary)?.Id;
        var response = await _client.SendAsync(new(1, "list"), _lifetime.Token);
        _sessions = await _client.SessionsAsync(_lifetime.Token);
        Devices.ItemsSource = response.Devices;
        Devices.SelectedItem = response.Devices?.FirstOrDefault(x => x.Id == selected) ?? response.Devices?.FirstOrDefault();
        ShowDevice();
        Status.Text = response.Devices?.Length > 0 ? "Select a device to register it or sign in." : "Create a lab device or import a profile to begin.";
    }
    private DeviceSummary Device() => Devices.SelectedItem as DeviceSummary ?? throw new ArgumentException("Select a device first.");
    private NativeSessionSummary Session() => Sessions.SelectedItem as NativeSessionSummary ?? throw new ArgumentException("Select an account first.");
    private void DeviceChanged(object sender, SelectionChangedEventArgs e) => ShowDevice();
    private void ShowDevice()
    {
        if (Devices.SelectedItem is not DeviceSummary device) { Sessions.ItemsSource = null; DeviceInfo.Text = "No device selected."; return; }
        DeviceInfo.Text = $"{device.Profile.Brand} · Android SDK {device.Profile.SdkVersion} · {(device.Registered ? "Registered" : "Not registered")}\nLast check-in: {device.LastOutcome} · Google ID: {device.GoogleAndroidId ?? "Not assigned"}";
        var items = _sessions.Where(x => x.DeviceId == device.Id).ToArray();
        Sessions.ItemsSource = items;
        if (Sessions.SelectedItem == null && items.Length > 0)
        {
            Sessions.SelectedItem = items[0];
        }
    }
    private async Task CreateAsync(DeviceProfile profile)
    {
        var response = await _client.SendAsync(new(1, "create", Profile: profile), _lifetime.Token);
        await LoadAsync();
        Devices.SelectedItem = ((DeviceSummary[])Devices.ItemsSource).First(x => x.Id == response.Devices![0].Id);
        Status.Text = "Profile saved locally. Register the device before Google sign-in.";
    }
    private async void NewDevice(object sender, RoutedEventArgs e) => await RunAsync(() => CreateAsync(new()
    {
        Name = "Pixel 9 Pro XL " + DateTime.Now.ToString("HH:mm"),
        Brand = "google",
        Manufacturer = "Google",
        Model = "Pixel 9 Pro XL",
        Product = "komodo",
        Device = "komodo",
        Hardware = "komodo",
        Fingerprint = "google/komodo/komodo:14/AD1A.240905.004/12185678:user/release-keys",
        SdkVersion = 34,
        BuildTimeSeconds = 1725500000,
        Bootloader = "komodo-1.0-12185678",
        Radio = "g5300i-240719-240726-B-12052468",
        WidthPixels = 1344,
        HeightPixels = 2992,
        DensityDpi = 480,
        NativePlatforms = ["arm64-v8a", "armeabi-v7a", "armeabi"]
    }));
    private async void Import(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Device profile or Magisk pif (*.json)|*.json", Title = "Import device profile" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(async () =>
        {
            if (new FileInfo(dialog.FileName).Length > 65536) throw new ArgumentException("Profile exceeds 64 KiB.");
            var content = await File.ReadAllTextAsync(dialog.FileName);
            var profile = ParseProfile(content, dialog.FileName);
            profile.Validate();
            await CreateAsync(profile);
            Status.Text = $"Imported '{profile.Model}'. Click Register / check in to enroll it with Google.";
        });
    }

    public static DeviceProfile ParseProfile(string json, string filename)
    {
        try
        {
            var p = JsonSerializer.Deserialize<DeviceProfile>(json, RuntimeProtocol.Json);
            if (p is not null && !string.IsNullOrWhiteSpace(p.Fingerprint)) return p;
        }
        catch { }

        // Magisk / PlayIntegrityFix pif.json compatibility
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string GetProp(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }
            return "";
        }

        var brand = GetProp("BRAND", "brand");
        var model = GetProp("MODEL", "model");
        var manufacturer = GetProp("MANUFACTURER", "manufacturer");
        var product = GetProp("PRODUCT", "product");
        var device = GetProp("DEVICE", "device");
        var fingerprint = GetProp("FINGERPRINT", "fingerprint");
        var hardware = GetProp("HARDWARE", "hardware");
        if (string.IsNullOrWhiteSpace(hardware)) hardware = device;

        var releasePart = fingerprint.Split('/') is { Length: >= 3 } buildParts ? buildParts[2].Split(':').Last() : "";
        int sdk = releasePart switch { "16" => 36, "15" => 35, "14" => 34, "13" => 33, "12" or "12.1" => 31, "11" => 30, "10" => 29, _ => 0 };
        // FIRST_API_LEVEL describes launch API level, not the Android version of this build.
        if (root.TryGetProperty("SdkVersion", out var sdkProp) || root.TryGetProperty("sdk_version", out sdkProp) || root.TryGetProperty("SDK_INT", out sdkProp))
        {
            if (sdkProp.ValueKind == JsonValueKind.Number) sdk = sdkProp.GetInt32();
            else if (int.TryParse(sdkProp.GetString(), out var parsedSdk)) sdk = parsedSdk;
        }

        if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Could not recognize profile format. Ensure file is a GManager profile or Magisk pif.json.");
        if (sdk is < 21 or > 36) throw new ArgumentException("Provide SDK_INT for this profile's Android build. FIRST_API_LEVEL is not its current SDK version.");

        var fingerprintParts = fingerprint.Split('/');
        var fingerprintProduct = fingerprintParts.Length >= 3 ? fingerprintParts[1] : "";
        var fingerprintDevice = fingerprintParts.Length >= 3 ? fingerprintParts[2].Split(':')[0] : "";
        if (string.IsNullOrWhiteSpace(product)) product = fingerprintProduct;
        if (string.IsNullOrWhiteSpace(device)) device = fingerprintDevice;
        if (string.IsNullOrWhiteSpace(hardware)) hardware = device;
        var name = $"{model} (Magisk PIF)";
        return new()
        {
            Name = name,
            Brand = string.IsNullOrWhiteSpace(brand) ? "google" : brand,
            Manufacturer = string.IsNullOrWhiteSpace(manufacturer) ? "Google" : manufacturer,
            Model = model,
            Product = product,
            Device = device,
            Hardware = hardware,
            Fingerprint = fingerprint,
            SdkVersion = sdk,
            BuildTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }
    private async void Export(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var profile = Device().Profile;
        var dialog = new SaveFileDialog { Filter = "Device profile (*.json)|*.json", FileName = "device-profile.json" };
        if (dialog.ShowDialog(this) == true)
        {
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(profile, new JsonSerializerOptions(RuntimeProtocol.Json) { WriteIndented = true }));
            Status.Text = "Profile exported. Account and registration credentials are excluded.";
        }
        else Status.Text = "Export cancelled.";
    });
    private async void Checkin(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await _client.SendAsync(new(1, "checkin", DeviceId: Device().Id), _lifetime.Token);
        await LoadAsync();
        Status.Text = "Google accepted the device check-in.";
    });
    private async void SignIn(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var device = Device();
        if (!device.Registered)
        {
            Status.Text = "Registering this virtual device with Google…";
            device = (await _client.SendAsync(new(1, "checkin", DeviceId: device.Id), _lifetime.Token)).Devices![0];
        }
        var login = new NativeLoginWindow(_client, device) { Owner = this };
        if (login.ShowDialog() == true && login.Session is { } session)
        {
            SelectedSession = session;
            await LoadAsync();
            Sessions.SelectedItem = _sessions.FirstOrDefault(x => x.Id == session.Id);
            Status.Text = session.Status == NativeSessionStatus.Active
                ? "Signed in. GMS setup and account check-in accepted. Select Use selected account."
                : "Account saved. " + session.LastError + " Select Finish account setup to retry.";
        }
        else Status.Text = "Sign-in cancelled. No password was saved.";
    });
    private async void TestAccess(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Sessions.SelectedItem is not NativeSessionSummary session)
        {
            if (Sessions.Items.Count > 0 && Sessions.Items[0] is NativeSessionSummary first)
            {
                Sessions.SelectedItem = first;
                session = first;
            }
            else
            {
                Status.Text = "Select an account first.";
                return;
            }
        }
        var results = new List<string>();
        foreach (var service in new[] { NativeService.Identity, NativeService.Gmail, NativeService.Drive })
        {
            try { await _client.GrantAsync(session.Id, service, true, _lifetime.Token); results.Add(service + ": grant received"); }
            catch (NativeRuntimeException ex) { results.Add(service + ": " + ex.Code); }
        }
        await LoadAsync();
        Status.Text = string.Join(" · ", results);
    });
    private async void FinishSetup(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var sessionId = Session().Id;
        try
        {
            var result = await _client.SendAsync(new(1, "finish-setup", SessionId: sessionId), _lifetime.Token);
            await LoadAsync();
            Sessions.SelectedItem = _sessions.FirstOrDefault(x => x.Id == sessionId);
            Status.Text = result.Message;
        }
        catch (NativeRuntimeException)
        {
            await LoadAsync();
            Sessions.SelectedItem = _sessions.FirstOrDefault(x => x.Id == sessionId);
            throw;
        }
    });
    private async void RemoveSession(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var session = Session();
        if (MessageBox.Show(this, $"Remove the local session for {session.Email}? Google-side access remains managed in your Google account.", "Remove local session", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { Status.Text = "Removal cancelled."; return; }
        await _client.SendAsync(new(1, "forget-session", SessionId: session.Id), _lifetime.Token);
        if (SelectedSession?.Id == session.Id) SelectedSession = null;
        await LoadAsync();
        Status.Text = "Local session removed.";
    });
    private void UseAccount(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is not NativeSessionSummary session)
        {
            if (Sessions.Items.Count > 0 && Sessions.Items[0] is NativeSessionSummary first)
            {
                session = first;
            }
            else
            {
                Status.Text = "Select an account first.";
                return;
            }
        }
        SelectedSession = session;
        DialogResult = true;
    }
    private async void Refresh(object sender, RoutedEventArgs e) => await RunAsync(LoadAsync);
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}

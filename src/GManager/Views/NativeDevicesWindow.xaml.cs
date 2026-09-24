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
        Name = "Windows Lab " + DateTime.Now.ToString("HH:mm"), Fingerprint = "gmanager/windows_lab/windows_lab:13/TQ3A.230805.001/lab:userdebug/test-keys",
        Brand = "gmanager", Manufacturer = "GManager", Model = "Windows Lab", Product = "windows_lab", Device = "windows_lab", Hardware = "windows", SdkVersion = 33
    }));
    private async void Import(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Device profile (*.json)|*.json", Title = "Import device profile" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(async () =>
        {
            if (new FileInfo(dialog.FileName).Length > 32768) throw new ArgumentException("Profile exceeds 32 KiB.");
            var profile = JsonSerializer.Deserialize<DeviceProfile>(await File.ReadAllTextAsync(dialog.FileName), RuntimeProtocol.Json)
                ?? throw new ArgumentException("Invalid profile.");
            profile.Validate();
            await CreateAsync(profile);
        });
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
            Status.Text = "Signed in. Session ready. Select Use selected account.";
            // Fire-and-forget device association check-in in background (matching microG behavior)
            _ = Task.Run(async () =>
            {
                try { await _client.SendAsync(new(1, "checkin", DeviceId: device.Id), _lifetime.Token); }
                catch { }
            });
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

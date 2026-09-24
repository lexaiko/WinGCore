using GManager.Contracts;
using GManager.Platform.Windows;
using GManager.Providers.Google;
using Xunit;
using Xunit.Abstractions;

namespace GManager.Runtime.Tests;

public class LiveGrantVerificationTests
{
    private readonly ITestOutputHelper _output;

    public LiveGrantVerificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task LiveVerifyGmailGrantOnExistingSession()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(localAppData, "GManager", "runtime", "runtime.db");
        if (!File.Exists(dbPath)) return;

        var store = new WindowsDeviceStore(dbPath);
        var sessions = store.ListSessions();
        if (sessions.Length == 0) return;

        var sessionSummary = sessions[0];
        var session = store.GetSession(sessionSummary.Id);
        var device = store.Get(sessionSummary.DeviceId);

        using var httpClient = new HttpClient();
        var provider = new GoogleNativeAuthProvider(httpClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Test Identity
        var idResult = await provider.GrantAsync(device, session.Credential, NativeService.Identity, cts.Token);
        _output.WriteLine($"Identity grant: {idResult.Code} - {idResult.Message}");
        Assert.Equal("Accepted", idResult.Code);

        // Test Drive
        var driveResult = await provider.GrantAsync(device, session.Credential, NativeService.Drive, cts.Token);
        _output.WriteLine($"Drive grant: {driveResult.Code} - {driveResult.Message}");
        Assert.Equal("Accepted", driveResult.Code);

        // Test Gmail
        var gmailResult = await provider.GrantAsync(device, session.Credential, NativeService.Gmail, cts.Token);
        _output.WriteLine($"Gmail grant: {gmailResult.Code} - {gmailResult.Message}");
        Assert.Equal("Accepted", gmailResult.Code);

        // Now test calling Gmail API!
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/messages?q=in:inbox&maxResults=10");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", gmailResult.Grant!.AccessToken);

        using var resp = await httpClient.SendAsync(req, cts.Token);
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        _output.WriteLine($"Gmail API response status: {resp.StatusCode}, body: {json}");
        Assert.True(resp.IsSuccessStatusCode, $"Gmail API failed with: {resp.StatusCode} - {json}");
    }
}

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

    [LiveFact]
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
        if (idResult.Code == "ActionNeeded")
        {
            _output.WriteLine("Local session expired or revoked remotely. Skipping live verification.");
            return;
        }
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
        _output.WriteLine($"Gmail API response status: {resp.StatusCode}");
        Assert.True(resp.IsSuccessStatusCode, $"Gmail API failed with: {resp.StatusCode}");
    }

    [LiveFact]
    public async Task LiveVerifyCheckinAndGooglePlaySyncOnExistingSession()
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
        var authProvider = new GoogleNativeAuthProvider(httpClient);
        var checkinProvider = new GoogleCheckinProvider(httpClient);
        var playSyncProvider = new GooglePlaySyncProvider(httpClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        // 1. Request GSF check-in token (LSid)
        var checkinGrant = await authProvider.GrantAsync(device, session.Credential, NativeService.Checkin, cts.Token);
        _output.WriteLine($"GSF Checkin grant: {checkinGrant.Code} - {checkinGrant.Message}");
        if (checkinGrant.Code == "ActionNeeded")
        {
            _output.WriteLine("Local session expired or revoked remotely. Skipping live verification.");
            return;
        }
        Assert.True(checkinGrant.Success);
        Assert.NotNull(checkinGrant.Grant);

        // 2. Perform live check-in with GSF LSid cookie
        var checkinState = device with { Accounts = [new(session.Credential.Email, checkinGrant.Grant.AccessToken)] };
        var checkinResult = await checkinProvider.CheckinAsync(checkinState, cts.Token);
        _output.WriteLine($"Google Checkin outcome: {checkinResult.Outcome} - {checkinResult.Error}");
        Assert.Equal(CheckinOutcome.Accepted, checkinResult.Outcome);

        // 3. Request Google Play token
        var playGrant = await authProvider.GrantAsync(device, session.Credential, NativeService.GooglePlay, cts.Token);
        _output.WriteLine($"Google Play grant: {playGrant.Code} - {playGrant.Message}");
        Assert.True(playGrant.Success);
        Assert.NotNull(playGrant.Grant);

        // 4. Upload device configuration to Google Play FDFE
        var syncResult = await playSyncProvider.UploadDeviceConfigAsync(device, playGrant.Grant.AccessToken, cts.Token);
        _output.WriteLine($"Google Play uploadDeviceConfig: {syncResult.Code} - {syncResult.Message} (Token: {syncResult.DeviceConfigToken})");
        Assert.True(syncResult.Success);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using GManager.Contracts;
using GManager.Platform.Windows;
using GManager.Providers.Google;
using GManager.Runtime;

namespace GManager.Runtime.Tests;

public sealed class NativeAccountTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GManager.NativeTests", Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_directory, "runtime.db");
    private static DeviceProfile Profile => new() { Name = "Test", Fingerprint = "test", Brand = "test", Manufacturer = "test", Model = "test", Product = "test", Device = "test", Hardware = "test", SdkVersion = 33 };
    private static NativeCredential Credential => new() { Email = "test@example.com", AccountId = "account-1", DisplayName = "Test", MasterToken = "master-secret-fixture" };
    private static Guid Registered(WindowsDeviceStore store)
    {
        var id = store.Create(Profile).Id;
        store.SaveResult(id, new(CheckinOutcome.Accepted, new() { AndroidId = 123, SecurityToken = 456 }, null));
        return id;
    }

    [Fact]
    public void SameAccountOnTwoDevicesHasIsolatedEncryptedSessions()
    {
        var store = new WindowsDeviceStore(Db);
        var first = store.SaveSession(Registered(store), Credential);
        var second = store.SaveSession(Registered(store), Credential);
        Assert.NotEqual(first.Id, second.Id);
        var reopened = new WindowsDeviceStore(Db);
        Assert.Equal(Credential.MasterToken, reopened.GetSession(first.Id).Credential.MasterToken);
        Assert.DoesNotContain(Credential.MasterToken, Encoding.UTF8.GetString(File.ReadAllBytes(Db)));
        Assert.DoesNotContain(Credential.Email, Encoding.UTF8.GetString(File.ReadAllBytes(Db)));
        reopened.DeleteSession(first.Id);
        Assert.Equal(second.Id, Assert.Single(reopened.ListSessions()).Id);
    }

    [Fact]
    public void SessionInventoryDoesNotHideAccountsPastOneHundred()
    {
        var store = new WindowsDeviceStore(Db);
        var device = Registered(store);
        for (var index = 0; index < 101; index++)
            store.SaveSession(device, new NativeCredential
            {
                Email = $"test{index}@example.com", AccountId = $"account-{index}",
                DisplayName = "Test", MasterToken = "fixture"
            });
        Assert.Equal(101, store.ListSessions().Length);
    }

    [Fact]
    public void ReEnrollmentKeepsSessionIdentityAndReplacesCredentials()
    {
        var store = new WindowsDeviceStore(Db);
        var device = Registered(store);
        var first = store.SaveSession(device, Credential);
        store.SetSessionStatus(first.Id, NativeSessionStatus.ActionNeeded, "Expired");
        var updated = store.SaveSession(device, new() { Email = Credential.Email, AccountId = Credential.AccountId, DisplayName = "Updated", MasterToken = "replacement" });
        Assert.Equal(first.Id, updated.Id);
        Assert.Equal(NativeSessionStatus.Active, updated.Status);
        Assert.Null(updated.LastError);
        Assert.Equal("replacement", store.GetSession(first.Id).Credential.MasterToken);
    }

    [Fact]
    public async Task LoginTicketIsSingleUseAndBoundToDevice()
    {
        var store = new WindowsDeviceStore(Db);
        var device = Registered(store);
        var auth = new FakeAuth();
        var broker = new NativeAccountBroker(store, store, auth);
        var ticket = broker.BeginLogin(device);
        var accepted = await broker.CompleteLoginAsync(ticket.Id, "one-time-cookie", default);
        Assert.True(accepted.Success);
        Assert.Equal(device, Assert.Single(accepted.Sessions!).DeviceId);
        var replay = await broker.CompleteLoginAsync(ticket.Id, "one-time-cookie", default);
        Assert.Equal("LoginExpired", replay.Code);
        Assert.Equal(1, auth.EnrollCalls);
    }

    [Fact]
    public async Task RejectedTicketCanBeReplacedBeforeProviderExchange()
    {
        var store = new WindowsDeviceStore(Db);
        var device = Registered(store);
        var auth = new FakeAuth();
        var broker = new NativeAccountBroker(store, store, auth);
        var oldTicket = broker.BeginLogin(device);
        broker.CancelLogin(oldTicket.Id);
        Assert.Equal("LoginExpired", (await broker.CompleteLoginAsync(oldTicket.Id, "cookie", default)).Code);
        Assert.Equal(0, auth.EnrollCalls);
        var replacement = broker.BeginLogin(device);
        var result = await broker.CompleteLoginAsync(replacement.Id, "cookie", default);
        Assert.True(result.Success);
        Assert.Equal(1, auth.EnrollCalls);
        Assert.Equal(device, Assert.Single(result.Sessions!).DeviceId);
    }

    [Fact]
    public async Task CancelledLoginNeverCallsProvider()
    {
        var store = new WindowsDeviceStore(Db);
        var auth = new FakeAuth();
        var broker = new NativeAccountBroker(store, store, auth);
        var ticket = broker.BeginLogin(Registered(store));
        broker.CancelLogin(ticket.Id);
        var result = await broker.CompleteLoginAsync(ticket.Id, "cookie", default);
        Assert.False(result.Success);
        Assert.Equal(0, auth.EnrollCalls);
        Assert.Empty(store.ListSessions());
    }

    [Fact]
    public async Task GrantsAreIsolatedAndRevocationClearsSessionCache()
    {
        var store = new WindowsDeviceStore(Db);
        var first = store.SaveSession(Registered(store), Credential);
        var second = store.SaveSession(Registered(store), Credential);
        var auth = new FakeAuth();
        var broker = new NativeAccountBroker(store, store, auth);
        await broker.GetGrantAsync(first.Id, NativeService.Gmail, false, default);
        await broker.GetGrantAsync(first.Id, NativeService.Gmail, false, default);
        await broker.GetGrantAsync(first.Id, NativeService.Drive, false, default);
        await broker.GetGrantAsync(second.Id, NativeService.Gmail, false, default);
        Assert.Equal(3, auth.GrantCalls);
        auth.Revoked = true;
        var revoked = await broker.GetGrantAsync(first.Id, NativeService.Gmail, true, default);
        Assert.Equal("ActionNeeded", revoked.Code);
        Assert.Equal("ActionNeeded", (await broker.GetGrantAsync(first.Id, NativeService.Drive, false, default)).Code);
        Assert.True((await broker.GetGrantAsync(second.Id, NativeService.Gmail, false, default)).Success);
        Assert.Equal(4, auth.GrantCalls);
    }

    [Fact]
    public async Task AndroidAuthUsesHexDeviceIdAndPreservesEqualsInTokens()
    {
        var store = new WindowsDeviceStore(Db);
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(GoogleNativeAuthProvider.Endpoint, request.RequestUri);
            var fields = (await request.Content!.ReadAsStringAsync()).Split('&')
                .Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
            Assert.Equal("7b", fields["androidId"]);
            Assert.Equal("cookie=value", fields["Token"]);
            Assert.Equal("1", fields["ACCESS_TOKEN"]);
            Assert.Equal("ac2dm", fields["service"]);
            Assert.False(fields.ContainsKey("client_id"));
            return new(HttpStatusCode.OK) { Content = new StringContent("Token=master==\nEmail=test@example.com\naccountId=123\nfirstName=Test\n") };
        }));
        var result = await new GoogleNativeAuthProvider(http).EnrollAsync(store.Get(Registered(store)), "cookie=value", default);
        Assert.True(result.Success);
        Assert.Equal("master==", result.Credential!.MasterToken);
        Assert.DoesNotContain("master==", result.ToString());
    }

    [Theory]
    [InlineData("Error=BadAuthentication\nInfo=secret", "ActionNeeded")]
    [InlineData("Error=NeedsBrowser\nUrl=https://accounts.google.com/?secret=123", "ChallengeRequired")]
    [InlineData("Error=Unauthorized", "PermissionRequired")]
    [InlineData("Token=one\nToken=two", "InvalidResponse")]
    [InlineData("<html>error</html>", "InvalidResponse")]
    [InlineData("Token=secret", "InvalidResponse")]
    public async Task RejectionsNeverExposeCredentials(string body, string code)
    {
        var store = new WindowsDeviceStore(Db);
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
        var result = await new GoogleNativeAuthProvider(http).EnrollAsync(store.Get(Registered(store)), "cookie", default);
        Assert.Equal(code, result.Code);
        Assert.Null(result.Credential);
        Assert.DoesNotContain("secret", result.Message);
    }

    [Fact]
    public void IpcDiagnosticStringsExcludeSecrets()
    {
        var request = new RuntimeRequest(1, "complete-login", LoginTicket: "ticket-secret", LoginToken: "cookie-secret");
        Assert.DoesNotContain("secret", request.ToString());
        var response = new RuntimeResponse(1, true, "Accepted", "OK", Grant: new() { AccessToken = "grant-secret", ExpiresAt = DateTimeOffset.UtcNow });
        Assert.DoesNotContain("secret", response.ToString());
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }

    private sealed class FakeAuth : INativeAuthProvider
    {
        public int EnrollCalls;
        public int GrantCalls;
        public bool Revoked;
        public Task<NativeAuthResult> EnrollAsync(DeviceState device, string loginToken, CancellationToken token)
        { EnrollCalls++; return Task.FromResult(new NativeAuthResult("Accepted", "OK", Credential)); }
        public Task<NativeAuthResult> GrantAsync(DeviceState device, NativeCredential credential, NativeService service, CancellationToken token)
        {
            GrantCalls++;
            return Task.FromResult(Revoked ? new NativeAuthResult("ActionNeeded", "Expired") :
                new NativeAuthResult("Accepted", "OK", Grant: new() { AccessToken = service + "-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }));
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

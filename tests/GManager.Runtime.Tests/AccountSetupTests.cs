using System.Net;
using GManager.Contracts;
using GManager.Platform.Windows;
using GManager.Providers.Google;
using GManager.Runtime;

namespace GManager.Runtime.Tests;

public sealed class AccountSetupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GManager.SetupTests", Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_directory, "runtime.db");
    private static NativeCredential Credential(string email) => new()
    { Email = email, AccountId = email, DisplayName = "Fixture", MasterToken = "fixture-master", Sid = "fixture-sid", Lsid = "fixture-lsid" };
    private static Guid Device(WindowsDeviceStore store)
    {
        var id = store.Create(new DeviceProfile
        {
            Name = "Fixture", Model = "Fixture", Brand = "test", Manufacturer = "test",
            Product = "test", Device = "test", Hardware = "test", Fingerprint = "test/test/test:14/BUILD/1:user/test-keys", SdkVersion = 34
        }).Id;
        store.SaveResult(id, new(CheckinOutcome.Accepted, new() { AndroidId = 123, SecurityToken = 456 }, null));
        return id;
    }

    [Fact]
    public void CheckinCarriesProvidedCapabilitiesWithoutInventingExtras()
    {
        var store = new WindowsDeviceStore(Db);
        var state = store.Get(Device(store));
        var profile = state.Summary.Profile with
        {
            AvailableFeatures = ["android.hardware.wifi"], SharedLibraries = ["android.test.runner"],
            GlExtensions = ["GL_EXT_fixture"], Locales = ["en-US", "id-ID"], GlEsVersion = 0x30002, ScreenLayout = 3
        };
        var request = GoogleCheckinProvider.BuildRequest(state with { Summary = state.Summary with { Profile = profile } }, DateTimeOffset.UtcNow);
        Assert.Equal(profile.AvailableFeatures, request.DeviceConfiguration.AvailableFeature.ToArray());
        Assert.Equal(profile.SharedLibraries, request.DeviceConfiguration.SharedLibrary.ToArray());
        Assert.Equal(profile.GlExtensions, request.DeviceConfiguration.GlExtension.ToArray());
        Assert.Equal(profile.Locales, request.DeviceConfiguration.Locale.ToArray());
        Assert.Equal(0x30002, request.DeviceConfiguration.GlEsVersion);
        Assert.Equal(3, request.DeviceConfiguration.ScreenLayout);
    }

    [Fact]
    public async Task SetupUsesDedicatedGmsEnrollmentFieldsAndPreservesProtectedMetadata()
    {
        var store = new WindowsDeviceStore(Db);
        var device = store.Get(Device(store));
        using var http = new HttpClient(new Handler(async request =>
        {
            var fields = (await request.Content!.ReadAsStringAsync()).Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
            Assert.Equal("com.google.android.gms", fields["app"]);
            Assert.Equal("com.google.android.gms", fields["callerPkg"]);
            Assert.Equal("ac2dm", fields["service"]);
            Assert.Equal("1", fields["system_partition"]);
            Assert.Equal("1", fields["has_permission"]);
            Assert.Equal("1", fields["add_account"]);
            Assert.Equal("1", fields["get_accountid"]);
            Assert.Equal("null", fields["droidguard_results"]);
            Assert.Equal("7b", fields["androidId"]);
            Assert.Equal("fixture-master", fields["Token"]);
            Assert.False(fields.ContainsKey("ACCESS_TOKEN"));
            return new(HttpStatusCode.OK) { Content = new StringContent("Auth=fixture-ac2dm\naccountId=server-id\n") };
        }));
        var result = await new GoogleNativeAuthProvider(http).SetupAccountAsync(device, Credential("a@example.test"), default);
        Assert.True(result.Success);
        Assert.Equal("server-id", result.Credential!.AccountId);
        Assert.Equal("fixture-sid", result.Credential.Sid);
        var session = store.SaveSession(device.Summary.Id, result.Credential);
        Assert.Equal("fixture-lsid", new WindowsDeviceStore(Db).GetSession(session.Id).Credential.Lsid);
        Assert.DoesNotContain("fixture-sid", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Db)));
    }

    [Fact]
    public async Task LoginSavesPendingSessionAndRetryAfterRestartChecksInEveryAccount()
    {
        var store = new WindowsDeviceStore(Db);
        var device = Device(store);
        var other = store.SaveSession(device, Credential("other@example.test"));
        var provider = new Auth { FailSetup = true };
        var checkin = new Checkin();
        var service = new RuntimeService(store, checkin, new NativeAccountBroker(store, store, provider));
        var ticket = (await service.HandleAsync(new(1, "begin-login", DeviceId: device), default)).Login!;
        var login = await service.HandleAsync(new(1, "complete-login", LoginTicket: ticket.Id, LoginToken: "fixture-cookie"), default);
        Assert.True(login.Success);
        Assert.Equal("AccountSavedSetupPending", login.Code);
        var session = Assert.Single(login.Sessions!);
        Assert.Equal(NativeSessionStatus.SetupPending, session.Status);
        Assert.Equal(0, checkin.Calls);
        Assert.Equal("SetupPending", (await service.HandleAsync(new(1, "get-grant", SessionId: session.Id), default)).Code);

        var reopened = new WindowsDeviceStore(Db);
        provider.FailSetup = false;
        service = new(reopened, checkin, new NativeAccountBroker(reopened, reopened, provider));
        var retry = await service.HandleAsync(new(1, "finish-setup", SessionId: session.Id), default);
        Assert.True(retry.Success);
        Assert.Equal(NativeSessionStatus.Active, Assert.Single(retry.Sessions!).Status);
        Assert.Equal(1, provider.EnrollCalls);
        Assert.Equal(new[] { "new@example.test", "other@example.test" }, checkin.Emails.OrderBy(x => x).ToArray());
        Assert.Equal(NativeSessionStatus.Active, reopened.GetSession(other.Id).Summary.Status);
    }

    [Fact]
    public async Task FailedAccountCheckinIsVisibleAndPreservesRegistration()
    {
        var store = new WindowsDeviceStore(Db);
        var device = Device(store);
        var session = store.SaveSession(device, Credential("new@example.test"));
        var checkin = new Checkin { Reject = true };
        var service = new RuntimeService(store, checkin, new NativeAccountBroker(store, store, new Auth()));
        var result = await service.HandleAsync(new(1, "finish-setup", SessionId: session.Id), default);
        Assert.False(result.Success);
        Assert.Equal(NativeSessionStatus.AssociationPending, Assert.Single(result.Sessions!).Status);
        Assert.Equal(CheckinOutcome.Rejected, store.Get(device).Summary.LastOutcome);
        Assert.Equal(123UL, store.Get(device).Registration!.AndroidId);
        checkin.Reject = false;
        Assert.True((await service.HandleAsync(new(1, "checkin", DeviceId: device), default)).Success);
        Assert.Equal(NativeSessionStatus.Active, store.GetSession(session.Id).Summary.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
    private sealed class Auth : INativeAuthProvider
    {
        public bool FailSetup;
        public int EnrollCalls;
        public Task<NativeAuthResult> EnrollAsync(DeviceState device, string loginToken, CancellationToken token)
        { EnrollCalls++; return Task.FromResult(new NativeAuthResult("Accepted", "Fixture", Credential("new@example.test"))); }
        public Task<NativeAuthResult> SetupAccountAsync(DeviceState device, NativeCredential credential, CancellationToken token) =>
            Task.FromResult(FailSetup ? new NativeAuthResult("TransientFailure", "Fixture unavailable") : Grant());
        public Task<NativeAuthResult> GrantAsync(DeviceState device, NativeCredential credential, NativeService service, CancellationToken token) => Task.FromResult(Grant());
        private static NativeAuthResult Grant() => new("Accepted", "Fixture", Grant: new() { AccessToken = "fixture-grant", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
    }
    private sealed class Checkin : ICheckinProvider
    {
        public bool Reject;
        public int Calls;
        public string[] Emails = [];
        public Task<CheckinResult> CheckinAsync(DeviceState device, CancellationToken token)
        {
            Calls++;
            Emails = device.Accounts!.Select(x => x.Email).ToArray();
            return Task.FromResult(Reject ? new CheckinResult(CheckinOutcome.Rejected, null, "Fixture rejected")
                : new CheckinResult(CheckinOutcome.Accepted, device.Registration, null));
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

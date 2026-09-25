using System.Globalization;
using System.Net;
using Google.Protobuf;
using GManager.Contracts;
using GManager.Platform.Windows;
using GManager.Providers.Google;
using GManager.Providers.Google.Protocol;
using GManager.Runtime;

namespace GManager.Runtime.Tests;

public sealed class GooglePlaySyncTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GManager.PlaySyncTests", Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_directory, "runtime.db");

    private static Guid CreateDevice(WindowsDeviceStore store)
    {
        var id = store.Create(new DeviceProfile
        {
            Name = "Pixel 9 Pro XL", Model = "Pixel 9 Pro XL", Brand = "google", Manufacturer = "Google",
            Product = "komodo", Device = "komodo", Hardware = "komodo",
            Fingerprint = "google/komodo/komodo:14/AD1A.240905.004/1:user/release-keys",
            SdkVersion = 34,
            WidthPixels = 1344, HeightPixels = 2992, DensityDpi = 480, GlEsVersion = 0x30002,
            AvailableFeatures = ["android.hardware.camera"], SharedLibraries = ["android.test.runner"]
        }).Id;
        store.SaveResult(id, new(CheckinOutcome.Accepted, new() { AndroidId = 0x123456789abcdef0UL, SecurityToken = 999 }, null));
        return id;
    }

    private static NativeCredential Credential(string email) => new()
    {
        Email = email, AccountId = email, DisplayName = "Test User",
        MasterToken = "master-token-xyz", Sid = "sid-123", Lsid = "lsid-456"
    };

    [Fact]
    public async Task GrantAsyncWithCheckinServiceRequestsGsfAc2dmAndExtractsLsid()
    {
        var store = new WindowsDeviceStore(Db);
        var device = store.Get(CreateDevice(store));
        using var http = new HttpClient(new MockHttpHandler(async request =>
        {
            Assert.Equal("https://android.googleapis.com/auth", request.RequestUri!.ToString());
            var form = (await request.Content!.ReadAsStringAsync()).Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));

            Assert.Equal("com.google.android.gsf", form["app"]);
            Assert.Equal("com.google.android.gsf", form["callerPkg"]);
            Assert.Equal("ac2dm", form["service"]);
            Assert.Equal("master-token-xyz", form["Token"]);
            Assert.Equal("test@example.com", form["Email"]);
            Assert.Equal("1", form["has_permission"]);

            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("SID=gsf-sid\nLSID=gsf-lsid-cookie\nAuth=gsf-auth-token\n")
            };
        }));

        var provider = new GoogleNativeAuthProvider(http);
        var result = await provider.GrantAsync(device, Credential("test@example.com"), NativeService.Checkin, default);

        Assert.True(result.Success);
        Assert.NotNull(result.Grant);
        Assert.Equal("gsf-lsid-cookie", result.Grant.AccessToken);
    }

    [Fact]
    public async Task GrantAsyncWithGooglePlayServiceRequestsVendingScope()
    {
        var store = new WindowsDeviceStore(Db);
        var device = store.Get(CreateDevice(store));
        using var http = new HttpClient(new MockHttpHandler(async request =>
        {
            var form = (await request.Content!.ReadAsStringAsync()).Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));

            Assert.Equal("com.android.vending", form["app"]);
            Assert.Equal("com.android.vending", form["callerPkg"]);
            Assert.Equal("oauth2:https://www.googleapis.com/auth/googleplay", form["service"]);
            Assert.Equal("master-token-xyz", form["Token"]);

            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("Auth=play-oauth-token\nExpiry=1800000000\n")
            };
        }));

        var provider = new GoogleNativeAuthProvider(http);
        var result = await provider.GrantAsync(device, Credential("test@example.com"), NativeService.GooglePlay, default);

        Assert.True(result.Success);
        Assert.NotNull(result.Grant);
        Assert.Equal("play-oauth-token", result.Grant.AccessToken);
    }

    [Fact]
    public void BuildRequestConstructsValidUploadDeviceConfigRequest()
    {
        var store = new WindowsDeviceStore(Db);
        var device = store.Get(CreateDevice(store));

        var request = GooglePlaySyncProvider.BuildRequest(device);

        Assert.NotNull(request.DeviceConfiguration);
        Assert.Equal("Google", request.Manufacturer);
        Assert.Equal(1344, request.DeviceConfiguration.WidthPixels);
        Assert.Equal(2992, request.DeviceConfiguration.HeightPixels);
        Assert.Equal(480, request.DeviceConfiguration.DensityDpi);
        Assert.Equal(0x30002, request.DeviceConfiguration.GlEsVersion);
        Assert.Contains("android.hardware.camera", request.DeviceConfiguration.AvailableFeature);
        Assert.Contains("android.test.runner", request.DeviceConfiguration.SharedLibrary);
    }

    [Fact]
    public async Task UploadDeviceConfigSendsFdfeRequestWithCorrectHeadersAndParsesToken()
    {
        var store = new WindowsDeviceStore(Db);
        var device = store.Get(CreateDevice(store));
        var androidIdHex = device.Registration!.AndroidId.ToString("x", CultureInfo.InvariantCulture);

        using var http = new HttpClient(new MockHttpHandler(async request =>
        {
            Assert.Equal("https://play-fe.googleapis.com/fdfe/uploadDeviceConfig", request.RequestUri!.ToString());
            Assert.Equal("Bearer play-token-123", request.Headers.Authorization!.ToString());
            Assert.Equal(androidIdHex, request.Headers.GetValues("X-DFE-Device-Id").First());
            Assert.Equal("am-google", request.Headers.GetValues("X-DFE-Client-Id").First());
            Assert.StartsWith("Android-Finsky/", request.Headers.UserAgent.ToString());
            Assert.Equal("application/x-protobuf", request.Content!.Headers.ContentType!.MediaType);

            var body = await request.Content.ReadAsByteArrayAsync();
            var parsed = UploadDeviceConfigRequest.Parser.ParseFrom(body);
            Assert.Equal("Google", parsed.Manufacturer);

            // Construct response protobuf
            var response = new GooglePlayApiResponse
            {
                Payload = new GooglePlayResponsePayload
                {
                    UploadDeviceConfigResponse = new UploadDeviceConfigResponse
                    {
                        DeviceConfigToken = "play-config-token-999"
                    }
                }
            };
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(response.ToByteArray())
            };
        }));

        var syncProvider = new GooglePlaySyncProvider(http);
        var result = await syncProvider.UploadDeviceConfigAsync(device, "play-token-123", default);

        Assert.True(result.Success);
        Assert.Equal("Accepted", result.Code);
        Assert.Equal("play-config-token-999", result.DeviceConfigToken);
    }

    [Fact]
    public async Task CheckinAsyncUsesGsfCheckinTokenAndCallsPlaySync()
    {
        var store = new WindowsDeviceStore(Db);
        var deviceId = CreateDevice(store);
        var session = store.SaveSession(deviceId, Credential("sync@example.test"));

        var auth = new MockAuth();
        var checkin = new MockCheckin();
        var playSync = new MockPlaySync();
        var broker = new NativeAccountBroker(store, store, auth);
        var service = new RuntimeService(store, checkin, broker, playSync);

        var response = await service.HandleAsync(new(1, "checkin", DeviceId: deviceId), default);

        Assert.True(response.Success);
        Assert.Contains(NativeService.Checkin, auth.RequestedServices);
        Assert.Single(checkin.ReceivedCookies);
        Assert.Equal("sync@example.test", checkin.ReceivedCookies[0].Email);
        Assert.Equal("gsf-lsid-token", checkin.ReceivedCookies[0].Token);

        Assert.Equal(1, playSync.UploadCalls);
        Assert.Equal("play-token-mock", playSync.LastToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class MockHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class MockAuth : INativeAuthProvider
    {
        public readonly List<NativeService> RequestedServices = [];

        public Task<NativeAuthResult> EnrollAsync(DeviceState device, string loginToken, CancellationToken token) =>
            throw new NotImplementedException();
        public Task<NativeAuthResult> SetupAccountAsync(DeviceState device, NativeCredential credential, CancellationToken token) =>
            throw new NotImplementedException();
        public Task<NativeAuthResult> GrantAsync(DeviceState device, NativeCredential credential, NativeService service, CancellationToken token)
        {
            RequestedServices.Add(service);
            var tokenStr = service switch
            {
                NativeService.Checkin => "gsf-lsid-token",
                NativeService.GooglePlay => "play-token-mock",
                _ => "mock-token"
            };
            return Task.FromResult(new NativeAuthResult("Accepted", "Mock", Grant: new()
            {
                AccessToken = tokenStr,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            }));
        }
    }

    private sealed class MockCheckin : ICheckinProvider
    {
        public List<CheckinAccount> ReceivedCookies = [];
        public Task<CheckinResult> CheckinAsync(DeviceState device, CancellationToken cancellationToken)
        {
            ReceivedCookies = device.Accounts?.ToList() ?? [];
            return Task.FromResult(new CheckinResult(CheckinOutcome.Accepted, device.Registration, null));
        }
    }

    private sealed class MockPlaySync : IDeviceSyncProvider
    {
        public int UploadCalls;
        public string? LastToken;
        public Task<DeviceSyncResult> UploadDeviceConfigAsync(DeviceState device, string googlePlayToken, CancellationToken cancellationToken)
        {
            UploadCalls++;
            LastToken = googlePlayToken;
            return Task.FromResult(new DeviceSyncResult(true, "Accepted", "Mock synced", "mock-cfg-token"));
        }
    }
}

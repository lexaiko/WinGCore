using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using GManager.Contracts;
using GManager.Platform.Windows;
using GManager.Providers.Google;
using GManager.Providers.Google.Protocol;
using GManager.Runtime;
using Microsoft.Data.Sqlite;

namespace GManager.Runtime.Tests;

public sealed class RuntimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GManager.Runtime.Tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "runtime.db");
    private static DeviceProfile Profile => new()
    {
        Name = "Test", Fingerprint = "test/test/test:13/test/1:userdebug/test-keys", Brand = "test",
        Manufacturer = "Test", Model = "Test", Product = "test", Device = "test", Hardware = "test", SdkVersion = 33
    };
    private static Registration Credentials => new()
    {
        AndroidId = 0xF000000000000001, SecurityToken = 0xE000000000000002, Digest = "sensitive-fixture-digest", LastCheckinMs = 1234567
    };

    [Fact]
    public void RestartPreservesDeviceAndProtectsRegistration()
    {
        var store = new WindowsDeviceStore(DatabasePath);
        var created = store.Create(Profile);
        var before = store.Get(created.Id);
        Assert.False(created.Registered);
        store.SaveResult(created.Id, new(CheckinOutcome.Accepted, Credentials, null));
        var reopened = new WindowsDeviceStore(DatabasePath).Get(created.Id);
        Assert.Equal(before.LoggingId, reopened.LoggingId);
        Assert.Equal(before.Summary.Id, reopened.Summary.Id);
        Assert.Equal(Credentials.AndroidId, reopened.Registration!.AndroidId);
        Assert.Equal(Credentials.SecurityToken, reopened.Registration.SecurityToken);
        Assert.True(reopened.Summary.Registered);
        Assert.DoesNotContain(Credentials.SecurityToken.ToString(), JsonSerializer.Serialize(reopened.Summary));
        Assert.DoesNotContain("sensitive-fixture-digest", Encoding.UTF8.GetString(File.ReadAllBytes(DatabasePath)));
        Assert.DoesNotContain(Credentials.SecurityToken.ToString(), reopened.ToString());
    }

    [Fact]
    public void FailedRenewalPreservesCredentialsAndOtherDevices()
    {
        var store = new WindowsDeviceStore(DatabasePath);
        var first = store.Create(Profile);
        var second = store.Create(Profile);
        store.SaveResult(first.Id, new(CheckinOutcome.Accepted, Credentials, null));
        store.SaveResult(first.Id, new(CheckinOutcome.TransientFailure, null, "HTTP 503"));
        var state = store.Get(first.Id);
        Assert.Equal(Credentials.SecurityToken, state.Registration!.SecurityToken);
        Assert.True(state.Summary.Registered);
        Assert.Equal(CheckinOutcome.TransientFailure, state.Summary.LastOutcome);
        Assert.Null(store.Get(second.Id).Registration);
        Assert.NotEqual(store.Get(first.Id).LoggingId, store.Get(second.Id).LoggingId);
    }

    [Fact]
    public void CorruptSecretFailsWithoutResettingIdentity()
    {
        var store = new WindowsDeviceStore(DatabasePath);
        var device = store.Create(Profile);
        store.SaveResult(device.Id, new(CheckinOutcome.Accepted, Credentials, null));
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE devices SET registration=X'00010203'";
            command.ExecuteNonQuery();
        }
        Assert.Throws<CryptographicException>(() => store.Get(device.Id));
        Assert.Equal(device.Id, Assert.Single(store.List()).Id);
    }

    [Fact]
    public void RequestUsesPersistedCredentialsAndCorrectWireTypes()
    {
        var store = new WindowsDeviceStore(DatabasePath);
        var device = store.Create(Profile);
        var initial = GoogleCheckinProvider.BuildRequest(store.Get(device.Id), DateTimeOffset.UtcNow);
        Assert.Equal(0, initial.Fragment);
        Assert.Equal(0, initial.AndroidId);
        Assert.False(initial.HasSecurityToken);
        Assert.Equal("event_log_start", Assert.Single(initial.Checkin.Event).Tag);
        store.SaveResult(device.Id, new(CheckinOutcome.Accepted, Credentials, null));
        var request = GoogleCheckinProvider.BuildRequest(store.Get(device.Id), DateTimeOffset.UtcNow);
        Assert.Equal(1, request.Fragment);
        Assert.Equal(Credentials.AndroidId, unchecked((ulong)request.AndroidId));
        Assert.Equal(Credentials.SecurityToken, request.SecurityToken);
        Assert.Equal(initial.LoggingId, request.LoggingId);
        Assert.Equal(Credentials.LastCheckinMs, request.Checkin.LastCheckinMs);
        var input = new CodedInputStream(request.ToByteArray());
        var tags = new List<uint>();
        uint tag;
        while ((tag = input.ReadTag()) != 0) { tags.Add(tag); input.SkipLastField(); }
        Assert.Contains(16U, tags); // Android ID: field 2, varint.
        Assert.Contains(105U, tags); // Security token: field 13, fixed64.
        Assert.Contains(160U, tags); // Fragment: field 20, varint.
    }

    [Fact]
    public async Task ProviderAcceptsIndependentBinaryFixtureAndGzipTransport()
    {
        // Independent wire fixture: statsOk=true, fixed64 androidId=123, fixed64 token=456.
        var fixture = Convert.FromHexString("0801397B0000000000000041C801000000000000");
        var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(GoogleCheckinProvider.Endpoint, request.RequestUri);
            Assert.Equal("application/x-protobuffer", request.Content!.Headers.ContentType!.MediaType);
            Assert.Contains("gzip", request.Content.Headers.ContentEncoding);
            await using var requestStream = new GZipStream(await request.Content.ReadAsStreamAsync(token), CompressionMode.Decompress);
            using var memory = new MemoryStream();
            await requestStream.CopyToAsync(memory, token);
            Assert.Equal(3, CheckinRequest.Parser.ParseFrom(memory.ToArray()).Version);
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true)) gzip.Write(fixture);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(compressed.ToArray()) };
            response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        });
        using var client = new HttpClient(handler);
        var store = new WindowsDeviceStore(DatabasePath);
        var result = await new GoogleCheckinProvider(client).CheckinAsync(store.Get(store.Create(Profile).Id), default);
        Assert.Equal(CheckinOutcome.Accepted, result.Outcome);
        Assert.Equal(123UL, result.Registration!.AndroidId);
        Assert.Equal(456UL, result.Registration.SecurityToken);
    }

    [Theory]
    [InlineData(403, "secret-server-error", CheckinOutcome.Rejected)]
    [InlineData(429, "secret-server-error", CheckinOutcome.TransientFailure)]
    [InlineData(503, "secret-server-error", CheckinOutcome.TransientFailure)]
    [InlineData(200, "not protobuf", CheckinOutcome.InvalidResponse)]
    [InlineData(200, "", CheckinOutcome.InvalidResponse)]
    public async Task FailureNeverRegistersDeviceOrLeaksResponse(int status, string body, CheckinOutcome outcome)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(body) })));
        var store = new WindowsDeviceStore(DatabasePath);
        var device = store.Create(Profile);
        var service = new RuntimeService(store, new GoogleCheckinProvider(client));
        var response = await service.HandleAsync(new(1, "checkin", DeviceId: device.Id), default);
        Assert.False(response.Success);
        Assert.False(store.Get(device.Id).Summary.Registered);
        Assert.Equal(outcome.ToString(), response.Code);
        Assert.DoesNotContain("secret-server-error", JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task ChangedServerIdentityDoesNotReplaceRegistration()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new CheckinResponse { AndroidId = 1, SecurityToken = 2, StatsOk = true }.ToByteArray()) })));
        var store = new WindowsDeviceStore(DatabasePath);
        var device = store.Create(Profile);
        store.SaveResult(device.Id, new(CheckinOutcome.Accepted, Credentials, null));
        var response = await new RuntimeService(store, new GoogleCheckinProvider(client))
            .HandleAsync(new(1, "checkin", DeviceId: device.Id), default);
        Assert.False(response.Success);
        Assert.Equal(Credentials.AndroidId, store.Get(device.Id).Registration!.AndroidId);
    }

    [Fact]
    public async Task OversizedGzipResponseIsRejected()
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true)) gzip.Write(new byte[1024 * 1024 + 1]);
        using var client = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(compressed.ToArray()) };
            response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        }));
        var store = new WindowsDeviceStore(DatabasePath);
        var result = await new GoogleCheckinProvider(client).CheckinAsync(store.Get(store.Create(Profile).Id), default);
        Assert.Equal(CheckinOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task PipeSupportsRestartAndRejectsVersionMismatch()
    {
        var store = new WindowsDeviceStore(DatabasePath);
        var pipe = RuntimePipe.NameFor(_directory);
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No network expected.")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = RuntimePipe.ServeAsync(pipe, new(store, new GoogleCheckinProvider(client)), timeout.Token);
        var mismatch = await RuntimePipe.SendAsync(pipe, new(999, "create", Profile), timeout.Token);
        Assert.Equal("VersionMismatch", mismatch.Code);
        var created = await RuntimePipe.SendAsync(pipe, new(1, "create", Profile), timeout.Token);
        var id = Assert.Single(created.Devices!).Id;
        await RuntimePipe.SendAsync(pipe, new(1, "stop"), timeout.Token);
        await server;
        server = RuntimePipe.ServeAsync(pipe, new(new WindowsDeviceStore(DatabasePath), new GoogleCheckinProvider(client)), timeout.Token);
        var listed = await RuntimePipe.SendAsync(pipe, new(1, "list"), timeout.Token);
        Assert.Equal(id, Assert.Single(listed.Devices!).Id);
        await RuntimePipe.SendAsync(pipe, new(1, "stop"), timeout.Token);
        await server;
    }

    [Fact]
    public async Task FramesRejectUnboundedAllocationAndTruncation()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeMessages.ReadAsync<RuntimeRequest>(
            new MemoryStream(BitConverter.GetBytes(int.MaxValue)), default));
        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeMessages.ReadAsync<RuntimeRequest>(
            new MemoryStream([1, 0, 0, 0]), default));
    }

    [Fact]
    public async Task ConcurrentCheckinsUseUpdatedRegistration()
    {
        var observed = new List<int>();
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            await using var gzip = new GZipStream(await request.Content!.ReadAsStreamAsync(token), CompressionMode.Decompress);
            using var bytes = new MemoryStream();
            await gzip.CopyToAsync(bytes, token);
            observed.Add(CheckinRequest.Parser.ParseFrom(bytes.ToArray()).Fragment);
            await Task.Delay(30, token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new CheckinResponse { AndroidId = 123, SecurityToken = 456, StatsOk = true }.ToByteArray())
            };
        }));
        var store = new WindowsDeviceStore(DatabasePath);
        var device = store.Create(Profile);
        var service = new RuntimeService(store, new GoogleCheckinProvider(client));
        var request = new RuntimeRequest(1, "checkin", DeviceId: device.Id);
        var results = await Task.WhenAll(service.HandleAsync(request, default), service.HandleAsync(request, default));
        Assert.All(results, x => Assert.True(x.Success));
        Assert.Equal(new[] { 0, 1 }, observed);
    }

    [Fact]
    public async Task CancellationDoesNotRecordAFailedRegistration()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(1000, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var store = new WindowsDeviceStore(DatabasePath);
        var device = store.Create(Profile);
        var service = new RuntimeService(store, new GoogleCheckinProvider(client));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.HandleAsync(new(1, "checkin", DeviceId: device.Id), cancellation.Token));
        Assert.Equal(CheckinOutcome.NeverAttempted, store.Get(device.Id).Summary.LastOutcome);
        Assert.Null(store.Get(device.Id).Registration);
    }

    [Fact]
    public void ProfileRejectsInvalidAndUnknownFields()
    {
        Assert.Throws<ArgumentException>(() => (Profile with { SdkVersion = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (Profile with { Name = "test\r\nheader" }).Validate());
        Assert.Throws<ArgumentException>(() => (Profile with { NativePlatforms = null! }).Validate());
        var json = JsonSerializer.Serialize(Profile, RuntimeProtocol.Json).TrimEnd('}') + ",\"securityToken\":123}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DeviceProfile>(json, RuntimeProtocol.Json));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}

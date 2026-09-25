using System.IO;
using GManager.Contracts;
using GManager.Platform.Windows;
using GManager.Providers.Google;
using GManager.Providers.Google.Mcs;
using GManager.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace GManager.Runtime.Tests;

public class McsIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public McsIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LiveFact]
    public async Task LiveMtalkTlsAndVersionHandshake()
    {
        // 1. Establish real TLS connection to Google mtalk
        await using var conn = new McsConnection();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        _output.WriteLine($"Connecting to {McsConstants.DefaultHost}:{McsConstants.DefaultPort}...");
        await conn.ConnectAsync(McsConstants.DefaultHost, McsConstants.DefaultPort, cts.Token);
        Assert.True(conn.IsConnected);
        _output.WriteLine("TCP and TLS connection established with mtalk.google.com.");

        // 2. Perform MCS version handshake (exchange version byte 41)
        await conn.HandshakeVersionAsync(cts.Token);
        _output.WriteLine($"Version handshake complete. Server version: {conn.RemoteVersion}");
        Assert.True(conn.RemoteVersion >= 38, $"Expected server version >= 38, got {conn.RemoteVersion}");

        conn.Close();
    }

    [LiveFact]
    public async Task LiveMtalkAuthenticatedSessionWithHeartbeat()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(localAppData, "GManager", "runtime", "runtime.db");
        if (!File.Exists(dbPath))
        {
            _output.WriteLine("No runtime.db found; skipping live authenticated MCS test.");
            return;
        }

        var store = new WindowsDeviceStore(dbPath);
        var sessions = store.ListSessions();
        if (sessions.Length == 0)
        {
            _output.WriteLine("No active native sessions found; skipping live authenticated MCS test.");
            return;
        }

        var sessionSummary = sessions[0];
        var session = store.GetSession(sessionSummary.Id);
        var device = store.Get(sessionSummary.DeviceId);

        if (device.Registration is null)
        {
            _output.WriteLine("Device not registered; skipping.");
            return;
        }

        using var httpClient = new HttpClient();
        var authProvider = new GoogleNativeAuthProvider(httpClient);
        var broker = new NativeAccountBroker(store, store, authProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Obtain ac2dm grant using the broker
        _output.WriteLine($"Requesting ac2dm grant for session {session.Summary.Email}...");
        var grantResult = await broker.GetGrantAsync(session.Summary.Id, NativeService.Messaging, false, cts.Token);
        _output.WriteLine($"ac2dm grant status: {grantResult.Code}");
        if (!grantResult.Success)
        {
            _output.WriteLine($"Session cannot obtain grant ({grantResult.Code}); skipping live test.");
            return;
        }
        Assert.NotNull(grantResult.Grant);

        // Connect to mtalk.google.com
        await using var conn = new McsConnection();
        _output.WriteLine("Connecting to mtalk.google.com:5228 over TLS...");
        await conn.ConnectAsync(McsConstants.DefaultHost, McsConstants.DefaultPort, cts.Token);
        await conn.HandshakeVersionAsync(cts.Token);
        _output.WriteLine($"TLS & version handshake complete (server version: {conn.RemoteVersion}).");

        // Build LoginRequest
        var loginReq = McsLogin.BuildRequest(
            (long)device.Registration.AndroidId,
            (long)device.Registration.SecurityToken,
            session.Credential.AccountId,
            grantResult.Grant.AccessToken,
            device.Summary.Profile.SdkVersion);

        _output.WriteLine($"Sending MCS LoginRequest for Android ID: {device.Registration.AndroidId:x}, Account: {session.Summary.Email}...");
        var loginResponse = await McsLogin.PerformLoginAsync(conn, loginReq, cts.Token);

        _output.WriteLine($"MCS LoginResponse received! ID: '{loginResponse.Id}', ServerTimestamp: {loginResponse.ServerTimestamp}");
        Assert.NotNull(loginResponse);
        Assert.Null(loginResponse.Error);

        // Send a HeartbeatPing
        _output.WriteLine("Sending HeartbeatPing to Google MCS...");
        var ping = new HeartbeatPing { StreamId = 1, Status = 1 };
        await conn.SendAsync(McsConstants.HeartbeatPingTag, ping, cts.Token);

        // Receive messages until HeartbeatAck or up to 5 messages
        McsMessage? ackMsg = null;
        for (int i = 0; i < 5; i++)
        {
            var msg = await conn.ReceiveAsync(cts.Token);
            if (msg == null) break;
            _output.WriteLine($"Received message from Google: Tag {msg.Tag} ({msg.Payload?.GetType().Name})");
            if (msg.Tag is McsConstants.HeartbeatAckTag or McsConstants.HeartbeatPingTag)
            {
                ackMsg = msg;
                break;
            }
        }

        Assert.NotNull(ackMsg);
        _output.WriteLine($"Heartbeat verified with Google MCS (Tag: {ackMsg.Tag})!");

        // Clean close
        conn.Close();
        _output.WriteLine("MCS live integration test completed successfully and closed cleanly.");
    }

    [LiveFact]
    public async Task LiveMcsClientEndToEndLifecycle()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(localAppData, "GManager", "runtime", "runtime.db");
        if (!File.Exists(dbPath))
        {
            _output.WriteLine("No runtime.db found; skipping live McsClient test.");
            return;
        }

        var store = new WindowsDeviceStore(dbPath);
        var sessions = store.ListSessions();
        if (sessions.Length == 0)
        {
            _output.WriteLine("No active native sessions found; skipping live McsClient test.");
            return;
        }

        var sessionSummary = sessions[0];
        using var httpClient = new HttpClient();
        var broker = new NativeAccountBroker(store, store, new GoogleNativeAuthProvider(httpClient));
        using var preCheckCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var preCheck = await broker.GetGrantAsync(sessionSummary.Id, NativeService.Messaging, false, preCheckCts.Token);
        if (!preCheck.Success)
        {
            _output.WriteLine($"Session cannot obtain grant ({preCheck.Code}); skipping live test.");
            return;
        }

        var connectedTcs = new TaskCompletionSource<bool>();
        var messageReceivedTcs = new TaskCompletionSource<McsMessage>();

        var testSink = new ActionMcsEventSink(
            onState: (prev, curr, reason) =>
            {
                _output.WriteLine($"[State] {prev} -> {curr}" + (reason != null ? $" ({reason})" : ""));
                if (curr == McsConnectionState.Connected)
                    connectedTcs.TrySetResult(true);
            },
            onMessage: msg =>
            {
                _output.WriteLine($"[Msg] Tag={msg.Tag} Type={msg.Payload.GetType().Name}");
                messageReceivedTcs.TrySetResult(msg);
            },
            onLog: log => _output.WriteLine($"[Log] {log}"),
            onError: (ex, ctx) => _output.WriteLine($"[Error in {ctx}] {ex.Message}"));

        var client = McsClient.Create(
            store,
            broker,
            sessionSummary.Id,
            testSink,
            heartbeatInterval: TimeSpan.FromSeconds(2));

        try
        {
            _output.WriteLine($"Starting McsClient for {sessionSummary.Email}...");
            client.Start();

            var connected = await Task.WhenAny(connectedTcs.Task, Task.Delay(15000));
            Assert.True(connected == connectedTcs.Task && await connectedTcs.Task, "McsClient failed to reach Connected state within 15s.");
            Assert.Equal(McsConnectionState.Connected, client.State);
            _output.WriteLine("McsClient reached Connected state!");

            var msgReceived = await Task.WhenAny(messageReceivedTcs.Task, Task.Delay(10000));
            Assert.True(msgReceived == messageReceivedTcs.Task, "McsClient did not receive server message within 10s.");
            var firstMsg = await messageReceivedTcs.Task;
            _output.WriteLine($"McsClient received initial message Tag={firstMsg.Tag} successfully.");

            // Allow at least one heartbeat interval to pass
            await Task.Delay(3000);
        }
        finally
        {
            _output.WriteLine("Stopping McsClient cleanly...");
            await client.StopAsync();
            client.Dispose();
            Assert.Equal(McsConnectionState.Stopped, client.State);
            _output.WriteLine("McsClient reached Stopped state cleanly.");
        }
    }
}

sealed class ActionMcsEventSink(
    Action<McsConnectionState, McsConnectionState, string?>? onState = null,
    Action<McsMessage>? onMessage = null,
    Action<string>? onLog = null,
    Action<Exception, string>? onError = null) : IMcsEventSink
{
    public void OnStateChanged(McsConnectionState previous, McsConnectionState current, string? reason = null) =>
        onState?.Invoke(previous, current, reason);

    public void OnMessageReceived(McsMessage message) =>
        onMessage?.Invoke(message);

    public void OnError(Exception exception, string context) =>
        onError?.Invoke(exception, context);

    public void OnLog(string message) =>
        onLog?.Invoke(message);
}

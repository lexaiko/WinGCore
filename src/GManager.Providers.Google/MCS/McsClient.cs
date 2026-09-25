using GManager.Contracts;
using GManager.Runtime;

namespace GManager.Providers.Google.Mcs;

public sealed record McsAuthContext(
    long AndroidId,
    long SecurityToken,
    string? AccountId,
    string? Ac2dmToken,
    int SdkVersion = 33);

public sealed class McsClient : IAsyncDisposable, IDisposable
{
    private readonly Func<CancellationToken, Task<McsAuthContext>> _authContextProvider;
    private readonly Func<IMcsConnection> _connectionFactory;
    private readonly IMcsEventSink _eventSink;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _minBackoff = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _maxBackoff = TimeSpan.FromSeconds(60);

    private CancellationTokenSource? _lifecycleCts;
    private Task? _workerTask;
    private McsConnectionState _state = McsConnectionState.Disconnected;
    private readonly object _stateLock = new();

    public McsConnectionState State
    {
        get { lock (_stateLock) return _state; }
        private set
        {
            McsConnectionState prev;
            lock (_stateLock)
            {
                if (_state == value) return;
                prev = _state;
                _state = value;
            }
            _eventSink.OnStateChanged(prev, value);
        }
    }

    public McsClient(
        Func<CancellationToken, Task<McsAuthContext>> authContextProvider,
        IMcsEventSink? eventSink = null,
        Func<IMcsConnection>? connectionFactory = null,
        TimeSpan? heartbeatInterval = null)
    {
        _authContextProvider = authContextProvider ?? throw new ArgumentNullException(nameof(authContextProvider));
        _eventSink = eventSink ?? NullMcsEventSink.Instance;
        _connectionFactory = connectionFactory ?? (() => new McsConnection());
        _heartbeatInterval = heartbeatInterval ?? McsConstants.DefaultHeartbeatInterval;
    }

    public static McsClient Create(
        IDeviceStore deviceStore,
        NativeAccountBroker broker,
        Guid sessionId,
        IMcsEventSink? eventSink = null,
        Func<IMcsConnection>? connectionFactory = null,
        TimeSpan? heartbeatInterval = null,
        RuntimeService? runtimeService = null)
    {
        return new McsClient(async token =>
        {
            var sessions = broker.List();
            var session = sessions.FirstOrDefault(x => x.Id == sessionId)
                ?? throw new KeyNotFoundException($"Session {sessionId} not found.");

            var device = deviceStore.Get(session.DeviceId);
            if (device.Registration is null)
                throw new InvalidOperationException("Virtual device is not registered with Google.");

            NativeAuthResult grant;
            if (runtimeService is not null)
            {
                var response = await runtimeService.HandleAsync(new(1, "get-grant", SessionId: sessionId, Service: NativeService.Messaging), token);
                grant = new(response.Success ? "Accepted" : response.Code, response.Message, Grant: response.Grant);
            }
            else grant = await broker.GetGrantAsync(sessionId, NativeService.Messaging, false, token);
            if (!grant.Success)
                throw new InvalidOperationException($"Failed to obtain ac2dm grant: {grant.Code} - {grant.Message}");

            return new McsAuthContext(
                (long)device.Registration.AndroidId,
                (long)device.Registration.SecurityToken,
                session.Email, // or accountId
                grant.Grant?.AccessToken,
                device.Summary.Profile.SdkVersion);
        }, eventSink, connectionFactory, heartbeatInterval);
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_state is McsConnectionState.Connecting or McsConnectionState.Connected or McsConnectionState.Reconnecting)
                return;

            _lifecycleCts = new CancellationTokenSource();
            _workerTask = Task.Run(() => RunLoopAsync(_lifecycleCts.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_stateLock)
        {
            cts = _lifecycleCts;
            task = _workerTask;
            _lifecycleCts = null;
            _workerTask = null;
            State = McsConnectionState.Stopped;
        }

        if (cts != null)
        {
            cts.Cancel();
            if (task != null)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { /* Ignore timeout or task cancellation */ }
            }
            cts.Dispose();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        int attempt = 0;
        _eventSink.OnLog("MCS client background loop started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            State = attempt == 0 ? McsConnectionState.Connecting : McsConnectionState.Reconnecting;
            IMcsConnection? connection = null;

            try
            {
                var authContext = await _authContextProvider(cancellationToken);
                connection = _connectionFactory();

                _eventSink.OnLog("Attempting connection to Google mtalk...");
                await ConnectWithPortFallbackAsync(connection, cancellationToken);

                _eventSink.OnLog("TLS connection established. Performing version handshake...");
                await connection.HandshakeVersionAsync(cancellationToken);

                _eventSink.OnLog($"Handshake complete (server version: {connection.RemoteVersion}). Sending LoginRequest...");
                var loginReq = McsLogin.BuildRequest(
                    authContext.AndroidId,
                    authContext.SecurityToken,
                    authContext.AccountId,
                    authContext.Ac2dmToken,
                    authContext.SdkVersion);

                var loginResponse = await McsLogin.PerformLoginAsync(connection, loginReq, cancellationToken);
                _eventSink.OnLog($"MCS authenticated successfully (ID: {loginResponse.Id}). Active connection established.");

                State = McsConnectionState.Connected;
                attempt = 0; // Reset backoff upon successful login

                await RunSessionAsync(connection, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _eventSink.OnError(ex, "MCS session error");
                _eventSink.OnLog($"MCS connection error: {ex.Message}");
            }
            finally
            {
                if (connection != null)
                {
                    try { connection.Close(); await connection.DisposeAsync(); }
                    catch { }
                }
            }

            if (cancellationToken.IsCancellationRequested) break;

            attempt++;
            var delay = CalculateBackoff(attempt, _minBackoff, _maxBackoff);
            _eventSink.OnLog($"MCS disconnected. Reconnecting in {delay.TotalSeconds:F1}s (attempt {attempt})...");
            State = McsConnectionState.Reconnecting;

            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }

        State = McsConnectionState.Stopped;
        _eventSink.OnLog("MCS client background loop stopped.");
    }

    private async Task ConnectWithPortFallbackAsync(IMcsConnection connection, CancellationToken cancellationToken)
    {
        Exception? lastEx = null;
        foreach (var port in McsConstants.DefaultPorts)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                await connection.ConnectAsync(McsConstants.DefaultHost, port, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _eventSink.OnLog($"Port {port} failed: {ex.Message}. Trying fallback port...");
            }
        }
        throw lastEx ?? new IOException("Could not connect to any MCS port.");
    }

    private async Task RunSessionAsync(IMcsConnection connection, CancellationToken cancellationToken)
    {
        var heartbeat = new McsHeartbeat(_heartbeatInterval);
        heartbeat.OnConnected();

        while (!cancellationToken.IsCancellationRequested && connection.IsConnected)
        {
            var now = DateTimeOffset.UtcNow;

            // 1. Check if we need to send ping
            if (heartbeat.ShouldSendPing(now))
            {
                var ping = heartbeat.CreatePing();
                await connection.SendAsync(McsConstants.HeartbeatPingTag, ping, cancellationToken);
                _eventSink.OnLog("Heartbeat ping sent to Google MCS.");
            }

            // 2. Check for heartbeat ACK timeout
            if (heartbeat.IsTimedOut(now))
            {
                throw new TimeoutException("Heartbeat ACK timed out after 90 seconds. Assuming connection dead.");
            }

            // 3. Read packet with timeout
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(TimeSpan.FromSeconds(5));

            McsMessage? message;
            try
            {
                message = await connection.ReceiveAsync(readCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout on read; loop back to check heartbeats
                continue;
            }

            if (message == null)
            {
                throw new EndOfStreamException("Google closed the MCS stream.");
            }

            // 4. Handle incoming message
            switch (message.Tag)
            {
                case McsConstants.HeartbeatPingTag:
                    _eventSink.OnLog("Incoming HeartbeatPing from Google MCS. Responding with ACK...");
                    var ack = heartbeat.CreateAck(message.AsHeartbeatPing);
                    await connection.SendAsync(McsConstants.HeartbeatAckTag, ack, cancellationToken);
                    break;

                case McsConstants.HeartbeatAckTag:
                    heartbeat.OnAckReceived();
                    _eventSink.OnLog("HeartbeatAck received from Google MCS.");
                    break;

                case McsConstants.CloseTag:
                    throw new IOException("Server sent Close stanza.");

                case McsConstants.DataMessageStanzaTag:
                    _eventSink.OnLog($"Data message received (from: {message.AsDataMessage?.From}, category: {message.AsDataMessage?.Category}).");
                    _eventSink.OnMessageReceived(message);
                    break;

                default:
                    _eventSink.OnLog($"Received MCS message with tag: {message.Tag}");
                    _eventSink.OnMessageReceived(message);
                    break;
            }
        }
    }

    public static TimeSpan CalculateBackoff(int attempt, TimeSpan minDelay, TimeSpan maxDelay, Random? random = null)
    {
        random ??= Random.Shared;
        double raw = minDelay.TotalSeconds * Math.Pow(2, Math.Min(attempt - 1, 6));
        double jitter = (random.NextDouble() * 0.4) - 0.2; // +/- 20%
        double seconds = Math.Clamp(raw * (1.0 + jitter), minDelay.TotalSeconds, maxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    public void Dispose()
    {
        _ = StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

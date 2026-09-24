using System.IO;
using GManager.Providers.Google.Mcs;
using Google.Protobuf;
using Xunit;

namespace GManager.Runtime.Tests;

public class McsProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(300)]
    [InlineData(16384)]
    [InlineData(65535)]
    [InlineData(1048576)]
    public async Task VarintRoundtripMatchesExpected(int expected)
    {
        using var ms = new MemoryStream();
        await McsProtocol.WriteVarintAsync(ms, expected);
        ms.Position = 0;
        int actual = await McsProtocol.ReadVarintAsync(ms);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task FrameSerializationAndParsingMatchesProtobuf()
    {
        var ping = new HeartbeatPing
        {
            StreamId = 42,
            LastStreamIdReceived = 17,
            Status = 1
        };

        using var ms = new MemoryStream();
        await McsProtocol.WriteMessageAsync(ms, McsConstants.HeartbeatPingTag, ping);

        ms.Position = 0;
        var message = await McsProtocol.ReadMessageAsync(ms);

        Assert.NotNull(message);
        Assert.Equal(McsConstants.HeartbeatPingTag, message.Tag);
        Assert.NotNull(message.AsHeartbeatPing);
        Assert.Equal(42, message.AsHeartbeatPing.StreamId);
        Assert.Equal(17, message.AsHeartbeatPing.LastStreamIdReceived);
        Assert.Equal(1, message.AsHeartbeatPing.Status);
    }

    [Fact]
    public async Task LoginResponseParsingExtractsErrorCorrectly()
    {
        var response = new LoginResponse
        {
            Id = "android-33",
            Error = new ErrorInfo
            {
                Code = 401,
                Message = "Authentication failed"
            }
        };

        using var ms = new MemoryStream();
        await McsProtocol.WriteMessageAsync(ms, McsConstants.LoginResponseTag, response);

        ms.Position = 0;
        var message = await McsProtocol.ReadMessageAsync(ms);

        Assert.NotNull(message);
        Assert.Equal(McsConstants.LoginResponseTag, message.Tag);
        Assert.NotNull(message.AsLoginResponse);
        Assert.NotNull(message.AsLoginResponse.Error);
        Assert.Equal(401, message.AsLoginResponse.Error.Code);
        Assert.Equal("Authentication failed", message.AsLoginResponse.Error.Message);
    }

    [Fact]
    public void HeartbeatSchedulerTracksTimingAndTimeout()
    {
        var interval = TimeSpan.FromSeconds(30);
        var timeout = TimeSpan.FromSeconds(60);
        var heartbeat = new McsHeartbeat(interval, timeout);

        var start = DateTimeOffset.UtcNow;
        heartbeat.OnConnected();

        Assert.False(heartbeat.ShouldSendPing(start));
        Assert.False(heartbeat.IsTimedOut(start));

        // After interval has passed
        var afterInterval = start + TimeSpan.FromSeconds(31);
        Assert.True(heartbeat.ShouldSendPing(afterInterval));

        // Ping created
        var ping = heartbeat.CreatePing(streamId: 5);
        Assert.Equal(5, ping.StreamId);

        // Before timeout
        var beforeTimeout = heartbeat.LastPingSentAt + TimeSpan.FromSeconds(40);
        Assert.False(heartbeat.IsTimedOut(beforeTimeout));

        // After timeout
        var afterTimeout = heartbeat.LastPingSentAt + TimeSpan.FromSeconds(65);
        Assert.True(heartbeat.IsTimedOut(afterTimeout));

        // When ACK received, timeout is cleared
        heartbeat.OnAckReceived();
        Assert.False(heartbeat.IsTimedOut(afterTimeout));
    }

    [Fact]
    public void CalculateBackoffIsBoundedWithExponentialGrowth()
    {
        var min = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(30);
        var fixedRandom = new Random(42);

        var d1 = McsClient.CalculateBackoff(1, min, max, fixedRandom);
        var d2 = McsClient.CalculateBackoff(2, min, max, fixedRandom);
        var d3 = McsClient.CalculateBackoff(3, min, max, fixedRandom);
        var d10 = McsClient.CalculateBackoff(10, min, max, fixedRandom);

        Assert.InRange(d1.TotalSeconds, 0.8, 1.2);
        Assert.InRange(d2.TotalSeconds, 1.6, 2.4);
        Assert.InRange(d3.TotalSeconds, 3.2, 4.8);
        Assert.Equal(30.0, d10.TotalSeconds); // Capped at max
    }

    [Fact]
    public async Task McsLoginThrowsExpectedExceptionOnErrorResponse()
    {
        var fakeConn = new FakeMcsConnection();
        var errorResponse = new LoginResponse
        {
            Id = "android-33",
            Error = new ErrorInfo { Code = 403, Message = "Bad authentication" }
        };

        fakeConn.EnqueueReceive(McsConstants.LoginResponseTag, errorResponse);

        var req = McsLogin.BuildRequest(12345, 67890);
        var ex = await Assert.ThrowsAsync<McsLoginException>(() =>
            McsLogin.PerformLoginAsync(fakeConn, req));

        Assert.Equal(403, ex.ErrorCode);
        Assert.Contains("Bad authentication", ex.Message);
    }

    [Fact]
    public async Task McsLoginSucceedsOnValidResponse()
    {
        var fakeConn = new FakeMcsConnection();
        var successResponse = new LoginResponse
        {
            Id = "android-33",
            ServerTimestamp = 1700000000
        };

        fakeConn.EnqueueReceive(McsConstants.LoginResponseTag, successResponse);

        var req = McsLogin.BuildRequest(12345, 67890);
        var response = await McsLogin.PerformLoginAsync(fakeConn, req);

        Assert.Equal("android-33", response.Id);
        Assert.Null(response.Error);
    }

    private sealed class FakeMcsConnection : IMcsConnection
    {
        private readonly Queue<McsMessage> _receiveQueue = new();
        public List<(int Tag, IMessage Msg)> SentMessages { get; } = new();

        public bool IsConnected { get; private set; } = true;
        public int RemoteVersion { get; set; } = 41;

        public void EnqueueReceive(int tag, IMessage message)
        {
            _receiveQueue.Enqueue(new McsMessage { Tag = tag, Payload = message });
        }

        public Task ConnectAsync(string host, int port, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandshakeVersionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendAsync(int tag, IMessage message, CancellationToken cancellationToken)
        {
            SentMessages.Add((tag, message));
            return Task.CompletedTask;
        }

        public Task<McsMessage?> ReceiveAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_receiveQueue.Count > 0 ? _receiveQueue.Dequeue() : null);
        }

        public void Close() => IsConnected = false;
        public void Dispose() => Close();
        public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
    }
}

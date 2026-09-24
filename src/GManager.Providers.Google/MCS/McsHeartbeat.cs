namespace GManager.Providers.Google.Mcs;

public sealed class McsHeartbeat(TimeSpan? interval = null, TimeSpan? ackTimeout = null)
{
    public TimeSpan Interval { get; set; } = interval ?? McsConstants.DefaultHeartbeatInterval;
    public TimeSpan AckTimeout { get; set; } = ackTimeout ?? McsConstants.HeartbeatAckTimeout;

    public DateTimeOffset LastPingSentAt { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastAckReceivedAt { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset ConnectedAt { get; private set; } = DateTimeOffset.MinValue;

    public void OnConnected()
    {
        var now = DateTimeOffset.UtcNow;
        ConnectedAt = now;
        LastPingSentAt = now;
        LastAckReceivedAt = now;
    }

    public HeartbeatPing CreatePing(int streamId = 0)
    {
        LastPingSentAt = DateTimeOffset.UtcNow;
        return new HeartbeatPing
        {
            StreamId = streamId,
            Status = 1
        };
    }

    public HeartbeatAck CreateAck(HeartbeatPing? ping = null)
    {
        return new HeartbeatAck
        {
            Status = ping?.Status ?? 1,
            StreamId = ping?.StreamId ?? 0
        };
    }

    public void OnAckReceived()
    {
        LastAckReceivedAt = DateTimeOffset.UtcNow;
    }

    public bool ShouldSendPing(DateTimeOffset now)
    {
        return now - LastPingSentAt >= Interval;
    }

    public bool IsTimedOut(DateTimeOffset now)
    {
        if (LastPingSentAt > LastAckReceivedAt)
        {
            return now - LastPingSentAt > AckTimeout;
        }
        return false;
    }
}

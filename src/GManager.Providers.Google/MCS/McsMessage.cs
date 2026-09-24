using Google.Protobuf;

namespace GManager.Providers.Google.Mcs;

public sealed class McsMessage
{
    public required int Tag { get; init; }
    public int StreamId { get; init; }
    public required IMessage Payload { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public DataMessageStanza? AsDataMessage => Payload as DataMessageStanza;
    public HeartbeatPing? AsHeartbeatPing => Payload as HeartbeatPing;
    public HeartbeatAck? AsHeartbeatAck => Payload as HeartbeatAck;
    public LoginResponse? AsLoginResponse => Payload as LoginResponse;
    public Close? AsClose => Payload as Close;
    public IqStanza? AsIqStanza => Payload as IqStanza;

    public override string ToString() => $"McsMessage [Tag={Tag}, StreamId={StreamId}, Type={Payload?.GetType().Name}]";
}

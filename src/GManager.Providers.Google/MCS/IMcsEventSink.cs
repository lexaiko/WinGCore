namespace GManager.Providers.Google.Mcs;

public enum McsConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Stopped
}

public interface IMcsEventSink
{
    void OnStateChanged(McsConnectionState previous, McsConnectionState current, string? reason = null);
    void OnMessageReceived(McsMessage message);
    void OnError(Exception exception, string context);
    void OnLog(string message);
}

public sealed class NullMcsEventSink : IMcsEventSink
{
    public static readonly NullMcsEventSink Instance = new();
    public void OnStateChanged(McsConnectionState previous, McsConnectionState current, string? reason = null) { }
    public void OnMessageReceived(McsMessage message) { }
    public void OnError(Exception exception, string context) { }
    public void OnLog(string message) { }
}

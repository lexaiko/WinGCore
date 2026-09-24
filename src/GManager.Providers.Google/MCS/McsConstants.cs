namespace GManager.Providers.Google.Mcs;

public static class McsConstants
{
    public const int HeartbeatPingTag = 0;
    public const int HeartbeatAckTag = 1;
    public const int LoginRequestTag = 2;
    public const int LoginResponseTag = 3;
    public const int CloseTag = 4;
    public const int IqStanzaTag = 7;
    public const int DataMessageStanzaTag = 8;

    public const byte VersionCode = 41;

    public const string DefaultHost = "mtalk.google.com";
    public const int DefaultPort = 5228;
    public const int FallbackPort = 443;
    public static readonly int[] DefaultPorts = [DefaultPort, FallbackPort];

    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HeartbeatAckTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
}

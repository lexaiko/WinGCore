using System.Net.Security;
using System.Net.Sockets;
using Google.Protobuf;

namespace GManager.Providers.Google.Mcs;

public interface IMcsConnection : IAsyncDisposable, IDisposable
{
    bool IsConnected { get; }
    int RemoteVersion { get; }
    Task ConnectAsync(string host, int port, CancellationToken cancellationToken);
    Task HandshakeVersionAsync(CancellationToken cancellationToken);
    Task SendAsync(int tag, IMessage message, CancellationToken cancellationToken);
    Task<McsMessage?> ReceiveAsync(CancellationToken cancellationToken);
    void Close();
}

public sealed class McsConnection : IMcsConnection
{
    private TcpClient? _tcpClient;
    private SslStream? _sslStream;
    private bool _isDisposed;

    public bool IsConnected => _tcpClient is { Connected: true } && _sslStream != null && !_isDisposed;
    public int RemoteVersion { get; private set; } = -1;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        Close();
        _isDisposed = false;

        _tcpClient = new TcpClient
        {
            NoDelay = true,
            ReceiveTimeout = (int)McsConstants.HeartbeatAckTimeout.TotalMilliseconds,
            SendTimeout = 15000
        };

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(McsConstants.ConnectTimeout);

        await _tcpClient.ConnectAsync(host, port, linkedCts.Token);

        _sslStream = new SslStream(_tcpClient.GetStream(), false);
        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = host
        };

        await _sslStream.AuthenticateAsClientAsync(sslOptions, linkedCts.Token);
    }

    public async Task HandshakeVersionAsync(CancellationToken cancellationToken)
    {
        if (_sslStream == null) throw new InvalidOperationException("TLS stream is not connected.");

        // Client writes version byte 41
        await McsProtocol.WriteVersionAsync(_sslStream, cancellationToken);

        // Server writes version byte 41
        RemoteVersion = await McsProtocol.ReadVersionAsync(_sslStream, cancellationToken);
    }

    public async Task SendAsync(int tag, IMessage message, CancellationToken cancellationToken)
    {
        if (_sslStream == null) throw new InvalidOperationException("Not connected to MCS server.");
        await McsProtocol.WriteMessageAsync(_sslStream, tag, message, cancellationToken);
    }

    public async Task<McsMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_sslStream == null) return null;
        return await McsProtocol.ReadMessageAsync(_sslStream, cancellationToken);
    }

    public void Close()
    {
        _isDisposed = true;
        try { _sslStream?.Dispose(); } catch { }
        try { _tcpClient?.Close(); } catch { }
        _sslStream = null;
        _tcpClient = null;
    }

    public void Dispose() => Close();

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}

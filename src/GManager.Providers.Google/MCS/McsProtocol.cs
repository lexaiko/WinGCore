using Google.Protobuf;

namespace GManager.Providers.Google.Mcs;

public static class McsProtocol
{
    public static async Task WriteVersionAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var buf = new[] { McsConstants.VersionCode };
        await stream.WriteAsync(buf.AsMemory(0, 1), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<int> ReadVersionAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var buf = new byte[1];
        int read = await stream.ReadAsync(buf.AsMemory(0, 1), cancellationToken);
        if (read <= 0) throw new EndOfStreamException("Server closed connection during version handshake.");
        return buf[0];
    }

    public static async Task WriteVarintAsync(Stream stream, int value, CancellationToken cancellationToken = default)
    {
        var buffer = new List<byte>(5);
        while (true)
        {
            if ((value & ~0x7F) == 0)
            {
                buffer.Add((byte)value);
                break;
            }
            buffer.Add((byte)((value & 0x7F) | 0x80));
            value >>>= 7;
        }
        await stream.WriteAsync(buffer.ToArray().AsMemory(), cancellationToken);
    }

    public static async Task<int> ReadVarintAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        int res = 0, shift = -7;
        var buf = new byte[1];
        while (shift < 35)
        {
            int read = await stream.ReadAsync(buf.AsMemory(0, 1), cancellationToken);
            if (read <= 0) throw new EndOfStreamException("Server closed connection while reading varint.");
            byte b = buf[0];
            shift += 7;
            res |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return res;
        }
        throw new InvalidDataException("Varint exceeds 32 bits.");
    }

    public static async Task WriteMessageAsync(Stream stream, int tag, IMessage message, CancellationToken cancellationToken = default)
    {
        byte[] payload = message.ToByteArray();
        await stream.WriteAsync(new[] { (byte)tag }.AsMemory(), cancellationToken);
        await WriteVarintAsync(stream, payload.Length, cancellationToken);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload.AsMemory(), cancellationToken);
        }
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<McsMessage?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var tagBuf = new byte[1];
        int read = await stream.ReadAsync(tagBuf.AsMemory(0, 1), cancellationToken);
        if (read <= 0) return null; // Clean EOF
        int tag = tagBuf[0];

        int size = await ReadVarintAsync(stream, cancellationToken);
        if (size < 0 || size > 1024 * 1024) throw new InvalidDataException($"Invalid MCS packet size: {size}");

        byte[] payload = new byte[size];
        int totalRead = 0;
        while (totalRead < size)
        {
            int chunk = await stream.ReadAsync(payload.AsMemory(totalRead, size - totalRead), cancellationToken);
            if (chunk <= 0) throw new EndOfStreamException($"Connection terminated mid-packet (read {totalRead}/{size}).");
            totalRead += chunk;
        }

        IMessage parsed = tag switch
        {
            McsConstants.HeartbeatPingTag => HeartbeatPing.Parser.ParseFrom(payload),
            McsConstants.HeartbeatAckTag => HeartbeatAck.Parser.ParseFrom(payload),
            McsConstants.LoginRequestTag => LoginRequest.Parser.ParseFrom(payload),
            McsConstants.LoginResponseTag => LoginResponse.Parser.ParseFrom(payload),
            McsConstants.CloseTag => Close.Parser.ParseFrom(payload),
            McsConstants.IqStanzaTag => IqStanza.Parser.ParseFrom(payload),
            McsConstants.DataMessageStanzaTag => DataMessageStanza.Parser.ParseFrom(payload),
            _ => throw new InvalidDataException($"Unknown MCS packet tag: {tag}")
        };

        return new McsMessage { Tag = tag, Payload = parsed };
    }
}

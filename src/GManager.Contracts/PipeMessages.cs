using System.Buffers.Binary;
using System.Text.Json;

namespace GManager.Contracts;

public static class PipeMessages
{
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > RuntimeProtocol.MaxMessageBytes)
            throw new InvalidDataException("Invalid runtime message length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, RuntimeProtocol.Json)
            ?? throw new JsonException("Empty runtime message.");
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, RuntimeProtocol.Json);
        if (bytes.Length > RuntimeProtocol.MaxMessageBytes) throw new InvalidDataException("Runtime message is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

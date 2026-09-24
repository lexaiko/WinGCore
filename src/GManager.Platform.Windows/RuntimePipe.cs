using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using GManager.Contracts;
using GManager.Runtime;

namespace GManager.Platform.Windows;

public static class RuntimePipe
{
    public static string NameFor(string directory)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows user SID unavailable.");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + "|" + path)))[..24];
        return "GManager.Runtime.v1." + hash;
    }

    public static async Task<RuntimeResponse> SendAsync(string pipeName, RuntimeRequest request, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000, cancellationToken);
        await PipeMessages.WriteAsync(pipe, request, cancellationToken);
        return await PipeMessages.ReadAsync<RuntimeResponse>(pipe, cancellationToken);
    }

    public static async Task ServeAsync(string pipeName, RuntimeService service, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            await pipe.WaitForConnectionAsync(cancellationToken);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(50));
            var token = requestTimeout.Token;
            try
            {
                // An incomplete client must not monopolize the only command connection.
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await PipeMessages.ReadAsync<RuntimeRequest>(pipe, readTimeout.Token);
                var stop = request.Version == RuntimeProtocol.Version && request.Operation == "stop";
                var response = stop
                    ? new RuntimeResponse(RuntimeProtocol.Version, true, "Stopped", "Runtime stopping.")
                    : await service.HandleAsync(request, token);
                await PipeMessages.WriteAsync(pipe, response, token);
                if (stop) return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
            catch (JsonException) { }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Storage/provider exceptions may contain private data; return only a stable code.
                try
                {
                    await PipeMessages.WriteAsync(pipe, new RuntimeResponse(RuntimeProtocol.Version, false,
                        "RuntimeError", "Runtime operation failed. Stored credentials were not reset."), token);
                }
                catch (IOException) { }
                catch (OperationCanceledException) { }
            }
        }
    }
}

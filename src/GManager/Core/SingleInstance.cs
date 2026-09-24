using System.IO;
using System.IO.Pipes;
using System.Text;

namespace GManager.Core;

/// <summary>
/// Single instance manager using a system Mutex and Named Pipe IPC.
/// If an instance is already running, passes CLI arguments/commands to the active instance and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _isFirstInstance;
    private CancellationTokenSource? _pipeCts;

    public bool IsFirstInstance => _isFirstInstance;

    public event Action<string>? MessageReceived;

    public SingleInstance(string mutexName = AppConfig.MutexName)
    {
        _mutex = new Mutex(true, mutexName, out _isFirstInstance);
    }

    /// <summary>
    /// Starts listening on Named Pipe if this is the first instance.
    /// </summary>
    public void StartIpcServer(string pipeName = AppConfig.IpcPipeName)
    {
        if (!_isFirstInstance) return;

        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var message = await reader.ReadToEndAsync(token);

                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        MessageReceived?.Invoke(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore pipe transmission glitches and continue listening
                    await Task.Delay(500, token);
                }
            }
        }, token);
    }

    /// <summary>
    /// Sends a message to the existing running instance via Named Pipe.
    /// </summary>
    public static async Task<bool> SendMessageToExistingInstanceAsync(string message, string pipeName = AppConfig.IpcPipeName, int timeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            await client.ConnectAsync(timeoutMs);

            using var writer = new StreamWriter(client, Encoding.UTF8);
            await writer.WriteAsync(message);
            await writer.FlushAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();

        if (_isFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Mutex could already be released or abandoned
            }
        }
        _mutex.Dispose();
    }
}

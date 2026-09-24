using System.Diagnostics;
using System.IO;
using GManager.Contracts;
using GManager.Core;
using GManager.Platform.Windows;

namespace GManager.Auth;

public sealed class NativeRuntimeClient(AppConfig config)
{
    private readonly SemaphoreSlim _startup = new(1, 1);
    public string DataDirectory => Path.Combine(config.AppDataDirectory, "runtime");

    public async Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(55));
        var response = await RuntimePipe.SendAsync(RuntimePipe.NameFor(DataDirectory), request, timeout.Token);
        if (!response.Success) throw new NativeRuntimeException(response.Code, response.Message);
        return response;
    }

    private async Task EnsureStartedAsync(CancellationToken token)
    {
        await _startup.WaitAsync(token);
        try
        {
            try
            {
                var status = await RuntimePipe.SendAsync(RuntimePipe.NameFor(DataDirectory), new(1, "status"), token);
                if (!status.Success) throw new NativeRuntimeException(status.Code, status.Message);
                return;
            }
            catch (TimeoutException) { }
            var executable = Path.Combine(AppContext.BaseDirectory, "NativeRuntime", "GManager.RuntimeHost.exe");
            if (!File.Exists(executable)) throw new InvalidOperationException("Native runtime is missing. Rebuild or reinstall GManager.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("serve");
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(DataDirectory);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start native runtime.");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await RuntimePipe.SendAsync(RuntimePipe.NameFor(DataDirectory), new(1, "status"), token);
                    return;
                }
                catch (TimeoutException) when (attempt < 2) { }
            }
        }
        finally { _startup.Release(); }
    }

    public async Task<NativeSessionSummary[]> SessionsAsync(CancellationToken token = default) =>
        (await SendAsync(new(1, "sessions"), token)).Sessions
        ?? throw new InvalidOperationException("Native runtime returned an incomplete account inventory. Restart GManager and its runtime.");

    public async Task<NativeGrant> GrantAsync(Guid sessionId, NativeService service, bool force, CancellationToken token) =>
        (await SendAsync(new(1, "get-grant", SessionId: sessionId, Service: service, ForceRefresh: force), token)).Grant
        ?? throw new InvalidOperationException("Native runtime did not return a service grant.");
}

public sealed class NativeRuntimeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

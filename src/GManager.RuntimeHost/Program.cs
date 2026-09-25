using System.Net;
using System.Text.Json;
using GManager.Contracts;
using GManager.Platform.Windows;
using GManager.Providers.Google;
using GManager.Runtime;

var arguments = args.ToList();
var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GManager", "runtime");
var dataOption = arguments.IndexOf("--data-dir");
if (dataOption >= 0)
{
    if (dataOption + 1 >= arguments.Count) return Usage();
    directory = Path.GetFullPath(arguments[dataOption + 1]);
    arguments.RemoveRange(dataOption, 2);
}
if (arguments.Count == 0 || arguments[0] is "--help" or "help") return Usage();
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
try
{
    var pipe = RuntimePipe.NameFor(directory);
    if (arguments[0] == "serve" && arguments.Count == 1)
    {
        Directory.CreateDirectory(directory);
        using var lease = new FileStream(Path.Combine(directory, "runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var store = new WindowsDeviceStore(Path.Combine(directory, "runtime.db"));
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false, // Critical: bypasses Windows WPAD proxy auto-discovery delay (~2-3s)
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
        var broker = new NativeAccountBroker(store, store, new GoogleNativeAuthProvider(http));
        var service = new RuntimeService(store, new GoogleCheckinProvider(http), broker);
        Console.WriteLine("Native runtime ready. Use GManager for interactive account sign-in.");

        var sessions = store.ListSessions();
        var primarySession = sessions.FirstOrDefault();
        GManager.Providers.Google.Mcs.McsClient? mcsClient = null;
        if (primarySession != null)
        {
            Console.WriteLine($"[MCS] Starting persistent MCS transport for {primarySession.Email} ({primarySession.Id})...");
            mcsClient = GManager.Providers.Google.Mcs.McsClient.Create(store, broker, primarySession.Id, new ConsoleMcsEventSink(), runtimeService: service);
            mcsClient.Start();
        }

        try
        {
            await RuntimePipe.ServeAsync(pipe, service, shutdown.Token);
        }
        finally
        {
            if (mcsClient != null)
            {
                Console.WriteLine("[MCS] Stopping MCS transport...");
                await mcsClient.StopAsync();
                mcsClient.Dispose();
            }
        }
        return 0;
    }
    RuntimeRequest request;
    switch (arguments[0])
    {
        case "status" or "list" or "stop" or "sessions" when arguments.Count == 1:
            request = new(RuntimeProtocol.Version, arguments[0]);
            break;
        case "create" when arguments.Count == 2:
            if (new FileInfo(arguments[1]).Length > 32 * 1024) throw new ArgumentException("Profile exceeds 32 KiB.");
            var profile = JsonSerializer.Deserialize<DeviceProfile>(await File.ReadAllTextAsync(arguments[1]), RuntimeProtocol.Json)
                ?? throw new ArgumentException("Profile is empty.");
            profile.Validate();
            request = new(RuntimeProtocol.Version, "create", profile);
            break;
        case "checkin" when arguments.Count == 2 && Guid.TryParse(arguments[1], out var id):
            request = new(RuntimeProtocol.Version, "checkin", DeviceId: id);
            break;
        case "forget" when arguments.Count == 2 && Guid.TryParse(arguments[1], out var sessionId):
            request = new(RuntimeProtocol.Version, "forget-session", SessionId: sessionId);
            break;
        case "finish-setup" when arguments.Count == 2 && Guid.TryParse(arguments[1], out var setupId):
            request = new(RuntimeProtocol.Version, "finish-setup", SessionId: setupId);
            break;
        default: return Usage();
    }
    shutdown.CancelAfter(TimeSpan.FromSeconds(55));
    var response = await RuntimePipe.SendAsync(pipe, request, shutdown.Token);
    Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions(RuntimeProtocol.Json) { WriteIndented = true }));
    return response.Success ? 0 : 2;
}
catch (TimeoutException)
{
    Console.Error.WriteLine("Runtime unavailable. Start 'serve' with the same --data-dir first.");
    return 3;
}
catch (OperationCanceledException) { return 4; }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
catch (JsonException) { Console.Error.WriteLine("Invalid profile JSON."); return 2; }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
{
    Console.Error.WriteLine("Cannot access runtime storage or IPC. Check permissions and whether a runtime is already running.");
    return 3;
}

static int Usage()
{
    Console.WriteLine("""
        GManager native runtime (experimental)
          serve                    Run the per-user background service
          status                   Show runtime capabilities
          create <profile.json>    Create a persistent local virtual device
          list                     List the first 100 devices (no secrets)
          checkin <device-id>       Send native Google check-in for this device
          stop                     Stop the service
          sessions                 List native account sessions (no secrets)
          finish-setup <session-id> Complete GMS account setup and account check-in
          forget <session-id>      Remove a native session locally
        Optional: --data-dir <directory> on each command to select isolated storage.
        Interactive enrollment and service grants are available through the WPF app.
        Play Integrity attestation and Android APK execution are not implemented.
        """);
    return 0;
}

sealed class ConsoleMcsEventSink : GManager.Providers.Google.Mcs.IMcsEventSink
{
    public void OnStateChanged(GManager.Providers.Google.Mcs.McsConnectionState previous, GManager.Providers.Google.Mcs.McsConnectionState current, string? reason = null)
    {
        Console.WriteLine($"[MCS] State: {previous} -> {current}" + (reason != null ? $" ({reason})" : ""));
    }

    public void OnMessageReceived(GManager.Providers.Google.Mcs.McsMessage message)
    {
        Console.WriteLine($"[MCS] Received Tag={message.Tag} ({message.Payload.GetType().Name}) [{message.Payload.CalculateSize()} bytes]");
    }

    public void OnError(Exception exception, string context)
    {
        Console.Error.WriteLine($"[MCS] Error in {context}: {exception.Message}");
    }

    public void OnLog(string message)
    {
        Console.WriteLine($"[MCS] {message}");
    }
}

using System.IO;
using System.Net.Http;
using GManager.Auth;
using GManager.Contracts;
using GManager.Core;
using GManager.Platform.Windows;
using GManager.Providers.Google;
using GManager.Providers.Google.Mcs;
using GManager.Runtime;

namespace GManager.Services;

/// <summary>
/// Connects the native MCS (Google Cloud Messaging) persistent transport to local desktop sync.
/// When Google pushes an incoming mail or data message packet over MCS, this coordinator triggers
/// instantaneous background synchronization without polling.
/// </summary>
public sealed class McsPushCoordinator : IDisposable, IAsyncDisposable
{
    private readonly SyncService _syncService;
    private readonly Database _database;
    private readonly AppConfig _config;
    private readonly IEventAggregator _eventAggregator;
    private readonly HttpClient _httpClient;

    private readonly List<McsClient> _clients = [];
    private readonly object _lock = new();
    private bool _isDisposed;

    public McsPushCoordinator(
        SyncService syncService,
        Database database,
        AppConfig config,
        IEventAggregator eventAggregator,
        HttpClient httpClient)
    {
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Starts MCS listeners for all active native device accounts.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        var dbPath = Path.Combine(_config.AppDataDirectory, "runtime", "runtime.db");
        if (!File.Exists(dbPath)) return;

        try
        {
            var store = new WindowsDeviceStore(dbPath);
            var sessions = store.ListSessions();
            if (sessions.Length == 0) return;

            var authProvider = new GoogleNativeAuthProvider(_httpClient);
            var broker = new NativeAccountBroker(store, store, authProvider);

            lock (_lock)
            {
                if (_isDisposed) return;

                foreach (var session in sessions)
                {
                    var localAccountId = NativeAccountCoordinator.LocalId(session.Id);
                    var eventSink = new McsPushEventSink(
                        async () =>
                        {
                            // Trigger instant push sync for the account
                            await _syncService.SyncAccountNowAsync(localAccountId);
                        });

                    var client = McsClient.Create(store, broker, session.Id, eventSink);
                    client.Start();
                    _clients.Add(client);
                }
            }
        }
        catch
        {
            // Silently degrade if runtime store is busy or unavailable
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            foreach (var client in _clients)
            {
                try { client.Dispose(); }
                catch { }
            }
            _clients.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<McsClient> toStop;
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            toStop = [.. _clients];
            _clients.Clear();
        }

        foreach (var client in toStop)
        {
            try
            {
                await client.StopAsync();
                client.Dispose();
            }
            catch { }
        }
    }

    private sealed class McsPushEventSink(Func<Task> onPushReceived) : IMcsEventSink
    {
        public void OnStateChanged(McsConnectionState previous, McsConnectionState current, string? reason = null)
        {
            // Connection state tracking
        }

        public void OnMessageReceived(McsMessage message)
        {
            // Detect incoming data messages (Tag 8) or IQ stanzas (Tag 7) from Google
            if (message.Tag is McsConstants.DataMessageStanzaTag or McsConstants.IqStanzaTag)
            {
                _ = Task.Run(async () =>
                {
                    try { await onPushReceived(); }
                    catch { }
                });
            }
        }

        public void OnError(Exception exception, string context) { }

        public void OnLog(string message) { }
    }
}

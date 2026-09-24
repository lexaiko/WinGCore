using System.Web;

namespace GManager.Auth;

/// <summary>
/// iOS-style custom URL scheme OAuth handler.
///
/// On iOS, ASWebAuthenticationSession delivers the OAuth code via a custom URL scheme
/// (e.g. com.googleusercontent.apps.XXX:/oauth2redirect?code=...).
/// On Windows, we register gmanager:// in the Windows Registry. When Google redirects
/// there, the OS launches GManager (or the running instance) with the full URI as a
/// command-line argument.  The second instance sends it over the Named Pipe, and the
/// first instance calls <see cref="DeliverUri"/> to fulfill any pending login awaiter.
/// </summary>
public static class ProtocolActivationHandler
{
    private static TaskCompletionSource<Uri>? _pendingTcs;
    private static readonly object _lock = new();

    /// <summary>
    /// Returns an awaitable task that completes when the OS activates us with an
    /// OAuth redirect URI.  Only one pending activation is allowed at a time.
    /// </summary>
    public static Task<Uri> WaitForActivationAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // Cancel any previous stale waiter
            _pendingTcs?.TrySetCanceled();
            _pendingTcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var tcs = _pendingTcs;

        // Apply timeout
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        cts.Token.Register(() =>
        {
            tcs.TrySetException(new TimeoutException(
                "Google Sign-In timed out. The browser may have been closed before authorisation was completed."));
            cts.Dispose();
        });

        return tcs.Task;
    }

    /// <summary>
    /// Called by App.xaml.cs when the IPC pipe delivers a gmanager:// URI from
    /// the second (protocol-activated) instance.
    /// </summary>
    public static void DeliverUri(string rawUri)
    {
        if (Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
        {
            DeliverUri(uri);
        }
    }

    /// <summary>Deliver a parsed URI to the waiting caller.</summary>
    public static void DeliverUri(Uri uri)
    {
        TaskCompletionSource<Uri>? tcs;
        lock (_lock) { tcs = _pendingTcs; }
        tcs?.TrySetResult(uri);
    }

    /// <summary>
    /// Parses the authorization code and state from a gmanager:/oauth/callback?code=...
    /// URI delivered by Google.
    /// </summary>
    public static (string? Code, string? State, string? Error) ParseCallback(Uri uri)
    {
        var qs = HttpUtility.ParseQueryString(uri.Query);
        return (qs["code"], qs["state"], qs["error"]);
    }
}

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GManager.Auth;

public record LoopbackResult(string? Code, string? Error, string? ErrorDescription);

/// <summary>
/// Ephemeral HTTP listener on 127.0.0.1 that captures the OAuth redirect callback from the browser.
/// </summary>
public sealed class LoopbackServer : IDisposable
{
    private HttpListener? _listener;
    private int _port;

    public int Port => _port;
    public string RedirectUri => $"http://127.0.0.1:{_port}/";

    public void Start()
    {
        _port = GetAvailableLoopbackPort();
        _listener = new HttpListener();
        _listener.Prefixes.Add(RedirectUri);
        _listener.Start();
    }

    /// <summary>
    /// Waits for incoming OAuth redirect callback, validates state token, and returns authorization code.
    /// </summary>
    public async Task<LoopbackResult> WaitForCallbackAsync(string expectedState, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_listener == null || !_listener.IsListening)
        {
            throw new InvalidOperationException("Loopback server is not started.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        try
        {
            // Asynchronously wait for the HTTP request
            var contextTask = _listener.GetContextAsync();
            var completedTask = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, linkedCts.Token));

            if (completedTask != contextTask)
            {
                throw new TimeoutException("OAuth loopback listener timed out waiting for browser callback.");
            }

            var context = await contextTask;
            var request = context.Request;
            var response = context.Response;

            var query = request.QueryString;
            var receivedState = query["state"];
            var code = query["code"];
            var error = query["error"];
            var errorDesc = query["error_description"];

            bool isSuccess = !string.IsNullOrEmpty(code) && string.Equals(receivedState, expectedState, StringComparison.Ordinal);

            // Respond to browser with a clean, native-styled confirmation page
            var responseHtml = GetResponseHtml(isSuccess, isSuccess ? null : (error ?? "Invalid state parameter"));
            var buffer = Encoding.UTF8.GetBytes(responseHtml);

            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            response.StatusCode = isSuccess ? (int)HttpStatusCode.OK : (int)HttpStatusCode.BadRequest;

            using (var output = response.OutputStream)
            {
                await output.WriteAsync(buffer, linkedCts.Token);
                await output.FlushAsync(linkedCts.Token);
            }

            if (!string.Equals(receivedState, expectedState, StringComparison.Ordinal))
            {
                return new LoopbackResult(null, "state_mismatch", "CSRF state verification failed.");
            }

            return new LoopbackResult(code, error, errorDesc);
        }
        finally
        {
            Stop();
        }
    }

    public void Stop()
    {
        try
        {
            if (_listener != null && _listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
            }
        }
        catch
        {
            // Ignore teardown errors
        }
        finally
        {
            _listener = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private static int GetAvailableLoopbackPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static string GetResponseHtml(bool success, string? errorMessage)
    {
        var title = success ? "Connected to GManager" : "Authentication Failed";
        var statusColor = success ? "#0F9D58" : "#DB4437";
        var iconSvg = success
            ? "<svg width=\"48\" height=\"48\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"#0F9D58\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><polyline points=\"20 6 9 17 4 12\"></polyline></svg>"
            : "<svg width=\"48\" height=\"48\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"#DB4437\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><circle cx=\"12\" cy=\"12\" r=\"10\"></circle><line x1=\"15\" y1=\"9\" x2=\"9\" y2=\"15\"></line><line x1=\"9\" y1=\"9\" x2=\"15\" y2=\"15\"></line></svg>";

        var message = success
            ? "Your Google account was successfully connected.<br>You can safely close this browser tab and return to GManager."
            : $"Could not complete sign in: {WebUtility.HtmlEncode(errorMessage ?? "Unknown error")}.<br>Please return to GManager and try again.";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{title}</title>
    <style>
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI Variable Display', 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            background-color: #F8F9FA;
            color: #202124;
            display: flex;
            align-items: center;
            justify-content: center;
            min-height: 100vh;
            margin: 0;
            padding: 24px;
        }}
        @media (prefers-color-scheme: dark) {{
            body {{
                background-color: #202124;
                color: #E8EAED;
            }}
            .card {{
                background-color: #303134 !important;
                box-shadow: 0 4px 16px rgba(0,0,0,0.4) !important;
            }}
            .subtitle {{
                color: #9AA0A6 !important;
            }}
        }}
        .card {{
            background: #FFFFFF;
            border-radius: 16px;
            padding: 40px 32px;
            max-width: 440px;
            width: 100%;
            text-align: center;
            box-shadow: 0 4px 20px rgba(0,0,0,0.06);
            border: 1px solid rgba(128,128,128,0.15);
        }}
        .icon {{
            margin-bottom: 20px;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            width: 72px;
            height: 72px;
            border-radius: 50%;
            background-color: {statusColor}15;
        }}
        h1 {{
            font-size: 22px;
            font-weight: 600;
            margin: 0 0 12px;
            letter-spacing: -0.2px;
        }}
        .subtitle {{
            font-size: 14px;
            line-height: 1.6;
            color: #5F6368;
            margin: 0;
        }}
    </style>
</head>
<body>
    <div class=""card"">
        <div class=""icon"">
            {iconSvg}
        </div>
        <h1>{title}</h1>
        <p class=""subtitle"">{message}</p>
    </div>
</body>
</html>";
    }
}

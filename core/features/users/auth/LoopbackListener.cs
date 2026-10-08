using System;
using Life.Auth.Desktop;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Grid.Auth.Features;

public enum CaptureKind
{
    OAuthCallback,
    MagicToken,
    Cancelled,
}

/// <summary>What the browser returned on the loopback origin.</summary>
public sealed record RedirectCapture
{
    public CaptureKind Kind { get; init; }
    public string? Code { get; init; }
    public string? State { get; init; }
    public string? MagicToken { get; init; }
    public string? Error { get; init; }
    public string? ErrorDescription { get; init; }
}

/// <summary>
/// Minimal loopback HTTP server used by the native flow and magic email:
///
///  * <c>GET {origin}{basePath}?code=&amp;state=</c>  â€” AUTH's final native
///    redirect (redirectWithAuthorizationCode appends code+state).
///  * <c>GET {origin}/</c> and any non-callback path â€” serves a tiny capture
///    page that reads the <c>#magic_link=</c> / <c>#magic_token=</c> fragment
///    (browsers never send fragments to the server) and POSTs it back to
///    <c>/magic-capture</c>, so the token reaches the app.
///  * <c>POST {origin}/magic-capture</c> â€” body <c>{"magicToken":"..."}</c>.
///  * <c>GET {origin}/magic-capture?magic_token=...</c> â€” direct fallback.
///
/// Built on TcpListener on purpose: no Windows URL ACL reservation, no package,
/// no admin rights. One listener = one capture, then the app closes it.
/// </summary>
public sealed class LoopbackListener : IDisposable
{
    private static readonly Regex MagicFragment = new(
        @"#magic_(?:link|token)=([A-Za-z0-9_-]+)",
        RegexOptions.Compiled);

    private readonly TcpListener _listener;
    private readonly string _basePath;
    private readonly TaskCompletionSource<RedirectCapture> _capture =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private CancellationTokenSource? _loopCts;
    private bool _captured;
    private bool _started;
    private int _disposed;
    private DesktopBrowserReturn? _desktopReturn;
    public void ConfigureDesktop(string appName, Func<Task<bool>> activation)
        => _desktopReturn = new DesktopBrowserReturn(appName, activation, DateTimeOffset.UtcNow);
    public void CompleteDesktop(bool authenticated) => _desktopReturn?.Complete(authenticated);
    public void RetainCompletionPage() => _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => Dispose());

    public LoopbackListener(string host, int port, string basePath)
    {
        var address = host switch
        {
            "::1" => IPAddress.IPv6Loopback,
            "localhost" => IPAddress.Loopback,
            _ => IPAddress.Loopback, // 127.0.0.1 default
        };
        _listener = new TcpListener(address, port);
        if (!basePath.StartsWith('/')) basePath = "/" + basePath;
        _basePath = basePath;
        Origin = $"http://{(host == "::1" ? "[::1]" : host)}:{port}";
        RedirectUri = Origin + basePath;
    }

    public string Origin { get; }
    public string RedirectUri { get; }

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_started) return Task.CompletedTask;
            _started = true;
            _listener.Start();
        }
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => AcceptLoopAsync(_loopCts.Token), ct);
        return Task.CompletedTask;
    }

    /// <summary>Wait for the browser to complete the redirect. Returns null on
    /// timeout or cancellation.</summary>
    public async Task<RedirectCapture?> WaitForCaptureAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await _capture.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _desktopReturn?.Invalidate();
        _loopCts?.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        _loopCts?.Dispose();
        _capture.TrySetResult(new RedirectCapture { Kind = CaptureKind.Cancelled });
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch { return; }
            _ = Task.Run(() => HandleConnectionAsync(client, ct));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(TimeSpan.FromSeconds(10));
                ct = bounded.Token;
                var (method, path, query, body) = await ReadRequestAsync(stream, ct);
                if (_desktopReturn is not null && method == "GET" && path == "/desktop/return.js")
                {
                    await WriteResponseAsync(stream, HttpStatus(200), DesktopBrowserReturn.Script, ct, "application/javascript");
                    return;
                }
                if (_desktopReturn is not null && method == "POST" && (path == "/desktop/status" || path == "/desktop/activate"))
                {
                    var fields = SplitTarget("/?" + body).Query;
                    var nonce = fields.GetValueOrDefault("activation") ?? "";
                    var status = _desktopReturn.Status(nonce, DateTimeOffset.UtcNow);
                    if (status == "expired") { await WriteResponseAsync(stream, HttpStatus(400), "expired", ct, "text/plain"); return; }
                    if (path == "/desktop/activate") {
                        var raised = await _desktopReturn.TryActivateAsync(nonce, DateTimeOffset.UtcNow).WaitAsync(TimeSpan.FromSeconds(5));
                        await WriteResponseAsync(stream, HttpStatus(200), raised ? "foreground" : "user-action-required", ct, "text/plain");
                    } else await WriteResponseAsync(stream, HttpStatus(200), status, ct, "text/plain");
                    return;
                }
                var capture = BuildCapture(method, path, query, body);
                if (capture is not null)
                {
                    if (_desktopReturn is not null && capture.Kind != CaptureKind.MagicToken)
                    {
                        // Render pending, never success, before the host validates and commits.
                        await WriteResponseAsync(stream, HttpStatus(200), _desktopReturn.Html(), ct);
                        TryCapture(capture);
                    }
                    else { await WriteResponseAsync(stream, HttpStatus(200), OkHtml(), ct); TryCapture(capture); }
                }
                else if (method == "POST" && path == "/magic-capture")
                {
                    var token = ParseMagicBody(body);
                    if (token is not null)
                    {
                        TryCapture(new RedirectCapture { Kind = CaptureKind.MagicToken, MagicToken = token });
                        await WriteResponseAsync(stream, HttpStatus(200), OkHtml(), ct);
                    }
                    else
                    {
                        await WriteResponseAsync(stream, HttpStatus(400), TextPlain("bad magic body"), ct);
                    }
                }
                else if (method == "GET" && path == _basePath)
                {
                    // OAuth endpoint reached without code/state (e.g. user
                    // closed the tab / opened it twice).
                    await WriteResponseAsync(stream, HttpStatus(400), TextPlain("No pending callback. Return to the app to retry."), ct);
                }
                else if (method == "GET")
                {
                    // Serve the fragment-capture page for magic email links
                    // (path '/' or any path, e.g. /return).
                    await WriteResponseAsync(stream, HttpStatus(200), MagicCapturePage(), ct);
                }
                else
                {
                    await WriteResponseAsync(stream, HttpStatus(404), TextPlain("not found"), ct);
                }
            }
            catch
            {
                // Never surface socket noise to the app loop.
            }
        }
    }

    private RedirectCapture? BuildCapture(string method, string path, Dictionary<string, string> query, string body)
    {
        if (method == "GET")
        {
            if (path == _basePath)
            {
                var code = query.GetValueOrDefault("code");
                var state = query.GetValueOrDefault("state");
                var error = query.GetValueOrDefault("error");
                if (error is not null)
                {
                    return new RedirectCapture
                    {
                        Kind = CaptureKind.Cancelled,
                        State = state,
                        Error = error,
                        ErrorDescription = query.GetValueOrDefault("error_description"),
                    };
                }
                if (code is not null && state is not null)
                {
                    return new RedirectCapture { Kind = CaptureKind.OAuthCallback, Code = code, State = state };
                }
                return null;
            }
            if (path == "/magic-capture")
            {
                var token = query.GetValueOrDefault("magic_token") ?? query.GetValueOrDefault("state");
                if (token is not null)
                {
                    return new RedirectCapture { Kind = CaptureKind.MagicToken, MagicToken = token };
                }
                return null;
            }
            // Any other GET (/, /return, /?fragment) cannot deliver the magic
            // token over HTTP by itself; the served page POSTs it instead.
            return null;
        }

        if (method == "POST" && path == "/magic-capture")
        {
            var token = ParseMagicBody(body);
            if (token is not null)
            {
                return new RedirectCapture { Kind = CaptureKind.MagicToken, MagicToken = token };
            }
        }
        return null;
    }

    private void TryCapture(RedirectCapture capture)
    {
        lock (_gate)
        {
            if (_captured) return;
            _captured = true;
        }
        _capture.TrySetResult(capture);
    }

    private static string? ParseMagicBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var token = root.TryGetProperty("magicToken", out var t) ? t.GetString() : null;
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string HttpStatus(int code) => code switch
    {
        200 => "HTTP/1.1 200 OK\r\n",
        400 => "HTTP/1.1 400 Bad Request\r\n",
        404 => "HTTP/1.1 404 Not Found\r\n",
        _ => "HTTP/1.1 200 OK\r\n",
    };

    private static string OkHtml()
        => DocumentHtml("<meta http-equiv=\"refresh\" content=\"0;URL=about:blank\" />You can close this window.");

    private static string TextPlain(string text)
    {
        var s = System.Net.WebUtility.HtmlEncode(text);
        return $"<pre>{s}</pre>";
    }

    private static string MagicCapturePage()
    {
        return DocumentHtml("""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>Returning to Gridâ€¦</title></head>
            <body>
              <p>Returning to Gridâ€¦</p>
              <script>
                (function () {
                  var m = location.hash.match(/#magic_(?:link|token)=([A-Za-z0-9_-]+)/);
                  if (!m) { document.body.innerText = 'No magic token found.'; return; }
                  fetch('/magic-capture', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ magicToken: m[1] })
                  }).then(function () {
                    window.close();
                  }).catch(function () {
                    document.body.innerText = 'Returning to Grid failed. Please close this window.';
                  });
                })();
              </script>
            </body></html>
            """);
    }

    private static string DocumentHtml(string inner)
        => "<!doctype html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:system-ui;margin:2rem\">" + inner + "</body></html>";

    private static async Task WriteResponseAsync(Stream stream, string statusLine, string body, CancellationToken ct, string contentType = "text/html; charset=utf-8")
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
            statusLine +
            "Content-Type: " + contentType + "\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            (body.Contains("/desktop/return.js") || contentType == "application/javascript" ? "Content-Security-Policy: " + DesktopBrowserReturn.ContentSecurityPolicy + "\r\n" : "") +
            "Content-Length: " + payload.Length + "\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private async Task<(string Method, string Path, Dictionary<string, string> Query, string Body)> ReadRequestAsync(
        Stream stream, CancellationToken ct)
    {
        var firstLine = await ReadLineAsync(stream, ct);
        var parts = (firstLine ?? "").Split(' ', 3);
        var method = parts.Length > 0 ? parts[0] : "";
        var target = parts.Length > 1 ? parts[1] : "/";

        int contentLength = 0;
        string? requestHost = null, requestOrigin = null;
        int headerBytes = 0;
        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (string.IsNullOrEmpty(line)) break;
            headerBytes += line.Length;
            if (headerBytes > 16384) throw new InvalidDataException("Headers too large");
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) { if (requestHost is not null) throw new InvalidDataException("Duplicate host"); requestHost = line[5..].Trim(); }
            if (line.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) { if (requestOrigin is not null) throw new InvalidDataException("Duplicate origin"); requestOrigin = line[7..].Trim(); }
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
            }
        }

        if (!string.Equals(requestHost, new Uri(Origin).Authority, StringComparison.OrdinalIgnoreCase) ||
            (requestOrigin is not null && !string.Equals(requestOrigin, Origin, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Untrusted loopback origin");
        var (path, query) = SplitTarget(target);
        var body = "";
        if (contentLength < 0 || contentLength > 16384) throw new InvalidDataException("Request too large");
        if (contentLength > 0)
        {
            var buffer = new byte[contentLength];
            var read = 0;
            while (read < contentLength)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, contentLength - read), ct);
                if (n <= 0) break;
                read += n;
            }
            body = Encoding.UTF8.GetString(buffer, 0, read);
        }
        return (method, path, query, body);
    }

    private static (string Path, Dictionary<string, string> Query) SplitTarget(string target)
    {
        var qIndex = target.IndexOf('?');
        if (qIndex < 0) return (target, new Dictionary<string, string>());
        var path = target[..qIndex];
        var query = new Dictionary<string, string>();
        var pieces = target[(qIndex + 1)..].Split('&');
        foreach (var piece in pieces)
        {
            var eq = piece.IndexOf('=');
            if (eq < 0)
            {
                if (!query.TryAdd(System.Net.WebUtility.UrlDecode(piece), "")) throw new InvalidDataException("Duplicate parameter");
            }
            else
            {
                if (!query.TryAdd(System.Net.WebUtility.UrlDecode(piece[..eq]), System.Net.WebUtility.UrlDecode(piece[(eq + 1)..]))) throw new InvalidDataException("Duplicate parameter");
            }
        }
        return (path, query);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new StringBuilder();
        var one = new byte[1];
        while (buffer.Length < 8192)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) break;
            var c = (char)one[0];
            if (c == '\r') continue;
            if (c == '\n') break;
            buffer.Append(c);
        }
        return buffer.Length > 0 ? buffer.ToString() : null;
    }
}

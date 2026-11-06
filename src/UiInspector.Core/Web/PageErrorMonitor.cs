using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace UiInspector.Core.Web;

public enum PageEventKind
{
    /// <summary>An uncaught exception in the page's script.</summary>
    ScriptError,

    /// <summary>console.error, a failed console.assert, or a browser error (CSP, mixed content…).</summary>
    ConsoleError,

    /// <summary>A request answered with 4xx/5xx, or that didn't get an answer (refused, DNS, timeout).</summary>
    FailedRequest,

    /// <summary>A page load or API call that took <see cref="PageErrorMonitor.SlowRequest"/> or longer.</summary>
    SlowRequest,
}

/// <summary>Something that went wrong in a web page while the steps ran.</summary>
public sealed record PageEvent
{
    public required DateTime Time { get; init; }
    public required PageEventKind Kind { get; init; }

    /// <summary>"TypeError: x is undefined", "POST /api/orders → 500 Internal Server Error".</summary>
    public required string Text { get; init; }

    /// <summary>Where: the script file and line, or the request's address (its query string left out).</summary>
    public string? Where { get; init; }

    /// <summary>The page's title.</summary>
    public string Page { get; init; } = "";

    /// <summary>The number of steps done when it happened (0: before the first step).</summary>
    public int AfterStep { get; init; }

    /// <summary>How many times the same thing happened after the same step.</summary>
    public int Count { get; init; } = 1;
}

/// <summary>
/// Follows every page of a Chromium browser (Chrome, Edge, Electron, WebView2) over its
/// remote-debugging port and collects what a developer asks for first: script errors,
/// console errors, failed requests and slow ones. Pages opened later are joined within a few
/// seconds. Nothing in the pages is changed; only localhost ports are used.
/// </summary>
public sealed class PageErrorMonitor : IAsyncDisposable
{
    public static readonly TimeSpan SlowRequest = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int MaxEvents = 200;

    private readonly Lock _gate = new();
    private readonly List<PageEvent> _events = [];
    private readonly Dictionary<int, Task> _ports = [];
    private readonly Dictionary<string, CdpSession> _sessions = [];
    private readonly HashSet<string> _joined = [];
    private readonly CancellationTokenSource _lifetime = new();
    private DateTime _since = DateTime.Now;
    private int _dropped;

    /// <summary>The events, oldest first.</summary>
    public IReadOnlyList<PageEvent> Events
    {
        get { lock (_gate) return [.. _events]; }
    }

    /// <summary>Events left out after the first <c>200</c> (a page that logs an error every frame).</summary>
    public int Dropped
    {
        get { lock (_gate) return _dropped; }
    }

    /// <summary>The debugging ports being followed.</summary>
    public IReadOnlyList<int> Ports
    {
        get { lock (_gate) return [.. _ports.Keys]; }
    }

    /// <summary>Pages being followed (joined and listening).</summary>
    public int PageCount
    {
        get { lock (_gate) return _joined.Count(key => _sessions.TryGetValue(key, out var s) && !s.IsClosed); }
    }

    /// <summary>"recording" or "replay": what the events belong to (set by <see cref="Clear"/>).</summary>
    public string Phase { get; private set; } = "recording";

    /// <summary>The number of steps done at a moment, for <see cref="PageEvent.AfterStep"/>.</summary>
    public Func<DateTime, int> StepAt { get; set; } = _ => 0;

    /// <summary>A new event arrived. Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Forgets the events so far; older ones the browser still reports when a page is joined are skipped too.</summary>
    public void Clear(string phase)
    {
        lock (_gate)
        {
            _events.Clear();
            _dropped = 0;
            _since = DateTime.Now;
        }
        Phase = phase;
        Changed?.Invoke();
    }

    /// <summary>Starts following the browser on this port, if not already. Returns at once.</summary>
    public void Watch(int port)
    {
        if (port <= 0) return;
        lock (_gate)
        {
            if (_ports.ContainsKey(port) || _lifetime.IsCancellationRequested) return;
            _ports[port] = Task.Run(() => FollowPortAsync(port, _lifetime.Token));
        }
    }

    /// <summary>Joins the port's pages now and every few seconds, so new tabs and windows are covered.</summary>
    private async Task FollowPortAsync(int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var targets = await CdpDiscovery.ListTargetsAsync(port, ct);
                foreach (var page in targets.Where(t => t.Type is "page" or "webview" or "app" && !t.Url.StartsWith("devtools://", StringComparison.Ordinal)))
                {
                    var key = $"{port}/{page.Id}";
                    lock (_gate)
                    {
                        if (_sessions.TryGetValue(key, out var open) && !open.IsClosed) continue;
                    }
                    await JoinAsync(key, page, ct);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or CdpException
                                           or System.Net.WebSockets.WebSocketException)
            {
                // The browser may have closed or be restarting; try again on the next round.
            }
            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task JoinAsync(string key, CdpTarget page, CancellationToken ct)
    {
        var session = await CdpSession.ConnectAsync(page.WebSocketDebuggerUrl, ct);
        var requests = new Dictionary<string, Request>();
        session.EventReceived += (method, p) => OnEvent(page, requests, method, p);
        lock (_gate)
        {
            if (_sessions.Remove(key, out var old)) _ = old.DisposeAsync().AsTask();
            _sessions[key] = session;
        }
        // Runtime and Log also report what was logged before joining; Add skips what's older than the recording.
        await session.SendAsync("Runtime.enable", ct: ct);
        await session.SendAsync("Log.enable", ct: ct);
        await session.SendAsync("Network.enable", ct: ct);
        lock (_gate) _joined.Add(key);
    }

    private sealed class Request
    {
        public required string Method { get; init; }
        public required string Url { get; init; }
        public required double Started { get; init; }
        public string Type { get; set; } = "";
        public bool Reported { get; set; }
    }

    private void OnEvent(CdpTarget page, Dictionary<string, Request> requests, string method, JsonElement p)
    {
        switch (method)
        {
            case "Runtime.exceptionThrown":
            {
                var d = p.GetProperty("exceptionDetails");
                var text = d.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var desc)
                    ? FirstLine(desc.GetString())
                    : d.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                Add(page, PageEventKind.ScriptError, text, ScriptPlace(d), WallTime(p, "timestamp"));
                break;
            }
            case "Runtime.consoleAPICalled":
            {
                var type = p.GetProperty("type").GetString();
                if (type is not ("error" or "assert")) break;
                var text = string.Join(" ", p.GetProperty("args").EnumerateArray().Select(ArgumentText));
                var where = p.TryGetProperty("stackTrace", out var stack) && stack.TryGetProperty("callFrames", out var frames)
                    && frames.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } top ? ScriptPlace(top) : null;
                Add(page, PageEventKind.ConsoleError, type == "assert" ? $"Assertion failed: {text}" : text, where, WallTime(p, "timestamp"));
                break;
            }
            case "Log.entryAdded":
            {
                var entry = p.GetProperty("entry");
                // Network errors come from the Network events (with method and status); this keeps CSP, mixed content and the like.
                if (entry.GetProperty("level").GetString() != "error" || entry.GetProperty("source").GetString() is "network" or "javascript") break;
                var url = entry.TryGetProperty("url", out var u) ? Address(u.GetString()) : null;
                Add(page, PageEventKind.ConsoleError, FirstLine(entry.GetProperty("text").GetString()), url, WallTime(entry, "timestamp"));
                break;
            }
            case "Network.requestWillBeSent":
            {
                var request = p.GetProperty("request");
                var url = request.GetProperty("url").GetString() ?? "";
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) break; // data:, blob:, chrome-extension:
                lock (requests)
                {
                    requests[p.GetProperty("requestId").GetString()!] = new Request
                    {
                        Method = request.GetProperty("method").GetString() ?? "GET",
                        Url = url,
                        Started = p.GetProperty("timestamp").GetDouble(),
                        Type = p.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
                    };
                }
                break;
            }
            case "Network.responseReceived":
            {
                if (Find(requests, p) is not { } r) break;
                r.Type = p.TryGetProperty("type", out var type) ? type.GetString() ?? r.Type : r.Type;
                var response = p.GetProperty("response");
                var status = response.GetProperty("status").GetInt32();
                if (status < 400) break;
                var reason = response.TryGetProperty("statusText", out var st) && st.GetString() is { Length: > 0 } s ? s : StatusText(status);
                r.Reported = true;
                Add(page, PageEventKind.FailedRequest, $"{r.Method} {Path(r.Url)} → {status} {reason}".TrimEnd(), Address(r.Url), DateTime.Now);
                break;
            }
            case "Network.loadingFailed":
            {
                if (Remove(requests, p) is not { } r || r.Reported) break;
                // Requests the page or the user stopped (navigating away, a cancelled fetch) aren't failures.
                if (p.TryGetProperty("canceled", out var canceled) && canceled.GetBoolean()) break;
                var error = p.GetProperty("errorText").GetString() ?? "failed";
                if (p.TryGetProperty("blockedReason", out var blocked)) error += $" (blocked: {blocked.GetString()})";
                Add(page, PageEventKind.FailedRequest, $"{r.Method} {Path(r.Url)} → {error}", Address(r.Url), DateTime.Now);
                break;
            }
            case "Network.loadingFinished":
            {
                if (Remove(requests, p) is not { } r || r.Reported) break;
                // Pages and API calls; a slow image or font rarely is the bug.
                if (r.Type is not ("Document" or "XHR" or "Fetch")) break;
                var seconds = p.GetProperty("timestamp").GetDouble() - r.Started;
                if (seconds < SlowRequest.TotalSeconds) break;
                Add(page, PageEventKind.SlowRequest, string.Create(CultureInfo.InvariantCulture, $"{r.Method} {Path(r.Url)} took {seconds:0.0} s"),
                    Address(r.Url), DateTime.Now);
                break;
            }
        }
    }

    private void Add(CdpTarget page, PageEventKind kind, string text, string? where, DateTime time)
    {
        lock (_gate)
        {
            if (time < _since) return;
            var step = StepAt(time);
            var same = _events.FindIndex(e => e.Kind == kind && e.Text == text && e.Where == where && e.AfterStep == step);
            if (same >= 0)
            {
                _events[same] = _events[same] with { Count = _events[same].Count + 1 };
            }
            else if (_events.Count >= MaxEvents)
            {
                _dropped++;
                return;
            }
            else
            {
                _events.Add(new PageEvent { Time = time, Kind = kind, Text = text, Where = where, Page = page.Title, AfterStep = step });
            }
        }
        Changed?.Invoke();
    }

    private static Request? Find(Dictionary<string, Request> requests, JsonElement p)
    {
        lock (requests) return requests.GetValueOrDefault(p.GetProperty("requestId").GetString()!);
    }

    private static Request? Remove(Dictionary<string, Request> requests, JsonElement p)
    {
        lock (requests) return requests.Remove(p.GetProperty("requestId").GetString()!, out var r) ? r : null;
    }

    // Runtime and Log timestamps are milliseconds since 1970; events without one happened now.
    private static DateTime WallTime(JsonElement e, string property) =>
        e.TryGetProperty(property, out var ms) && ms.TryGetDouble(out var value) && value > 1e12
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)value).LocalDateTime
            : DateTime.Now;

    private static string ArgumentText(JsonElement arg) =>
        arg.TryGetProperty("value", out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()
        : arg.TryGetProperty("description", out var d) ? FirstLine(d.GetString())
        : arg.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

    /// <summary>"app.js:42" from a stack frame or exception details (lines are 0-based there).</summary>
    private static string? ScriptPlace(JsonElement e)
    {
        var url = e.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url)) return null;
        var line = e.TryGetProperty("lineNumber", out var l) && l.TryGetInt32(out var n) ? $":{n + 1}" : "";
        return Address(url) + line;
    }

    private static string FirstLine(string? text) => (text ?? "").Split('\n')[0].Trim();

    /// <summary>
    /// The address without its query string and fragment: they can hold tokens, e-mail addresses
    /// or session ids, and the report may be shared. The path is what a developer needs.
    /// </summary>
    internal static string Address(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut] + (url[cut] == '?' ? "?…" : "");
    }

    /// <summary>"/api/orders" for the short text; the full address is in <see cref="PageEvent.Where"/>.</summary>
    private static string Path(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : Address(url);

    private static string StatusText(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        408 => "Request Timeout",
        409 => "Conflict",
        422 => "Unprocessable Content",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "",
    };

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        Task[] loops;
        CdpSession[] sessions;
        lock (_gate)
        {
            loops = [.. _ports.Values];
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }
        await Task.WhenAny(Task.WhenAll(loops), Task.Delay(2000));
        foreach (var session in sessions) await session.DisposeAsync();
        _lifetime.Dispose();
    }
}

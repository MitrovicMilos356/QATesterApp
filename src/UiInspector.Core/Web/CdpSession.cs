using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace UiInspector.Core.Web;

/// <summary>A debuggable target listed by a Chromium DevTools endpoint (/json/list).</summary>
public sealed record CdpTarget(string Id, string Type, string Title, string Url, string WebSocketDebuggerUrl);

public sealed class CdpException(string message) : Exception(message);

/// <summary>Lists targets on a local Chromium remote-debugging port.</summary>
public static class CdpDiscovery
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task<IReadOnlyList<CdpTarget>> ListTargetsAsync(int port, CancellationToken ct = default)
    {
        // Only ever localhost: the debugging port gives full control over the app.
        var json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Where(t => t.TryGetProperty("webSocketDebuggerUrl", out _))
            .Select(t => new CdpTarget(
                t.GetProperty("id").GetString() ?? "",
                t.GetProperty("type").GetString() ?? "",
                t.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                t.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                t.GetProperty("webSocketDebuggerUrl").GetString() ?? ""))
            .ToList();
    }
}

/// <summary>
/// Minimal Chrome DevTools Protocol client: JSON-RPC over one WebSocket, request/response only
/// (events are ignored). Enough to evaluate script and query the DOM and accessibility tree.
/// </summary>
public sealed class CdpSession : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private Task? _receiveLoop;
    private int _nextId;

    private CdpSession()
    {
    }

    public static async Task<CdpSession> ConnectAsync(string webSocketUrl, CancellationToken ct = default)
    {
        var session = new CdpSession();
        session._socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await session._socket.ConnectAsync(new Uri(webSocketUrl), ct);
        session._receiveLoop = Task.Run(session.ReceiveLoopAsync);
        return session;
    }

    /// <summary>Sends a command and returns its "result" object.</summary>
    public async Task<JsonElement> SendAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }

        try
        {
            return await tcs.Task.WaitAsync(CommandTimeout, ct);
        }
        catch (TimeoutException)
        {
            throw new CdpException($"The page didn't answer '{method}' within {CommandTimeout.TotalSeconds:0} s.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Evaluates an expression and returns its value (serialized by the page as JSON).</summary>
    public async Task<JsonElement> EvaluateValueAsync(string expression, CancellationToken ct = default)
    {
        var result = await SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, ct);
        ThrowOnException(result, expression);
        return result.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : default;
    }

    /// <summary>Evaluates an expression that yields a DOM node and returns its remote object id (or null).</summary>
    public async Task<string?> EvaluateObjectIdAsync(string expression, CancellationToken ct = default)
    {
        var result = await SendAsync("Runtime.evaluate", new { expression, returnByValue = false }, ct);
        ThrowOnException(result, expression);
        return result.GetProperty("result").TryGetProperty("objectId", out var id) ? id.GetString() : null;
    }

    private static void ThrowOnException(JsonElement result, string expression)
    {
        if (result.TryGetProperty("exceptionDetails", out var details))
        {
            var text = details.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var d)
                ? d.GetString()
                : details.GetProperty("text").GetString();
            throw new CdpException($"Page script failed: {text} (in {expression[..Math.Min(60, expression.Length)]}…)");
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open && !_closing.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer, _closing.Token);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);

                Dispatch(message.ToArray());
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            foreach (var pending in _pending.Values) pending.TrySetException(new CdpException("The connection to the page closed."));
        }
    }

    private void Dispatch(byte[] utf8)
    {
        using var doc = JsonDocument.Parse(utf8);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idElement) || !_pending.TryGetValue(idElement.GetInt32(), out var tcs)) return;

        if (root.TryGetProperty("error", out var error))
        {
            var text = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
            tcs.TrySetException(new CdpException(text ?? "CDP error"));
        }
        else
        {
            tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        if (_receiveLoop is not null) await Task.WhenAny(_receiveLoop, Task.Delay(1000));
        _socket.Dispose();
        _sendLock.Dispose();
        _closing.Dispose();
    }
}

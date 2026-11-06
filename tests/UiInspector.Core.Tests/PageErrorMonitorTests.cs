using System.Net;
using System.Text.Json;
using UiInspector.Core.Reporting;
using UiInspector.Core.Web;

namespace UiInspector.Core.Tests;

/// <summary>
/// Makes the web fixture page (Edge with a debug port) throw, log errors and call a small local
/// server that answers 500, answers slowly, or isn't there, and checks what the monitor collects.
/// </summary>
public class PageErrorMonitorTests(EdgeWebFixture fixture) : IClassFixture<EdgeWebFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task CollectsScriptErrorsConsoleErrorsAndFailedAndSlowRequests()
    {
        using var server = new TestServer();
        await using var page = await PageAsync();
        // Logged before the monitor started: the browser still reports it on joining, and it's skipped.
        await page.EvaluateValueAsync("console.error('before the recording'), true");
        await Task.Delay(100);

        await using var monitor = new PageErrorMonitor { StepAt = _ => 2 };
        monitor.Watch(fixture.Port);
        await WaitForAsync(() => monitor.PageCount >= 1);

        await page.EvaluateValueAsync(
            "console.error('Order list failed:', 42);" +
            "setTimeout(() => { null.total; }, 0);" +
            $"fetch('{server.Url}api/orders?token=secret-123').catch(() => {{}});" +
            $"fetch('{server.Url}api/slow').catch(() => {{}});" +
            $"fetch('http://127.0.0.1:{TestServer.ClosedPort()}/api/down').catch(() => {{}});" +
            "true");

        await WaitForAsync(() => Task.FromResult(monitor.Events.Count >= 5));
        var events = monitor.Events;
        var texts = string.Join("\n", events.Select(e => $"{e.Kind}: {e.Text} @ {e.Where}"));

        Assert.DoesNotContain(events, e => e.Text.Contains("before the recording"));
        Assert.Contains(events, e => e.Kind == PageEventKind.ConsoleError && e.Text == "Order list failed: 42");
        Assert.Contains(events, e => e.Kind == PageEventKind.ScriptError && e.Text.StartsWith("TypeError", StringComparison.Ordinal));
        var failed = Assert.Single(events, e => e.Kind == PageEventKind.FailedRequest && e.Text.Contains("/api/orders"));
        Assert.Equal("GET /api/orders → 500 Internal Server Error", failed.Text);
        // The query string can hold tokens: it's left out of the report.
        Assert.Equal($"{server.Url}api/orders?…", failed.Where);
        Assert.DoesNotContain("secret-123", texts);
        Assert.Contains(events, e => e.Kind == PageEventKind.FailedRequest && e.Text.Contains("/api/down → net::ERR_CONNECTION_REFUSED"));
        Assert.Contains(events, e => e.Kind == PageEventKind.SlowRequest && e.Text.StartsWith("GET /api/slow took 3.", StringComparison.Ordinal));
        Assert.All(events, e => Assert.Equal(2, e.AfterStep));
        Assert.All(events, e => Assert.Equal(fixture.Title, e.Page));
    }

    [Fact]
    public async Task RepeatedErrorsAreCountedOnce_AndClearStartsOver()
    {
        await using var page = await PageAsync();
        await using var monitor = new PageErrorMonitor();
        monitor.Watch(fixture.Port);
        await WaitForAsync(() => monitor.PageCount >= 1);

        await page.EvaluateValueAsync("for (let i = 0; i < 3; i++) console.error('same thing'); true");
        await WaitForAsync(() => Task.FromResult(monitor.Events.Any(e => e.Text == "same thing" && e.Count == 3)));
        Assert.Single(monitor.Events, e => e.Text == "same thing");

        monitor.Clear("replay");
        Assert.Empty(monitor.Events);
        Assert.Equal("replay", monitor.Phase);
    }

    // The recorder starts following the page with the first step in it, and counts the steps.
    [Fact]
    public async Task Recorder_FollowsThePageOfItsSteps()
    {
        using var automation = new FlaUI.UIA3.UIA3Automation();
        using var worker = new UiaWorker();
        var inspector = new ElementInspector();
        await using var recorder = new Recording.Recorder(new InspectionService(worker, inspector), inspector, new Recording.RecorderOptions
        {
            InputFactory = () => new NoInput(),
            TrackFocusEvents = false,
            FocusOverride = _ => null, // never the user's real focused element
        });
        await recorder.StartAsync();
        var checkout = fixture.FindWindow(automation)!.FindFirstDescendant(automation.ConditionFactory.ByName("Checkout"))!;
        await recorder.ClickOnAsync(checkout, time: 1000);
        await WaitForAsync(() => recorder.Steps.Count == 1 && !recorder.Steps[0].IsGenerating);
        await WaitForAsync(() => recorder.PageErrors.PageCount >= 1);
        Assert.Equal([fixture.Port], recorder.PageErrors.Ports);

        await using var page = await PageAsync();
        await page.EvaluateValueAsync("console.error('after the checkout click'), true");
        await WaitForAsync(() => recorder.PageErrors.Events.Any(e => e.Text == "after the checkout click"));
        Assert.Equal(1, recorder.PageErrors.Events.Single(e => e.Text == "after the checkout click").AfterStep);

        // A new recording starts over.
        recorder.Clear();
        Assert.Empty(recorder.PageErrors.Events);
        await recorder.StopAsync();
    }

    [Fact]
    public void BugReport_ListsPageErrors_OrSaysThereWereNone()
    {
        var withErrors = new BugReport
        {
            Title = "Orders don't load",
            PageErrorsWatched = true,
            PageEvents =
            [
                new PageEvent { Time = DateTime.Now, Kind = PageEventKind.FailedRequest, Text = "GET /api/orders → 500 Internal Server Error", Where = "https://shop.test/api/orders?…", Count = 2 },
                new PageEvent { Time = DateTime.Now, Kind = PageEventKind.ScriptError, Text = "TypeError: <b>x</b> is undefined" },
            ],
        };
        var html = BugReportRenderer.Html(withErrors, new BugReportOptions());
        Assert.Contains("<h2>Errors in the web page</h2>", html);
        Assert.Contains("GET /api/orders → 500 Internal Server Error (×2)", html);
        Assert.Contains("Before step 1", html);
        Assert.Contains("TypeError: &lt;b&gt;x&lt;/b&gt; is undefined", html); // page text is escaped
        Assert.Contains("- Before step 1 · **Failed request:** GET /api/orders → 500 Internal Server Error (×2) (`https://shop.test/api/orders?…`)",
            BugReportRenderer.Text(withErrors, new BugReportOptions()));

        // Followed and nothing went wrong: worth saying. Not followed: no section at all.
        Assert.Contains("None while the steps were replayed", BugReportRenderer.Html(withErrors with { PageEvents = [], PageErrorsPhase = "replay" }, new BugReportOptions()));
        Assert.DoesNotContain("Errors in the web page", BugReportRenderer.Html(withErrors with { PageErrorsWatched = false }, new BugReportOptions()));
        Assert.DoesNotContain("Errors in the web page", BugReportRenderer.Html(withErrors, new BugReportOptions { PageErrors = false }));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<CdpSession> PageAsync()
    {
        var targets = await CdpDiscovery.ListTargetsAsync(fixture.Port);
        var target = targets.First(t => t.Type == "page" && t.Title == fixture.Title);
        return await CdpSession.ConnectAsync(target.WebSocketDebuggerUrl);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        Assert.Fail("Timed out waiting for the page errors.");
    }

    private static Task WaitForAsync(Func<bool> condition) => WaitForAsync(() => Task.FromResult(condition()));

    /// <summary>Answers /api/orders with 500 and /api/slow after 3.2 s; lets any page call it (CORS).</summary>
    private sealed class TestServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public string Url { get; }

        public TestServer()
        {
            Url = $"http://localhost:{ClosedPort()}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        /// <summary>A port nothing listens on (just freed).</summary>
        public static int ClosedPort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                _ = Task.Run(async () =>
                {
                    var path = context.Request.Url!.AbsolutePath;
                    if (path == "/api/slow") await Task.Delay(3200);
                    context.Response.StatusCode = path == "/api/orders" ? 500 : 200;
                    context.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    var body = JsonSerializer.SerializeToUtf8Bytes(new { ok = path != "/api/orders" });
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                });
            }
        }

        public void Dispose() => _listener.Close();
    }
}

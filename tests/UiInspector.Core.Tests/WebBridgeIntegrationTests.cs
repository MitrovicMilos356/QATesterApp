using System.Diagnostics;
using System.Management;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using UiInspector.Core.Selectors;
using UiInspector.Core.Web;

namespace UiInspector.Core.Tests;

/// <summary>
/// Opens Fixtures/web_fixture.html in Microsoft Edge (app mode, throwaway profile, remote
/// debugging on <see cref="Port"/>) at an off-screen position, and closes it afterwards.
/// </summary>
public class EdgeWebFixture : IDisposable
{
    public const string Title = "UiInspector Web Fixture";
    private const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private const int PreferredPort = 9339;

    private readonly string _profile = Path.Combine(Path.GetTempPath(), "uiinspector-edge-" + Guid.NewGuid().ToString("N"));

    /// <summary>9339 when free (so saved snippets are stable), otherwise any free port; 0 without a debug port.</summary>
    public int Port { get; }

    public EdgeWebFixture() : this(withDebugPort: true)
    {
    }

    protected EdgeWebFixture(bool withDebugPort)
    {
        if (!File.Exists(EdgePath)) throw new InvalidOperationException("Microsoft Edge is required for the web bridge tests.");
        Port = !withDebugPort ? 0 : IsPortFree(PreferredPort) ? PreferredPort : FreePort();

        var page = new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "web_fixture.html")).AbsoluteUri;
        var port = withDebugPort ? $"--remote-debugging-port={Port} " : "";
        Process.Start(new ProcessStartInfo(EdgePath,
            // --disable-sync: a fresh profile otherwise signs in with the Windows account and opens a sync dialog.
            // The backgrounding switches keep the off-screen window rendering, as an on-screen app would; Chromium
            // updates its accessibility tree with rendering, so without them changes show up late or not at all.
            $"--user-data-dir=\"{_profile}\" {port}--no-first-run --no-default-browser-check --disable-sync " +
            "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling " +
            // Shell-started, so Edge doesn't inherit the test runner's output handles: a leftover Edge
            // holding them would keep `dotnet test` waiting after the tests finish.
            $"--window-position=-3000,-3000 --window-size=900,820 --app=\"{page}\"") { UseShellExecute = true })?.Dispose();

        using var automation = new UIA3Automation();
        var ready = Retry.WhileFalse(() =>
        {
            var window = FindWindow(automation);
            if (window is null) return false;

            // Chromium builds its page accessibility tree lazily. A tree search doesn't wake it,
            // but a hit-test does (as when the inspector picks at the cursor), so poke the page.
            var r = window.BoundingRectangle;
            automation.FromPoint(new System.Drawing.Point(r.X + r.Width / 2, r.Y + r.Height / 2));
            return window.FindFirstDescendant(automation.ConditionFactory.ByName("Checkout")) is not null;
        }, TimeSpan.FromSeconds(30)).Result;
        if (!ready)
        {
            var window = FindWindow(automation);
            var own = OwnProcessIds();
            var titles = automation.GetDesktop().FindAllChildren()
                .Where(w => own.Contains(w.Properties.ProcessId.ValueOrDefault))
                .Select(w => $"\"{w.Properties.Name.ValueOrDefault}\"");
            var why = window is null
                ? $"no window titled \"{Title}\" from its own {own.Count} Edge processes (their windows: {string.Join(", ", titles)})"
                : "its window appeared, but the page content (the Checkout button) never showed up in UI Automation";
            Dispose();
            throw new InvalidOperationException($"The Edge web fixture didn't appear: {why}.");
        }
    }

    /// <summary>This fixture's own Edge window (matched by process, since other windows may share the title).</summary>
    public AutomationElement? FindWindow(UIA3Automation automation)
    {
        var own = OwnProcessIds();
        return automation.GetDesktop()
            .FindAllChildren(automation.ConditionFactory.ByName(Title))
            .FirstOrDefault(w => own.Contains(w.Properties.ProcessId.ValueOrDefault));
    }

    // Only Edge processes using this fixture's own throwaway profile; never the user's Edge.
    private HashSet<int> OwnProcessIds()
    {
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe'");
        var ids = new HashSet<int>();
        foreach (var item in searcher.Get())
        {
            using (item)
            {
                if ((item["CommandLine"] as string)?.Contains(_profile, StringComparison.OrdinalIgnoreCase) == true)
                    ids.Add(Convert.ToInt32(item["ProcessId"]));
            }
        }
        return ids;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        // Edge can start helper processes while it's being closed, so repeat until none are left.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var ids = OwnProcessIds();
            if (ids.Count == 0) break;
            foreach (var id in ids)
            {
                try { using var p = Process.GetProcessById(id); p.Kill(entireProcessTree: true); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            Thread.Sleep(300);
        }
        for (var attempt = 0; attempt < 10 && Directory.Exists(_profile); attempt++)
        {
            try { Directory.Delete(_profile, recursive: true); }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}

[Trait("Category", "Desktop")]
public class WebBridgeIntegrationTests(EdgeWebFixture fixture) : IClassFixture<EdgeWebFixture>, IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly ElementInspector _inspector = new();
    private readonly EdgeWebFixture _fixture = fixture;

    public void Dispose() => _automation.Dispose();

    private async Task<WebResult> InspectAsync(Func<AutomationElement, AutomationElement?> find)
    {
        var window = _fixture.FindWindow(_automation) ?? throw new InvalidOperationException("Fixture window is gone.");
        var element = Retry.WhileNull(() => find(window), TimeSpan.FromSeconds(10)).Result
                      ?? throw new InvalidOperationException("Element not found in the fixture page.");
        var inspection = _inspector.InspectElement(_automation, element) ?? throw new InvalidOperationException("Inspection failed.");
        var path = inspection.Ancestors.Reverse().Append(inspection.Element).ToList();
        return await new WebBridge().InspectAsync(path, _fixture.Port);
    }

    private AutomationElement? ByName(AutomationElement scope, ControlType type, string name) =>
        scope.FindFirstDescendant(_automation.ConditionFactory.ByControlType(type).And(_automation.ConditionFactory.ByName(name)));

    private static void Save(string name, WebResult web)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "generated");
        Directory.CreateDirectory(dir);
        foreach (var snippet in WebCodeTemplates.Render(web)) File.WriteAllText(Path.Combine(dir, $"web_{name}.{snippet.Id}.txt"), snippet.Code);
    }

    [Fact]
    public async Task TestId_IsPreferred()
    {
        var web = await InspectAsync(w => ByName(w, ControlType.Button, "Checkout"));
        Save("checkout", web);

        Assert.True(web.Connected, web.Problem);
        var step = Assert.Single(web.Best!.Steps);
        Assert.Equal(WebLocatorKind.TestId, step.Kind);
        Assert.Equal("checkout", step.Value);
        Assert.Equal(VerificationStatus.Unique, web.Best.Verification.Status);
        Assert.Equal("[data-testid=\"checkout\"]", web.Selenium!.Target.Value);
    }

    [Fact]
    public async Task LinkWithStatusInName_UsesStablePartOfRoleName()
    {
        var web = await InspectAsync(w => ByName(w, ControlType.Hyperlink, "Dyno (direct message), Online"));
        Save("dyno", web);

        Assert.Equal("a", web.Element!.Tag); // climbed from the inner span/div to the link
        var step = web.Best!.Steps[^1];
        Assert.Equal(WebLocatorKind.Role, step.Kind);
        Assert.Equal("link", step.Role);
        Assert.Equal("Dyno (direct message)", step.Name);
        Assert.False(step.Exact);
        Assert.Equal(VerificationStatus.Unique, web.Best.Verification.Status);
    }

    [Fact]
    public async Task DuplicateCaption_IsAnchoredToItsSection()
    {
        var web = await InspectAsync(w => ByName(w, ControlType.Group, "Orders") is { } orders ? ByName(orders, ControlType.Button, "Delete") : null);
        Save("orders_delete", web);

        var best = web.Best!;
        Assert.Equal(2, best.Steps.Count);
        Assert.Equal("Orders", best.Steps[0].Name);
        Assert.Equal("Delete", best.Steps[1].Name);
        Assert.Equal(VerificationStatus.Unique, best.Verification.Status);
        // The hashed class must not be used for Selenium either.
        Assert.DoesNotContain("972a0", web.Selenium!.Target.Value);
    }

    [Fact]
    public async Task LabelledInput_UsesRoleAndLabel()
    {
        var web = await InspectAsync(w => ByName(w, ControlType.Edit, "Email"));
        Save("email", web);

        var step = Assert.Single(web.Best!.Steps);
        Assert.Equal("textbox", step.Role);
        Assert.Equal("Email", step.Name);
        Assert.Equal("#email-input", web.Selenium!.Target.Value);
    }

    [Fact]
    public async Task ButtonInIframe_EntersTheFrameFirst()
    {
        var web = await InspectAsync(w => ByName(w, ControlType.Button, "Pay now"));
        Save("pay", web);

        Assert.Equal(1, web.Element!.FrameDepth);
        var frame = Assert.Single(web.Best!.Frames);
        Assert.Equal("#payment-frame", frame.Value);
        Assert.Equal("Pay now", web.Best.Steps[^1].Name);
        Assert.Equal(VerificationStatus.Unique, web.Best.Verification.Status);
    }

    [Fact]
    public async Task ButtonInShadowDom_SeleniumStepsThroughTheHost()
    {
        var web = await InspectAsync(w => ByName(w, ControlType.Button, "Like"));
        Save("like", web);

        Assert.True(web.Element!.InShadowDom);
        Assert.Equal("Like", web.Best!.Steps[^1].Name);
        var host = Assert.Single(web.Selenium!.ShadowHosts);
        Assert.Equal("fancy-card", host.Value);
        Assert.Equal("button.like", web.Selenium.Target.Value);
    }

    // A row with no id, hashed classes and the same button as its siblings: found through the
    // order number in it, never by position.
    [Fact]
    public async Task RowWithoutId_IsFoundThroughTheTextInIt()
    {
        // Chromium may leave plain <div>s out of the UIA tree, so pick the Delete button on the label's line.
        var web = await InspectAsync(w =>
        {
            if (ByName(w, ControlType.Text, "Order 1042") is not { } label) return null;
            var y = label.BoundingRectangle.Y + label.BoundingRectangle.Height / 2;
            return w.FindAllDescendants(_automation.ConditionFactory.ByControlType(ControlType.Button).And(_automation.ConditionFactory.ByName("Delete")))
                .FirstOrDefault(b => Math.Abs(b.BoundingRectangle.Y + b.BoundingRectangle.Height / 2 - y) < 6);
        });
        Save("order_1042", web);

        var best = web.Best!;
        Assert.Equal(WebLocatorKind.Text, best.Steps[0].Kind);
        Assert.Equal("Order 1042", best.Steps[0].Value);
        Assert.Equal("ancestor::div[1]", best.Steps[1].Value);
        Assert.Equal("Delete", best.Steps[2].Name);
        Assert.All(best.Steps, s => Assert.Null(s.Index));
        Assert.Equal(VerificationStatus.Unique, best.Verification.Status);

        var selenium = web.Selenium!;
        Assert.Contains("normalize-space(.)='Order 1042'", selenium.ContainerXPath);
        Assert.Null(selenium.Target.Index);
        Assert.DoesNotContain("5b7e2", selenium.Target.Value + selenium.ContainerXPath); // no hashed classes
    }

    [Fact]
    public async Task WithoutDebugPort_ExplainsHowToEnableIt()
    {
        var window = _fixture.FindWindow(_automation)!;
        var element = ByName(window, ControlType.Button, "Checkout")!;
        var inspection = _inspector.InspectElement(_automation, element)!;
        var path = inspection.Ancestors.Reverse().Append(inspection.Element).ToList();

        var web = await new WebBridge().InspectAsync(path, portOverride: 1); // nothing listens on port 1

        Assert.False(web.Connected);
        Assert.Contains("port 1", web.Problem);
    }
}

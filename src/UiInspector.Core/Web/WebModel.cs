using UiInspector.Core.Selectors;

namespace UiInspector.Core.Web;

public enum WebLocatorKind
{
    /// <summary><c>getByTestId</c> for data-testid, otherwise a CSS attribute selector.</summary>
    TestId,

    /// <summary><c>getByRole(role, { name })</c>: ARIA role plus accessible name.</summary>
    Role,

    Css,

    /// <summary><c>getByText(value, { exact: true })</c>: the element showing exactly this text.</summary>
    Text,

    /// <summary>An XPath relative to the previous step, e.g. <c>ancestor::div[1]</c> (the nearest div around it).</summary>
    XPath,
}

/// <summary>One Playwright/Selenium locator step. <see cref="Index"/> (0-based) picks one of several matches.</summary>
public sealed record WebLocator
{
    public required WebLocatorKind Kind { get; init; }

    /// <summary>CSS selector (Css), test id value (TestId), or unused (Role).</summary>
    public string Value { get; init; } = "";

    public string TestIdAttribute { get; init; } = "data-testid";

    public string Role { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>Role names: exact, case-sensitive match; false = case-insensitive substring (Playwright's default).</summary>
    public bool Exact { get; init; } = true;

    public int? Index { get; init; }

    /// <summary>
    /// Keep only elements containing exactly this text (Playwright <c>filter({ has: getByText(…) })</c>),
    /// used to find a row by the text in it when nothing else tells rows apart.
    /// </summary>
    public string? FilterHasText { get; init; }

    /// <summary>Also keep only elements that contain the next step's element.</summary>
    public bool FilterHasNext { get; init; }

    /// <summary>
    /// Take the last match. With the filters above this is the innermost container (ancestors
    /// come before descendants in document order), not a position among look-alikes.
    /// </summary>
    public bool Last { get; init; }

    /// <summary>The same locator as plain CSS, for Selenium; null if it has no CSS form (role locators).</summary>
    public string? AsCss => Kind switch
    {
        WebLocatorKind.Css => Value,
        WebLocatorKind.TestId => $"[{TestIdAttribute}=\"{Value.Replace("\"", "\\\"")}\"]",
        _ => null,
    };

    public override string ToString()
    {
        var text = Kind switch
        {
            WebLocatorKind.Role => $"role={Role} name{(Exact ? "=" : "~")}\"{Name}\"",
            WebLocatorKind.TestId => $"{TestIdAttribute}=\"{Value}\"",
            WebLocatorKind.Text => $"text=\"{Value}\"",
            WebLocatorKind.XPath => $"xpath={Value}",
            _ => $"css={Value}",
        };
        if (FilterHasText is { } has) text += $" containing \"{has}\"";
        if (Last) text += " (innermost)";
        return Index is { } index ? $"{text} #{index + 1}" : text;
    }
}

/// <summary>A verified Playwright locator: frames to enter, then steps (anchor…target).</summary>
public sealed record WebSelector
{
    public IReadOnlyList<WebLocator> Frames { get; init; } = [];

    public required IReadOnlyList<WebLocator> Steps { get; init; }

    public required string Strategy { get; init; }

    public required int Score { get; init; }

    public required SelectorVerification Verification { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// A verified Selenium locator. Selenium's CSS doesn't pierce Shadow DOM, so shadow hosts are
/// listed separately and the code steps through each host's shadow root.
/// </summary>
public sealed record SeleniumSelector
{
    public IReadOnlyList<WebLocator> Frames { get; init; } = [];

    public IReadOnlyList<WebLocator> ShadowHosts { get; init; } = [];

    /// <summary>
    /// XPath (from the document) to a container told apart by text in it; <see cref="Target"/> is
    /// then searched inside that container. Null for plain CSS selectors.
    /// </summary>
    public string? ContainerXPath { get; init; }

    public required WebLocator Target { get; init; }

    public required SelectorVerification Verification { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>The web element behind a UIA element, as the page itself sees it.</summary>
public sealed record WebElementSummary
{
    public required string Tag { get; init; }

    public string Id { get; init; } = "";

    public string Role { get; init; } = "";

    public string AccessibleName { get; init; } = "";

    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();

    public int FrameDepth { get; init; }

    public bool InShadowDom { get; init; }

    public bool InsideCrossOriginFrame { get; init; }

    public string Describe() =>
        $"<{Tag}{(Id.Length > 0 ? $" id=\"{Id}\"" : "")}>" +
        (Role.Length > 0 ? $" role={Role}" : "") +
        (AccessibleName.Length > 0 ? $" name=\"{AccessibleName}\"" : "");
}

/// <summary>Where a web locator came from.</summary>
public enum WebSource
{
    /// <summary>Read from the page over the DevTools Protocol and verified in it (needs a debug port).</summary>
    DevTools,

    /// <summary>Built from Chromium's accessibility tree through UI Automation; no debug port needed.</summary>
    Accessibility,
}

public enum WebAppKind
{
    /// <summary>A website in Edge or Chrome: scripts open their own browser and go to the URL.</summary>
    Browser,

    /// <summary>An Electron/CEF app: scripts start it with a debug port, then attach.</summary>
    Electron,

    /// <summary>A WebView2 host: scripts start it with the WebView2 debug-port variable set, then attach.</summary>
    WebView2,
}

/// <summary>How generated scripts get to the app: its type and the exe to start.</summary>
public sealed record WebAppLaunch(WebAppKind Kind, string? ExePath, string ProcessName)
{
    /// <summary>
    /// For apps installed with the Squirrel updater (Discord, Slack…): the app's exe name, and
    /// <see cref="ExePath"/> is the stable Update.exe next to the versioned app-x.y.z folders.
    /// </summary>
    public string? SquirrelExe { get; init; }

    /// <summary>The exe file name to look for among running processes, e.g. "Discord.exe".</summary>
    public string ImageName => SquirrelExe ?? (ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ProcessName : ProcessName + ".exe");

    /// <summary>The arguments that start the app with a debug port.</summary>
    public IReadOnlyList<string> StartArguments(int port) => SquirrelExe is { } exe
        ? ["--processStart", exe, "--process-start-args", $"--remote-debugging-port={port}"]
        : Kind == WebAppKind.WebView2 ? [] : [$"--remote-debugging-port={port}"];
}

public enum BrowserFlavor
{
    /// <summary>Chrome, Electron, CEF: Selenium uses ChromeOptions/chromedriver.</summary>
    Chrome,

    /// <summary>Edge and WebView2: Selenium uses EdgeOptions/msedgedriver.</summary>
    Edge,

    /// <summary>
    /// Firefox: Playwright drives its own build of Firefox (installed once with "playwright install
    /// firefox"); Selenium uses geckodriver and the installed Firefox. No attaching: Firefox has no
    /// Chromium debug port.
    /// </summary>
    Firefox,
}

/// <summary>Result of the CDP web bridge for one element.</summary>
public sealed record WebResult
{
    /// <summary>True when the page was reached and the element mapped to a DOM node.</summary>
    public bool Connected { get; init; }

    /// <summary>Why the bridge couldn't help, phrased as something the user can do.</summary>
    public string? Problem { get; init; }

    public int Port { get; init; }

    public string PageUrl { get; init; } = "";

    public string PageTitle { get; init; } = "";

    /// <summary>What generated code matches the page URL against (origin, or URL without query).</summary>
    public string UrlPrefix { get; init; } = "";

    public BrowserFlavor Browser { get; init; }

    public WebElementSummary? Element { get; init; }

    public WebSelector? Best { get; init; }

    public IReadOnlyList<WebSelector> Alternatives { get; init; } = [];

    public SeleniumSelector? Selenium { get; init; }

    /// <summary>Things worth knowing about this element (frames, shadow DOM, uncertain mapping).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public WebSource Source { get; init; } = WebSource.DevTools;

    public WebAppLaunch? Launch { get; init; }

    /// <summary>Scripts attach to the running app over <see cref="Port"/>; otherwise they start it themselves.</summary>
    public bool Attach => Port > 0;

    public static WebResult Unavailable(string problem) => new() { Problem = problem };
}

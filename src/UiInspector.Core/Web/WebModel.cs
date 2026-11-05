using UiInspector.Core.Selectors;

namespace UiInspector.Core.Web;

public enum WebLocatorKind
{
    /// <summary><c>getByTestId</c> for data-testid, otherwise a CSS attribute selector.</summary>
    TestId,

    /// <summary><c>getByRole(role, { name })</c>: ARIA role plus accessible name.</summary>
    Role,

    Css,
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
            _ => $"css={Value}",
        };
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

public enum BrowserFlavor
{
    /// <summary>Chrome, Electron, CEF: Selenium uses ChromeOptions/chromedriver.</summary>
    Chrome,

    /// <summary>Edge and WebView2: Selenium uses EdgeOptions/msedgedriver.</summary>
    Edge,
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

    public static WebResult Unavailable(string problem) => new() { Problem = problem };
}

using System.Text.RegularExpressions;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Web;

/// <summary>
/// Playwright and Selenium locators for an element in a Chromium page, built without a debug
/// port. Chromium exposes its accessibility tree to UI Automation: the HTML id is the
/// AutomationId, classes are the ClassName, and the ARIA role and accessible name come through
/// too. So the native selector, already verified against that same tree, translates step by step:
/// AutomationId → <c>#id</c>, role + name → <c>getByRole</c>, label text → <c>getByText</c>,
/// "the container holding this text" → <c>filter({ has })</c>. What UI Automation doesn't show
/// (data-testid, which elements are plain divs) needs the debug port.
/// </summary>
public static partial class AccessibilityLocators
{
    // Roles Playwright's getByRole accepts that Chromium reports reliably. "group" is left out:
    // Chromium reports plain <div>s as groups too, but Playwright calls those "generic".
    private static readonly HashSet<string> PlaywrightRoles =
    [
        "button", "link", "textbox", "searchbox", "checkbox", "radio", "switch", "combobox", "listbox", "option",
        "list", "listitem", "menu", "menubar", "menuitem", "menuitemcheckbox", "menuitemradio", "tab", "tablist",
        "tabpanel", "tree", "treeitem", "table", "row", "cell", "columnheader", "rowheader", "grid", "gridcell",
        "heading", "img", "navigation", "region", "dialog", "alertdialog", "slider", "spinbutton", "article",
        "form", "banner", "main", "contentinfo", "complementary", "toolbar", "alert", "status", "progressbar",
    ];

    private static readonly Dictionary<string, string> RoleForControlType = new()
    {
        ["Button"] = "button", ["Hyperlink"] = "link", ["Edit"] = "textbox", ["CheckBox"] = "checkbox",
        ["RadioButton"] = "radio", ["ComboBox"] = "combobox", ["ListItem"] = "listitem", ["List"] = "list",
        ["MenuItem"] = "menuitem", ["TabItem"] = "tab", ["TreeItem"] = "treeitem", ["Image"] = "img",
        ["Table"] = "table", ["DataItem"] = "row", ["Slider"] = "slider", ["Spinner"] = "spinbutton",
    };

    // How each role looks in HTML, for Selenium's XPath (it has no getByRole).
    private static readonly Dictionary<string, string> HtmlForRole = new()
    {
        ["button"] = "self::button or (self::input and (@type='button' or @type='submit' or @type='reset'))",
        ["link"] = "self::a",
        ["textbox"] = "self::textarea or (self::input and (not(@type) or @type='text' or @type='email' or @type='tel' or @type='url' or @type='password')) or @contenteditable='true'",
        ["searchbox"] = "self::input and @type='search'",
        ["checkbox"] = "self::input and @type='checkbox'",
        ["radio"] = "self::input and @type='radio'",
        ["combobox"] = "self::select",
        ["listitem"] = "self::li",
        ["list"] = "self::ul or self::ol",
        ["option"] = "self::option",
        ["img"] = "self::img",
        ["heading"] = "self::h1 or self::h2 or self::h3 or self::h4 or self::h5 or self::h6",
        ["navigation"] = "self::nav",
        ["region"] = "self::section",
        ["row"] = "self::tr",
        ["cell"] = "self::td",
        ["table"] = "self::table",
        ["form"] = "self::form",
        ["dialog"] = "self::dialog",
        ["article"] = "self::article",
        ["main"] = "self::main",
        ["banner"] = "self::header",
        ["contentinfo"] = "self::footer",
        ["complementary"] = "self::aside",
    };

    private const string SourceNote =
        "Built from Chromium's accessibility tree through UI Automation, without a debug port, and checked there rather than in the page. " +
        "Test ids (data-testid) can't be seen this way; start the app with --remote-debugging-port to use them.";

    // Which browser a script opens: Edge for Edge and WebView2, Firefox for Firefox, Chrome otherwise (Chrome, Electron).
    private static BrowserFlavor Flavor(WebAppLaunch? launch) =>
        launch?.ProcessName.StartsWith("msedge", StringComparison.OrdinalIgnoreCase) == true || launch?.Kind == WebAppKind.WebView2 ? BrowserFlavor.Edge
        : launch?.ProcessName.Equals("firefox", StringComparison.OrdinalIgnoreCase) == true ? BrowserFlavor.Firefox
        : BrowserFlavor.Chrome;

    /// <param name="native">The verified native selector for the element.</param>
    /// <param name="path">Desktop first, the element last.</param>
    /// <param name="pageUrl">The page's URL (the document's UIA value), for finding the page in scripts.</param>
    public static WebResult FromSelector(SelectorResult native, IReadOnlyList<ElementInfo> path, string? pageUrl, WebAppLaunch? launch)
    {
        var pageIndex = IndexOfPage(path, 0);
        if (pageIndex < 0) return WebResult.Unavailable("This element isn't inside a web page (Chromium or Firefox).");
        if (pageIndex == path.Count - 1) return WebResult.Unavailable("That's the whole web page; pick an element inside it.");
        if (native.Best is not { } selector || selector.Steps.Count == 0)
        {
            return WebResult.Unavailable(native.Problem ?? "No verified selector to build the web locator from.");
        }

        // Iframes are nested page documents; the element right above each one is the <iframe>.
        var frames = new List<(WebLocator Locator, int DocumentIndex)>();
        for (var i = IndexOfPage(path, pageIndex + 1); i > 0; i = IndexOfPage(path, i + 1))
        {
            if (FrameLocator(path[i - 1]) is not { } frame) return WebResult.Unavailable("The element is in an iframe that can't be identified from outside.");
            frames.Add((frame, i));
        }
        var innermost = frames.Count > 0 ? frames[^1].DocumentIndex : pageIndex;

        // Match each selector step to the element it finds. Anchors outside the innermost page or
        // frame are dropped: the target is unique inside them, so it's unique inside the frame too.
        var steps = new List<(SelectorStep Step, ElementInfo Element)>();
        var from = 1;
        for (var s = 0; s < selector.Steps.Count; s++)
        {
            var index = s == selector.Steps.Count - 1 ? path.Count - 1 : FindElement(path, selector.Steps[s], from);
            if (index < 0) return WebResult.Unavailable("The selector's steps couldn't be matched to the page.");
            if (index > innermost) steps.Add((selector.Steps[s], path[index]));
            from = index + 1;
        }
        if (steps.Count == 0) return WebResult.Unavailable("No part of the selector is inside the page.");

        var playwright = new List<WebLocator>();
        foreach (var (step, element) in steps)
        {
            if (PlaywrightStep(step, element) is not { } locator)
            {
                return WebResult.Unavailable(
                    $"{CodeTemplates.Describe(element)} has no role, name or id a web locator could use. Start the app with --remote-debugging-port for full page access.");
            }
            playwright.Add(locator);
        }

        var target = path[^1];
        var role = Role(target);
        var warnings = selector.Warnings
            .Where(w => w is not SelectorGenerator.LanguageWarning)
            .Append("Accessible names follow the app's UI language.")
            .ToList();
        var verification = selector.Verification.Status == VerificationStatus.Unique
            ? new SelectorVerification(VerificationStatus.Unique, 1)
            : selector.Verification;

        return new WebResult
        {
            Connected = true,
            Source = WebSource.Accessibility,
            Launch = launch,
            PageUrl = pageUrl ?? "",
            PageTitle = path[pageIndex].Name,
            UrlPrefix = pageUrl is null ? "" : WebBridge.UrlPrefix(pageUrl),
            Browser = Flavor(launch),
            Element = new WebElementSummary
            {
                Tag = role ?? target.ControlType.ToLowerInvariant(),
                Id = target.AutomationId,
                Role = role ?? "",
                AccessibleName = target.Name,
                FrameDepth = frames.Count,
            },
            Best = new WebSelector
            {
                Frames = frames.Select(f => f.Locator).ToList(),
                Steps = playwright,
                Strategy = $"{selector.Strategy} (from the accessibility tree)",
                Score = selector.Score,
                Verification = verification,
                Warnings = warnings,
            },
            Selenium = SeleniumXPath(steps) is { } xpath
                ? new SeleniumSelector
                {
                    Frames = frames.Select(f => f.Locator).ToList(),
                    Target = new WebLocator { Kind = WebLocatorKind.XPath, Value = xpath },
                    Verification = verification,
                    Warnings = ["Selenium can't search inside Shadow DOM with XPath; if the element isn't found, start the app with a debug port."],
                }
                : null,
            Notes = frames.Count > 0
                ? [SourceNote, $"It's inside {(frames.Count == 1 ? "an iframe" : $"{frames.Count} nested iframes")}; the code enters {(frames.Count == 1 ? "it" : "them")} first."]
                : [SourceNote],
        };
    }

    /// <summary>
    /// For look-alikes that only a label before them tells apart, when Chromium has left the row
    /// containers out of the tree ("Order 1042", "Pending", Delete, "Order 1043", … side by side):
    /// the first element like the target that follows the label. Anchored to the label, so rows
    /// can be added or reordered. UIA worker thread.
    /// </summary>
    internal static WebResult WithPrecedingLabel(WebResult web, IReadOnlyList<ElementInfo> path, IElementTree tree)
    {
        if (web is not { Connected: true, Best: { } best } || best.Steps.All(s => s.Index is null)) return web;
        var target = path[^1];
        if (path.Count < 2 || Role(target) is not { } role || target.Name.Trim().Length == 0) return web;

        var page = path.Last(WebApps.IsWebPage);
        foreach (var sibling in tree.PreviousSiblings(target, MaxLabelDistance))
        {
            var type = sibling.ControlType;
            var name = sibling.Name.Trim();
            // Another element like the target in between: the label belongs to that one.
            if (type == target.ControlType && name == target.Name.Trim()) return web;
            if (type != "Text") continue;

            var verdict = AttributeStability.AnalyzeLandmarkText(name);
            if (!verdict.IsUsable || verdict.Match != MatchKind.Exact) continue;
            if (CountNamed(tree, page, name) != 1) continue; // the label must be unique in the page

            var text = new SelectorCondition(SelectorProperty.Name, verdict.Value);
            var targetStep = new SelectorStep([new(SelectorProperty.ControlType, target.ControlType), new(SelectorProperty.Name, target.Name.Trim())]);
            var following = $"following::{XPathStep(targetStep, target)}[1]";
            return web with
            {
                Best = best with
                {
                    Steps =
                    [
                        new WebLocator { Kind = WebLocatorKind.Text, Value = verdict.Value, Exact = true },
                        new WebLocator { Kind = WebLocatorKind.XPath, Value = following },
                    ],
                    Strategy = $"{role} \"{target.Name.Trim()}\" right after the text \"{verdict.Value}\" (from the accessibility tree)",
                    Warnings = [.. best.Warnings.Where(w => !w.Contains("position", StringComparison.Ordinal)), verdict.Warning!],
                },
                Selenium = web.Selenium is { } selenium
                    ? selenium with { Target = new WebLocator { Kind = WebLocatorKind.XPath, Value = $"//{TextStep(text)}/{following}" } }
                    : null,
            };
        }
        return web;
    }

    private const int MaxLabelDistance = 8;
    private const int MaxInnerTexts = 30;
    private const int MaxLandmarkScan = 300;

    private static int CountNamed(IElementTree tree, ElementInfo scope, string name) =>
        tree.FindAll(scope, new SelectorStep([new SelectorCondition(SelectorProperty.Name, name)]), FlaUI.Core.Definitions.TreeScope.Descendants).Count;

    /// <summary>
    /// For a clicked area with no name, role or id (the clickable &lt;div&gt;s chat apps are full
    /// of): a text inside it that's unique in the page. Clicking the text reaches the area too,
    /// since the click bubbles up to it. UIA worker thread.
    /// </summary>
    internal static WebResult WithInnerText(WebResult web, IReadOnlyList<ElementInfo> path, string? pageUrl, WebAppLaunch? launch, IElementTree tree)
    {
        var target = path[^1];
        var weak = !web.Connected || web.Best is null || web.Best.Steps.Any(s => s.Index is not null);
        if (!weak || Role(target) is not null || target.Name.Trim().Length > 0) return web;

        var pageIndex = IndexOfPage(path, 0);
        if (pageIndex < 0) return web;
        var frames = new List<WebLocator>();
        for (var i = IndexOfPage(path, pageIndex + 1); i > 0; i = IndexOfPage(path, i + 1))
        {
            if (FrameLocator(path[i - 1]) is not { } frame) return web;
            frames.Add(frame);
        }
        var page = path.Last(WebApps.IsWebPage);
        var texts = (tree.Descendants(target, MaxLandmarkScan) ?? []).Where(d => d.ControlType == "Text").Take(MaxInnerTexts);
        foreach (var text in texts)
        {
            var verdict = AttributeStability.AnalyzeLandmarkText(text.Name);
            if (!verdict.IsUsable || verdict.Match != MatchKind.Exact) continue;
            if (CountNamed(tree, page, verdict.Value) != 1) continue;

            var condition = new SelectorCondition(SelectorProperty.Name, verdict.Value);
            var unique = new SelectorVerification(VerificationStatus.Unique, 1);
            var source = web.Connected ? web : new WebResult
            {
                Connected = true,
                Source = WebSource.Accessibility,
                Launch = launch,
                PageUrl = pageUrl ?? "",
                PageTitle = path[pageIndex].Name,
                UrlPrefix = pageUrl is null ? "" : WebBridge.UrlPrefix(pageUrl),
                Browser = Flavor(launch),
                Notes = [SourceNote],
            };
            return source with
            {
                Element = new WebElementSummary { Tag = target.ControlType.ToLowerInvariant(), AccessibleName = verdict.Value, FrameDepth = frames.Count },
                Best = new WebSelector
                {
                    Frames = frames,
                    Steps = [new WebLocator { Kind = WebLocatorKind.Text, Value = verdict.Value, Exact = true }],
                    Strategy = $"The text \"{verdict.Value}\" inside the clicked area (from the accessibility tree)",
                    Score = 60,
                    Verification = unique,
                    Warnings =
                    [
                        "The clicked area has no name, role or id, so the script clicks the text inside it; the click reaches the area as well.",
                        verdict.Warning!,
                    ],
                },
                Selenium = new SeleniumSelector { Frames = frames, Target = new WebLocator { Kind = WebLocatorKind.XPath, Value = "//" + TextStep(condition) }, Verification = unique },
            };
        }
        return web;
    }

    // ------------------------------------------------------------------ Playwright

    private static WebLocator? PlaywrightStep(SelectorStep step, ElementInfo element)
    {
        if (step.Has is { } has)
        {
            var text = has.Get(SelectorProperty.Name);
            if (text is null) return null;
            var css = step.Get(SelectorProperty.ClassName) is { } cls && CssIdentifier().IsMatch(cls.Value) ? "." + cls.Value : "*";
            return new WebLocator
            {
                Kind = WebLocatorKind.Css,
                Value = css,
                FilterHasText = text.Value,
                FilterHasNext = true,
                Last = true,
                Exact = text.Match == MatchKind.Exact,
            };
        }

        WebLocator? locator = null;
        if (step.Get(SelectorProperty.AutomationId) is { } id)
        {
            locator = new WebLocator { Kind = WebLocatorKind.Css, Value = CssIdentifier().IsMatch(id.Value) ? "#" + id.Value : $"[id=\"{id.Value.Replace("\"", "\\\"")}\"]" };
        }
        else if (step.Get(SelectorProperty.Name) is { } name)
        {
            if (Role(element) is { } role) locator = new WebLocator { Kind = WebLocatorKind.Role, Role = role, Name = name.Value, Exact = name.Match == MatchKind.Exact };
            else if (element.ControlType == "Text") locator = new WebLocator { Kind = WebLocatorKind.Text, Value = name.Value, Exact = name.Match == MatchKind.Exact };
        }
        else if (step.Get(SelectorProperty.ClassName) is { } cls && CssIdentifier().IsMatch(cls.Value))
        {
            locator = new WebLocator { Kind = WebLocatorKind.Css, Value = "." + cls.Value };
        }
        else if (Role(element) is { } role)
        {
            locator = new WebLocator { Kind = WebLocatorKind.Role, Role = role };
        }
        return locator is null ? null : locator with { Index = step.Index };
    }

    private static WebLocator? FrameLocator(ElementInfo frame)
    {
        if (AttributeStability.AnalyzeAutomationId(frame.AutomationId).IsUsable && frame.AutomationId != "RootWebArea")
        {
            return new WebLocator { Kind = WebLocatorKind.Css, Value = CssIdentifier().IsMatch(frame.AutomationId) ? "#" + frame.AutomationId : $"iframe[id=\"{frame.AutomationId}\"]" };
        }
        // An iframe's accessible name is its title attribute.
        return frame.Name.Length > 0 ? new WebLocator { Kind = WebLocatorKind.Css, Value = $"iframe[title=\"{frame.Name.Replace("\"", "\\\"")}\"]" } : null;
    }

    // ------------------------------------------------------------------ Selenium (one XPath)

    private static string? SeleniumXPath(List<(SelectorStep Step, ElementInfo Element)> steps)
    {
        var xpath = "";
        for (var i = 0; i < steps.Count; i++)
        {
            var (step, element) = steps[i];
            if (step.Has is { } has)
            {
                // The innermost element containing both the text and the next step's element: the row.
                if (i + 1 >= steps.Count || has.Get(SelectorProperty.Name) is not { } text) return null;
                var next = XPathStep(steps[i + 1].Step with { Index = null }, steps[i + 1].Element);
                if (next is null) return null;
                var container = step.Get(SelectorProperty.ClassName) is { } cls ? $"*[{ClassPredicate(cls.Value)}]" : "*";
                xpath = $"({xpath}//{container}[.//{TextStep(text)}][.//{next}])[last()]";
                continue;
            }

            var fragment = XPathStep(step with { Index = null }, element);
            if (fragment is null) return null;
            xpath = step.Index is { } index ? $"({xpath}//{fragment})[{index + 1}]" : $"{xpath}//{fragment}";
        }
        return xpath;
    }

    private static string? XPathStep(SelectorStep step, ElementInfo element)
    {
        if (step.Get(SelectorProperty.AutomationId) is { } id) return $"*[@id={Literal(id.Value)}]";
        if (step.Get(SelectorProperty.Name) is { } name)
        {
            if (element.ControlType == "Text") return TextStep(name);
            if (Role(element) is not { } role) return null;
            return $"*[{RolePredicate(role)}][{NamePredicate(name, role)}]";
        }
        if (step.Get(SelectorProperty.ClassName) is { } cls) return $"*[{ClassPredicate(cls.Value)}]";
        return Role(element) is { } r ? $"*[{RolePredicate(r)}]" : null;
    }

    private static string RolePredicate(string role) =>
        HtmlForRole.TryGetValue(role, out var html) ? $"{html} or @role='{role}'" : $"@role='{role}'";

    // Where an accessible name usually comes from: the text, aria-label, title, value, placeholder,
    // alt, or (for fields) a <label for>.
    private static string NamePredicate(SelectorCondition name, string role)
    {
        var literal = Literal(name.Value);
        string Matches(string expression) => name.Match == MatchKind.Exact ? $"{expression}={literal}" : $"contains({expression}, {literal})";
        var sources = new List<string>
        {
            Matches("normalize-space(.)"), Matches("@aria-label"), Matches("@title"), Matches("@value"), Matches("@placeholder"), Matches("@alt"),
        };
        if (role is "textbox" or "searchbox" or "combobox" or "checkbox" or "radio")
        {
            sources.Add($"@id=//label[{Matches("normalize-space(.)")}]/@for");
        }
        return string.Join(" or ", sources);
    }

    private static string TextStep(SelectorCondition text)
    {
        var literal = Literal(text.Value);
        return text.Match == MatchKind.Exact
            ? $"*[normalize-space(.)={literal} and not(*[normalize-space(.)={literal}])]"
            : $"*[contains(normalize-space(.), {literal}) and not(*[contains(normalize-space(.), {literal})])]";
    }

    private static string ClassPredicate(string cls) => $"contains(concat(' ', normalize-space(@class), ' '), {Literal(" " + cls + " ")})";

    private static string Literal(string value)
    {
        if (!value.Contains('\'')) return $"'{value}'";
        if (!value.Contains('"')) return $"\"{value}\"";
        return "concat('" + value.Replace("'", "', \"'\", '") + "')";
    }

    // ------------------------------------------------------------------ shared

    /// <summary>The ARIA role Playwright knows the element by, or null if there's no usable one.</summary>
    public static string? Role(ElementInfo element)
    {
        var aria = element.AriaRole.Trim().ToLowerInvariant();
        if (PlaywrightRoles.Contains(aria)) return aria;
        return RoleForControlType.GetValueOrDefault(element.ControlType);
    }

    private static int IndexOfPage(IReadOnlyList<ElementInfo> path, int start)
    {
        for (var i = start; i < path.Count; i++)
        {
            if (WebApps.IsWebPage(path[i])) return i;
        }
        return -1;
    }

    private static int FindElement(IReadOnlyList<ElementInfo> path, SelectorStep step, int from)
    {
        for (var i = from; i < path.Count; i++)
        {
            var e = path[i];
            if (step.ControlType is { } type && e.ControlType != type) continue;
            if (step.Conditions.All(c => Matches(e, c))) return i;
        }
        return -1;
    }

    private static bool Matches(ElementInfo e, SelectorCondition c)
    {
        var value = c.Property switch
        {
            SelectorProperty.ControlType => e.ControlType,
            SelectorProperty.AutomationId => e.AutomationId,
            SelectorProperty.Name => e.Name,
            SelectorProperty.ClassName => e.ClassName,
            _ => "",
        };
        return c.Match == MatchKind.Exact ? value.Trim() == c.Value : value.Contains(c.Value, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex CssIdentifier();
}

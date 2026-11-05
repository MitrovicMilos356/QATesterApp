using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Web;

/// <summary>
/// Connects a UIA element inside a Chromium page (Chrome, Edge, Electron, WebView2) to its DOM
/// node through the Chrome DevTools Protocol, then builds Playwright and Selenium locators and
/// verifies each one in the live page. Needs the app to run with --remote-debugging-port.
/// </summary>
public sealed partial class WebBridge
{
    private const int MaxChainForMatch = 8;
    private const int MaxAnchors = 6;
    private const int ScopePenalty = 5;
    private const int IndexPenalty = 45;
    private const double ViewportTolerance = 24;
    private const int MaxRoleCandidates = 400;
    private const int MaxLandmarks = 8;
    private const int ContentPenalty = 15;

    private static readonly string[] TestIdAttributes = ["data-testid", "data-test-id", "data-test", "data-qa", "data-cy"];

    // Chromium-internal or meaningless roles that Playwright's getByRole can't (or shouldn't) target.
    private static readonly HashSet<string> NonRoles =
    [
        "generic", "none", "presentation", "StaticText", "RootWebArea", "InlineTextBox", "LineBreak", "LabelText",
        "paragraph", "Iframe", "IframePresentational", "Unknown", "Section",
    ];

    // Roles worth anchoring on: they name a region of the page.
    private static readonly HashSet<string> AnchorRoles =
    [
        "list", "listbox", "navigation", "region", "form", "dialog", "alertdialog", "group", "listitem", "menu", "menubar",
        "tablist", "toolbar", "table", "grid", "row", "main", "complementary", "banner", "contentinfo", "tree", "article", "search",
    ];

    private static readonly Dictionary<string, string[]> RolesForControlType = new()
    {
        ["Button"] = ["button"],
        ["Hyperlink"] = ["link"],
        ["ListItem"] = ["listitem", "option", "treeitem"],
        ["Edit"] = ["textbox", "searchbox", "combobox"],
        ["CheckBox"] = ["checkbox", "switch"],
        ["RadioButton"] = ["radio"],
        ["ComboBox"] = ["combobox", "listbox"],
        ["MenuItem"] = ["menuitem", "menuitemcheckbox", "menuitemradio"],
        ["TabItem"] = ["tab"],
        ["TreeItem"] = ["treeitem"],
        ["Image"] = ["img", "image"],
        ["List"] = ["list", "listbox"],
    };

    /// <param name="pathFromRoot">Desktop first, element last (the UIA ancestor chain).</param>
    /// <param name="portOverride">Debugging port to use instead of detecting it from the process.</param>
    public async Task<WebResult> InspectAsync(IReadOnlyList<ElementInfo> pathFromRoot, int? portOverride, CancellationToken ct = default)
    {
        var target = pathFromRoot[^1];
        var pageRootIndex = pathFromRoot.ToList().FindIndex(IsPageRoot);
        if (pageRootIndex < 0) return WebResult.Unavailable("This element isn't inside a Chromium web page.");
        if (pageRootIndex == pathFromRoot.Count - 1) return WebResult.Unavailable("That's the whole web page; pick an element inside it.");
        var pageRoot = pathFromRoot[pageRootIndex];
        var app = string.IsNullOrEmpty(target.ProcessName) ? "The app" : target.ProcessName;

        var port = portOverride ?? DebugPort.TryFind(target.ProcessId);
        if (port is null)
        {
            return WebResult.Unavailable(
                $"{app} wasn't started with a remote-debugging port, so its page structure (ids, test ids, iframes) can't be read. " +
                $"Quit {app} completely, start it with --remote-debugging-port=9222, and pick the element again to get Playwright and Selenium code.");
        }

        IReadOnlyList<CdpTarget> targets;
        try
        {
            targets = await CdpDiscovery.ListTargetsAsync(port.Value, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return WebResult.Unavailable($"Nothing answers on debugging port {port}. Is {app} still running with --remote-debugging-port={port}?");
        }

        var pages = targets.Where(t => t.Type is "page" or "webview" or "app").ToList();
        var byTitle = pages.Where(t => t.Title == pageRoot.Name).ToList();
        var page = byTitle.FirstOrDefault() ?? (pages.Count == 1 ? pages[0] : null);
        if (page is null)
        {
            return WebResult.Unavailable(
                $"Couldn't tell which page this is: {pages.Count} pages are open on port {port}, none titled \"{pageRoot.Name}\".");
        }

        await using var session = await CdpSession.ConnectAsync(page.WebSocketDebuggerUrl, ct);
        var context = new PageContext(session, page, port.Value, target);
        return await InspectPageAsync(context, pageRoot, target, ct);
    }

    private sealed record PageContext(CdpSession Session, CdpTarget Page, int Port, ElementInfo UiaTarget);

    private async Task<WebResult> InspectPageAsync(PageContext context, ElementInfo pageRoot, ElementInfo target, CancellationToken ct)
    {
        var session = context.Session;
        var notes = new List<string>();

        // Screen pixels → CSS pixels: the page root's UIA rectangle is the viewport's origin on
        // screen, and devicePixelRatio already includes both monitor scaling and page zoom.
        var viewport = await session.EvaluateValueAsync("({ dpr: devicePixelRatio, w: innerWidth, h: innerHeight })", ct);
        var dpr = viewport.GetProperty("dpr").GetDouble();
        var root = pageRoot.BoundingRectangle;
        if (Math.Abs(root.Width / dpr - viewport.GetProperty("w").GetDouble()) > ViewportTolerance)
        {
            notes.Add("The page's size doesn't match its window (unusual zoom or scaling?), so the mapping to the DOM may be off.");
        }
        var rect = target.BoundingRectangle;
        var cssX = (rect.X + rect.Width / 2.0 - root.X) / dpr;
        var cssY = (rect.Y + rect.Height / 2.0 - root.Y) / dpr;

        var located = await session.EvaluateValueAsync(
            string.Create(CultureInfo.InvariantCulture, $"{PageScript.Locate}({cssX:F1}, {cssY:F1})"), ct);
        if (!located.GetProperty("found").GetBoolean())
        {
            return WebResult.Unavailable("The element's position isn't inside the visible page (scrolled out of view?).");
        }

        var chain = located.GetProperty("chain").EnumerateArray().Select(DomNode.Parse).ToList();
        var frames = located.GetProperty("frames").EnumerateArray().Select(DomNode.Parse).ToList();
        var hosts = located.GetProperty("hosts").EnumerateArray().Select(DomNode.Parse).ToList();
        var crossOrigin = located.GetProperty("crossOrigin").GetBoolean();

        await session.SendAsync("DOM.enable", ct: ct);
        await session.SendAsync("Accessibility.enable", ct: ct);
        var ax = new AxCache(session);

        var targetIndex = crossOrigin ? 0 : await ChooseTargetAsync(chain, ax, target, ct);
        await session.EvaluateValueAsync($"{PageScript.State}.target = {PageScript.State}.chain[{targetIndex}], true", ct);
        var node = chain[targetIndex];
        var nodeAx = await ax.GetAsync(targetIndex, ct);

        var summary = new WebElementSummary
        {
            Tag = node.Tag,
            Id = node.Id,
            Role = nodeAx.Role,
            AccessibleName = nodeAx.Name,
            Attributes = node.Attributes,
            FrameDepth = frames.Count,
            InShadowDom = node.InShadow,
            InsideCrossOriginFrame = crossOrigin,
        };

        if (!string.IsNullOrEmpty(target.Name) && nodeAx.Name.Length > 0
            && !nodeAx.Name.Contains(target.Name, StringComparison.OrdinalIgnoreCase)
            && !target.Name.Contains(nodeAx.Name, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"The page element's name (\"{nodeAx.Name}\") differs from the UIA name (\"{target.Name}\"); check that the right element was mapped.");
        }
        if (frames.Count > 0) notes.Add($"It's inside {(frames.Count == 1 ? "an iframe" : $"{frames.Count} nested iframes")}; the code enters {(frames.Count == 1 ? "it" : "them")} first.");
        if (node.InShadow) notes.Add("It's inside Shadow DOM. Playwright locators reach into open shadow roots by themselves; Selenium code steps through each shadow host.");

        var frameLocators = await FrameLocatorsAsync(session, frames, ct);
        var result = new WebResult
        {
            Connected = true,
            Port = context.Port,
            PageUrl = context.Page.Url,
            PageTitle = context.Page.Title,
            UrlPrefix = UrlPrefix(context.Page.Url),
            Browser = context.UiaTarget.ProcessName.StartsWith("msedge", StringComparison.OrdinalIgnoreCase) ? BrowserFlavor.Edge : BrowserFlavor.Chrome,
            Element = summary,
        };

        if (crossOrigin)
        {
            notes.Add("It's inside a cross-origin iframe, whose content runs in a separate process the inspector can't read yet. " +
                      "The frame can be entered with the frame locator; for the element itself, use the native (UIA) selector.");
            return result with { Notes = notes };
        }

        var playwright = await PlaywrightSelectorsAsync(session, chain, ax, targetIndex, frameLocators, ct);
        var selenium = await SeleniumSelectorAsync(session, chain, hosts, targetIndex, frameLocators, ct);
        return result with
        {
            Best = playwright.FirstOrDefault(),
            Alternatives = playwright.Skip(1).ToList(),
            Selenium = selenium,
            Notes = notes,
        };
    }

    // ------------------------------------------------------------------ choosing the DOM element

    /// <summary>
    /// elementFromPoint returns the deepest element (often a span inside a link). Walk up to the
    /// element that corresponds to the UIA target: same accessible name first, then a role that
    /// fits the UIA control type, then the nearest natively interactive element.
    /// </summary>
    private static async Task<int> ChooseTargetAsync(List<DomNode> chain, AxCache ax, ElementInfo uia, CancellationToken ct)
    {
        var limit = Math.Min(chain.Count, MaxChainForMatch);
        int? byRole = null;
        for (var i = 0; i < limit; i++)
        {
            var info = await ax.GetAsync(i, ct);
            if (info.Ignored) continue;
            if (uia.Name.Length > 0 && string.Equals(info.Name, uia.Name.Trim(), StringComparison.Ordinal)) return i;
            if (byRole is null && RolesForControlType.TryGetValue(uia.ControlType, out var roles) && roles.Contains(info.Role)) byRole = i;
        }
        if (byRole is { } r) return r;

        for (var i = 0; i < limit; i++)
        {
            if (chain[i].IsNativelyInteractive) return i;
        }
        return 0;
    }

    // ------------------------------------------------------------------ Playwright

    private sealed record Candidate(WebLocator Locator, int Score, string Strategy, IReadOnlyList<string?> Warnings);

    private async Task<List<WebSelector>> PlaywrightSelectorsAsync(
        CdpSession session, List<DomNode> chain, AxCache ax, int targetIndex, IReadOnlyList<WebLocator> frames, CancellationToken ct)
    {
        var node = chain[targetIndex];
        var nodeAx = await ax.GetAsync(targetIndex, ct);
        var candidates = TargetCandidates(node, nodeAx);
        var selectors = new List<WebSelector>();

        WebSelector Make(IReadOnlyList<WebLocator> steps, string strategy, int score, IEnumerable<string?> warnings, SelectorVerification verification) => new()
        {
            Frames = frames,
            Steps = steps,
            Strategy = strategy,
            Score = score,
            Verification = verification,
            Warnings = warnings.OfType<string>().Distinct().ToList(),
        };

        // 1. Unique in the page (or the innermost frame's document).
        foreach (var c in candidates)
        {
            var match = await CountAsync(session, ax, c.Locator, "u.doc", targetIndex, ct);
            if (match.IsUniqueTarget) selectors.Add(Make([c.Locator], c.Strategy, c.Score, c.Warnings, match.Verification));
        }
        if (selectors.Any(s => s.Score >= 80)) return Ranked(selectors);

        // 2. Unique inside the nearest ancestor that is itself unique.
        (Candidate Anchor, int Index)? usedAnchor = null;
        var anchorLimit = Math.Min(chain.Count, targetIndex + 1 + MaxAnchors);
        for (var i = targetIndex + 1; i < anchorLimit; i++)
        {
            Candidate? anchor = null;
            foreach (var a in await AnchorCandidatesAsync(chain[i], ax, i, ct))
            {
                var match = await CountAsync(session, ax, a.Locator, "u.doc", i, ct);
                if (match.IsUniqueTarget) { anchor = a; break; }
            }
            if (anchor is null) continue;

            usedAnchor ??= (anchor, i);
            var found = false;
            foreach (var c in candidates)
            {
                var match = await CountAsync(session, ax, c.Locator, $"u.chain[{i}]", targetIndex, ct);
                if (!match.IsUniqueTarget) continue;
                found = true;
                selectors.Add(Make([anchor.Locator, c.Locator], $"{c.Strategy} inside {anchor.Strategy}",
                    Math.Min(anchor.Score, c.Score) - ScopePenalty, anchor.Warnings.Concat(c.Warnings), match.Verification));
            }
            if (found) return Ranked(selectors);
        }
        if (selectors.Count > 0) return Ranked(selectors);

        // 3. Inside the nearest container told apart by text in it: the row around "Order 1042".
        //    Survives rows being added, removed or reordered, unlike a position.
        if (await TextAnchorAsync(session, chain, targetIndex, ct) is { } around)
        {
            foreach (var c in candidates)
            {
                var match = await CountAsync(session, ax, c.Locator, $"u.chain[{around.ChainIndex}]", targetIndex, ct);
                if (!match.IsUniqueTarget) continue;
                selectors.Add(Make([around.TextLocator, around.Container, c.Locator], $"{c.Strategy} inside the <{around.Tag}> around \"{around.Text}\"",
                    Math.Min(c.Score, around.Score) - ContentPenalty, c.Warnings.Append(around.Warning), match.Verification));
            }
            if (selectors.Count > 0) return Ranked(selectors);
        }

        // 4. Last resort: position among matches.
        var scope = usedAnchor is { } ua ? $"u.chain[{ua.Index}]" : "u.doc";
        foreach (var c in candidates)
        {
            var match = await CountAsync(session, ax, c.Locator, scope, targetIndex, ct);
            if (match.Index < 0) continue;
            var steps = new List<WebLocator>();
            if (usedAnchor is { } a) steps.Add(a.Anchor.Locator);
            steps.Add(c.Locator with { Index = match.Index });
            var warnings = c.Warnings.Append($"Uses position {match.Index + 1} of {match.Count} matches; it breaks if items are added, removed or reordered.");
            selectors.Add(Make(steps, $"{c.Strategy} #{match.Index + 1}{(usedAnchor is { } s ? $" inside {s.Anchor.Strategy}" : "")}",
                c.Score - IndexPenalty, warnings, new SelectorVerification(VerificationStatus.Unique, 1)));
            break;
        }
        return Ranked(selectors);
    }

    private static List<WebSelector> Ranked(List<WebSelector> selectors) =>
        selectors.OrderByDescending(s => s.Score).DistinctBy(s => s.Strategy).Take(5).ToList();

    private static List<Candidate> TargetCandidates(DomNode node, AxInfo ax)
    {
        var list = new List<Candidate>();

        foreach (var attribute in TestIdAttributes)
        {
            if (!node.Attributes.TryGetValue(attribute, out var value)) continue;
            var verdict = AttributeStability.AnalyzeAutomationId(value);
            if (verdict.IsUsable)
            {
                list.Add(new(new WebLocator { Kind = WebLocatorKind.TestId, Value = value, TestIdAttribute = attribute }, 100, $"Test id ({attribute})", [verdict.Warning]));
            }
        }

        var role = IsPlaywrightRole(ax.Role) ? ax.Role : null;
        if (role is not null && ax.Name.Length > 0)
        {
            var name = AttributeStability.AnalyzeName(ax.Name);
            if (name.IsUsable)
            {
                var exact = name.Match == MatchKind.Exact;
                list.Add(new(new WebLocator { Kind = WebLocatorKind.Role, Role = role, Name = name.Value, Exact = exact },
                    name.Stability == Stability.Stable ? 90 : 75, exact ? "Role + name" : "Role + name (stable part)",
                    [name.Warning, "Accessible names follow the app's UI language."]));
            }
        }

        if (node.Id.Length > 0)
        {
            var id = AttributeStability.AnalyzeAutomationId(node.Id);
            if (id.IsUsable)
            {
                list.Add(new(new WebLocator { Kind = WebLocatorKind.Css, Value = IdSelector(node.Tag, node.Id) },
                    id.Stability == Stability.Stable ? 85 : 65, "id", [id.Warning]));
            }
        }

        var classes = node.Classes
            .Where(c => SimpleIdentifier().IsMatch(c) && AttributeStability.AnalyzeClassName(c, "").IsUsable)
            .Take(2)
            .ToList();
        if (classes.Count > 0)
        {
            list.Add(new(new WebLocator { Kind = WebLocatorKind.Css, Value = node.Tag + string.Concat(classes.Select(c => "." + c)) },
                50, "CSS class", ["Class names describe styling and may change in a redesign."]));
        }

        if (role is not null) list.Add(new(new WebLocator { Kind = WebLocatorKind.Role, Role = role, Exact = true }, 35, "Role", []));
        list.Add(new(new WebLocator { Kind = WebLocatorKind.Css, Value = node.Tag }, 20, "Tag", []));
        return list;
    }

    private static async Task<List<Candidate>> AnchorCandidatesAsync(DomNode node, AxCache ax, int index, CancellationToken ct)
    {
        var list = new List<Candidate>();
        foreach (var attribute in TestIdAttributes)
        {
            if (node.Attributes.TryGetValue(attribute, out var value) && AttributeStability.AnalyzeAutomationId(value).IsUsable)
            {
                list.Add(new(new WebLocator { Kind = WebLocatorKind.TestId, Value = value, TestIdAttribute = attribute }, 100, $"[{attribute}=\"{value}\"]", []));
            }
        }
        if (node.Id.Length > 0 && AttributeStability.AnalyzeAutomationId(node.Id).IsUsable)
        {
            list.Add(new(new WebLocator { Kind = WebLocatorKind.Css, Value = IdSelector(node.Tag, node.Id) }, 85, $"#{node.Id}", []));
        }

        var info = await ax.GetAsync(index, ct);
        if (!info.Ignored && AnchorRoles.Contains(info.Role) && info.Name.Length > 0 && AttributeStability.AnalyzeName(info.Name) is { IsUsable: true } name)
        {
            list.Add(new(new WebLocator { Kind = WebLocatorKind.Role, Role = info.Role, Name = name.Value, Exact = name.Match == MatchKind.Exact },
                80, $"{info.Role} \"{name.Value}\"", [name.Warning]));
        }
        return list;
    }

    // ------------------------------------------------------------------ Selenium (CSS only)

    private static async Task<SeleniumSelector?> SeleniumSelectorAsync(
        CdpSession session, List<DomNode> chain, List<DomNode> hosts, int targetIndex, IReadOnlyList<WebLocator> frames, CancellationToken ct)
    {
        var node = chain[targetIndex];
        var cssCandidates = TargetCandidates(node, AxInfo.Empty)
            .Where(c => c.Locator.AsCss is not null)
            .Select(c => (Css: c.Locator.AsCss!, c.Warnings))
            .ToList();

        // Prefix candidates with ancestors that have an id or test id (same shadow root only:
        // a descendant combinator can't cross a shadow boundary, which the count reflects).
        var anchored = chain.Skip(targetIndex + 1).Take(MaxAnchors)
            .Select(a => a.Id.Length > 0 && AttributeStability.AnalyzeAutomationId(a.Id).IsUsable ? IdSelector(a.Tag, a.Id)
                : TestIdAttributes.Where(a.Attributes.ContainsKey).Select(t => $"[{t}=\"{a.Attributes[t]}\"]").FirstOrDefault())
            .OfType<string>()
            .SelectMany(anchor => cssCandidates.Select(c => (Css: $"{anchor} {c.Css}", c.Warnings)))
            .ToList();

        var shadowHosts = new List<WebLocator>();
        for (var i = 0; i < hosts.Count; i++)
        {
            var host = hosts[i];
            var options = new List<string>();
            if (host.Id.Length > 0) options.Add(IdSelector(host.Tag, host.Id));
            options.Add(host.Tag);
            WebLocator? chosen = null;
            foreach (var css in options)
            {
                var m = Match.Parse(await session.EvaluateValueAsync(PageScript.CountHostCss(i, Js(css)), ct));
                if (m.Count == 1) { chosen = new WebLocator { Kind = WebLocatorKind.Css, Value = css }; break; }
                if (css == host.Tag && m.Index >= 0) chosen = new WebLocator { Kind = WebLocatorKind.Css, Value = css, Index = m.Index };
            }
            if (chosen is null) return null;
            shadowHosts.Add(chosen);
        }

        foreach (var (css, warnings) in cssCandidates.Concat(anchored))
        {
            var m = Match.Parse(await session.EvaluateValueAsync(PageScript.CountCss("u.target.getRootNode()", Js(css), deep: false), ct));
            if (m.IsUniqueTarget)
            {
                return new SeleniumSelector
                {
                    Frames = frames,
                    ShadowHosts = shadowHosts,
                    Target = new WebLocator { Kind = WebLocatorKind.Css, Value = css },
                    Verification = m.Verification,
                    Warnings = warnings.OfType<string>().ToList(),
                };
            }
        }

        // Inside the container around a text, found by XPath (Selenium's CSS can't match text).
        if (await TextAnchorAsync(session, chain, targetIndex, ct) is { } around)
        {
            foreach (var (css, warnings) in cssCandidates)
            {
                var m = Match.Parse(await session.EvaluateValueAsync(PageScript.CountCss($"u.chain[{around.ChainIndex}]", Js(css), deep: false), ct));
                if (!m.IsUniqueTarget) continue;
                return new SeleniumSelector
                {
                    Frames = frames,
                    ContainerXPath = around.SeleniumXPath,
                    Target = new WebLocator { Kind = WebLocatorKind.Css, Value = css },
                    Verification = m.Verification,
                    Warnings = warnings.Append(around.Warning).OfType<string>().ToList(),
                };
            }
        }

        // Positional fallback on the plain tag.
        var tagMatch =Match.Parse(await session.EvaluateValueAsync(PageScript.CountCss("u.target.getRootNode()", Js(node.Tag), deep: false), ct));
        if (tagMatch.Index < 0) return null;
        return new SeleniumSelector
        {
            Frames = frames,
            ShadowHosts = shadowHosts,
            Target = new WebLocator { Kind = WebLocatorKind.Css, Value = node.Tag, Index = tagMatch.Index },
            Verification = new SelectorVerification(VerificationStatus.Unique, 1),
            Warnings = [$"Uses position {tagMatch.Index + 1} of {tagMatch.Count} <{node.Tag}> elements; it breaks if the page changes."],
        };
    }

    // ------------------------------------------------------------------ containers told apart by text

    /// <summary>
    /// A container found through a text inside it: the element showing exactly <see cref="Text"/>
    /// (unique in the page), then its <see cref="Level"/>-th <see cref="Tag"/> ancestor.
    /// </summary>
    private sealed record TextAnchor(int ChainIndex, string Tag, string Text, int Level, int Score, string? Warning)
    {
        public WebLocator TextLocator => new() { Kind = WebLocatorKind.Text, Value = Text };

        public WebLocator Container => new() { Kind = WebLocatorKind.XPath, Value = $"ancestor::{Tag}[{Level}]" };

        /// <summary>The same in one XPath; the text part matches what <see cref="PageScript.CheckLandmark"/> verified.</summary>
        public string SeleniumXPath
        {
            get
            {
                var text = XPathLiteral(Text);
                return $"//*[normalize-space(.)={text} and not(*[normalize-space(.)={text}])]/ancestor::{Tag}[{Level}]";
            }
        }
    }

    /// <summary>
    /// The nearest ancestor of the target that holds a text found nowhere else in the page, and
    /// that text. Not for Shadow DOM: XPath and Selenium can't search across shadow roots.
    /// </summary>
    private static async Task<TextAnchor?> TextAnchorAsync(CdpSession session, List<DomNode> chain, int targetIndex, CancellationToken ct)
    {
        var target = chain[targetIndex];
        if (target.InShadow) return null;

        var limit = Math.Min(chain.Count, targetIndex + 1 + MaxAnchors);
        for (var i = targetIndex + 1; i < limit; i++)
        {
            var box = chain[i];
            if (box.InShadow || box.Tag is "body" or "html" || !SimpleIdentifier().IsMatch(box.Tag)) break;

            var texts = (await session.EvaluateValueAsync(PageScript.LandmarkTexts(i, targetIndex), ct))
                .EnumerateArray().Select(t => t.GetString() ?? "");
            var ranked = texts
                .Select(AttributeStability.AnalyzeLandmarkText)
                .Where(v => v.IsUsable && v.Match == MatchKind.Exact && !string.Equals(v.Value, target.Text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => v.Value.Length)
                .Take(MaxLandmarks);

            foreach (var verdict in ranked)
            {
                var check = await session.EvaluateValueAsync(PageScript.CheckLandmark(i, Js(verdict.Value)), ct);
                var level = check.GetProperty("level").GetInt32();
                if (check.GetProperty("count").GetInt32() != 1 || level < 1) continue;
                return new TextAnchor(i, box.Tag, verdict.Value, level, verdict.Stability == Stability.Stable ? 80 : 65, verdict.Warning);
            }
        }
        return null;
    }

    private static string XPathLiteral(string value)
    {
        if (!value.Contains('\'')) return $"'{value}'";
        if (!value.Contains('"')) return $"\"{value}\"";
        return "concat('" + value.Replace("'", "', \"'\", '") + "')";
    }

    // ------------------------------------------------------------------ frames

    private static async Task<IReadOnlyList<WebLocator>> FrameLocatorsAsync(CdpSession session, List<DomNode> frames, CancellationToken ct)
    {
        var locators = new List<WebLocator>();
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var options = new List<string>();
            if (f.Id.Length > 0 && AttributeStability.AnalyzeAutomationId(f.Id).IsUsable) options.Add(IdSelector(f.Tag, f.Id));
            foreach (var attribute in new[] { "name", "title", "src" })
            {
                if (f.Attributes.TryGetValue(attribute, out var value) && value.Length > 0) options.Add($"{f.Tag}[{attribute}=\"{CssString(value)}\"]");
            }

            WebLocator? chosen = null;
            foreach (var css in options)
            {
                var m = Match.Parse(await session.EvaluateValueAsync(PageScript.CountFrameCss(i, Js(css)), ct));
                if (m.Count == 1 && m.Index == 0) { chosen = new WebLocator { Kind = WebLocatorKind.Css, Value = css }; break; }
            }
            if (chosen is null)
            {
                var m = Match.Parse(await session.EvaluateValueAsync(PageScript.CountFrameCss(i, Js(f.Tag)), ct));
                chosen = new WebLocator { Kind = WebLocatorKind.Css, Value = f.Tag, Index = m.Count > 1 ? m.Index : null };
            }
            locators.Add(chosen);
        }
        return locators;
    }

    // ------------------------------------------------------------------ verification

    private sealed record Match(int Count, int Index)
    {
        public bool IsUniqueTarget => Count == 1 && Index == 0;

        public SelectorVerification Verification => Count == 0
            ? new(VerificationStatus.NotFound, 0)
            : Index < 0 ? new(VerificationStatus.WrongElement, Count)
            : new(Count == 1 ? VerificationStatus.Unique : VerificationStatus.Ambiguous, Count);

        public static Match Parse(JsonElement e) => new(e.GetProperty("count").GetInt32(), e.GetProperty("index").GetInt32());
    }

    /// <param name="scope">JS expression for the search root, relative to <c>u</c> (the page state).</param>
    /// <param name="expectedIndex">Chain index of the element that should be the (only) match.</param>
    private static async Task<Match> CountAsync(CdpSession session, AxCache ax, WebLocator locator, string scope, int expectedIndex, CancellationToken ct)
    {
        if (locator.AsCss is { } css)
        {
            var expectedSetup = $"{PageScript.State}.expected = {PageScript.State}.chain[{expectedIndex}]";
            var script = PageScript.CountCss(scope, Js(css), deep: true).Replace("m.indexOf(u.target)", "m.indexOf(u.expected)");
            await session.EvaluateValueAsync(expectedSetup + ", true", ct);
            return Match.Parse(await session.EvaluateValueAsync(script, ct));
        }

        // Role locators: check each possible element's computed role and name in Chromium's
        // accessibility tree, which is what getByRole matches against. (Accessibility.queryAXTree
        // would do this in one call, but it never answers while the page isn't being rendered,
        // e.g. when the app's window is covered by the inspector.)
        var expectedBackend = (await ax.GetAsync(expectedIndex, ct)).BackendId;
        var tag = Js(SimpleIdentifier().IsMatch(await ax.TagAsync(expectedIndex, ct)) ? await ax.TagAsync(expectedIndex, ct) : "");
        var arrayId = await session.EvaluateObjectIdAsync(
            $"(() => {{ const u = {PageScript.State}; return u.roleCandidates({scope}, {Js(locator.Role)}, {tag}); }})()", ct)
            ?? throw new CdpException("Lost the page element; pick it again.");

        var properties = await session.SendAsync("Runtime.getProperties", new { objectId = arrayId, ownProperties = true }, ct);
        var elementIds = properties.GetProperty("result").EnumerateArray()
            .Where(p => int.TryParse(p.GetProperty("name").GetString(), out _))
            .OrderBy(p => int.Parse(p.GetProperty("name").GetString()!))
            .Select(p => p.GetProperty("value").TryGetProperty("objectId", out var id) ? id.GetString() : null)
            .OfType<string>()
            .ToList();
        if (elementIds.Count > MaxRoleCandidates) return new Match(0, -1); // too many to check; skip this strategy

        var matches = new List<int>();
        foreach (var elementId in elementIds)
        {
            var tree = await session.SendAsync("Accessibility.getPartialAXTree", new { objectId = elementId, fetchRelatives = false }, ct);
            var node = tree.GetProperty("nodes").EnumerateArray().FirstOrDefault(n => n.TryGetProperty("backendDOMNodeId", out _));
            if (node.ValueKind == JsonValueKind.Undefined) continue;
            if (node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean()) continue;
            var role = node.TryGetProperty("role", out var r) && r.TryGetProperty("value", out var rv) ? rv.GetString() : null;
            if (role != locator.Role) continue;
            if (locator.Name.Length > 0 && !NameMatches(AxInfo.ReadName(node), locator)) continue;
            matches.Add(node.GetProperty("backendDOMNodeId").GetInt32());
        }
        return new Match(matches.Count, matches.IndexOf(expectedBackend));
    }

    private static bool NameMatches(string name, WebLocator locator) => locator.Exact
        ? string.Equals(name, locator.Name, StringComparison.Ordinal)
        : name.Contains(locator.Name, StringComparison.OrdinalIgnoreCase);

    private static async Task<int> BackendIdAsync(CdpSession session, string objectId, CancellationToken ct)
    {
        var described = await session.SendAsync("DOM.describeNode", new { objectId }, ct);
        return described.GetProperty("node").GetProperty("backendNodeId").GetInt32();
    }

    // ------------------------------------------------------------------ accessibility info

    private sealed record AxInfo(string Role, string Name, bool Ignored, int BackendId)
    {
        public static readonly AxInfo Empty = new("", "", true, -1);

        public static string ReadName(JsonElement node) =>
            node.TryGetProperty("name", out var n) && n.TryGetProperty("value", out var v) ? v.GetString()?.Trim() ?? "" : "";
    }

    /// <summary>Accessible role/name of chain elements, fetched once each from Chromium.</summary>
    private sealed class AxCache(CdpSession session)
    {
        private readonly Dictionary<int, AxInfo> _cache = [];
        private readonly Dictionary<int, string> _tags = [];

        public async Task<string> TagAsync(int chainIndex, CancellationToken ct)
        {
            if (_tags.TryGetValue(chainIndex, out var tag)) return tag;
            var value = await session.EvaluateValueAsync($"{PageScript.State}.chain[{chainIndex}].tagName.toLowerCase()", ct);
            return _tags[chainIndex] = value.GetString() ?? "";
        }

        public async Task<AxInfo> GetAsync(int chainIndex, CancellationToken ct)
        {
            if (_cache.TryGetValue(chainIndex, out var cached)) return cached;

            var objectId = await session.EvaluateObjectIdAsync($"{PageScript.State}.chain[{chainIndex}]", ct);
            if (objectId is null) return _cache[chainIndex] = AxInfo.Empty;

            var backend = await BackendIdAsync(session, objectId, ct);
            var tree = await session.SendAsync("Accessibility.getPartialAXTree", new { objectId, fetchRelatives = false }, ct);
            var node = tree.GetProperty("nodes").EnumerateArray()
                .FirstOrDefault(n => n.TryGetProperty("backendDOMNodeId", out var b) && b.GetInt32() == backend);

            var info = node.ValueKind == JsonValueKind.Undefined
                ? AxInfo.Empty with { BackendId = backend }
                : new AxInfo(
                    node.TryGetProperty("role", out var r) && r.TryGetProperty("value", out var rv) ? rv.GetString() ?? "" : "",
                    AxInfo.ReadName(node),
                    node.TryGetProperty("ignored", out var ig) && ig.GetBoolean(),
                    backend);
            return _cache[chainIndex] = info;
        }
    }

    // ------------------------------------------------------------------ helpers

    private sealed record DomNode(string Tag, string Id, IReadOnlyDictionary<string, string> Attributes, IReadOnlyList<string> Classes, string Text, bool InShadow)
    {
        public bool IsNativelyInteractive =>
            Tag is "button" or "input" or "select" or "textarea" or "summary" or "option"
            || (Tag == "a" && Attributes.ContainsKey("href"))
            || Attributes.ContainsKey("role");

        public static DomNode Parse(JsonElement e) => new(
            e.GetProperty("tag").GetString() ?? "",
            e.GetProperty("id").GetString() ?? "",
            e.GetProperty("attrs").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? ""),
            e.GetProperty("classes").EnumerateArray().Select(c => c.GetString() ?? "").ToList(),
            e.GetProperty("text").GetString() ?? "",
            e.GetProperty("inShadow").GetBoolean());
    }

    private static bool IsPageRoot(ElementInfo e) =>
        e.FrameworkId == "Chrome" && e.ControlType == "Document" && e.AutomationId == "RootWebArea";

    private static bool IsPlaywrightRole(string role) =>
        role.Length > 0 && !NonRoles.Contains(role) && role.All(char.IsLower);

    private static string IdSelector(string tag, string id) =>
        SimpleIdentifier().IsMatch(id) ? $"#{id}" : $"{tag}[id=\"{CssString(id)}\"]";

    private static string CssString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Js(string value) => JsonSerializer.Serialize(value);

    /// <summary>Origin for web pages (so a changing path doesn't break page lookup), full URL without query otherwise.</summary>
    private static string UrlPrefix(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (uri.Scheme is "http" or "https") return $"{uri.Scheme}://{uri.Authority}/";
        return uri.GetLeftPart(UriPartial.Path);
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex SimpleIdentifier();
}

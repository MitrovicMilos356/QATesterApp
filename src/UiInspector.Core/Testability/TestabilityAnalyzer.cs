using System.Diagnostics;
using System.Drawing;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UiInspector.Core.Reporting;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Testability;

/// <summary>
/// Grades every control of a window for test automation: how the generator would find it, what
/// makes that fragile, and what the developers can change. The window is read in one UIA call
/// and every selector is built and checked against that copy, so a window of a few hundred
/// controls takes a few seconds, without clicking anything.
/// </summary>
public sealed partial class TestabilityAnalyzer(UiaWorker worker, ElementInspector inspector)
{
    private const int MaxElements = 5000;
    private const int MaxFindings = 400;
    private const int MaxScriptedControls = 6;

    // Long lists (rows, messages) repeat the same control: grading the first few says it all.
    private const int RepeatThreshold = 6;
    private const int RepeatsGraded = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> InteractiveTypes =
    [
        "Button", "SplitButton", "CheckBox", "RadioButton", "Edit", "ComboBox", "Hyperlink", "ListItem",
        "MenuItem", "TabItem", "TreeItem", "Slider", "Spinner", "DataItem",
    ];

    private static readonly HashSet<string> ActionPatterns = ["Invoke", "Toggle", "SelectionItem", "ExpandCollapse", "Value"];

    // Parts of the window frame and scrolling, which tests don't target.
    private static readonly HashSet<string> SkippedSubtrees = ["TitleBar", "ScrollBar", "Thumb"];

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "brave", "opera", "vivaldi", "firefox",
    };

    /// <param name="anyElement">Any element of the window to analyse (the window itself is fine).</param>
    public Task<TestabilityReport> AnalyzeAsync(ElementInfo anyElement, CancellationToken cancellationToken = default) =>
        worker.RunAsync(a => Analyze(a, anyElement), cancellationToken, Timeout);

    internal TestabilityReport Analyze(AutomationBase automation, ElementInfo anyElement)
    {
        var watch = Stopwatch.StartNew();
        if (anyElement.Ref is null) throw new InvalidOperationException("The element has no live handle; pick it again.");
        var window = TopLevelWindow(automation, anyElement.Ref.Element)
                     ?? throw new InvalidOperationException("That's the desktop itself. Pick an element inside a window.");

        var desktop = inspector.ReadDesktop(automation);
        var root = inspector.ReadSubtree(automation, window, MaxElements, out var truncated)
                   ?? throw new InvalidOperationException("The window closed before it could be read.");
        var snapshot = TreeSnapshot.Capture(automation, [desktop, root.Info])
                       ?? throw new InvalidOperationException("The window closed before it could be read.");

        // In a browser the toolbar and tabs aren't the app under test: only the page is analysed.
        var start = (Path: new List<ElementInfo> { desktop }, Node: root);
        if (Browsers.Contains(root.Info.ProcessName) && ChoosePage(root, desktop, anyElement) is { } page) start = page;

        var candidates = new List<IReadOnlyList<ElementInfo>>();
        var scanned = 0;
        Collect(start.Node, start.Path, candidates, ref scanned);
        scanned += start.Path.Count - 1;

        var generator = new SelectorGenerator(inspector);
        var findings = new List<TestabilityFinding>();
        var repeats = RepeatCounts(candidates);
        var graded = new Dictionary<string, (int Count, int FindingIndex)>();
        var windowInfo = root.Info;

        foreach (var path in candidates)
        {
            if (!path.Skip(1).All(snapshot.Contains)) continue; // appeared between the two reads
            var key = RepeatKey(path);
            if (repeats[key] >= RepeatThreshold)
            {
                var seen = graded.GetValueOrDefault(key, (Count: 0, FindingIndex: -1));
                if (seen.Count >= RepeatsGraded)
                {
                    // Counted against the first graded one of its kind.
                    var first = findings[seen.FindingIndex];
                    findings[seen.FindingIndex] = first with { SimilarNotListed = first.SimilarNotListed + 1 };
                    continue;
                }
                graded[key] = (seen.Count + 1, seen.FindingIndex < 0 ? findings.Count : seen.FindingIndex);
            }
            if (findings.Count >= MaxFindings)
            {
                truncated = true;
                break;
            }

            var result = generator.Generate(automation, path, snapshot);
            findings.Add(Grade(findings.Count + 1, path, result, snapshot, windowInfo));
        }

        var frameworks = candidates
            .Select(p => p[^1].FrameworkId)
            .Where(f => f.Length > 0)
            .GroupBy(f => f)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();

        var report = new TestabilityReport
        {
            Window = windowInfo,
            Findings = findings,
            Groups = Group(findings),
            ElementsScanned = scanned,
            Truncated = truncated,
            Frameworks = frameworks,
            Screenshot = Screenshot(windowInfo, findings),
        };
        report = report with { Plan = Plan(automation, start.Node, start.Path, report, snapshot) };
        return report with { Duration = watch.Elapsed };
    }

    /// <summary>The test suggestions for the same read: features, their test cases, and a script for each main flow.</summary>
    private static TestIdeas.TestPlan Plan(AutomationBase automation, ElementNode root, IReadOnlyList<ElementInfo> ancestors, TestabilityReport report, TreeSnapshot snapshot)
    {
        var isWeb = IsPage(root.Info);
        Func<TestIdeas.FeatureControl, Web.WebResult?>? web = null;
        if (isWeb)
        {
            // Playwright and Selenium locators from the accessibility tree, as for recorded steps (no debug port needed).
            var pagePath = ancestors.Append(root.Info).ToList();
            var pageUrl = Web.WebApps.ReadPageUrl(pagePath);
            var launch = Web.WebApps.Describe(pagePath);
            web = control =>
            {
                if (control.Result is not { Best: not null } native) return null;
                var locators = Web.AccessibilityLocators.FromSelector(native, control.Path, pageUrl, launch);
                locators = Web.AccessibilityLocators.WithPrecedingLabel(locators, control.Path, snapshot);
                return Web.AccessibilityLocators.WithInnerText(locators, control.Path, pageUrl, launch, snapshot);
            };
        }

        // Controls a script needs that weren't graded one by one (look-alikes in a long row, like page
        // numbers) get their selector now, from the same snapshot.
        var generator = new SelectorGenerator(new ElementInspector());
        TestIdeas.FeatureControl Ensure(TestIdeas.FeatureControl c) =>
            c.Result is null && c.Path.Skip(1).All(snapshot.Contains) ? c with { Result = generator.Generate(automation, c.Path, snapshot) } : c;

        var features = TestIdeas.FeatureDetector.Detect(root, ancestors, report)
            .Select(f => f with
            {
                Fields = f.Fields.Select(x => x with { Control = Ensure(x.Control) }).ToList(),
                Action = f.Action is null ? null : Ensure(f.Action),
                Controls = f.Controls.Select((c, i) => i < MaxScriptedControls ? Ensure(c) : c).ToList(),
            })
            .Select(f => f with
            {
                Cases = TestIdeas.TestCaseCatalog.For(f, isWeb),
                Scripts = TestIdeas.TestPlanScripts.Build(f, web),
                Warnings = f.Fields.Select(x => x.Control).Concat(f.Controls).Append(f.Action).OfType<TestIdeas.FeatureControl>()
                    .Where(c => c.Grade == TestabilityGrade.Fragile)
                    .Select(c => $"{TestIdeas.FeatureDetector.Label(c.Element)}: only found by its position; scripts may click the wrong one when the page changes (see the testability report).")
                    .Distinct()
                    .ToList(),
            })
            // The suites run the cases, so they're built once the cases are there.
            .Select(f => f with { Suites = TestIdeas.TestSuites.Build(f, isWeb, web) })
            .ToList();
        return new TestIdeas.TestPlan { Window = report.Window, Features = features, IsWeb = isWeb };
    }

    private static AutomationElement? TopLevelWindow(AutomationBase automation, AutomationElement element)
    {
        var desktop = automation.GetDesktop();
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        if (automation.Compare(element, desktop)) return null;
        var current = element;
        for (var parent = walker.GetParent(current); parent is not null; parent = walker.GetParent(current))
        {
            if (automation.Compare(parent, desktop)) return current;
            current = parent;
        }
        return current;
    }

    /// <summary>
    /// The page to grade. A browser window holds more than the open tab's page: other tabs (YouTube
    /// playing in the background; Firefox keeps every open tab's page, marked off-screen), the
    /// sidebar, PDF viewers. The page that contains the element
    /// the person pointed at wins; otherwise the visible page that takes the most room.
    /// </summary>
    internal static (List<ElementInfo> Path, ElementNode Node)? ChoosePage(ElementNode root, ElementInfo desktop, ElementInfo pointedAt)
    {
        var pages = new List<(List<ElementInfo> Path, ElementNode Node)>();
        FindPages(root, [desktop], pages);
        if (pages.Count == 0) return null;
        foreach (var page in pages)
        {
            if (page.Node.Info.IsSameElement(pointedAt) || Contains(page.Node, pointedAt)) return page;
        }
        var visible = pages.Where(p => !p.Node.Info.IsOffscreen).ToList();
        return (visible.Count > 0 ? visible : pages).MaxBy(p => (long)p.Node.Info.BoundingRectangle.Width * p.Node.Info.BoundingRectangle.Height);
    }

    /// <summary>
    /// A web page: Chromium (Edge, Chrome, Electron) reports it as a Document with AutomationId
    /// "RootWebArea"; Firefox as a Document of its own (its toolbars and menus are never Documents).
    /// </summary>
    internal static bool IsPage(ElementInfo e) =>
        e.ControlType == "Document" && (e.AutomationId == "RootWebArea" || e.FrameworkId == "Gecko");

    // Outermost pages only: a page's iframes show up as pages inside it, and belong to it.
    private static void FindPages(ElementNode node, List<ElementInfo> path, List<(List<ElementInfo> Path, ElementNode Node)> into)
    {
        if (IsPage(node.Info))
        {
            into.Add((path, node));
            return;
        }
        var here = new List<ElementInfo>(path) { node.Info };
        foreach (var child in node.Children) FindPages(child, here, into);
    }

    private static bool Contains(ElementNode node, ElementInfo element) =>
        node.Children.Any(c => c.Info.IsSameElement(element) || Contains(c, element));

    private static void Collect(ElementNode node, List<ElementInfo> ancestors, List<IReadOnlyList<ElementInfo>> into, ref int scanned)
    {
        scanned++;
        var info = node.Info;
        if (SkippedSubtrees.Contains(info.ControlType)) return;
        var path = new List<ElementInfo>(ancestors) { info };
        if (ancestors.Count >= 2 && IsInteractive(info)) into.Add(path);
        foreach (var child in node.Children) Collect(child, path, into, ref scanned);
    }

    private static bool IsInteractive(ElementInfo e)
    {
        if (InteractiveTypes.Contains(e.ControlType)) return true;
        if (IsPage(e)) return false;
        // Clickable containers (a <div> with a click handler shows up as a Group with Invoke).
        return e.ControlType is not ("Window" or "Pane" or "List" or "Tree" or "Table" or "DataGrid" or "Menu" or "MenuBar" or "Tab" or "ToolBar" or "StatusBar")
               && e.Patterns.Any(ActionPatterns.Contains);
    }

    // Same kind of control under the same parent (list rows), or the same control again and again (each row's Delete).
    private static string RepeatKey(IReadOnlyList<ElementInfo> path)
    {
        var e = path[^1];
        return $"{string.Join('.', path[^2].RuntimeId)}/{e.ControlType}";
    }

    private static Dictionary<string, int> RepeatCounts(List<IReadOnlyList<ElementInfo>> candidates) =>
        candidates.GroupBy(RepeatKey).ToDictionary(g => g.Key, g => g.Count());

    private static TestabilityFinding Grade(int number, IReadOnlyList<ElementInfo> path, SelectorResult result, TreeSnapshot snapshot, ElementInfo window)
    {
        var e = path[^1];
        var issues = new List<string>();
        var problems = TestabilityProblem.None;
        TestabilityGrade grade;
        var best = result.Best;

        if (best is null)
        {
            grade = TestabilityGrade.Fragile;
            problems |= TestabilityProblem.NotFound;
            issues.Add(result.Problem ?? "No selector could be built for it.");
        }
        else if (best.Verification.Status != VerificationStatus.Unique)
        {
            grade = TestabilityGrade.Fragile;
            problems |= TestabilityProblem.NotFound;
            issues.Add(best.Verification.Describe());
        }
        else if (best.Steps.Any(s => s.Index is not null))
        {
            grade = TestabilityGrade.Fragile;
            problems |= TestabilityProblem.Position;
            var position = best.Warnings.FirstOrDefault(w => w.StartsWith("Uses position", StringComparison.Ordinal));
            issues.Add("Only its position tells it apart from controls like it" + (position is null ? "." : $": {Lower(position)}"));
        }
        else
        {
            var target = best.Steps.Count > 0 ? best.Steps[^1] : best.Window;
            var id = AttributeStability.AnalyzeAutomationId(e.AutomationId, e.FrameworkId, e.NativeWindowHandle);
            var byId = target.Get(SelectorProperty.AutomationId) is not null;
            var anchor = best.Steps.FirstOrDefault(s => s.Has is not null)?.Has?.Get(SelectorProperty.Name)?.Value;
            var namedScope = best.Steps.Take(best.Steps.Count - 1).Any(s => s.Get(SelectorProperty.Name) is not null);

            grade = byId && id.Stability == Stability.Stable && anchor is null && !namedScope ? TestabilityGrade.Good : TestabilityGrade.Fair;
            if (anchor is not null)
            {
                problems |= TestabilityProblem.NearbyText;
                issues.Add($"Found by text near it (“{anchor}”): it breaks if that text changes.");
            }
            if (target.Get(SelectorProperty.Name) is { } name)
            {
                problems |= TestabilityProblem.OwnText;
                var text = Visible(name.Value);
                issues.Add(text.Length == 0
                    ? "Found by the icon-font character that is its name: it breaks when the icon changes."
                    : $"Found by its text (“{text}”): it breaks when the wording or the app's language changes.");
            }
            else if (!byId)
            {
                problems |= TestabilityProblem.TypeOnly;
                issues.Add(target.Get(SelectorProperty.ClassName) is not null
                    ? "Found only by its type and class: another control like it breaks the test."
                    : "Found only by its type: another control like it breaks the test.");
            }
            if (byId && id.Stability == Stability.Weak && id.Warning is not null) issues.Add(id.Warning);
        }

        // Problems with its id, whatever the selector ended up using.
        var verdict = AttributeStability.AnalyzeAutomationId(e.AutomationId, e.FrameworkId, e.NativeWindowHandle);
        var badId = e.AutomationId.Length > 0 && !verdict.IsUsable;
        // A WinForms control without a Name reports its window handle: that's simply "no id", which the fix covers.
        var handleAsId = e.AutomationId.All(char.IsDigit) && (e.FrameworkId == "WinForm" || e.AutomationId == e.NativeWindowHandle.ToInt64().ToString());
        if (badId && !handleAsId && verdict.Warning is not null)
        {
            problems |= TestabilityProblem.ChangingId;
            issues.Add(verdict.Warning);
        }
        var duplicates = 0;
        if (verdict.IsUsable)
        {
            var sameId = new SelectorStep([new(SelectorProperty.ControlType, e.ControlType), new(SelectorProperty.AutomationId, e.AutomationId)]);
            duplicates = snapshot.FindAll(window, sameId, TreeScope.Descendants).Count;
            if (duplicates > 1)
            {
                problems |= TestabilityProblem.DuplicateId;
                issues.Add($"Its AutomationId “{e.AutomationId}” is used by {duplicates} controls of this kind.");
            }
        }

        var iconOnly = !string.IsNullOrWhiteSpace(e.Name) && Visible(e.Name).Length == 0;
        var missingName = (string.IsNullOrWhiteSpace(e.Name) || iconOnly) && e.ControlType is not ("ListItem" or "TreeItem" or "DataItem");
        if (missingName)
        {
            problems |= TestabilityProblem.MissingName;
            var kind = e.LocalizedControlType.Length > 0 ? e.LocalizedControlType : e.ControlType.ToLowerInvariant();
            issues.Add(iconOnly
                ? $"Its name is only an icon-font character: screen readers announce it as just “{kind}” (or read out a code), and a test that finds it by that character breaks when the icon changes."
                : $"It has no accessible name: screen readers announce it only as “{kind}”, and tests can't find it by its text.");
        }

        var needsId = grade != TestabilityGrade.Good || badId || duplicates > 1;
        // Found by text near it without a position: one of several rows. (A popup found by its text, with
        // a position inside it, is one control among look-alikes instead.)
        var inRows = problems.HasFlag(TestabilityProblem.NearbyText);
        var context = best?.Steps.Take(best.Steps.Count - 1).Select(s => s.Get(SelectorProperty.AutomationId)?.Value).LastOrDefault(v => v is not null);
        return new TestabilityFinding
        {
            Number = number,
            Path = path,
            Grade = grade,
            FoundBy = best?.Strategy,
            Issues = issues,
            Fix = Fix(e, needsId, missingName, context, inRows, lookAlikes: problems.HasFlag(TestabilityProblem.Position)),
            Problems = problems,
            Selector = best,
            Result = result,
            Where = Where(path),
        };
    }

    // ---------------------------------------------------------------- grouping

    private const int MaxWhereLength = 50;
    private const int ExamplesInTitle = 3;

    private static readonly HashSet<string> SectionTypes =
    [
        "Group", "Pane", "List", "ListItem", "Table", "DataItem", "Tab", "TabItem", "Menu", "ToolBar", "Custom", "Header", "Tree", "TreeItem", "DataGrid",
    ];

    // Web page landmarks (ARIA roles), which most sites have even when nothing else is named.
    private static readonly Dictionary<string, string> Landmarks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["banner"] = "the header",
        ["navigation"] = "the navigation",
        ["main"] = "the main content",
        ["contentinfo"] = "the footer",
        ["complementary"] = "the sidebar",
        ["search"] = "the search area",
        ["form"] = "a form",
        ["dialog"] = "a dialog",
        ["alertdialog"] = "a dialog",
    };

    /// <summary>
    /// "in “Programiranje”" or "in the navigation": the nearest named section or page landmark
    /// above the control, inside the window or page.
    /// </summary>
    internal static string Where(IReadOnlyList<ElementInfo> path)
    {
        var element = path[^1];
        for (var i = path.Count - 2; i >= 2; i--)
        {
            var section = path[i];
            if (IsPage(section)) break;
            var name = Visible(section.Name);
            if (SectionTypes.Contains(section.ControlType) && name.Length > 0 && name != Visible(element.Name))
            {
                var role = Landmarks.TryGetValue(section.AriaRole, out var landmark) ? $"{landmark} " : "";
                return $"in {role}“{(name.Length > MaxWhereLength ? name[..(MaxWhereLength - 1)] + "…" : name)}”";
            }
            if (Landmarks.TryGetValue(section.AriaRole, out var unnamed)) return $"in {unnamed}";
        }
        return "";
    }

    /// <summary>
    /// The text a person sees: without icon-font glyphs (Unicode's private use area), which sites
    /// use for icons and screen readers can't read.
    /// </summary>
    public static string Visible(string name) =>
        CodeTemplates.OneLine(new string(name.Where(c => c is < '' or > '').ToArray()).Trim());

    /// <summary>Same grade, same problems, same kind of control, found the same way (whatever the text): one group.</summary>
    internal static IReadOnlyList<TestabilityGroup> Group(IReadOnlyList<TestabilityFinding> findings) => findings
        .GroupBy(GroupKey)
        .Select(g => g.ToList())
        .Select(list => new TestabilityGroup
        {
            Findings = list,
            Title = GroupTitle(list),
            Where = list.Select(f => f.Where).Distinct().Count() == 1 ? list[0].Where : "",
        })
        .OrderByDescending(g => g.Grade)
        .ThenByDescending(g => g.Count)
        .ThenBy(g => g.First.Number)
        .ToList();

    // Built the same way: the same kind of control, in the same kind of parent and grandparent (so the
    // job list's links and the footer's links stay apart), found the same way whatever the text.
    private static string GroupKey(TestabilityFinding f)
    {
        var e = f.Element;
        var foundBy = f.FoundBy is null ? "" : QuotedText().Replace(Position().Replace(f.FoundBy, "#n"), "\"…\"");
        var structure = string.Join(">", f.Path.Reverse().Skip(1).Take(2).Select(Shape));
        var landmark = f.Path.Reverse().Skip(1).Select(a => Landmarks.GetValueOrDefault(a.AriaRole)).FirstOrDefault(l => l is not null) ?? "";
        return $"{f.Grade}|{(int)f.Problems}|{Shape(e)}|{structure}|{landmark}|{foundBy}";
    }

    private static string Shape(ElementInfo e)
    {
        var className = AttributeStability.AnalyzeClassName(e.ClassName, e.FrameworkId);
        return className.IsUsable ? $"{e.ControlType}.{className.Value}" : e.ControlType;
    }

    private static string GroupTitle(IReadOnlyList<TestabilityFinding> list)
    {
        var first = StepWording.Element(list[0].Element with { Name = Visible(list[0].Element.Name) });
        var count = list.Count + list.Sum(f => f.SimilarNotListed);
        if (count == 1) return char.ToUpperInvariant(first[0]) + first[1..];

        var kind = Kind(list[0].Element);
        var names = list
            .Select(f => Visible(f.Element.Name))
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();
        var examples = names.Take(ExamplesInTitle).Select(n => n.Length > 40 ? $"“{n[..39]}…”" : $"“{n}”").ToList();
        var like = examples.Count == 0 ? "" : $" like {string.Join(", ", examples)}{(names.Count > examples.Count ? "…" : "")}";
        return $"{count} {kind}{like}";
    }

    // "the “Save” button" → "buttons", "the “Remember me” check box" → "check boxes".
    private static string Kind(ElementInfo e)
    {
        var words = StepWording.Element(e with { Name = "" });
        var kind = words.StartsWith("the ", StringComparison.Ordinal) ? words[4..] : words;
        return kind.EndsWith('x') ? kind + "es" : kind + "s";
    }

    [System.Text.RegularExpressions.GeneratedRegex("\"[^\"]*\"|“[^”]*”")]
    private static partial System.Text.RegularExpressions.Regex QuotedText();

    [System.Text.RegularExpressions.GeneratedRegex(@"#\d+")]
    private static partial System.Text.RegularExpressions.Regex Position();

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>The change that makes the element easy to find, in the technology it's built with.</summary>
    /// <param name="context">The AutomationId of the container it's found in, if any ("ordersPanel"), to start the suggested id with.</param>
    /// <param name="inRows">It repeats in rows told apart only by their text, so its id has to differ per row.</param>
    /// <param name="lookAlikes">Identical controls sit next to it, so one suggested id can't fit them all.</param>
    internal static string? Fix(ElementInfo e, bool needsId, bool missingName, string? context = null, bool inRows = false, bool lookAlikes = false)
    {
        if (!needsId && !missingName) return null;
        var camel = SuggestedId(e, kebab: false, context);
        var kebab = SuggestedId(e, kebab: true, context);
        var parts = new List<string>();
        if (needsId)
        {
            parts.Add(e.FrameworkId switch
            {
                "WPF" => $"Add AutomationProperties.AutomationId=\"{camel}\" to it in XAML.",
                "XAML" or "DirectUI" => $"Add AutomationProperties.AutomationId=\"{camel}\" to it in XAML.",
                "WinForm" => $"Set the control's Name to \"{camel}\" (WinForms reports the Name as the AutomationId).",
                "Chrome" or "Gecko" => $"Give the HTML element a fixed id=\"{kebab}\" (every tool sees it) or data-testid=\"{kebab}\" (Playwright, Selenium).",
                "Qt" => $"Call setObjectName(\"{camel}\") on the widget.",
                "Win32" => "Give it a control ID that's unique in its dialog.",
                _ => $"Give it a unique AutomationId, e.g. \"{camel}\".",
            });
            if (inRows) parts.Add("It repeats in every row, so add the row's key to the id (e.g. the order number), or give each row an id of its own.");
            else if (lookAlikes) parts.Add("The controls that look like it each need their own id, named after what each one does.");
        }
        if (missingName)
        {
            parts.Add(e.FrameworkId switch
            {
                "WPF" or "XAML" or "DirectUI" => "Give it AutomationProperties.Name (or visible text) saying what it does.",
                "WinForm" => "Set its AccessibleName (or Text) to what it does.",
                "Chrome" or "Gecko" => "Give it an aria-label (or visible text) saying what it does.",
                "Qt" => "Call setAccessibleName() with what it does.",
                _ => "Give it an accessible name saying what it does.",
            });
        }
        return string.Join(" ", parts);
    }

    /// <summary>An id made from the element's name and kind: "saveButton", or "save-button" for HTML.</summary>
    internal static string SuggestedId(ElementInfo e, bool kebab, string? context = null)
    {
        var words = new List<string>();
        if (context is not null)
        {
            // "ordersPanel" → "orders": the container's kind is noise in the element's id.
            var stem = SuffixWords().Replace(context, "");
            words.AddRange(CamelWords().Matches(stem.Length > 0 ? stem : context).Select(m => m.Value.ToLowerInvariant()));
        }
        var nameWords = CodeTemplates.OneLine(e.Name)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(w => w.Length > 0)
            .Take(4)
            .ToList();
        words.AddRange(nameWords.Count > 0 ? nameWords : ["what", "it", "does"]); // no name to go by: a placeholder to replace
        var kind = e.ControlType switch
        {
            "Edit" or "Document" => "field",
            "CheckBox" => "checkbox",
            "RadioButton" => "option",
            "ComboBox" => "dropdown",
            "Hyperlink" => "link",
            "ListItem" or "DataItem" => "item",
            "TabItem" => "tab",
            "MenuItem" => "menu-item",
            "TreeItem" => "node",
            _ => "button",
        };
        if (words[^1] != kind.Split('-')[^1]) words.AddRange(kind.Split('-'));

        if (kebab) return string.Join('-', words);
        var sb = new StringBuilder(words[0]);
        foreach (var w in words.Skip(1)) sb.Append(char.ToUpperInvariant(w[0])).Append(w[1..]);
        return sb.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:Panel|Pane|Group|Container|Box|View|List|Area)$")]
    private static partial System.Text.RegularExpressions.Regex SuffixWords();

    [System.Text.RegularExpressions.GeneratedRegex(@"[A-Z]?[a-z0-9]+|[A-Z]+(?![a-z])")]
    private static partial System.Text.RegularExpressions.Regex CamelWords();

    private static StepScreenshot? Screenshot(ElementInfo window, IReadOnlyList<TestabilityFinding> findings)
    {
        var image = WindowCapture.CaptureWindow(window.NativeWindowHandle);
        if (image is null) return null;
        var marks = findings
            .Where(f => !f.Element.IsOffscreen)
            .Select(f => new WindowCapture.NumberedMark(f.Element.BoundingRectangle, f.Number, f.Grade switch
            {
                TestabilityGrade.Good => Color.FromArgb(21, 128, 61),
                TestabilityGrade.Fair => Color.FromArgb(217, 119, 6),
                _ => Color.FromArgb(220, 38, 38),
            }));
        return WindowCapture.MarkNumbered(image, marks);
    }
}

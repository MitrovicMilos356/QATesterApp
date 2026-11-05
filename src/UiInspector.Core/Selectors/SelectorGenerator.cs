using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;

namespace UiInspector.Core.Selectors;

/// <summary>
/// Builds selectors for an element and verifies each one against the live UI before offering it.
/// Order of preference: a unique attribute inside the window, then the same scoped to the nearest
/// uniquely identifiable ancestor, then (last resort, flagged fragile) an index among matches.
/// Must run on the <see cref="UiaWorker"/> thread.
/// </summary>
public sealed class SelectorGenerator(ElementInspector inspector)
{
    private const int MaxAlternatives = 4;
    private const int MaxAnchorsTried = 8;
    private const int ScopePenalty = 5;
    private const int IndexPenalty = 45;
    private const int MaxParentWalk = 40;

    private const string LanguageWarning = "Names follow the app's UI language; this part breaks if the app runs in another language.";

    public SelectorResult Generate(AutomationBase automation, IReadOnlyList<ElementInfo> pathFromRoot)
    {
        var target = pathFromRoot[^1];
        var result = new SelectorResult
        {
            Target = target,
            ProcessName = target.ProcessName,
            ProcessPath = TryGetProcessPath(target.ProcessId),
        };

        if (pathFromRoot.Count < 2)
            return result with { Problem = "That's the desktop itself. Pick an element inside a window." };
        if (pathFromRoot.Any(e => e.Ref is null))
            return result with { Problem = "The element has no live handle; pick it again." };

        var occlusion = CheckOcclusion(automation, target);
        var desktop = pathFromRoot[0].Ref!.Element;
        var window = pathFromRoot[1];

        var windowStep = BuildWindowStep(window, out var windowWarnings);
        var windowMatches = FindMatches(automation, desktop, windowStep, TreeScope.Children);
        var windowVerification = Verify(windowMatches, window);

        // Apps like Firefox give every window the same AutomationId; the title then tells them apart.
        if (windowVerification.Status == VerificationStatus.Ambiguous && windowStep.Get(SelectorProperty.Name) is null)
        {
            var title = AttributeStability.AnalyzeWindowTitle(window.Name);
            if (title.IsUsable)
            {
                var withTitle = windowStep with { Conditions = [.. windowStep.Conditions, new(SelectorProperty.Name, title.Value, title.Match)] };
                var titleVerification = Verify(FindMatches(automation, desktop, withTitle, TreeScope.Children), window);
                if (titleVerification.Status == VerificationStatus.Unique)
                {
                    windowStep = withTitle;
                    windowVerification = titleVerification;
                    if (title.Warning is not null) windowWarnings.Add(title.Warning);
                }
            }
        }

        if (windowVerification.Status is VerificationStatus.NotFound or VerificationStatus.WrongElement)
        {
            return result with { Occlusion = occlusion, Problem = "The window can't be found again from the desktop; it may have closed." };
        }
        if (windowVerification.Status == VerificationStatus.Ambiguous)
        {
            windowWarnings.Add($"{windowVerification.MatchCount} open windows match the window part; the code uses the first one found.");
        }

        if (pathFromRoot.Count == 2)
        {
            var windowOnly = new Selector
            {
                Window = windowStep,
                Steps = [],
                Strategy = "Top-level window",
                Score = 90,
                Verification = windowVerification,
                Warnings = windowWarnings,
            };
            return result with { Best = windowOnly, Occlusion = occlusion };
        }

        var selectors = BuildTargetSelectors(automation, pathFromRoot, windowStep, windowWarnings);
        var ranked = selectors
            .OrderByDescending(s => s.Score)
            .DistinctBy(s => s.Strategy)
            .Take(MaxAlternatives + 1)
            .ToList();

        return result with
        {
            Best = ranked.FirstOrDefault(),
            Alternatives = ranked.Skip(1).ToList(),
            Occlusion = occlusion,
            Problem = ranked.Count == 0 ? "No selector could be verified for this element." : null,
        };
    }

    private List<Selector> BuildTargetSelectors(
        AutomationBase automation, IReadOnlyList<ElementInfo> path, SelectorStep windowStep, List<string> windowWarnings)
    {
        var target = path[^1];
        var windowElement = path[1].Ref!.Element;
        var targetCandidates = Candidates(target, isTarget: true);
        var selectors = new List<Selector>();

        Selector Make(IReadOnlyList<SelectorStep> steps, string strategy, int score, IEnumerable<string?> warnings, SelectorVerification verification) => new()
        {
            Window = windowStep,
            Steps = steps,
            Strategy = strategy,
            Score = score,
            Verification = verification,
            Warnings = windowWarnings.Concat(warnings).OfType<string>().Distinct().ToList(),
        };

        // 1. A unique match anywhere inside the window.
        foreach (var candidate in targetCandidates)
        {
            var verification = Verify(FindMatches(automation, windowElement, candidate.Step, TreeScope.Descendants), target);
            if (verification.Status == VerificationStatus.Unique)
            {
                selectors.Add(Make([candidate.Step], candidate.Strategy, candidate.Score, candidate.Warnings, verification));
            }
        }
        if (selectors.Any(s => s.Score >= 80)) return selectors;

        // 2. Unique inside the nearest ancestor that is itself uniquely identifiable.
        var anchors = path.Skip(2).Take(path.Count - 3).Reverse().Take(MaxAnchorsTried);
        (Candidate Step, ElementInfo Element)? usedAnchor = null;
        foreach (var anchor in anchors)
        {
            var anchorCandidate = Candidates(anchor, isTarget: false)
                .FirstOrDefault(c => Verify(FindMatches(automation, windowElement, c.Step, TreeScope.Descendants), anchor).Status == VerificationStatus.Unique);
            if (anchorCandidate is null) continue;

            usedAnchor ??= (anchorCandidate, anchor);
            var found = false;
            foreach (var candidate in targetCandidates)
            {
                var verification = Verify(FindMatches(automation, anchor.Ref!.Element, candidate.Step, TreeScope.Descendants), target);
                if (verification.Status != VerificationStatus.Unique) continue;

                found = true;
                selectors.Add(Make(
                    [anchorCandidate.Step, candidate.Step],
                    $"{candidate.Strategy} inside {Describe(anchorCandidate.Step)}",
                    Math.Min(candidate.Score, anchorCandidate.Score) - ScopePenalty,
                    anchorCandidate.Warnings.Concat(candidate.Warnings),
                    verification));
            }
            if (found) return selectors;
        }
        if (selectors.Count > 0) return selectors;

        // 3. Last resort: position among the matches, inside the best scope available.
        var scopeElement = usedAnchor?.Element.Ref!.Element ?? windowElement;
        foreach (var candidate in targetCandidates)
        {
            var matches = FindMatches(automation, scopeElement, candidate.Step, TreeScope.Descendants);
            var index = matches.FindIndex(m => SameRuntimeId(m, target));
            if (index < 0) continue;

            var indexed = candidate.Step with { Index = index };
            var steps = usedAnchor is { } a ? new[] { a.Step.Step, indexed } : [indexed];
            var scopeText = usedAnchor is { } s ? $" inside {Describe(s.Step.Step)}" : "";
            var warnings = candidate.Warnings
                .Concat(usedAnchor?.Step.Warnings ?? [])
                .Append($"Uses position {index + 1} of {matches.Count} matches; it breaks if items are added, removed or reordered.");
            selectors.Add(Make(steps, $"{candidate.Strategy} #{index + 1}{scopeText}", candidate.Score - IndexPenalty, warnings,
                new SelectorVerification(VerificationStatus.Unique, 1)));
            break;
        }
        return selectors;
    }

    private sealed record Candidate(SelectorStep Step, int Score, string Strategy, IReadOnlyList<string?> Warnings);

    // Ordered strongest first. Every step includes the ControlType: it's cheap, stable across
    // languages and builds, and it stops an ID or name from matching an unrelated element type.
    private static List<Candidate> Candidates(ElementInfo element, bool isTarget)
    {
        var candidates = new List<Candidate>();
        if (string.IsNullOrEmpty(element.ControlType)) return candidates;
        var type = new SelectorCondition(SelectorProperty.ControlType, element.ControlType);

        var id = AttributeStability.AnalyzeAutomationId(element.AutomationId, element.FrameworkId, element.NativeWindowHandle);
        if (id.IsUsable)
        {
            candidates.Add(new(new SelectorStep([type, new(SelectorProperty.AutomationId, id.Value)]),
                id.Stability == Stability.Stable ? 100 : 70, "AutomationId", [id.Warning]));
        }

        var name = AttributeStability.AnalyzeName(element.Name);
        if (name.IsUsable)
        {
            candidates.Add(new(new SelectorStep([type, new(SelectorProperty.Name, name.Value, name.Match)]),
                name.Stability == Stability.Stable ? 80 : 65,
                name.Match == MatchKind.Contains ? "Name (stable part)" : "Name",
                [name.Warning, LanguageWarning]));
        }

        var className = AttributeStability.AnalyzeClassName(element.ClassName, element.FrameworkId);
        if (className.IsUsable)
        {
            candidates.Add(new(new SelectorStep([type, new(SelectorProperty.ClassName, className.Value)]),
                50, "ClassName", [className.Warning]));
        }

        // A bare ControlType only identifies the target when it's the only one of its kind in scope.
        if (isTarget) candidates.Add(new(new SelectorStep([type]), 30, "ControlType", []));
        return candidates;
    }

    private static SelectorStep BuildWindowStep(ElementInfo window, out List<string> warnings)
    {
        warnings = [];
        var conditions = new List<SelectorCondition>();
        if (!string.IsNullOrEmpty(window.ControlType)) conditions.Add(new(SelectorProperty.ControlType, window.ControlType));

        var id = AttributeStability.AnalyzeAutomationId(window.AutomationId, window.FrameworkId, window.NativeWindowHandle);
        if (id.IsUsable)
        {
            conditions.Add(new(SelectorProperty.AutomationId, id.Value));
            if (id.Warning is not null) warnings.Add(id.Warning);
        }

        var className = AttributeStability.AnalyzeClassName(window.ClassName, window.FrameworkId);
        if (className.IsUsable) conditions.Add(new(SelectorProperty.ClassName, className.Value));

        if (!id.IsUsable)
        {
            var title = AttributeStability.AnalyzeWindowTitle(window.Name);
            if (title.IsUsable)
            {
                conditions.Add(new(SelectorProperty.Name, title.Value, title.Match));
                if (title.Warning is not null) warnings.Add(title.Warning);
            }
        }
        return new SelectorStep(conditions);
    }

    private static string Describe(SelectorStep step)
    {
        var type = step.ControlType ?? "element";
        if (step.Get(SelectorProperty.AutomationId) is { } id) return $"{type} #{id.Value}";
        if (step.Get(SelectorProperty.Name) is { } name) return $"{type} \"{name.Value}\"";
        if (step.Get(SelectorProperty.ClassName) is { } cls) return $"{type} .{cls.Value}";
        return type;
    }

    // ------------------------------------------------------------------ live matching

    private sealed record Match(int[] RuntimeId);

    private static List<Match> FindMatches(AutomationBase automation, AutomationElement scope, SelectorStep step, TreeScope treeScope)
    {
        var props = automation.PropertyLibrary.Element;
        var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
        request.Add(props.RuntimeId);
        request.Add(props.Name);
        request.Add(props.AutomationId);
        request.Add(props.ClassName);

        using (request.Activate())
        {
            var found = scope.FindAll(treeScope, BuildCondition(automation.ConditionFactory, step));
            var contains = step.Conditions.Where(c => c.Match == MatchKind.Contains).ToList();
            return found
                .Where(e => contains.All(c => (Read(e, c.Property) ?? "").Contains(c.Value, StringComparison.Ordinal)))
                .Select(e => new Match(e.Properties.RuntimeId.TryGetValue(out var id) ? id : []))
                .ToList();
        }
    }

    private static string? Read(AutomationElement e, SelectorProperty property) => property switch
    {
        SelectorProperty.Name => e.Properties.Name.ValueOrDefault,
        SelectorProperty.AutomationId => e.Properties.AutomationId.ValueOrDefault,
        SelectorProperty.ClassName => e.Properties.ClassName.ValueOrDefault,
        _ => null,
    };

    private static ConditionBase BuildCondition(ConditionFactory cf, SelectorStep step)
    {
        var exact = step.Conditions
            .Where(c => c.Match == MatchKind.Exact)
            .Select(c => c.Property switch
            {
                SelectorProperty.ControlType => cf.ByControlType(Enum.Parse<ControlType>(c.Value)),
                SelectorProperty.AutomationId => cf.ByAutomationId(c.Value),
                SelectorProperty.Name => cf.ByName(c.Value),
                SelectorProperty.ClassName => cf.ByClassName(c.Value),
                _ => throw new ArgumentOutOfRangeException(nameof(step)),
            })
            .ToArray();

        return exact.Length switch
        {
            0 => TrueCondition.Default,
            1 => exact[0],
            _ => new AndCondition(exact),
        };
    }

    private static SelectorVerification Verify(List<Match> matches, ElementInfo expected)
    {
        if (matches.Count == 0) return new(VerificationStatus.NotFound, 0);
        var containsExpected = matches.Any(m => SameRuntimeId(m, expected));
        if (!containsExpected) return new(VerificationStatus.WrongElement, matches.Count);
        return new(matches.Count == 1 ? VerificationStatus.Unique : VerificationStatus.Ambiguous, matches.Count);
    }

    private static bool SameRuntimeId(Match match, ElementInfo element) =>
        element.RuntimeId.Count > 0 && match.RuntimeId.AsSpan().SequenceEqual(element.RuntimeId.ToArray());

    // ------------------------------------------------------------------ occlusion

    /// <summary>Hit-tests the element's center to see whether a real mouse click would reach it.</summary>
    private OcclusionReport CheckOcclusion(AutomationBase automation, ElementInfo target)
    {
        var rect = target.BoundingRectangle;
        if (target.IsOffscreen || rect.Width <= 0 || rect.Height <= 0)
        {
            return new(true, null, "It's off-screen or has no size, so a mouse click can't reach it. " +
                                   "Use a pattern action (Invoke, Select…) or scroll it into view first.");
        }

        var center = new System.Drawing.Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        if (!ScreenInfo.IsOnAnyMonitor(center))
        {
            return new(true, null, "Its center is outside every monitor (the window is off-screen), so a mouse click can't reach it. " +
                                   "Use a pattern action (Invoke, Select…), or move the window on-screen first.");
        }

        var patterns = ElementInspector.GetCacheablePatterns(automation);
        using (ElementInspector.CreateCacheRequest(automation, patterns).Activate())
        {
            var hit = automation.FromPoint(center);
            if (hit is null) return new(false, null, "Nothing reported at its center point.");

            var walker = automation.TreeWalkerFactory.GetControlViewWalker();
            var node = hit;
            for (var depth = 0; node is not null && depth < MaxParentWalk; depth++)
            {
                var runtimeId = node.Properties.RuntimeId.ValueOrDefault ?? [];
                if (runtimeId.AsSpan().SequenceEqual(target.RuntimeId.ToArray()))
                {
                    return new(false, null, depth == 0
                        ? "A click at its center reaches it."
                        : "A click at its center lands on a child element inside it, which is fine.");
                }
                node = walker.GetParent(node);
            }

            var covering = inspector.ReadInfo(hit, patterns);
            var who = $"{(string.IsNullOrEmpty(covering.ControlType) ? "?" : covering.ControlType)} " +
                      $"{(string.IsNullOrEmpty(covering.Name) ? "(no name)" : $"\"{covering.Name}\"")}";
            var message = covering.ProcessId != target.ProcessId
                ? $"Another window is in front of it ({covering.ProcessName}: {who}). A real mouse click would hit that window."
                : $"Covered by {who} at its center. A mouse click would hit that instead; prefer a pattern action, or wait for it to go away.";
            return new(true, covering, message);
        }
    }

    private static string? TryGetProcessPath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}

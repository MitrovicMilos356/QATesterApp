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
    // Searching the live UI is slow, so content anchors only look near the target there. A snapshot
    // is searched in memory, so it can afford whole apps: Discord's popups sit among 600+ unnamed groups.
    private const int MaxContentAnchorDepth = 4;
    private const int MaxLookAlikeContainers = 100;
    private const int MaxLandmarkScan = 300;
    private const int MaxSnapshotLookAlikeContainers = 5000;
    private const int MaxSnapshotLandmarkScan = 3000;
    private const int MaxLandmarksTried = 12;
    private const int ContentAnchorPenalty = 15;
    private const int AnchoredIndexPenalty = 25;

    internal const string LanguageWarning = "Names follow the app's UI language; this part breaks if the app runs in another language.";

    internal const string UnverifiedWarning =
        "Not verified: the element was gone (e.g. its dialog closed) before the selector could be checked, so it may match more than one element.";

    /// <param name="tree">Where to search and verify: the live UI (default), or a snapshot taken when the user clicked.</param>
    public SelectorResult Generate(AutomationBase automation, IReadOnlyList<ElementInfo> pathFromRoot, IElementTree? tree = null)
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

        // A snapshot must hold the inspected path (it may miss content an app added a moment later).
        var live = tree is null || tree is TreeSnapshot snapshot && !pathFromRoot.Skip(1).All(snapshot.Contains);
        if (live) tree = new LiveTree(automation);
        // Whether a click would reach it only means something for the UI as it is now.
        var occlusion = live ? CheckOcclusion(automation, target) : null;
        var window = pathFromRoot[1];

        var windowStep = BuildWindowStep(window, out var windowWarnings);
        var windowMatches = tree!.FindAll(pathFromRoot[0], windowStep, TreeScope.Children);
        var windowVerification = Verify(windowMatches, window);

        // Apps like Firefox give every window the same AutomationId; the title then tells them apart.
        if (windowVerification.Status == VerificationStatus.Ambiguous && windowStep.Get(SelectorProperty.Name) is null)
        {
            var title = AttributeStability.AnalyzeWindowTitle(window.Name);
            if (title.IsUsable)
            {
                var withTitle = windowStep with { Conditions = [.. windowStep.Conditions, new(SelectorProperty.Name, title.Value, title.Match)] };
                var titleVerification = Verify(tree!.FindAll(pathFromRoot[0], withTitle, TreeScope.Children), window);
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

        var selectors = BuildTargetSelectors(tree!, pathFromRoot, windowStep, windowWarnings);
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

    /// <summary>
    /// A selector built from the snapshot alone, for elements that are gone by the time they could
    /// be checked (e.g. a dialog button whose click closed the dialog). It follows the same
    /// preferences, and scopes weaker matches to the nearest ancestor with a stable AutomationId.
    /// Needs no UIA calls.
    /// </summary>
    public static Selector? BuildUnverified(IReadOnlyList<ElementInfo> pathFromRoot)
    {
        if (pathFromRoot.Count < 2) return null;
        var windowStep = BuildWindowStep(pathFromRoot[1], out var warnings);
        var verification = new SelectorVerification(VerificationStatus.Unverified, 0);
        warnings.Add(UnverifiedWarning);

        if (pathFromRoot.Count == 2)
        {
            return new Selector { Window = windowStep, Steps = [], Strategy = "Top-level window (unverified)", Score = 0, Verification = verification, Warnings = warnings };
        }

        var candidate = Candidates(pathFromRoot[^1], isTarget: true).FirstOrDefault();
        if (candidate is null) return null;

        var steps = new List<SelectorStep>();
        var strategy = candidate.Strategy;
        if (candidate.Score < 100)
        {
            var anchor = pathFromRoot.Skip(2).Take(pathFromRoot.Count - 3).Reverse()
                .Select(a => Candidates(a, isTarget: false).FirstOrDefault(c => c.Strategy == "AutomationId" && c.Score == 100))
                .FirstOrDefault(c => c is not null);
            if (anchor is not null)
            {
                steps.Add(anchor.Step);
                strategy += $" inside {Describe(anchor.Step)}";
            }
        }
        steps.Add(candidate.Step);

        return new Selector
        {
            Window = windowStep,
            Steps = steps,
            Strategy = strategy + " (unverified)",
            Score = 0,
            Verification = verification,
            Warnings = warnings.Concat(candidate.Warnings.OfType<string>()).Distinct().ToList(),
        };
    }

    private List<Selector> BuildTargetSelectors(
        IElementTree tree, IReadOnlyList<ElementInfo> path, SelectorStep windowStep, List<string> windowWarnings)
    {
        var target = path[^1];
        var windowElement = path[1];
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
            var verification = Verify(tree.FindAll(windowElement, candidate.Step, TreeScope.Descendants), target);
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
                .FirstOrDefault(c => Verify(tree.FindAll(windowElement, c.Step, TreeScope.Descendants), anchor).Status == VerificationStatus.Unique);
            if (anchorCandidate is null) continue;

            usedAnchor ??= (anchorCandidate, anchor);
            var found = false;
            foreach (var candidate in targetCandidates)
            {
                var verification = Verify(tree.FindAll(anchor, candidate.Step, TreeScope.Descendants), target);
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

        // 3. Inside the nearest ancestor that stands out by text it contains: the row with
        //    "Order 1042" in it, the card titled "Invoices". This is how a person tells
        //    look-alike controls apart, and it survives rows being added or reordered.
        var scopeIndex = usedAnchor is { } scoped ? path.ToList().FindIndex(e => e.IsSameElement(scoped.Element)) : 1;
        selectors.AddRange(ContentAnchoredSelectors(tree, path, Math.Max(scopeIndex, 1), targetCandidates, usedAnchor?.Step, Make));
        if (selectors.Count > 0) return selectors;

        // 4. Last resort: position among the matches, inside the best scope available.
        var scopeElement = usedAnchor?.Element ?? windowElement;
        foreach (var candidate in targetCandidates)
        {
            var matches = tree.FindAll(scopeElement, candidate.Step, TreeScope.Descendants);
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

    private delegate Selector MakeSelector(IReadOnlyList<SelectorStep> steps, string strategy, int score, IEnumerable<string?> warnings, SelectorVerification verification);

    /// <param name="scopeIndex">Path index of the search scope: the window, or the nearest uniquely identifiable ancestor.</param>
    /// <param name="scopeAnchor">The step that finds that ancestor, or null for the window.</param>
    private List<Selector> ContentAnchoredSelectors(
        IElementTree tree, IReadOnlyList<ElementInfo> path, int scopeIndex, List<Candidate> targetCandidates,
        Candidate? scopeAnchor, MakeSelector make)
    {
        var selectors = new List<Selector>();
        var target = path[^1];
        var scope = path[scopeIndex];
        var snapshot = tree is TreeSnapshot;
        var lowest = snapshot ? scopeIndex + 1 : Math.Max(scopeIndex + 1, path.Count - 1 - MaxContentAnchorDepth);
        var maxLookAlikes = snapshot ? MaxSnapshotLookAlikeContainers : MaxLookAlikeContainers;
        // A position inside the nearest anchored container (the third item of the popup that says
        // "Edit Profile") still beats a position in the whole window, but only if nothing exact turns up.
        Selector? indexed = null;

        for (var i = path.Count - 2; i >= lowest; i--)
        {
            var container = path[i];
            if (string.IsNullOrEmpty(container.ControlType)) continue;
            var containerStep = ContainerStep(container);
            if (tree.FindAll(scope, containerStep, TreeScope.Descendants).Count > maxLookAlikes) continue;

            foreach (var landmark in Landmarks(tree, container, target, snapshot ? MaxSnapshotLandmarkScan : MaxLandmarkScan))
            {
                var anchorStep = containerStep with { Has = landmark.Step };
                if (Verify(tree.FindAll(scope, anchorStep, TreeScope.Descendants), container).Status != VerificationStatus.Unique) continue;

                IReadOnlyList<SelectorStep> Steps(SelectorStep targetStep) =>
                    scopeAnchor is { } a ? [a.Step, anchorStep, targetStep] : [anchorStep, targetStep];
                var where = $"{container.ControlType} containing \"{landmark.Text}\"" + (scopeAnchor is { } s ? $" inside {Describe(s.Step)}" : "");
                var warnings = scopeAnchor?.Warnings.Append(landmark.Warning) ?? [landmark.Warning];

                foreach (var candidate in targetCandidates)
                {
                    var matches = tree.FindAll(container, candidate.Step, TreeScope.Descendants);
                    var verification = Verify(matches, target);
                    if (verification.Status == VerificationStatus.Unique)
                    {
                        selectors.Add(make(Steps(candidate.Step), $"{candidate.Strategy} inside {where}",
                            Math.Min(candidate.Score, landmark.Score) - ContentAnchorPenalty,
                            candidate.Warnings.Concat(warnings), verification));
                        continue;
                    }

                    var index = matches.FindIndex(m => SameRuntimeId(m, target));
                    if (indexed is not null || index < 0) continue;
                    indexed = make(Steps(candidate.Step with { Index = index }), $"{candidate.Strategy} #{index + 1} inside {where}",
                        Math.Min(candidate.Score, landmark.Score) - ContentAnchorPenalty - AnchoredIndexPenalty,
                        candidate.Warnings.Concat(warnings)
                            .Append($"Uses position {index + 1} of {matches.Count} matches inside the {container.ControlType} containing \"{landmark.Text}\"; it breaks if items are added, removed or reordered there."),
                        new SelectorVerification(VerificationStatus.Unique, 1));
                }
                if (selectors.Count > 0) return selectors;
            }
        }
        if (indexed is not null) selectors.Add(indexed);
        return selectors;
    }

    private static SelectorStep ContainerStep(ElementInfo container)
    {
        var conditions = new List<SelectorCondition> { new(SelectorProperty.ControlType, container.ControlType) };
        var className = AttributeStability.AnalyzeClassName(container.ClassName, container.FrameworkId);
        if (className.IsUsable) conditions.Add(new(SelectorProperty.ClassName, className.Value));
        return new SelectorStep(conditions);
    }

    private sealed record Landmark(SelectorStep Step, string Text, int Score, string? Warning);

    /// <summary>
    /// Texts inside <paramref name="container"/> that might tell it apart from look-alikes: labels
    /// first, other named elements next. The target's own name is skipped (every look-alike has it).
    /// </summary>
    private static IEnumerable<Landmark> Landmarks(IElementTree tree, ElementInfo container, ElementInfo target, int maxScan)
    {
        var descendants = tree.Descendants(container, maxScan);
        if (descendants is null) yield break;
        var named = descendants
            .Where(d => !d.RuntimeId.AsSpan().SequenceEqual(target.RuntimeId.ToArray()))
            .Select(d => (Type: d.ControlType, d.Name))
            .Where(d => d.Type.Length > 0 && d.Name.Trim().Length > 0 && d.Name.Trim() != target.Name.Trim())
            .Distinct()
            .ToList();

        var ranked = named
            .Select(d => (d.Type, Verdict: AttributeStability.AnalyzeLandmarkText(d.Name)))
            .Where(d => d.Verdict.IsUsable)
            .OrderBy(d => d.Type == "Text" ? 0 : 1)
            .ThenBy(d => d.Verdict.Value.Length)
            .Take(MaxLandmarksTried);

        foreach (var (type, verdict) in ranked)
        {
            var step = new SelectorStep([new(SelectorProperty.ControlType, type), new(SelectorProperty.Name, verdict.Value, verdict.Match)]);
            yield return new Landmark(step, verdict.Value, verdict.Stability == Stability.Stable ? 80 : 65, verdict.Warning);
        }
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

    // ------------------------------------------------------------------ verification

    private static SelectorVerification Verify(List<int[]> matches, ElementInfo expected)
    {
        if (matches.Count == 0) return new(VerificationStatus.NotFound, 0);
        var containsExpected = matches.Any(m => SameRuntimeId(m, expected));
        if (!containsExpected) return new(VerificationStatus.WrongElement, matches.Count);
        return new(matches.Count == 1 ? VerificationStatus.Unique : VerificationStatus.Ambiguous, matches.Count);
    }

    private static bool SameRuntimeId(int[] runtimeId, ElementInfo element) =>
        element.RuntimeId.Count > 0 && runtimeId.AsSpan().SequenceEqual(element.RuntimeId.ToArray());

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

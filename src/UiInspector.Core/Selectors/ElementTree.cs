using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;

namespace UiInspector.Core.Selectors;

/// <summary>
/// Where selectors are searched and verified: the live UI, or a <see cref="TreeSnapshot"/> taken
/// the moment the user clicked (so a popup that closes because of the click still counts).
/// Scopes are given as elements from the inspected path; their RuntimeIds locate them.
/// </summary>
public interface IElementTree
{
    /// <summary>RuntimeIds of the elements under <paramref name="scope"/> that match <paramref name="step"/>, in tree order.</summary>
    List<int[]> FindAll(ElementInfo scope, SelectorStep step, TreeScope treeScope);

    /// <summary>Control type, name and RuntimeId of each descendant; null when there are more than <paramref name="max"/>.</summary>
    List<TreeElement>? Descendants(ElementInfo scope, int max);

    /// <summary>The siblings before <paramref name="element"/>, nearest first.</summary>
    List<TreeElement> PreviousSiblings(ElementInfo element, int max);
}

/// <summary>One element as the tree reports it.</summary>
public sealed record TreeElement(string ControlType, string Name, int[] RuntimeId);

/// <summary>The live UI, searched through UI Automation. UIA worker thread only.</summary>
public sealed class LiveTree(AutomationBase automation) : IElementTree
{
    private const int UIA_E_ELEMENTNOTAVAILABLE = unchecked((int)0x80040201);

    public List<int[]> FindAll(ElementInfo scope, SelectorStep step, TreeScope treeScope) =>
        scope.Ref is null ? [] : FindAll(scope.Ref.Element, step, treeScope);

    // A scope that's gone (a closed popup, a part of a page Chromium rebuilt) simply has no matches.
    private List<int[]> FindAll(AutomationElement scope, SelectorStep step, TreeScope treeScope)
    {
        try
        {
            return FindAllIn(scope, step, treeScope);
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == UIA_E_ELEMENTNOTAVAILABLE)
        {
            return [];
        }
    }

    private List<int[]> FindAllIn(AutomationElement scope, SelectorStep step, TreeScope treeScope)
    {
        var props = automation.PropertyLibrary.Element;
        var request = new CacheRequest
        {
            TreeScope = TreeScope.Element,
            // Checking what an element contains needs a live element to search inside.
            AutomationElementMode = step.Has is null ? AutomationElementMode.None : AutomationElementMode.Full,
        };
        request.Add(props.RuntimeId);
        request.Add(props.Name);
        request.Add(props.AutomationId);
        request.Add(props.ClassName);

        // Cached values are only read while the request is active, so everything happens inside it.
        using (request.Activate())
        {
            var found = scope.FindAll(treeScope, Condition(automation.ConditionFactory, step));
            var contains = step.Conditions.Where(c => c.Match == MatchKind.Contains).ToList();
            return found
                .Where(e => contains.All(c => (Read(e, c.Property) ?? "").Contains(c.Value, StringComparison.Ordinal)))
                .Where(e => step.Has is null || FindAll(e, step.Has, TreeScope.Descendants).Count > 0)
                .Select(e => e.Properties.RuntimeId.TryGetValue(out var id) ? id : [])
                .ToList();
        }
    }

    public List<TreeElement>? Descendants(ElementInfo scope, int max)
    {
        if (scope.Ref is null) return [];
        using (ElementsRequest().Activate())
        {
            var descendants = scope.Ref.Element.FindAllDescendants();
            return descendants.Length > max ? null : descendants.Select(Describe).ToList();
        }
    }

    public List<TreeElement> PreviousSiblings(ElementInfo element, int max)
    {
        var result = new List<TreeElement>();
        if (element.Ref is null) return result;
        // No cache request here: FlaUI's caching walker throws instead of returning null at the first sibling.
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        for (var sibling = walker.GetPreviousSibling(element.Ref.Element); sibling is not null && result.Count < max; sibling = walker.GetPreviousSibling(sibling))
        {
            result.Add(Describe(sibling));
        }
        return result;
    }

    private CacheRequest ElementsRequest()
    {
        var props = automation.PropertyLibrary.Element;
        var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
        request.Add(props.Name);
        request.Add(props.ControlType);
        request.Add(props.RuntimeId);
        return request;
    }

    private static TreeElement Describe(AutomationElement e) => new(
        e.Properties.ControlType.TryGetValue(out var t) ? t.ToString() : "",
        e.Properties.Name.ValueOrDefault ?? "",
        e.Properties.RuntimeId.ValueOrDefault ?? []);

    private static string? Read(AutomationElement e, SelectorProperty property) => property switch
    {
        SelectorProperty.Name => e.Properties.Name.ValueOrDefault,
        SelectorProperty.AutomationId => e.Properties.AutomationId.ValueOrDefault,
        SelectorProperty.ClassName => e.Properties.ClassName.ValueOrDefault,
        _ => null,
    };

    internal static ConditionBase Condition(ConditionFactory cf, SelectorStep step)
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
}

/// <summary>
/// The element tree of one window, copied in a single UI Automation call (with the other
/// top-level windows, for telling the window apart). Taken at the moment of a click, it lets the
/// selector be checked against the UI as the user saw it, even after the click closed a popup.
/// A large app (Discord: ~800 elements) takes about 65 ms.
/// </summary>
public sealed class TreeSnapshot : IElementTree
{
    private const int MaxElements = 50_000;

    private sealed class Node
    {
        public required TreeElement Info { get; init; }
        public string AutomationId { get; init; } = "";
        public string ClassName { get; init; } = "";
        public Node? Parent { get; set; }
        public List<Node> Children { get; } = [];
    }

    private readonly Dictionary<string, Node> _byRuntimeId = [];
    private readonly Node _desktop;

    private TreeSnapshot(Node desktop) => _desktop = desktop;

    public int Count => _byRuntimeId.Count;

    /// <summary>Copies the tree of <c>path[1]</c> (the top-level window) and lists the desktop's windows. UIA worker thread.</summary>
    public static TreeSnapshot? Capture(AutomationBase automation, IReadOnlyList<ElementInfo> path)
    {
        if (path.Count < 2 || path[0].Ref is null || path[1].Ref is null) return null;
        var p = automation.PropertyLibrary.Element;
        CacheRequest Request(TreeScope scope)
        {
            var request = new CacheRequest
            {
                TreeScope = scope,
                AutomationElementMode = AutomationElementMode.None,
                TreeFilter = new PropertyCondition(p.IsControlElement, true), // the control view, as the walker and FindAll use
            };
            foreach (var id in new[] { p.Name, p.AutomationId, p.ControlType, p.ClassName, p.RuntimeId }) request.Add(id);
            return request;
        }

        // Cached values can only be read while their request is active, so each copy happens inside it.
        TreeSnapshot? snapshot = null;
        Node? windowNode;
        using (Request(TreeScope.Subtree).Activate())
        {
            var window = path[1].Ref!.Element.FrameworkAutomationElement.GetUpdatedCache();
            if (window is null) return null;
            snapshot = new TreeSnapshot(new Node { Info = new TreeElement("Pane", "Desktop", [.. path[0].RuntimeId]) });
            windowNode = snapshot.Copy(window);
            if (windowNode is null) return null;
        }

        using (Request(TreeScope.Element | TreeScope.Children).Activate())
        {
            var desktop = path[0].Ref!.Element.FrameworkAutomationElement.GetUpdatedCache();
            if (desktop is null) return null;
            snapshot.Add(snapshot._desktop);
            foreach (var child in desktop.CachedChildren)
            {
                // The clicked window gets its whole tree; the others only matter as look-alikes of it.
                var node = Key(child.Properties.RuntimeId.ValueOrDefault ?? []) == Key(windowNode.Info.RuntimeId) ? windowNode : Make(child);
                node.Parent = snapshot._desktop;
                snapshot._desktop.Children.Add(node);
                snapshot.Add(node);
            }
        }
        return snapshot;
    }

    private Node? Copy(AutomationElement element)
    {
        var node = Make(element);
        foreach (var child in element.CachedChildren)
        {
            if (_byRuntimeId.Count > MaxElements) return null;
            var copy = Copy(child);
            if (copy is null) return null;
            copy.Parent = node;
            node.Children.Add(copy);
            Add(copy);
        }
        return node;
    }

    private static Node Make(AutomationElement e) => new()
    {
        Info = new TreeElement(
            e.Properties.ControlType.TryGetValue(out var t) ? t.ToString() : "",
            e.Properties.Name.ValueOrDefault ?? "",
            e.Properties.RuntimeId.ValueOrDefault ?? []),
        AutomationId = e.Properties.AutomationId.ValueOrDefault ?? "",
        ClassName = e.Properties.ClassName.ValueOrDefault ?? "",
    };

    private void Add(Node node) => _byRuntimeId.TryAdd(Key(node.Info.RuntimeId), node);

    private static string Key(IReadOnlyList<int> runtimeId) => string.Join('.', runtimeId);

    private Node? Find(ElementInfo element) =>
        element.RuntimeId.Count == 0 ? null : _byRuntimeId.GetValueOrDefault(Key(element.RuntimeId));

    /// <summary>True if the snapshot has this element (the inspected path should all be in it).</summary>
    public bool Contains(ElementInfo element) => Find(element) is not null;

    public List<int[]> FindAll(ElementInfo scope, SelectorStep step, TreeScope treeScope) =>
        Find(scope) is { } node ? FindAll(node, step, treeScope).Select(n => n.Info.RuntimeId).ToList() : [];

    private static IEnumerable<Node> FindAll(Node scope, SelectorStep step, TreeScope treeScope)
    {
        var candidates = treeScope == TreeScope.Children ? scope.Children : Below(scope);
        if (step.Has is null) return candidates.Where(n => Matches(n, step));

        // Everything with a match somewhere below it, found in one pass instead of a search under each candidate.
        var containing = new HashSet<Node>();
        foreach (var match in FindAll(scope, step.Has, TreeScope.Descendants))
        {
            for (var p = match.Parent; p is not null && p != scope && containing.Add(p); p = p.Parent) { }
        }
        return candidates.Where(n => containing.Contains(n) && Matches(n, step));
    }

    public List<TreeElement>? Descendants(ElementInfo scope, int max)
    {
        if (Find(scope) is not { } node) return [];
        var all = Below(node).Take(max + 1).Select(n => n.Info).ToList();
        return all.Count > max ? null : all;
    }

    public List<TreeElement> PreviousSiblings(ElementInfo element, int max)
    {
        if (Find(element) is not { Parent: { } parent } node) return [];
        var index = parent.Children.IndexOf(node);
        return Enumerable.Range(0, index).Reverse().Select(i => parent.Children[i].Info).Take(max).ToList();
    }

    // Pre-order, as UI Automation returns descendants.
    private static IEnumerable<Node> Below(Node node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var descendant in Below(child)) yield return descendant;
        }
    }

    private static bool Matches(Node node, SelectorStep step) => step.Conditions.All(c =>
    {
        var value = c.Property switch
        {
            SelectorProperty.ControlType => node.Info.ControlType,
            SelectorProperty.AutomationId => node.AutomationId,
            SelectorProperty.Name => node.Info.Name,
            SelectorProperty.ClassName => node.ClassName,
            _ => "",
        };
        return c.Match == MatchKind.Exact ? value == c.Value : value.Contains(c.Value, StringComparison.Ordinal);
    });
}

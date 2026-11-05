using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;

namespace UiInspector.Core;

/// <summary>
/// Reads an element and its ancestors from UI Automation. All methods must run on the
/// <see cref="UiaWorker"/> thread that owns <paramref name="automation"/>.
/// </summary>
public sealed class ElementInspector
{
    private const int MaxAncestorDepth = 64;
    private const string PatternSuffix = "Pattern";

    private readonly ConcurrentDictionary<int, string> _processNames = new();

    public InspectionResult? InspectAtPoint(AutomationBase automation, Point screenPoint, bool includeAncestors = true)
    {
        return Inspect(automation, includeAncestors, InspectionSource.Point, screenPoint,
            () => automation.FromPoint(screenPoint));
    }

    public InspectionResult? InspectFocused(AutomationBase automation, bool includeAncestors = true)
    {
        return Inspect(automation, includeAncestors, InspectionSource.Focus, null,
            automation.FocusedElement);
    }

    /// <summary>Reads the desktop root element, the starting point for browsing all windows.</summary>
    public ElementInfo ReadDesktop(AutomationBase automation)
    {
        var patterns = GetCacheablePatterns(automation);
        using (CreateCacheRequest(automation, patterns).Activate())
        {
            var desktop = automation.GetDesktop().FrameworkAutomationElement.GetUpdatedCache()
                          ?? throw new InvalidOperationException("UI Automation returned no desktop element.");
            return ReadInfo(desktop, patterns);
        }
    }

    /// <summary>Inspects an element found by searching the tree (used by tests).</summary>
    internal InspectionResult? InspectElement(AutomationBase automation, AutomationElement element)
    {
        // Re-fetch under the active cache request, since `element` was found without it.
        return Inspect(automation, includeAncestors: true, InspectionSource.Search, null,
            () => element.FrameworkAutomationElement.GetUpdatedCache());
    }

    private InspectionResult? Inspect(
        AutomationBase automation,
        bool includeAncestors,
        InspectionSource source,
        Point? screenPoint,
        Func<AutomationElement?> find)
    {
        var stopwatch = Stopwatch.StartNew();
        var patterns = GetCacheablePatterns(automation);

        // One cache request fetches every property we display in a single cross-process
        // round trip per element, instead of one call per property.
        using (CreateCacheRequest(automation, patterns).Activate())
        {
            var element = find();
            if (element is null) return null;

            var info = ReadInfo(element, patterns);
            var walker = automation.TreeWalkerFactory.GetControlViewWalker();
            var webContentPending = IsChromiumPageRoot(info) && walker.GetFirstChild(element) is null;
            var ancestors = new List<ElementInfo>();
            var truncated = false;

            if (includeAncestors)
            {
                var parent = walker.GetParent(element);
                while (parent is not null)
                {
                    if (ancestors.Count == MaxAncestorDepth)
                    {
                        truncated = true;
                        break;
                    }
                    ancestors.Add(ReadInfo(parent, patterns));
                    parent = walker.GetParent(parent);
                }
            }

            return new InspectionResult
            {
                Element = info,
                Ancestors = ancestors,
                AncestorsTruncated = truncated,
                WebContentPending = webContentPending,
                Source = source,
                ScreenPoint = screenPoint,
                Duration = stopwatch.Elapsed,
            };
        }
    }

    /// <summary>
    /// Reads the control-view children of <paramref name="parent"/>, up to <paramref name="maxChildren"/>.
    /// The desktop has one child per top-level window; grids can have thousands, hence the cap.
    /// </summary>
    public ChildrenResult GetChildren(AutomationBase automation, ElementRef parent, int maxChildren = 500)
    {
        var patterns = GetCacheablePatterns(automation);
        using (CreateCacheRequest(automation, patterns).Activate())
        {
            var walker = automation.TreeWalkerFactory.GetControlViewWalker();
            var children = new List<ElementInfo>();
            var child = walker.GetFirstChild(parent.Element);
            while (child is not null)
            {
                if (children.Count == maxChildren) return new ChildrenResult(children, Truncated: true);
                children.Add(ReadInfo(child, patterns));
                child = walker.GetNextSibling(child);
            }
            return new ChildrenResult(children, Truncated: false);
        }
    }

    // Chromium-based hosts (Chrome, Edge, Electron, WebView2, CEF) report FrameworkId "Chrome"
    // and expose each page as a Document with AutomationId "RootWebArea".
    private static bool IsChromiumPageRoot(ElementInfo info) =>
        info.FrameworkId == "Chrome"
        && info.ControlType == nameof(ControlType.Document)
        && info.AutomationId == "RootWebArea";

    internal static CacheablePattern[] GetCacheablePatterns(AutomationBase automation) =>
        automation.PatternLibrary.AllForCurrentFramework
            .Where(p => p != PatternId.NotSupportedByFramework)
            .Select(p => p.AvailabilityProperty is { } availability && availability != PropertyId.NotSupportedByFramework
                ? new CacheablePattern(TrimPatternName(p.Name), availability)
                : null)
            .OfType<CacheablePattern>()
            .ToArray();

    internal static CacheRequest CreateCacheRequest(AutomationBase automation, CacheablePattern[] patterns)
    {
        var props = automation.PropertyLibrary.Element;
        var request = new CacheRequest
        {
            TreeScope = TreeScope.Element,
            AutomationElementMode = AutomationElementMode.Full,
        };

        PropertyId[] wanted =
        [
            props.Name, props.AutomationId, props.ControlType, props.LocalizedControlType,
            props.ClassName, props.FrameworkId, props.AriaRole, props.HelpText, props.ProcessId,
            props.BoundingRectangle, props.IsOffscreen, props.IsEnabled, props.IsKeyboardFocusable,
            props.HasKeyboardFocus, props.IsPassword, props.NativeWindowHandle, props.RuntimeId,
        ];
        foreach (var id in wanted) request.Add(id);
        foreach (var pattern in patterns) request.Add(pattern.Availability);
        return request;
    }

    internal ElementInfo ReadInfo(AutomationElement element, CacheablePattern[] patterns)
    {
        var p = element.Properties;
        var processId = Get(p.ProcessId);

        return new ElementInfo
        {
            Name = Get(p.Name) ?? "",
            AutomationId = Get(p.AutomationId) ?? "",
            ControlType = p.ControlType.TryGetValue(out var controlType) ? controlType.ToString() : "",
            LocalizedControlType = Get(p.LocalizedControlType) ?? "",
            ClassName = Get(p.ClassName) ?? "",
            FrameworkId = Get(p.FrameworkId) ?? "",
            AriaRole = Get(p.AriaRole) ?? "",
            HelpText = Get(p.HelpText) ?? "",
            ProcessId = processId,
            ProcessName = GetProcessName(processId),
            BoundingRectangle = Get(p.BoundingRectangle),
            IsOffscreen = Get(p.IsOffscreen),
            IsEnabled = Get(p.IsEnabled),
            IsKeyboardFocusable = Get(p.IsKeyboardFocusable),
            HasKeyboardFocus = Get(p.HasKeyboardFocus),
            IsPassword = Get(p.IsPassword),
            NativeWindowHandle = Get(p.NativeWindowHandle),
            RuntimeId = Get(p.RuntimeId) ?? [],
            Patterns = patterns
                .Where(pattern => element.FrameworkAutomationElement
                    .TryGetPropertyValue<bool>(pattern.Availability, out var available) && available)
                .Select(pattern => pattern.Name)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            Ref = new ElementRef(element),
        };
    }

    private static T? Get<T>(AutomationProperty<T> property) =>
        property.TryGetValue(out var value) ? value : default;

    private static string TrimPatternName(string name) =>
        name.EndsWith(PatternSuffix, StringComparison.Ordinal) ? name[..^PatternSuffix.Length] : name;

    private string GetProcessName(int processId)
    {
        if (processId <= 0) return "";
        return _processNames.GetOrAdd(processId, static id =>
        {
            try
            {
                using var process = Process.GetProcessById(id);
                return process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return "?";
            }
        });
    }

    internal sealed record CacheablePattern(string Name, PropertyId Availability);
}

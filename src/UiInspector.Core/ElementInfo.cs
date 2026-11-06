using System.Drawing;

namespace UiInspector.Core;

/// <summary>
/// A snapshot of one UI Automation element's properties. Values are copied out of UIA,
/// so the snapshot stays readable after the element (or its app) is gone.
/// </summary>
public sealed record ElementInfo
{
    public string Name { get; init; } = "";
    public string AutomationId { get; init; } = "";
    public string ControlType { get; init; } = "";
    public string LocalizedControlType { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string FrameworkId { get; init; } = "";
    public string AriaRole { get; init; } = "";
    public string HelpText { get; init; } = "";
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public Rectangle BoundingRectangle { get; init; }
    public bool IsOffscreen { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsKeyboardFocusable { get; init; }
    public bool HasKeyboardFocus { get; init; }
    public bool IsPassword { get; init; }
    public IntPtr NativeWindowHandle { get; init; }

    /// <summary>Session-only identity. Changes when the app restarts; never use it in selectors.</summary>
    public IReadOnlyList<int> RuntimeId { get; init; } = [];

    /// <summary>UIA control patterns the element supports, e.g. "Invoke", "Value", "Toggle".</summary>
    public IReadOnlyList<string> Patterns { get; init; } = [];

    /// <summary>
    /// Handle to the live UIA element, for follow-up queries such as loading children.
    /// Null for snapshots that weren't read from UIA (e.g. in tests).
    /// </summary>
    public ElementRef? Ref { get; init; }

    public bool IsSameElement(ElementInfo? other) =>
        other is not null && RuntimeId.Count > 0 && RuntimeId.SequenceEqual(other.RuntimeId);
}

/// <summary>
/// Opaque handle to a live UIA element. UIA elements are free-threaded COM objects, so a handle
/// stays usable on any <see cref="UiaWorker"/> thread, including one that replaced a hung worker.
/// It goes stale when the element disappears; queries on it then fail with ElementNotAvailable.
/// </summary>
public sealed class ElementRef
{
    internal ElementRef(FlaUI.Core.AutomationElements.AutomationElement element) => Element = element;

    internal FlaUI.Core.AutomationElements.AutomationElement Element { get; }
}

/// <summary>An element with the elements below it, as read in one go.</summary>
public sealed record ElementNode(ElementInfo Info, IReadOnlyList<ElementNode> Children);

public sealed record ChildrenResult(IReadOnlyList<ElementInfo> Children, bool Truncated);

public enum InspectionSource
{
    Point,
    Focus,

    /// <summary>Found by searching the tree (tests, selector checks), not by position or focus.</summary>
    Search,
}

/// <summary>The result of one inspection: the element plus its ancestor chain.</summary>
public sealed record InspectionResult
{
    public required ElementInfo Element { get; init; }

    /// <summary>Ancestors in the control view, nearest parent first, desktop root last.</summary>
    public IReadOnlyList<ElementInfo> Ancestors { get; init; } = [];

    public required InspectionSource Source { get; init; }

    /// <summary>Screen point that was hit-tested, in physical pixels (only for <see cref="InspectionSource.Point"/>).</summary>
    public Point? ScreenPoint { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>True when the ancestor walk stopped at the depth limit before reaching the desktop.</summary>
    public bool AncestorsTruncated { get; init; }

    /// <summary>
    /// True when the hit is an empty Chromium/Electron/WebView2 page root. Chromium builds its
    /// accessibility tree lazily, after the first UIA client asks, so the page content usually
    /// appears a moment later.
    /// </summary>
    public bool WebContentPending { get; init; }
}

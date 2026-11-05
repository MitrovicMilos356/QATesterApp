namespace UiInspector.Core;

/// <summary>
/// Picks the element a test would actually interact with. Hit-testing returns the deepest
/// element under the cursor, which is often a label or icon inside the real control
/// (e.g. the status Text inside a Discord chat Hyperlink). This walks up a few levels to the
/// nearest interactive ancestor, without crossing a window or page boundary.
/// </summary>
public static class SmartTarget
{
    public const int MaxClimb = 6;

    private static readonly HashSet<string> InteractiveControlTypes =
    [
        "Button", "CheckBox", "ComboBox", "Edit", "Hyperlink", "ListItem", "MenuItem", "RadioButton",
        "Slider", "Spinner", "SplitButton", "TabItem", "TreeItem", "DataItem",
    ];

    // Patterns that mean "you can act on this". LegacyIAccessible is on almost everything, so it doesn't count.
    private static readonly HashSet<string> ActionPatterns =
    [
        "Invoke", "Toggle", "SelectionItem", "ExpandCollapse", "Value", "RangeValue",
    ];

    // Climbing past these would select a container, not a control.
    private static readonly HashSet<string> BoundaryControlTypes =
    [
        "Window", "Document", "Pane", "List", "Tree", "Table", "DataGrid", "Menu", "MenuBar", "ToolBar", "Tab",
    ];

    /// <summary>
    /// Index into the element chain: 0 is the hit element itself, n is <c>Ancestors[n - 1]</c>.
    /// Returns 0 when the hit element is already interactive or no interactive ancestor is near.
    /// </summary>
    public static int FindIndex(InspectionResult result)
    {
        if (IsInteractive(result.Element)) return 0;

        var limit = Math.Min(MaxClimb, result.Ancestors.Count);
        for (var i = 0; i < limit; i++)
        {
            var ancestor = result.Ancestors[i];
            if (BoundaryControlTypes.Contains(ancestor.ControlType)) break;
            if (IsInteractive(ancestor)) return i + 1;
        }
        return 0;
    }

    /// <summary>The element at <see cref="FindIndex"/>.</summary>
    public static ElementInfo Find(InspectionResult result)
    {
        var index = FindIndex(result);
        return index == 0 ? result.Element : result.Ancestors[index - 1];
    }

    public static bool IsInteractive(ElementInfo element) =>
        !BoundaryControlTypes.Contains(element.ControlType)
        && (InteractiveControlTypes.Contains(element.ControlType) || element.Patterns.Any(ActionPatterns.Contains));
}

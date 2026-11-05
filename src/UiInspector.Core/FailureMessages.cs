using System.Drawing;
using FlaUI.Core.Exceptions;

namespace UiInspector.Core;

/// <summary>Turns UIA failures into messages a user can act on.</summary>
public static class FailureMessages
{
    /// <param name="point">Hit-tested point, or null when the focused element was inspected.</param>
    public static string Describe(Exception ex, Point? point)
    {
        var target = point is null ? "focused window" : "window under the cursor";

        if (ex is UiaTimeoutException)
            return ex.Message;

        var hung = point is { } p ? WindowResponsiveness.IsWindowAtPointHung(p) : WindowResponsiveness.IsForegroundWindowHung();
        if (hung)
            return $"The {target} is not responding (hung), so it can't report its UI. Try again once it recovers.";

        if (ex is ElementNotAvailableException)
            return "The element disappeared while it was being read (e.g. a menu or tooltip closed). Try the delayed capture.";

        return $"Inspection failed: {ex.Message}";
    }
}

using UiInspector.Core;

namespace UiInspector.Core.Tests;

public class SmartTargetTests
{
    private static ElementInfo E(string controlType, string name = "", params string[] patterns) =>
        new() { ControlType = controlType, Name = name, Patterns = patterns };

    private static InspectionResult Chain(ElementInfo element, params ElementInfo[] ancestorsNearestFirst) => new()
    {
        Element = element,
        Ancestors = ancestorsNearestFirst,
        Source = InspectionSource.Point,
    };

    [Fact]
    public void DiscordStatusText_SelectsTheChatHyperlink()
    {
        // Real chain captured from Discord, nearest ancestor first.
        var result = Chain(
            E("Text", "Dungeon crawling | Use /raid"),
            E("Group", "Playing Dungeon crawling | Use /raid"),
            E("Group"),
            E("Hyperlink", "Dyno (direct message), Online", "Invoke", "LegacyIAccessible"),
            E("Group"),
            E("ListItem", "DynoDungeon crawling | Use /raid"),
            E("List", "Direct Messages"));

        Assert.Equal(3, SmartTarget.FindIndex(result));
        Assert.Equal("Hyperlink", SmartTarget.Find(result).ControlType);
    }

    [Fact]
    public void InteractiveHit_StaysOnTheHitElement()
    {
        var result = Chain(E("Button", "Seven", "Invoke"), E("Group", "Number pad"), E("Window", "Calculator"));

        Assert.Equal(0, SmartTarget.FindIndex(result));
    }

    [Fact]
    public void TextInsideButton_SelectsTheButton()
    {
        var result = Chain(E("Text", "OK"), E("Button", "OK"), E("Window"));

        Assert.Equal(1, SmartTarget.FindIndex(result));
    }

    [Fact]
    public void ActionPatternCountsEvenOnUnusualControlType()
    {
        var result = Chain(E("Image"), E("Custom", "Star rating", "RangeValue"), E("Pane"));

        Assert.Equal(1, SmartTarget.FindIndex(result));
    }

    [Fact]
    public void LegacyIAccessibleAloneIsNotInteractive()
    {
        var result = Chain(E("Text", "Label", "LegacyIAccessible"), E("Group", "", "LegacyIAccessible"), E("Window"));

        Assert.Equal(0, SmartTarget.FindIndex(result));
    }

    [Fact]
    public void StopsAtContainerBoundary()
    {
        // The Button lies beyond the Pane, so it belongs to some other region; don't jump there.
        var result = Chain(E("Text", "Caption"), E("Pane"), E("Button", "Far away", "Invoke"));

        Assert.Equal(0, SmartTarget.FindIndex(result));
    }

    [Fact]
    public void NotesDocumentWithValuePatternIsNotATarget()
    {
        // Notepad's editor is a Document with the Value pattern; documents are boundaries, not controls.
        Assert.False(SmartTarget.IsInteractive(E("Document", "Text Editor", "Value", "Text")));
    }

    [Fact]
    public void StopsAfterMaxClimb()
    {
        var groups = Enumerable.Repeat(E("Group"), SmartTarget.MaxClimb).ToList();
        groups.Add(E("Button", "Too far", "Invoke"));
        var result = Chain(E("Text"), [.. groups]);

        Assert.Equal(0, SmartTarget.FindIndex(result));
    }
}

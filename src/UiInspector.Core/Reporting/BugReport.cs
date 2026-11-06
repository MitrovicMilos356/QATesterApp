using UiInspector.Core.Recording;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Reporting;

public enum Severity
{
    None,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>What goes into a bug report. Every part can be switched off.</summary>
public sealed record BugReportOptions
{
    public bool Steps { get; init; } = true;
    public bool Screenshots { get; init; } = true;

    /// <summary>Outline the element each step acted on.</summary>
    public bool HighlightElement { get; init; } = true;

    public bool ExpectedActual { get; init; } = true;
    public bool Severity { get; init; } = true;
    public bool Notes { get; init; } = true;

    public bool Environment { get; init; } = true;
    public bool WindowsVersion { get; init; } = true;
    public bool AppVersion { get; init; } = true;
    public bool AppPath { get; init; }
    public bool Screens { get; init; } = true;
    public bool Language { get; init; } = true;
    public bool DateTime { get; init; } = true;

    /// <summary>The script that replays the steps, in <see cref="ScriptLanguage"/>.</summary>
    public bool Script { get; init; }
    public string ScriptLanguage { get; init; } = "flaui";

    /// <summary>The selector of each step's element, for the developer.</summary>
    public bool ElementDetails { get; init; }

    /// <summary>Ids of steps whose screenshot is left out (e.g. it shows private data).</summary>
    public IReadOnlySet<int> HiddenScreenshots { get; init; } = new HashSet<int>();
}

/// <summary>A bug report: what the tester writes, plus what the recording provides.</summary>
public sealed record BugReport
{
    public string Title { get; init; } = "";
    public Severity Severity { get; init; }
    public string Expected { get; init; } = "";
    public string Actual { get; init; } = "";
    public string Notes { get; init; } = "";

    public IReadOnlyList<RecordedStep> Steps { get; init; } = [];

    public EnvironmentInfo? Environment { get; init; }

    public IReadOnlyList<CodeSnippet> Scripts { get; init; } = [];
}

/// <summary>Steps in the words a manual tester would use ("Click the “Save” button").</summary>
public static class StepWording
{
    public static string Describe(RecordedStep step)
    {
        var target = Element(step.Target);
        return step.Action switch
        {
            RecordedAction.Click => $"Click {target}.",
            RecordedAction.DoubleClick => $"Double-click {target}.",
            RecordedAction.RightClick => $"Right-click {target}.",
            RecordedAction.SetValue or RecordedAction.TypeText => step.IsSecret
                ? $"Type the password into {target}."
                : $"Type “{CodeTemplates.OneLine(step.Text ?? "")}” into {target}.",
            RecordedAction.PressKey => $"Press {step.Key}{(step.Repeat > 1 ? $" {step.Repeat} times" : "")}" +
                                       (step.Target.ControlType is "Edit" or "Document" or "ComboBox" or "List" or "Tree" or "DataGrid" ? $" in {target}." : "."),
            RecordedAction.CheckExists => $"Check that {target} is shown.",
            RecordedAction.CheckText => $"Check that {target} shows “{CodeTemplates.OneLine(step.Text ?? "")}”.",
            _ => step.Describe(),
        };
    }

    /// <summary>"the “Save” button", "the “Email” field", or "a button" when it has no name.</summary>
    public static string Element(ElementInfo element)
    {
        var kind = Kind(element);
        var name = CodeTemplates.OneLine(element.Name.Trim());
        if (name.Length > 60) name = name[..57] + "…";
        return name.Length > 0 ? $"the “{name}” {kind}" : $"the {kind}";
    }

    /// <summary>"In the “Orders - App” window (app)", for steps that move to another window.</summary>
    public static string Window(RecordedStep step)
    {
        var window = step.Path.Count > 1 ? step.Path[1] : step.Target;
        var title = string.IsNullOrWhiteSpace(window.Name) ? "the app's window" : $"the “{CodeTemplates.OneLine(window.Name.Trim())}” window";
        var app = string.IsNullOrEmpty(step.Target.ProcessName) ? "" : $" ({step.Target.ProcessName})";
        return $"In {title}{app}:";
    }

    private static string Kind(ElementInfo element) => element.ControlType switch
    {
        "Button" or "SplitButton" => "button",
        "Hyperlink" => "link",
        "Edit" => "field",
        "Document" => "editor",
        "CheckBox" => "check box",
        "RadioButton" => "option",
        "ComboBox" => "drop-down",
        "ListItem" => "list item",
        "TreeItem" => "tree item",
        "TabItem" => "tab",
        "MenuItem" => "menu item",
        "Text" => "text",
        "Image" => "image",
        "DataItem" => "row",
        "Window" => "window",
        "Pane" => "panel",
        "" => "element",
        var other => string.IsNullOrEmpty(element.LocalizedControlType) ? other.ToLowerInvariant() : element.LocalizedControlType,
    };
}

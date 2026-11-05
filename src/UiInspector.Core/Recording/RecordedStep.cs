using UiInspector.Core.Selectors;

namespace UiInspector.Core.Recording;

public enum RecordedAction
{
    /// <summary>A left click. Scripts use the element's pattern action (Invoke, Toggle, Select…) where it has one.</summary>
    Click,
    DoubleClick,
    RightClick,

    /// <summary>Text typed into a field that supports setting its value directly.</summary>
    SetValue,

    /// <summary>Text typed into an editor without a settable value (rich text, web editors): typed as keys.</summary>
    TypeText,
    PressKey,

    /// <summary>A check added with the Add check hotkey: the element must exist.</summary>
    CheckExists,

    /// <summary>A check added with the Add check hotkey: the element's text must equal <see cref="RecordedStep.Text"/>.</summary>
    CheckText,
}

/// <summary>Which text a <see cref="RecordedAction.CheckText"/> step compares.</summary>
public enum TextSource
{
    /// <summary>The Value pattern's value (text fields).</summary>
    Value,

    /// <summary>The element's Name (labels).</summary>
    Name,
}

/// <summary>
/// One recorded action. The selector is generated after the step is recorded, so it's null
/// for a moment; <see cref="Fallback"/> holds an unverified selector when the element was
/// gone before it could be checked.
/// </summary>
public sealed record RecordedStep
{
    /// <summary>Stays the same when the step is updated (e.g. a click that turned out to be a double-click).</summary>
    public required int Id { get; init; }

    public required RecordedAction Action { get; init; }

    /// <summary>Desktop first, the element last.</summary>
    public required IReadOnlyList<ElementInfo> Path { get; init; }

    public ElementInfo Target => Path[^1];

    /// <summary>Typed text (SetValue, TypeText) or expected text (CheckText). Null for secrets.</summary>
    public string? Text { get; init; }

    /// <summary>A password field: the script reads the text from an environment variable instead.</summary>
    public bool IsSecret { get; init; }

    public KeyChord? Key { get; init; }

    /// <summary>How many times <see cref="Key"/> was pressed in a row.</summary>
    public int Repeat { get; init; } = 1;

    public TextSource CheckSource { get; init; }

    public SelectorResult? Selector { get; init; }

    public Selector? Fallback { get; init; }

    /// <summary>Why no verified selector was found, if so.</summary>
    public string? Problem { get; init; }

    public bool IsGenerating => Selector is null && Fallback is null && Problem is null;

    /// <summary>The selector the script uses: the verified one if there is one, else the unverified fallback.</summary>
    public Selector? ChosenSelector => Selector?.Best ?? Fallback;

    public string Describe()
    {
        var target = CodeTemplates.Describe(Target);
        return Action switch
        {
            RecordedAction.Click => $"Click {target}",
            RecordedAction.DoubleClick => $"Double-click {target}",
            RecordedAction.RightClick => $"Right-click {target}",
            RecordedAction.SetValue or RecordedAction.TypeText => IsSecret
                ? $"Type a password into {target}"
                : $"Type \"{Shorten(Text ?? "")}\" into {target}",
            RecordedAction.PressKey => $"Press {Key}{(Repeat > 1 ? $" ×{Repeat}" : "")} in {target}",
            RecordedAction.CheckExists => $"Check that {target} exists",
            RecordedAction.CheckText => $"Check that {target} shows \"{Shorten(Text ?? "")}\"",
            _ => Action.ToString(),
        };
    }

    private static string Shorten(string text)
    {
        var line = CodeTemplates.OneLine(text);
        return line.Length <= 60 ? line : line[..57] + "…";
    }
}

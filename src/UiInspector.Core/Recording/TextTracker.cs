namespace UiInspector.Core.Recording;

/// <summary>A text field as seen at one moment: where it is, and its text (null if unreadable or a password).</summary>
public sealed record FieldState(IReadOnlyList<ElementInfo> Path, string? Text, bool IsSecret, bool CanSetValue)
{
    public ElementInfo Element => Path[^1];
}

/// <summary>Text a field ended up with after the user typed into it.</summary>
public sealed record TextEntry(FieldState Field, string? Text);

/// <summary>
/// Works out what was typed where, without ever seeing the keystrokes. It remembers the text
/// field that has focus and its text at that moment; whenever something happens (a click, a
/// key step, focus moving on, recording stopping), it's told the field's text now and reports
/// the field if the text changed. Password fields can't be read, so for them it relies on the
/// hook's "something was typed" signal. Pure logic: UIA reads happen in the caller.
/// </summary>
public sealed class TextTracker
{
    private string? _baseline;
    private bool _typed;

    /// <summary>The text field that has focus, as far as the tracker knows; null if focus isn't in one.</summary>
    public FieldState? Field { get; private set; }

    /// <summary>A printable key was pressed (where, isn't known).</summary>
    public void Typed() => _typed = true;

    /// <param name="focusedNow">The text field that has focus now; null if focus is somewhere else.</param>
    /// <param name="trackedText"><see cref="Field"/>'s text now, read just before; null if unreadable or gone.</param>
    /// <returns>Fields whose text changed, in the order the changes happened.</returns>
    public IReadOnlyList<TextEntry> Update(FieldState? focusedNow, string? trackedText)
    {
        var entries = new List<TextEntry>();
        var trackedChanged = false;

        if (Field is { } field)
        {
            trackedChanged = field.IsSecret
                ? _typed
                : trackedText is not null && (_baseline is null ? _typed : trackedText != _baseline);
            if (trackedChanged)
            {
                entries.Add(new TextEntry(field, field.IsSecret ? null : trackedText));
                _baseline = trackedText;
            }
        }

        var sameField = focusedNow is not null && Field is not null && focusedNow.Element.IsSameElement(Field.Element);
        if (!sameField)
        {
            // Typing that didn't change the tracked field went to the field that has focus now
            // (focus moved without a focus event, which some apps don't raise).
            if (_typed && !trackedChanged && focusedNow is not null && (focusedNow.IsSecret || !string.IsNullOrEmpty(focusedNow.Text)))
            {
                entries.Add(new TextEntry(focusedNow, focusedNow.IsSecret ? null : focusedNow.Text));
            }
            Field = focusedNow;
            _baseline = focusedNow?.Text;
        }

        _typed = false;
        return entries;
    }

    public void Reset()
    {
        Field = null;
        _baseline = null;
        _typed = false;
    }
}

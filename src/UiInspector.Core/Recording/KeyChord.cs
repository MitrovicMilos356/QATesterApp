namespace UiInspector.Core.Recording;

/// <summary>A key with the modifiers held at the time, e.g. Ctrl+S or Enter.</summary>
public sealed record KeyChord(int VirtualKey, bool Ctrl = false, bool Alt = false, bool Shift = false, bool Win = false)
{
    /// <summary>Canonical key name ("S", "Enter", "F5", "PageDown"), or null for keys scripts can't name.</summary>
    public string? KeyName => NameOf(VirtualKey);

    public bool HasCommandModifier => Ctrl || Alt || Win;

    /// <summary>Readable form, e.g. "Ctrl+Shift+S".</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName ?? $"VK 0x{VirtualKey:X2}");
        return string.Join("+", parts);
    }

    public static string? NameOf(int vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Escape",
        0x20 => "Space",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => null,
    };
}

/// <summary>What the recorder does with a key press.</summary>
public enum KeyKind
{
    /// <summary>Not recorded at all: modifiers on their own, Tab (focus moves are tracked anyway), the inspector's own hotkeys.</summary>
    Ignore,

    /// <summary>Text entry. Only the fact that something was typed is noted, never which key.</summary>
    Typing,

    /// <summary>Recorded as a "press key" step: Enter, Escape, F-keys, shortcuts like Ctrl+S.</summary>
    Step,

    /// <summary>Editing or navigation keys (arrows, Delete, Space…): typing inside a text field, a step anywhere else.</summary>
    StepUnlessTextField,
}

/// <summary>
/// Decides, inside the keyboard hook, what a key press means. Printable keys are reduced to
/// <see cref="KeyKind.Typing"/> right there, so typed text never leaves the hook: the recorder
/// reads what a field contains from UI Automation instead (and never reads password fields).
/// </summary>
public static class KeyClassifier
{
    private const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SPACE = 0x20;
    private const int VK_PRIOR = 0x21, VK_DOWN = 0x28, VK_INSERT = 0x2D, VK_DELETE = 0x2E;
    private const int VK_F1 = 0x70, VK_F7 = 0x76, VK_F12 = 0x7B, VK_F24 = 0x87;

    public static KeyKind Classify(KeyChord chord)
    {
        var vk = chord.VirtualKey;
        if (IsModifier(vk)) return KeyKind.Ignore;

        // The inspector's own hotkeys (Ctrl+Shift+F7…F12).
        if (chord is { Ctrl: true, Shift: true, Alt: false, Win: false } && vk is >= VK_F7 and <= VK_F12) return KeyKind.Ignore;

        if (chord.HasCommandModifier)
        {
            // AltGr arrives as Ctrl+Alt: on many keyboard layouts it types characters such as @ or €.
            if (chord is { Ctrl: true, Alt: true, Win: false } && !IsNamedNonCharacterKey(vk)) return KeyKind.Typing;

            if (chord is { Ctrl: true, Alt: false, Win: false })
            {
                // Clipboard and undo shortcuts change a field's text; the field's new value is what gets recorded.
                if (vk is 'V' or 'X' or 'Z' or 'Y' or VK_BACK or VK_DELETE) return KeyKind.Typing;
                // Selecting, copying and moving the caret change nothing worth replaying.
                if (vk is 'A' or 'C' or VK_INSERT || vk is >= VK_PRIOR and <= VK_DOWN) return KeyKind.Ignore;
            }
            return chord.KeyName is null ? KeyKind.Ignore : KeyKind.Step;
        }

        if (vk == VK_TAB) return KeyKind.Ignore;
        if (vk is VK_RETURN or VK_ESCAPE || vk is >= VK_F1 and <= VK_F24) return KeyKind.Step;
        if (vk is VK_BACK or VK_SPACE or VK_DELETE or VK_INSERT || vk is >= VK_PRIOR and <= VK_DOWN) return KeyKind.StepUnlessTextField;
        return KeyKind.Typing;
    }

    private static bool IsModifier(int vk) =>
        vk is 0x10 or 0x11 or 0x12 // Shift, Ctrl, Alt
        or >= 0xA0 and <= 0xA5     // left/right Shift, Ctrl, Alt
        or 0x5B or 0x5C            // Windows keys
        or 0x14 or 0x90 or 0x91;   // Caps Lock, Num Lock, Scroll Lock

    private static bool IsNamedNonCharacterKey(int vk) =>
        vk is VK_BACK or VK_TAB or VK_RETURN or VK_ESCAPE or VK_SPACE or VK_INSERT or VK_DELETE
        || vk is >= VK_PRIOR and <= VK_DOWN
        || vk is >= VK_F1 and <= VK_F24;
}

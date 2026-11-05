using System.Drawing;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Recording;

/// <summary>Mouse settings that decide what counts as a double-click or a drag (the system's, by default).</summary>
public sealed record ClickSettings(int DoubleClickMs, Size DoubleClickArea, Size DragThreshold)
{
    public static ClickSettings FromSystem() => new(
        (int)GetDoubleClickTime(),
        new Size(GetSystemMetrics(SM_CXDOUBLECLK), GetSystemMetrics(SM_CYDOUBLECLK)),
        new Size(GetSystemMetrics(SM_CXDRAG), GetSystemMetrics(SM_CYDRAG)));

    private const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37, SM_CXDRAG = 68, SM_CYDRAG = 69;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

/// <summary>
/// Turns what the recorder observed into a tidy list of steps: merges a second click into a
/// double-click, drops drags, replaces a click into a field with the text typed there, merges
/// repeated keys and repeated edits of one field. Not thread-safe; the recorder locks around it.
/// </summary>
public sealed class StepBuilder(ClickSettings settings)
{
    private readonly List<RecordedStep> _steps = [];
    private int _nextId = 1;
    private (int StepId, MouseButton Button, Point Position, long Time, ElementInfo Target)? _lastDown;

    public IReadOnlyList<RecordedStep> Steps => _steps.ToList();

    public RecordedStep? Find(int id) => _steps.Find(s => s.Id == id);

    /// <summary>Records a mouse button press. Returns the id of a step that needs a selector, if any.</summary>
    public int? AddClick(IReadOnlyList<ElementInfo> path, MouseButton button, Point position, long time)
    {
        var target = path[^1];
        if (button == MouseButton.Left && _lastDown is { Button: MouseButton.Left } last
            && time - last.Time <= settings.DoubleClickMs
            && Math.Abs(position.X - last.Position.X) <= settings.DoubleClickArea.Width / 2
            && Math.Abs(position.Y - last.Position.Y) <= settings.DoubleClickArea.Height / 2
            && target.IsSameElement(last.Target)
            && Find(last.StepId) is { Action: RecordedAction.Click } first)
        {
            Replace(first with { Action = RecordedAction.DoubleClick });
            _lastDown = null; // a third click starts over
            return null;
        }

        var step = new RecordedStep
        {
            Id = _nextId++,
            Action = button == MouseButton.Right ? RecordedAction.RightClick : RecordedAction.Click,
            Path = path,
        };
        _steps.Add(step);
        _lastDown = (step.Id, button, position, time, target);
        return step.Id;
    }

    /// <summary>A mouse button was released. Returns true if the press turned out to be a drag, which isn't recorded.</summary>
    public bool MouseUp(Point position)
    {
        if (_lastDown is not { } down) return false;
        var moved = Math.Abs(position.X - down.Position.X) > settings.DragThreshold.Width
                    || Math.Abs(position.Y - down.Position.Y) > settings.DragThreshold.Height;
        if (!moved) return false;

        _steps.RemoveAll(s => s.Id == down.StepId && s.Action is RecordedAction.Click or RecordedAction.RightClick);
        _lastDown = null;
        return true;
    }

    /// <summary>Records the text a field ended up with. Returns the id of a step that needs a selector, if any.</summary>
    public int? AddText(TextEntry entry)
    {
        var field = entry.Field;
        var action = field.CanSetValue ? RecordedAction.SetValue : RecordedAction.TypeText;

        if (_steps.Count > 0 && _steps[^1] is var last && last.Target.IsSameElement(field.Element))
        {
            // Typing again into the field just typed into: keep only the final text.
            // A plain click into the field just before typing only gave it focus: the text replaces it.
            if (last.Action is RecordedAction.SetValue or RecordedAction.TypeText or RecordedAction.Click)
            {
                Replace(last with { Action = action, Text = entry.Text, IsSecret = field.IsSecret });
                _lastDown = null;
                return null;
            }
        }

        var step = new RecordedStep { Id = _nextId++, Action = action, Path = field.Path, Text = entry.Text, IsSecret = field.IsSecret };
        _steps.Add(step);
        _lastDown = null;
        return step.Id;
    }

    /// <summary>Records a key step on the focused element. Returns the id of a step that needs a selector, if any.</summary>
    public int? AddKey(IReadOnlyList<ElementInfo> focusPath, KeyChord key)
    {
        _lastDown = null;
        if (_steps.Count > 0 && _steps[^1] is { Action: RecordedAction.PressKey } last
            && last.Key == key && last.Target.IsSameElement(focusPath[^1]))
        {
            Replace(last with { Repeat = last.Repeat + 1 });
            return null;
        }

        var step = new RecordedStep { Id = _nextId++, Action = RecordedAction.PressKey, Path = focusPath, Key = key };
        _steps.Add(step);
        return step.Id;
    }

    public int AddCheck(IReadOnlyList<ElementInfo> path, string? text, TextSource source)
    {
        _lastDown = null;
        var step = new RecordedStep
        {
            Id = _nextId++,
            Action = text is null ? RecordedAction.CheckExists : RecordedAction.CheckText,
            Path = path,
            Text = text,
            CheckSource = source,
        };
        _steps.Add(step);
        return step.Id;
    }

    public void SetSelector(int id, SelectorResult? result, Selector? fallback, string? problem)
    {
        if (Find(id) is { } step) Replace(step with { Selector = result, Fallback = fallback, Problem = problem });
    }

    public bool Remove(int id)
    {
        if (_lastDown?.StepId == id) _lastDown = null;
        return _steps.RemoveAll(s => s.Id == id) > 0;
    }

    public void Clear()
    {
        _steps.Clear();
        _lastDown = null;
    }

    private void Replace(RecordedStep step)
    {
        var index = _steps.FindIndex(s => s.Id == step.Id);
        if (index >= 0) _steps[index] = step;
    }
}

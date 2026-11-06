namespace UiInspector.App;

/// <summary>A short notice that fades out at the bottom of the window ("Copied", "Saved").</summary>
public static class Toast
{
    /// <summary>The window shows it; raised on the UI thread.</summary>
    public static event Action<string>? Shown;

    public static void Show(string text) => Shown?.Invoke(text);
}

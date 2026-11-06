using System.IO;
using System.Text.Json;

namespace UiInspector.App;

/// <summary>
/// What the window remembers between runs: its place and size, the splitters, the page shown,
/// the theme and the preferred code languages. Kept in %LOCALAPPDATA%\UiInspector\settings.json;
/// a missing or broken file just means the defaults.
/// </summary>
public sealed class AppSettings
{
    // UIINSPECTOR_SETTINGS points test runs at their own file, so they don't change the tester's layout.
    private static readonly string FilePath = Environment.GetEnvironmentVariable("UIINSPECTOR_SETTINGS") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UiInspector", "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }

    public int Page { get; set; }

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Splitter positions: the width of each named column, as XAML writes it ("260", "1.3*").</summary>
    public Dictionary<string, string> Columns { get; set; } = [];

    public string? SnippetLanguage { get; set; }
    public string? RecorderLanguage { get; set; }
    public string? PlanLanguage { get; set; }

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembering the layout is no reason to fail closing the window.
        }
    }
}

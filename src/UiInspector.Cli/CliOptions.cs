using System.Drawing;
using System.Globalization;

namespace UiInspector.Cli;

public enum CliMode
{
    Interactive,
    Point,
    Focused,
    Help,
}

public sealed record CliOptions
{
    public CliMode Mode { get; init; } = CliMode.Interactive;
    public Point Point { get; init; }
    public bool IncludeAncestors { get; init; } = true;
    public bool Code { get; init; }
    public int? CdpPort { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(3);
    public bool Record { get; init; }
    public string Language { get; init; } = "flaui";
    public string? OutFile { get; init; }

    public static readonly string[] Languages =
        ["flaui", "uiautomation", "pywinauto", "appium", "playwright-js", "playwright-py", "playwright-cs", "selenium-py", "selenium-java"];

    public const string Usage = """
        uiinspect - inspect Windows UI elements via UI Automation

        Usage:
          uiinspect                     Interactive mode (global hotkeys, see below)
          uiinspect --record            Interactive mode, recording from the start
          uiinspect --point X Y         Inspect the element at screen point X,Y (physical pixels) and exit
          uiinspect --focused           Inspect the element with keyboard focus and exit

        Options:
          --code                        Also generate a verified selector and code (FlaUI, uiautomation, pywinauto, Appium)
          --cdp-port N                  Chromium debugging port for web elements (default: read from the app's command line)
          --no-ancestors                Don't walk and print the ancestor chain (not combinable with --code)
          --timeout MS                  Abandon an inspection after MS milliseconds (default 5000)
          --delay MS                    Countdown for the delayed-capture hotkey (default 3000)
          --lang NAME                   Recorded script language: flaui (default), uiautomation, pywinauto, appium;
                                        for web pages also playwright-js, playwright-py, playwright-cs, selenium-py, selenium-java
          --out FILE                    Write the recorded script to FILE instead of printing it
          -h, --help                    Show this help

        Interactive hotkeys:
          Ctrl+Shift+F9                 Inspect the element under the mouse cursor
          Ctrl+Shift+F10                Inspect the element with keyboard focus
          Ctrl+Shift+F11                Delayed capture: open a menu/tooltip, then wait for the countdown
          Ctrl+Shift+F12                Toggle hover tracking (one line per element you move over)
          Ctrl+Shift+F8                 Start/stop recording; on stop, the script is printed (or written to --out)
          Ctrl+Shift+F7                 While recording: add a check for the element under the cursor
          Ctrl+C                        Quit (stops recording first)

        Exit codes: 0 found, 1 no element, 2 error or timeout, 64 bad arguments.
        """;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            options = args[i] switch
            {
                "-h" or "--help" or "/?" => options with { Mode = CliMode.Help },
                "--point" => options with { Mode = CliMode.Point, Point = new Point(Int(args, ++i, "X"), Int(args, ++i, "Y")) },
                "--focused" => options with { Mode = CliMode.Focused },
                "--no-ancestors" => options with { IncludeAncestors = false },
                "--code" => options with { Code = true },
                "--cdp-port" => options with { CdpPort = Int(args, ++i, "--cdp-port") },
                "--timeout" => options with { Timeout = Milliseconds(args, ++i, "--timeout") },
                "--delay" => options with { Delay = Milliseconds(args, ++i, "--delay") },
                "--record" => options with { Mode = CliMode.Interactive, Record = true },
                "--lang" => options with { Language = Choice(args, ++i, "--lang", Languages) },
                "--out" => options with { OutFile = Text(args, ++i, "--out") },
                var unknown => throw new ArgumentException($"Unknown argument '{unknown}'."),
            };
        }
        if (options.Code && !options.IncludeAncestors)
            throw new ArgumentException("--code needs the ancestor chain; drop --no-ancestors.");
        return options;
    }

    private static int Int(string[] args, int index, string what)
    {
        if (index >= args.Length || !int.TryParse(args[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"Expected an integer for {what}.");
        return value;
    }

    private static string Text(string[] args, int index, string what)
    {
        if (index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Expected a value for {what}.");
        return args[index];
    }

    private static string Choice(string[] args, int index, string what, string[] allowed)
    {
        var value = Text(args, index, what).ToLowerInvariant();
        if (!allowed.Contains(value)) throw new ArgumentException($"{what} must be one of: {string.Join(", ", allowed)}.");
        return value;
    }

    private static TimeSpan Milliseconds(string[] args, int index, string what)
    {
        var ms = Int(args, index, what);
        if (ms <= 0) throw new ArgumentException($"{what} must be positive.");
        return TimeSpan.FromMilliseconds(ms);
    }
}

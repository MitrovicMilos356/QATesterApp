using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.Exceptions;

namespace UiInspector.Core.Web;

/// <summary>What kind of Chromium-based app an element is in, and how a script can start it.</summary>
public static partial class WebApps
{
    public static WebAppLaunch? Describe(IReadOnlyList<ElementInfo> pathFromRoot)
    {
        if (pathFromRoot.Count < 2) return null;
        var content = pathFromRoot[^1];
        var window = pathFromRoot[1];
        var name = content.ProcessName;

        if (name.Equals("msedge", StringComparison.OrdinalIgnoreCase) || name.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            || name.Equals("firefox", StringComparison.OrdinalIgnoreCase))
        {
            return new WebAppLaunch(WebAppKind.Browser, ExePath(content.ProcessId), name);
        }
        // WebView2 content runs in msedgewebview2.exe; the app to start is the one owning the window.
        if (name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
        {
            return Squirrel(new WebAppLaunch(WebAppKind.WebView2, ExePath(window.ProcessId), window.ProcessName));
        }
        return Squirrel(new WebAppLaunch(WebAppKind.Electron, ExePath(window.ProcessId) ?? ExePath(content.ProcessId), window.ProcessName.Length > 0 ? window.ProcessName : name));
    }

    /// <summary>
    /// Apps installed with Squirrel (Discord, Slack, GitHub Desktop…) live in a versioned folder,
    /// …\Discord\app-1.0.9267\Discord.exe, that changes with every update. The Update.exe beside
    /// those folders is stable and starts the current version: Update.exe --processStart Discord.exe.
    /// </summary>
    internal static WebAppLaunch Squirrel(WebAppLaunch launch)
    {
        if (launch.ExePath is not { } exe) return launch;
        var folder = Path.GetDirectoryName(exe);
        var root = folder is null ? null : Path.GetDirectoryName(folder);
        if (folder is null || root is null || !SquirrelFolder().IsMatch(Path.GetFileName(folder))) return launch;
        var update = Path.Combine(root, "Update.exe");
        return File.Exists(update) ? launch with { ExePath = update, SquirrelExe = Path.GetFileName(exe) } : launch;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^app-\d+(\.\d+)+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SquirrelFolder();

    /// <summary>
    /// A web page in a UIA path: Chromium (Edge, Chrome, Electron, WebView2) reports it as a Document
    /// with AutomationId "RootWebArea", Firefox as a Document of its own.
    /// </summary>
    public static bool IsWebPage(ElementInfo e) =>
        e.ControlType == "Document" && (e is { FrameworkId: "Chrome", AutomationId: "RootWebArea" } || e.FrameworkId == "Gecko");

    /// <summary>True for an element inside a web page that Playwright and Selenium can drive.</summary>
    public static bool IsWebContent(IReadOnlyList<ElementInfo> pathFromRoot) =>
        pathFromRoot.Count > 0 && pathFromRoot[^1].FrameworkId is "Chrome" or "Gecko" && pathFromRoot.Any(IsWebPage);

    /// <summary>The URL of the page the element is in (both Chromium and Firefox expose it as the document's value). UIA worker thread.</summary>
    public static string? ReadPageUrl(IReadOnlyList<ElementInfo> pathFromRoot)
    {
        var page = pathFromRoot.FirstOrDefault(IsWebPage);
        if (page?.Ref is null) return null;
        try
        {
            var value = page.Ref.Element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is COMException or FlaUIException)
        {
            return null;
        }
    }

    private static string? ExePath(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.NotSupportedException)
        {
            return null;
        }
    }
}

using System.Management;
using System.Text.RegularExpressions;

namespace UiInspector.Core.Web;

/// <summary>
/// Finds the Chromium remote-debugging port of a running app by reading process command lines.
/// The UIA element's process may be the browser process itself (Chrome, Edge, Electron) or a
/// host app whose WebView2 child carries the switch, so the process, its parent and its
/// descendants are all checked.
/// </summary>
public static partial class DebugPort
{
    public static int? TryFind(int processId)
    {
        var processes = Snapshot();
        if (!processes.TryGetValue(processId, out var start)) return null;

        var candidates = new List<ProcessRow> { start };
        if (processes.TryGetValue(start.ParentId, out var parent)) candidates.Add(parent);
        candidates.AddRange(Descendants(processes, processId));

        foreach (var process in candidates)
        {
            var match = PortSwitch().Match(process.CommandLine);
            if (!match.Success) continue;

            var port = int.Parse(match.Groups[1].Value);
            if (port != 0) return port;

            // --remote-debugging-port=0 picks a free port and writes it to <user-data-dir>\DevToolsActivePort.
            if (ReadActivePort(process.CommandLine) is { } active) return active;
        }
        return null;
    }

    private sealed record ProcessRow(int Id, int ParentId, string CommandLine);

    private static Dictionary<int, ProcessRow> Snapshot()
    {
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process");
        var rows = new Dictionary<int, ProcessRow>();
        foreach (var item in searcher.Get())
        {
            using (item)
            {
                var id = Convert.ToInt32(item["ProcessId"]);
                rows[id] = new ProcessRow(id, Convert.ToInt32(item["ParentProcessId"]), item["CommandLine"] as string ?? "");
            }
        }
        return rows;
    }

    private static IEnumerable<ProcessRow> Descendants(Dictionary<int, ProcessRow> processes, int rootId)
    {
        var queue = new Queue<int>([rootId]);
        var seen = new HashSet<int> { rootId };
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var child in processes.Values.Where(p => p.ParentId == id && seen.Add(p.Id)))
            {
                yield return child;
                queue.Enqueue(child.Id);
            }
        }
    }

    private static int? ReadActivePort(string commandLine)
    {
        var dir = UserDataDir().Match(commandLine);
        if (!dir.Success) return null;
        var file = Path.Combine(dir.Groups["dir"].Value.Trim('"'), "DevToolsActivePort");
        try
        {
            var first = File.ReadLines(file).FirstOrDefault();
            return int.TryParse(first, out var port) ? port : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"--remote-debugging-port[= ](\d+)")]
    private static partial Regex PortSwitch();

    [GeneratedRegex("--user-data-dir=(?<dir>\"[^\"]+\"|\\S+)")]
    private static partial Regex UserDataDir();
}

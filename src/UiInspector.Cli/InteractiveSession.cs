using System.Drawing;
using UiInspector.Core;
using UiInspector.Core.Recording;
using UiInspector.Core.Selectors;

namespace UiInspector.Cli;

/// <summary>Hotkey-driven inspection loop with optional hover tracking and recording.</summary>
public sealed class InteractiveSession(InspectionService service, ElementInspector inspector, ConsoleRenderer renderer, CliOptions options)
{
    private static readonly TimeSpan HoverInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SelectorWait = TimeSpan.FromSeconds(25);

    private volatile bool _hoverEnabled;
    private Recorder? _recorder;
    private readonly Dictionary<int, string> _printed = [];
    private readonly Lock _printGate = new();

    public async Task<int> RunAsync()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var hotkeys = new HotkeyListener();
        var registrations = hotkeys.Start();

        renderer.Info($"uiinspect — interactive mode (DPI awareness: {ScreenInfo.GetProcessDpiAwarenessName()})");
        foreach (var (hotkey, error) in registrations)
        {
            if (error is null) renderer.Info($"  {hotkey.Display,-16} {Describe(hotkey.Command)}");
            else renderer.Warning($"  {hotkey.Display,-16} NOT AVAILABLE ({error}) — another app already uses it");
        }
        renderer.Info("  Ctrl+C           quit");

        if (registrations.All(r => r.Error is not null))
        {
            renderer.Error("No hotkeys could be registered. Use --point or --focused instead.");
            return 2;
        }

        var hoverTask = HoverLoopAsync(cts.Token);
        try
        {
            if (options.Record) await ToggleRecordingAsync();
            await foreach (var command in hotkeys.Commands.ReadAllAsync(cts.Token))
            {
                await HandleAsync(command, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        await hoverTask;
        if (_recorder is not null)
        {
            if (_recorder.IsRecording) await StopRecordingAsync();
            await _recorder.DisposeAsync();
        }
        renderer.Info("Bye.");
        return 0;
    }

    // ---------------------------------------------------------------- recording

    private async Task ToggleRecordingAsync()
    {
        if (_recorder is null)
        {
            _recorder = new Recorder(service, inspector);
            _recorder.Changed += PrintNewSteps;
            _recorder.Notice += renderer.Warning;
        }

        if (_recorder.IsRecording)
        {
            await StopRecordingAsync();
            return;
        }

        try
        {
            await _recorder.StartAsync();
            renderer.Success("● Recording. Use the app you want to test; Ctrl+Shift+F7 adds a check, Ctrl+Shift+F8 stops.");
        }
        catch (Exception ex)
        {
            renderer.Error($"Couldn't start recording: {ex.Message}");
        }
    }

    private async Task StopRecordingAsync()
    {
        var recorder = _recorder!;
        await recorder.StopAsync();

        // Selectors are generated in the background; give the last ones a moment.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (recorder.Steps.Any(s => s.IsGenerating) && waited.Elapsed < SelectorWait) await Task.Delay(100);
        PrintNewSteps();

        var steps = recorder.Steps;
        if (steps.Count == 0)
        {
            renderer.Info("■ Stopped. Nothing was recorded.");
            return;
        }

        var script = recorder.RenderScripts().First(s => s.Id == options.Language);
        if (options.OutFile is { } path)
        {
            try
            {
                await File.WriteAllTextAsync(path, script.Code);
                renderer.Success($"■ Stopped. {steps.Count} steps; the {script.Title} script is in {Path.GetFullPath(path)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                renderer.Error($"Couldn't write {path}: {ex.Message}");
                Console.WriteLine(script.Code);
            }
        }
        else
        {
            renderer.Success($"■ Stopped. {steps.Count} steps. {script.Title} script (other languages: --lang):");
            Console.WriteLine();
            Console.WriteLine(script.Code);
        }
        renderer.Info("Ctrl+Shift+F8 records more steps; Ctrl+C quits.");
    }

    /// <summary>Prints each step once its selector is ready, and again if it changes (e.g. a click that became a double-click).</summary>
    private void PrintNewSteps()
    {
        if (_recorder is null) return;
        lock (_printGate)
        {
            var steps = _recorder.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step.IsGenerating) continue;
                var selector = step.ChosenSelector;
                var line = $"  {i + 1}. {step.Describe()}   [{selector?.Strategy ?? "no selector"}]";
                if (_printed.TryGetValue(step.Id, out var previous) && previous == line) continue;
                _printed[step.Id] = line;
                if (selector?.Verification.Status == VerificationStatus.Unique && selector.Steps.All(s => s.Index is null)) renderer.Info(line);
                else renderer.Warning(line + (selector is null ? $": {step.Problem}" : $": {selector.Verification.Describe()}"));
            }
        }
    }

    private async Task HandleAsync(HotkeyCommand command, CancellationToken ct)
    {
        switch (command)
        {
            case HotkeyCommand.InspectAtCursor:
                await InspectAtCursorAsync(ct);
                break;

            case HotkeyCommand.InspectFocused:
                await InspectAndRenderAsync(
                    () => service.InspectFocusedAsync(options.IncludeAncestors, renderer.WaitingForWebContent, ct), null, ct);
                break;

            case HotkeyCommand.InspectDelayed:
                var seconds = (int)Math.Ceiling(options.Delay.TotalSeconds);
                renderer.Info($"Delayed capture: put the cursor on the target (open the menu/tooltip now)… {seconds} s");
                await Task.Delay(options.Delay, ct);
                await InspectAtCursorAsync(ct);
                break;

            case HotkeyCommand.ToggleHover:
                _hoverEnabled = !_hoverEnabled;
                renderer.Info(_hoverEnabled ? "Hover tracking ON (Ctrl+Shift+F12 to stop)" : "Hover tracking OFF");
                break;

            case HotkeyCommand.ToggleRecording:
                await ToggleRecordingAsync();
                break;

            case HotkeyCommand.AddCheck:
                if (_recorder is { IsRecording: true } recorder) await recorder.AddCheckAsync(ScreenInfo.GetCursorPosition());
                else renderer.Warning("Checks are added while recording: press Ctrl+Shift+F8 first.");
                break;
        }
    }

    private Task InspectAtCursorAsync(CancellationToken ct)
    {
        var point = ScreenInfo.GetCursorPosition();
        return InspectAndRenderAsync(
            () => service.InspectAtPointAsync(point, options.IncludeAncestors, onWaiting: renderer.WaitingForWebContent, cancellationToken: ct),
            point, ct);
    }

    private async Task InspectAndRenderAsync(Func<Task<InspectionResult?>> inspect, Point? point, CancellationToken ct)
    {
        try
        {
            var result = await inspect();
            if (result is null)
            {
                renderer.Warning("No element found.");
            }
            else
            {
                renderer.Render(result);
                if (options.Code) await SelectorOutput.PrintAsync(service, renderer, result, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            renderer.Error(FailureMessages.Describe(ex, point));
        }
    }

    private async Task HoverLoopAsync(CancellationToken ct)
    {
        Point? lastPoint = null;
        ElementInfo? lastElement = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HoverInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_hoverEnabled)
            {
                lastPoint = null;
                lastElement = null;
                continue;
            }

            var point = ScreenInfo.GetCursorPosition();
            if (point == lastPoint) continue;
            lastPoint = point;

            try
            {
                var result = await service.InspectAtPointAsync(point, includeAncestors: false, waitForWebContent: false, cancellationToken: ct);

                // Hover must stay responsive, so it never waits; it re-checks the same spot instead
                // until Chromium has built the page content.
                if (result is { WebContentPending: true }) lastPoint = null;

                if (result is null || result.Element.IsSameElement(lastElement)) continue;
                lastElement = result.Element;
                renderer.RenderCompact(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                renderer.Error($"[hover] {FailureMessages.Describe(ex, point)}");
            }
        }
    }

    private static string Describe(HotkeyCommand command) => command switch
    {
        HotkeyCommand.InspectAtCursor => "inspect element under cursor",
        HotkeyCommand.InspectFocused => "inspect focused element",
        HotkeyCommand.InspectDelayed => "delayed capture under cursor (for menus/tooltips)",
        HotkeyCommand.ToggleHover => "toggle hover tracking",
        HotkeyCommand.ToggleRecording => "start/stop recording (prints the script on stop)",
        HotkeyCommand.AddCheck => "while recording: add a check for the element under the cursor",
        _ => command.ToString(),
    };
}

using System.Drawing;
using UiInspector.Core;

namespace UiInspector.Cli;

/// <summary>Hotkey-driven inspection loop with optional hover tracking.</summary>
public sealed class InteractiveSession(InspectionService service, ConsoleRenderer renderer, CliOptions options)
{
    private static readonly TimeSpan HoverInterval = TimeSpan.FromMilliseconds(100);

    private volatile bool _hoverEnabled;

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
            await foreach (var command in hotkeys.Commands.ReadAllAsync(cts.Token))
            {
                await HandleAsync(command, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        await hoverTask;
        renderer.Info("Bye.");
        return 0;
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
        _ => command.ToString(),
    };
}

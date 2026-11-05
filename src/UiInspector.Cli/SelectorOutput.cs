using UiInspector.Core;
using UiInspector.Core.Selectors;
using UiInspector.Core.Web;

namespace UiInspector.Cli;

/// <summary>Generates and prints the selector and code for an inspection (the --code option).</summary>
public static class SelectorOutput
{
    public static async Task PrintAsync(InspectionService service, ConsoleRenderer renderer, InspectionResult inspection, CancellationToken ct = default)
    {
        var path = inspection.Ancestors.Reverse().Append(inspection.Element).ToList();
        try
        {
            var result = await service.GenerateSelectorAsync(path, ct);
            renderer.RenderSelector(result, result.Best is { } best ? CodeTemplates.Render(best, result) : []);
            if (result.Web is { } web) renderer.RenderWeb(web, WebCodeTemplates.Render(web));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            renderer.Error($"Selector generation failed: {FailureMessages.Describe(ex, inspection.ScreenPoint)}");
        }
    }
}

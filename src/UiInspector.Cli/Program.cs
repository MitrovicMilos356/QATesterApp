using System.Text;
using UiInspector.Cli;
using UiInspector.Core;

Console.OutputEncoding = Encoding.UTF8;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Run 'uiinspect --help' for usage.");
    return 64;
}

if (options.Mode == CliMode.Help)
{
    Console.WriteLine(CliOptions.Usage);
    return 0;
}

var renderer = new ConsoleRenderer();
using var worker = new UiaWorker(new UiaWorkerOptions { OperationTimeout = options.Timeout });
var inspector = new ElementInspector();
var service = new InspectionService(worker, inspector) { CdpPort = options.CdpPort };

return options.Mode switch
{
    CliMode.Point => await RunOnceAsync(
        () => service.InspectAtPointAsync(options.Point, options.IncludeAncestors, onWaiting: renderer.WaitingForWebContent),
        options.Point),
    CliMode.Focused => await RunOnceAsync(
        () => service.InspectFocusedAsync(options.IncludeAncestors, onWaiting: renderer.WaitingForWebContent),
        null),
    _ => await new InteractiveSession(service, inspector, renderer, options).RunAsync(),
};

async Task<int> RunOnceAsync(Func<Task<InspectionResult?>> inspect, System.Drawing.Point? point)
{
    try
    {
        var result = await inspect();
        if (result is null)
        {
            renderer.Warning("No element found.");
            return 1;
        }
        renderer.Render(result);
        if (options.Code) await SelectorOutput.PrintAsync(service, renderer, result);
        return 0;
    }
    catch (Exception ex)
    {
        renderer.Error(FailureMessages.Describe(ex, point));
        return 2;
    }
}

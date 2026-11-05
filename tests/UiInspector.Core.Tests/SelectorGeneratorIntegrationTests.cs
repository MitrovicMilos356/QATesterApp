using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Tests;

/// <summary>Starts the WinForms selector fixture once for all integration tests and closes it afterwards.</summary>
public class SelectorFixtureWindow : IDisposable
{
    public const string Title = "UiInspector Selector Fixture";

    private readonly Process _process;

    public SelectorFixtureWindow() : this(Title, "SelectorFixtureForm")
    {
    }

    /// <summary>Another instance with its own title and form name, for tests that change the fixture's state.</summary>
    protected SelectorFixtureWindow(string title, string formName)
    {
        WindowTitle = title;
        var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "selector_fixture.ps1");
        _process = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Title \"{title}\" -FormName {formName}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        using var automation = new UIA3Automation();
        var window = Retry.WhileNull(
            () => automation.GetDesktop().FindFirstChild(automation.ConditionFactory.ByName(title)),
            TimeSpan.FromSeconds(20)).Result;
        if (window is null)
        {
            Dispose();
            throw new InvalidOperationException("The selector fixture window didn't appear.");
        }
    }

    public string WindowTitle { get; }

    public void Dispose()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        _process.Dispose();
        GC.SuppressFinalize(this);
    }
}

[Trait("Category", "Desktop")]
public class SelectorGeneratorIntegrationTests(SelectorFixtureWindow fixture) : IClassFixture<SelectorFixtureWindow>, IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly ElementInspector _inspector = new();
    private readonly SelectorFixtureWindow _fixture = fixture; // keeps the window alive for the class

    public void Dispose() => _automation.Dispose();

    private AutomationElement Window =>
        _automation.GetDesktop().FindFirstChild(_automation.ConditionFactory.ByName(SelectorFixtureWindow.Title))
        ?? throw new InvalidOperationException("Fixture window is gone.");

    private SelectorResult GenerateFor(Func<AutomationElement, AutomationElement> findTarget)
    {
        var target = findTarget(Window);
        var inspection = _inspector.InspectElement(_automation, target) ?? throw new InvalidOperationException("Inspection failed.");
        var path = inspection.Ancestors.Reverse().Append(inspection.Element).ToList();
        return new SelectorGenerator(_inspector).Generate(_automation, path);
    }

    private AutomationElement Panel(AutomationElement window, string id) =>
        window.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId(id))!;

    private AutomationElement[] Buttons(AutomationElement scope, string name) =>
        scope.FindAllDescendants(_automation.ConditionFactory.ByControlType(ControlType.Button)
            .And(_automation.ConditionFactory.ByName(name)));

    [Fact]
    public void UniqueAutomationId_IsTheBestSelector()
    {
        var result = GenerateFor(w => w.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId("saveButton"))!);

        var best = Assert.IsType<Selector>(result.Best);
        Assert.Equal("AutomationId", best.Strategy);
        Assert.Equal(VerificationStatus.Unique, best.Verification.Status);
        var step = Assert.Single(best.Steps);
        Assert.Equal("saveButton", step.Get(SelectorProperty.AutomationId)?.Value);
        Assert.Equal("Button", step.ControlType);
    }

    [Fact]
    public void WindowPart_UsesTitleNotWinFormsClassName()
    {
        var result = GenerateFor(w => w.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId("saveButton"))!);

        var window = result.Best!.Window;
        Assert.Null(window.Get(SelectorProperty.ClassName)); // WindowsForms10.* changes per build
        Assert.Equal("SelectorFixtureForm", window.Get(SelectorProperty.AutomationId)?.Value);
    }

    [Fact]
    public void DuplicateCaption_IsScopedToItsPanel()
    {
        var result = GenerateFor(w => Buttons(Panel(w, "ordersPanel"), "Delete").Single());

        var best = result.Best!;
        Assert.Equal(2, best.Steps.Count);
        Assert.Equal("ordersPanel", best.Steps[0].Get(SelectorProperty.AutomationId)?.Value);
        Assert.Equal("Delete", best.Steps[1].Get(SelectorProperty.Name)?.Value);
        Assert.Null(best.Steps[1].Index);
        Assert.Equal(VerificationStatus.Unique, best.Verification.Status);
        Assert.Contains("inside Pane #ordersPanel", best.Strategy);
    }

    [Fact]
    public void IdenticalButtons_FallBackToIndexWithinPanel_AndSayItsFragile()
    {
        var result = GenerateFor(w => Buttons(Panel(w, "toolsPanel"), "Action")[2]);

        var best = result.Best!;
        Assert.Equal("toolsPanel", best.Steps[0].Get(SelectorProperty.AutomationId)?.Value);
        Assert.Equal(2, best.Steps[^1].Index);
        Assert.Contains(best.Warnings, w => w.Contains("position 3 of 3"));
    }

    [Fact]
    public void NameWithNumber_GetsAWarning()
    {
        var result = GenerateFor(w => w.FindFirstDescendant(_automation.ConditionFactory.ByName("Status: 3 items"))!);

        Assert.Contains(result.Best!.Warnings, w => w.Contains("numbers"));
    }

    [Fact]
    public void SelectingTheWindowItself_GivesAWindowOnlySelector()
    {
        var result = GenerateFor(w => w);

        Assert.Empty(result.Best!.Steps);
        Assert.Equal(VerificationStatus.Unique, result.Best.Verification.Status);
    }

    [Fact]
    public void OffscreenWindow_IsReportedAsNotClickable()
    {
        var result = GenerateFor(w => w.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId("saveButton"))!);

        Assert.True(result.Occlusion?.IsCovered);
    }

    // Writes the snippets for three selector shapes (plain ID, anchored name, anchored index) to
    // bin/.../generated so they can be run, unedited, against the fixture in end-to-end checks.
    [Fact]
    public void GeneratedSnippets_AreWrittenForEndToEndChecks()
    {
        var cases = new (string Name, Func<AutomationElement, AutomationElement> Find)[]
        {
            ("save_button", w => w.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId("saveButton"))!),
            ("orders_delete", w => Buttons(Panel(w, "ordersPanel"), "Delete").Single()),
            ("third_action", w => Buttons(Panel(w, "toolsPanel"), "Action")[2]),
        };

        var outDir = Path.Combine(AppContext.BaseDirectory, "generated");
        Directory.CreateDirectory(outDir);
        foreach (var (name, find) in cases)
        {
            var result = GenerateFor(find);
            var snippets = CodeTemplates.Render(result.Best!, result);
            Assert.Equal(4, snippets.Count);
            foreach (var snippet in snippets) File.WriteAllText(Path.Combine(outDir, $"{name}.{snippet.Id}.txt"), snippet.Code);
        }
    }
}

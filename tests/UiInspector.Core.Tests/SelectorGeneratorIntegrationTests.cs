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
        // Shell-started (hidden), so it doesn't inherit the test runner's output handles: a leftover
        // fixture holding them would keep `dotnet test` waiting after the tests finish.
        _process = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Title \"{title}\" -FormName {formName}")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        })!;

        // A UIA call can fail now and then while other desktop tests run; keep trying, and if the
        // window still doesn't show up, close the process before giving up so it can't be left behind.
        AutomationElement? window = null;
        try
        {
            using var automation = new UIA3Automation();
            window = Retry.WhileNull(
                () => automation.GetDesktop().FindFirstChild(automation.ConditionFactory.ByName(title)),
                TimeSpan.FromSeconds(20), ignoreException: true).Result;
        }
        finally
        {
            if (window is null) Dispose();
        }
        if (window is null) throw new InvalidOperationException("The selector fixture window didn't appear.");
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

    // The second order row: its panel has no ID or name, and all rows have the same Delete button.
    private AutomationElement OrderRowDelete(AutomationElement window, string order)
    {
        var label = window.FindFirstDescendant(_automation.ConditionFactory.ByName(order))!;
        var row = _automation.TreeWalkerFactory.GetControlViewWalker().GetParent(label);
        return Buttons(row, "Delete").Single();
    }

    [Fact]
    public void RowWithoutId_IsFoundByTheTextInIt_NotByPosition()
    {
        var result = GenerateFor(w => OrderRowDelete(w, "Order 1042"));

        var best = result.Best!;
        Assert.All(best.Steps, s => Assert.Null(s.Index));
        Assert.Equal("ordersList", best.Steps[0].Get(SelectorProperty.AutomationId)?.Value);
        Assert.Equal("Order 1042", best.Steps[1].Has?.Get(SelectorProperty.Name)?.Value);
        Assert.Equal("Delete", best.Steps[2].Get(SelectorProperty.Name)?.Value);
        Assert.Equal(VerificationStatus.Unique, best.Verification.Status);
        Assert.Contains("containing \"Order 1042\"", best.Strategy);
        Assert.DoesNotContain(best.Warnings, w => w.Contains("position"));
    }

    // Regression (Discord's profile popout): the click closes the popup, so by the time the selector
    // is checked the only thing that told the two "OK" buttons apart is gone. Checked against a
    // snapshot taken at the click, the selector is as good as for any other element.
    [Fact]
    public void ClickThatClosesItsPopup_IsVerifiedAgainstTheSnapshotTakenAtTheClick()
    {
        var window = Window;
        window.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId("showPopupButton"))!.Patterns.Invoke.Pattern.Invoke();
        var question = Retry.WhileNull(() => window.FindFirstDescendant(_automation.ConditionFactory.ByName("Confirm delete?")), TimeSpan.FromSeconds(5)).Result!;
        var popup = _automation.TreeWalkerFactory.GetControlViewWalker().GetParent(question);
        var ok = Buttons(popup, "OK").Single();

        var inspection = _inspector.InspectElement(_automation, ok)!;
        var path = inspection.Ancestors.Reverse().Append(inspection.Element).ToList();
        var snapshot = TreeSnapshot.Capture(_automation, path)!; // what the recorder does at mouse-down

        ok.Patterns.Invoke.Pattern.Invoke(); // the app reacts: the popup closes
        Retry.WhileTrue(() => window.FindFirstDescendant(_automation.ConditionFactory.ByName("Confirm delete?")) is not null, TimeSpan.FromSeconds(5));

        var generator = new SelectorGenerator(_inspector);
        var live = generator.Generate(_automation, path);
        Assert.True(live.Best is null || live.Best.Steps.Any(s => s.Index is not null), "Checked live, the closed popup can't be told apart.");

        var atClick = generator.Generate(_automation, path, snapshot);
        var best = atClick.Best!;
        Assert.Equal(VerificationStatus.Unique, best.Verification.Status);
        Assert.All(best.Steps, s => Assert.Null(s.Index));
        Assert.Contains("containing \"Confirm delete?\"", best.Strategy);
    }

    [Fact]
    public void Snapshot_CopiesTheWholeWindowQuickly()
    {
        var target = Window.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId("saveButton"))!;
        var inspection = _inspector.InspectElement(_automation, target)!;
        var path = inspection.Ancestors.Reverse().Append(inspection.Element).ToList();

        var watch = Stopwatch.StartNew();
        var snapshot = TreeSnapshot.Capture(_automation, path)!;
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
        Assert.True(snapshot.Count > 30);
        Assert.All(path.Skip(1), e => Assert.True(snapshot.Contains(e)));

        // Same answer as checking live.
        var generator = new SelectorGenerator(_inspector);
        Assert.Equal(generator.Generate(_automation, path).Best!.Strategy, generator.Generate(_automation, path, snapshot).Best!.Strategy);
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
            ("order_1042", w => OrderRowDelete(w, "Order 1042")),
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

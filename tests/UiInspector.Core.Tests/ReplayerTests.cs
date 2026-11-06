using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using UiInspector.Core.Recording;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Tests;

/// <summary>Its own fixture window, since replaying changes its state.</summary>
public sealed class ReplayFixtureWindow() : SelectorFixtureWindow(Title, "ReplayFixtureForm")
{
    public new const string Title = "UiInspector Replay Fixture";
}

/// <summary>
/// Replays steps against the fixture. Only pattern actions are used (Invoke, Toggle, SetValue)
/// and checks, never the real mouse or keyboard, so whoever is at the computer isn't disturbed.
/// </summary>
[Trait("Category", "Desktop")]
public class ReplayerTests(ReplayFixtureWindow fixture) : IClassFixture<ReplayFixtureWindow>, IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly UiaWorker _worker = new();
    private readonly ElementInspector _inspector = new();
    private readonly ReplayFixtureWindow _fixture = fixture;
    private int _nextId;

    public void Dispose()
    {
        _worker.Dispose();
        _automation.Dispose();
    }

    private AutomationElement Window =>
        _automation.GetDesktop().FindFirstChild(_automation.ConditionFactory.ByName(ReplayFixtureWindow.Title))
        ?? throw new InvalidOperationException("Fixture window is gone.");

    private AutomationElement ById(string id) =>
        Window.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId(id)) ?? throw new InvalidOperationException($"#{id} not found.");

    private AutomationElement PanelDelete(string panel) =>
        ById(panel).FindFirstDescendant(_automation.ConditionFactory.ByControlType(ControlType.Button).And(_automation.ConditionFactory.ByName("Delete")))!;

    private AutomationElement StatusLabel(string text) =>
        Window.FindFirstDescendant(_automation.ConditionFactory.ByControlType(ControlType.Text).And(_automation.ConditionFactory.ByName(text)))!;

    private RecordedStep Step(RecordedAction action, AutomationElement element, string? text = null, TextSource source = TextSource.Name)
    {
        var inspection = _inspector.InspectElement(_automation, element)!;
        var path = inspection.Ancestors.Reverse().Append(inspection.Element).ToList();
        return new RecordedStep
        {
            Id = ++_nextId,
            Action = action,
            Path = path,
            Text = text,
            CheckSource = source,
            Selector = new SelectorGenerator(_inspector).Generate(_automation, path),
        };
    }

    private Replayer Replayer(TimeSpan? findTimeout = null) =>
        new(_worker, new ReplayOptions { StepPause = TimeSpan.Zero, FindTimeout = findTimeout ?? TimeSpan.FromSeconds(5) });

    [Fact]
    public async Task Recording_IsPlayedBack_StepByStep()
    {
        PanelDelete("ordersPanel").Patterns.Invoke.Pattern.Invoke();
        var check = Step(RecordedAction.CheckText, StatusLabel("Deleted order"), "Deleted order");
        var steps = new[]
        {
            Step(RecordedAction.SetValue, ById("notesBox"), "Replayed text"),
            Step(RecordedAction.Click, ById("saveButton")),
            Step(RecordedAction.Click, PanelDelete("ordersPanel")),
            check,
        };
        PanelDelete("customersPanel").Patterns.Invoke.Pattern.Invoke(); // the app is somewhere else now
        ById("notesBox").Patterns.Value.Pattern.SetValue("");

        var seen = new List<ReplayStepResult>();
        var replayer = Replayer();
        replayer.Progress += seen.Add;
        var results = await replayer.RunAsync(steps);

        Assert.All(results, r => Assert.Equal(ReplayStatus.Passed, r.Status));
        Assert.Equal("Replayed text", ById("notesBox").Patterns.Value.Pattern.Value.Value);
        Assert.NotNull(StatusLabel("Deleted order"));
        Assert.Equal(steps.Length * 2, seen.Count); // Running, then the outcome
        Assert.All(results, r => Assert.NotNull(r.Bounds));
    }

    [Fact]
    public async Task FailedCheck_StopsTheReplay_AndSaysWhatItGot()
    {
        var notes = ById("notesBox");
        notes.Patterns.Value.Pattern.SetValue("What the app shows");
        var steps = new[]
        {
            Step(RecordedAction.CheckText, notes, "What the test expects", TextSource.Value),
            Step(RecordedAction.Click, ById("saveButton")),
        };

        var results = await Replayer().RunAsync(steps);

        Assert.Equal(ReplayStatus.Failed, results[0].Status);
        Assert.Equal("Expected \"What the test expects\", but it shows \"What the app shows\".", results[0].Message);
        Assert.NotNull(results[0].Screenshot); // the off-screen fixture window, for the bug report
        Assert.Equal(ReplayStatus.Pending, results[1].Status);
    }

    [Fact]
    public async Task MissingElement_SaysWhichPartOfTheSelectorFoundNothing()
    {
        var save = Step(RecordedAction.Click, ById("saveButton"));
        var selector = save.ChosenSelector!;
        var renamed = selector with { Steps = [new SelectorStep([new(SelectorProperty.ControlType, "Button"), new(SelectorProperty.AutomationId, "goneButton")])] };
        var closed = selector with { Window = new SelectorStep([new(SelectorProperty.ControlType, "Window"), new(SelectorProperty.Name, "No such window")]) };

        var results = await Replayer(TimeSpan.FromMilliseconds(500)).RunAsync(
            [save with { Selector = save.Selector! with { Best = renamed } }]);
        Assert.Equal(ReplayStatus.Failed, results[0].Status);
        Assert.StartsWith("Nothing matches Button [AutomationId = \"goneButton\"] in the window (waited 0.5 s)", results[0].Message);

        results = await Replayer(TimeSpan.FromMilliseconds(500)).RunAsync(
            [save with { Selector = save.Selector! with { Best = closed } }]);
        Assert.Contains("isn't open", results[0].Message);
    }

    [Fact]
    public async Task TextStepWithoutText_FailsAndSaysWhy_InsteadOfTypingNothing()
    {
        var step = Step(RecordedAction.SetValue, ById("notesBox"));

        var results = await Replayer().RunAsync([step]);

        Assert.Equal(ReplayStatus.Failed, results[0].Status);
        Assert.Contains("couldn't be read while recording", results[0].Message);
    }

    [Fact]
    public async Task StepWithoutSelector_IsSkipped_LikeInTheScripts()
    {
        var save = Step(RecordedAction.Click, ById("saveButton"));
        var results = await Replayer().RunAsync([save with { Selector = null, Problem = "the element was gone" }, save]);

        Assert.Equal(ReplayStatus.Skipped, results[0].Status);
        Assert.Contains("the element was gone", results[0].Message);
        Assert.Equal(ReplayStatus.Passed, results[1].Status);
    }
}

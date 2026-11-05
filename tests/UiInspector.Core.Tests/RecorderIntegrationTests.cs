using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using UiInspector.Core.Recording;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Tests;

/// <summary>A separate fixture window for the recorder, since recording changes its state.</summary>
public sealed class RecorderFixtureWindow() : SelectorFixtureWindow(Title, "RecorderFixtureForm")
{
    public new const string Title = "UiInspector Recorder Fixture";
}

[Trait("Category", "Desktop")]
public class InputHookTests
{
    // Events that arrive while the hook is installed are dropped on the spot: nothing is read or kept.
    [Fact]
    public void InstallsAndRemovesCleanly()
    {
        using (var hook = new InputHook())
        {
            hook.Start(_ => { });
            Assert.Throws<InvalidOperationException>(() => hook.Start(_ => { }));
        }

        using var again = new InputHook();
        again.Start(_ => { }); // the first one is gone, so a new one installs fine
    }
}

/// <summary>Never installs a hook: the tests feed the recorder directly.</summary>
internal sealed class NoInput : IInputSource
{
    public void Start(Action<RawInput> sink)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Records a workflow against the fixture the way a user would produce it: focus moves, text
/// appears in fields, clicks land on controls, and each click is then performed. Real mouse and
/// keyboard input is never used (it would disturb whoever is at the computer); the recorder is
/// fed the same element-level events its hook and focus handler would produce.
/// </summary>
[Trait("Category", "Desktop")]
public class RecorderIntegrationTests(RecorderFixtureWindow fixture) : IClassFixture<RecorderFixtureWindow>, IAsyncLifetime
{
    private readonly UIA3Automation _automation = new();
    private readonly UiaWorker _worker = new();
    private readonly RecorderFixtureWindow _fixture = fixture;
    private AutomationElement? _focused;
    private Recorder _recorder = null!;

    public Task InitializeAsync()
    {
        var inspector = new ElementInspector();
        _recorder = new Recorder(new InspectionService(_worker, inspector), inspector, new RecorderOptions
        {
            InputFactory = () => new NoInput(),
            TrackFocusEvents = false,
            ClickSettings = new ClickSettings(500, new System.Drawing.Size(4, 4), new System.Drawing.Size(4, 4)),
            FocusOverride = _ => _focused, // never the user's real focused element
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _recorder.DisposeAsync();
        _worker.Dispose();
        _automation.Dispose();
    }

    private AutomationElement Window =>
        _automation.GetDesktop().FindFirstChild(_automation.ConditionFactory.ByName(RecorderFixtureWindow.Title))
        ?? throw new InvalidOperationException("Fixture window is gone.");

    private AutomationElement ById(string id) =>
        Window.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId(id)) ?? throw new InvalidOperationException($"#{id} not found.");

    private AutomationElement OrdersDelete() =>
        ById("ordersPanel").FindFirstDescendant(_automation.ConditionFactory.ByControlType(ControlType.Button).And(_automation.ConditionFactory.ByName("Delete")))!;

    private async Task<IReadOnlyList<RecordedStep>> WaitForSelectorsAsync()
    {
        for (var i = 0; i < 200 && _recorder.Steps.Any(s => s.IsGenerating); i++) await Task.Delay(50);
        return _recorder.Steps;
    }

    [Fact]
    public async Task Workflow_BecomesVerifiedStepsAndRunnableScripts()
    {
        var notes = ById("notesBox");
        var password = ById("passwordBox");
        var remember = ById("rememberBox");

        // 1. Click into the notes box (focus follows) and type.
        await _recorder.ClickOnAsync(notes, time: 1000);
        _focused = notes;
        await _recorder.FocusOnAsync(notes);
        notes.Patterns.Value.Pattern.SetValue("Hello from the recorder");

        // 2. Focus moves to the password box; type a password there.
        _focused = password;
        await _recorder.FocusOnAsync(password);
        _recorder.Post(new RawTyping(0));
        password.Patterns.Value.Pattern.SetValue("s3cret");

        // 3. Click the checkbox (the recorder sees the mouse go down while the password box still has focus).
        await _recorder.ClickOnAsync(remember, time: 3000);
        remember.Patterns.Toggle.Pattern.Toggle();
        _focused = remember;

        // 4. Save, then the Delete button in the Orders panel (same caption as in Customers).
        await _recorder.ClickOnAsync(ById("saveButton"), time: 5000);
        ById("saveButton").Patterns.Invoke.Pattern.Invoke();
        await _recorder.ClickOnAsync(OrdersDelete(), time: 7000);
        OrdersDelete().Patterns.Invoke.Pattern.Invoke();

        // 5. Check what the status label says now.
        await Task.Delay(200);
        var status = Window.FindFirstDescendant(_automation.ConditionFactory.ByName("Deleted order"))!;
        await _recorder.CheckOnAsync(status);

        var steps = await WaitForSelectorsAsync();
        Assert.Equal(
            [RecordedAction.SetValue, RecordedAction.SetValue, RecordedAction.Click, RecordedAction.Click, RecordedAction.Click, RecordedAction.CheckText],
            steps.Select(s => s.Action));

        Assert.Equal("Hello from the recorder", steps[0].Text); // the click into the box became the typed text
        Assert.True(steps[1].IsSecret);
        Assert.Null(steps[1].Text); // the password itself is never read
        Assert.Equal("Deleted order", steps[5].Text);

        Assert.All(steps, s => Assert.Equal(VerificationStatus.Unique, s.ChosenSelector?.Verification.Status));
        Assert.Equal("ordersPanel", steps[4].ChosenSelector!.Steps[0].Get(SelectorProperty.AutomationId)?.Value);
        Assert.Equal(ElementAction.Toggle, RecordingScripts.ClickAction(steps[2].Target));

        var outDir = Path.Combine(AppContext.BaseDirectory, "generated");
        Directory.CreateDirectory(outDir);
        foreach (var script in _recorder.RenderScripts())
        {
            Assert.DoesNotContain("s3cret", script.Code);
            File.WriteAllText(Path.Combine(outDir, $"recorded.{script.Id}.txt"), script.Code);
        }
    }

    [Fact]
    public async Task TextTypedJustBeforeStopping_IsRecorded()
    {
        var notes = ById("notesBox");
        _focused = notes;
        await _recorder.FocusOnAsync(notes);
        notes.Patterns.Value.Pattern.SetValue("last words " + Guid.NewGuid().ToString("N")[..6]);

        await _recorder.FlushAsync(); // what StopAsync does

        var step = Assert.Single(await WaitForSelectorsAsync());
        Assert.Equal(RecordedAction.SetValue, step.Action);
        Assert.StartsWith("last words", step.Text);
    }
}

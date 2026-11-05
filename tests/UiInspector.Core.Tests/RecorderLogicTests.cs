using System.Drawing;
using UiInspector.Core.Recording;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Tests;

public class KeyClassifierTests
{
    [Theory]
    [InlineData('A')]
    [InlineData('7')]
    [InlineData(0xBE)] // OEM period
    public void PrintableKeys_AreOnlyTyping(int vk)
    {
        Assert.Equal(KeyKind.Typing, KeyClassifier.Classify(new KeyChord(vk)));
        Assert.Equal(KeyKind.Typing, KeyClassifier.Classify(new KeyChord(vk, Shift: true)));
    }

    [Fact]
    public void AltGrCharacters_AreTyping()
    {
        // AltGr+Q types @ on German layouts; Windows reports it as Ctrl+Alt+Q.
        Assert.Equal(KeyKind.Typing, KeyClassifier.Classify(new KeyChord('Q', Ctrl: true, Alt: true)));
    }

    [Theory]
    [InlineData(0x0D)] // Enter
    [InlineData(0x1B)] // Escape
    [InlineData(0x74)] // F5
    public void CommandKeys_AreSteps(int vk) => Assert.Equal(KeyKind.Step, KeyClassifier.Classify(new KeyChord(vk)));

    [Fact]
    public void Shortcuts_AreSteps()
    {
        Assert.Equal(KeyKind.Step, KeyClassifier.Classify(new KeyChord('S', Ctrl: true)));
        Assert.Equal(KeyKind.Step, KeyClassifier.Classify(new KeyChord(0x73, Alt: true))); // Alt+F4
        Assert.Equal(KeyKind.Step, KeyClassifier.Classify(new KeyChord('S', Ctrl: true, Shift: true)));
    }

    [Fact]
    public void EditingShortcuts_AreTypingOrIgnored()
    {
        Assert.Equal(KeyKind.Typing, KeyClassifier.Classify(new KeyChord('V', Ctrl: true)));
        Assert.Equal(KeyKind.Typing, KeyClassifier.Classify(new KeyChord('Z', Ctrl: true)));
        Assert.Equal(KeyKind.Ignore, KeyClassifier.Classify(new KeyChord('C', Ctrl: true)));
        Assert.Equal(KeyKind.Ignore, KeyClassifier.Classify(new KeyChord('A', Ctrl: true)));
        Assert.Equal(KeyKind.Ignore, KeyClassifier.Classify(new KeyChord(0x25, Ctrl: true, Shift: true))); // select word left
    }

    [Fact]
    public void NavigationKeys_DependOnWhetherATextFieldHasFocus()
    {
        foreach (var vk in new[] { 0x28, 0x2E, 0x20, 0x08, 0x22 }) // Down, Delete, Space, Backspace, PageDown
        {
            Assert.Equal(KeyKind.StepUnlessTextField, KeyClassifier.Classify(new KeyChord(vk)));
        }
    }

    [Fact]
    public void ModifiersTabAndOwnHotkeys_AreIgnored()
    {
        Assert.Equal(KeyKind.Ignore, KeyClassifier.Classify(new KeyChord(0xA2, Ctrl: true))); // left Ctrl itself
        Assert.Equal(KeyKind.Ignore, KeyClassifier.Classify(new KeyChord(0x09)));
        Assert.Equal(KeyKind.Ignore, KeyClassifier.Classify(new KeyChord(0x09, Shift: true)));
        Assert.Equal(KeyKind.Ignore, KeyClassifier.Classify(new KeyChord(0x77, Ctrl: true, Shift: true))); // Ctrl+Shift+F8
    }

    [Fact]
    public void ChordsReadNaturally()
    {
        Assert.Equal("Ctrl+Shift+S", new KeyChord('S', Ctrl: true, Shift: true).ToString());
        Assert.Equal("PageDown", new KeyChord(0x22).ToString());
        Assert.Equal("Alt+F4", new KeyChord(0x73, Alt: true).ToString());
    }
}

/// <summary>Elements and paths for recorder logic tests (no UIA involved).</summary>
internal static class Fake
{
    private static int _nextRuntimeId = 100;

    public static readonly ElementInfo Desktop = new() { ControlType = "Pane", Name = "Desktop 1", RuntimeId = [42] };
    public static readonly ElementInfo Window = new() { ControlType = "Window", Name = "Orders - App", AutomationId = "MainForm", ProcessName = "app", RuntimeId = [1, 1] };

    public static IReadOnlyList<ElementInfo> Path(string controlType, string name = "", string automationId = "", string[]? patterns = null, bool password = false) =>
    [
        Desktop,
        Window,
        new ElementInfo
        {
            ControlType = controlType,
            Name = name,
            AutomationId = automationId,
            Patterns = patterns ?? [],
            IsPassword = password,
            ProcessName = "app",
            RuntimeId = [7, Interlocked.Increment(ref _nextRuntimeId)],
        },
    ];

    public static FieldState Field(IReadOnlyList<ElementInfo> path, string? text, bool secret = false, bool canSet = true) =>
        new(path, secret ? null : text, secret, canSet);
}

public class TextTrackerTests
{
    [Fact]
    public void ChangedTextIsReported_WhenSomethingHappensNext()
    {
        var tracker = new TextTracker();
        var email = Fake.Field(Fake.Path("Edit", "Email"), "");
        tracker.Update(email, null); // focus arrives in the empty field

        var entries = tracker.Update(email, "me@example.com"); // a click elsewhere: focus is still in the field

        var entry = Assert.Single(entries);
        Assert.Equal("me@example.com", entry.Text);
        Assert.Empty(tracker.Update(email, "me@example.com")); // nothing new since
    }

    [Fact]
    public void FocusMovingOn_FlushesTheField_AndStartsTrackingTheNextOne()
    {
        var tracker = new TextTracker();
        var name = Fake.Field(Fake.Path("Edit", "Name"), "");
        var city = Fake.Field(Fake.Path("Edit", "City"), "Paris");
        tracker.Update(name, null);

        var entries = tracker.Update(city, "Ada");

        Assert.Equal("Ada", Assert.Single(entries).Text);
        Assert.Same(city, tracker.Field);
        Assert.Empty(tracker.Update(city, "Paris")); // unchanged prefilled value isn't recorded
    }

    [Fact]
    public void UnchangedField_IsNotRecorded_EvenIfVisited()
    {
        var tracker = new TextTracker();
        var field = Fake.Field(Fake.Path("Edit", "Search"), "prefilled");
        tracker.Update(field, null);
        Assert.Empty(tracker.Update(null, "prefilled"));
        Assert.Null(tracker.Field);
    }

    [Fact]
    public void PasswordFields_AreNeverRead_ButTypingIntoThemIsRecorded()
    {
        var tracker = new TextTracker();
        var password = Fake.Field(Fake.Path("Edit", "Password", password: true), null, secret: true);
        tracker.Update(password, null);
        tracker.Typed();

        var entry = Assert.Single(tracker.Update(null, null));
        Assert.True(entry.Field.IsSecret);
        Assert.Null(entry.Text);
    }

    [Fact]
    public void PasswordFieldLeftWithoutTyping_IsNotRecorded()
    {
        var tracker = new TextTracker();
        tracker.Update(Fake.Field(Fake.Path("Edit", "Password", password: true), null, secret: true), null);
        Assert.Empty(tracker.Update(null, null));
    }

    [Fact]
    public void TypingIntoAFieldThatGotFocusWithoutAnEvent_IsAttributedToIt()
    {
        var tracker = new TextTracker();
        var first = Fake.Field(Fake.Path("Edit", "First"), "");
        tracker.Update(first, null);
        tracker.Typed();

        // Focus moved to "Second" without a focus event; the typing went there.
        var second = Fake.Field(Fake.Path("Edit", "Second"), "typed here");
        var entry = Assert.Single(tracker.Update(second, ""));
        Assert.Equal("typed here", entry.Text);
        Assert.Same(second.Element, entry.Field.Element);
    }
}

public class StepBuilderTests
{
    private static readonly ClickSettings Settings = new(500, new Size(4, 4), new Size(4, 4));

    [Fact]
    public void TwoQuickClicksOnOneElement_BecomeADoubleClick()
    {
        var builder = new StepBuilder(Settings);
        var file = Fake.Path("ListItem", "report.txt");
        builder.AddClick(file, MouseButton.Left, new Point(10, 10), 1000);
        builder.MouseUp(new Point(10, 10));
        Assert.Null(builder.AddClick(file, MouseButton.Left, new Point(11, 10), 1200));

        Assert.Equal(RecordedAction.DoubleClick, Assert.Single(builder.Steps).Action);
    }

    [Fact]
    public void SlowClicks_StayTwoClicks()
    {
        var builder = new StepBuilder(Settings);
        var button = Fake.Path("Button", "Next");
        builder.AddClick(button, MouseButton.Left, new Point(10, 10), 1000);
        builder.AddClick(button, MouseButton.Left, new Point(10, 10), 2000);
        Assert.Equal(2, builder.Steps.Count);
    }

    [Fact]
    public void Drags_AreDropped()
    {
        var builder = new StepBuilder(Settings);
        builder.AddClick(Fake.Path("Thumb"), MouseButton.Left, new Point(10, 10), 1000);
        Assert.True(builder.MouseUp(new Point(80, 10)));
        Assert.Empty(builder.Steps);
    }

    [Fact]
    public void ClickIntoAField_IsReplacedByTheTextTypedThere()
    {
        var builder = new StepBuilder(Settings);
        var email = Fake.Path("Edit", "Email", patterns: ["Value"]);
        var id = builder.AddClick(email, MouseButton.Left, new Point(5, 5), 1000);
        Assert.Null(builder.AddText(new TextEntry(Fake.Field(email, "a@b.c"), "a@b.c")));

        var step = Assert.Single(builder.Steps);
        Assert.Equal(RecordedAction.SetValue, step.Action);
        Assert.Equal("a@b.c", step.Text);
        Assert.Equal(id, step.Id); // keeps the selector that's being generated for the click
    }

    [Fact]
    public void RepeatedEditsOfOneField_KeepOnlyTheFinalText()
    {
        var builder = new StepBuilder(Settings);
        var field = Fake.Path("Edit", "Search", patterns: ["Value"]);
        builder.AddText(new TextEntry(Fake.Field(field, "x"), "first"));
        builder.AddText(new TextEntry(Fake.Field(field, "x"), "second"));
        Assert.Equal("second", Assert.Single(builder.Steps).Text);
    }

    [Fact]
    public void EditorsWithoutASettableValue_AreTypedAsKeys()
    {
        var builder = new StepBuilder(Settings);
        builder.AddText(new TextEntry(Fake.Field(Fake.Path("Edit", "Message"), "hi", canSet: false), "hi"));
        Assert.Equal(RecordedAction.TypeText, Assert.Single(builder.Steps).Action);
    }

    [Fact]
    public void RepeatedKeys_AreCounted()
    {
        var builder = new StepBuilder(Settings);
        var list = Fake.Path("List", "Files");
        var down = new KeyChord(0x28);
        builder.AddKey(list, down);
        builder.AddKey(list, down);
        builder.AddKey(list, down);
        builder.AddKey(list, new KeyChord(0x0D));

        Assert.Equal(2, builder.Steps.Count);
        Assert.Equal(3, builder.Steps[0].Repeat);
        Assert.Equal("Press Down ×3 in List \"Files\"", builder.Steps[0].Describe());
    }

    [Fact]
    public void RemovedStepsCantMergeWithLaterClicks()
    {
        var builder = new StepBuilder(Settings);
        var button = Fake.Path("Button", "OK");
        var id = builder.AddClick(button, MouseButton.Left, new Point(1, 1), 1000)!.Value;
        builder.Remove(id);
        Assert.NotNull(builder.AddClick(button, MouseButton.Left, new Point(1, 1), 1100));
        Assert.Equal(RecordedAction.Click, Assert.Single(builder.Steps).Action);
    }
}

public class RecordingScriptTests
{
    private static SelectorCondition C(SelectorProperty p, string v, MatchKind m = MatchKind.Exact) => new(p, v, m);

    private static readonly SelectorStep WindowStep = new([C(SelectorProperty.ControlType, "Window"), C(SelectorProperty.AutomationId, "MainForm")]);

    private static RecordedStep Step(int id, RecordedAction action, IReadOnlyList<ElementInfo> path, string automationId,
        string? text = null, bool secret = false, KeyChord? key = null, int repeat = 1, TextSource source = TextSource.Value)
    {
        var selector = new Selector
        {
            Window = WindowStep,
            Steps = [new([C(SelectorProperty.ControlType, path[^1].ControlType), C(SelectorProperty.AutomationId, automationId)])],
            Strategy = "AutomationId",
            Score = 100,
            Verification = new(VerificationStatus.Unique, 1),
        };
        return new RecordedStep
        {
            Id = id,
            Action = action,
            Path = path,
            Text = text,
            IsSecret = secret,
            Key = key,
            Repeat = repeat,
            CheckSource = source,
            Selector = new SelectorResult { Target = path[^1], Best = selector, ProcessName = "app", ProcessPath = @"C:\Apps\app.exe" },
        };
    }

    private static readonly IReadOnlyList<RecordedStep> Workflow =
    [
        Step(1, RecordedAction.SetValue, Fake.Path("Edit", "User", patterns: ["Value"]), "userBox", text: "ada"),
        Step(2, RecordedAction.SetValue, Fake.Path("Edit", "Password", patterns: ["Value"], password: true), "passwordBox", secret: true),
        Step(3, RecordedAction.Click, Fake.Path("CheckBox", "Remember me", patterns: ["Toggle"]), "rememberBox"),
        Step(4, RecordedAction.Click, Fake.Path("Button", "Sign in", patterns: ["Invoke"]), "signInButton"),
        Step(5, RecordedAction.PressKey, Fake.Path("List", "Orders"), "orders", key: new KeyChord(0x28), repeat: 2),
        Step(6, RecordedAction.PressKey, Fake.Path("List", "Orders"), "orders", key: new KeyChord('S', Ctrl: true, Shift: true)),
        Step(7, RecordedAction.CheckText, Fake.Path("Text", "Signed in as ada"), "status", text: "Signed in as ada", source: TextSource.Name),
    ];

    private static string Code(string id) => RecordingScripts.Render(Workflow).Single(s => s.Id == id).Code;

    [Fact]
    public void EveryLanguage_HasEveryStepInOrder()
    {
        foreach (var snippet in RecordingScripts.Render(Workflow))
        {
            var positions = Enumerable.Range(1, Workflow.Count).Select(n => snippet.Code.IndexOf($" {n}. ", StringComparison.Ordinal)).ToList();
            Assert.All(positions, p => Assert.True(p > 0, $"{snippet.Id} is missing a step"));
            Assert.Equal(positions.Order(), positions);
        }
    }

    [Fact]
    public void Passwords_ComeFromEnvironmentVariables_NeverTheScript()
    {
        foreach (var snippet in RecordingScripts.Render(Workflow))
        {
            Assert.Contains("UIINSPECTOR_SECRET_1", snippet.Code);
        }
        Assert.Contains("target.Patterns.Value.Pattern.SetValue(Secret(\"UIINSPECTOR_SECRET_1\"));", Code("flaui"));
        Assert.Contains("target.GetValuePattern().SetValue(secret('UIINSPECTOR_SECRET_1'))", Code("uiautomation"));
        Assert.Contains("target.iface_value.SetValue(secret('UIINSPECTOR_SECRET_1'))", Code("pywinauto"));
    }

    [Fact]
    public void Clicks_UsePatternActions()
    {
        var flaui = Code("flaui");
        Assert.Contains("target.Patterns.Toggle.Pattern.Toggle();", flaui);
        Assert.Contains("target.Patterns.Invoke.Pattern.Invoke();", flaui);
        Assert.DoesNotContain("moves the real mouse", flaui);
    }

    [Fact]
    public void Keys_AreWrittenInEachLibrarysSyntax()
    {
        Assert.Contains("for (var i = 0; i < 2; i++) Keyboard.Type(VirtualKeyShort.DOWN);", Code("flaui"));
        Assert.Contains("Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_S);", Code("flaui"));
        Assert.Contains("target.SendKeys('{Ctrl}{Shift}s')", Code("uiautomation"));
        Assert.Contains("target.SendKeys('{Down}')", Code("uiautomation"));
        Assert.Contains("target.type_keys('^+s')", Code("pywinauto"));
        Assert.Contains("target.send_keys(Keys.CONTROL + Keys.SHIFT + 's' + Keys.CONTROL + Keys.SHIFT)", Code("appium"));
    }

    [Fact]
    public void Checks_CompareTheRecordedText()
    {
        Assert.Contains("var actual = target.Name;", Code("flaui"));
        Assert.Contains("if (actual != \"Signed in as ada\")", Code("flaui"));
        Assert.Contains("actual = target.Name", Code("uiautomation"));
        Assert.Contains("actual = target.element_info.name", Code("pywinauto"));
    }

    [Fact]
    public void Header_SaysWhatToSetAndHowToLaunch()
    {
        var code = Code("pywinauto");
        Assert.Contains("Recorded with UiInspector: 7 steps in app.", code);
        Assert.Contains(@"Application(backend=""uia"").start(r'C:\Apps\app.exe')", code);
        Assert.Contains("Set the UIINSPECTOR_SECRET_1 environment variable", code);
    }

    [Fact]
    public void TypedText_IsEscapedForKeySyntax()
    {
        var editor = Fake.Path("Document", "Message");
        var steps = new[] { Step(1, RecordedAction.TypeText, editor, "msg", text: "50% off {now} (x+y)\nok") };
        var scripts = RecordingScripts.Render(steps);

        Assert.Contains("Keyboard.Type(\"50% off {now} (x+y)\\nok\");", scripts.Single(s => s.Id == "flaui").Code);
        Assert.Contains("target.SendKeys('50% off {{}now{}} {(}x+y{)}{Enter}ok')", scripts.Single(s => s.Id == "uiautomation").Code);
        Assert.Contains("target.type_keys('50{%} off {{}now{}} {(}x{+}y{)}{ENTER}ok', with_spaces=True, with_tabs=True)", scripts.Single(s => s.Id == "pywinauto").Code);
    }

    [Fact]
    public void StepsWithoutASelector_AreSkippedWithTheReason()
    {
        var step = new RecordedStep { Id = 1, Action = RecordedAction.Click, Path = Fake.Path("Button", "Gone"), Problem = "The window can't be found again" };
        var code = RecordingScripts.Render([step]).Single(s => s.Id == "flaui").Code;
        Assert.Contains("SKIPPED, The window can't be found again", code);
        Assert.DoesNotContain("var target", code);
    }

    [Fact]
    public void ClickIntoATextField_FocusesIt_InsteadOfTypingSampleText()
    {
        var field = new ElementInfo { ControlType = "Edit", Patterns = ["Value"] };
        Assert.Equal(ElementAction.Focus, RecordingScripts.ClickAction(field));
    }

    [Fact]
    public void UnverifiedSelector_IsBuiltFromTheSnapshot_AndScopedToAStableAncestor()
    {
        var dialog = new ElementInfo { ControlType = "Window", Name = "Confirm", AutomationId = "ConfirmDialog", RuntimeId = [3] };
        var buttons = new ElementInfo { ControlType = "Pane", AutomationId = "buttonPanel", RuntimeId = [4] };
        var ok = new ElementInfo { ControlType = "Button", Name = "OK", RuntimeId = [5] };

        var selector = SelectorGenerator.BuildUnverified([Fake.Desktop, dialog, buttons, ok])!;

        Assert.Equal(VerificationStatus.Unverified, selector.Verification.Status);
        Assert.Equal("buttonPanel", selector.Steps[0].Get(SelectorProperty.AutomationId)?.Value);
        Assert.Equal("OK", selector.Steps[1].Get(SelectorProperty.Name)?.Value);
        Assert.Contains("(unverified)", selector.Strategy);
    }
}

using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using UiInspector.Core.Testability;

namespace UiInspector.Core.Tests;

/// <summary>Its own fixture window, so other tests' changes to it don't matter.</summary>
public sealed class TestabilityFixtureWindow() : SelectorFixtureWindow(Title, "TestabilityFixtureForm")
{
    public new const string Title = "UiInspector Testability Fixture";
}

[Trait("Category", "Desktop")]
public class TestabilityTests(TestabilityFixtureWindow fixture) : IClassFixture<TestabilityFixtureWindow>, IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly UiaWorker _worker = new();
    private readonly ElementInspector _inspector = new();
    private readonly TestabilityFixtureWindow _fixture = fixture;

    public void Dispose()
    {
        _worker.Dispose();
        _automation.Dispose();
    }

    private AutomationElement Window =>
        _automation.GetDesktop().FindFirstChild(_automation.ConditionFactory.ByName(TestabilityFixtureWindow.Title))
        ?? throw new InvalidOperationException("Fixture window is gone.");

    private async Task<TestabilityReport> AnalyzeAsync()
    {
        var save = Window.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId("saveButton"))!;
        var info = _inspector.InspectElement(_automation, save)!.Element;
        return await new TestabilityAnalyzer(_worker, _inspector).AnalyzeAsync(info);
    }

    private static TestabilityFinding Only(TestabilityReport report, Func<ElementInfo, bool> match) =>
        report.Findings.Single(f => match(f.Element));

    [Fact]
    public async Task EveryControl_IsGraded_ByHowTestsCanFindIt()
    {
        var report = await AnalyzeAsync();

        Assert.True(report.Duration < TimeSpan.FromSeconds(10), $"took {report.Duration}");
        Assert.Equal(["WinForm"], report.Frameworks);
        Assert.NotNull(report.Screenshot);

        var save = Only(report, e => e.AutomationId == "saveButton");
        Assert.Equal(TestabilityGrade.Good, save.Grade);
        Assert.Empty(save.Issues);
        Assert.Null(save.Fix);

        // Three identical buttons: only position tells them apart.
        var actions = report.Findings.Where(f => f.Element.Name == "Action").ToList();
        Assert.Equal(3, actions.Count);
        Assert.All(actions, f => Assert.Equal(TestabilityGrade.Fragile, f.Grade));
        Assert.All(actions, f => Assert.True(f.Problems.HasFlag(TestabilityProblem.Position)));
        Assert.Contains("Set the control's Name to \"toolsActionButton\"", actions[0].Fix);

        // Rows told apart by their text: works, but the id has to be unique per row.
        var row = report.Findings.Single(f => f.FoundBy?.Contains("Order 1042") == true);
        Assert.Equal(TestabilityGrade.Fair, row.Grade);
        Assert.True(row.Problems.HasFlag(TestabilityProblem.NearbyText));
        Assert.Contains("add the row's key", row.Fix);
        // A WinForms window handle as id is just "no id": the fix says so, no extra issue about it.
        Assert.DoesNotContain(row.Issues, i => i.Contains("window handle"));

        // Found by a stable id, but nothing for a screen reader to announce.
        var password = Only(report, e => e.AutomationId == "passwordBox");
        Assert.Equal(TestabilityGrade.Good, password.Grade);
        Assert.True(password.MissingName);
        Assert.Contains("AccessibleName", password.Fix);

        // Unnamed popup items: counted inside their menu, still fragile, and the suggested id is a placeholder.
        // (Windows names the first of the three after a nearby label, so it's found by that text instead.)
        var menuItems = report.Findings.Where(f => f.FoundBy?.Contains("Profile menu") == true).ToList();
        Assert.Equal(2, menuItems.Count);
        Assert.All(menuItems, f => Assert.Equal(TestabilityGrade.Fragile, f.Grade));
        Assert.All(menuItems, f => Assert.True(f.MissingName));
        Assert.Contains("whatItDoesButton", menuItems[0].Fix);
        Assert.DoesNotContain("row", menuItems[0].Fix);
        Assert.Contains("each need their own id", menuItems[0].Fix);
        Assert.Contains("each need their own id", actions[0].Fix);

        Assert.InRange(report.Score, 1, 99);
        Assert.Equal(report.Findings.Count, report.Findings.Select(f => f.Number).Distinct().Count());
    }

    [Fact]
    public async Task Report_SaysWhatToFixFirst_AndNumbersEveryControl()
    {
        var report = await AnalyzeAsync();
        var html = TestabilityRenderer.Html(report);
        var text = TestabilityRenderer.Text(report);

        var dir = Path.Combine(AppContext.BaseDirectory, "generated");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "testability.html"), html);

        Assert.Contains($"{report.Score}<small> / 100</small>", html);
        Assert.Contains("In short: the changes that help most", html);
        Assert.Contains("All problems, counted", html);
        var byPosition = report.Findings.Count(f => f.Problems.HasFlag(TestabilityProblem.Position));
        Assert.Equal(5, byPosition); // the three Action buttons and two unnamed menu items
        Assert.Contains($"{byPosition} controls are only found by their position.", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("Already easy to automate", html);
        Assert.Contains("“Save” button", html);
        Assert.Contains($"**Score: {report.Score}/100**", text);
        Assert.DoesNotContain("base64", text);
    }

    // A long list of look-alikes is one entry with one fix, not one entry per control.
    [Fact]
    public async Task LookAlikes_WithTheSameProblem_AreOneGroup()
    {
        var report = await AnalyzeAsync();

        var rows = report.Groups.Single(g => g.First.FoundBy?.Contains("Order 104") == true);
        Assert.Equal(3, rows.Count);
        Assert.Equal("3 buttons like “Delete”", rows.Title);
        var actions = report.Groups.Single(g => g.First.Element.Name == "Action");
        Assert.Equal(3, actions.Count);
        Assert.Equal(TestabilityGrade.Fragile, actions.Grade);
        // Same caption in two differently named panels: two groups, each with its own fix.
        Assert.Equal(2, report.Groups.Count(g => g.First.FoundBy?.StartsWith("Name inside Pane #") == true && g.First.Element.Name == "Delete"));
        // Most fragile first.
        Assert.Equal(TestabilityGrade.Fragile, report.Groups[0].Grade);
        Assert.Equal(report.Findings.Count, report.Groups.Sum(g => g.Findings.Count));

        var html = TestabilityRenderer.Html(report);
        Assert.Contains("3 buttons like “Delete”", html);
        Assert.Contains("Fix (for each of them):", html);
        Assert.Contains($"Numbers {string.Join(", ", rows.Findings.Select(f => f.Number))}", html);
    }

    // Edge keeps other tabs' pages (and its sidebar) in the tree: the page pointed at is the one graded.
    [Fact]
    public void InABrowser_ThePagePointedAt_IsGraded_NotAnotherTab()
    {
        ElementInfo Info(string type, string name, int id, bool offscreen = false, int size = 100, string automationId = "") => new()
        {
            ControlType = type, Name = name, AutomationId = automationId, RuntimeId = [42, id], IsOffscreen = offscreen,
            BoundingRectangle = new System.Drawing.Rectangle(0, 0, size, size),
        };
        var jobLink = Info("Hyperlink", "Senior .NET developer", 5);
        var youTube = new ElementNode(Info("Document", "YouTube", 2, offscreen: true, size: 900, automationId: "RootWebArea"),
            [new ElementNode(Info("Hyperlink", "The Deep Web's Unsearchable Horrors", 3), [])]);
        var jobs = new ElementNode(Info("Document", "HelloWorld", 4, size: 800, automationId: "RootWebArea"), [new ElementNode(jobLink, [])]);
        var window = new ElementNode(Info("Window", "Edge", 1), [new ElementNode(Info("Pane", "", 6), [youTube, jobs])]);
        var desktop = Info("Pane", "Desktop", 0);

        Assert.Equal("HelloWorld", TestabilityAnalyzer.ChoosePage(window, desktop, jobLink)!.Value.Node.Info.Name);
        // Pointed at the window itself (or the toolbar): the visible page, not the bigger hidden tab.
        Assert.Equal("HelloWorld", TestabilityAnalyzer.ChoosePage(window, desktop, window.Info)!.Value.Node.Info.Name);
    }

    // Firefox: every open tab's page is a Document (background tabs off-screen), and its toolbars,
    // tabs and menus aren't pages, so they're never graded.
    [Fact]
    public void InFirefox_TheVisibleTabsPage_IsGraded_NotTheToolbarsOrOtherTabs()
    {
        ElementInfo Info(string type, string name, int id, bool offscreen = false, string automationId = "") => new()
        {
            ControlType = type, Name = name, AutomationId = automationId, FrameworkId = "Gecko", RuntimeId = [42, id],
            IsOffscreen = offscreen, BoundingRectangle = new System.Drawing.Rectangle(0, 0, 1920, 955),
        };
        var back = new ElementNode(Info("Button", "Back", 10, automationId: "back-button"), []);
        var hiddenTab = new ElementNode(Info("Document", "Other tab", 2, offscreen: true), [new ElementNode(Info("Hyperlink", "Elsewhere", 3), [])]);
        var shownTab = new ElementNode(Info("Document", "HelloWorld", 4), [new ElementNode(Info("Hyperlink", "Jobs", 5), [])]);
        var panels = new ElementNode(Info("Pane", "", 6, automationId: "tabbrowser-tabpanels"), [hiddenTab, shownTab]);
        var window = new ElementNode(Info("Window", "Firefox", 1, automationId: "main-window"),
            [new ElementNode(Info("ToolBar", "Navigation", 7), [back]), panels]);
        var desktop = Info("Pane", "Desktop", 0);

        Assert.True(TestabilityAnalyzer.IsPage(shownTab.Info));
        Assert.False(TestabilityAnalyzer.IsPage(window.Info));
        Assert.Equal("HelloWorld", TestabilityAnalyzer.ChoosePage(window, desktop, back.Info)!.Value.Node.Info.Name);
    }

    // Desktop apps get pytest + uiautomation and FlaUI + xUnit suites, saved for running against the fixture.
    [Fact]
    public async Task DesktopForm_GetsDataDrivenSuites()
    {
        var report = await AnalyzeAsync();
        var login = report.Plan!.Features.Single(f => f.Kind == TestIdeas.FeatureKind.Login && f.Action is not null);
        Assert.Equal("Sign in", login.Action!.Element.Name);
        Assert.Equal(["User name", "Password"], login.Fields.Select(x => x.Label));

        var py = login.Suites.Single(s => s.Id == "suite-uiautomation").Code;
        var cs = login.Suites.Single(s => s.Id == "suite-flaui").Code;
        var dir = Path.Combine(AppContext.BaseDirectory, "generated", "suites");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "desktop_login_test.py"), py);
        File.WriteAllText(Path.Combine(dir, "DesktopLoginSuite.cs"), cs);
        Assert.Equal(["suite-uiautomation", "suite-flaui", "suite-pywinauto", "suite-appium"], login.Suites.Select(s => s.Id));
        var pywinauto = login.Suites.Single(s => s.Id == "suite-pywinauto").Code;
        var appium = login.Suites.Single(s => s.Id == "suite-appium").Code;
        File.WriteAllText(Path.Combine(dir, "desktop_login_pywinauto_test.py"), pywinauto);
        File.WriteAllText(Path.Combine(dir, "desktop_login_appium_test.py"), appium);
        Assert.Contains("auto_id='signInUser'", pywinauto);
        Assert.Contains("iface_invoke.Invoke()", pywinauto);
        Assert.Contains("[@AutomationId='signInUser']", appium);

        Assert.Contains("AutomationId='TestabilityFixtureForm'", py);
        Assert.Contains("@pytest.mark.parametrize", py);
        Assert.Contains("GetInvokePattern().Invoke()", py);                // the button, not the keyboard
        Assert.Contains("\"*\", \"\"", py);                                // "both empty" empties every field
        Assert.DoesNotContain("\"password\", \"nobody@example.com\"", py); // a user-field case never lands in the password
        Assert.Contains("[MemberData(nameof(Cases))]", cs);
        Assert.Contains("FlaUI.Core.Capturing.Capture.Element(window)", cs);
    }

    [Theory]
    [InlineData("WPF", "AutomationProperties.AutomationId=\"searchFriendsField\"")]
    [InlineData("Chrome", "id=\"search-friends-field\"")]
    [InlineData("WinForm", "Name to \"searchFriendsField\"")]
    [InlineData("Qt", "setObjectName(\"searchFriendsField\")")]
    [InlineData("Win32", "control ID")]
    public void Fix_IsWrittenForTheAppsTechnology(string framework, string expected)
    {
        var field = new ElementInfo { ControlType = "Edit", Name = "Search friends", FrameworkId = framework };
        Assert.Contains(expected, TestabilityAnalyzer.Fix(field, needsId: true, missingName: false));
    }

    [Theory]
    [InlineData("Delete", "Button", "ordersPanel", "ordersDeleteButton")]
    [InlineData("Remember me", "CheckBox", null, "rememberMeCheckbox")]
    [InlineData("Save", "Button", null, "saveButton")]
    [InlineData("", "Button", null, "whatItDoesButton")]
    [InlineData("Order #1042 – details", "Hyperlink", null, "order1042DetailsLink")]
    public void SuggestedId_ComesFromTheNameKindAndContainer(string name, string type, string? context, string expected)
    {
        Assert.Equal(expected, TestabilityAnalyzer.SuggestedId(new ElementInfo { Name = name, ControlType = type }, kebab: false, context));
    }
}

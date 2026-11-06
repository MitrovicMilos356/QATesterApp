using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using UiInspector.Core.Recording;
using UiInspector.Core.Selectors;
using UiInspector.Core.Web;

namespace UiInspector.Core.Tests;

/// <summary>The web fixture in Edge without a debug port, as most apps run.</summary>
public sealed class EdgeNoPortFixture() : EdgeWebFixture(withDebugPort: false);

/// <summary>
/// Web locators built from the accessibility tree (UI Automation) only, for apps started the
/// normal way. Generated snippets are saved for end-to-end runs.
/// </summary>
[Trait("Category", "Desktop")]
public class NoDebugPortWebTests(EdgeNoPortFixture fixture) : IClassFixture<EdgeNoPortFixture>, IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly ElementInspector _inspector = new();
    private readonly UiaWorker _worker = new(new UiaWorkerOptions { OperationTimeout = TimeSpan.FromSeconds(20) });
    private readonly EdgeNoPortFixture _fixture = fixture;

    public void Dispose()
    {
        _worker.Dispose();
        _automation.Dispose();
    }

    private AutomationElement Window => _fixture.FindWindow(_automation) ?? throw new InvalidOperationException("Fixture window is gone.");

    private AutomationElement Find(Func<AutomationElement, AutomationElement?> find) =>
        Retry.WhileNull(() => find(Window), TimeSpan.FromSeconds(10)).Result ?? throw new InvalidOperationException("Element not found in the fixture page.");

    private AutomationElement? ByName(AutomationElement scope, ControlType type, string name) =>
        scope.FindFirstDescendant(_automation.ConditionFactory.ByControlType(type).And(_automation.ConditionFactory.ByName(name)));

    // The Delete button on the same line as "Order 1042" (Chromium may leave the row <div> out of the UIA tree).
    private AutomationElement? OrderDelete(AutomationElement w)
    {
        if (ByName(w, ControlType.Text, "Order 1042") is not { } label) return null;
        var y = label.BoundingRectangle.Y + label.BoundingRectangle.Height / 2;
        return w.FindAllDescendants(_automation.ConditionFactory.ByControlType(ControlType.Button).And(_automation.ConditionFactory.ByName("Delete")))
            .FirstOrDefault(b => Math.Abs(b.BoundingRectangle.Y + b.BoundingRectangle.Height / 2 - y) < 6);
    }

    private async Task<WebResult> WebAsync(string name, Func<AutomationElement, AutomationElement?> find)
    {
        var inspection = _inspector.InspectElement(_automation, Find(find))!;
        var path = inspection.Ancestors.Reverse().Append(inspection.Element).ToList();
        var result = await new InspectionService(_worker, _inspector).GenerateSelectorAsync(path);
        var web = result.Web!;

        var dir = Path.Combine(AppContext.BaseDirectory, "generated");
        Directory.CreateDirectory(dir);
        foreach (var snippet in WebCodeTemplates.Render(web)) File.WriteAllText(Path.Combine(dir, $"noport_{name}.{snippet.Id}.txt"), snippet.Code);
        return web;
    }

    // Replay sets the text the way the scripts do, and the page's own input handler sees it.
    [Fact]
    public async Task Replay_TypesIntoAWebField_AndThePageSeesIt()
    {
        var email = Find(w => w.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId("email-input")));
        var inspection = _inspector.InspectElement(_automation, email)!;
        var path = inspection.Ancestors.Reverse().Append(inspection.Element).ToList();
        var step = new RecordedStep
        {
            Id = 1, Action = RecordedAction.SetValue, Path = path, Text = "probe@example.com",
            Selector = new SelectorGenerator(_inspector).Generate(_automation, path),
        };
        var results = await new Replayer(_worker, new ReplayOptions { StepPause = TimeSpan.Zero }).RunAsync([step]);

        Assert.Equal(ReplayStatus.Passed, results[0].Status);
        Assert.False(results[0].IsWarning, results[0].Message);
        Assert.Equal("probe@example.com", email.Patterns.Value.Pattern.Value.Value);
        Assert.NotNull(Retry.WhileNull(() => Window.FindFirstDescendant(_automation.ConditionFactory.ByName("email: probe@example.com")), TimeSpan.FromSeconds(3)).Result);
    }

    // In a browser only the page is the app under test: Edge's own toolbar isn't graded.
    [Fact]
    public async Task Testability_GradesOnlyThePage_AndFlagsTheChangingReactId()
    {
        var checkout = Find(w => ByName(w, ControlType.Button, "Checkout"));
        var info = _inspector.InspectElement(_automation, checkout)!.Element;

        var report = await new Testability.TestabilityAnalyzer(_worker, _inspector).AnalyzeAsync(info);

        Assert.All(report.Findings, f => Assert.Contains(f.Path, e => e is { ControlType: "Document", AutomationId: "RootWebArea" }));
        Assert.Equal("Chrome", report.Frameworks[0]);
        var search = report.Findings.Single(f => f.Element.AutomationId == "_r_b3r_");
        Assert.True(search.Problems.HasFlag(Testability.TestabilityProblem.ChangingId));
        Assert.Contains("data-testid=\"search-friends-field\"", search.Fix);
        Assert.Equal(Testability.TestabilityGrade.Fair, search.Grade); // found by its label meanwhile
    }

    // The test plan finds the sign-in form and puts its main flow together as a Playwright script
    // from the same locators the recorder would use; the password comes from an environment variable.
    [Fact]
    public async Task TestPlan_FindsTheLogin_WithCasesAndAScript()
    {
        var checkout = Find(w => ByName(w, ControlType.Button, "Checkout"));
        var report = await new Testability.TestabilityAnalyzer(_worker, _inspector).AnalyzeAsync(_inspector.InspectElement(_automation, checkout)!.Element);

        var plan = report.Plan!;
        Assert.True(plan.IsWeb);
        var login = plan.Features[0];
        Assert.Equal(TestIdeas.FeatureKind.Login, login.Kind);
        Assert.Equal("Sign in", login.Action?.Element.Name);
        Assert.True(login.Cases.Count >= 30, $"{login.Cases.Count} cases");
        var script = login.Scripts.Single(s => s.Id == "playwright-py").Code;
        Assert.Contains("page.locator(\"#login-user\")", script);
        Assert.Contains("fill(\"tester@example.com\")", script);
        Assert.Contains("secret(\"UIINSPECTOR_SECRET_1\")", script);
        Assert.Contains("get_by_role(\"button\", name=\"Sign in\", exact=True)", script);
        Assert.DoesNotContain("Recorded with UiInspector", script);
        Assert.Contains(plan.Features, f => f.Kind == TestIdeas.FeatureKind.Search);
        Assert.Equal(TestIdeas.FeatureKind.Page, plan.Features[^1].Kind);
    }

    // The data-driven suites for the sign-in form and the (deliberately unsafe) comment form, saved
    // for running with pytest and Playwright Test.
    [Fact]
    public async Task TestPlan_WritesDataDrivenSuites()
    {
        var checkout = Find(w => ByName(w, ControlType.Button, "Checkout"));
        var report = await new Testability.TestabilityAnalyzer(_worker, _inspector).AnalyzeAsync(_inspector.InspectElement(_automation, checkout)!.Element);
        var plan = report.Plan!;
        var login = plan.Features.Single(f => f.Kind == TestIdeas.FeatureKind.Login);
        var comment = plan.Features.Single(f => f.Fields.Any(x => x.Label == "Comment"));

        var dir = Path.Combine(AppContext.BaseDirectory, "generated", "suites");
        Directory.CreateDirectory(dir);
        foreach (var (feature, name) in new[] { (login, "login"), (comment, "comment") })
        {
            // Every web suite language, each saved under the name its runner expects.
            Assert.Equal(["suite-playwright-py", "suite-playwright-js", "suite-playwright-cs", "suite-selenium-py", "suite-selenium-java"], feature.Suites.Select(s => s.Id));
            string Code(string id) => feature.Suites.Single(s => s.Id == id).Code;
            File.WriteAllText(Path.Combine(dir, $"{name}_test.py"), Code("suite-playwright-py"));
            File.WriteAllText(Path.Combine(dir, $"{name}.spec.js"), Code("suite-playwright-js"));
            File.WriteAllText(Path.Combine(dir, $"{name}_selenium_test.py"), Code("suite-selenium-py"));
            var pascal = char.ToUpperInvariant(name[0]) + name[1..];
            File.WriteAllText(Path.Combine(dir, $"{pascal}Suite.cs"), Code("suite-playwright-cs"));
            File.WriteAllText(Path.Combine(dir, $"{pascal}SeleniumTest.java"), Code("suite-selenium-java"));
            Assert.Contains("@pytest.mark.parametrize", Code("suite-playwright-py"));
            Assert.Contains("page.on(\"dialog\"", Code("suite-playwright-py"));
            Assert.Contains("for (const [id, title, field, value, expected] of CASES)", Code("suite-playwright-js"));
            Assert.Contains("[TestCaseSource(nameof(Cases))]", Code("suite-playwright-cs"));
            Assert.Contains("def take_alert(driver):", Code("suite-selenium-py"));
            Assert.Contains("@ParameterizedTest(name = \"{0} {1}\")", Code("suite-selenium-java"));
        }
        var loginPy = login.Suites.Single(s => s.Id == "suite-playwright-py").Code;
        Assert.Contains("page.locator(\"#login-user\").fill(values[\"login_email\"])", loginPy);
        Assert.Contains("\"a\" * 1000", loginPy);                       // long values as code, not as 1,000 letters
        Assert.Contains("def test_password_is_hidden(browser):", loginPy);
        Assert.Contains("def test_unknown_user_and_wrong_password_get_the_same_message(browser):", loginPy);
    }

    // A selected test case gets a script of its own: just that case, with its value and the automatic checks.
    [Fact]
    public async Task TestPlan_ScriptForOneCase()
    {
        var checkout = Find(w => ByName(w, ControlType.Button, "Checkout"));
        var report = await new Testability.TestabilityAnalyzer(_worker, _inspector).AnalyzeAsync(_inspector.InspectElement(_automation, checkout)!.Element);
        var plan = report.Plan!;
        var comment = plan.Features.Single(f => f.Fields.Any(x => x.Label == "Comment"));
        var login = plan.Features.Single(f => f.Kind == TestIdeas.FeatureKind.Login);

        var sql = comment.Cases.Single(c => c.Title == "Comment: SQL injection");
        var py = TestIdeas.TestSuites.ForCase(comment, sql, plan.IsWeb).Single(s => s.Id == "suite-playwright-py");
        Assert.StartsWith($"{sql.Id} · ", py.Title);
        Assert.Contains($"# Test {sql.Id} “Comment: SQL injection”", py.Code);
        Assert.Single(py.Code.Split('\n'), l => l.TrimStart().StartsWith("(\"" + "FORM-", StringComparison.Ordinal));
        Assert.Contains("\"' OR '1'='1' --\"", py.Code);
        var dir = Path.Combine(AppContext.BaseDirectory, "generated", "suites");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "one_case_test.py"), py.Code);

        // Checked by hand: no script.
        Assert.Empty(TestIdeas.TestSuites.ForCase(comment, comment.Cases.Single(c => c.Title == "Keyboard order"), plan.IsWeb));
        // The password check is a test of its own, with no data cases.
        var hidden = TestIdeas.TestSuites.ForCase(login, login.Cases.Single(c => c.Title == "Password: is hidden while typing"), plan.IsWeb)
            .Single(s => s.Id == "suite-playwright-py").Code;
        Assert.Contains("def test_password_is_hidden(browser):", hidden);
        Assert.DoesNotContain("def test_case(", hidden);
    }

    // Regression (Discord's friend search): React's useId ids change every run, so the field is found
    // by its label instead, and replay types into it.
    [Fact]
    public async Task ReactGeneratedId_IsNotUsed_TheFieldIsFoundByItsLabel()
    {
        var search = Find(w => w.FindFirstDescendant(_automation.ConditionFactory.ByAutomationId("_r_b3r_")));
        var inspection = _inspector.InspectElement(_automation, search)!;
        var path = inspection.Ancestors.Reverse().Append(inspection.Element).ToList();
        var selector = new SelectorGenerator(_inspector).Generate(_automation, path);

        var best = selector.Best!;
        Assert.DoesNotContain(best.Steps, s => s.Get(SelectorProperty.AutomationId) is not null);
        Assert.Equal("Search friends", best.Steps[^1].Get(SelectorProperty.Name)?.Value);

        var web = await WebAsync("react_search", _ => search);
        var js = WebCodeTemplates.Render(web).Single(s => s.Id == "playwright-js").Code;
        Assert.Contains("getByRole('textbox', { name: 'Search friends', exact: true })", js);
        Assert.DoesNotContain("#_r_b3r_", js); // (the target comment still names it, for the reader)

        var step = new RecordedStep { Id = 1, Action = RecordedAction.SetValue, Path = path, Text = "Ada", Selector = selector };
        var results = await new Replayer(_worker, new ReplayOptions { StepPause = TimeSpan.Zero }).RunAsync([step]);
        Assert.Equal(ReplayStatus.Passed, results[0].Status);
        var reported = Retry.WhileNull(() => Window.FindFirstDescendant(_automation.ConditionFactory.ByName("search: Ada")), TimeSpan.FromSeconds(3)).Result;
        Assert.True(reported is not null, $"status: {StatusText()}; field: {search.Patterns.Value.Pattern.Value.Value}; replay: {results[0].Message}");
    }

    private string StatusText() =>
        Window.FindAllDescendants(_automation.ConditionFactory.ByControlType(ControlType.Text))
            .Select(t => t.Name).FirstOrDefault(n => n.Contains(':') || n is "Ready") ?? "?";

    [Fact]
    public async Task Button_GetsRoleAndName_AndScriptsOpenTheirOwnBrowser()
    {
        var web = await WebAsync("checkout", w => ByName(w, ControlType.Button, "Checkout"));

        Assert.True(web.Connected, web.Problem);
        Assert.Equal(WebSource.Accessibility, web.Source);
        Assert.Equal(WebAppKind.Browser, web.Launch?.Kind);
        Assert.StartsWith("file:///", web.PageUrl);
        var step = Assert.Single(web.Best!.Steps);
        Assert.Equal(("button", "Checkout"), (step.Role, step.Name));

        var js = WebCodeTemplates.Render(web).Single(s => s.Id == "playwright-js").Code;
        Assert.Contains("chromium.launch({ channel: 'msedge', headless: false })", js);
        Assert.Contains("await page.goto('file:///", js);
        Assert.DoesNotContain("connectOverCDP", js);
    }

    [Fact]
    public async Task HtmlId_BecomesCssId()
    {
        var web = await WebAsync("email", w => ByName(w, ControlType.Edit, "Email"));
        Assert.Equal("#email-input", Assert.Single(web.Best!.Steps).Value);
        Assert.Equal("//*[@id='email-input']", web.Selenium!.Target.Value);
    }

    [Fact]
    public async Task StatusInName_UsesTheStablePart()
    {
        var web = await WebAsync("dyno", w => ByName(w, ControlType.Hyperlink, "Dyno (direct message), Online"));
        var step = web.Best!.Steps[^1];
        Assert.Equal(("link", "Dyno (direct message)", false), (step.Role, step.Name, step.Exact));
    }

    [Fact]
    public async Task DuplicateCaption_IsAnchoredToItsNamedRegion()
    {
        var web = await WebAsync("orders_delete", w => ByName(w, ControlType.Group, "Orders") is { } orders ? ByName(orders, ControlType.Button, "Delete") : null);
        Assert.Equal(("region", "Orders"), (web.Best!.Steps[0].Role, web.Best.Steps[0].Name));
        Assert.Equal("Delete", web.Best.Steps[1].Name);
    }

    [Fact]
    public async Task ButtonInIframe_EntersTheFrameByItsId()
    {
        var web = await WebAsync("pay", w => ByName(w, ControlType.Button, "Pay now"));
        Assert.Equal("#payment-frame", Assert.Single(web.Best!.Frames).Value);
        Assert.Equal("Pay now", web.Best.Steps[^1].Name);
    }

    [Fact]
    public async Task ButtonInShadowDom_IsFoundByRole()
    {
        // UI Automation flattens Shadow DOM, and getByRole pierces it.
        var web = await WebAsync("like", w => ByName(w, ControlType.Button, "Like"));
        Assert.Equal(("button", "Like"), (web.Best!.Steps[^1].Role, web.Best.Steps[^1].Name));
    }

    [Fact]
    public async Task RowWithoutId_IsTheInnermostElementHoldingItsTextAndTheButton()
    {
        var web = await WebAsync("order_1042", OrderDelete);

        // Chromium exposes the row <div>s to UI Automation or leaves them out, depending on its
        // accessibility mode. Either way the row is found through "Order 1042", never by position.
        // The same, as for an Electron app the script starts itself (end-to-end runs use a stand-in launcher).
        var asApp = web with { Launch = new WebAppLaunch(WebAppKind.Electron, "FAKE_APP_EXE", "FakeApp"), Browser = BrowserFlavor.Edge };
        foreach (var snippet in WebCodeTemplates.Render(asApp))
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "generated", $"startapp_order_1042.{snippet.Id}.txt"), snippet.Code);
        }

        var steps = string.Join(" › ", web.Best!.Steps);
        Assert.All(web.Best.Steps, s => Assert.Null(s.Index));
        var js = WebCodeTemplates.Render(web).Single(s => s.Id == "playwright-js").Code;
        if (web.Best.Steps[0].FilterHasText is not null)
        {
            // Rows exposed: the innermost element holding the text and a Delete button.
            Assert.Equal(("*", "Order 1042", true, true), (web.Best.Steps[0].Value, web.Best.Steps[0].FilterHasText, web.Best.Steps[0].FilterHasNext, web.Best.Steps[0].Last));
            Assert.Contains("[last()]", web.Selenium!.Target.Value);
            Assert.Contains(".locator('*').filter({ has: page.getByText('Order 1042', { exact: true }) })", js);
        }
        else
        {
            // Rows left out: the first Delete button after the text.
            Assert.True(web.Best.Steps[0] is { Kind: WebLocatorKind.Text, Value: "Order 1042" }, steps);
            Assert.StartsWith("following::", web.Best.Steps[1].Value);
            Assert.Contains("/following::", web.Selenium!.Target.Value);
            Assert.Contains("page.getByText('Order 1042', { exact: true }).locator('xpath=following::", js);
        }
    }

    [Fact]
    public async Task UnnamedClickableArea_IsClickedThroughTheTextInIt()
    {
        // The area itself, as smart target picks it when you click its text.
        var web = await WebAsync("tile", w =>
        {
            if (ByName(w, ControlType.Text, "Weekly summary") is not { } text) return null;
            var area = _automation.TreeWalkerFactory.GetControlViewWalker().GetParent(text);
            return area is not null && area.Name.Length == 0 ? area : null;
        });

        Assert.True(web.Connected, web.Problem);
        var step = Assert.Single(web.Best!.Steps);
        Assert.Equal((WebLocatorKind.Text, "Weekly summary"), (step.Kind, step.Value));
        Assert.Contains("normalize-space(.)='Weekly summary'", web.Selenium!.Target.Value);
    }

    // A recording in the web page: fill the email, delete order 1042, check the status. The
    // recorder is fed element events (no real input), and each action is then performed.
    [Fact]
    public async Task Recording_GetsPlaywrightAndSeleniumScripts()
    {
        AutomationElement? focused = null;
        await using var recorder = new Recorder(new InspectionService(_worker, _inspector), _inspector, new RecorderOptions
        {
            InputFactory = () => new NoInput(),
            TrackFocusEvents = false,
            ClickSettings = new ClickSettings(500, new System.Drawing.Size(4, 4), new System.Drawing.Size(4, 4)),
            FocusOverride = _ => focused,
        }) { CaptureScreenshots = false };

        var email = Find(w => ByName(w, ControlType.Edit, "Email"));
        focused = email;
        await recorder.FocusOnAsync(email);
        email.Patterns.Value.Pattern.SetValue("tester@example.com");

        var delete = Find(OrderDelete);
        await recorder.ClickOnAsync(delete, time: 1000);
        delete.Patterns.Invoke.Pattern.Invoke();
        focused = delete;

        // Found by its text: Chromium may or may not expose the #status <div> itself.
        var status = Retry.WhileNull(() => ByName(Window, ControlType.Text, "deleted 1042"), TimeSpan.FromSeconds(10)).Result
                     ?? throw new InvalidOperationException("The status didn't become \"deleted 1042\" after the Delete button was invoked.");
        await recorder.CheckOnAsync(status);

        IReadOnlyList<RecordedStep> steps = [];
        for (var i = 0; i < 300 && (steps = recorder.Steps).Any(s => s.IsGenerating); i++) await Task.Delay(50);

        Assert.Equal([RecordedAction.SetValue, RecordedAction.Click, RecordedAction.CheckText], steps.Select(s => s.Action));
        Assert.All(steps, s => Assert.True(s.Web?.Connected, s.Web?.Problem));

        var scripts = recorder.RenderScripts();
        Assert.Equal(["flaui", "uiautomation", "pywinauto", "appium", "playwright-js", "playwright-py", "playwright-cs", "selenium-py", "selenium-java"], scripts.Select(s => s.Id));
        var js = scripts.Single(s => s.Id == "playwright-js").Code;
        Assert.Contains("await target.fill('tester@example.com');", js);
        Assert.Contains("await target.click();", js);
        Assert.Contains("if (actual !== 'deleted 1042')", js);

        var dir = Path.Combine(AppContext.BaseDirectory, "generated");
        foreach (var script in scripts) File.WriteAllText(Path.Combine(dir, $"recorded_web.{script.Id}.txt"), script.Code);
    }
}

public class WebScriptStartModeTests
{
    private static WebResult App(WebAppKind kind, string exe, string process) => new()
    {
        Connected = true,
        Source = WebSource.Accessibility,
        PageUrl = "https://app.example.com/orders",
        UrlPrefix = "https://app.example.com/",
        PageTitle = "Orders",
        Launch = new WebAppLaunch(kind, exe, process),
        Element = new WebElementSummary { Tag = "button", Role = "button" },
        Best = new WebSelector
        {
            Steps = [new WebLocator { Kind = WebLocatorKind.Role, Role = "button", Name = "Save" }],
            Strategy = "Role + name",
            Score = 80,
            Verification = new SelectorVerification(VerificationStatus.Unique, 1),
        },
        Selenium = new SeleniumSelector { Target = new WebLocator { Kind = WebLocatorKind.XPath, Value = "//*[@role='button']" }, Verification = new SelectorVerification(VerificationStatus.Unique, 1) },
    };

    private static string Code(WebResult web, string id) => WebCodeTemplates.Render(web).Single(s => s.Id == id).Code;

    // A page in Firefox: every script opens its own Firefox (Playwright's build, Selenium's geckodriver).
    [Fact]
    public void FirefoxPage_GetsFirefoxScriptsInEveryLanguage()
    {
        var web = App(WebAppKind.Browser, @"C:\Program Files\Mozilla Firefox\firefox.exe", "firefox") with { Browser = BrowserFlavor.Firefox };

        var js = Code(web, "playwright-js");
        Assert.Contains("const { firefox } = require('playwright');", js);
        Assert.Contains("await firefox.launch({ headless: false })", js);
        Assert.Contains("npx playwright install firefox", js);
        Assert.Contains("Opens its own Firefox window", js);
        Assert.DoesNotContain("chromium", js);

        var py = Code(web, "playwright-py");
        Assert.Contains("browser = p.firefox.launch(headless=False)", py);
        Assert.Contains("page.goto(\"https://app.example.com/orders\")", py);
        Assert.Contains("playwright.Firefox.LaunchAsync(new() { Headless = false })", Code(web, "playwright-cs"));
        Assert.Contains("webdriver.Firefox()", Code(web, "selenium-py"));
        var java = Code(web, "selenium-java");
        Assert.Contains("import org.openqa.selenium.firefox.FirefoxDriver;", java);
        Assert.Contains("new FirefoxDriver(", java);
    }

    [Fact]
    public void ElectronApp_IsStartedWithAPortByTheScript()
    {
        var web = App(WebAppKind.Electron, @"C:\Apps\Orders\Orders.exe", "Orders");

        var js = Code(web, "playwright-js");
        Assert.Contains(@"spawn(String.raw`C:\Apps\Orders\Orders.exe`, ['--remote-debugging-port=9222']", js);
        Assert.Contains("await connect('http://127.0.0.1:9222')", js);
        Assert.Contains("Close Orders completely first", js);

        var py = Code(web, "playwright-py");
        Assert.Contains(@"subprocess.Popen([r""C:\Apps\Orders\Orders.exe"", ""--remote-debugging-port=9222""])", py);

        Assert.Contains(@"new System.Diagnostics.ProcessStartInfo(@""C:\Apps\Orders\Orders.exe"")", Code(web, "playwright-cs"));
        Assert.Contains("By.XPATH", Code(web, "selenium-py"));
        Assert.Contains("By.xpath", Code(web, "selenium-java"));
    }

    // Discord, Slack…: …\Discord\app-1.0.9267\Discord.exe changes with each update; Update.exe doesn't.
    [Fact]
    public void SquirrelApp_IsStartedThroughUpdateExe_AndScriptsCheckForARunningCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "uiinspector-squirrel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "app-1.0.9267"));
        File.WriteAllText(Path.Combine(root, "app-1.0.9267", "Discord.exe"), "");
        File.WriteAllText(Path.Combine(root, "Update.exe"), "");
        try
        {
            var launch = WebApps.Squirrel(new WebAppLaunch(WebAppKind.Electron, Path.Combine(root, "app-1.0.9267", "Discord.exe"), "Discord"));
            Assert.Equal(Path.Combine(root, "Update.exe"), launch.ExePath);
            Assert.Equal("Discord.exe", launch.SquirrelExe);

            var web = App(WebAppKind.Electron, launch.ExePath!, "Discord") with { Launch = launch };
            var py = Code(web, "playwright-py");
            Assert.Contains("Update.exe\", \"--processStart\", \"Discord.exe\", \"--process-start-args\", \"--remote-debugging-port=9222\"])", py);
            Assert.Contains("if is_running(\"Discord.exe\"):", py);
            Assert.Contains("Discord is running without a debugging port. Quit it completely", py);
            Assert.Contains("isRunning('Discord.exe')", Code(web, "playwright-js"));
            Assert.Contains("GetProcessesByName(\"Discord\")", Code(web, "playwright-cs"));
            Assert.Contains("if not port_open(9222):", Code(web, "selenium-py"));
            Assert.Contains("endsWith(\"\\\\discord.exe\")", Code(web, "selenium-java"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WebView2App_GetsThePortThroughTheEnvironment()
    {
        var js = Code(App(WebAppKind.WebView2, @"C:\Apps\Host.exe", "Host"), "playwright-js");
        Assert.Contains("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=9222'", js);
    }

    [Fact]
    public void AppAlreadyOnADebugPort_IsAttachedTo()
    {
        var js = Code(App(WebAppKind.Electron, @"C:\Apps\Orders\Orders.exe", "Orders") with { Port = 9333 }, "playwright-js");
        Assert.Contains("chromium.connectOverCDP('http://127.0.0.1:9333')", js);
        Assert.DoesNotContain("spawn(", js);
    }
}

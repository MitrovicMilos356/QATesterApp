using UiInspector.Core.Recording;
using UiInspector.Core.Reporting;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Tests;

public class BugReportTests
{
    // A maximized Chromium window painted by itself comes out black except its frame: that's not a
    // picture to keep. Pages that are white, or dark themes like Discord's, are.
    [Theory]
    [InlineData(0x000000, true)]
    [InlineData(0x0A0A0A, true)]
    [InlineData(0xFFFFFF, false)]
    [InlineData(0x313338, false)]
    public void MostlyBlackCapture_IsRecognized(int background, bool expected)
    {
        using var image = new System.Drawing.Bitmap(400, 300);
        using (var g = System.Drawing.Graphics.FromImage(image))
        {
            g.Clear(System.Drawing.Color.FromArgb(255, System.Drawing.Color.FromArgb(background)));
            g.FillRectangle(System.Drawing.Brushes.White, 0, 0, 400, 30);       // a title bar
            g.DrawRectangle(System.Drawing.Pens.Red, 50, 80, 120, 40);          // an outline drawn on it
        }
        Assert.Equal(expected, WindowCapture.IsMostlyBlack(image, new System.Drawing.Rectangle(0, 0, 400, 300)));
    }

    // Stand-ins for screenshots: distinct bytes, so tests can see which image went where.
    private static StepScreenshot Shot(byte marker) => new([0x89, 0x50, marker, 1], [0x89, 0x50, marker, 2], 10, 10);

    private static readonly IReadOnlyList<RecordedStep> Steps =
    [
        new() { Id = 1, Action = RecordedAction.SetValue, Path = Fake.Path("Edit", "User"), Text = "ada", Screenshot = Shot(1) },
        new() { Id = 2, Action = RecordedAction.SetValue, Path = Fake.Path("Edit", "Password", password: true), IsSecret = true, Screenshot = Shot(2) },
        new() { Id = 3, Action = RecordedAction.Click, Path = Fake.Path("Button", "Sign in"), Screenshot = Shot(3) },
        new() { Id = 4, Action = RecordedAction.PressKey, Path = Fake.Path("List", "Orders"), Key = new KeyChord(0x28), Repeat = 3 },
    ];

    private static readonly EnvironmentInfo Environment = new()
    {
        Windows = "Windows 11 Pro 23H2 (build 22631.3447, 64-bit)",
        Apps = [new AppInfo("app", "Orders App", "2.4.1", @"C:\Apps\Orders\app.exe")],
        Monitors = [new MonitorInfo(1920, 1080, 100, true), new MonitorInfo(2560, 1440, 150, false)],
        Language = "English (United States), en-US",
        RecordedAt = new DateTime(2026, 11, 5, 21, 40, 0),
        TimeZone = "(UTC+01:00) Belgrade",
    };

    private static readonly BugReport Report = new()
    {
        Title = "Sign-in shows an error for a valid user",
        Severity = Severity.High,
        Expected = "The order list opens.",
        Actual = "\"Unknown error\" appears.",
        Notes = "Only happens on the first try.",
        Steps = Steps,
        Environment = Environment,
        Scripts = RecordingScripts.Render(Steps),
    };

    private static string Html(BugReportOptions? options = null) => BugReportRenderer.Html(Report, options ?? new BugReportOptions());

    private static string Base64(byte[] png) => Convert.ToBase64String(png);

    [Fact]
    public void Default_HasStepsInTesterWordsWithHighlightedScreenshots()
    {
        var html = Html();

        Assert.Contains("Type “ada” into the “User” field.", html);
        Assert.Contains("Type the password into the “Password” field.", html);
        Assert.Contains("Click the “Sign in” button.", html);
        Assert.Contains("Press Down 3 times in the “Orders” list.", html);
        Assert.Contains("In the “Orders - App” window (app):", html); // once, at the first step in that window
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "Orders - App” window"));

        Assert.Contains(Base64(Shot(3).HighlightedPng), html);
        Assert.DoesNotContain(Base64(Shot(3).Png), html);
        Assert.Contains("severity sev-High", html);
        Assert.Contains("The order list opens.", html);
    }

    [Fact]
    public void EachOptionRemovesItsPart()
    {
        Assert.DoesNotContain("Steps to reproduce", Html(new() { Steps = false }));
        Assert.DoesNotContain("data:image/png", Html(new() { Screenshots = false }));
        Assert.Contains(Base64(Shot(3).Png), Html(new() { HighlightElement = false }));
        Assert.DoesNotContain("Expected result", Html(new() { ExpectedActual = false }));
        Assert.DoesNotContain("severity sev-High", Html(new() { Severity = false }));
        Assert.DoesNotContain("first try", Html(new() { Notes = false }));
        Assert.DoesNotContain("<h2>Environment</h2>", Html(new() { Environment = false }));

        Assert.DoesNotContain("Windows 11", Html(new() { WindowsVersion = false }));
        Assert.DoesNotContain("Orders App 2.4.1", Html(new() { AppVersion = false }));
        Assert.DoesNotContain("2560×1440", Html(new() { Screens = false }));
        Assert.DoesNotContain("en-US", Html(new() { Language = false }));
        Assert.DoesNotContain("Belgrade", Html(new() { DateTime = false }));
    }

    [Fact]
    public void OffByDefault_AppPathScriptAndElementDetails()
    {
        var html = Html();
        Assert.DoesNotContain(@"C:\Apps\Orders\app.exe", html);
        Assert.DoesNotContain("Automation script", html);
        Assert.DoesNotContain("Element details", html);

        var full = Html(new() { AppPath = true, Script = true, ScriptLanguage = "pywinauto", ElementDetails = true });
        Assert.Contains(@"C:\Apps\Orders\app.exe", full);
        Assert.Contains("Automation script (Python · pywinauto)", full);
        Assert.Contains("from pywinauto import Desktop", full);
        Assert.Contains("Element details", full);
    }

    [Fact]
    public void SingleScreenshots_CanBeLeftOut()
    {
        var html = Html(new() { HiddenScreenshots = new HashSet<int> { 1 } });
        Assert.DoesNotContain(Base64(Shot(1).HighlightedPng), html);
        Assert.Contains(Base64(Shot(2).HighlightedPng), html);
    }

    [Fact]
    public void TesterTextIsEscaped_AndEmptyFieldsAreFlagged()
    {
        var html = BugReportRenderer.Html(Report with { Title = "<script>alert(1)</script>", Expected = "" }, new BugReportOptions());
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Not filled in yet.", html);
    }

    [Fact]
    public void Markdown_SavesScreenshotsAsFiles_TextHasNone()
    {
        var markdown = BugReportRenderer.Markdown(Report, new BugReportOptions(), "bug_files");
        Assert.Equal(3, markdown.Files.Count);
        Assert.Contains("![Step 3](bug_files/step-03.png)", markdown.Text);
        Assert.Equal(Shot(3).HighlightedPng, markdown.Files["bug_files/step-03.png"]);
        Assert.Contains("**Severity:** High", markdown.Text);
        Assert.Contains("- **App:** Orders App 2.4.1", markdown.Text);

        var text = BugReportRenderer.Text(Report, new BugReportOptions());
        Assert.DoesNotContain("![", text);
        Assert.Contains("3 screenshots in the saved report", text);
        Assert.Contains("3. Click the “Sign in” button.", text);
    }

    [Fact]
    public void Wording_NamesElementsTheWayTestersDo()
    {
        Assert.Equal("the “Remember me” check box", StepWording.Element(new ElementInfo { ControlType = "CheckBox", Name = "Remember me" }));
        Assert.Equal("the link", StepWording.Element(new ElementInfo { ControlType = "Hyperlink" }));
        var check = new RecordedStep { Id = 9, Action = RecordedAction.CheckText, Path = Fake.Path("Text", "Status"), Text = "Saved" };
        Assert.Equal("Check that the “Status” text shows “Saved”.", StepWording.Describe(check));
    }

    [Fact]
    public void Environment_IsReadFromThisMachine()
    {
        // This test process stands in for the app under test.
        var self = new RecordedStep { Id = 1, Action = RecordedAction.Click, Path = [new ElementInfo { ProcessId = System.Environment.ProcessId, ProcessName = "testhost" }] };
        var env = EnvironmentInfo.Collect([self], DateTime.Now);
        var app = Assert.Single(env.Apps);
        Assert.NotNull(app.Version);
        Assert.DoesNotContain("Operating System", app.ProductName ?? "");
        Assert.StartsWith("Windows", env.Windows);
        Assert.Contains("build", env.Windows);
        Assert.NotEmpty(env.Monitors);
        Assert.All(env.Monitors, m => Assert.True(m.Width > 0 && m.ScalePercent >= 100));
    }
}

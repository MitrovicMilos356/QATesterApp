using UiInspector.Core.Testability;
using UiInspector.Core.TestIdeas;

namespace UiInspector.Core.Tests;

public class TestPlanTests
{
    private int _nextId = 1;

    private ElementNode Node(string type, string name = "", string automationId = "", bool password = false, string role = "", string[]? patterns = null, params ElementNode[] children) =>
        new(new ElementInfo
        {
            ControlType = type, Name = name, AutomationId = automationId, IsPassword = password, AriaRole = role, FrameworkId = "Chrome",
            RuntimeId = [42, _nextId++], Patterns = patterns ?? [], IsKeyboardFocusable = true,
            BoundingRectangle = new System.Drawing.Rectangle(0, 0, 100, 20),
        }, children);

    private static TestabilityReport EmptyReport(ElementNode root) => new() { Window = root.Info, Findings = [] };

    private static readonly ElementInfo Desktop = new() { ControlType = "Pane", Name = "Desktop", RuntimeId = [42, 0] };

    private List<TestFeature> Detect(ElementNode page)
    {
        var window = new ElementInfo { ControlType = "Window", Name = "Shop", RuntimeId = [42, 999] };
        return FeatureDetector.Detect(page, [Desktop, window], EmptyReport(page));
    }

    [Theory]
    [InlineData("Email", FieldKind.Email)]
    [InlineData("E-pošta", FieldKind.Email)]
    [InlineData("Lozinka", FieldKind.Password)]
    [InlineData("Broj telefona", FieldKind.Phone)]
    [InlineData("Datum rođenja", FieldKind.Date)]
    [InlineData("Ime i prezime", FieldKind.PersonName)]
    [InlineData("Pretraži oglase", FieldKind.Search)]
    [InlineData("Website", FieldKind.Url)]
    [InlineData("Quantity", FieldKind.Number)]
    [InlineData("Poruka", FieldKind.MultiLine)]
    [InlineData("Company", FieldKind.Text)]
    public void Fields_AreSortedByWhatTheyreFor(string label, FieldKind expected)
    {
        Assert.Equal(expected, FeatureDetector.Classify(new ElementInfo { ControlType = "Edit", Name = label }));
    }

    [Fact]
    public void PasswordBox_IsAPassword_WhateverItsLabel()
    {
        Assert.Equal(FieldKind.Password, FeatureDetector.Classify(new ElementInfo { ControlType = "Edit", Name = "Secret", IsPassword = true }));
    }

    [Fact]
    public void LoginForm_IsFound_WithItsFieldsAndButton()
    {
        var page = Node("Document", "Shop", "RootWebArea", children:
        [
            Node("Group", "Sign in", role: "form", children:
            [
                Node("Edit", "Email", "email"),
                Node("Edit", "Password", "pwd", password: true),
                Node("Button", "Forgot?"),
                Node("Button", "Sign in"),
            ]),
        ]);

        var login = Assert.Single(Detect(page), f => f.Kind == FeatureKind.Login);
        Assert.Equal([FieldKind.Email, FieldKind.Password], login.Fields.Select(f => f.Kind));
        Assert.Equal("Sign in", login.Action?.Element.Name);
    }

    [Fact]
    public void TwoPasswords_MakeARegistration()
    {
        var page = Node("Document", "Shop", "RootWebArea", children:
        [
            Node("Group", "", children:
            [
                Node("Edit", "Email"), Node("Edit", "Password", password: true), Node("Edit", "Repeat password", password: true), Node("Button", "Create account"),
            ]),
        ]);

        var signup = Assert.Single(Detect(page), f => f.Kind == FeatureKind.Registration);
        Assert.Equal("Create account", signup.Action?.Element.Name);
        Assert.Contains(TestCaseCatalog.For(signup, isWeb: true), c => c.Title == "Passwords don't match");
    }

    [Fact]
    public void SearchFiltersPagingAndNavigation_AreFound()
    {
        var page = Node("Document", "Jobs", "RootWebArea", children:
        [
            Node("Group", "", role: "navigation", children: [Node("Hyperlink", "Jobs"), Node("Hyperlink", "Companies"), Node("Hyperlink", "About")]),
            Node("Group", "", role: "search", children: [Node("Edit", "Search jobs"), Node("Button", "Search")]),
            Node("Group", "", children: [Node("CheckBox", "Remote"), Node("CheckBox", "Full time"), Node("Button", "Programiranje [77]")]),
            Node("Group", "", children: [Node("Hyperlink", "1"), Node("Hyperlink", "2"), Node("Hyperlink", "Next")]),
        ]);

        var features = Detect(page);
        Assert.Equal("Search", Assert.Single(features, f => f.Kind == FeatureKind.Search).Action?.Element.Name);
        Assert.Equal(3, Assert.Single(features, f => f.Kind == FeatureKind.Filters).ControlCount);
        Assert.Equal(3, Assert.Single(features, f => f.Kind == FeatureKind.Paging).ControlCount);
        Assert.Equal(3, Assert.Single(features, f => f.Kind == FeatureKind.Navigation).ControlCount);
        Assert.Equal(FeatureKind.Page, features[^1].Kind);
        // Most important first.
        Assert.True(features.IndexOf(features.First(f => f.Kind == FeatureKind.Search)) < features.IndexOf(features.First(f => f.Kind == FeatureKind.Navigation)));
    }

    // Creating a password: the rules apply, with the edge cases a tester would otherwise have to think up.
    [Fact]
    public void SignUp_PasswordAndEmailCases_CoverTheEdgeCases()
    {
        var page = Node("Document", "Shop", "RootWebArea", children:
        [
            Node("Group", "", children:
                [Node("Edit", "Email"), Node("Edit", "Password", password: true), Node("Edit", "Repeat password", password: true), Node("Button", "Create account")]),
        ]);
        var signup = Detect(page).Single(f => f.Kind == FeatureKind.Registration);

        var cases = TestCaseCatalog.For(signup, isWeb: true);

        Assert.True(cases.Count >= 60, $"{cases.Count} cases");
        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count());
        Assert.All(cases, c => Assert.NotEmpty(c.Steps));
        Assert.All(cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Expected)));
        TestCase Case(string title) => cases.First(c => c.Title == $"Password: {title}");
        Assert.Equal("!@#$%^&*()", Case("only symbols").Value);
        Assert.Equal("12345678", Case("only digits").Value);
        Assert.Equal("Pass😀word1", Case("emoji").Value);
        // What's shown and what's typed: the long text itself, the spaces without the quotes around them.
        Assert.Equal(new string('a', 1000), Case("very long").Value);
        Assert.Equal("        ", Case("only spaces").Value);
        Assert.Equal(" Valid#Pass2024 ", Case("spaces at start and end").Value);
        // Only the tester knows their minimum length: described, nothing to copy.
        Assert.Null(Case("one shorter than the minimum").Value);
        Assert.Contains(cases, c => c.Title == "Email: missing @" && c.Value == "tester.example.com");
        Assert.Equal(254, cases.First(c => c.Title == "Email: longest allowed (254 characters)").Value!.Length);
        Assert.Contains(cases, c => c.Category == TestCategory.Accessibility);
        // Field cases point at their field, so the app can outline it.
        Assert.Equal("Repeat password", cases.First(c => c.Title.StartsWith("Repeat password:")).Target?.Element.Name);
    }

    // A login says yes or no: strength rules and email formats don't apply there.
    [Fact]
    public void Login_ExpectsTheGeneralMessage_NotFormatOrStrengthRules()
    {
        var page = Node("Document", "Shop", "RootWebArea", children:
            [Node("Group", "", children: [Node("Edit", "Email"), Node("Edit", "Password", password: true), Node("Button", "Log in")])]);
        var login = Detect(page).Single(f => f.Kind == FeatureKind.Login);

        var cases = TestCaseCatalog.For(login, isWeb: true);

        Assert.True(cases.Count >= 30, $"{cases.Count} cases");
        Assert.DoesNotContain(cases, c => c.Title is "Password: only digits" or "Password: common password" or "Email: missing @");
        Assert.Contains("same general message", cases.First(c => c.Title == "Password: very long").Expected);
        Assert.Contains(cases, c => c.Title == "User that doesn't exist" && c.Expected.Contains("same message"));
        Assert.Contains(cases, c => c.Title == "Password: is hidden while typing");
        // Steps name the controls found on the page.
        Assert.Contains(cases, c => c.Steps.Any(s => s.Contains("“Log in” button")));
    }

    // Security cases say what a failure means, so a tester who sees the alert knows what they found.
    [Fact]
    public void SecurityCases_SayWhatAFailureMeans()
    {
        var page = Node("Document", "Jobs", "RootWebArea", children: [Node("Group", "", role: "search", children: [Node("Edit", "Search jobs"), Node("Button", "Search")])]);
        var search = Detect(page).Single(f => f.Kind == FeatureKind.Search);

        var cases = TestCaseCatalog.For(search, isWeb: true);

        var script = cases.Single(c => c.Title == "Search jobs: HTML and script");
        Assert.Equal("<script>alert('x')</script>", script.Value);
        Assert.Contains("cross-site scripting (XSS)", script.IfItFails);
        Assert.Contains("reflected", script.IfItFails);
        Assert.Contains("SQL injection", cases.Single(c => c.Title == "Search jobs: SQL injection").IfItFails);
        Assert.All(cases.Where(c => c.Category == TestCategory.Security), c => Assert.False(string.IsNullOrWhiteSpace(c.IfItFails), c.Title));
    }

    [Fact]
    public void Csv_HasOneRowPerCase_AndIsSafeToOpenInExcel()
    {
        var page = Node("Document", "Shop", "RootWebArea", children:
            [Node("Group", "", children: [Node("Edit", "Quantity"), Node("Button", "Order")])]);
        var form = Detect(page).Single(f => f.Kind == FeatureKind.Form);
        var plan = new TestPlan { Window = page.Info, Features = [form with { Cases = TestCaseCatalog.For(form, isWeb: true) }], IsWeb = true };

        var csv = TestPlanRenderer.Csv(plan);

        Assert.StartsWith("\uFEFFID,Feature,Title,Kind,Priority,Steps,Test data,Exact value,Expected result,If it fails,Automate\r\n", csv);
        Assert.Contains("\"1. ", csv);            // steps numbered, in one quoted cell
        Assert.Contains(",'-1,", csv);           // "-1" would run as a formula in Excel
        Assert.DoesNotContain(",-1,", csv);
        var html = TestPlanRenderer.Html(plan);
        Assert.Contains("<code>-1</code>", html);
        Assert.Contains("Bad input", html);
    }
}

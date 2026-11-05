using UiInspector.Core.Selectors;

namespace UiInspector.Core.Tests;

public class CodeTemplateTests
{
    private static SelectorCondition C(SelectorProperty p, string v, MatchKind m = MatchKind.Exact) => new(p, v, m);

    private static readonly SelectorStep DiscordWindow = new(
    [
        C(SelectorProperty.ControlType, "Window"),
        C(SelectorProperty.ClassName, "Chrome_WidgetWin_1"),
        C(SelectorProperty.Name, "Discord", MatchKind.Contains),
    ]);

    // List "Direct Messages" → Hyperlink containing "Dyno (direct message)".
    private static readonly Selector DiscordChat = new()
    {
        Window = DiscordWindow,
        Steps =
        [
            new([C(SelectorProperty.ControlType, "List"), C(SelectorProperty.Name, "Direct Messages")]),
            new([C(SelectorProperty.ControlType, "Hyperlink"), C(SelectorProperty.Name, "Dyno (direct message)", MatchKind.Contains)]),
        ],
        Strategy = "Name (stable part) inside List \"Direct Messages\"",
        Score = 60,
        Verification = new(VerificationStatus.Unique, 1),
        Warnings = ["Matches only \"Dyno (direct message)\": the rest (\"Online\") looks like a status that can change."],
    };

    private static SelectorResult ResultFor(ElementInfo target) => new() { Target = target, ProcessName = "Discord" };

    private static readonly ElementInfo ChatLink = new()
    {
        ControlType = "Hyperlink",
        Name = "Dyno (direct message), Online",
        Patterns = ["Invoke", "LegacyIAccessible"],
    };

    private static string Code(Selector selector, ElementInfo target, string id) =>
        CodeTemplates.Render(selector, ResultFor(target)).Single(s => s.Id == id).Code;

    [Fact]
    public void FlaUi_ScopesThroughAnchorAndInvokes()
    {
        var code = Code(DiscordChat, ChatLink, "flaui");

        Assert.Contains("automation.GetDesktop().FindAllChildren(cf.ByControlType(ControlType.Window).And(cf.ByClassName(\"Chrome_WidgetWin_1\"))).Where(e => e.Name.Contains(\"Discord\")).FirstOrDefault()", code);
        Assert.Contains("window.FindFirstDescendant(cf.ByControlType(ControlType.List).And(cf.ByName(\"Direct Messages\")))", code);
        Assert.Contains("scope1.FindAllDescendants(cf.ByControlType(ControlType.Hyperlink)).Where(e => e.Name.Contains(\"Dyno (direct message)\")).FirstOrDefault()", code);
        Assert.Contains("target.Patterns.Invoke.Pattern.Invoke();", code);
        Assert.Contains("// Note: Matches only", code);
    }

    [Fact]
    public void PythonUiAutomation_UsesSubNameForPartialNames()
    {
        var code = Code(DiscordChat, ChatLink, "uiautomation");

        Assert.Contains("window = auto.WindowControl(searchDepth=1, ClassName='Chrome_WidgetWin_1', SubName='Discord')", code);
        Assert.Contains("scope1 = window.ListControl(Name='Direct Messages')", code);
        Assert.Contains("target = scope1.HyperlinkControl(SubName='Dyno (direct message)')", code);
        Assert.Contains("target.GetInvokePattern().Invoke()", code);
    }

    [Fact]
    public void Pywinauto_EscapesRegexForPartialNames()
    {
        var code = Code(DiscordChat, ChatLink, "pywinauto");

        // Parentheses must be escaped inside title_re, and backslashes doubled for the Python string.
        Assert.Contains(@"title_re='.*Dyno\\ \\(direct\\ message\\).*'", code);
        Assert.Contains("target.iface_invoke.Invoke()", code);
    }

    [Fact]
    public void Appium_BuildsXPathFromDesktopRoot()
    {
        var code = Code(DiscordChat, ChatLink, "appium");

        Assert.Contains("/Pane[@ClassName=\\'#32769\\']/Window[@ClassName=\\'Chrome_WidgetWin_1\\'][contains(@Name,\\'Discord\\')]", code);
        Assert.Contains(".//Hyperlink[contains(@Name,\\'Dyno (direct message)\\')]", code);
        Assert.Contains("target.click()", code);
    }

    [Fact]
    public void Index_IsZeroBasedForFlaUiAndPywinautoButOneBasedForUiAutomation()
    {
        var selector = DiscordChat with
        {
            Steps = [new([C(SelectorProperty.ControlType, "Button"), C(SelectorProperty.Name, "Action")], Index: 2)],
        };
        var button = new ElementInfo { ControlType = "Button", Name = "Action", Patterns = ["Invoke"] };

        Assert.Contains(".ElementAtOrDefault(2)", Code(selector, button, "flaui"));
        Assert.Contains("found_index=2", Code(selector, button, "pywinauto"));
        Assert.Contains("foundIndex=3", Code(selector, button, "uiautomation"));
        Assert.Contains("find_elements(AppiumBy.XPATH, './/Button[@Name=\\'Action\\']')[2]", Code(selector, button, "appium"));
    }

    [Fact]
    public void RowFoundByItsText_IsWrittenInEachLibrarysSyntax()
    {
        var selector = DiscordChat with
        {
            Steps =
            [
                new([C(SelectorProperty.ControlType, "Pane")]) { Has = new([C(SelectorProperty.ControlType, "Text"), C(SelectorProperty.Name, "Order 1042")]) },
                new([C(SelectorProperty.ControlType, "Button"), C(SelectorProperty.Name, "Delete")]),
            ],
        };
        var button = new ElementInfo { ControlType = "Button", Name = "Delete", Patterns = ["Invoke"] };

        Assert.Contains("window.FindAllDescendants(cf.ByControlType(ControlType.Pane)).Where(e => e.FindFirstDescendant(cf.ByControlType(ControlType.Text).And(cf.ByName(\"Order 1042\"))) != null).FirstOrDefault()",
            Code(selector, button, "flaui"));
        Assert.Contains("scope1 = window.PaneControl(Compare=lambda c, d: c.TextControl(Name='Order 1042').Exists(0, 0))", Code(selector, button, "uiautomation"));
        Assert.Contains("child_window(control_type='Pane', predicate_func=lambda e: len(e.descendants(control_type='Text', title='Order 1042')) > 0)", Code(selector, button, "pywinauto"));
        Assert.Contains(".//Pane[.//Text[@Name=\\'Order 1042\\']]", Code(selector, button, "appium"));
        Assert.Equal("Pane containing Text [Name = \"Order 1042\"]", selector.Steps[0].ToString());
    }

    [Fact]
    public void QuotesAndBackslashesAreEscapedPerLanguage()
    {
        var selector = DiscordChat with
        {
            Steps = [new([C(SelectorProperty.ControlType, "Button"), C(SelectorProperty.Name, "Say \"hi\" it's C:\\x")])],
        };
        var button = new ElementInfo { ControlType = "Button", Name = "x", Patterns = ["Invoke"] };

        Assert.Contains("cf.ByName(\"Say \\\"hi\\\" it's C:\\\\x\")", Code(selector, button, "flaui"));
        Assert.Contains("Name='Say \"hi\" it\\'s C:\\\\x'", Code(selector, button, "uiautomation"));
        // XPath with both quote kinds falls back to concat().
        Assert.Contains("concat(", Code(selector, button, "appium"));
    }

    [Theory]
    [InlineData(new[] { "Invoke", "LegacyIAccessible" }, "Button", ElementAction.Invoke)]
    [InlineData(new[] { "Toggle" }, "CheckBox", ElementAction.Toggle)]
    [InlineData(new[] { "SelectionItem" }, "ListItem", ElementAction.Select)]
    [InlineData(new[] { "Value", "Text" }, "Document", ElementAction.SetValue)] // Notepad's editor
    [InlineData(new[] { "LegacyIAccessible" }, "Text", ElementAction.Click)]
    public void ChoosesPatternActionBeforeMouseClick(string[] patterns, string controlType, ElementAction expected)
    {
        var element = new ElementInfo { ControlType = controlType, Patterns = patterns };
        Assert.Equal(expected, CodeTemplates.ChooseAction(element));
    }

    [Fact]
    public void PasswordFieldsNeverGetSampleTextTypedIn()
    {
        var password = new ElementInfo { ControlType = "Edit", Patterns = ["Value"], IsPassword = true };
        Assert.Equal(ElementAction.Click, CodeTemplates.ChooseAction(password));
    }
}

using UiInspector.Core.Selectors;

namespace UiInspector.Core.Tests;

public class AttributeStabilityTests
{
    [Theory]
    [InlineData("GoAway 11:21 PM", "GoAway")]          // Discord's message header
    [InlineData("Edited 05/11/2026 14:02", "Edited")]
    [InlineData("Ada, Today at 9:05", "Ada")]
    public void TimesAndDates_AreDroppedFromNames(string name, string expected)
    {
        var verdict = AttributeStability.AnalyzeName(name);
        Assert.Equal(expected, verdict.Value);
        Assert.Equal(MatchKind.Contains, verdict.Match);
    }

    [Fact]
    public void ANameThatIsOnlyATime_IsUnusable()
    {
        Assert.False(AttributeStability.AnalyzeName("11:21 PM").IsUsable);
        Assert.False(AttributeStability.AnalyzeLandmarkText("Today at 9:05").IsUsable);
    }

    [Fact]
    public void LandmarkText_KeepsNumbers_BecauseTheyAreTheIdentity()
    {
        var order = AttributeStability.AnalyzeLandmarkText("Order 1042");
        Assert.Equal("Order 1042", order.Value);
        Assert.Equal(MatchKind.Exact, order.Match);
        Assert.Contains("test data", order.Warning);

        Assert.Equal(Stability.Stable, AttributeStability.AnalyzeLandmarkText("Invoices").Stability);
        Assert.Equal("Dyno", AttributeStability.AnalyzeLandmarkText("Dyno, Online").Value); // status suffix dropped
        Assert.False(AttributeStability.AnalyzeLandmarkText("3f2504e0-4f89-11d3-9a0c-0305e82c3301").IsUsable);
        Assert.False(AttributeStability.AnalyzeLandmarkText(new string('x', 60)).IsUsable);
    }

    [Theory]
    [InlineData("checkoutBtn")]
    [InlineData("num7Button")]       // Calculator
    [InlineData("go-button")]        // HTML id via Chromium
    [InlineData("firefox-view-button")]
    [InlineData("user_admin2")]      // not hex, so not a hash
    [InlineData("RootWebArea")]
    [InlineData("form:results:table")]    // JSF ids use colons, and are stable
    [InlineData("search_r_field_")]
    [InlineData("matrix-3")]
    public void StableAutomationIds(string id)
    {
        Assert.Equal(Stability.Stable, AttributeStability.AnalyzeAutomationId(id).Stability);
    }

    [Theory]
    [InlineData("3F2504E0-4F89-11D3-9A0C-0305E82C3301")]
    [InlineData("{3f2504e0-4f89-11d3-9a0c-0305e82c3301}")]
    [InlineData("btn__972a0")]
    [InlineData("css-1q2w3e")]
    [InlineData("_r_b3r_")]               // React 19's useId: Discord's friend search box
    [InlineData(":r1a:")]                 // React 18
    [InlineData("«r5»")]
    [InlineData("radix-:r2:")]
    [InlineData(":r4:-form-item")]
    [InlineData("mui-12345")]
    [InlineData("mat-input-0")]
    [InlineData("ember412")]
    [InlineData("ui-id-3")]
    [InlineData("s2id_autogen7")]          // Select2 (helloworld.rs filter inputs)
    [InlineData("select2-city-result-4x7k-12")]
    [InlineData("")]
    public void UnusableAutomationIds(string id)
    {
        Assert.Equal(Stability.Unusable, AttributeStability.AnalyzeAutomationId(id).Stability);
    }

    [Theory]
    [InlineData("15")]          // Notepad's editor: Win32 control ID
    [InlineData("textBox17")]
    [InlineData("Panel_42")]
    public void WeakAutomationIdsComeWithAWarning(string id)
    {
        var verdict = AttributeStability.AnalyzeAutomationId(id);
        Assert.Equal(Stability.Weak, verdict.Stability);
        Assert.NotNull(verdict.Warning);
    }

    [Fact]
    public void NumericIdThatIsTheWindowHandle_IsUnusable()
    {
        // Unnamed WinForms controls report their HWND (here 0x20626 = 132646) as AutomationId.
        Assert.Equal(Stability.Unusable, AttributeStability.AnalyzeAutomationId("132646", "Win32", new IntPtr(132646)).Stability);
        Assert.Equal(Stability.Unusable, AttributeStability.AnalyzeAutomationId("132646", "WinForm").Stability);
        // A Win32 control ID differs from the handle and stays usable.
        Assert.Equal(Stability.Weak, AttributeStability.AnalyzeAutomationId("15", "Win32", new IntPtr(0x60592)).Stability);
    }

    [Theory]
    [InlineData("WindowsForms10.BUTTON.app.0.3a8ddef_r41_ad1", "WinForm")]
    [InlineData("channel__972a0 dm__972a0 container_e45859", "Chrome")]  // Discord, HTML class list
    [InlineData("content_d125d2", "Chrome")]
    [InlineData("appMount__51fd7", "Chrome")]
    [InlineData("flex-1", "Chrome")]        // Tailwind (helloworld.rs job cards)
    [InlineData("p-4", "Chrome")]
    [InlineData("md:flex", "Chrome")]
    [InlineData("w-full", "Chrome")]
    [InlineData("text-sm", "Chrome")]
    [InlineData("d-flex", "Chrome")]        // Bootstrap
    [InlineData("col-md-6", "Chrome")]
    [InlineData("hidden", "Chrome")]
    [InlineData("bg-gray-100", "Chrome")]
    [InlineData("hover:bg-blue-500", "Chrome")]
    [InlineData("max-w-[960px]", "Chrome")]
    [InlineData("job-card card shadow", "Gecko")]   // Firefox reports the whole HTML class list
    [InlineData("flex-1", "Gecko")]
    public void UnusableClassNames(string className, string framework)
    {
        Assert.Equal(Stability.Unusable, AttributeStability.AnalyzeClassName(className, framework).Stability);
    }

    [Theory]
    [InlineData("Notepad", "Win32")]
    [InlineData("Edit", "Win32")]
    [InlineData("Chrome_WidgetWin_1", "Win32")]
    [InlineData("ApplicationFrameWindow", "Win32")]
    [InlineData("job-card", "Chrome")]
    [InlineData("text-editor", "Chrome")]       // starts like a utility class, but isn't one
    [InlineData("content-wrapper", "Chrome")]
    [InlineData("btn-primary", "Chrome")]
    [InlineData("navbar", "Chrome")]
    [InlineData("search-results", "Chrome")]
    public void StableClassNames(string className, string framework)
    {
        Assert.Equal(Stability.Stable, AttributeStability.AnalyzeClassName(className, framework).Stability);
    }

    [Fact]
    public void NameWithStatusSuffix_MatchesOnlyTheStablePart()
    {
        var verdict = AttributeStability.AnalyzeName("Dyno (direct message), Online");

        Assert.Equal("Dyno (direct message)", verdict.Value);
        Assert.Equal(MatchKind.Contains, verdict.Match);
        Assert.Contains("Online", verdict.Warning);
    }

    [Theory]
    [InlineData("Inbox (3)", "Inbox")]
    [InlineData("Messages 12", "Messages")]
    public void NameWithTrailingCounter_DropsTheCounter(string name, string expected)
    {
        var verdict = AttributeStability.AnalyzeName(name);

        Assert.Equal(expected, verdict.Value);
        Assert.Equal(MatchKind.Contains, verdict.Match);
    }

    [Fact]
    public void PlainName_IsStableAndExact()
    {
        var verdict = AttributeStability.AnalyzeName("Seven");

        Assert.Equal(Stability.Stable, verdict.Stability);
        Assert.Equal(MatchKind.Exact, verdict.Match);
        Assert.Equal("Seven", verdict.Value);
    }

    [Fact]
    public void LongName_IsTreatedAsContent()
    {
        var text = new string('x', 120);
        Assert.Equal(Stability.Unusable, AttributeStability.AnalyzeName(text).Stability);
    }

    [Theory]
    [InlineData("Untitled - Notepad", "Notepad")]
    [InlineData("@GoAway - Discord", "Discord")]
    [InlineData("YouTube — Mozilla Firefox", "Mozilla Firefox")]
    public void WindowTitle_UsesTheAppNamePart(string title, string expected)
    {
        var verdict = AttributeStability.AnalyzeWindowTitle(title);

        Assert.Equal(expected, verdict.Value);
        Assert.Equal(MatchKind.Contains, verdict.Match);
    }

    [Fact]
    public void WindowTitleWithoutSeparator_IsExactButWeak()
    {
        var verdict = AttributeStability.AnalyzeWindowTitle("Calculator");

        Assert.Equal(MatchKind.Exact, verdict.Match);
        Assert.Equal(Stability.Weak, verdict.Stability);
    }
}

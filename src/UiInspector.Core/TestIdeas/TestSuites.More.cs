using System.Text;
using UiInspector.Core.Selectors;
using UiInspector.Core.Web;

namespace UiInspector.Core.TestIdeas;

/// <summary>
/// The suites in the remaining languages: Playwright for C# (NUnit), Selenium for Python (pytest)
/// and Java (JUnit 5) for web pages; pywinauto and Appium (pytest) for desktop apps. Same cases,
/// same automatic checks and screenshots as the others, in each framework's usual shape.
/// </summary>
public static partial class TestSuites
{
    // Long runs of one letter as code, as in the other languages.
    private static string Java(string? value) => value is null ? "null"
        : value.Length > 40 && value.Distinct().Count() == 1 ? $"{WebCodeTemplates.JavaString(value[..1])}.repeat({value.Length})"
        : WebCodeTemplates.JavaString(value);

    private static string HowToGetPlaywright(WebResult page, string install, string browser) => page.Browser == BrowserFlavor.Firefox
        ? $"{install}, then once: {browser}"
        : $"{install}   (uses the installed browser)";

    private static string Accounts(List<Field> fields) => fields.Any(x => x.Env is not null)
        ? $"Set {string.Join(" and ", fields.Where(x => x.Env is not null).Select(x => x.Env).Distinct())} to a test account."
        : "";

    // ---------------------------------------------------------------- Playwright (C#, NUnit)

    private static string PlaywrightCs(TestFeature f, List<Field> fields, WebResult? action, List<Case> cases, List<TestCase> manual, WebResult page, Scope scope)
    {
        var sb = new StringBuilder();
        var className = Pascal(scope.Only?.Id ?? f.Title) + (scope.Only is null ? "Suite" : "Test");
        sb.Append(Header(f, "//", cases.Count, manual,
        [
            HowToGetPlaywright(page, "NuGet: Microsoft.Playwright, NUnit, NUnit3TestAdapter, Microsoft.NET.Test.Sdk (a test project)", "pwsh bin/Debug/net8.0/playwright.ps1 install firefox"),
            "Run:  dotnet test      (set HEADLESS=1 to run without a window)",
            Accounts(fields),
        ], scope));
        sb.AppendLine("using System.Text.RegularExpressions;");
        sb.AppendLine("using Microsoft.Playwright;");
        sb.AppendLine("using NUnit.Framework;");
        sb.AppendLine();
        sb.AppendLine("[TestFixture]");
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");
        sb.AppendLine($"    private const string Url = {WebCodeTemplates.CsString(page.PageUrl)};");
        sb.AppendLine($"    private static readonly Regex ErrorText = new(@\"{ErrorPattern.Replace("\"", "\"\"")}\", RegexOptions.IgnoreCase);");
        var password = fields.FirstOrDefault(x => x.Feature.Kind == FieldKind.Password);
        sb.AppendLine("    private IPlaywright playwright = null!;");
        sb.AppendLine("    private IBrowser browser = null!;");
        sb.AppendLine();
        sb.AppendLine("    [OneTimeSetUp]");
        sb.AppendLine("    public async Task StartBrowser()");
        sb.AppendLine("    {");
        sb.AppendLine("        playwright = await Playwright.CreateAsync();");
        sb.AppendLine("        var headless = Environment.GetEnvironmentVariable(\"HEADLESS\") == \"1\";");
        sb.AppendLine(page.Browser == BrowserFlavor.Firefox
            ? "        browser = await playwright.Firefox.LaunchAsync(new() { Headless = headless });"
            : $"        browser = await playwright.Chromium.LaunchAsync(new() {{ Channel = \"{(page.Browser == BrowserFlavor.Edge ? "msedge" : "chrome")}\", Headless = headless }});");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    [OneTimeTearDown]");
        sb.AppendLine("    public async Task StopBrowser()");
        sb.AppendLine("    {");
        sb.AppendLine("        await browser.CloseAsync();");
        sb.AppendLine("        playwright.Dispose();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    // Valid data for every field (check boxes: \"true\" ticked, \"\" not); each case replaces one of them.");
        sb.AppendLine("    private static Dictionary<string, string> Valid() => new()");
        sb.AppendLine("    {");
        foreach (var x in fields)
        {
            var value = x.Feature.Kind == FieldKind.CheckBox ? "\"true\"" : x.Env is null ? Cs(x.Sample) : $"Environment.GetEnvironmentVariable(\"{x.Env}\") ?? {Cs(x.Sample)}";
            sb.AppendLine($"        [\"{x.Key}\"] = {value},");
        }
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    // id, title, field (null: the valid data as it is; \"*\": every field empty), value, expected");
        sb.AppendLine("    public static IEnumerable<TestCaseData> Cases()");
        sb.AppendLine("    {");
        foreach (var c in cases)
        {
            sb.AppendLine($"        yield return new TestCaseData({Cs(c.Id)}, {Cs(c.Title)}, {(c.Key is null ? "null" : $"\"{c.Key}\"")}, {Cs(c.Value)}, {Cs(c.Expected)}).SetName({Cs($"{c.Id} {c.Title}")});");
        }
        if (cases.Count == 0) sb.AppendLine("        yield break;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static async Task Fill(IPage page, Dictionary<string, string> values)");
        sb.AppendLine("    {");
        foreach (var x in fields)
        {
            var locator = WebCodeTemplates.PlaywrightLocatorCs(x.Web!.Best!);
            sb.AppendLine(x.Feature.Kind == FieldKind.CheckBox
                ? $"        await {locator}.SetCheckedAsync(values[\"{x.Key}\"] == \"true\");"
                : $"        await {locator}.FillAsync(values[\"{x.Key}\"]);");
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static async Task Send(IPage page) =>");
        sb.AppendLine(action is null
            ? $"        await {WebCodeTemplates.PlaywrightLocatorCs(fields.Last(x => x.Feature.Kind != FieldKind.CheckBox).Web!.Best!)}.PressAsync(\"Enter\");"
            : $"        await {WebCodeTemplates.PlaywrightLocatorCs(action.Best!)}.ClickAsync();");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Your app's own checks, once you know its messages.</summary>");
        sb.AppendLine("    private static Task CheckExpected(IPage page, string id, string expected) => Task.CompletedTask;");
        if (cases.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    [TestCaseSource(nameof(Cases))]");
            sb.AppendLine("    public async Task Case(string id, string title, string? field, string? value, string expected)");
            sb.AppendLine("    {");
            sb.AppendLine("        var page = await browser.NewPageAsync();");
            sb.AppendLine("        var alerts = new List<string>();");
            sb.AppendLine("        var serverErrors = new List<string>();");
            sb.AppendLine("        page.Dialog += async (_, dialog) => { alerts.Add(dialog.Message); await dialog.DismissAsync(); };");
            sb.AppendLine("        page.Response += (_, response) => { if (response.Status >= 500) serverErrors.Add($\"{response.Status} {response.Url}\"); };");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await page.GotoAsync(Url);");
            sb.AppendLine("            var values = Valid();");
            sb.AppendLine("            if (field == \"*\") foreach (var key in values.Keys.ToList()) values[key] = \"\"; // every field empty");
            sb.AppendLine("            else if (field is not null) values[field] = value ?? \"\";");
            sb.AppendLine("            await Fill(page, values);");
            sb.AppendLine("            await Send(page);");
            sb.AppendLine("            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 }); }");
            sb.AppendLine("            catch (PlaywrightException) { } // pages that keep a connection open never go idle");
            sb.AppendLine("            Directory.CreateDirectory(\"results\");");
            sb.AppendLine("            await page.ScreenshotAsync(new() { Path = Path.Combine(\"results\", $\"{id}.png\"), FullPage = true });");
            sb.AppendLine();
            sb.AppendLine("            var problems = new List<string>();");
            sb.AppendLine("            if (alerts.Count > 0) problems.Add($\"an alert appeared ('{alerts[0]}'): the page ran code typed into a field (XSS)\");");
            sb.AppendLine("            if (!string.IsNullOrEmpty(value) && value.Contains(\"<script\") && (await page.ContentAsync()).Contains(value))");
            sb.AppendLine("                problems.Add(\"the input is in the page as HTML, not as text (XSS, even if no alert showed)\");");
            sb.AppendLine("            if (ErrorText.Match(await page.InnerTextAsync(\"body\")) is { Success: true } error)");
            sb.AppendLine("                problems.Add($\"error text on the page: '{error.Value}' (database or crash details)\");");
            sb.AppendLine("            if (serverErrors.Count > 0) problems.Add($\"the server failed: {serverErrors[0]}\");");
            if (password is not null)
            {
                sb.AppendLine($"            if (values[\"{password.Key}\"].Length > 0 && page.Url.Contains(values[\"{password.Key}\"])) problems.Add(\"the password is in the address\");");
            }
            sb.AppendLine("            Assert.That(problems, Is.Empty, $\"{id} {title}\\nExpected: {expected}\");");
            sb.AppendLine("            await CheckExpected(page, id, expected);");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            await page.CloseAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }
        if (password is not null && scope.HiddenCheck)
        {
            sb.AppendLine();
            sb.AppendLine("    [Test]");
            sb.AppendLine("    public async Task PasswordFieldHidesWhatIsTyped()");
            sb.AppendLine("    {");
            sb.AppendLine("        var page = await browser.NewPageAsync();");
            sb.AppendLine("        await page.GotoAsync(Url);");
            sb.AppendLine($"        Assert.That(await {WebCodeTemplates.PlaywrightLocatorCs(password.Web!.Best!)}.GetAttributeAsync(\"type\"), Is.EqualTo(\"password\"), \"the password field shows what's typed\");");
            sb.AppendLine("        await page.CloseAsync();");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Selenium (Python, pytest)

    private static (string Options, string Driver, string Headless) SeleniumBrowser(WebResult page) => page.Browser switch
    {
        BrowserFlavor.Firefox => ("FirefoxOptions", "Firefox", "-headless"),
        BrowserFlavor.Edge => ("EdgeOptions", "Edge", "--headless=new"),
        _ => ("ChromeOptions", "Chrome", "--headless=new"),
    };

    private static string SeleniumPy(TestFeature f, List<Field> fields, WebResult? action, List<Case> cases, List<TestCase> manual, WebResult page, Scope scope)
    {
        var sb = new StringBuilder();
        var (options, driver, headless) = SeleniumBrowser(page);
        sb.Append(Header(f, "#", cases.Count, manual,
        [
            "pip install pytest selenium   (Selenium Manager downloads the matching driver)",
            "Run:  pytest " + Slug(scope.Only?.Id ?? f.Title) + "_test.py -v      (set HEADLESS=1 to run without a window)",
            "Selenium can't see the server's answers, so server errors show only through error text on the page.",
            Accounts(fields),
        ], scope));
        sb.AppendLine("import os");
        sb.AppendLine("import re");
        sb.AppendLine("import time");
        sb.AppendLine("from pathlib import Path");
        sb.AppendLine();
        sb.AppendLine("import pytest");
        sb.AppendLine("from selenium import webdriver");
        sb.AppendLine("from selenium.common.exceptions import NoAlertPresentException");
        sb.AppendLine("from selenium.webdriver.common.by import By");
        sb.AppendLine("from selenium.webdriver.common.keys import Keys");
        sb.AppendLine("from selenium.webdriver.support import expected_conditions as EC");
        sb.AppendLine("from selenium.webdriver.support.ui import WebDriverWait");
        sb.AppendLine();
        sb.AppendLine($"URL = {WebCodeTemplates.PyString(page.PageUrl)}");
        sb.AppendLine("HEADLESS = os.environ.get(\"HEADLESS\") == \"1\"");
        sb.AppendLine("RESULTS = Path(\"results\")");
        sb.AppendLine($"ERROR_TEXT = re.compile(r\"{ErrorPattern}\", re.IGNORECASE)");
        sb.AppendLine();
        sb.AppendLine("# Valid data for every field; each case replaces one of them.");
        sb.AppendLine("VALID = {");
        foreach (var x in fields)
        {
            var value = x.Env is null ? Py(x.Sample) : $"os.environ.get(\"{x.Env}\", {Py(x.Sample)})";
            sb.AppendLine($"    \"{x.Key}\": {(x.Feature.Kind == FieldKind.CheckBox ? "True" : value)},");
        }
        sb.AppendLine("}");
        var password = fields.FirstOrDefault(x => x.Feature.Kind == FieldKind.Password);
        sb.AppendLine($"PASSWORD_FIELD = {(password is null ? "None" : $"\"{password.Key}\"")}");
        sb.AppendLine();
        sb.AppendLine("# (id, title, field, value, expected): field None runs the valid data as it is, \"*\" empties every field.");
        sb.AppendLine("CASES = [");
        foreach (var c in cases) sb.AppendLine($"    ({Py(c.Id)}, {Py(c.Title)}, {(c.Key is null ? "None" : $"\"{c.Key}\"")}, {Py(c.Value)}, {Py(c.Expected)}),");
        sb.AppendLine("]");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("@pytest.fixture(scope=\"module\")");
        sb.AppendLine("def driver():");
        sb.AppendLine($"    options = webdriver.{options}()");
        sb.AppendLine("    if HEADLESS:");
        sb.AppendLine($"        options.add_argument(\"{headless}\")");
        sb.AppendLine($"    driver = webdriver.{driver}(options=options)");
        sb.AppendLine("    yield driver");
        sb.AppendLine("    driver.quit()");
        foreach (var x in fields)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"def find_{x.Key}(driver, wait):");
            foreach (var line in WebCodeTemplates.SeleniumFindBody(x.Web!.Selenium!, java: false)) sb.AppendLine("    " + line);
        }
        if (action is not null)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("def find_send_button(driver, wait):");
            foreach (var line in WebCodeTemplates.SeleniumFindBody(action.Selenium!, java: false)) sb.AppendLine("    " + line);
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def type_into(driver, field, text):");
        sb.AppendLine("    # Chrome's and Edge's drivers can't type characters beyond the basic range (emoji):");
        sb.AppendLine("    # those are set as the page would get them from a paste, with an input event.");
        sb.AppendLine("    if any(ord(c) > 0xFFFF for c in text):");
        sb.AppendLine("        driver.execute_script(\"arguments[0].value = arguments[1];\"");
        sb.AppendLine("                              \"arguments[0].dispatchEvent(new Event('input', {bubbles: true}));\", field, text)");
        sb.AppendLine("    else:");
        sb.AppendLine("        field.clear()");
        sb.AppendLine("        field.send_keys(text)");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def fill(driver, wait, values):");
        foreach (var x in fields)
        {
            if (x.Feature.Kind == FieldKind.CheckBox)
            {
                sb.AppendLine($"    box = find_{x.Key}(driver, wait)");
                sb.AppendLine($"    if box.is_selected() != values[\"{x.Key}\"]:");
                sb.AppendLine("        box.click()");
            }
            else
            {
                sb.AppendLine($"    type_into(driver, find_{x.Key}(driver, wait), values[\"{x.Key}\"])");
            }
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def send(driver, wait):");
        sb.AppendLine(action is null
            ? $"    find_{fields.Last(x => x.Feature.Kind != FieldKind.CheckBox).Key}(driver, wait).send_keys(Keys.ENTER)"
            : "    find_send_button(driver, wait).click()");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def take_alert(driver):");
        sb.AppendLine("    \"\"\"The text of an alert the page opened (and closes it), or None.\"\"\"");
        sb.AppendLine("    try:");
        sb.AppendLine("        alert = driver.switch_to.alert");
        sb.AppendLine("        text = alert.text");
        sb.AppendLine("        alert.dismiss()");
        sb.AppendLine("        return text");
        sb.AppendLine("    except NoAlertPresentException:");
        sb.AppendLine("        return None");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def check_expected(driver, case_id, expected):");
        sb.AppendLine("    \"\"\"Your app's own checks, once you know its messages.\"\"\"");
        if (cases.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("@pytest.mark.parametrize(\"case_id, title, field, value, expected\", CASES, ids=[c[0] for c in CASES])");
            sb.AppendLine("def test_case(driver, case_id, title, field, value, expected):");
            sb.AppendLine("    wait = WebDriverWait(driver, 10)");
            sb.AppendLine("    driver.get(URL)");
            sb.AppendLine("    values = dict(VALID)");
            sb.AppendLine("    if field == \"*\":  # every field empty");
            sb.AppendLine("        values = {k: False if isinstance(v, bool) else \"\" for k, v in VALID.items()}");
            sb.AppendLine("    elif field is not None:");
            sb.AppendLine("        values[field] = value");
            sb.AppendLine("    fill(driver, wait, values)");
            sb.AppendLine("    send(driver, wait)");
            sb.AppendLine("    time.sleep(1)  # let the page react");
            sb.AppendLine();
            sb.AppendLine("    problems = []");
            sb.AppendLine("    alert = take_alert(driver)");
            sb.AppendLine("    if alert is not None:");
            sb.AppendLine("        problems.append(f\"an alert appeared ({alert!r}): the page ran code typed into a field (XSS)\")");
            sb.AppendLine("    driver.switch_to.default_content()");
            sb.AppendLine("    RESULTS.mkdir(exist_ok=True)");
            sb.AppendLine("    driver.save_screenshot(str(RESULTS / f\"{case_id}.png\"))");
            sb.AppendLine("    if value and \"<script\" in value and value in driver.page_source:");
            sb.AppendLine("        problems.append(\"the input is in the page as HTML, not as text (XSS, even if no alert showed)\")");
            sb.AppendLine("    error = ERROR_TEXT.search(driver.find_element(By.TAG_NAME, \"body\").text)");
            sb.AppendLine("    if error:");
            sb.AppendLine("        problems.append(f\"error text on the page: {error.group(0)!r} (database or crash details)\")");
            sb.AppendLine("    if PASSWORD_FIELD and values[PASSWORD_FIELD] and values[PASSWORD_FIELD] in driver.current_url:");
            sb.AppendLine("        problems.append(\"the password is in the address\")");
            sb.AppendLine("    assert not problems, f\"{case_id} {title}: \" + \"; \".join(problems) + f\"\\nExpected: {expected}\"");
            sb.AppendLine("    check_expected(driver, case_id, expected)");
        }
        if (password is not null && scope.HiddenCheck)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("def test_password_is_hidden(driver):");
            sb.AppendLine("    driver.get(URL)");
            sb.AppendLine($"    assert find_{password.Key}(driver, WebDriverWait(driver, 10)).get_attribute(\"type\") == \"password\", \"the password field shows what's typed\"");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Selenium (Java, JUnit 5)

    private static string SeleniumJava(TestFeature f, List<Field> fields, WebResult? action, List<Case> cases, List<TestCase> manual, WebResult page, Scope scope)
    {
        var sb = new StringBuilder();
        var (options, driver, headless) = SeleniumBrowser(page);
        var package = page.Browser switch { BrowserFlavor.Firefox => "firefox", BrowserFlavor.Edge => "edge", _ => "chrome" };
        var className = Pascal(scope.Only?.Id ?? f.Title) + "Test";
        sb.Append(Header(f, "//", cases.Count, manual,
        [
            "Maven: org.seleniumhq.selenium:selenium-java, org.junit.jupiter:junit-jupiter (Java 11+)",
            $"Save as src/test/java/{className}.java, run:  mvn test      (set HEADLESS=1 to run without a window)",
            "Selenium can't see the server's answers, so server errors show only through error text on the page.",
            Accounts(fields),
        ], scope));
        sb.AppendLine("import java.nio.file.Files;");
        sb.AppendLine("import java.nio.file.Path;");
        sb.AppendLine("import java.nio.file.StandardCopyOption;");
        sb.AppendLine("import java.time.Duration;");
        sb.AppendLine("import java.util.ArrayList;");
        sb.AppendLine("import java.util.HashMap;");
        sb.AppendLine("import java.util.List;");
        sb.AppendLine("import java.util.Map;");
        sb.AppendLine("import java.util.regex.Matcher;");
        sb.AppendLine("import java.util.regex.Pattern;");
        sb.AppendLine("import java.util.stream.Stream;");
        sb.AppendLine("import org.junit.jupiter.api.AfterAll;");
        sb.AppendLine("import org.junit.jupiter.api.Assertions;");
        sb.AppendLine("import org.junit.jupiter.api.BeforeAll;");
        sb.AppendLine("import org.junit.jupiter.api.Test;");
        sb.AppendLine("import org.junit.jupiter.api.TestInstance;");
        sb.AppendLine("import org.junit.jupiter.params.ParameterizedTest;");
        sb.AppendLine("import org.junit.jupiter.params.provider.Arguments;");
        sb.AppendLine("import org.junit.jupiter.params.provider.MethodSource;");
        sb.AppendLine("import org.openqa.selenium.*;");
        sb.AppendLine($"import org.openqa.selenium.{package}.{driver}Driver;");
        sb.AppendLine($"import org.openqa.selenium.{package}.{options};");
        sb.AppendLine("import org.openqa.selenium.support.ui.ExpectedConditions;");
        sb.AppendLine("import org.openqa.selenium.support.ui.WebDriverWait;");
        sb.AppendLine();
        sb.AppendLine("@TestInstance(TestInstance.Lifecycle.PER_CLASS)");
        sb.AppendLine($"class {className} {{");
        sb.AppendLine($"    static final String URL = {WebCodeTemplates.JavaString(page.PageUrl)};");
        sb.AppendLine($"    static final Pattern ERROR_TEXT = Pattern.compile({WebCodeTemplates.JavaString(ErrorPattern)}, Pattern.CASE_INSENSITIVE);");
        var password = fields.FirstOrDefault(x => x.Feature.Kind == FieldKind.Password);
        sb.AppendLine($"    static final String PASSWORD_FIELD = {(password is null ? "null" : $"\"{password.Key}\"")};");
        sb.AppendLine("    WebDriver driver;");
        sb.AppendLine("    WebDriverWait wait;");
        sb.AppendLine();
        sb.AppendLine("    @BeforeAll");
        sb.AppendLine("    void start() {");
        sb.AppendLine($"        {options} options = new {options}();");
        sb.AppendLine($"        if (\"1\".equals(System.getenv(\"HEADLESS\"))) options.addArguments(\"{headless}\");");
        sb.AppendLine($"        driver = new {driver}Driver(options);");
        sb.AppendLine("        wait = new WebDriverWait(driver, Duration.ofSeconds(10));");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    @AfterAll");
        sb.AppendLine("    void stop() {");
        sb.AppendLine("        driver.quit();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    static String env(String name, String fallback) {");
        sb.AppendLine("        String value = System.getenv(name);");
        sb.AppendLine("        return value != null ? value : fallback;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    // Valid data for every field (check boxes: \"true\" ticked, \"\" not); each case replaces one of them.");
        sb.AppendLine("    static Map<String, String> valid() {");
        sb.AppendLine("        Map<String, String> values = new HashMap<>();");
        foreach (var x in fields)
        {
            var value = x.Feature.Kind == FieldKind.CheckBox ? "\"true\"" : x.Env is null ? Java(x.Sample) : $"env(\"{x.Env}\", {Java(x.Sample)})";
            sb.AppendLine($"        values.put(\"{x.Key}\", {value});");
        }
        sb.AppendLine("        return values;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    // id, title, field (null: the valid data as it is; \"*\": every field empty), value, expected");
        sb.AppendLine("    static Stream<Arguments> cases() {");
        sb.AppendLine("        return Stream.of(");
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            sb.AppendLine($"            Arguments.of({Java(c.Id)}, {Java(c.Title)}, {(c.Key is null ? "null" : $"\"{c.Key}\"")}, {Java(c.Value)}, {Java(c.Expected)}){(i < cases.Count - 1 ? "," : "")}");
        }
        sb.AppendLine("        );");
        sb.AppendLine("    }");
        foreach (var x in fields)
        {
            sb.AppendLine();
            sb.AppendLine($"    WebElement find_{x.Key}() {{");
            foreach (var line in WebCodeTemplates.SeleniumFindBody(x.Web!.Selenium!, java: true)) sb.AppendLine("        " + line);
            sb.AppendLine("    }");
        }
        if (action is not null)
        {
            sb.AppendLine();
            sb.AppendLine("    WebElement findSendButton() {");
            foreach (var line in WebCodeTemplates.SeleniumFindBody(action.Selenium!, java: true)) sb.AppendLine("        " + line);
            sb.AppendLine("    }");
        }
        sb.AppendLine();
        sb.AppendLine("    // Chrome's and Edge's drivers can't type characters beyond the basic range (emoji):");
        sb.AppendLine("    // those are set as the page would get them from a paste, with an input event.");
        sb.AppendLine("    void typeInto(WebElement field, String text) {");
        sb.AppendLine("        if (text.codePoints().anyMatch(c -> c > 0xFFFF)) {");
        sb.AppendLine("            ((JavascriptExecutor) driver).executeScript(");
        sb.AppendLine("                \"arguments[0].value = arguments[1]; arguments[0].dispatchEvent(new Event('input', {bubbles: true}));\", field, text);");
        sb.AppendLine("        } else {");
        sb.AppendLine("            field.clear();");
        sb.AppendLine("            field.sendKeys(text);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void fill(Map<String, String> values) {");
        foreach (var x in fields)
        {
            if (x.Feature.Kind == FieldKind.CheckBox)
            {
                sb.AppendLine($"        WebElement box_{x.Key} = find_{x.Key}();");
                sb.AppendLine($"        if (box_{x.Key}.isSelected() != \"true\".equals(values.get(\"{x.Key}\"))) box_{x.Key}.click();");
            }
            else
            {
                sb.AppendLine($"        typeInto(find_{x.Key}(), values.get(\"{x.Key}\"));");
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void send() {");
        sb.AppendLine(action is null
            ? $"        find_{fields.Last(x => x.Feature.Kind != FieldKind.CheckBox).Key}().sendKeys(Keys.ENTER);"
            : "        findSendButton().click();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /** The text of an alert the page opened (and closes it), or null. */");
        sb.AppendLine("    String takeAlert() {");
        sb.AppendLine("        try {");
        sb.AppendLine("            Alert alert = driver.switchTo().alert();");
        sb.AppendLine("            String text = alert.getText();");
        sb.AppendLine("            alert.dismiss();");
        sb.AppendLine("            return text;");
        sb.AppendLine("        } catch (NoAlertPresentException e) {");
        sb.AppendLine("            return null;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /** Your app's own checks, once you know its messages. */");
        sb.AppendLine("    void checkExpected(String id, String expected) {}");
        if (cases.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    @ParameterizedTest(name = \"{0} {1}\")");
            sb.AppendLine("    @MethodSource(\"cases\")");
            sb.AppendLine("    void runCase(String id, String title, String field, String value, String expected) throws Exception {");
            sb.AppendLine("        driver.get(URL);");
            sb.AppendLine("        Map<String, String> values = valid();");
            sb.AppendLine("        if (\"*\".equals(field)) values.replaceAll((k, v) -> \"\"); // every field empty");
            sb.AppendLine("        else if (field != null) values.put(field, value);");
            sb.AppendLine("        fill(values);");
            sb.AppendLine("        send();");
            sb.AppendLine("        Thread.sleep(1000); // let the page react");
            sb.AppendLine();
            sb.AppendLine("        List<String> problems = new ArrayList<>();");
            sb.AppendLine("        String alert = takeAlert();");
            sb.AppendLine("        if (alert != null) problems.add(\"an alert appeared ('\" + alert + \"'): the page ran code typed into a field (XSS)\");");
            sb.AppendLine("        driver.switchTo().defaultContent();");
            sb.AppendLine("        Files.createDirectories(Path.of(\"results\"));");
            sb.AppendLine("        Files.copy(((TakesScreenshot) driver).getScreenshotAs(OutputType.FILE).toPath(), Path.of(\"results\", id + \".png\"), StandardCopyOption.REPLACE_EXISTING);");
            sb.AppendLine("        if (value != null && value.contains(\"<script\") && driver.getPageSource().contains(value))");
            sb.AppendLine("            problems.add(\"the input is in the page as HTML, not as text (XSS, even if no alert showed)\");");
            sb.AppendLine("        Matcher error = ERROR_TEXT.matcher(driver.findElement(By.tagName(\"body\")).getText());");
            sb.AppendLine("        if (error.find()) problems.add(\"error text on the page: '\" + error.group() + \"' (database or crash details)\");");
            sb.AppendLine("        if (PASSWORD_FIELD != null && !values.get(PASSWORD_FIELD).isEmpty() && driver.getCurrentUrl().contains(values.get(PASSWORD_FIELD)))");
            sb.AppendLine("            problems.add(\"the password is in the address\");");
            sb.AppendLine("        Assertions.assertTrue(problems.isEmpty(), id + \" \" + title + \": \" + String.join(\"; \", problems) + \"\\nExpected: \" + expected);");
            sb.AppendLine("        checkExpected(id, expected);");
            sb.AppendLine("    }");
        }
        if (password is not null && scope.HiddenCheck)
        {
            sb.AppendLine();
            sb.AppendLine("    @Test");
            sb.AppendLine("    void passwordFieldHidesWhatIsTyped() {");
            sb.AppendLine("        driver.get(URL);");
            sb.AppendLine($"        Assertions.assertEquals(\"password\", find_{password.Key}().getAttribute(\"type\"), \"the password field shows what's typed\");");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- pywinauto (pytest)

    private static string PywinautoPy(TestFeature f, List<Field> fields, List<Case> cases, List<TestCase> manual, SelectorStep window, Scope scope)
    {
        var sb = new StringBuilder();
        sb.Append(Header(f, "#", cases.Count, manual,
        [
            "pip install pytest pywinauto pillow   (pillow for the screenshots)",
            "Run with the app open on this form:  pytest " + Slug(scope.Only?.Id ?? f.Title) + "_test.py -v",
            "Each case starts from the window as it is: if a case leaves the form (a successful login), put the way back in reset().",
        ], scope));
        sb.AppendLine("import os");
        sb.AppendLine("import re");
        sb.AppendLine("import time");
        sb.AppendLine("from pathlib import Path");
        sb.AppendLine();
        sb.AppendLine("import pytest");
        sb.AppendLine("from pywinauto import Desktop");
        sb.AppendLine();
        sb.AppendLine("RESULTS = Path(\"results\")");
        sb.AppendLine($"ERROR_TEXT = re.compile(r\"{ErrorPattern}|unhandled|has stopped working\", re.IGNORECASE)");
        sb.AppendLine();
        sb.AppendLine("VALID = {");
        foreach (var x in fields)
        {
            var value = x.Env is null ? Py(x.Sample) : $"os.environ.get(\"{x.Env}\", {Py(x.Sample)})";
            sb.AppendLine($"    \"{x.Key}\": {(x.Feature.Kind == FieldKind.CheckBox ? "True" : value)},");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("CASES = [");
        foreach (var c in cases) sb.AppendLine($"    ({Py(c.Id)}, {Py(c.Title)}, {(c.Key is null ? "None" : $"\"{c.Key}\"")}, {Py(c.Value)}, {Py(c.Expected)}),");
        sb.AppendLine("]");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def window_spec():");
        sb.AppendLine($"    return Desktop(backend=\"uia\").window({string.Join(", ", CodeTemplates.PywinautoArgs(window))})");
        string Spec(Selector s) => "window_spec()" + string.Concat(s.Steps.Select(step => $".child_window({string.Join(", ", CodeTemplates.PywinautoArgs(step))})"));
        foreach (var x in fields)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"def find_{x.Key}():");
            sb.AppendLine($"    return {Spec(x.Feature.Control.Selector!)}.wait(\"exists\", timeout=10)");
        }
        if (f.Action?.Selector is { } actionSelector)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("def find_send_button():");
            sb.AppendLine($"    return {Spec(actionSelector)}.wait(\"exists\", timeout=10)");
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def fill(values):");
        foreach (var x in fields)
        {
            if (x.Feature.Kind == FieldKind.CheckBox)
            {
                sb.AppendLine($"    box = find_{x.Key}()");
                sb.AppendLine($"    if bool(box.get_toggle_state()) != values[\"{x.Key}\"]:");
                sb.AppendLine("        box.iface_toggle.Toggle()");
            }
            else
            {
                sb.AppendLine($"    find_{x.Key}().iface_value.SetValue(values[\"{x.Key}\"])");
            }
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def send():");
        if (f.Action?.Selector is not null)
        {
            sb.AppendLine(f.Action.Element.Patterns.Contains("Invoke") ? "    find_send_button().iface_invoke.Invoke()" : "    find_send_button().click_input()");
        }
        else
        {
            sb.AppendLine($"    find_{fields.Last(x => x.Feature.Kind != FieldKind.CheckBox).Key}().type_keys(\"{{ENTER}}\")");
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def reset():");
        sb.AppendLine("    \"\"\"Bring the app back to this form if a case moved away from it.\"\"\"");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def check_expected(window, case_id, expected):");
        sb.AppendLine("    \"\"\"Your app's own checks, once you know its messages.\"\"\"");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def texts(wrapper):");
        sb.AppendLine("    return \" \".join(t.window_text() for t in wrapper.descendants(control_type=\"Text\"))");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("@pytest.mark.parametrize(\"case_id, title, field, value, expected\", CASES, ids=[c[0] for c in CASES])");
        sb.AppendLine("def test_case(case_id, title, field, value, expected):");
        sb.AppendLine("    reset()");
        sb.AppendLine("    spec = window_spec()  # the specification can say whether it still exists; the window it finds can't");
        sb.AppendLine("    window = spec.wait(\"exists\", timeout=10)");
        sb.AppendLine("    before = {w.handle for w in Desktop(backend=\"uia\").windows()}");
        sb.AppendLine("    values = dict(VALID)");
        sb.AppendLine("    if field == \"*\":  # every field empty");
        sb.AppendLine("        values = {k: False if isinstance(v, bool) else \"\" for k, v in VALID.items()}");
        sb.AppendLine("    elif field is not None:");
        sb.AppendLine("        values[field] = value");
        sb.AppendLine("    fill(values)");
        sb.AppendLine("    send()");
        sb.AppendLine("    time.sleep(1)");
        sb.AppendLine("    RESULTS.mkdir(exist_ok=True)");
        sb.AppendLine();
        sb.AppendLine("    problems = []");
        sb.AppendLine("    alive = spec.exists()");
        sb.AppendLine("    if not alive:");
        sb.AppendLine("        problems.append(\"the window is gone (the app closed or crashed)\")");
        sb.AppendLine("    else:");
        sb.AppendLine("        try:");
        sb.AppendLine("            window.capture_as_image().save(RESULTS / f\"{case_id}.png\")");
        sb.AppendLine("        except Exception:");
        sb.AppendLine("            pass  # no screenshot (no Pillow, or the window is off screen); the checks still count");
        sb.AppendLine("    # New windows of the app (message boxes): an error dialog is a failure.");
        sb.AppendLine("    for w in Desktop(backend=\"uia\").windows():");
        sb.AppendLine("        if alive and w.handle not in before and w.process_id() == window.process_id():");
        sb.AppendLine("            text = texts(w)");
        sb.AppendLine("            if ERROR_TEXT.search(text) or re.search(r\"\\berror\\b|exception\", w.window_text(), re.IGNORECASE):");
        sb.AppendLine("                problems.append(f\"an error window appeared: {w.window_text()!r}: {text[:200]!r}\")");
        sb.AppendLine("            w.close()");
        sb.AppendLine("    if alive:");
        sb.AppendLine("        error = ERROR_TEXT.search(texts(window))");
        sb.AppendLine("        if error:");
        sb.AppendLine("            problems.append(f\"error text in the window: {error.group(0)!r}\")");
        sb.AppendLine("    assert not problems, f\"{case_id} {title}: \" + \"; \".join(problems) + f\"\\nExpected: {expected}\"");
        sb.AppendLine("    check_expected(window, case_id, expected)");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Appium (Python, pytest)

    private static string AppiumPy(TestFeature f, List<Field> fields, List<Case> cases, List<TestCase> manual, SelectorStep window, Scope scope)
    {
        var sb = new StringBuilder();
        sb.Append(Header(f, "#", cases.Count, manual,
        [
            "pip install pytest Appium-Python-Client; needs an Appium server with a Windows driver at http://127.0.0.1:4723",
            "Run with the app open on this form:  pytest " + Slug(scope.Only?.Id ?? f.Title) + "_test.py -v",
            "Each case starts from the window as it is: if a case leaves the form (a successful login), put the way back in reset().",
        ], scope));
        sb.AppendLine("import os");
        sb.AppendLine("import re");
        sb.AppendLine("import time");
        sb.AppendLine("from pathlib import Path");
        sb.AppendLine();
        sb.AppendLine("import pytest");
        sb.AppendLine("from appium import webdriver");
        sb.AppendLine("from appium.options.windows import WindowsOptions");
        sb.AppendLine("from appium.webdriver.common.appiumby import AppiumBy");
        sb.AppendLine();
        sb.AppendLine("RESULTS = Path(\"results\")");
        sb.AppendLine($"ERROR_TEXT = re.compile(r\"{ErrorPattern}|unhandled|has stopped working\", re.IGNORECASE)");
        sb.AppendLine($"WINDOW = {Py("/Pane[@ClassName='#32769']/" + CodeTemplates.XPathStep(window))}");
        sb.AppendLine("TOP_WINDOWS = \"/Pane[@ClassName='#32769']/Window\"");
        sb.AppendLine();
        sb.AppendLine("VALID = {");
        foreach (var x in fields)
        {
            var value = x.Env is null ? Py(x.Sample) : $"os.environ.get(\"{x.Env}\", {Py(x.Sample)})";
            sb.AppendLine($"    \"{x.Key}\": {(x.Feature.Kind == FieldKind.CheckBox ? "True" : value)},");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("CASES = [");
        foreach (var c in cases) sb.AppendLine($"    ({Py(c.Id)}, {Py(c.Title)}, {(c.Key is null ? "None" : $"\"{c.Key}\"")}, {Py(c.Value)}, {Py(c.Expected)}),");
        sb.AppendLine("]");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("@pytest.fixture(scope=\"module\")");
        sb.AppendLine("def driver():");
        sb.AppendLine("    options = WindowsOptions()");
        sb.AppendLine("    options.app = \"Root\"  # desktop session: works with apps that are already running");
        sb.AppendLine("    driver = webdriver.Remote(\"http://127.0.0.1:4723\", options=options)");
        sb.AppendLine("    driver.implicitly_wait(10)");
        sb.AppendLine("    yield driver");
        sb.AppendLine("    driver.quit()");
        void Finder(string name, Selector s)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"def {name}(driver):");
            sb.AppendLine("    scope = driver.find_element(AppiumBy.XPATH, WINDOW)");
            foreach (var step in s.Steps)
            {
                var xpath = Py(".//" + CodeTemplates.XPathStep(step with { Index = null }));
                sb.AppendLine(step.Index is { } index
                    ? $"    scope = scope.find_elements(AppiumBy.XPATH, {xpath})[{index}]"
                    : $"    scope = scope.find_element(AppiumBy.XPATH, {xpath})");
            }
            sb.AppendLine("    return scope");
        }
        foreach (var x in fields) Finder($"find_{x.Key}", x.Feature.Control.Selector!);
        if (f.Action?.Selector is { } actionSelector) Finder("find_send_button", actionSelector);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def fill(driver, values):");
        foreach (var x in fields)
        {
            if (x.Feature.Kind == FieldKind.CheckBox)
            {
                sb.AppendLine($"    box = find_{x.Key}(driver)");
                sb.AppendLine($"    if box.is_selected() != values[\"{x.Key}\"]:");
                sb.AppendLine("        box.click()");
            }
            else
            {
                sb.AppendLine($"    field = find_{x.Key}(driver)");
                sb.AppendLine("    field.clear()");
                sb.AppendLine($"    field.send_keys(values[\"{x.Key}\"])");
            }
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def send(driver):");
        sb.AppendLine(f.Action?.Selector is not null
            ? "    find_send_button(driver).click()"
            : $"    find_{fields.Last(x => x.Feature.Kind != FieldKind.CheckBox).Key}(driver).send_keys(\"\\ue007\")  # Enter");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def reset(driver):");
        sb.AppendLine("    \"\"\"Bring the app back to this form if a case moved away from it.\"\"\"");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def check_expected(driver, case_id, expected):");
        sb.AppendLine("    \"\"\"Your app's own checks, once you know its messages.\"\"\"");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def texts(element):");
        sb.AppendLine("    return \" \".join(t.get_attribute(\"Name\") or \"\" for t in element.find_elements(AppiumBy.XPATH, \".//Text\"))");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("@pytest.mark.parametrize(\"case_id, title, field, value, expected\", CASES, ids=[c[0] for c in CASES])");
        sb.AppendLine("def test_case(driver, case_id, title, field, value, expected):");
        sb.AppendLine("    reset(driver)");
        sb.AppendLine("    before = {w.get_attribute(\"NativeWindowHandle\") for w in driver.find_elements(AppiumBy.XPATH, TOP_WINDOWS)}");
        sb.AppendLine("    values = dict(VALID)");
        sb.AppendLine("    if field == \"*\":  # every field empty");
        sb.AppendLine("        values = {k: False if isinstance(v, bool) else \"\" for k, v in VALID.items()}");
        sb.AppendLine("    elif field is not None:");
        sb.AppendLine("        values[field] = value");
        sb.AppendLine("    fill(driver, values)");
        sb.AppendLine("    send(driver)");
        sb.AppendLine("    time.sleep(1)");
        sb.AppendLine("    RESULTS.mkdir(exist_ok=True)");
        sb.AppendLine();
        sb.AppendLine("    problems = []");
        sb.AppendLine("    windows = driver.find_elements(AppiumBy.XPATH, WINDOW)");
        sb.AppendLine("    if not windows:");
        sb.AppendLine("        problems.append(\"the window is gone (the app closed or crashed)\")");
        sb.AppendLine("    else:");
        sb.AppendLine("        windows[0].screenshot(str(RESULTS / f\"{case_id}.png\"))");
        sb.AppendLine("        error = ERROR_TEXT.search(texts(windows[0]))");
        sb.AppendLine("        if error:");
        sb.AppendLine("            problems.append(f\"error text in the window: {error.group(0)!r}\")");
        sb.AppendLine("    # New top-level windows (message boxes): an error dialog is a failure.");
        sb.AppendLine("    for w in driver.find_elements(AppiumBy.XPATH, TOP_WINDOWS):");
        sb.AppendLine("        if w.get_attribute(\"NativeWindowHandle\") not in before:");
        sb.AppendLine("            name, text = w.get_attribute(\"Name\") or \"\", texts(w)");
        sb.AppendLine("            if ERROR_TEXT.search(text) or re.search(r\"\\berror\\b|exception\", name, re.IGNORECASE):");
        sb.AppendLine("                problems.append(f\"an error window appeared: {name!r}: {text[:200]!r}\")");
        sb.AppendLine("    assert not problems, f\"{case_id} {title}: \" + \"; \".join(problems) + f\"\\nExpected: {expected}\"");
        sb.AppendLine("    check_expected(driver, case_id, expected)");
        return sb.ToString();
    }
}

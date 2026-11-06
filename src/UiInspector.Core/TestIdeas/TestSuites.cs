using System.Text;
using UiInspector.Core.Selectors;
using UiInspector.Core.Web;

namespace UiInspector.Core.TestIdeas;

/// <summary>
/// Data-driven test suites: every automatable case of a feature, run one by one with its own
/// data, through the locators the analysis verified. After each case they check automatically
/// what can be told without knowing the app: no alert (code from the input ran), no database or
/// crash text, no server error, the password not in the address, the main window still there;
/// and they save a screenshot, so the case's expected result can be compared by eye. Whether the
/// app gave the right answer ("refused with a message") is the app's own business: each case
/// carries its expected result, and check_expected() is where the tester makes that a check.
/// </summary>
internal static class TestSuites
{
    private const int MaxLinks = 20;

    /// <summary>Text that pages only show when something broke inside: database errors, stack traces, server error pages.</summary>
    internal const string ErrorPattern =
        @"SQL syntax|SQLSTATE|ODBC|ORA-\d{5}|mysql_|pg_query|Unclosed quotation mark|syntax error at or near|" +
        @"Traceback \(most recent call last\)|Stack trace:|Unhandled exception|Internal Server Error|" +
        @"Server Error in '/' Application|Exception Details:|java\.lang\.\w+Exception|NullReferenceException|Fatal error:";

    private sealed record Field(string Key, FeatureField Feature, string Sample, string? Env, WebResult? Web);

    private sealed record Case(string Id, string Title, string? Key, string? Value, string Expected);

    public static IReadOnlyList<CodeSnippet> Build(TestFeature feature, bool isWeb, Func<FeatureControl, WebResult?>? web)
    {
        return feature.Kind switch
        {
            FeatureKind.Login or FeatureKind.Registration or FeatureKind.Form or FeatureKind.Search => FormSuites(feature, isWeb, web),
            FeatureKind.Navigation or FeatureKind.List when isWeb && web is not null => LinkSuites(feature, web),
            _ => [],
        };
    }

    // ---------------------------------------------------------------- forms

    private static IReadOnlyList<CodeSnippet> FormSuites(TestFeature f, bool isWeb, Func<FeatureControl, WebResult?>? web)
    {
        var fields = new List<Field>();
        var used = new HashSet<string>();
        foreach (var field in f.Fields.Where(x => x.Kind is not (FieldKind.Choice or FieldKind.Radio)))
        {
            if (field.Control.Selector is null) continue;
            var key = Key(field.Label, used);
            var (sample, env) = field.Kind switch
            {
                FieldKind.Password => ("Valid#Pass2024", "UIINSPECTOR_PASSWORD"),
                _ when f.Kind == FeatureKind.Login => ("tester@example.com", "UIINSPECTOR_USER"),
                _ => (TestPlanScripts.SampleValue(field.Kind), null),
            };
            fields.Add(new Field(key, field, sample, env, isWeb ? web?.Invoke(field.Control) : null));
        }
        if (fields.Count == 0 || fields.All(x => x.Feature.Kind == FieldKind.CheckBox)) return [];

        // The cases a suite can run: their own value (or empty), typed into one of these fields.
        var cases = new List<Case>();
        var main = f.Cases.FirstOrDefault(c => c.Automatable && c.Category == TestCategory.Positive);
        cases.Add(new Case(main?.Id ?? "MAIN", main?.Title ?? "Valid data in every field", null, null, main?.Expected ?? "It works with valid data."));
        var manual = new List<TestCase>();
        foreach (var c in f.Cases)
        {
            // "*": every field empty.
            if (c is { EmptiesEverything: true, Automatable: true })
            {
                cases.Add(new Case(c.Id, c.Title, "*", "", c.Expected));
                continue;
            }
            if (!c.IsFieldData || c.Target is null) continue;
            var target = fields.FirstOrDefault(x => x.Feature.Control.Element.IsSameElement(c.Target.Element));
            if (target is null || target.Feature.Kind == FieldKind.CheckBox) continue;
            if (c.Data is not null && c.Value is null) manual.Add(c);
            else if (c.Automatable) cases.Add(new Case(c.Id, c.Title, target.Key, c.Value ?? "", c.Expected));
        }

        var snippets = new List<CodeSnippet>();
        if (isWeb)
        {
            if (fields.All(x => x.Web is { Connected: true, Best: not null }) && fields[0].Web is { Launch.Kind: WebAppKind.Browser } page)
            {
                var action = f.Action is { } a ? web?.Invoke(a) : null;
                if (action is not null && action is not { Connected: true, Best: not null }) action = null;
                snippets.Add(new CodeSnippet("suite-playwright-py", "Test suite · Playwright (Python, pytest)", PlaywrightPy(f, fields, action, cases, manual, page)));
                snippets.Add(new CodeSnippet("suite-playwright-js", "Test suite · Playwright Test (JavaScript)", PlaywrightJs(f, fields, action, cases, manual, page)));
            }
        }
        else if (fields[0].Feature.Control.Selector is { } first)
        {
            snippets.Add(new CodeSnippet("suite-uiautomation", "Test suite · Python uiautomation (pytest)", UiaPy(f, fields, cases, manual, first.Window)));
            snippets.Add(new CodeSnippet("suite-flaui", "Test suite · FlaUI (C#, xUnit)", FlaUiXunit(f, fields, cases, manual, first.Window)));
        }
        return snippets;
    }

    /// <summary>A short identifier from the label: "email", "password", "search_jobs".</summary>
    private static string Key(string label, HashSet<string> used)
    {
        var words = new string(label.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3);
        var key = string.Join('_', words);
        if (key.Length == 0 || char.IsDigit(key[0])) key = "field" + (key.Length == 0 ? "" : "_" + key);
        var unique = key;
        for (var i = 2; !used.Add(unique); i++) unique = $"{key}_{i}";
        return unique;
    }

    private static string Header(TestFeature f, string comment, int cases, IReadOnlyList<TestCase> manual, IEnumerable<string> howToRun)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{comment} Test suite for “{f.Title}”: {cases} cases from UiInspector's test plan, each run with its own data.");
        foreach (var line in howToRun.Where(l => l.Length > 0)) sb.AppendLine($"{comment} {line}");
        sb.AppendLine($"{comment}");
        sb.AppendLine($"{comment} After each case it checks automatically what can be checked without knowing the app:");
        sb.AppendLine($"{comment}   no alert (code typed into a field ran: XSS), no database or crash text on the page,");
        sb.AppendLine($"{comment}   no server error, the password not in the address, the window still there.");
        sb.AppendLine($"{comment} A screenshot of every result goes to the results folder. Whether the app gave the right");
        sb.AppendLine($"{comment} answer (e.g. \"refused with a message\") is each case's EXPECTED: compare it with the");
        sb.AppendLine($"{comment} screenshot, or turn it into a check in check_expected() once you know your app's messages.");
        sb.AppendLine($"{comment} Only run the security cases against apps you're allowed to test.");
        if (manual.Count > 0)
        {
            sb.AppendLine($"{comment}");
            sb.AppendLine($"{comment} Not run here, because they need your app's own values (do them by hand or add them):");
            foreach (var c in manual.Take(12)) sb.AppendLine($"{comment}   {c.Id} {c.Title}: {c.Data}");
            if (manual.Count > 12) sb.AppendLine($"{comment}   …and {manual.Count - 12} more (see the test plan)");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    // Long runs of one letter are written as code ("a" * 1000), not as 1,000 letters.
    private static string Py(string? value) => value is null ? "None"
        : value.Length > 40 && value.Distinct().Count() == 1 ? $"{WebCodeTemplates.PyString(value[..1])} * {value.Length}"
        : WebCodeTemplates.PyString(value);

    private static string Js(string? value) => value is null ? "null"
        : value.Length > 40 && value.Distinct().Count() == 1 ? $"{WebCodeTemplates.JsString(value[..1])}.repeat({value.Length})"
        : WebCodeTemplates.JsString(value);

    private static string Cs(string? value) => value is null ? "null"
        : value.Length > 40 && value.Distinct().Count() == 1 ? $"new string('{(value[0] == '\'' ? "\\'" : value[0].ToString())}', {value.Length})"
        : CodeTemplates.Cs(value);

    // ---------------------------------------------------------------- Playwright (Python, pytest)

    private static string Launch(WebResult page, bool python) => page.Browser switch
    {
        BrowserFlavor.Firefox => python ? "p.firefox.launch(headless=HEADLESS)" : "",
        BrowserFlavor.Edge => python ? "p.chromium.launch(channel=\"msedge\", headless=HEADLESS)" : "msedge",
        _ => python ? "p.chromium.launch(channel=\"chrome\", headless=HEADLESS)" : "chrome",
    };

    private static string PlaywrightPy(TestFeature f, List<Field> fields, WebResult? action, List<Case> cases, List<TestCase> manual, WebResult page)
    {
        var sb = new StringBuilder();
        sb.Append(Header(f, "#", cases.Count, manual,
        [
            page.Browser == BrowserFlavor.Firefox ? "pip install pytest playwright, then once: playwright install firefox" : "pip install pytest playwright   (uses the installed browser)",
            "Run:  pytest " + Slug(f.Title) + "_test.py -v      (set HEADLESS=1 to run without a window)",
            fields.Any(x => x.Env is not null) ? $"Set {string.Join(" and ", fields.Where(x => x.Env is not null).Select(x => x.Env).Distinct())} to a test account (defaults: {string.Join(", ", fields.Where(x => x.Env is not null).Select(x => x.Sample))})." : "",
        ]));
        sb.AppendLine("import os");
        sb.AppendLine("import re");
        sb.AppendLine("from pathlib import Path");
        sb.AppendLine();
        sb.AppendLine("import pytest");
        sb.AppendLine("from playwright.sync_api import sync_playwright");
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
        foreach (var c in cases)
        {
            sb.AppendLine($"    ({Py(c.Id)}, {Py(c.Title)}, {(c.Key is null ? "None" : $"\"{c.Key}\"")}, {Py(c.Value)}, {Py(c.Expected)}),");
        }
        sb.AppendLine("]");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def fill(page, values):");
        foreach (var x in fields)
        {
            var locator = WebCodeTemplates.PlaywrightLocator(x.Web!.Best!, python: true);
            sb.AppendLine(x.Feature.Kind == FieldKind.CheckBox
                ? $"    {locator}.set_checked(values[\"{x.Key}\"])"
                : $"    {locator}.fill(values[\"{x.Key}\"])");
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def send(page):");
        sb.AppendLine(action is null
            ? $"    {WebCodeTemplates.PlaywrightLocator(fields.Last(x => x.Feature.Kind != FieldKind.CheckBox).Web!.Best!, python: true)}.press(\"Enter\")"
            : $"    {WebCodeTemplates.PlaywrightLocator(action.Best!, python: true)}.click()");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def check_expected(page, case_id, expected):");
        sb.AppendLine("    \"\"\"Your app's own checks, once you know its messages. For example:");
        sb.AppendLine("    if case_id == \"LOGIN-02\": assert page.get_by_text(\"Wrong user name or password\").is_visible()\"\"\"");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("@pytest.fixture(scope=\"module\")");
        sb.AppendLine("def browser():");
        sb.AppendLine("    with sync_playwright() as p:");
        sb.AppendLine($"        browser = {Launch(page, python: true)}");
        sb.AppendLine("        yield browser");
        sb.AppendLine("        browser.close()");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("@pytest.mark.parametrize(\"case_id, title, field, value, expected\", CASES, ids=[c[0] for c in CASES])");
        sb.AppendLine("def test_case(browser, case_id, title, field, value, expected):");
        sb.AppendLine("    page = browser.new_page()");
        sb.AppendLine("    alerts, server_errors = [], []");
        sb.AppendLine("    page.on(\"dialog\", lambda d: (alerts.append(d.message), d.dismiss()))");
        sb.AppendLine("    page.on(\"response\", lambda r: server_errors.append(f\"{r.status} {r.url}\") if r.status >= 500 else None)");
        sb.AppendLine("    try:");
        sb.AppendLine("        page.goto(URL)");
        sb.AppendLine("        values = dict(VALID)");
        sb.AppendLine("        if field == \"*\":  # every field empty");
        sb.AppendLine("            values = {k: False if isinstance(v, bool) else \"\" for k, v in VALID.items()}");
        sb.AppendLine("        elif field is not None:");
        sb.AppendLine("            values[field] = value");
        sb.AppendLine("        fill(page, values)");
        sb.AppendLine("        send(page)");
        sb.AppendLine("        try:");
        sb.AppendLine("            page.wait_for_load_state(\"networkidle\", timeout=5000)");
        sb.AppendLine("        except Exception:");
        sb.AppendLine("            pass  # pages that keep a connection open never go idle");
        sb.AppendLine("        RESULTS.mkdir(exist_ok=True)");
        sb.AppendLine("        page.screenshot(path=RESULTS / f\"{case_id}.png\", full_page=True)");
        sb.AppendLine();
        sb.AppendLine("        problems = []");
        sb.AppendLine("        if alerts:");
        sb.AppendLine("            problems.append(f\"an alert appeared ({alerts[0]!r}): the page ran code typed into a field (XSS)\")");
        sb.AppendLine("        if value and \"<script\" in value and value in page.content():");
        sb.AppendLine("            problems.append(\"the input is in the page as HTML, not as text (XSS, even if no alert showed)\")");
        sb.AppendLine("        error = ERROR_TEXT.search(page.inner_text(\"body\"))");
        sb.AppendLine("        if error:");
        sb.AppendLine("            problems.append(f\"error text on the page: {error.group(0)!r} (database or crash details)\")");
        sb.AppendLine("        if server_errors:");
        sb.AppendLine("            problems.append(f\"the server failed: {server_errors[0]}\")");
        sb.AppendLine("        if PASSWORD_FIELD and values[PASSWORD_FIELD] and values[PASSWORD_FIELD] in page.url:");
        sb.AppendLine("            problems.append(\"the password is in the address\")");
        sb.AppendLine("        assert not problems, f\"{case_id} {title}: \" + \"; \".join(problems) + f\"\\nExpected: {expected}\"");
        sb.AppendLine("        check_expected(page, case_id, expected)");
        sb.AppendLine("    finally:");
        sb.AppendLine("        page.close()");
        if (password is not null)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("def test_password_is_hidden(browser):");
            sb.AppendLine("    page = browser.new_page()");
            sb.AppendLine("    page.goto(URL)");
            sb.AppendLine($"    assert {WebCodeTemplates.PlaywrightLocator(password.Web!.Best!, python: true)}.get_attribute(\"type\") == \"password\", \"the password field shows what's typed\"");
            sb.AppendLine("    page.close()");
        }
        if (f.Kind == FeatureKind.Login && password is not null && fields.FirstOrDefault(x => x.Env == "UIINSPECTOR_USER") is { } user)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("def message_after(browser, values):");
            sb.AppendLine("    page = browser.new_page()");
            sb.AppendLine("    page.goto(URL)");
            sb.AppendLine("    fill(page, values)");
            sb.AppendLine("    send(page)");
            sb.AppendLine("    page.wait_for_timeout(2000)");
            sb.AppendLine("    text = page.inner_text(\"body\")");
            sb.AppendLine("    page.close()");
            sb.AppendLine("    return text");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("def test_unknown_user_and_wrong_password_get_the_same_message(browser):");
            sb.AppendLine($"    \"\"\"Different messages tell attackers which accounts exist. Needs UIINSPECTOR_USER (a real test user).\"\"\"");
            sb.AppendLine("    if \"UIINSPECTOR_USER\" not in os.environ:");
            sb.AppendLine("        pytest.skip(\"set UIINSPECTOR_USER to a real test user\")");
            sb.AppendLine($"    wrong_password = message_after(browser, {{**VALID, \"{password.Key}\": \"Wrong#Pass0000\"}})");
            sb.AppendLine($"    unknown_user = message_after(browser, {{**VALID, \"{user.Key}\": \"nobody-7f3k@example.com\", \"{password.Key}\": \"Wrong#Pass0000\"}})");
            sb.AppendLine("    assert wrong_password == unknown_user, \"the page answers differently for an unknown user than for a wrong password\"");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Playwright Test (JavaScript)

    private static string PlaywrightJs(TestFeature f, List<Field> fields, WebResult? action, List<Case> cases, List<TestCase> manual, WebResult page)
    {
        var sb = new StringBuilder();
        var file = Slug(f.Title) + ".spec.js";
        sb.Append(Header(f, "//", cases.Count, manual,
        [
            page.Browser == BrowserFlavor.Firefox ? "npm i -D @playwright/test, then once: npx playwright install firefox" : "npm i -D @playwright/test   (uses the installed browser)",
            $"Save as tests/{file}, run:  npx playwright test {file}      (set HEADLESS=1 to run without a window)",
            fields.Any(x => x.Env is not null) ? $"Set {string.Join(" and ", fields.Where(x => x.Env is not null).Select(x => x.Env).Distinct())} to a test account." : "",
        ]));
        sb.AppendLine("const { test, expect } = require('@playwright/test');");
        sb.AppendLine();
        sb.AppendLine(page.Browser == BrowserFlavor.Firefox
            ? "test.use({ browserName: 'firefox', headless: process.env.HEADLESS === '1' });"
            : $"test.use({{ channel: '{Launch(page, python: false)}', headless: process.env.HEADLESS === '1' }});");
        sb.AppendLine();
        sb.AppendLine($"const URL = {WebCodeTemplates.JsString(page.PageUrl)};");
        sb.AppendLine($"const ERROR_TEXT = /{ErrorPattern.Replace("/", "\\/")}/i;");
        sb.AppendLine();
        sb.AppendLine("// Valid data for every field; each case replaces one of them.");
        sb.AppendLine("const VALID = {");
        foreach (var x in fields)
        {
            var value = x.Env is null ? Js(x.Sample) : $"process.env.{x.Env} ?? {Js(x.Sample)}";
            sb.AppendLine($"  {x.Key}: {(x.Feature.Kind == FieldKind.CheckBox ? "true" : value)},");
        }
        sb.AppendLine("};");
        var password = fields.FirstOrDefault(x => x.Feature.Kind == FieldKind.Password);
        sb.AppendLine($"const PASSWORD_FIELD = {(password is null ? "null" : $"'{password.Key}'")};");
        sb.AppendLine();
        sb.AppendLine("// [id, title, field, value, expected]: field null runs the valid data as it is.");
        sb.AppendLine("const CASES = [");
        foreach (var c in cases) sb.AppendLine($"  [{Js(c.Id)}, {Js(c.Title)}, {(c.Key is null ? "null" : $"'{c.Key}'")}, {Js(c.Value)}, {Js(c.Expected)}],");
        sb.AppendLine("];");
        sb.AppendLine();
        sb.AppendLine("async function fill(page, values) {");
        foreach (var x in fields)
        {
            var locator = WebCodeTemplates.PlaywrightLocator(x.Web!.Best!, python: false);
            sb.AppendLine(x.Feature.Kind == FieldKind.CheckBox
                ? $"  await {locator}.setChecked(values.{x.Key});"
                : $"  await {locator}.fill(values.{x.Key});");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("async function send(page) {");
        sb.AppendLine(action is null
            ? $"  await {WebCodeTemplates.PlaywrightLocator(fields.Last(x => x.Feature.Kind != FieldKind.CheckBox).Web!.Best!, python: false)}.press('Enter');"
            : $"  await {WebCodeTemplates.PlaywrightLocator(action.Best!, python: false)}.click();");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("// Your app's own checks, once you know its messages. For example:");
        sb.AppendLine("// if (id === 'LOGIN-02') await expect(page.getByText('Wrong user name or password')).toBeVisible();");
        sb.AppendLine("async function checkExpected(page, id, expected) {}");
        sb.AppendLine();
        sb.AppendLine("for (const [id, title, field, value, expected] of CASES) {");
        sb.AppendLine("  test(`${id} ${title}`, async ({ page }) => {");
        sb.AppendLine("    const alerts = [], serverErrors = [];");
        sb.AppendLine("    page.on('dialog', d => { alerts.push(d.message()); d.dismiss().catch(() => {}); });");
        sb.AppendLine("    page.on('response', r => { if (r.status() >= 500) serverErrors.push(`${r.status()} ${r.url()}`); });");
        sb.AppendLine("    await page.goto(URL);");
        sb.AppendLine("    const values = { ...VALID };");
        sb.AppendLine("    if (field === '*') for (const k of Object.keys(values)) values[k] = typeof values[k] === 'boolean' ? false : ''; // every field empty");
        sb.AppendLine("    else if (field !== null) values[field] = value;");
        sb.AppendLine("    await fill(page, values);");
        sb.AppendLine("    await send(page);");
        sb.AppendLine("    await page.waitForLoadState('networkidle', { timeout: 5000 }).catch(() => {}); // some pages never go idle");
        sb.AppendLine("    await page.screenshot({ path: `results/${id}.png`, fullPage: true });");
        sb.AppendLine();
        sb.AppendLine("    const problems = [];");
        sb.AppendLine("    if (alerts.length) problems.push(`an alert appeared (${alerts[0]}): the page ran code typed into a field (XSS)`);");
        sb.AppendLine("    if (value && value.includes('<script') && (await page.content()).includes(value)) problems.push('the input is in the page as HTML, not as text (XSS, even if no alert showed)');");
        sb.AppendLine("    const error = (await page.innerText('body')).match(ERROR_TEXT);");
        sb.AppendLine("    if (error) problems.push(`error text on the page: \"${error[0]}\" (database or crash details)`);");
        sb.AppendLine("    if (serverErrors.length) problems.push(`the server failed: ${serverErrors[0]}`);");
        sb.AppendLine("    if (PASSWORD_FIELD && values[PASSWORD_FIELD] && page.url().includes(values[PASSWORD_FIELD])) problems.push('the password is in the address');");
        sb.AppendLine("    expect(problems, `${id} ${title}\\nExpected: ${expected}`).toEqual([]);");
        sb.AppendLine("    await checkExpected(page, id, expected);");
        sb.AppendLine("  });");
        sb.AppendLine("}");
        if (password is not null)
        {
            sb.AppendLine();
            sb.AppendLine("test('the password field hides what is typed', async ({ page }) => {");
            sb.AppendLine("  await page.goto(URL);");
            sb.AppendLine($"  await expect({WebCodeTemplates.PlaywrightLocator(password.Web!.Best!, python: false)}).toHaveAttribute('type', 'password');");
            sb.AppendLine("});");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- desktop: Python uiautomation (pytest)

    private static string UiaPy(TestFeature f, List<Field> fields, List<Case> cases, List<TestCase> manual, SelectorStep window)
    {
        var sb = new StringBuilder();
        sb.Append(Header(f, "#", cases.Count, manual,
        [
            "pip install pytest uiautomation",
            "Run with the app open on this form:  pytest " + Slug(f.Title) + "_test.py -v",
            "Each case starts from the window as it is: if a case leaves the form (a successful login), put the way back in reset().",
        ]));
        sb.AppendLine("import re");
        sb.AppendLine("import time");
        sb.AppendLine("from pathlib import Path");
        sb.AppendLine();
        sb.AppendLine("import pytest");
        sb.AppendLine("import uiautomation as auto");
        sb.AppendLine();
        sb.AppendLine("RESULTS = Path(\"results\")");
        sb.AppendLine($"ERROR_TEXT = re.compile(r\"{ErrorPattern}|unhandled|has stopped working\", re.IGNORECASE)");
        sb.AppendLine();
        sb.AppendLine("VALID = {");
        foreach (var x in fields)
        {
            var value = x.Env is null ? Py(x.Sample) : $"__import__(\"os\").environ.get(\"{x.Env}\", {Py(x.Sample)})";
            sb.AppendLine($"    \"{x.Key}\": {(x.Feature.Kind == FieldKind.CheckBox ? "True" : value)},");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("CASES = [");
        foreach (var c in cases) sb.AppendLine($"    ({Py(c.Id)}, {Py(c.Title)}, {(c.Key is null ? "None" : $"\"{c.Key}\"")}, {Py(c.Value)}, {Py(c.Expected)}),");
        sb.AppendLine("]");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def find(control, what):");
        sb.AppendLine("    if not control.Exists(maxSearchSeconds=10):");
        sb.AppendLine("        raise LookupError(what + \" not found\")");
        sb.AppendLine("    return control");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def main_window():");
        sb.AppendLine($"    return find(auto.{CodeTemplates.ControlClass(window)}({string.Join(", ", CodeTemplates.UiaArgs(window).Prepend("searchDepth=1"))}), \"the window\")");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def set_text(control, text):");
        sb.AppendLine("    pattern = control.GetValuePattern()");
        sb.AppendLine("    if pattern:");
        sb.AppendLine("        pattern.SetValue(text)");
        sb.AppendLine("    else:");
        sb.AppendLine("        control.Click()");
        sb.AppendLine("        control.SendKeys(\"{Ctrl}a{Del}\" + text.replace(\"{\", \"{{}\"), waitTime=0)");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def fill(window, values):");
        foreach (var x in fields)
        {
            var chain = UiaChain(x.Feature.Control.Selector!);
            sb.AppendLine(x.Feature.Kind == FieldKind.CheckBox
                ? $"    box = find({chain}, {Py(x.Feature.Label)})\n    if bool(box.GetTogglePattern().ToggleState) != values[\"{x.Key}\"]:\n        box.GetTogglePattern().Toggle()"
                : $"    set_text(find({chain}, {Py(x.Feature.Label)}), values[\"{x.Key}\"])");
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def send(window):");
        if (f.Action?.Selector is { } action)
        {
            var target = $"find({UiaChain(action)}, {Py(FeatureDetector.Label(f.Action.Element))})";
            sb.AppendLine(f.Action.Element.Patterns.Contains("Invoke") ? $"    {target}.GetInvokePattern().Invoke()" : $"    {target}.Click()");
        }
        else
        {
            sb.AppendLine($"    find({UiaChain(fields.Last(x => x.Feature.Kind != FieldKind.CheckBox).Feature.Control.Selector!)}, \"the last field\").SendKeys(\"{{Enter}}\")");
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def reset(window):");
        sb.AppendLine("    \"\"\"Bring the app back to this form if a case moved away from it.\"\"\"");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def check_expected(window, case_id, expected):");
        sb.AppendLine("    \"\"\"Your app's own checks, once you know its messages.\"\"\"");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def visible_texts(control, depth=12):");
        sb.AppendLine("    return [c.Name for c, _ in auto.WalkControl(control, maxDepth=depth) if c.Name]");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("@pytest.mark.parametrize(\"case_id, title, field, value, expected\", CASES, ids=[c[0] for c in CASES])");
        sb.AppendLine("def test_case(case_id, title, field, value, expected):");
        sb.AppendLine("    window = main_window()");
        sb.AppendLine("    reset(window)");
        sb.AppendLine("    before = {w.NativeWindowHandle for w in auto.GetRootControl().GetChildren()}");
        sb.AppendLine("    values = dict(VALID)");
        sb.AppendLine("    if field == \"*\":  # every field empty");
        sb.AppendLine("        values = {k: False if isinstance(v, bool) else \"\" for k, v in VALID.items()}");
        sb.AppendLine("    elif field is not None:");
        sb.AppendLine("        values[field] = value");
        sb.AppendLine("    fill(window, values)");
        sb.AppendLine("    send(window)");
        sb.AppendLine("    time.sleep(1)");
        sb.AppendLine("    RESULTS.mkdir(exist_ok=True)");
        sb.AppendLine();
        sb.AppendLine("    problems = []");
        sb.AppendLine("    if not window.Exists(maxSearchSeconds=1):");
        sb.AppendLine("        problems.append(\"the window is gone (the app closed or crashed)\")");
        sb.AppendLine("    else:");
        sb.AppendLine("        window.CaptureToImage(str(RESULTS / f\"{case_id}.png\"))");
        sb.AppendLine("    # New windows of the app (message boxes): an error dialog is a failure.");
        sb.AppendLine("    for w in auto.GetRootControl().GetChildren():");
        sb.AppendLine("        if w.NativeWindowHandle not in before and w.ProcessId == window.ProcessId:");
        sb.AppendLine("            text = \" \".join(visible_texts(w, 4))");
        sb.AppendLine("            if ERROR_TEXT.search(text) or re.search(r\"\\berror\\b|exception\", w.Name, re.IGNORECASE):");
        sb.AppendLine("                problems.append(f\"an error window appeared: {w.Name!r}: {text[:200]!r}\")");
        sb.AppendLine("            w.GetWindowPattern().Close() if w.GetWindowPattern() else None");
        sb.AppendLine("    if window.Exists(0, 0):");
        sb.AppendLine("        error = ERROR_TEXT.search(\" \".join(visible_texts(window)))");
        sb.AppendLine("        if error:");
        sb.AppendLine("            problems.append(f\"error text in the window: {error.group(0)!r}\")");
        sb.AppendLine("    assert not problems, f\"{case_id} {title}: \" + \"; \".join(problems) + f\"\\nExpected: {expected}\"");
        sb.AppendLine("    check_expected(window, case_id, expected)");
        return sb.ToString();
    }

    private static string UiaChain(Selector s) =>
        "main_window()" + string.Concat(s.Steps.Select(step => $".{CodeTemplates.ControlClass(step)}({string.Join(", ", CodeTemplates.UiaArgs(step))})"));

    // ---------------------------------------------------------------- desktop: FlaUI (C#, xUnit)

    private static string FlaUiXunit(TestFeature f, List<Field> fields, List<Case> cases, List<TestCase> manual, SelectorStep window)
    {
        var sb = new StringBuilder();
        var className = Pascal(f.Title) + "Suite";
        sb.Append(Header(f, "//", cases.Count, manual,
        [
            "NuGet: FlaUI.UIA3, xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk (a test project)",
            "Run with the app open on this form:  dotnet test --logger \"console;verbosity=normal\"",
            "Each case starts from the window as it is: if a case leaves the form (a successful login), put the way back in Reset().",
        ]));
        sb.AppendLine("using System.Text.RegularExpressions;");
        sb.AppendLine("using FlaUI.Core.AutomationElements;");
        sb.AppendLine("using FlaUI.Core.Definitions;");
        sb.AppendLine("using FlaUI.Core.Input;");
        sb.AppendLine("using FlaUI.Core.Tools;");
        sb.AppendLine("using FlaUI.UIA3;");
        sb.AppendLine("using Xunit;");
        sb.AppendLine();
        sb.AppendLine($"public class {className} : IDisposable");
        sb.AppendLine("{");
        sb.AppendLine($"    private static readonly Regex ErrorText = new(@\"{ErrorPattern.Replace("\"", "\"\"")}|unhandled|has stopped working\", RegexOptions.IgnoreCase);");
        sb.AppendLine("    private readonly UIA3Automation automation = new();");
        sb.AppendLine("    private readonly FlaUI.Core.Conditions.ConditionFactory cf;");
        sb.AppendLine("    private readonly TimeSpan timeout = TimeSpan.FromSeconds(10);");
        sb.AppendLine();
        sb.AppendLine($"    public {className}() => cf = automation.ConditionFactory;");
        sb.AppendLine();
        sb.AppendLine("    public void Dispose() => automation.Dispose();");
        sb.AppendLine();
        sb.AppendLine("    private static Dictionary<string, string> Valid() => new()");
        sb.AppendLine("    {");
        foreach (var x in fields.Where(x => x.Feature.Kind != FieldKind.CheckBox))
        {
            var value = x.Env is null ? Cs(x.Sample) : $"Environment.GetEnvironmentVariable(\"{x.Env}\") ?? {Cs(x.Sample)}";
            sb.AppendLine($"        [\"{x.Key}\"] = {value},");
        }
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    // id, title, field (null: the valid data as it is), value, expected");
        sb.AppendLine("    public static TheoryData<string, string, string?, string?, string> Cases => new()");
        sb.AppendLine("    {");
        foreach (var c in cases) sb.AppendLine($"        {{ {Cs(c.Id)}, {Cs(c.Title)}, {(c.Key is null ? "null" : $"\"{c.Key}\"")}, {Cs(c.Value)}, {Cs(c.Expected)} }},");
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    private AutomationElement Find(Func<AutomationElement?> search, string what) =>");
        sb.AppendLine("        Retry.WhileNull(search, timeout).Result ?? throw new InvalidOperationException($\"{what} not found.\");");
        sb.AppendLine();
        sb.AppendLine("    private AutomationElement MainWindow() =>");
        sb.AppendLine($"        Find(() => {CodeTemplates.FlaUiFind("automation.GetDesktop()", window, children: true)}, \"The window\");");
        foreach (var x in fields)
        {
            sb.AppendLine();
            sb.AppendLine($"    private AutomationElement Field_{x.Key}(AutomationElement window)");
            sb.AppendLine("    {");
            AppendFlaUiChain(sb, x.Feature.Control.Selector!, x.Feature.Label);
            sb.AppendLine("    }");
        }
        if (f.Action?.Selector is { } actionSelector)
        {
            sb.AppendLine();
            sb.AppendLine("    private AutomationElement SendButton(AutomationElement window)");
            sb.AppendLine("    {");
            AppendFlaUiChain(sb, actionSelector, FeatureDetector.Label(f.Action.Element));
            sb.AppendLine("    }");
        }
        sb.AppendLine();
        sb.AppendLine("    private static void SetText(AutomationElement field, string text)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (field.Patterns.Value.IsSupported) { field.Patterns.Value.Pattern.SetValue(text); return; }");
        sb.AppendLine("        field.Focus();");
        sb.AppendLine("        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);");
        sb.AppendLine("        Keyboard.Type(text);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void Fill(AutomationElement window, Dictionary<string, string> values)");
        sb.AppendLine("    {");
        foreach (var x in fields)
        {
            sb.AppendLine(x.Feature.Kind == FieldKind.CheckBox
                ? $"        if (Field_{x.Key}(window).Patterns.Toggle.Pattern.ToggleState.Value != ToggleState.On) Field_{x.Key}(window).Patterns.Toggle.Pattern.Toggle();"
                : $"        SetText(Field_{x.Key}(window), values[\"{x.Key}\"]);");
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void Send(AutomationElement window)");
        sb.AppendLine("    {");
        if (f.Action?.Selector is not null)
        {
            sb.AppendLine(f.Action.Element.Patterns.Contains("Invoke") ? "        SendButton(window).Patterns.Invoke.Pattern.Invoke();" : "        SendButton(window).Click();");
        }
        else
        {
            sb.AppendLine($"        Field_{fields.Last(x => x.Feature.Kind != FieldKind.CheckBox).Key}(window).Focus();");
            sb.AppendLine("        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);");
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Brings the app back to this form if a case moved away from it.</summary>");
        sb.AppendLine("    private void Reset(AutomationElement window) { }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Your app's own checks, once you know its messages.</summary>");
        sb.AppendLine("    private void CheckExpected(AutomationElement window, string id, string expected) { }");
        sb.AppendLine();
        sb.AppendLine("    [Theory]");
        sb.AppendLine("    [MemberData(nameof(Cases))]");
        sb.AppendLine("    public void Case(string id, string title, string? field, string? value, string expected)");
        sb.AppendLine("    {");
        sb.AppendLine("        var window = MainWindow();");
        sb.AppendLine("        Reset(window);");
        sb.AppendLine("        var before = automation.GetDesktop().FindAllChildren().Select(w => w.Properties.NativeWindowHandle.ValueOrDefault).ToHashSet();");
        sb.AppendLine("        var values = Valid();");
        sb.AppendLine("        if (field == \"*\") foreach (var key in values.Keys.ToList()) values[key] = \"\"; // every field empty");
        sb.AppendLine("        else if (field is not null) values[field] = value ?? \"\";");
        sb.AppendLine("        Fill(window, values);");
        sb.AppendLine("        Send(window);");
        sb.AppendLine("        Thread.Sleep(1000);");
        sb.AppendLine();
        sb.AppendLine("        var problems = new List<string>();");
        sb.AppendLine("        Directory.CreateDirectory(\"results\");");
        sb.AppendLine("        try { FlaUI.Core.Capturing.Capture.Element(window).ToFile(Path.Combine(\"results\", $\"{id}.png\")); }");
        sb.AppendLine("        catch (Exception) { problems.Add(\"the window is gone (the app closed or crashed)\"); }");
        sb.AppendLine("        // New windows of the app (message boxes): an error dialog is a failure.");
        sb.AppendLine("        foreach (var w in automation.GetDesktop().FindAllChildren())");
        sb.AppendLine("        {");
        sb.AppendLine("            if (before.Contains(w.Properties.NativeWindowHandle.ValueOrDefault) || w.Properties.ProcessId.ValueOrDefault != window.Properties.ProcessId.ValueOrDefault) continue;");
        sb.AppendLine("            var text = string.Join(\" \", w.FindAllDescendants(cf.ByControlType(ControlType.Text)).Select(t => t.Name));");
        sb.AppendLine("            if (ErrorText.IsMatch(text) || Regex.IsMatch(w.Name, @\"\\berror\\b|exception\", RegexOptions.IgnoreCase))");
        sb.AppendLine("                problems.Add($\"an error window appeared: '{w.Name}': {text}\");");
        sb.AppendLine("            if (w.Patterns.Window.IsSupported) w.Patterns.Window.Pattern.Close();");
        sb.AppendLine("        }");
        sb.AppendLine("        var texts = string.Join(\" \", window.FindAllDescendants(cf.ByControlType(ControlType.Text)).Select(t => t.Name));");
        sb.AppendLine("        if (ErrorText.Match(texts) is { Success: true } error) problems.Add($\"error text in the window: '{error.Value}'\");");
        sb.AppendLine("        Assert.True(problems.Count == 0, $\"{id} {title}: {string.Join(\"; \", problems)}\\nExpected: {expected}\");");
        sb.AppendLine("        CheckExpected(window, id, expected);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendFlaUiChain(StringBuilder sb, Selector s, string label)
    {
        var scope = "window";
        for (var i = 0; i < s.Steps.Count; i++)
        {
            var name = i == s.Steps.Count - 1 ? "target" : $"scope{i + 1}";
            sb.AppendLine($"        var {name} = Find(() => {CodeTemplates.FlaUiFind(scope, s.Steps[i], children: false)}, {CodeTemplates.Cs(label)});");
            scope = name;
        }
        sb.AppendLine(s.Steps.Count == 0 ? "        return window;" : "        return target;");
    }

    // ---------------------------------------------------------------- links (web)

    private static IReadOnlyList<CodeSnippet> LinkSuites(TestFeature f, Func<FeatureControl, WebResult?> web)
    {
        var links = f.Controls
            .Take(MaxLinks)
            .Select(c => (Control: c, Web: web(c)))
            .Where(x => x.Web is { Connected: true, Best: not null })
            .ToList();
        if (links.Count == 0 || links[0].Web is not { Launch.Kind: WebAppKind.Browser } page) return [];
        var id = f.Kind == FeatureKind.Navigation ? "NAV" : "LIST";

        var py = new StringBuilder();
        py.Append(Header(f, "#", links.Count, [],
        [
            page.Browser == BrowserFlavor.Firefox ? "pip install pytest playwright, then once: playwright install firefox" : "pip install pytest playwright   (uses the installed browser)",
            "Run:  pytest " + Slug(f.Title) + "_test.py -v      (set HEADLESS=1 to run without a window)",
            "Each link is clicked from a fresh page; it must open without an error page, server error or crash text.",
        ]));
        py.AppendLine("import os");
        py.AppendLine("import re");
        py.AppendLine("from pathlib import Path");
        py.AppendLine();
        py.AppendLine("import pytest");
        py.AppendLine("from playwright.sync_api import sync_playwright");
        py.AppendLine();
        py.AppendLine($"URL = {WebCodeTemplates.PyString(page.PageUrl)}");
        py.AppendLine("HEADLESS = os.environ.get(\"HEADLESS\") == \"1\"");
        py.AppendLine("RESULTS = Path(\"results\")");
        py.AppendLine($"ERROR_TEXT = re.compile(r\"{ErrorPattern}|\\b404\\b|not found|page doesn't exist\", re.IGNORECASE)");
        py.AppendLine();
        py.AppendLine("LINKS = [");
        for (var i = 0; i < links.Count; i++)
        {
            py.AppendLine($"    (\"{id}-L{i + 1:00}\", {Py(FeatureDetector.Label(links[i].Control.Element))}, lambda page: {WebCodeTemplates.PlaywrightLocator(links[i].Web!.Best!, python: true)}),");
        }
        py.AppendLine("]");
        py.AppendLine();
        py.AppendLine();
        py.AppendLine("@pytest.fixture(scope=\"module\")");
        py.AppendLine("def browser():");
        py.AppendLine("    with sync_playwright() as p:");
        py.AppendLine($"        browser = {Launch(page, python: true)}");
        py.AppendLine("        yield browser");
        py.AppendLine("        browser.close()");
        py.AppendLine();
        py.AppendLine();
        py.AppendLine("@pytest.mark.parametrize(\"case_id, name, link\", LINKS, ids=[l[0] for l in LINKS])");
        py.AppendLine("def test_link_opens(browser, case_id, name, link):");
        py.AppendLine("    page = browser.new_page()");
        py.AppendLine("    failed = []");
        py.AppendLine("    page.on(\"response\", lambda r: failed.append(f\"{r.status} {r.url}\") if r.request.resource_type == \"document\" and r.status >= 400 else None)");
        py.AppendLine("    try:");
        py.AppendLine("        page.goto(URL)");
        py.AppendLine("        link(page).click()");
        py.AppendLine("        try:");
        py.AppendLine("            page.wait_for_load_state(\"networkidle\", timeout=5000)");
        py.AppendLine("        except Exception:");
        py.AppendLine("            pass");
        py.AppendLine("        RESULTS.mkdir(exist_ok=True)");
        py.AppendLine("        page.screenshot(path=RESULTS / f\"{case_id}.png\")");
        py.AppendLine("        problems = []");
        py.AppendLine("        if failed:");
        py.AppendLine("            problems.append(f\"the page failed to load: {failed[0]}\")");
        py.AppendLine("        error = ERROR_TEXT.search(page.title() + \" \" + page.inner_text(\"body\")[:2000])");
        py.AppendLine("        if error:");
        py.AppendLine("            problems.append(f\"error text: {error.group(0)!r}\")");
        py.AppendLine("        assert not problems, f\"{case_id} {name!r}: \" + \"; \".join(problems)");
        py.AppendLine("    finally:");
        py.AppendLine("        page.close()");

        var js = new StringBuilder();
        var file = Slug(f.Title) + ".spec.js";
        js.Append(Header(f, "//", links.Count, [],
        [
            page.Browser == BrowserFlavor.Firefox ? "npm i -D @playwright/test, then once: npx playwright install firefox" : "npm i -D @playwright/test   (uses the installed browser)",
            $"Save as tests/{file}, run:  npx playwright test {file}      (set HEADLESS=1 to run without a window)",
            "Each link is clicked from a fresh page; it must open without an error page, server error or crash text.",
        ]));
        js.AppendLine("const { test, expect } = require('@playwright/test');");
        js.AppendLine();
        js.AppendLine(page.Browser == BrowserFlavor.Firefox
            ? "test.use({ browserName: 'firefox', headless: process.env.HEADLESS === '1' });"
            : $"test.use({{ channel: '{Launch(page, python: false)}', headless: process.env.HEADLESS === '1' }});");
        js.AppendLine();
        js.AppendLine($"const URL = {WebCodeTemplates.JsString(page.PageUrl)};");
        js.AppendLine($"const ERROR_TEXT = /{ErrorPattern.Replace("/", "\\/")}|\\b404\\b|not found|page doesn't exist/i;");
        js.AppendLine();
        js.AppendLine("const LINKS = [");
        for (var i = 0; i < links.Count; i++)
        {
            js.AppendLine($"  ['{id}-L{i + 1:00}', {Js(FeatureDetector.Label(links[i].Control.Element))}, page => {WebCodeTemplates.PlaywrightLocator(links[i].Web!.Best!, python: false)}],");
        }
        js.AppendLine("];");
        js.AppendLine();
        js.AppendLine("for (const [id, name, link] of LINKS) {");
        js.AppendLine("  test(`${id} ${name} opens`, async ({ page }) => {");
        js.AppendLine("    const failed = [];");
        js.AppendLine("    page.on('response', r => { if (r.request().resourceType() === 'document' && r.status() >= 400) failed.push(`${r.status()} ${r.url()}`); });");
        js.AppendLine("    await page.goto(URL);");
        js.AppendLine("    await link(page).click();");
        js.AppendLine("    await page.waitForLoadState('networkidle', { timeout: 5000 }).catch(() => {});");
        js.AppendLine("    await page.screenshot({ path: `results/${id}.png` });");
        js.AppendLine("    const problems = [];");
        js.AppendLine("    if (failed.length) problems.push(`the page failed to load: ${failed[0]}`);");
        js.AppendLine("    const error = ((await page.title()) + ' ' + (await page.innerText('body')).slice(0, 2000)).match(ERROR_TEXT);");
        js.AppendLine("    if (error) problems.push(`error text: \"${error[0]}\"`);");
        js.AppendLine("    expect(problems, `${id} ${name}`).toEqual([]);");
        js.AppendLine("  });");
        js.AppendLine("}");

        return
        [
            new CodeSnippet("suite-playwright-py", "Test suite · Playwright (Python, pytest)", py.ToString()),
            new CodeSnippet("suite-playwright-js", "Test suite · Playwright Test (JavaScript)", js.ToString()),
        ];
    }

    // ---------------------------------------------------------------- names

    private static string Slug(string title)
    {
        var slug = string.Join('_', new string(title.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4));
        return slug.Length == 0 ? "feature" : slug;
    }

    private static string Pascal(string title)
    {
        var words = new string(title.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4);
        var name = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return name.Length == 0 || char.IsDigit(name[0]) ? "Feature" + name : name;
    }
}

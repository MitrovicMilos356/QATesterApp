using System.Text;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Web;

/// <summary>
/// Playwright and Selenium code for a verified web selector. Every snippet attaches to the
/// already-running app over its remote-debugging port (nothing is launched), finds the page by
/// URL, enters iframes (and, for Selenium, shadow roots), then clicks or fills the element.
/// </summary>
public static class WebCodeTemplates
{
    private const string SampleText = "Hello from UiInspector";
    private const int WaitSeconds = 10;

    public static IReadOnlyList<CodeSnippet> Render(WebResult web, WebSelector? playwright = null)
    {
        var snippets = new List<CodeSnippet>();
        if (!web.Connected || web.Element is null) return snippets;

        if ((playwright ?? web.Best) is { } pw)
        {
            snippets.Add(new("playwright-js", "Playwright (JavaScript)", PlaywrightJs(web, pw)));
            snippets.Add(new("playwright-py", "Playwright (Python)", PlaywrightPy(web, pw)));
            snippets.Add(new("playwright-cs", "Playwright (C#)", PlaywrightCs(web, pw)));
        }
        if (web.Selenium is { } se)
        {
            snippets.Add(new("selenium-py", "Selenium (Python)", SeleniumPy(web, se)));
            snippets.Add(new("selenium-java", "Selenium (Java)", SeleniumJava(web, se)));
        }
        return snippets;
    }

    private static bool IsFillable(WebResult web) =>
        web.Element is { } e && (e.Tag is "input" or "textarea" || e.Role is "textbox" or "searchbox")
        && !(e.Attributes.TryGetValue("type", out var type) && type is "checkbox" or "radio" or "button" or "submit" or "password");

    private static string Endpoint(WebResult web) => $"http://127.0.0.1:{web.Port}";

    // ------------------------------------------------------------------ Playwright: JavaScript

    private static string PlaywrightJs(WebResult web, WebSelector s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// npm i playwright-core   (attaches to the running app; no browser download needed)");
        Header(sb, "//", web, s.Strategy, s.Verification, s.Warnings);
        sb.AppendLine("const { chromium } = require('playwright-core');");
        sb.AppendLine();
        sb.AppendLine("(async () => {");
        sb.AppendLine($"  const browser = await chromium.connectOverCDP({Js(Endpoint(web))});");
        sb.AppendLine($"  const page = browser.contexts().flatMap(c => c.pages()).find(p => p.url().startsWith({Js(web.UrlPrefix)}));");
        sb.AppendLine($"  if (!page) throw new Error({Js("Page not found: " + web.UrlPrefix)});");
        sb.AppendLine();
        sb.AppendLine($"  const target = {PwChain(s, "page", PwFlavor.Js)};");
        sb.AppendLine(IsFillable(web) ? $"  await target.fill({Js(SampleText)});" : "  await target.click();");
        sb.AppendLine();
        sb.AppendLine("  await browser.close(); // disconnects only; the app keeps running");
        sb.AppendLine("})();");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Playwright: Python

    private static string PlaywrightPy(WebResult web, WebSelector s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# pip install playwright   (attaches to the running app; no browser download needed)");
        Header(sb, "#", web, s.Strategy, s.Verification, s.Warnings);
        sb.AppendLine("from playwright.sync_api import sync_playwright");
        sb.AppendLine();
        sb.AppendLine("with sync_playwright() as p:");
        sb.AppendLine($"    browser = p.chromium.connect_over_cdp({Py(Endpoint(web))})");
        sb.AppendLine($"    page = next((pg for ctx in browser.contexts for pg in ctx.pages if pg.url.startswith({Py(web.UrlPrefix)})), None)");
        sb.AppendLine("    if page is None:");
        sb.AppendLine($"        raise LookupError({Py("Page not found: " + web.UrlPrefix)})");
        sb.AppendLine();
        sb.AppendLine($"    target = {PwChain(s, "page", PwFlavor.Py)}");
        sb.AppendLine(IsFillable(web) ? $"    target.fill({Py(SampleText)})" : "    target.click()");
        sb.AppendLine("    browser.close()  # disconnects only; the app keeps running");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Playwright: C#

    private static string PlaywrightCs(WebResult web, WebSelector s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// NuGet: Microsoft.Playwright   (attaches to the running app; no browser download needed)");
        Header(sb, "//", web, s.Strategy, s.Verification, s.Warnings);
        sb.AppendLine("using Microsoft.Playwright;");
        sb.AppendLine();
        sb.AppendLine("using var playwright = await Playwright.CreateAsync();");
        sb.AppendLine($"var browser = await playwright.Chromium.ConnectOverCDPAsync({Cs(Endpoint(web))});");
        sb.AppendLine($"var page = browser.Contexts.SelectMany(c => c.Pages).FirstOrDefault(p => p.Url.StartsWith({Cs(web.UrlPrefix)}))");
        sb.AppendLine($"    ?? throw new InvalidOperationException({Cs("Page not found: " + web.UrlPrefix)});");
        sb.AppendLine();
        sb.AppendLine($"var target = {PwChain(s, "page", PwFlavor.Cs)};");
        sb.AppendLine(IsFillable(web) ? $"await target.FillAsync({Cs(SampleText)});" : "await target.ClickAsync();");
        sb.AppendLine("await browser.CloseAsync(); // disconnects only; the app keeps running");
        return sb.ToString();
    }

    private enum PwFlavor { Js, Py, Cs }

    private static string PwChain(WebSelector s, string start, PwFlavor f)
    {
        var sb = new StringBuilder(start);
        foreach (var frame in s.Frames)
        {
            sb.Append(f switch
            {
                PwFlavor.Js => $".frameLocator({Js(frame.Value)})",
                PwFlavor.Py => $".frame_locator({Py(frame.Value)})",
                _ => $".FrameLocator({Cs(frame.Value)})",
            });
            if (frame.Index is { } fi) sb.Append(f == PwFlavor.Cs ? $".Nth({fi})" : $".nth({fi})");
        }
        foreach (var step in s.Steps)
        {
            sb.Append(PwStep(step, f));
            if (step.Index is { } i) sb.Append(f == PwFlavor.Cs ? $".Nth({i})" : $".nth({i})");
        }
        return sb.ToString();
    }

    private static string PwStep(WebLocator l, PwFlavor f) => (l.Kind, f) switch
    {
        (WebLocatorKind.TestId, _) when l.TestIdAttribute != "data-testid" => PwCss(l.AsCss!, f),
        (WebLocatorKind.TestId, PwFlavor.Js) => $".getByTestId({Js(l.Value)})",
        (WebLocatorKind.TestId, PwFlavor.Py) => $".get_by_test_id({Py(l.Value)})",
        (WebLocatorKind.TestId, _) => $".GetByTestId({Cs(l.Value)})",

        (WebLocatorKind.Role, PwFlavor.Js) => l.Name.Length == 0
            ? $".getByRole({Js(l.Role)})"
            : $".getByRole({Js(l.Role)}, {{ name: {Js(l.Name)}{(l.Exact ? ", exact: true" : "")} }})",
        (WebLocatorKind.Role, PwFlavor.Py) => l.Name.Length == 0
            ? $".get_by_role({Py(l.Role)})"
            : $".get_by_role({Py(l.Role)}, name={Py(l.Name)}{(l.Exact ? ", exact=True" : "")})",
        (WebLocatorKind.Role, _) => l.Name.Length == 0
            ? $".GetByRole(AriaRole.{CsRole(l.Role)})"
            : $".GetByRole(AriaRole.{CsRole(l.Role)}, new() {{ Name = {Cs(l.Name)}{(l.Exact ? ", Exact = true" : "")} }})",

        _ => PwCss(l.Value, f),
    };

    private static string PwCss(string css, PwFlavor f) => f switch
    {
        PwFlavor.Js => $".locator({Js(css)})",
        PwFlavor.Py => $".locator({Py(css)})",
        _ => $".Locator({Cs(css)})",
    };

    // Playwright for .NET names enum members by capitalizing the ARIA role: "listitem" → Listitem.
    private static string CsRole(string role) => char.ToUpperInvariant(role[0]) + role[1..];

    // ------------------------------------------------------------------ Selenium: Python

    private static string SeleniumPy(WebResult web, SeleniumSelector s)
    {
        var edge = web.Browser == BrowserFlavor.Edge;
        var sb = new StringBuilder();
        sb.AppendLine("# pip install selenium   (Selenium Manager downloads the matching driver)");
        Header(sb, "#", web, $"CSS {s.Target}", s.Verification, s.Warnings);
        if (!edge) sb.AppendLine("# Electron apps: chromedriver must match the app's Chromium version, not your Chrome's.");
        sb.AppendLine("from selenium import webdriver");
        sb.AppendLine("from selenium.webdriver.common.by import By");
        sb.AppendLine("from selenium.webdriver.support import expected_conditions as EC");
        sb.AppendLine("from selenium.webdriver.support.ui import WebDriverWait");
        sb.AppendLine();
        sb.AppendLine($"options = webdriver.{(edge ? "EdgeOptions" : "ChromeOptions")}()");
        sb.AppendLine($"options.debugger_address = {Py($"127.0.0.1:{web.Port}")}  # attach to the running app");
        sb.AppendLine($"driver = webdriver.{(edge ? "Edge" : "Chrome")}(options=options)");
        sb.AppendLine($"wait = WebDriverWait(driver, {WaitSeconds})");
        sb.AppendLine();
        sb.AppendLine("for handle in driver.window_handles:");
        sb.AppendLine("    driver.switch_to.window(handle)");
        sb.AppendLine($"    if driver.current_url.startswith({Py(web.UrlPrefix)}):");
        sb.AppendLine("        break");
        sb.AppendLine("else:");
        sb.AppendLine($"    raise LookupError({Py("Page not found: " + web.UrlPrefix)})");
        sb.AppendLine();

        foreach (var frame in s.Frames)
        {
            sb.AppendLine(frame.Index is { } fi
                ? $"driver.switch_to.frame(driver.find_elements(By.CSS_SELECTOR, {Py(frame.Value)})[{fi}])"
                : $"driver.switch_to.frame(wait.until(EC.presence_of_element_located((By.CSS_SELECTOR, {Py(frame.Value)}))))");
        }

        var scope = "driver";
        for (var i = 0; i < s.ShadowHosts.Count; i++)
        {
            var host = s.ShadowHosts[i];
            var find = host.Index is { } hi
                ? $"{scope}.find_elements(By.CSS_SELECTOR, {Py(host.Value)})[{hi}]"
                : $"{scope}.find_element(By.CSS_SELECTOR, {Py(host.Value)})";
            sb.AppendLine($"shadow{i + 1} = wait.until(lambda d: {find}).shadow_root");
            scope = $"shadow{i + 1}";
        }

        var target = s.Target.Index is { } ti
            ? $"wait.until(lambda d: {scope}.find_elements(By.CSS_SELECTOR, {Py(s.Target.Value)})[{ti}])"
            : scope == "driver"
                ? $"wait.until(EC.element_to_be_clickable((By.CSS_SELECTOR, {Py(s.Target.Value)})))"
                : $"wait.until(lambda d: {scope}.find_element(By.CSS_SELECTOR, {Py(s.Target.Value)}))";
        sb.AppendLine($"target = {target}");
        sb.AppendLine(IsFillable(web) ? $"target.send_keys({Py(SampleText)})" : "target.click()");
        sb.AppendLine("# Don't call driver.quit() on an attached session: it can close the app's windows.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Selenium: Java

    private static string SeleniumJava(WebResult web, SeleniumSelector s)
    {
        var edge = web.Browser == BrowserFlavor.Edge;
        var options = edge ? "EdgeOptions" : "ChromeOptions";
        var driverType = edge ? "EdgeDriver" : "ChromeDriver";
        var package = edge ? "edge" : "chrome";
        var sb = new StringBuilder();
        sb.AppendLine("// Maven: org.seleniumhq.selenium:selenium-java (4.x, Java 11+; Selenium Manager downloads the driver)");
        Header(sb, "//", web, $"CSS {s.Target}", s.Verification, s.Warnings);
        if (!edge) sb.AppendLine("// Electron apps: chromedriver must match the app's Chromium version, not your Chrome's.");
        sb.AppendLine("import java.time.Duration;");
        sb.AppendLine("import org.openqa.selenium.*;");
        sb.AppendLine($"import org.openqa.selenium.{package}.{driverType};");
        sb.AppendLine($"import org.openqa.selenium.{package}.{options};");
        sb.AppendLine("import org.openqa.selenium.support.ui.ExpectedConditions;");
        sb.AppendLine("import org.openqa.selenium.support.ui.WebDriverWait;");
        sb.AppendLine();
        sb.AppendLine("public class InspectorSnippet {");
        sb.AppendLine("    public static void main(String[] args) {");
        sb.AppendLine($"        {options} options = new {options}();");
        sb.AppendLine($"        options.setExperimentalOption(\"debuggerAddress\", {Java($"127.0.0.1:{web.Port}")}); // attach to the running app");
        sb.AppendLine($"        WebDriver driver = new {driverType}(options);");
        sb.AppendLine($"        WebDriverWait wait = new WebDriverWait(driver, Duration.ofSeconds({WaitSeconds}));");
        sb.AppendLine();
        sb.AppendLine("        boolean found = false;");
        sb.AppendLine("        for (String handle : driver.getWindowHandles()) {");
        sb.AppendLine("            driver.switchTo().window(handle);");
        sb.AppendLine($"            if (driver.getCurrentUrl().startsWith({Java(web.UrlPrefix)})) {{ found = true; break; }}");
        sb.AppendLine("        }");
        sb.AppendLine($"        if (!found) throw new NoSuchWindowException({Java("Page not found: " + web.UrlPrefix)});");
        sb.AppendLine();

        foreach (var frame in s.Frames)
        {
            sb.AppendLine(frame.Index is { } fi
                ? $"        driver.switchTo().frame(driver.findElements(By.cssSelector({Java(frame.Value)})).get({fi}));"
                : $"        driver.switchTo().frame(wait.until(ExpectedConditions.presenceOfElementLocated(By.cssSelector({Java(frame.Value)}))));");
        }

        var scope = "driver";
        for (var i = 0; i < s.ShadowHosts.Count; i++)
        {
            var host = s.ShadowHosts[i];
            var find = host.Index is { } hi
                ? $"{scope}.findElements(By.cssSelector({Java(host.Value)})).get({hi})"
                : $"{scope}.findElement(By.cssSelector({Java(host.Value)}))";
            sb.AppendLine($"        SearchContext shadow{i + 1} = {find}.getShadowRoot();");
            scope = $"shadow{i + 1}";
        }

        var target = s.Target.Index is { } ti
            ? $"{scope}.findElements(By.cssSelector({Java(s.Target.Value)})).get({ti})"
            : scope == "driver"
                ? $"wait.until(ExpectedConditions.elementToBeClickable(By.cssSelector({Java(s.Target.Value)})))"
                : $"{scope}.findElement(By.cssSelector({Java(s.Target.Value)}))";
        sb.AppendLine($"        WebElement target = {target};");
        sb.AppendLine(IsFillable(web) ? $"        target.sendKeys({Java(SampleText)});" : "        target.click();");
        sb.AppendLine("        // Don't call driver.quit() on an attached session: it can close the app's windows.");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ shared

    private static void Header(StringBuilder sb, string comment, WebResult web, string strategy, SelectorVerification verification, IReadOnlyList<string> warnings)
    {
        sb.AppendLine($"{comment} Target: {web.Element!.Describe()} on {web.UrlPrefix}");
        sb.AppendLine($"{comment} Start the app with --remote-debugging-port={web.Port} (already the case for this one).");
        sb.AppendLine($"{comment} Strategy: {strategy}. {verification.Describe()}");
        foreach (var warning in warnings.Concat(web.Notes)) sb.AppendLine($"{comment} Note: {warning}");
        sb.AppendLine();
    }

    private static string Js(string value) => "'" + Escape(value).Replace("'", "\\'") + "'";

    private static string Py(string value) => "\"" + Escape(value).Replace("\"", "\\\"") + "\"";

    private static string Cs(string value) => "\"" + Escape(value).Replace("\"", "\\\"") + "\"";

    private static string Java(string value) => Cs(value);

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
}

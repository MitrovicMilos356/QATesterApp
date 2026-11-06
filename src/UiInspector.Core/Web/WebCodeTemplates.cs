using System.Text;
using UiInspector.Core.Recording;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Web;

/// <summary>What a web script does with one element.</summary>
public enum WebAction
{
    Click,
    DoubleClick,
    RightClick,
    Fill,
    Type,
    Press,
    CheckText,
    CheckValue,
    CheckExists,
}

/// <summary>One step of a web script: the element's locators and what to do with it.</summary>
public sealed record WebStep
{
    public required string Comment { get; init; }

    public WebSelector? Playwright { get; init; }

    public SeleniumSelector? Selenium { get; init; }

    public WebAction Action { get; init; }

    /// <summary>Text to fill/type, or the text a check expects.</summary>
    public string? Text { get; init; }

    /// <summary>Environment variable holding a password, instead of <see cref="Text"/>.</summary>
    public string? SecretName { get; init; }

    public KeyChord? Key { get; init; }

    public int Repeat { get; init; } = 1;

    /// <summary>Why this step isn't in the script (e.g. it isn't in the web page).</summary>
    public string? SkipReason { get; init; }
}

/// <summary>
/// Playwright and Selenium scripts. The app is reached one of three ways: attach to it over its
/// debug port when it already has one; for a website, open a browser and go to the URL; for an
/// Electron or WebView2 app, start it with a debug port, then attach. Locators enter iframes
/// (and, for Selenium, shadow roots), then each step acts on its element.
/// </summary>
public static class WebCodeTemplates
{
    private const string SampleText = "Hello from UiInspector";
    private const int WaitSeconds = 10;
    private const int DefaultPort = 9222;

    /// <summary>Snippets for one element: click it, or fill it with sample text if it's a text field.</summary>
    public static IReadOnlyList<CodeSnippet> Render(WebResult web, WebSelector? playwright = null)
    {
        if (!web.Connected || web.Element is null) return [];
        var step = new WebStep
        {
            Comment = $"Target: {web.Element.Describe()}",
            Playwright = playwright ?? web.Best,
            Selenium = web.Selenium,
            Action = IsFillable(web) ? WebAction.Fill : WebAction.Click,
            Text = SampleText,
        };
        var notes = (playwright ?? web.Best)?.Warnings ?? web.Selenium?.Warnings ?? [];
        var header = new List<string>();
        if ((playwright ?? web.Best) is { } pw) header.Add($"Strategy: {pw.Strategy}. {pw.Verification.Describe()}");
        header.AddRange(notes.Concat(web.Notes).Distinct().Select(n => $"Note: {n}"));
        return Render(web, [step], header);
    }

    /// <summary>Scripts for several steps in one page; <paramref name="app"/> says how to reach it.</summary>
    public static IReadOnlyList<CodeSnippet> Render(WebResult app, IReadOnlyList<WebStep> steps, IReadOnlyList<string> header)
    {
        var snippets = new List<CodeSnippet>();
        if (steps.Any(s => s.Playwright is not null))
        {
            snippets.Add(new("playwright-js", "Playwright (JavaScript)", PlaywrightJs(app, steps, header)));
            snippets.Add(new("playwright-py", "Playwright (Python)", PlaywrightPy(app, steps, header)));
            snippets.Add(new("playwright-cs", "Playwright (C#)", PlaywrightCs(app, steps, header)));
        }
        if (steps.Any(s => s.Selenium is not null))
        {
            snippets.Add(new("selenium-py", "Selenium (Python)", SeleniumPy(app, steps, header)));
            snippets.Add(new("selenium-java", "Selenium (Java)", SeleniumJava(app, steps, header)));
        }
        return snippets;
    }

    private static bool IsFillable(WebResult web) =>
        web.Element is { } e && (e.Tag is "input" or "textarea" || e.Role is "textbox" or "searchbox")
        && !(e.Attributes.TryGetValue("type", out var type) && type is "checkbox" or "radio" or "button" or "submit" or "password");

    // ------------------------------------------------------------------ how the script reaches the app

    private enum Reach { Attach, OpenBrowser, StartApp }

    private static Reach HowToReach(WebResult app) => app.Attach ? Reach.Attach
        : app.Launch is { Kind: WebAppKind.Browser } ? Reach.OpenBrowser
        : Reach.StartApp;

    private static int Port(WebResult app) => app.Port > 0 ? app.Port : DefaultPort;

    private static string Endpoint(WebResult app) => $"http://127.0.0.1:{Port(app)}";

    private static string Exe(WebResult app) => app.Launch?.ExePath ?? $@"C:\path\to\{app.Launch?.ProcessName ?? "app"}.exe";

    private static string AppName(WebResult app) => app.Launch?.ProcessName is { Length: > 0 } name ? name : "the app";

    private static string Channel(WebResult app) => app.Browser == BrowserFlavor.Edge ? "msedge" : "chrome";

    private static bool IsFirefox(WebResult app) => app.Browser == BrowserFlavor.Firefox;

    private static string BrowserName(WebResult app) => app.Browser switch
    {
        BrowserFlavor.Edge => "Edge",
        BrowserFlavor.Firefox => "Firefox",
        _ => "Chrome",
    };

    // Playwright drives Chromium-based browsers that are installed (by channel), but Firefox only in its own build.
    private static string PlaywrightInstallNote(WebResult app, string install, string downloadBrowser) => IsFirefox(app)
        ? $"{install}, then once: {downloadBrowser}   (Playwright drives its own Firefox build, not the installed one)"
        : $"{install}   (uses the installed app or browser; no browser download needed)";

    private static string ImageName(WebResult app) => app.Launch?.ImageName ?? $"{AppName(app)}.exe";

    private static IReadOnlyList<string> StartArguments(WebResult app) =>
        app.Launch?.StartArguments(Port(app)) ?? [$"--remote-debugging-port={Port(app)}"];

    private static string RunningWithoutPort(WebResult app) =>
        $"{AppName(app)} is running without a debugging port. Quit it completely (for apps in the system tray: right-click its tray icon, then Quit), then run this again.";

    private static string PythonPopen(WebResult app)
    {
        var arguments = string.Concat(StartArguments(app).Select(a => ", " + Py(a)));
        var env = app.Launch?.Kind == WebAppKind.WebView2
            ? $", env={{**os.environ, 'WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS': '--remote-debugging-port={Port(app)}'}}"
            : "";
        return $"subprocess.Popen([{PyRaw(Exe(app))}{arguments}]{env})";
    }

    private static void PythonIsRunning(StringBuilder sb)
    {
        sb.AppendLine("def is_running(image):");
        sb.AppendLine("    out = subprocess.run(['tasklist', '/FI', f'IMAGENAME eq {image}', '/NH'], capture_output=True, text=True).stdout");
        sb.AppendLine("    return image.lower() in out.lower()");
        sb.AppendLine();
        sb.AppendLine();
    }

    private static void Header(StringBuilder sb, string comment, WebResult app, IReadOnlyList<string> lines)
    {
        foreach (var line in lines) sb.AppendLine($"{comment} {line}");
        switch (HowToReach(app))
        {
            case Reach.Attach:
                sb.AppendLine($"{comment} Attaches to {AppName(app)}, which runs with --remote-debugging-port={app.Port}.");
                break;
            case Reach.OpenBrowser:
                sb.AppendLine($"{comment} Opens its own {BrowserName(app)} window and goes to the page; your browser isn't touched.");
                break;
            default:
                sb.AppendLine($"{comment} Starts {AppName(app)} with a debugging port, then attaches. Close {AppName(app)} completely first:");
                sb.AppendLine($"{comment} an already running instance ignores the port.");
                break;
        }
        sb.AppendLine();
    }

    // ------------------------------------------------------------------ Playwright: JavaScript

    private static string PlaywrightJs(WebResult app, IReadOnlyList<WebStep> steps, IReadOnlyList<string> header)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// " + PlaywrightInstallNote(app, IsFirefox(app) ? "npm i playwright" : "npm i playwright-core", "npx playwright install firefox"));
        Header(sb, "//", app, header);
        sb.AppendLine(IsFirefox(app) ? "const { firefox } = require('playwright');" : "const { chromium } = require('playwright-core');");
        var reach = HowToReach(app);
        if (reach == Reach.StartApp) sb.AppendLine("const { spawn } = require('child_process');");
        sb.AppendLine();
        sb.AppendLine("(async () => {");
        switch (reach)
        {
            case Reach.OpenBrowser:
                sb.AppendLine(IsFirefox(app) ? "  const browser = await firefox.launch({ headless: false });" : $"  const browser = await chromium.launch({{ channel: {Js(Channel(app))}, headless: false }});");
                sb.AppendLine("  const page = await browser.newPage();");
                sb.AppendLine($"  await page.goto({Js(app.PageUrl)});");
                break;
            case Reach.StartApp:
                sb.AppendLine($"  let browser = await chromium.connectOverCDP({Js(Endpoint(app))}).catch(() => null); // already running with the port?");
                sb.AppendLine("  if (!browser) {");
                sb.AppendLine($"    if (isRunning({Js(ImageName(app))})) throw new Error({Js(RunningWithoutPort(app))});");
                var env = app.Launch?.Kind == WebAppKind.WebView2 ? $", env: {{ ...process.env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port={Port(app)}' }}" : "";
                sb.AppendLine($"    spawn(String.raw`{Exe(app)}`, [{string.Join(", ", StartArguments(app).Select(Js))}], {{ detached: true, stdio: 'ignore'{env} }}).unref();");
                sb.AppendLine($"    browser = await connect({Js(Endpoint(app))});");
                sb.AppendLine("  }");
                sb.AppendLine($"  const page = await findPage(browser, {Js(app.UrlPrefix)}, {Js(app.PageTitle)});");
                break;
            default:
                sb.AppendLine($"  const browser = await chromium.connectOverCDP({Js(Endpoint(app))});");
                sb.AppendLine($"  const page = await findPage(browser, {Js(app.UrlPrefix)}, {Js(app.PageTitle)});");
                break;
        }

        foreach (var step in steps)
        {
            sb.AppendLine();
            if (Skip(sb, "  //", step)) continue;
            var target = PwChain(step.Playwright!, "page", PwFlavor.Js);
            var text = step.SecretName is { } secret ? $"secret({Js(secret)})" : Js(step.Text ?? "");
            sb.AppendLine($"  {{ // {step.Comment}");
            sb.AppendLine($"    const target = {target};");
            foreach (var line in PwAction(step, text, PwFlavor.Js)) sb.AppendLine("    " + line);
            sb.AppendLine("  }");
        }

        sb.AppendLine();
        sb.AppendLine(reach == Reach.OpenBrowser
            ? "  await browser.close();"
            : "  await browser.close(); // disconnects only; the app keeps running");
        sb.AppendLine("})().catch(e => { console.error(e); process.exit(1); });");

        if (steps.Any(s => s.SecretName is not null))
        {
            sb.AppendLine();
            sb.AppendLine("function secret(name) {");
            sb.AppendLine("  if (process.env[name] === undefined) throw new Error(`Set the ${name} environment variable to the password to type.`);");
            sb.AppendLine("  return process.env[name];");
            sb.AppendLine("}");
        }
        if (reach == Reach.StartApp)
        {
            sb.AppendLine();
            sb.AppendLine("// The app needs a moment before its debugging port answers.");
            sb.AppendLine("async function connect(endpoint) {");
            sb.AppendLine("  for (let i = 0; i < 60; i++) {");
            sb.AppendLine("    try { return await chromium.connectOverCDP(endpoint); } catch { await new Promise(r => setTimeout(r, 500)); }");
            sb.AppendLine("  }");
            sb.AppendLine($"  throw new Error({Js($"No debugging port at {Endpoint(app)}. Close {AppName(app)} completely, then run this again.")});");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("function isRunning(image) {");
            sb.AppendLine("  const out = require('child_process').execSync(`tasklist /FI \"IMAGENAME eq ${image}\" /NH`, { encoding: 'utf8' });");
            sb.AppendLine("  return out.toLowerCase().includes(image.toLowerCase());");
            sb.AppendLine("}");
        }
        if (reach != Reach.OpenBrowser)
        {
            sb.AppendLine();
            sb.AppendLine("async function findPage(browser, urlPrefix, title) {");
            sb.AppendLine("  for (let i = 0; i < 240; i++) { // up to 60 s: a just-started app may show a splash or update window first");
            sb.AppendLine("    for (const page of browser.contexts().flatMap(c => c.pages())) {");
            sb.AppendLine("      if (urlPrefix ? page.url().startsWith(urlPrefix) : (await page.title()) === title) return page;");
            sb.AppendLine("    }");
            sb.AppendLine("    await new Promise(r => setTimeout(r, 250));");
            sb.AppendLine("  }");
            sb.AppendLine("  throw new Error('Page not found: ' + (urlPrefix || title));");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Playwright: Python

    private static string PlaywrightPy(WebResult app, IReadOnlyList<WebStep> steps, IReadOnlyList<string> header)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# " + PlaywrightInstallNote(app, "pip install playwright", "playwright install firefox"));
        Header(sb, "#", app, header);
        var reach = HowToReach(app);
        sb.AppendLine("import os");
        if (reach == Reach.StartApp) sb.AppendLine("import subprocess");
        sb.AppendLine("import time");
        sb.AppendLine("from playwright.sync_api import sync_playwright");
        sb.AppendLine();
        sb.AppendLine();
        if (steps.Any(s => s.SecretName is not null))
        {
            sb.AppendLine("def secret(name):");
            sb.AppendLine("    if name not in os.environ:");
            sb.AppendLine("        raise KeyError('Set the ' + name + ' environment variable to the password to type.')");
            sb.AppendLine("    return os.environ[name]");
            sb.AppendLine();
            sb.AppendLine();
        }
        if (reach != Reach.OpenBrowser)
        {
            sb.AppendLine("def find_page(browser, url_prefix, title):");
            sb.AppendLine("    for _ in range(240):  # up to 60 s: a just-started app may show a splash or update window first");
            sb.AppendLine("        for page in (pg for ctx in browser.contexts for pg in ctx.pages):");
            sb.AppendLine("            if (page.url.startswith(url_prefix) if url_prefix else page.title() == title):");
            sb.AppendLine("                return page");
            sb.AppendLine("        time.sleep(0.25)");
            sb.AppendLine("    raise LookupError('Page not found: ' + (url_prefix or title))");
            sb.AppendLine();
            sb.AppendLine();
        }
        if (reach == Reach.StartApp)
        {
            sb.AppendLine("def connect(p, endpoint):");
            sb.AppendLine("    for _ in range(60):  # the app needs a moment before its debugging port answers");
            sb.AppendLine("        try:");
            sb.AppendLine("            return p.chromium.connect_over_cdp(endpoint)");
            sb.AppendLine("        except Exception:");
            sb.AppendLine("            time.sleep(0.5)");
            sb.AppendLine($"    raise RuntimeError({Py($"No debugging port at {Endpoint(app)}. Close {AppName(app)} completely, then run this again.")})");
            sb.AppendLine();
            sb.AppendLine();
            PythonIsRunning(sb);
        }

        sb.AppendLine("with sync_playwright() as p:");
        switch (reach)
        {
            case Reach.OpenBrowser:
                sb.AppendLine(IsFirefox(app) ? "    browser = p.firefox.launch(headless=False)" : $"    browser = p.chromium.launch(channel={Py(Channel(app))}, headless=False)");
                sb.AppendLine("    page = browser.new_page()");
                sb.AppendLine($"    page.goto({Py(app.PageUrl)})");
                break;
            case Reach.StartApp:
                sb.AppendLine("    try:");
                sb.AppendLine($"        browser = p.chromium.connect_over_cdp({Py(Endpoint(app))})  # already running with the port?");
                sb.AppendLine("    except Exception:");
                sb.AppendLine($"        if is_running({Py(ImageName(app))}):");
                sb.AppendLine($"            raise RuntimeError({Py(RunningWithoutPort(app))})");
                sb.AppendLine($"        {PythonPopen(app)}");
                sb.AppendLine($"        browser = connect(p, {Py(Endpoint(app))})");
                sb.AppendLine($"    page = find_page(browser, {Py(app.UrlPrefix)}, {Py(app.PageTitle)})");
                break;
            default:
                sb.AppendLine($"    browser = p.chromium.connect_over_cdp({Py(Endpoint(app))})");
                sb.AppendLine($"    page = find_page(browser, {Py(app.UrlPrefix)}, {Py(app.PageTitle)})");
                break;
        }

        foreach (var step in steps)
        {
            sb.AppendLine();
            if (Skip(sb, "    #", step)) continue;
            sb.AppendLine($"    # {step.Comment}");
            sb.AppendLine($"    target = {PwChain(step.Playwright!, "page", PwFlavor.Py)}");
            var text = step.SecretName is { } secret ? $"secret({Py(secret)})" : Py(step.Text ?? "");
            foreach (var line in PwAction(step, text, PwFlavor.Py)) sb.AppendLine("    " + line);
        }
        sb.AppendLine();
        sb.AppendLine(reach == Reach.OpenBrowser ? "    browser.close()" : "    browser.close()  # disconnects only; the app keeps running");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Playwright: C#

    private static string PlaywrightCs(WebResult app, IReadOnlyList<WebStep> steps, IReadOnlyList<string> header)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// " + PlaywrightInstallNote(app, "NuGet: Microsoft.Playwright", "pwsh bin/Debug/net8.0/playwright.ps1 install firefox"));
        Header(sb, "//", app, header);
        var reach = HowToReach(app);
        sb.AppendLine("using Microsoft.Playwright;");
        sb.AppendLine();
        sb.AppendLine("using var playwright = await Playwright.CreateAsync();");
        switch (reach)
        {
            case Reach.OpenBrowser:
                sb.AppendLine(IsFirefox(app) ? "var browser = await playwright.Firefox.LaunchAsync(new() { Headless = false });" : $"var browser = await playwright.Chromium.LaunchAsync(new() {{ Channel = {Cs(Channel(app))}, Headless = false }});");
                sb.AppendLine("var page = await browser.NewPageAsync();");
                sb.AppendLine($"await page.GotoAsync({Cs(app.PageUrl)});");
                break;
            case Reach.StartApp:
                sb.AppendLine("IBrowser? browser = null;");
                sb.AppendLine($"try {{ browser = await playwright.Chromium.ConnectOverCDPAsync({Cs(Endpoint(app))}); }} catch (PlaywrightException) {{ }} // already running with the port?");
                sb.AppendLine("if (browser is null)");
                sb.AppendLine("{");
                sb.AppendLine($"    if (System.Diagnostics.Process.GetProcessesByName({Cs(Path.GetFileNameWithoutExtension(ImageName(app)))}).Length > 0)");
                sb.AppendLine($"        throw new InvalidOperationException({Cs(RunningWithoutPort(app))});");
                sb.AppendLine($"    var start = new System.Diagnostics.ProcessStartInfo(@\"{Exe(app).Replace("\"", "\"\"")}\") {{ UseShellExecute = false }};");
                if (app.Launch?.Kind == WebAppKind.WebView2) sb.AppendLine($"    start.Environment[\"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS\"] = \"--remote-debugging-port={Port(app)}\";");
                foreach (var argument in StartArguments(app)) sb.AppendLine($"    start.ArgumentList.Add({Cs(argument)});");
                sb.AppendLine("    System.Diagnostics.Process.Start(start);");
                sb.AppendLine("    for (var i = 0; i < 60 && browser is null; i++) // the app needs a moment before its debugging port answers");
                sb.AppendLine("    {");
                sb.AppendLine($"        try {{ browser = await playwright.Chromium.ConnectOverCDPAsync({Cs(Endpoint(app))}); }}");
                sb.AppendLine("        catch (PlaywrightException) { await Task.Delay(500); }");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine($"if (browser is null) throw new InvalidOperationException({Cs($"No debugging port at {Endpoint(app)}. Close {AppName(app)} completely, then run this again.")});");
                sb.AppendLine($"var page = await FindPageAsync(browser, {Cs(app.UrlPrefix)}, {Cs(app.PageTitle)});");
                break;
            default:
                sb.AppendLine($"var browser = await playwright.Chromium.ConnectOverCDPAsync({Cs(Endpoint(app))});");
                sb.AppendLine($"var page = await FindPageAsync(browser, {Cs(app.UrlPrefix)}, {Cs(app.PageTitle)});");
                break;
        }

        foreach (var step in steps)
        {
            sb.AppendLine();
            if (Skip(sb, "//", step)) continue;
            sb.AppendLine($"{{ // {step.Comment}");
            sb.AppendLine($"    var target = {PwChain(step.Playwright!, "page", PwFlavor.Cs)};");
            var text = step.SecretName is { } secret ? $"Secret({Cs(secret)})" : Cs(step.Text ?? "");
            foreach (var line in PwAction(step, text, PwFlavor.Cs)) sb.AppendLine("    " + line);
            sb.AppendLine("}");
        }
        sb.AppendLine();
        sb.AppendLine(reach == Reach.OpenBrowser ? "await browser.CloseAsync();" : "await browser.CloseAsync(); // disconnects only; the app keeps running");

        if (steps.Any(s => s.SecretName is not null))
        {
            sb.AppendLine();
            sb.AppendLine("static string Secret(string name) => Environment.GetEnvironmentVariable(name)");
            sb.AppendLine("    ?? throw new InvalidOperationException($\"Set the {name} environment variable to the password to type.\");");
        }
        if (reach != Reach.OpenBrowser)
        {
            sb.AppendLine();
            sb.AppendLine("static async Task<IPage> FindPageAsync(IBrowser browser, string urlPrefix, string title)");
            sb.AppendLine("{");
            sb.AppendLine("    for (var i = 0; i < 240; i++) // up to 60 s: a just-started app may show a splash or update window first");
            sb.AppendLine("    {");
            sb.AppendLine("        foreach (var page in browser.Contexts.SelectMany(c => c.Pages))");
            sb.AppendLine("        {");
            sb.AppendLine("            if (urlPrefix.Length > 0 ? page.Url.StartsWith(urlPrefix) : await page.TitleAsync() == title) return page;");
            sb.AppendLine("        }");
            sb.AppendLine("        await Task.Delay(250);");
            sb.AppendLine("    }");
            sb.AppendLine("    throw new InvalidOperationException($\"Page not found: {(urlPrefix.Length > 0 ? urlPrefix : title)}\");");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private enum PwFlavor { Js, Py, Cs }

    private static IEnumerable<string> PwAction(WebStep step, string text, PwFlavor f)
    {
        var (aw, end) = f switch { PwFlavor.Js => ("await ", ";"), PwFlavor.Cs => ("await ", ";"), _ => ("", "") };
        string Call(string js, string py, string cs) => f switch { PwFlavor.Js => js, PwFlavor.Py => py, _ => cs };

        switch (step.Action)
        {
            case WebAction.Click:
                yield return $"{aw}target.{Call("click()", "click()", "ClickAsync()")}{end}";
                break;
            case WebAction.DoubleClick:
                yield return $"{aw}target.{Call("dblclick()", "dblclick()", "DblClickAsync()")}{end}";
                break;
            case WebAction.RightClick:
                yield return $"{aw}target.{Call("click({ button: 'right' })", "click(button='right')", "ClickAsync(new() { Button = MouseButton.Right })")}{end}";
                break;
            case WebAction.Fill:
                yield return $"{aw}target.{Call($"fill({text})", $"fill({text})", $"FillAsync({text})")}{end}";
                break;
            case WebAction.Type:
                yield return $"{aw}target.{Call($"pressSequentially({text})", $"press_sequentially({text})", $"PressSequentiallyAsync({text})")}{end}";
                break;
            case WebAction.Press when step.Key is { } key:
                var keys = Quote(PwKeys(key), f);
                var press = $"{aw}target.{Call($"press({keys})", $"press({keys})", $"PressAsync({keys})")}{end}";
                if (step.Repeat <= 1) yield return press;
                else yield return f switch
                {
                    PwFlavor.Js => $"for (let i = 0; i < {step.Repeat}; i++) {press}",
                    PwFlavor.Py => $"for _ in range({step.Repeat}):\n        {press}",
                    _ => $"for (var i = 0; i < {step.Repeat}; i++) {press}",
                };
                break;
            case WebAction.CheckText or WebAction.CheckValue:
                var read = step.Action == WebAction.CheckValue
                    ? Call("await target.inputValue()", "target.input_value()", "await target.InputValueAsync()")
                    : Call("(await target.innerText()).trim()", "target.inner_text().strip()", "(await target.InnerTextAsync()).Trim()");
                var expected = Quote(step.Text ?? "", f);
                yield return f switch
                {
                    PwFlavor.Js => $"const actual = {read};",
                    PwFlavor.Py => $"actual = {read}",
                    _ => $"var actual = {read};",
                };
                yield return f switch
                {
                    PwFlavor.Js => $"if (actual !== {expected}) throw new Error(`Check failed: expected ${{{expected}}}, got ${{actual}}`);",
                    PwFlavor.Py => $"if actual != {expected}:\n        raise AssertionError('Check failed: expected ' + repr({expected}) + ', got ' + repr(actual))",
                    _ => $"if (actual != {expected}) throw new InvalidOperationException($\"Check failed: expected {CodeTemplates.CsEscape(step.Text ?? "").Replace("{", "{{").Replace("}", "}}")}, got {{actual}}\");",
                };
                break;
            case WebAction.CheckExists:
                yield return $"{aw}target.{Call("waitFor()", "wait_for()", "WaitForAsync()")}{end}";
                break;
        }
    }

    // Playwright key names: "Control+Shift+s", "Enter", "ArrowDown".
    private static string PwKeys(KeyChord key)
    {
        var parts = new List<string>();
        if (key.Ctrl) parts.Add("Control");
        if (key.Alt) parts.Add("Alt");
        if (key.Shift) parts.Add("Shift");
        if (key.Win) parts.Add("Meta");
        var name = key.KeyName ?? "";
        parts.Add(name switch
        {
            { Length: 1 } => name.ToLowerInvariant(),
            "Left" or "Right" or "Up" or "Down" => "Arrow" + name,
            _ => name,
        });
        return string.Join("+", parts);
    }

    private static string Quote(string value, PwFlavor f) => f switch { PwFlavor.Js => Js(value), PwFlavor.Py => Py(value), _ => Cs(value) };

    /// <summary>The Playwright locator for <paramref name="s"/>, starting from <c>page</c>, in Python or JavaScript (for test suites).</summary>
    internal static string PlaywrightLocator(WebSelector s, bool python) => PwChain(s, "page", python ? PwFlavor.Py : PwFlavor.Js);

    internal static string PlaywrightLocatorCs(WebSelector s) => PwChain(s, "page", PwFlavor.Cs);

    internal static string PyString(string value) => Py(value);

    internal static string JsString(string value) => Js(value);

    internal static string CsString(string value) => Cs(value);

    internal static string JavaString(string value) => Java(value);

    /// <summary>
    /// The body of a function that finds the element with Selenium (Python or Java), given
    /// <c>driver</c> and <c>wait</c>: into its frames and shadow roots first, ending in a return.
    /// </summary>
    internal static IEnumerable<string> SeleniumFindBody(SeleniumSelector s, bool java)
    {
        string Q(string v) => java ? Java(v) : Py(v);
        yield return java ? "driver.switchTo().defaultContent();" : "driver.switch_to.default_content()";
        foreach (var frame in s.Frames)
        {
            yield return (frame.Index, java) switch
            {
                ({ } fi, false) => $"driver.switch_to.frame(driver.find_elements(By.CSS_SELECTOR, {Q(frame.Value)})[{fi}])",
                (null, false) => $"driver.switch_to.frame(wait.until(EC.presence_of_element_located((By.CSS_SELECTOR, {Q(frame.Value)}))))",
                ({ } fi, true) => $"driver.switchTo().frame(driver.findElements(By.cssSelector({Q(frame.Value)})).get({fi}));",
                _ => $"driver.switchTo().frame(wait.until(ExpectedConditions.presenceOfElementLocated(By.cssSelector({Q(frame.Value)}))));",
            };
        }
        var scope = "driver";
        for (var i = 0; i < s.ShadowHosts.Count; i++)
        {
            var host = s.ShadowHosts[i];
            yield return java
                ? $"SearchContext shadow{i + 1} = wait.until(d -> {(host.Index is { } hi ? $"{scope}.findElements(By.cssSelector({Q(host.Value)})).get({hi})" : $"{scope}.findElement(By.cssSelector({Q(host.Value)}))")}).getShadowRoot();"
                : $"shadow{i + 1} = wait.until(lambda d: {(host.Index is { } pyIndex ? $"{scope}.find_elements(By.CSS_SELECTOR, {Q(host.Value)})[{pyIndex}]" : $"{scope}.find_element(By.CSS_SELECTOR, {Q(host.Value)})")}).shadow_root";
            scope = $"shadow{i + 1}";
        }
        if (s.ContainerXPath is { } container)
        {
            yield return java
                ? $"WebElement container = wait.until(ExpectedConditions.presenceOfElementLocated(By.xpath({Q(container)})));"
                : $"container = wait.until(EC.presence_of_element_located((By.XPATH, {Q(container)})))";
            scope = "container";
        }
        var xpath = s.Target.Kind == WebLocatorKind.XPath;
        var by = java ? (xpath ? "By.xpath" : "By.cssSelector") : (xpath ? "By.XPATH" : "By.CSS_SELECTOR");
        var value = Q(s.Target.Value);
        yield return (s.Target.Index, java) switch
        {
            ({ } ti, false) => $"return wait.until(lambda d: {scope}.find_elements({by}, {value})[{ti}])",
            (null, false) => scope == "driver"
                ? $"return wait.until(EC.presence_of_element_located(({by}, {value})))"
                : $"return wait.until(lambda d: {scope}.find_element({by}, {value}))",
            ({ } ti, true) => $"return wait.until(d -> {scope}.findElements({by}({value})).get({ti}));",
            _ => scope == "driver"
                ? $"return wait.until(ExpectedConditions.presenceOfElementLocated({by}({value})));"
                : $"return wait.until(d -> {scope}.findElement({by}({value})));",
        };
    }

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
        for (var i = 0; i < s.Steps.Count; i++)
        {
            var step = s.Steps[i];
            sb.Append(PwStep(step, f));
            if (step.FilterHasText is { } text)
            {
                var byText = PwStep(new WebLocator { Kind = WebLocatorKind.Text, Value = text, Exact = step.Exact }, f);
                sb.Append(Filter(start + byText, f));
            }
            if (step.FilterHasNext && i + 1 < s.Steps.Count) sb.Append(Filter(start + PwStep(s.Steps[i + 1] with { Index = null }, f), f));
            if (step.Last) sb.Append(f == PwFlavor.Cs ? ".Last" : ".last");
            if (step.Last && f != PwFlavor.Cs) sb.Append("()");
            if (step.Index is { } index) sb.Append(f == PwFlavor.Cs ? $".Nth({index})" : $".nth({index})");
        }
        return sb.ToString();
    }

    private static string Filter(string has, PwFlavor f) => f switch
    {
        PwFlavor.Js => $".filter({{ has: {has} }})",
        PwFlavor.Py => $".filter(has={has})",
        _ => $".Filter(new() {{ Has = {has} }})",
    };

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

        (WebLocatorKind.Text, PwFlavor.Js) => $".getByText({Js(l.Value)}{(l.Exact ? ", { exact: true }" : "")})",
        (WebLocatorKind.Text, PwFlavor.Py) => $".get_by_text({Py(l.Value)}{(l.Exact ? ", exact=True" : "")})",
        (WebLocatorKind.Text, _) => $".GetByText({Cs(l.Value)}{(l.Exact ? ", new() { Exact = true }" : "")})",

        (WebLocatorKind.XPath, _) => PwCss("xpath=" + l.Value, f),

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

    private static string SeleniumPy(WebResult app, IReadOnlyList<WebStep> steps, IReadOnlyList<string> header)
    {
        var edge = app.Browser == BrowserFlavor.Edge;
        var reach = HowToReach(app);
        var sb = new StringBuilder();
        sb.AppendLine("# pip install selenium   (Selenium Manager downloads the matching driver)");
        Header(sb, "#", app, header);
        if (!edge && reach != Reach.OpenBrowser) sb.AppendLine("# Electron apps: chromedriver must match the app's Chromium version, not your Chrome's.");
        sb.AppendLine("import os");
        if (reach == Reach.StartApp) sb.AppendLine("import socket");
        if (reach == Reach.StartApp) sb.AppendLine("import subprocess");
        sb.AppendLine("import time");
        sb.AppendLine("from selenium import webdriver");
        sb.AppendLine("from selenium.webdriver.common.action_chains import ActionChains");
        sb.AppendLine("from selenium.webdriver.common.by import By");
        sb.AppendLine("from selenium.webdriver.common.keys import Keys");
        sb.AppendLine("from selenium.webdriver.support import expected_conditions as EC");
        sb.AppendLine("from selenium.webdriver.support.ui import WebDriverWait");
        sb.AppendLine();
        if (steps.Any(s => s.SecretName is not null))
        {
            sb.AppendLine();
            sb.AppendLine("def secret(name):");
            sb.AppendLine("    if name not in os.environ:");
            sb.AppendLine("        raise KeyError('Set the ' + name + ' environment variable to the password to type.')");
            sb.AppendLine("    return os.environ[name]");
            sb.AppendLine();
            sb.AppendLine();
        }
        if (reach == Reach.StartApp)
        {
            sb.AppendLine();
            sb.AppendLine("def port_open(port):");
            sb.AppendLine("    try:");
            sb.AppendLine("        socket.create_connection(('127.0.0.1', port), timeout=1).close()");
            sb.AppendLine("        return True");
            sb.AppendLine("    except OSError:");
            sb.AppendLine("        return False");
            sb.AppendLine();
            sb.AppendLine();
            PythonIsRunning(sb);
        }

        var options = edge ? "EdgeOptions" : IsFirefox(app) ? "FirefoxOptions" : "ChromeOptions";
        var driver = edge ? "Edge" : IsFirefox(app) ? "Firefox" : "Chrome";
        switch (reach)
        {
            case Reach.OpenBrowser:
                sb.AppendLine($"driver = webdriver.{driver}()");
                sb.AppendLine($"driver.get({Py(app.PageUrl)})");
                break;
            default:
                if (reach == Reach.StartApp)
                {
                    sb.AppendLine($"if not port_open({Port(app)}):  # not already running with the port");
                    sb.AppendLine($"    if is_running({Py(ImageName(app))}):");
                    sb.AppendLine($"        raise RuntimeError({Py(RunningWithoutPort(app))})");
                    sb.AppendLine($"    {PythonPopen(app)}");
                    sb.AppendLine("    for _ in range(60):  # the app needs a moment before its debugging port answers");
                    sb.AppendLine($"        if port_open({Port(app)}):");
                    sb.AppendLine("            break");
                    sb.AppendLine("        time.sleep(0.5)");
                    sb.AppendLine("    else:");
                    sb.AppendLine($"        raise RuntimeError({Py($"No debugging port at {Endpoint(app)}. Close {AppName(app)} completely, then run this again.")})");
                }
                sb.AppendLine($"options = webdriver.{options}()");
                sb.AppendLine($"options.debugger_address = {Py($"127.0.0.1:{Port(app)}")}  # attach to the app");
                sb.AppendLine($"driver = webdriver.{driver}(options=options)");
                sb.AppendLine("for _ in range(240):  # up to 60 s: a just-started app may show a splash or update window first");
                sb.AppendLine("    found = False");
                sb.AppendLine("    for handle in driver.window_handles:");
                sb.AppendLine("        driver.switch_to.window(handle)");
                sb.AppendLine($"        if {(app.UrlPrefix.Length > 0 ? $"driver.current_url.startswith({Py(app.UrlPrefix)})" : $"driver.title == {Py(app.PageTitle)}")}:");
                sb.AppendLine("            found = True");
                sb.AppendLine("            break");
                sb.AppendLine("    if found:");
                sb.AppendLine("        break");
                sb.AppendLine("    time.sleep(0.25)");
                sb.AppendLine("else:");
                sb.AppendLine($"    raise LookupError({Py("Page not found: " + (app.UrlPrefix.Length > 0 ? app.UrlPrefix : app.PageTitle))})");
                break;
        }
        sb.AppendLine($"wait = WebDriverWait(driver, {WaitSeconds})");

        foreach (var step in steps)
        {
            sb.AppendLine();
            if (Skip(sb, "#", step, selenium: true)) continue;
            var s = step.Selenium!;
            sb.AppendLine($"# {step.Comment}");
            sb.AppendLine("driver.switch_to.default_content()");
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
            if (s.ContainerXPath is { } container)
            {
                sb.AppendLine($"container = wait.until(EC.presence_of_element_located((By.XPATH, {Py(container)})))");
                scope = "container";
            }
            var by = s.Target.Kind == WebLocatorKind.XPath ? "By.XPATH" : "By.CSS_SELECTOR";
            var target = s.Target.Index is { } ti
                ? $"wait.until(lambda d: {scope}.find_elements({by}, {Py(s.Target.Value)})[{ti}])"
                : scope == "driver"
                    ? $"wait.until(EC.presence_of_element_located(({by}, {Py(s.Target.Value)})))"
                    : $"wait.until(lambda d: {scope}.find_element({by}, {Py(s.Target.Value)}))";
            sb.AppendLine($"target = {target}");
            var text = step.SecretName is { } secret ? $"secret({Py(secret)})" : Py(step.Text ?? "");
            sb.AppendLine(step.Action switch
            {
                WebAction.Click => "target.click()",
                WebAction.DoubleClick => "ActionChains(driver).double_click(target).perform()",
                WebAction.RightClick => "ActionChains(driver).context_click(target).perform()",
                WebAction.Fill => $"target.clear()\ntarget.send_keys({text})",
                WebAction.Type => $"target.send_keys({text})",
                WebAction.Press => (step.Repeat > 1 ? $"for _ in range({step.Repeat}):\n    " : "") + $"target.send_keys({SeleniumKeys(step.Key!, java: false)})",
                WebAction.CheckText or WebAction.CheckValue =>
                    $"actual = {(step.Action == WebAction.CheckValue ? "target.get_attribute('value')" : "target.text.strip()")}\n" +
                    $"if actual != {Py(step.Text ?? "")}:\n    raise AssertionError('Check failed: expected ' + repr({Py(step.Text ?? "")}) + ', got ' + repr(actual))",
                _ => "pass  # found, so it exists",
            });
        }
        sb.AppendLine();
        sb.AppendLine(reach == Reach.OpenBrowser
            ? "driver.quit()"
            : "# Don't call driver.quit() on an attached session: it can close the app's windows.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Selenium: Java

    private static string SeleniumJava(WebResult app, IReadOnlyList<WebStep> steps, IReadOnlyList<string> header)
    {
        var edge = app.Browser == BrowserFlavor.Edge;
        var reach = HowToReach(app);
        var options = edge ? "EdgeOptions" : IsFirefox(app) ? "FirefoxOptions" : "ChromeOptions";
        var driverType = edge ? "EdgeDriver" : IsFirefox(app) ? "FirefoxDriver" : "ChromeDriver";
        var package = edge ? "edge" : IsFirefox(app) ? "firefox" : "chrome";
        var sb = new StringBuilder();
        sb.AppendLine("// Maven: org.seleniumhq.selenium:selenium-java (4.x, Java 11+; Selenium Manager downloads the driver)");
        Header(sb, "//", app, header);
        if (!edge && reach != Reach.OpenBrowser) sb.AppendLine("// Electron apps: chromedriver must match the app's Chromium version, not your Chrome's.");
        sb.AppendLine("import java.time.Duration;");
        sb.AppendLine("import org.openqa.selenium.*;");
        sb.AppendLine("import org.openqa.selenium.interactions.Actions;");
        sb.AppendLine($"import org.openqa.selenium.{package}.{driverType};");
        sb.AppendLine($"import org.openqa.selenium.{package}.{options};");
        sb.AppendLine("import org.openqa.selenium.support.ui.ExpectedConditions;");
        sb.AppendLine("import org.openqa.selenium.support.ui.WebDriverWait;");
        sb.AppendLine();
        sb.AppendLine("public class InspectorSnippet {");
        sb.AppendLine("    public static void main(String[] args) throws Exception {");
        switch (reach)
        {
            case Reach.OpenBrowser:
                sb.AppendLine($"        WebDriver driver = new {driverType}();");
                sb.AppendLine($"        driver.get({Java(app.PageUrl)});");
                break;
            default:
                if (reach == Reach.StartApp)
                {
                    var image = Java("\\" + ImageName(app).ToLowerInvariant());
                    sb.AppendLine($"        if (!portOpen({Port(app)})) {{ // not already running with the port");
                    sb.AppendLine($"            if (ProcessHandle.allProcesses().anyMatch(p -> p.info().command().map(c -> c.toLowerCase().endsWith({image})).orElse(false)))");
                    sb.AppendLine($"                throw new IllegalStateException({Java(RunningWithoutPort(app))});");
                    sb.AppendLine($"            ProcessBuilder start = new ProcessBuilder({string.Join(", ", StartArguments(app).Prepend(Exe(app)).Select(Java))});");
                    if (app.Launch?.Kind == WebAppKind.WebView2)
                    {
                        sb.AppendLine($"            start.environment().put(\"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS\", \"--remote-debugging-port={Port(app)}\");");
                    }
                    sb.AppendLine("            start.start();");
                    sb.AppendLine($"            for (int i = 0; i < 60 && !portOpen({Port(app)}); i++) Thread.sleep(500); // the app needs a moment");
                    sb.AppendLine("        }");
                }
                sb.AppendLine($"        {options} options = new {options}();");
                sb.AppendLine($"        options.setExperimentalOption(\"debuggerAddress\", {Java($"127.0.0.1:{Port(app)}")}); // attach to the app");
                sb.AppendLine($"        WebDriver driver = new {driverType}(options);");
                sb.AppendLine("        boolean found = false;");
                sb.AppendLine("        for (int i = 0; i < 240 && !found; i++) { // up to 60 s: a just-started app may show a splash or update window first");
                sb.AppendLine("            for (String handle : driver.getWindowHandles()) {");
                sb.AppendLine("                driver.switchTo().window(handle);");
                sb.AppendLine($"                if ({(app.UrlPrefix.Length > 0 ? $"driver.getCurrentUrl().startsWith({Java(app.UrlPrefix)})" : $"driver.getTitle().equals({Java(app.PageTitle)})")}) {{ found = true; break; }}");
                sb.AppendLine("            }");
                sb.AppendLine("            if (!found) Thread.sleep(250);");
                sb.AppendLine("        }");
                sb.AppendLine($"        if (!found) throw new NoSuchWindowException({Java("Page not found: " + (app.UrlPrefix.Length > 0 ? app.UrlPrefix : app.PageTitle))});");
                break;
        }
        sb.AppendLine($"        WebDriverWait wait = new WebDriverWait(driver, Duration.ofSeconds({WaitSeconds}));");

        foreach (var step in steps)
        {
            sb.AppendLine();
            if (Skip(sb, "        //", step, selenium: true)) continue;
            var s = step.Selenium!;
            sb.AppendLine($"        // {step.Comment}");
            sb.AppendLine("        driver.switchTo().defaultContent();");
            foreach (var frame in s.Frames)
            {
                sb.AppendLine(frame.Index is { } fi
                    ? $"        driver.switchTo().frame(driver.findElements(By.cssSelector({Java(frame.Value)})).get({fi}));"
                    : $"        driver.switchTo().frame(wait.until(ExpectedConditions.presenceOfElementLocated(By.cssSelector({Java(frame.Value)}))));");
            }
            var scope = "driver";
            sb.AppendLine("        {");
            for (var i = 0; i < s.ShadowHosts.Count; i++)
            {
                var host = s.ShadowHosts[i];
                var find = host.Index is { } hi
                    ? $"{scope}.findElements(By.cssSelector({Java(host.Value)})).get({hi})"
                    : $"{scope}.findElement(By.cssSelector({Java(host.Value)}))";
                sb.AppendLine($"            SearchContext shadow{i + 1} = {find}.getShadowRoot();");
                scope = $"shadow{i + 1}";
            }
            if (s.ContainerXPath is { } container)
            {
                sb.AppendLine($"            WebElement container = wait.until(ExpectedConditions.presenceOfElementLocated(By.xpath({Java(container)})));");
                scope = "container";
            }
            var by = s.Target.Kind == WebLocatorKind.XPath ? "By.xpath" : "By.cssSelector";
            var target = s.Target.Index is { } ti
                ? $"{scope}.findElements({by}({Java(s.Target.Value)})).get({ti})"
                : scope == "driver"
                    ? $"wait.until(ExpectedConditions.presenceOfElementLocated({by}({Java(s.Target.Value)})))"
                    : $"{scope}.findElement({by}({Java(s.Target.Value)}))";
            sb.AppendLine($"            WebElement target = {target};");
            var text = step.SecretName is { } secret ? $"secret({Java(secret)})" : Java(step.Text ?? "");
            sb.AppendLine("            " + (step.Action switch
            {
                WebAction.Click => "target.click();",
                WebAction.DoubleClick => "new Actions(driver).doubleClick(target).perform();",
                WebAction.RightClick => "new Actions(driver).contextClick(target).perform();",
                WebAction.Fill => $"target.clear(); target.sendKeys({text});",
                WebAction.Type => $"target.sendKeys({text});",
                WebAction.Press => (step.Repeat > 1 ? $"for (int r = 0; r < {step.Repeat}; r++) " : "") + $"target.sendKeys({SeleniumKeys(step.Key!, java: true)});",
                WebAction.CheckText or WebAction.CheckValue =>
                    $"String actual = {(step.Action == WebAction.CheckValue ? "target.getAttribute(\"value\")" : "target.getText().trim()")};\n" +
                    $"            if (!actual.equals({Java(step.Text ?? "")})) throw new AssertionError(\"Check failed: expected \" + {Java(step.Text ?? "")} + \", got \" + actual);",
                _ => "// found, so it exists",
            }));
            sb.AppendLine("        }");
        }
        sb.AppendLine();
        sb.AppendLine(reach == Reach.OpenBrowser ? "        driver.quit();" : "        // Don't call driver.quit() on an attached session: it can close the app's windows.");
        sb.AppendLine("    }");
        if (reach == Reach.StartApp)
        {
            sb.AppendLine();
            sb.AppendLine("    static boolean portOpen(int port) {");
            sb.AppendLine("        try (java.net.Socket socket = new java.net.Socket(\"127.0.0.1\", port)) { return true; }");
            sb.AppendLine("        catch (java.io.IOException e) { return false; }");
            sb.AppendLine("    }");
        }
        if (steps.Any(s => s.SecretName is not null))
        {
            sb.AppendLine();
            sb.AppendLine("    static String secret(String name) {");
            sb.AppendLine("        String value = System.getenv(name);");
            sb.AppendLine("        if (value == null) throw new IllegalStateException(\"Set the \" + name + \" environment variable to the password to type.\");");
            sb.AppendLine("        return value;");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    // Selenium: modifiers are held until the end of send_keys, so Ctrl+S is CONTROL + "s".
    private static string SeleniumKeys(KeyChord key, bool java)
    {
        var parts = new List<string>();
        if (key.Ctrl) parts.Add("Keys.CONTROL");
        if (key.Alt) parts.Add("Keys.ALT");
        if (key.Shift) parts.Add("Keys.SHIFT");
        if (key.Win) parts.Add("Keys.META");
        var name = key.KeyName ?? "";
        parts.Add(name switch
        {
            { Length: 1 } => java ? Java(name.ToLowerInvariant()) : Py(name.ToLowerInvariant()),
            "Backspace" => "Keys.BACK_SPACE",
            "PageUp" => "Keys.PAGE_UP",
            "PageDown" => "Keys.PAGE_DOWN",
            "Left" or "Right" or "Up" or "Down" => "Keys.ARROW_" + name.ToUpperInvariant(),
            _ => "Keys." + name.ToUpperInvariant(),
        });
        return java ? $"Keys.chord({string.Join(", ", parts)})" : string.Join(" + ", parts);
    }

    // ------------------------------------------------------------------ shared

    private static bool Skip(StringBuilder sb, string comment, WebStep step, bool selenium = false)
    {
        var missing = selenium ? step.Selenium is null : step.Playwright is null;
        if (step.SkipReason is null && !missing) return false;
        sb.AppendLine($"{comment} {step.Comment}: SKIPPED, {step.SkipReason ?? "no locator for this library"}.");
        return true;
    }

    private static string Js(string value) => "'" + Escape(value).Replace("'", "\\'") + "'";

    private static string Py(string value) => "\"" + Escape(value).Replace("\"", "\\\"") + "\"";

    private static string Cs(string value) => "\"" + Escape(value).Replace("\"", "\\\"") + "\"";

    private static string Java(string value) => Cs(value);

    // A Windows path as a Python raw string: backslashes stay as they are.
    private static string PyRaw(string path) => "r\"" + path + "\"";

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
}

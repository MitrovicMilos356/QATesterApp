using System.Text;
using UiInspector.Core.Selectors;
using static UiInspector.Core.Selectors.CodeTemplates;

namespace UiInspector.Core.Recording;

/// <summary>
/// Renders recorded steps as one script per framework. Each step finds its element from the
/// desktop again, waiting up to 10 s for it, so windows and dialogs that open along the way are
/// waited for. Actions use UIA patterns where possible, like the single-element snippets;
/// passwords are read from environment variables, never written into the script.
/// </summary>
public static class RecordingScripts
{
    public static IReadOnlyList<CodeSnippet> Render(IReadOnlyList<RecordedStep> steps)
    {
        var script = new Script(steps);
        return
        [
            new("flaui", "FlaUI (C#)", FlaUi(script)),
            new("uiautomation", "Python · uiautomation", PythonUiAutomation(script)),
            new("pywinauto", "Python · pywinauto", Pywinauto(script)),
            new("appium", "Appium · Python", Appium(script)),
            .. WebScripts(script),
        ];
    }

    // ------------------------------------------------------------------ Playwright / Selenium

    /// <summary>Playwright and Selenium scripts, when the steps are in a Chromium web page (others are skipped).</summary>
    private static IReadOnlyList<CodeSnippet> WebScripts(Script script)
    {
        var app = script.Steps.Select(s => s.Web).FirstOrDefault(w => w is { Connected: true });
        if (app is null) return [];

        var steps = script.Steps.Select((step, i) => WebStep(script, step, i + 1)).ToList();
        var apps = script.Apps.Count == 0 ? "" : $" in {string.Join(", ", script.Apps)}";
        var header = new List<string> { $"Recorded with UiInspector: {script.Steps.Count} step{(script.Steps.Count == 1 ? "" : "s")}{apps}." };
        header.AddRange(script.SecretNames.Values.Select(name => $"Set the {name} environment variable to the password typed in that step."));
        header.AddRange(script.Steps
            .Select(s => s.Web).OfType<Web.WebResult>().Where(w => w.Connected)
            .SelectMany(w => w.Notes).Distinct()
            .Select(n => $"Note: {n}"));
        return Web.WebCodeTemplates.Render(app, steps, header);
    }

    private static Web.WebStep WebStep(Script script, RecordedStep step, int number)
    {
        var comment = $"{number}. {OneLine(step.Describe())}";
        if (step.Web is not { Connected: true } web)
        {
            return new Web.WebStep { Comment = comment, SkipReason = step.IsGenerating ? "its locator was still being generated" : step.Web?.Problem ?? "it isn't in the web page (a native script covers it)" };
        }

        return new Web.WebStep
        {
            Comment = comment,
            Playwright = web.Best,
            Selenium = web.Selenium,
            Action = step.Action switch
            {
                RecordedAction.DoubleClick => Web.WebAction.DoubleClick,
                RecordedAction.RightClick => Web.WebAction.RightClick,
                RecordedAction.SetValue => Web.WebAction.Fill,
                RecordedAction.TypeText => Web.WebAction.Type,
                RecordedAction.PressKey => Web.WebAction.Press,
                RecordedAction.CheckText => step.CheckSource == TextSource.Value ? Web.WebAction.CheckValue : Web.WebAction.CheckText,
                RecordedAction.CheckExists => Web.WebAction.CheckExists,
                _ => Web.WebAction.Click,
            },
            Text = step.Text,
            SecretName = script.SecretNames.GetValueOrDefault(step.Id),
            Key = step.Key,
            Repeat = step.Repeat,
        };
    }

    /// <summary>The action a recorded left click becomes: the pattern action, or focus for a click into a text field.</summary>
    public static ElementAction ClickAction(ElementInfo target)
    {
        var action = ChooseAction(target);
        return action == ElementAction.SetValue ? ElementAction.Focus : action;
    }

    /// <summary>Steps plus what the whole script needs to know (numbering, secrets, apps).</summary>
    private sealed class Script
    {
        public Script(IReadOnlyList<RecordedStep> steps)
        {
            Steps = steps;
            var secret = 0;
            foreach (var step in steps.Where(s => s.IsSecret)) SecretNames[step.Id] = $"UIINSPECTOR_SECRET_{++secret}";
            Apps = steps.Select(s => s.Selector?.ProcessName ?? s.Target.ProcessName).Where(n => n.Length > 0).Distinct().ToList();
            LaunchPath = steps.Select(s => s.Selector?.ProcessPath).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            // Squirrel apps (Discord, Slack…): the versioned exe moves with every update; Update.exe starts the current one.
            if (LaunchPath is not null && Web.WebApps.Squirrel(new Web.WebAppLaunch(Web.WebAppKind.Electron, LaunchPath, "")) is { SquirrelExe: { } exe } squirrel)
            {
                LaunchPath = squirrel.ExePath;
                LaunchArguments = ["--processStart", exe];
            }
            UsesNames = steps.Any(s => s.ChosenSelector is { } sel && sel.Steps.Prepend(sel.Window).Any(st => st.Get(SelectorProperty.Name) is not null));
        }

        public IReadOnlyList<RecordedStep> Steps { get; }

        public Dictionary<int, string> SecretNames { get; } = [];

        public IReadOnlyList<string> Apps { get; }

        public string? LaunchPath { get; }

        public IReadOnlyList<string> LaunchArguments { get; } = [];

        public bool UsesNames { get; }

        public bool HasSecrets => SecretNames.Count > 0;
    }

    private static void Header(StringBuilder sb, string comment, Script script, string launchLine)
    {
        var apps = script.Apps.Count == 0 ? "" : $" in {string.Join(", ", script.Apps)}";
        sb.AppendLine($"{comment} Recorded with UiInspector: {script.Steps.Count} step{(script.Steps.Count == 1 ? "" : "s")}{apps}.");
        sb.AppendLine($"{comment} Start the app before running this; the script attaches to its open windows.");
        if (script.LaunchPath is not null) sb.AppendLine($"{comment} To start it from the script instead: {launchLine}");
        if (script.UsesNames) sb.AppendLine($"{comment} Note: steps that match by name follow the app's UI language.");
        foreach (var name in script.SecretNames.Values) sb.AppendLine($"{comment} Set the {name} environment variable to the password typed in that step.");
        if (script.Steps.Any(s => s.Action is RecordedAction.DoubleClick or RecordedAction.RightClick
                                  || s.Action == RecordedAction.Click && ClickAction(s.Target) == ElementAction.Click))
        {
            sb.AppendLine($"{comment} Steps marked \"moves the real mouse\" need their element visible on screen.");
        }
        sb.AppendLine();
    }

    private static void StepComment(StringBuilder sb, string comment, string indent, int number, RecordedStep step)
    {
        if (step.ChosenSelector is not { } selector)
        {
            var why = step.IsGenerating ? "its selector was still being generated" : step.Problem ?? "no selector could be built";
            sb.AppendLine($"{indent}{comment} {number}. {OneLine(step.Describe())}: SKIPPED, {why}.");
            return;
        }

        var status = selector.Verification.Status switch
        {
            VerificationStatus.Unique => "verified",
            VerificationStatus.Unverified => "NOT verified, the element was gone before it could be checked",
            _ => selector.Verification.Describe(),
        };
        sb.AppendLine($"{indent}{comment} {number}. {OneLine(step.Describe())}  ({selector.Strategy}; {status})");
        var notes = selector.Warnings.Where(w => w is not SelectorGenerator.LanguageWarning and not SelectorGenerator.UnverifiedWarning);
        foreach (var note in notes) sb.AppendLine($"{indent}{comment}    Note: {note}");
    }

    // ------------------------------------------------------------------ FlaUI

    private static string FlaUi(Script script)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// NuGet: FlaUI.UIA3");
        var launchArguments = script.LaunchArguments.Count == 0 ? "" : $", {Cs(string.Join(' ', script.LaunchArguments))}";
        Header(sb, "//", script, $"FlaUI.Core.Application.Launch(@\"{script.LaunchPath?.Replace("\"", "\"\"")}\"{launchArguments});");
        sb.AppendLine("using FlaUI.Core.AutomationElements;");
        sb.AppendLine("using FlaUI.Core.Definitions;");
        sb.AppendLine("using FlaUI.Core.Input;");
        sb.AppendLine("using FlaUI.Core.Tools;");
        sb.AppendLine("using FlaUI.Core.WindowsAPI;");
        sb.AppendLine("using FlaUI.UIA3;");
        sb.AppendLine();
        sb.AppendLine("using var automation = new UIA3Automation();");
        sb.AppendLine("var cf = automation.ConditionFactory;");
        sb.AppendLine($"var timeout = TimeSpan.FromSeconds({WaitSeconds});");
        sb.AppendLine();
        sb.AppendLine("AutomationElement Find(Func<AutomationElement?> search, string what) =>");
        sb.AppendLine("    Retry.WhileNull(search, timeout).Result ?? throw new InvalidOperationException($\"{what} not found.\");");
        if (script.HasSecrets)
        {
            sb.AppendLine();
            sb.AppendLine("string Secret(string name) => Environment.GetEnvironmentVariable(name)");
            sb.AppendLine("    ?? throw new InvalidOperationException($\"Set the {name} environment variable to the password to type.\");");
        }

        for (var i = 0; i < script.Steps.Count; i++)
        {
            var step = script.Steps[i];
            sb.AppendLine();
            StepComment(sb, "//", "", i + 1, step);
            if (step.ChosenSelector is not { } s) continue;

            sb.AppendLine("{");
            var windowName = s.Steps.Count == 0 ? "target" : "window";
            sb.AppendLine($"    var {windowName} = Find(() => {FlaUiFind("automation.GetDesktop()", s.Window, children: true)}, {Cs(StepLabel(s.Window))});");
            var scope = "window";
            for (var j = 0; j < s.Steps.Count; j++)
            {
                var name = j == s.Steps.Count - 1 ? "target" : $"scope{j + 1}";
                sb.AppendLine($"    var {name} = Find(() => {FlaUiFind(scope, s.Steps[j], children: false)}, {Cs(StepLabel(s.Steps[j]))});");
                scope = name;
            }
            foreach (var line in FlaUiStepAction(script, step, i + 1)) sb.AppendLine("    " + line);
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static IEnumerable<string> FlaUiStepAction(Script script, RecordedStep step, int number)
    {
        var text = script.SecretNames.TryGetValue(step.Id, out var secret) ? $"Secret({Cs(secret)})" : Cs(step.Text ?? "");
        switch (step.Action)
        {
            case RecordedAction.Click:
                yield return FlaUiAction(ClickAction(step.Target), "");
                break;
            case RecordedAction.DoubleClick:
                yield return "target.DoubleClick(); // moves the real mouse";
                break;
            case RecordedAction.RightClick:
                yield return "target.RightClick(); // moves the real mouse";
                break;
            case RecordedAction.SetValue:
                yield return FlaUiAction(ElementAction.SetValue, text);
                break;
            case RecordedAction.TypeText:
                yield return "target.Focus();";
                yield return $"Keyboard.Type({text});";
                break;
            case RecordedAction.PressKey when step.Key is { } key:
                yield return "target.Focus();";
                var press = key.HasCommandModifier || key.Shift
                    ? $"Keyboard.TypeSimultaneously({string.Join(", ", FlaUiKeys(key))});"
                    : $"Keyboard.Type({FlaUiKeys(key).Single()});";
                yield return step.Repeat > 1 ? $"for (var i = 0; i < {step.Repeat}; i++) {press}" : press;
                break;
            case RecordedAction.CheckExists:
                yield return $"Console.WriteLine({Cs($"Check {number} passed: the element exists.")});";
                break;
            case RecordedAction.CheckText:
                yield return step.CheckSource == TextSource.Value
                    ? "var actual = target.Patterns.Value.Pattern.Value.Value;"
                    : "var actual = target.Name;";
                yield return $"if (actual != {Cs(step.Text ?? "")})";
                yield return $"    throw new InvalidOperationException($\"Check {number} failed: expected \\\"{CsEscape(Interpolated(step.Text ?? ""))}\\\", got \\\"{{actual}}\\\".\");";
                yield return $"Console.WriteLine({Cs($"Check {number} passed.")});";
                break;
        }
    }

    // Text inside a C# interpolated string: braces must be doubled.
    private static string Interpolated(string text) => text.Replace("{", "{{").Replace("}", "}}");

    private static IEnumerable<string> FlaUiKeys(KeyChord key)
    {
        if (key.Ctrl) yield return "VirtualKeyShort.CONTROL";
        if (key.Alt) yield return "VirtualKeyShort.ALT";
        if (key.Shift) yield return "VirtualKeyShort.SHIFT";
        if (key.Win) yield return "VirtualKeyShort.LWIN";
        var name = key.KeyName ?? "";
        yield return "VirtualKeyShort." + name switch
        {
            "Backspace" => "BACK",
            "PageUp" => "PRIOR",
            "PageDown" => "NEXT",
            { Length: 1 } => "KEY_" + name,
            _ => name.ToUpperInvariant(),
        };
    }

    // ------------------------------------------------------------------ Python uiautomation

    private static string PythonUiAutomation(Script script)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# pip install uiautomation");
        Header(sb, "#", script, $"subprocess.Popen([r'{script.LaunchPath}'{string.Concat(script.LaunchArguments.Select(a => ", " + Py(a)))}])");
        sb.AppendLine("import os");
        sb.AppendLine("import uiautomation as auto");
        sb.AppendLine();
        sb.AppendLine($"TIMEOUT = {WaitSeconds}");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def find(control, what):");
        sb.AppendLine("    if not control.Exists(maxSearchSeconds=TIMEOUT):");
        sb.AppendLine("        raise LookupError(what + ' not found')");
        sb.AppendLine("    return control");
        PythonSecretHelper(sb, script);

        for (var i = 0; i < script.Steps.Count; i++)
        {
            var step = script.Steps[i];
            sb.AppendLine();
            StepComment(sb, "#", "", i + 1, step);
            if (step.ChosenSelector is not { } s) continue;

            var windowArgs = new List<string> { "searchDepth=1" };
            windowArgs.AddRange(UiaArgs(s.Window));
            var window = $"auto.{ControlClass(s.Window)}({string.Join(", ", windowArgs)})";
            if (s.Steps.Count == 0)
            {
                sb.AppendLine($"target = find({window}, {Py(StepLabel(s.Window))})");
            }
            else
            {
                sb.AppendLine($"window = {window}");
                var scope = "window";
                for (var j = 0; j < s.Steps.Count; j++)
                {
                    var control = $"{scope}.{ControlClass(s.Steps[j])}({string.Join(", ", UiaArgs(s.Steps[j]))})";
                    if (j == s.Steps.Count - 1)
                    {
                        sb.AppendLine($"target = find({control}, {Py(StepLabel(s.Steps[j]))})");
                    }
                    else
                    {
                        sb.AppendLine($"scope{j + 1} = {control}");
                        scope = $"scope{j + 1}";
                    }
                }
            }

            var text = PyText(script, step);
            switch (step.Action)
            {
                case RecordedAction.Click:
                    sb.AppendLine(UiaAction(ClickAction(step.Target), ""));
                    break;
                case RecordedAction.DoubleClick:
                    sb.AppendLine("target.DoubleClick()  # moves the real mouse");
                    break;
                case RecordedAction.RightClick:
                    sb.AppendLine("target.RightClick()  # moves the real mouse");
                    break;
                case RecordedAction.SetValue:
                    sb.AppendLine(UiaAction(ElementAction.SetValue, text));
                    break;
                case RecordedAction.TypeText:
                    sb.AppendLine(script.SecretNames.ContainsKey(step.Id)
                        ? $"target.SendKeys({UiaEscapeCall(text)})"
                        : $"target.SendKeys({Py(UiaEscape(step.Text ?? ""))})");
                    break;
                case RecordedAction.PressKey when step.Key is { } key:
                    var press = $"target.SendKeys({Py(UiaKeys(key))})";
                    sb.AppendLine(step.Repeat > 1 ? $"for _ in range({step.Repeat}):\n    {press}" : press);
                    break;
                default:
                    PythonCheck(sb, step, i + 1, "target.GetValuePattern().Value", "target.Name");
                    break;
            }
        }
        return sb.ToString();
    }

    private static string UiaKeys(KeyChord key)
    {
        var sb = new StringBuilder();
        if (key.Ctrl) sb.Append("{Ctrl}");
        if (key.Alt) sb.Append("{Alt}");
        if (key.Shift) sb.Append("{Shift}");
        if (key.Win) sb.Append("{Win}");
        var name = key.KeyName ?? "";
        sb.Append(name switch
        {
            { Length: 1 } => name.ToLowerInvariant(),
            "Escape" => "{Esc}",
            "Backspace" => "{Back}",
            _ => "{" + name + "}",
        });
        return sb.ToString();
    }

    // uiautomation's SendKeys treats {} as key names and () as grouping after a held key.
    private static string UiaEscape(string text) => string.Concat(text.Select(c => c switch
    {
        '{' => "{{}",
        '}' => "{}}",
        '(' => "{(}",
        ')' => "{)}",
        '\n' => "{Enter}",
        '\r' => "",
        _ => c.ToString(),
    }));

    // The same escaping, done by the script at run time (for passwords, which aren't in the script).
    private static string UiaEscapeCall(string expression) =>
        $"''.join({{'{{': '{{{{}}', '}}': '{{}}}}', '(': '{{(}}', ')': '{{)}}'}}.get(c, c) for c in {expression})";

    // ------------------------------------------------------------------ pywinauto

    private static string Pywinauto(Script script)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# pip install pywinauto");
        Header(sb, "#", script, script.LaunchArguments.Count == 0
            ? $"Application(backend=\"uia\").start(r'{script.LaunchPath}')"
            : $"Application(backend=\"uia\").start(r'\"{script.LaunchPath}\" {string.Join(' ', script.LaunchArguments)}')");
        sb.AppendLine("import os");
        sb.AppendLine("from pywinauto import Desktop");
        sb.AppendLine();
        sb.AppendLine($"TIMEOUT = {WaitSeconds}");
        sb.AppendLine("desktop = Desktop(backend=\"uia\")");
        PythonSecretHelper(sb, script);

        for (var i = 0; i < script.Steps.Count; i++)
        {
            var step = script.Steps[i];
            sb.AppendLine();
            StepComment(sb, "#", "", i + 1, step);
            if (step.ChosenSelector is not { } s) continue;

            sb.AppendLine($"window = desktop.window({string.Join(", ", PywinautoArgs(s.Window))})");
            var spec = "window";
            foreach (var st in s.Steps) spec += $".child_window({string.Join(", ", PywinautoArgs(st))})";
            sb.AppendLine($"target = {spec}.wait(\"exists\", timeout=TIMEOUT)");

            var text = PyText(script, step);
            switch (step.Action)
            {
                case RecordedAction.Click:
                    sb.AppendLine(PywinautoAction(ClickAction(step.Target), ""));
                    break;
                case RecordedAction.DoubleClick:
                    sb.AppendLine("target.double_click_input()  # moves the real mouse");
                    break;
                case RecordedAction.RightClick:
                    sb.AppendLine("target.right_click_input()  # moves the real mouse");
                    break;
                case RecordedAction.SetValue:
                    sb.AppendLine(PywinautoAction(ElementAction.SetValue, text));
                    break;
                case RecordedAction.TypeText:
                    var keys = script.SecretNames.ContainsKey(step.Id)
                        ? $"''.join('{{' + c + '}}' if c in '+^%~(){{}}[]' else c for c in {text})"
                        : Py(PywinautoEscape(step.Text ?? ""));
                    sb.AppendLine($"target.type_keys({keys}, with_spaces=True, with_tabs=True)");
                    break;
                case RecordedAction.PressKey when step.Key is { } key:
                    var press = $"target.type_keys({Py(PywinautoKeys(key))})";
                    sb.AppendLine(step.Repeat > 1 ? $"for _ in range({step.Repeat}):\n    {press}" : press);
                    break;
                default:
                    PythonCheck(sb, step, i + 1, "target.iface_value.CurrentValue", "target.element_info.name");
                    break;
            }
        }
        return sb.ToString();
    }

    private static string PywinautoKeys(KeyChord key)
    {
        var sb = new StringBuilder();
        if (key.Win) sb.Append("{VK_LWIN down}");
        if (key.Ctrl) sb.Append('^');
        if (key.Alt) sb.Append('%');
        if (key.Shift) sb.Append('+');
        var name = key.KeyName ?? "";
        sb.Append(name switch
        {
            { Length: 1 } => name.ToLowerInvariant(),
            "Escape" => "{ESC}",
            "PageUp" => "{PGUP}",
            "PageDown" => "{PGDN}",
            _ => "{" + name.ToUpperInvariant() + "}",
        });
        if (key.Win) sb.Append("{VK_LWIN up}");
        return sb.ToString();
    }

    // pywinauto's type_keys treats + ^ % ~ ( ) { } [ ] as commands; braces make them literal.
    private static string PywinautoEscape(string text) => string.Concat(text.Select(c => c switch
    {
        '+' or '^' or '%' or '~' or '(' or ')' or '{' or '}' or '[' or ']' => "{" + c + "}",
        '\n' => "{ENTER}",
        '\r' => "",
        _ => c.ToString(),
    }));

    // ------------------------------------------------------------------ Appium

    private static string Appium(Script script)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# pip install Appium-Python-Client; needs an Appium server with the Windows driver");
        Header(sb, "#", script, $"start it yourself, or use options.app = r'{script.LaunchPath}'");
        sb.AppendLine("import os");
        sb.AppendLine("from appium import webdriver");
        sb.AppendLine("from appium.options.windows import WindowsOptions");
        sb.AppendLine("from appium.webdriver.common.appiumby import AppiumBy");
        sb.AppendLine("from selenium.webdriver.common.action_chains import ActionChains");
        sb.AppendLine("from selenium.webdriver.common.keys import Keys");
        sb.AppendLine();
        sb.AppendLine("options = WindowsOptions()");
        sb.AppendLine("options.app = \"Root\"  # desktop session: attach to apps that are already running");
        sb.AppendLine("driver = webdriver.Remote(\"http://127.0.0.1:4723\", options=options)");
        sb.AppendLine($"driver.implicitly_wait({WaitSeconds})");
        PythonSecretHelper(sb, script);

        for (var i = 0; i < script.Steps.Count; i++)
        {
            var step = script.Steps[i];
            sb.AppendLine();
            StepComment(sb, "#", "", i + 1, step);
            if (step.ChosenSelector is not { } s) continue;

            var windowXPath = Py("/Pane[@ClassName='#32769']/" + XPathStep(s.Window));
            sb.AppendLine($"{(s.Steps.Count == 0 ? "target" : "window")} = driver.find_element(AppiumBy.XPATH, {windowXPath})");
            var scope = "window";
            for (var j = 0; j < s.Steps.Count; j++)
            {
                var st = s.Steps[j];
                var name = j == s.Steps.Count - 1 ? "target" : $"scope{j + 1}";
                var xpath = Py(".//" + XPathStep(st with { Index = null }));
                sb.AppendLine(st.Index is { } index
                    ? $"{name} = {scope}.find_elements(AppiumBy.XPATH, {xpath})[{index}]"
                    : $"{name} = {scope}.find_element(AppiumBy.XPATH, {xpath})");
                scope = name;
            }

            var text = PyText(script, step);
            switch (step.Action)
            {
                case RecordedAction.Click:
                    sb.AppendLine(AppiumAction(ClickAction(step.Target), ""));
                    break;
                case RecordedAction.DoubleClick:
                    sb.AppendLine("ActionChains(driver).double_click(target).perform()");
                    break;
                case RecordedAction.RightClick:
                    sb.AppendLine("ActionChains(driver).context_click(target).perform()");
                    break;
                case RecordedAction.SetValue:
                    sb.AppendLine(AppiumAction(ElementAction.SetValue, text));
                    break;
                case RecordedAction.TypeText:
                    sb.AppendLine($"target.send_keys({text})");
                    break;
                case RecordedAction.PressKey when step.Key is { } key:
                    var press = $"target.send_keys({AppiumKeys(key)})";
                    sb.AppendLine(step.Repeat > 1 ? $"for _ in range({step.Repeat}):\n    {press}" : press);
                    break;
                default:
                    PythonCheck(sb, step, i + 1, "target.get_attribute(\"Value.Value\")", "target.get_attribute(\"Name\")");
                    break;
            }
        }
        return sb.ToString();
    }

    // The Windows driver toggles a modifier each time it's sent: once to press it, once to release it.
    private static string AppiumKeys(KeyChord key)
    {
        var modifiers = new List<string>();
        if (key.Ctrl) modifiers.Add("Keys.CONTROL");
        if (key.Alt) modifiers.Add("Keys.ALT");
        if (key.Shift) modifiers.Add("Keys.SHIFT");
        if (key.Win) modifiers.Add("Keys.META");
        var name = key.KeyName ?? "";
        var main = name switch
        {
            { Length: 1 } => Py(name.ToLowerInvariant()),
            "Backspace" => "Keys.BACKSPACE",
            "PageUp" => "Keys.PAGE_UP",
            "PageDown" => "Keys.PAGE_DOWN",
            _ => "Keys." + name.ToUpperInvariant(),
        };
        return string.Join(" + ", modifiers.Append(main).Concat(modifiers));
    }

    // ------------------------------------------------------------------ shared Python

    private static void PythonSecretHelper(StringBuilder sb, Script script)
    {
        if (!script.HasSecrets) return;
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def secret(name):");
        sb.AppendLine("    value = os.environ.get(name)");
        sb.AppendLine("    if value is None:");
        sb.AppendLine("        raise KeyError('Set the ' + name + ' environment variable to the password to type.')");
        sb.AppendLine("    return value");
    }

    private static string PyText(Script script, RecordedStep step) =>
        script.SecretNames.TryGetValue(step.Id, out var secret) ? $"secret({Py(secret)})" : Py(step.Text ?? "");

    private static void PythonCheck(StringBuilder sb, RecordedStep step, int number, string valueRead, string nameRead)
    {
        if (step.Action == RecordedAction.CheckText)
        {
            sb.AppendLine($"actual = {(step.CheckSource == TextSource.Value ? valueRead : nameRead)}");
            sb.AppendLine($"if actual != {Py(step.Text ?? "")}:");
            sb.AppendLine($"    raise AssertionError('Check {number} failed: expected ' + repr({Py(step.Text ?? "")}) + ', got ' + repr(actual))");
        }
        sb.AppendLine($"print('Check {number} passed.')");
    }
}

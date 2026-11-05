using System.Text;
using System.Text.RegularExpressions;

namespace UiInspector.Core.Selectors;

public sealed record CodeSnippet(string Id, string Title, string Code);

/// <summary>What the generated code does with the element once found.</summary>
public enum ElementAction
{
    /// <summary>Only the window was selected: find it, nothing else.</summary>
    None,
    Invoke,
    Toggle,
    Select,
    Expand,
    SetValue,
    Click,
}

/// <summary>
/// Renders a verified <see cref="Selector"/> as copy-pasteable code. Every snippet attaches to the
/// already-running app from the desktop (it doesn't launch anything), waits up to 10 s for the
/// element, then performs the most robust action the element supports: a UIA pattern call where
/// possible, a mouse click only as a fallback.
/// </summary>
public static partial class CodeTemplates
{
    private const string SampleText = "Hello from UiInspector";
    private const int WaitSeconds = 10;

    public static IReadOnlyList<CodeSnippet> Render(Selector selector, SelectorResult result)
    {
        var action = selector.Steps.Count == 0 ? ElementAction.None : ChooseAction(result.Target);
        return
        [
            new("flaui", "FlaUI (C#)", FlaUi(selector, result, action)),
            new("uiautomation", "Python · uiautomation", PythonUiAutomation(selector, result, action)),
            new("pywinauto", "Python · pywinauto", Pywinauto(selector, result, action)),
            new("appium", "Appium · Python", Appium(selector, result, action)),
        ];
    }

    public static ElementAction ChooseAction(ElementInfo target)
    {
        var p = target.Patterns;
        if (p.Contains("Invoke")) return ElementAction.Invoke;
        if (p.Contains("Toggle")) return ElementAction.Toggle;
        if (p.Contains("SelectionItem")) return ElementAction.Select;
        if (p.Contains("ExpandCollapse")) return ElementAction.Expand;
        if (p.Contains("Value") && !target.IsPassword && target.ControlType is "Edit" or "Document" or "ComboBox")
            return ElementAction.SetValue;
        return ElementAction.Click;
    }

    // ------------------------------------------------------------------ FlaUI

    private static string FlaUi(Selector s, SelectorResult r, ElementAction action)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// NuGet: FlaUI.UIA3");
        Header(sb, "//", s, r);
        sb.AppendLine("using FlaUI.Core.AutomationElements;");
        sb.AppendLine("using FlaUI.Core.Definitions;");
        sb.AppendLine("using FlaUI.Core.Tools;");
        sb.AppendLine("using FlaUI.UIA3;");
        sb.AppendLine();
        sb.AppendLine("using var automation = new UIA3Automation();");
        sb.AppendLine("var cf = automation.ConditionFactory;");
        sb.AppendLine($"var timeout = TimeSpan.FromSeconds({WaitSeconds});");
        sb.AppendLine();

        sb.AppendLine("var window = Retry.WhileNull(");
        sb.AppendLine($"    () => {FlaUiFind("automation.GetDesktop()", s.Window, children: true)},");
        sb.AppendLine("    timeout).Result ?? throw new InvalidOperationException(\"Window not found.\");");

        var scope = "window";
        for (var i = 0; i < s.Steps.Count; i++)
        {
            var name = i == s.Steps.Count - 1 ? "target" : $"scope{i + 1}";
            sb.AppendLine($"var {name} = Retry.WhileNull(");
            sb.AppendLine($"    () => {FlaUiFind(scope, s.Steps[i], children: false)},");
            sb.AppendLine($"    timeout).Result ?? throw new InvalidOperationException(\"{CsEscape(StepLabel(s.Steps[i]))} not found.\");");
            scope = name;
        }

        sb.AppendLine();
        sb.AppendLine(action switch
        {
            ElementAction.None => "Console.WriteLine($\"Found window: {window.Name}\");",
            ElementAction.Invoke => "target.Patterns.Invoke.Pattern.Invoke();",
            ElementAction.Toggle => "target.Patterns.Toggle.Pattern.Toggle();",
            ElementAction.Select => "target.Patterns.SelectionItem.Pattern.Select();",
            ElementAction.Expand => "target.Patterns.ExpandCollapse.Pattern.Expand();",
            ElementAction.SetValue => $"target.Patterns.Value.Pattern.SetValue({Cs(SampleText)});",
            _ => "target.Click(); // no pattern action available; this moves the real mouse",
        });
        return sb.ToString();
    }

    private static string FlaUiFind(string scope, SelectorStep step, bool children)
    {
        var exact = step.Conditions.Where(c => c.Match == MatchKind.Exact).Select(FlaUiCondition).ToList();
        var condition = exact.Count == 0
            ? "FlaUI.Core.Conditions.TrueCondition.Default"
            : string.Join(".And(", exact) + new string(')', exact.Count - 1);
        var contains = step.Conditions.Where(c => c.Match == MatchKind.Contains).ToList();
        var all = children ? "FindAllChildren" : "FindAllDescendants";
        var first = children ? "FindFirstChild" : "FindFirstDescendant";

        if (contains.Count == 0 && step.Index is null) return $"{scope}.{first}({condition})";

        var query = $"{scope}.{all}({condition})";
        foreach (var c in contains) query += $".Where(e => e.{c.Property}.Contains({Cs(c.Value)}))";
        return step.Index is { } index ? $"{query}.ElementAtOrDefault({index})" : $"{query}.FirstOrDefault()";
    }

    private static string FlaUiCondition(SelectorCondition c) => c.Property switch
    {
        SelectorProperty.ControlType => $"cf.ByControlType(ControlType.{c.Value})",
        SelectorProperty.AutomationId => $"cf.ByAutomationId({Cs(c.Value)})",
        SelectorProperty.Name => $"cf.ByName({Cs(c.Value)})",
        SelectorProperty.ClassName => $"cf.ByClassName({Cs(c.Value)})",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    // ------------------------------------------------------------------ Python uiautomation

    private static string PythonUiAutomation(Selector s, SelectorResult r, ElementAction action)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# pip install uiautomation");
        Header(sb, "#", s, r);
        sb.AppendLine("import uiautomation as auto");
        sb.AppendLine();

        var windowArgs = new List<string> { "searchDepth=1" };
        windowArgs.AddRange(UiaArgs(s.Window));
        sb.AppendLine($"window = auto.{ControlClass(s.Window)}({string.Join(", ", windowArgs)})");

        var scope = "window";
        for (var i = 0; i < s.Steps.Count; i++)
        {
            var name = i == s.Steps.Count - 1 ? "target" : $"scope{i + 1}";
            sb.AppendLine($"{name} = {scope}.{ControlClass(s.Steps[i])}({string.Join(", ", UiaArgs(s.Steps[i]))})");
            scope = name;
        }

        var last = s.Steps.Count == 0 ? "window" : "target";
        sb.AppendLine($"if not {last}.Exists(maxSearchSeconds={WaitSeconds}):");
        sb.AppendLine($"    raise LookupError({Py(StepLabel(s.Steps.Count == 0 ? s.Window : s.Steps[^1]) + " not found")})");
        sb.AppendLine(action switch
        {
            ElementAction.None => "print('Found window:', window.Name)",
            ElementAction.Invoke => "target.GetInvokePattern().Invoke()",
            ElementAction.Toggle => "target.GetTogglePattern().Toggle()",
            ElementAction.Select => "target.GetSelectionItemPattern().Select()",
            ElementAction.Expand => "target.GetExpandCollapsePattern().Expand()",
            ElementAction.SetValue => $"target.GetValuePattern().SetValue({Py(SampleText)})",
            _ => "target.Click()  # no pattern action available; this moves the real mouse",
        });
        return sb.ToString();
    }

    private static string ControlClass(SelectorStep step) => (step.ControlType ?? "") + "Control";

    private static IEnumerable<string> UiaArgs(SelectorStep step)
    {
        foreach (var c in step.Conditions)
        {
            switch (c.Property)
            {
                case SelectorProperty.AutomationId: yield return $"AutomationId={Py(c.Value)}"; break;
                case SelectorProperty.ClassName: yield return $"ClassName={Py(c.Value)}"; break;
                case SelectorProperty.Name:
                    yield return c.Match == MatchKind.Contains ? $"SubName={Py(c.Value)}" : $"Name={Py(c.Value)}";
                    break;
            }
        }
        if (step.Index is { } index) yield return $"foundIndex={index + 1}"; // 1-based in uiautomation
    }

    // ------------------------------------------------------------------ pywinauto

    private static string Pywinauto(Selector s, SelectorResult r, ElementAction action)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# pip install pywinauto");
        Header(sb, "#", s, r);
        sb.AppendLine("from pywinauto import Desktop");
        sb.AppendLine();
        sb.AppendLine($"window = Desktop(backend=\"uia\").window({string.Join(", ", PywinautoArgs(s.Window))})");

        var spec = "window";
        foreach (var step in s.Steps) spec += $".child_window({string.Join(", ", PywinautoArgs(step))})";
        var last = s.Steps.Count == 0 ? "window" : "target";
        sb.AppendLine($"{last} = {spec}.wait(\"exists\", timeout={WaitSeconds})");
        sb.AppendLine(action switch
        {
            ElementAction.None => "print(\"Found window:\", window.window_text())",
            ElementAction.Invoke => "target.iface_invoke.Invoke()",
            ElementAction.Toggle => "target.iface_toggle.Toggle()",
            ElementAction.Select => "target.iface_selection_item.Select()",
            ElementAction.Expand => "target.iface_expand_collapse.Expand()",
            ElementAction.SetValue => $"target.iface_value.SetValue({Py(SampleText)})",
            _ => "target.click_input()  # no pattern action available; this moves the real mouse",
        });
        return sb.ToString();
    }

    private static IEnumerable<string> PywinautoArgs(SelectorStep step)
    {
        foreach (var c in step.Conditions)
        {
            yield return c.Property switch
            {
                SelectorProperty.ControlType => $"control_type={Py(c.Value)}",
                SelectorProperty.AutomationId => $"auto_id={Py(c.Value)}",
                SelectorProperty.ClassName => $"class_name={Py(c.Value)}",
                SelectorProperty.Name when c.Match == MatchKind.Contains => $"title_re={Py(".*" + Regex.Escape(c.Value) + ".*")}",
                _ => $"title={Py(c.Value)}",
            };
        }
        if (step.Index is { } index) yield return $"found_index={index}";
    }

    // ------------------------------------------------------------------ Appium

    private static string Appium(Selector s, SelectorResult r, ElementAction action)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# pip install Appium-Python-Client; needs an Appium server with the Windows driver");
        Header(sb, "#", s, r);
        sb.AppendLine("from appium import webdriver");
        sb.AppendLine("from appium.options.windows import WindowsOptions");
        sb.AppendLine("from appium.webdriver.common.appiumby import AppiumBy");
        sb.AppendLine();
        sb.AppendLine("options = WindowsOptions()");
        sb.AppendLine("options.app = \"Root\"  # desktop session: attach to apps that are already running");
        sb.AppendLine("driver = webdriver.Remote(\"http://127.0.0.1:4723\", options=options)");
        sb.AppendLine($"driver.implicitly_wait({WaitSeconds})");
        sb.AppendLine();
        sb.AppendLine($"window = driver.find_element(AppiumBy.XPATH, {Py("/Pane[@ClassName='#32769']/" + XPathStep(s.Window))})");

        var scope = "window";
        for (var i = 0; i < s.Steps.Count; i++)
        {
            var step = s.Steps[i];
            var name = i == s.Steps.Count - 1 ? "target" : $"scope{i + 1}";
            var xpath = Py(".//" + XPathStep(step with { Index = null }));
            sb.AppendLine(step.Index is { } index
                ? $"{name} = {scope}.find_elements(AppiumBy.XPATH, {xpath})[{index}]"
                : $"{name} = {scope}.find_element(AppiumBy.XPATH, {xpath})");
            scope = name;
        }

        sb.AppendLine(action switch
        {
            ElementAction.None => "print(\"Found window:\", window.get_attribute(\"Name\"))",
            ElementAction.SetValue => $"target.send_keys({Py(SampleText)})",
            _ => "target.click()",
        });
        return sb.ToString();
    }

    private static string XPathStep(SelectorStep step)
    {
        var predicates = step.Conditions
            .Where(c => c.Property != SelectorProperty.ControlType)
            .Select(c => c.Match == MatchKind.Contains
                ? $"[contains(@{c.Property},{XPathLiteral(c.Value)})]"
                : $"[@{c.Property}={XPathLiteral(c.Value)}]");
        return (step.ControlType ?? "*") + string.Concat(predicates);
    }

    private static string XPathLiteral(string value)
    {
        if (!value.Contains('\'')) return $"'{value}'";
        if (!value.Contains('"')) return $"\"{value}\"";
        return "concat('" + value.Replace("'", "',\"'\",'") + "')";
    }

    // ------------------------------------------------------------------ shared

    private static void Header(StringBuilder sb, string comment, Selector s, SelectorResult r)
    {
        var app = string.IsNullOrEmpty(r.ProcessName) ? "" : $" in {r.ProcessName}";
        sb.AppendLine($"{comment} Target: {Describe(r.Target)}{app}");
        sb.AppendLine($"{comment} Strategy: {s.Strategy}. {s.Verification.Describe()}");
        foreach (var warning in s.Warnings) sb.AppendLine($"{comment} Note: {warning}");
        sb.AppendLine();
    }

    private static string Describe(ElementInfo e) =>
        $"{(string.IsNullOrEmpty(e.ControlType) ? "?" : e.ControlType)} {(string.IsNullOrEmpty(e.Name) ? "(no name)" : $"\"{OneLine(e.Name)}\"")}";

    private static string StepLabel(SelectorStep step)
    {
        var type = step.ControlType ?? "Element";
        if (step.Get(SelectorProperty.AutomationId) is { } id) return $"{type} #{id.Value}";
        if (step.Get(SelectorProperty.Name) is { } name) return $"{type} '{name.Value}'";
        return type;
    }

    private static string OneLine(string text) => NewLines().Replace(text, " ");

    private static string Cs(string value) => "\"" + CsEscape(value) + "\"";

    private static string CsEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Py(string value) =>
        "'" + value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n") + "'";

    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex NewLines();
}

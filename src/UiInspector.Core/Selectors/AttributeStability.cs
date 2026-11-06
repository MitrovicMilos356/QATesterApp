using System.Text.RegularExpressions;

namespace UiInspector.Core.Selectors;

/// <summary>How far an attribute value can be trusted to identify an element across runs and builds.</summary>
public enum Stability
{
    /// <summary>Don't use it: generated, randomized or content-like.</summary>
    Unusable,

    /// <summary>Usable, but with a caveat the user should see.</summary>
    Weak,

    Stable,
}

/// <summary>
/// The verdict on one attribute value. <see cref="Value"/> may be shortened from the original
/// (e.g. a stable prefix of a name), in which case <see cref="Match"/> is <see cref="MatchKind.Contains"/>.
/// </summary>
public sealed record AttributeVerdict(Stability Stability, string Value, MatchKind Match, string? Warning)
{
    public bool IsUsable => Stability != Stability.Unusable;

    public static AttributeVerdict Unusable(string value, string reason) => new(Stability.Unusable, value, MatchKind.Exact, reason);
}

/// <summary>
/// Heuristics for spotting attribute values that will break a selector: generated hashes, GUIDs,
/// per-build class names, and names that embed changing content. They're heuristics, so every
/// non-obvious decision comes with a warning the user can read.
/// </summary>
public static partial class AttributeStability
{
    private const int MaxNameLength = 80;
    private const int MinPrefixLength = 3;

    /// <param name="frameworkId">UIA FrameworkId of the element, e.g. "WinForm".</param>
    /// <param name="nativeWindowHandle">The element's HWND, if it has one.</param>
    public static AttributeVerdict AnalyzeAutomationId(string id, string frameworkId = "", IntPtr nativeWindowHandle = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return AttributeVerdict.Unusable(id, "No AutomationId.");
        if (Guid().IsMatch(id)) return AttributeVerdict.Unusable(id, $"AutomationId '{id}' is a GUID, which is usually generated per run.");
        if (HashedToken().IsMatch(id)) return AttributeVerdict.Unusable(id, $"AutomationId '{id}' looks generated (contains a hash).");
        if (FrameworkGeneratedId().IsMatch(id))
        {
            return AttributeVerdict.Unusable(id, $"AutomationId '{id}' is an id the page's framework makes up (like React's useId); it changes when the app restarts or redraws.");
        }

        if (AllDigits().IsMatch(id))
        {
            // WinForms controls without a Name report their window handle as the AutomationId.
            if (frameworkId == "WinForm" || (nativeWindowHandle != IntPtr.Zero && id == nativeWindowHandle.ToInt64().ToString()))
            {
                return AttributeVerdict.Unusable(id, $"AutomationId '{id}' is the control's window handle, which changes every time the app starts.");
            }
            return new(Stability.Weak, id, MatchKind.Exact,
                $"AutomationId '{id}' is a numeric Win32 control ID: stable for one app version, but not descriptive and it can repeat across dialogs.");
        }
        if (DesignerDefaultName().IsMatch(id))
        {
            return new(Stability.Weak, id, MatchKind.Exact,
                $"AutomationId '{id}' looks like a designer default name (type + number); it may change when the form is edited.");
        }
        return new(Stability.Stable, id, MatchKind.Exact, null);
    }

    public static AttributeVerdict AnalyzeClassName(string className, string frameworkId)
    {
        if (string.IsNullOrWhiteSpace(className)) return AttributeVerdict.Unusable(className, "No ClassName.");
        if (className.StartsWith("WindowsForms10.", StringComparison.Ordinal))
        {
            return AttributeVerdict.Unusable(className, "WinForms class names contain a per-build suffix, so they change between builds.");
        }
        if (frameworkId is "Chrome" or "Gecko" && className.Contains(' '))
        {
            return AttributeVerdict.Unusable(className, "This is an HTML class list; it describes styling, not identity.");
        }
        if (frameworkId is "Chrome" or "Gecko" && UtilityClass().IsMatch(className))
        {
            return AttributeVerdict.Unusable(className, $"'{className}' is a styling utility class (Tailwind, Bootstrap); it says nothing about the element and changes with any redesign.");
        }
        if (HashedToken().IsMatch(className))
        {
            return AttributeVerdict.Unusable(className, $"ClassName '{className}' contains a generated hash (CSS modules); it changes with every build.");
        }
        return new(Stability.Stable, className, MatchKind.Exact, null);
    }

    /// <summary>Analyzes an element's Name; may return a shorter stable part to match with Contains.</summary>
    public static AttributeVerdict AnalyzeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return AttributeVerdict.Unusable(name, "No Name.");

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength || trimmed.Contains('\n'))
        {
            return AttributeVerdict.Unusable(name, "The name is long or multi-line, so it's likely content (text, a message), not a label.");
        }

        // "GoAway 11:21 PM", "Today at 9:05", "Edited 05/11/2026": times and dates change every time.
        if (TimeOrDate().IsMatch(trimmed))
        {
            var rest = TimeOrDate().Replace(trimmed, " ").Trim(' ', ',', '-', '·', '—');
            rest = MultipleSpaces().Replace(rest, " ");
            if (rest.Length < MinPrefixLength) return AttributeVerdict.Unusable(name, $"The name \"{trimmed}\" is a time or date, which changes.");
            return new(Stability.Weak, rest, MatchKind.Contains, $"Matches only \"{rest}\": the rest of \"{trimmed}\" is a time or date, which changes.");
        }

        // "Dyno (direct message), Online": the part after the last comma is often a status.
        var comma = trimmed.LastIndexOf(", ", StringComparison.Ordinal);
        if (comma >= MinPrefixLength)
        {
            var head = trimmed[..comma];
            var tail = trimmed[(comma + 2)..];
            return new(Stability.Weak, head, MatchKind.Contains,
                $"Matches only \"{head}\": the rest (\"{tail}\") looks like a status that can change.");
        }

        // "Inbox (3)", "Messages 12": a trailing counter.
        var counter = TrailingCounter().Match(trimmed);
        if (counter.Success && counter.Groups["head"].Value.Length >= MinPrefixLength)
        {
            var head = counter.Groups["head"].Value;
            return new(Stability.Weak, head, MatchKind.Contains,
                $"Matches only \"{head}\": the trailing number looks like a counter.");
        }

        if (trimmed.Any(char.IsDigit))
        {
            return new(Stability.Weak, trimmed, MatchKind.Exact,
                "The name contains numbers (a count, date or ID?); check that it doesn't change.");
        }

        return new(Stability.Stable, trimmed, MatchKind.Exact, null);
    }

    /// <summary>
    /// Text that tells a container apart from its look-alikes ("Order 1042" in one of many rows).
    /// Unlike <see cref="AnalyzeName"/>, numbers are kept: here they're usually the identity.
    /// </summary>
    public static AttributeVerdict AnalyzeLandmarkText(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return AttributeVerdict.Unusable(name, "No text.");
        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength / 2 || trimmed.Contains('\n') || Guid().IsMatch(trimmed) || HashedToken().IsMatch(trimmed) || TimeOrDate().IsMatch(trimmed))
        {
            return AttributeVerdict.Unusable(name, "Too long or generated to identify a row by.");
        }

        var comma = trimmed.LastIndexOf(", ", StringComparison.Ordinal);
        if (comma >= MinPrefixLength) trimmed = trimmed[..comma];
        var match = comma >= MinPrefixLength ? MatchKind.Contains : MatchKind.Exact;

        return trimmed.Any(char.IsDigit)
            ? new(Stability.Weak, trimmed, match, $"Finds its container by the text \"{trimmed}\" in it, so the test depends on that data (fine when the test data is fixed).")
            : new(Stability.Stable, trimmed, match, $"Finds its container by the text \"{trimmed}\" in it.");
    }

    /// <summary>For window titles like "Untitled - Notepad" or "@GoAway - Discord": the app-name part.</summary>
    public static AttributeVerdict AnalyzeWindowTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return AttributeVerdict.Unusable(title, "The window has no title.");

        foreach (var separator in new[] { " - ", " — ", " – ", " | " })
        {
            var index = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (index < 0) continue;
            var appPart = title[(index + separator.Length)..].Trim();
            if (appPart.Length >= MinPrefixLength)
            {
                return new(Stability.Weak, appPart, MatchKind.Contains,
                    $"Matches the window by \"{appPart}\" only; the rest of the title (document, chat, page) changes.");
            }
        }
        return new(Stability.Weak, title.Trim(), MatchKind.Exact, "Matches the full window title; titles often change with content.");
    }

    [GeneratedRegex(@"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$")]
    private static partial Regex Guid();

    // CSS-module style hashes: a 5+ character hex token mixing letters and digits, glued on with
    // _ or - ("__972a0", "_d125d2", "container_e45859"), or Emotion's "css-1q2w3e".
    // Plain words like "go-button" or "user_admin2" don't match.
    [GeneratedRegex(@"(?:^|[_-])(?=[0-9a-f]*\d)(?=[0-9a-f]*[a-f])[0-9a-f]{5,}(?:$|[\s_-])|(?:^|\s)css-[0-9a-z]{5,}(?:$|\s)")]
    private static partial Regex HashedToken();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex AllDigits();

    // Utility classes describe one bit of styling: "flex-1", "p-4", "mt-2", "w-full", "text-sm",
    // "bg-gray-100", "d-flex", "col-md-6", "hidden", "md:flex". A responsive or state prefix ("md:",
    // "hover:") makes no difference. A known prefix needs a styling value after it, so names like
    // "text-editor" or "content-wrapper" still count as real class names.
    [GeneratedRegex("""
        ^(?:[a-z0-9-]+:)*-?(?:
          (?:flex|grid|col|row|d|w|h|size|min-w|min-h|max-w|max-h|[mp][trblxyse]?|space-[xy]|gap(?:-[xy])?|text|font|leading|tracking
            |bg|border(?:-[trblxy])?|rounded(?:-[trbl]{1,2})?|shadow|opacity|z|overflow(?:-[xy])?|items|justify|self|place-(?:items|content|self)
            |content|align|top|left|right|bottom|inset(?:-[xy])?|basis|grow|shrink|order|col-span|row-span|offset|line-clamp|aspect|object
            |cursor|select|whitespace|break|duration|ease|delay|ring|outline|translate-[xy]|scale|rotate|fill|stroke|float|clear)
          (?:-(?:xs|sm|md|lg|xl|xxl))?
          (?:-(?:\d+(?:\.\d+)?|\d+/\d+|\d?xs|sm|md|lg|\d?xl|xxl|full|screen|auto|px|none|flex|inline|block|grid|table|center|start|end|between
            |around|evenly|stretch|baseline|wrap|nowrap|reverse|inherit|current|transparent|white|black|bold|semibold|medium|normal|light|thin
            |extrabold|hidden|visible|scroll|clip|pointer|default|justify|middle|cover|contain|square|video|words|all|ellipsis|uppercase
            |lowercase|primary|secondary|success|danger|warning|info|light|dark|muted|\[[^\]]+\]
            |(?:slate|gray|grey|zinc|neutral|stone|red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose)-\d{2,3}))+
          |flex|grid|block|inline|inline-block|inline-flex|hidden|contents|relative|absolute|fixed|sticky|static|truncate|sr-only|visible
          |invisible|grow|shrink|italic|underline|uppercase|lowercase|capitalize
        )$
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex UtilityClass();

    // Ids web frameworks number at runtime: React's useId (":r1a:", "«r1»", "_r_b3r_"; Discord uses
    // the last), also inside library ids like "radix-:r2:"; MUI, jQuery UI, Ember, Ext JS, React Aria
    // and Angular Material counters ("mui-1234", "ui-id-3", "ember412", "mat-input-0").
    // (Colon ids from JSF, like "form:results:table", are stable: React's need a "-" or nothing around them.)
    // Select2 numbers its inputs too ("s2id_autogen7", "select2-city-result-4x7k-12").
    [GeneratedRegex(@"(?:^|-)[:«]r[0-9a-z]{1,8}[:»](?:$|-)|(?:^|-)_r_[0-9a-z]{1,8}_(?:$|-)|^(?:mui|ui-id|ember|ext-gen|ext-comp|react-aria|mat-[a-z-]+?|cdk-[a-z-]+?)-?\d+(?:-\d+)?$|^s2id_(?:autogen)?\d+|^select2-.*-\d+$")]
    private static partial Regex FrameworkGeneratedId();

    // WinForms/WPF designer defaults: "textBox17", "Panel_42", "button3".
    [GeneratedRegex(@"^(?:textBox|button|label|panel|comboBox|checkBox|listBox|groupBox|radioButton|dataGridView|tabPage)_?\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DesignerDefaultName();

    [GeneratedRegex(@"^(?<head>.*?\S)\s*[\(\[]?\d+[\)\]]?$")]
    private static partial Regex TrailingCounter();

    // Clock times (11:21, 11:21 PM, 23:05:59), numeric dates (05/11/2026, 2026-11-05, 5.11.2026),
    // and the words chat apps put around them ("Today at", "Yesterday at").
    [GeneratedRegex(@"(?:\b(?:Today|Yesterday|Tomorrow)(?:\s+at)?\s+)?\b\d{1,2}:\d{2}(?::\d{2})?(?:\s?[AaPp]\.?[Mm]\.?)?\b|\b\d{1,4}[./-]\d{1,2}[./-]\d{1,4}\b")]
    private static partial Regex TimeOrDate();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpaces();
}

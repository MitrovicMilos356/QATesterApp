using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace UiInspector.App;

/// <summary>
/// Shows code in a read-only <see cref="RichTextBox"/> with light syntax colouring: keywords,
/// strings, comments and numbers of Python, C#, Java and JavaScript. The colours are theme brushes,
/// so they follow light and dark mode. <c>local:CodeView.Text="{Binding Code}"</c>.
/// </summary>
public static partial class CodeView
{
    // Long suites stay readable without colouring; colouring them would make selecting a case slow.
    private const int MaxColoredLength = 60_000;

    private static readonly HashSet<string> Keywords =
    [
        // Python
        "def", "class", "import", "from", "as", "return", "if", "elif", "else", "for", "while", "in", "not", "and", "or", "is",
        "None", "True", "False", "with", "try", "except", "finally", "raise", "assert", "lambda", "yield", "pass", "async", "await",
        "global", "nonlocal", "break", "continue",
        // C#, Java, JavaScript
        "using", "namespace", "public", "private", "protected", "internal", "static", "readonly", "const", "var", "let", "new",
        "void", "string", "int", "bool", "double", "long", "object", "true", "false", "null", "this", "throw", "catch", "foreach",
        "do", "switch", "case", "default", "function", "package", "extends", "implements", "interface", "record", "sealed",
        "override", "virtual", "abstract", "final", "typeof", "instanceof", "export", "require", "of", "out", "ref", "params",
    ];

    // Earliest match wins, so a "//" inside a string stays part of the string.
    [GeneratedRegex("""(?<comment>(?<![:\w])//[^\n]*|(?<=^|\s)#(?![!\w])[^\n]*|/\*[\s\S]*?\*/)|(?<string>@?"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|`[^`]*`)|(?<number>\b\d+(?:\.\d+)?\b)|(?<word>\b[A-Za-z_]\w*\b)""", RegexOptions.Multiline)]
    private static partial Regex Tokens();

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(CodeView), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box) return;
        // A "\r" and a "\n" in separate runs would each break the line.
        var text = ((string?)e.NewValue ?? "").ReplaceLineEndings("\n");
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        if (text.Length > MaxColoredLength)
        {
            paragraph.Inlines.Add(new Run(text));
        }
        else
        {
            var at = 0;
            foreach (Match m in Tokens().Matches(text))
            {
                var key = m.Groups["comment"].Success ? "TextFillColorTertiaryBrush"
                    : m.Groups["string"].Success ? "SystemFillColorSuccessBrush"
                    : m.Groups["number"].Success ? "SystemFillColorCautionBrush"
                    : Keywords.Contains(m.Value) ? "AccentTextFillColorPrimaryBrush"
                    : null;
                if (key is null) continue;
                if (m.Index > at) paragraph.Inlines.Add(new Run(text[at..m.Index]));
                var run = new Run(m.Value);
                run.SetResourceReference(TextElement.ForegroundProperty, key);
                paragraph.Inlines.Add(run);
                at = m.Index + m.Length;
            }
            if (at < text.Length) paragraph.Inlines.Add(new Run(text[at..]));
        }
        // As wide as the longest line, so long lines scroll sideways instead of wrapping.
        var longest = text.Length == 0 ? 0 : text.Split('\n').Max(l => l.Length);
        // Refill the box's own document: a replaced one isn't seen by UI Automation (screen readers
        // and test scripts kept reading the first, empty one). It doesn't take the box's font by itself.
        var document = box.Document;
        document.Blocks.Clear();
        document.Blocks.Add(paragraph);
        document.PageWidth = Math.Max(300, longest * box.FontSize * 0.62 + 40);
        document.PagePadding = new Thickness(2, 0, 2, 0);
        document.FontFamily = box.FontFamily;
        document.FontSize = box.FontSize;
    }
}

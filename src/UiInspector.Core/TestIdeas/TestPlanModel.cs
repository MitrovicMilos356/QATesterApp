using UiInspector.Core.Selectors;

namespace UiInspector.Core.TestIdeas;

/// <summary>What a test case checks.</summary>
public enum TestCategory
{
    /// <summary>It works with good input (the "happy path").</summary>
    Positive,

    /// <summary>Bad input is refused, with a clear message.</summary>
    Negative,

    /// <summary>Limits: empty, the longest allowed, one past it, the first and last page.</summary>
    Boundary,

    /// <summary>Script and SQL in fields, passwords kept hidden, error messages that give nothing away.</summary>
    Security,

    /// <summary>Keyboard only, screen reader, labels, zoom.</summary>
    Accessibility,

    /// <summary>Messages, layout, state kept after refresh or Back.</summary>
    Usability,
}

public enum TestPriority
{
    High,
    Medium,
    Low,
}

/// <summary>One test case: what to do, with which data, and what should happen.</summary>
public sealed record TestCase
{
    /// <summary>Short id for test management tools, e.g. "LOGIN-07".</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required TestCategory Category { get; init; }

    public required TestPriority Priority { get; init; }

    /// <summary>Steps in plain words, naming the controls found on the page.</summary>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>The data to use, as shown: exact text (<c>!@#$%^&amp;*()</c>) or a description ("minimum length − 1").</summary>
    public string? Data { get; init; }

    /// <summary>
    /// The exact text to type, ready to copy: the 1,000 characters themselves, the spaces without the
    /// quotes shown around them. Null when the tester has to choose it (e.g. their own minimum length).
    /// </summary>
    public string? Value { get; init; }

    /// <summary>What should happen. Where only the tester can know (the exact message), it says so.</summary>
    public required string Expected { get; init; }

    /// <summary>What it means if the expected result doesn't happen: the risk, and how serious it is. Mostly for security cases.</summary>
    public string? IfItFails { get; init; }

    /// <summary>The control the case is about, to show it in the app.</summary>
    public FeatureControl? Target { get; init; }

    /// <summary>Worth automating: the feature's script runs it by changing the data.</summary>
    public bool Automatable { get; init; }

    /// <summary>A data-table case: <see cref="Value"/> goes into <see cref="Target"/>, the other fields keep valid data.</summary>
    public bool IsFieldData { get; init; }

    /// <summary>Every field is left empty ("send with everything empty").</summary>
    public bool EmptiesEverything { get; init; }
}

public enum FeatureKind
{
    Login,
    Registration,
    Search,
    Form,
    Filters,
    List,
    Paging,
    Tabs,
    Menu,
    Navigation,
    Dialog,
    Page,
}

/// <summary>What a field is for, judged by its type and label. It decides which test data fits.</summary>
public enum FieldKind
{
    Text,
    Email,
    Password,
    Phone,
    Number,
    Date,
    Url,
    PersonName,
    MultiLine,
    Search,
    Choice,
    CheckBox,
    Radio,
}

/// <summary>A control the feature uses, with how tests find it.</summary>
public sealed record FeatureControl(IReadOnlyList<ElementInfo> Path, SelectorResult? Result, Testability.TestabilityGrade? Grade)
{
    public ElementInfo Element => Path[^1];

    public Selector? Selector => Result?.Best;

    /// <summary>Its Playwright and Selenium locators, for controls in a web page (built when the page was read).</summary>
    public Web.WebResult? Web { get; init; }
}

public sealed record FeatureField(FeatureControl Control, FieldKind Kind, string Label);

/// <summary>A feature of the page or window, and the test cases suggested for it.</summary>
public sealed record TestFeature
{
    public required FeatureKind Kind { get; init; }

    /// <summary>"Login", "Search “Pretraži oglase”", "Filters (12)".</summary>
    public required string Title { get; init; }

    /// <summary>Where it is, e.g. "in the main content".</summary>
    public string Where { get; init; } = "";

    public IReadOnlyList<FeatureField> Fields { get; init; } = [];

    /// <summary>The button that submits or starts it, if there is one.</summary>
    public FeatureControl? Action { get; init; }

    /// <summary>Other controls it's made of (filter options, list items, tabs, links).</summary>
    public IReadOnlyList<FeatureControl> Controls { get; init; } = [];

    /// <summary>How many controls of this kind there are in all (lists show only a few).</summary>
    public int ControlCount { get; init; }

    public IReadOnlyList<TestCase> Cases { get; init; } = [];

    /// <summary>The feature's main flow as a script in each language; test data goes in where it types.</summary>
    public IReadOnlyList<CodeSnippet> Scripts { get; init; } = [];

    /// <summary>
    /// Test suites that run every automatable case with its own data and check automatically what
    /// can be checked (no alert, no database or crash text, no error page), with a screenshot each.
    /// </summary>
    public IReadOnlyList<CodeSnippet> Suites { get; init; } = [];

    /// <summary>Controls that tests will find unreliably, from the testability analysis.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Rough importance for ordering: logins and forms before menus and tabs.</summary>
    public int Weight { get; init; }
}

/// <summary>Test suggestions for one window or page.</summary>
public sealed record TestPlan
{
    public required ElementInfo Window { get; init; }

    public required IReadOnlyList<TestFeature> Features { get; init; }

    public bool IsWeb { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public int CaseCount => Features.Sum(f => f.Cases.Count);
}

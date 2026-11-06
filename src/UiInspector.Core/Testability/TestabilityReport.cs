using UiInspector.Core.Reporting;
using UiInspector.Core.Selectors;

namespace UiInspector.Core.Testability;

/// <summary>How well a test can find an element, judged by the best selector the generator can build for it.</summary>
public enum TestabilityGrade
{
    /// <summary>A stable, unique id: tests find it whatever the language, data or layout.</summary>
    Good,

    /// <summary>Found reliably, but by something that can change: its text, text near it, its type.</summary>
    Fair,

    /// <summary>Only its position tells it apart, or no selector works at all.</summary>
    Fragile,
}

/// <summary>Kinds of problems, for the report's summary.</summary>
[Flags]
public enum TestabilityProblem
{
    None = 0,

    /// <summary>No selector works at all.</summary>
    NotFound = 1,

    /// <summary>Only its position tells it apart.</summary>
    Position = 2,

    /// <summary>Found by its own text (language, wording).</summary>
    OwnText = 4,

    /// <summary>Found by text near it (row data, headings).</summary>
    NearbyText = 8,

    /// <summary>Found only by its control type or class.</summary>
    TypeOnly = 16,

    /// <summary>Has an id, but one that changes (generated, hashed, a GUID).</summary>
    ChangingId = 32,

    /// <summary>Its id is shared with other controls of its kind.</summary>
    DuplicateId = 64,

    /// <summary>No accessible name.</summary>
    MissingName = 128,
}

/// <summary>One control of the analysed window.</summary>
public sealed record TestabilityFinding
{
    /// <summary>1-based, matching the numbers on the report's screenshot.</summary>
    public required int Number { get; init; }

    /// <summary>Desktop first, the element last.</summary>
    public required IReadOnlyList<ElementInfo> Path { get; init; }

    public ElementInfo Element => Path[^1];

    public required TestabilityGrade Grade { get; init; }

    /// <summary>How tests find it now, e.g. "Name inside Pane #ordersPanel"; null if nothing works.</summary>
    public string? FoundBy { get; init; }

    /// <summary>What makes it hard to find, in plain words. Empty for a good element.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>What the developers can change, in the app's own technology; null when nothing needs to.</summary>
    public string? Fix { get; init; }

    public TestabilityProblem Problems { get; init; }

    /// <summary>Has no accessible name: screen readers can't announce it, and tests can't find it by its text.</summary>
    public bool MissingName => Problems.HasFlag(TestabilityProblem.MissingName);

    /// <summary>The developers should give it a (better) id.</summary>
    public bool NeedsId => Problems.HasFlag(TestabilityProblem.ChangingId) || Problems.HasFlag(TestabilityProblem.DuplicateId) || Grade != TestabilityGrade.Good;

    /// <summary>Other look-alike items next to it that weren't graded one by one (rows of a long list).</summary>
    public int SimilarNotListed { get; init; }

    /// <summary>Its nearest named section, e.g. "in “Oglasi za posao”"; empty when it has none.</summary>
    public string Where { get; init; } = "";

    public Selector? Selector { get; init; }

    /// <summary>Everything the generator found for it (alternatives, app path), for scripts built from it.</summary>
    public SelectorResult? Result { get; init; }
}

/// <summary>
/// Controls built the same way with the same problem (every job card's link, every row's Delete):
/// one entry and one fix in the report, instead of one per control.
/// </summary>
public sealed record TestabilityGroup
{
    public required IReadOnlyList<TestabilityFinding> Findings { get; init; }

    public TestabilityFinding First => Findings[0];

    public TestabilityGrade Grade => First.Grade;

    /// <summary>The controls in the group, counting look-alikes that weren't graded one by one.</summary>
    public int Count => Findings.Count + Findings.Sum(f => f.SimilarNotListed);

    /// <summary>"The “Save” button", or "24 links like “Senior .NET developer”, “Java developer”…".</summary>
    public required string Title { get; init; }

    /// <summary>Where they are, if they share a named section.</summary>
    public string Where { get; init; } = "";
}

/// <summary>A whole window graded for test automation.</summary>
public sealed record TestabilityReport
{
    public required ElementInfo Window { get; init; }

    public required IReadOnlyList<TestabilityFinding> Findings { get; init; }

    /// <summary>What to test on this window or page, and how, from the same read.</summary>
    public TestIdeas.TestPlan? Plan { get; init; }

    /// <summary>The findings grouped by what they have in common, most fragile and largest first.</summary>
    public IReadOnlyList<TestabilityGroup> Groups { get; init; } = [];

    /// <summary>Every element read from the window, interactive or not.</summary>
    public int ElementsScanned { get; init; }

    /// <summary>The window had more elements than are read at once; the rest wasn't analysed.</summary>
    public bool Truncated { get; init; }

    /// <summary>"WPF", "WinForm", "Chrome"…: the technologies the controls are built with, most used first.</summary>
    public IReadOnlyList<string> Frameworks { get; init; } = [];

    /// <summary>The window with every finding outlined and numbered (green, amber, red by grade).</summary>
    public StepScreenshot? Screenshot { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public TimeSpan Duration { get; init; }

    public int Count(TestabilityGrade grade) => Findings.Count(f => f.Grade == grade);

    /// <summary>0–100: good controls count fully, fair ones half.</summary>
    public int Score => Findings.Count == 0
        ? 100
        : (int)Math.Round(100.0 * (Count(TestabilityGrade.Good) + 0.5 * Count(TestabilityGrade.Fair)) / Findings.Count);
}

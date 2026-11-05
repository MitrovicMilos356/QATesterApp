namespace UiInspector.Core.Selectors;

public enum MatchKind
{
    Exact,

    /// <summary>The element's value contains this text (used for the stable part of a name or title).</summary>
    Contains,
}

/// <summary>UIA properties a selector can match on.</summary>
public enum SelectorProperty
{
    ControlType,
    AutomationId,
    Name,
    ClassName,
}

public sealed record SelectorCondition(SelectorProperty Property, string Value, MatchKind Match = MatchKind.Exact);

/// <summary>
/// One search step: find descendants of the previous step's element that meet all conditions.
/// <see cref="Index"/> (0-based) picks one of several matches and makes the selector fragile.
/// </summary>
public sealed record SelectorStep(IReadOnlyList<SelectorCondition> Conditions, int? Index = null)
{
    public string? ControlType => Conditions.FirstOrDefault(c => c.Property == SelectorProperty.ControlType)?.Value;

    public SelectorCondition? Get(SelectorProperty property) => Conditions.FirstOrDefault(c => c.Property == property);

    /// <summary>Readable form, e.g. <c>Hyperlink [Name ~ "Dyno (direct message)"] #3</c> (~ means "contains").</summary>
    public override string ToString()
    {
        var filters = Conditions
            .Where(c => c.Property != SelectorProperty.ControlType)
            .Select(c => $"{c.Property} {(c.Match == MatchKind.Contains ? "~" : "=")} \"{c.Value}\"");
        var text = ControlType ?? "*";
        var joined = string.Join(", ", filters);
        if (joined.Length > 0) text += $" [{joined}]";
        if (Index is { } index) text += $" #{index + 1}";
        return text;
    }
}

public enum VerificationStatus
{
    /// <summary>Exactly one element matches, and it's the selected one.</summary>
    Unique,

    /// <summary>Several elements match; code using it would act on the first.</summary>
    Ambiguous,

    /// <summary>Nothing matches (the element changed or disappeared).</summary>
    NotFound,

    /// <summary>Exactly one element matches, but it's a different one.</summary>
    WrongElement,

    /// <summary>Built from a snapshot after the element was gone, so it couldn't be checked.</summary>
    Unverified,
}

public sealed record SelectorVerification(VerificationStatus Status, int MatchCount)
{
    public string Describe() => Status switch
    {
        VerificationStatus.Unique => "Verified: matches exactly this element.",
        VerificationStatus.Ambiguous => $"Ambiguous: {MatchCount} elements match.",
        VerificationStatus.NotFound => "Broken: nothing matches anymore.",
        VerificationStatus.WrongElement => "Broken: it matches a different element.",
        VerificationStatus.Unverified => "Not verified: the element was gone before it could be checked.",
        _ => Status.ToString(),
    };
}

/// <summary>
/// A full selector: find the top-level window from the desktop, then each step in turn inside
/// the previous result. The last step is the target. With no steps, the window is the target.
/// </summary>
public sealed record Selector
{
    public required SelectorStep Window { get; init; }

    public required IReadOnlyList<SelectorStep> Steps { get; init; }

    /// <summary>Short human description, e.g. "AutomationId" or "Name inside anchor Pane #ordersPanel".</summary>
    public required string Strategy { get; init; }

    /// <summary>Higher is better; only meaningful for ranking selectors of the same element.</summary>
    public required int Score { get; init; }

    public required SelectorVerification Verification { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Whether a click at the element's center would actually reach it.</summary>
public sealed record OcclusionReport(bool IsCovered, ElementInfo? CoveringElement, string Message);

/// <summary>Everything the generator found for one element: the best selector, alternatives and checks.</summary>
public sealed record SelectorResult
{
    public required ElementInfo Target { get; init; }

    public Selector? Best { get; init; }

    public IReadOnlyList<Selector> Alternatives { get; init; } = [];

    public OcclusionReport? Occlusion { get; init; }

    /// <summary>Set when no selector could be built at all (e.g. the desktop itself was selected).</summary>
    public string? Problem { get; init; }

    /// <summary>Process that owns the target, for comments and launch hints in generated code.</summary>
    public string ProcessName { get; init; } = "";

    public string? ProcessPath { get; init; }

    /// <summary>Playwright/Selenium side for elements inside Chromium pages; null for native elements.</summary>
    public Web.WebResult? Web { get; init; }
}

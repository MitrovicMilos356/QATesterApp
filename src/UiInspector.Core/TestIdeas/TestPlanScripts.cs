using UiInspector.Core.Recording;
using UiInspector.Core.Selectors;
using UiInspector.Core.Web;

namespace UiInspector.Core.TestIdeas;

/// <summary>
/// A feature's main flow as a script, in every language the recorder writes: the steps are put
/// together as if they'd been recorded (type into each field, click the button), from the
/// selectors the analysis already verified. Running the other test cases means changing the
/// typed values, which the script's header says.
/// </summary>
internal static class TestPlanScripts
{
    private const int MaxTabsClicked = 3;

    /// <summary>A sensible value for the main flow; the test cases list the other values to try.</summary>
    internal static string SampleValue(FieldKind kind) => kind switch
    {
        FieldKind.Email => "tester@example.com",
        FieldKind.Phone => "+381 61 234 5678",
        FieldKind.Number => "5",
        FieldKind.Date => "01.01.2000",
        FieldKind.Url => "https://example.com",
        FieldKind.PersonName => "Ana",
        FieldKind.MultiLine => "Test message",
        FieldKind.Search => "Java",
        _ => "Test",
    };

    /// <param name="web">Builds Playwright/Selenium locators for a control in a web page; null for desktop apps.</param>
    public static IReadOnlyList<CodeSnippet> Build(TestFeature feature, Func<FeatureControl, WebResult?>? web)
    {
        var steps = new List<RecordedStep>();
        void Add(FeatureControl control, RecordedAction action, string? text = null, bool secret = false, KeyChord? key = null)
        {
            if (control.Result is not { Best: not null } result) return; // a look-alike that wasn't graded one by one
            steps.Add(new RecordedStep
            {
                Id = steps.Count + 1,
                Action = action,
                Path = control.Path,
                Text = text,
                IsSecret = secret,
                Key = key,
                Selector = result,
                Web = web?.Invoke(control),
            });
        }

        switch (feature.Kind)
        {
            case FeatureKind.Login or FeatureKind.Registration or FeatureKind.Form or FeatureKind.Search:
                foreach (var field in feature.Fields)
                {
                    switch (field.Kind)
                    {
                        case FieldKind.Password:
                            Add(field.Control, RecordedAction.SetValue, secret: true);
                            break;
                        case FieldKind.CheckBox:
                            Add(field.Control, RecordedAction.Click);
                            break;
                        case FieldKind.Choice or FieldKind.Radio:
                            break; // the option to pick depends on the app
                        default:
                            Add(field.Control, RecordedAction.SetValue, SampleValue(field.Kind));
                            break;
                    }
                }
                if (feature.Action is { } action) Add(action, RecordedAction.Click);
                else if (feature.Fields.LastOrDefault(f => f.Kind is not (FieldKind.CheckBox or FieldKind.Choice or FieldKind.Radio)) is { } last)
                    Add(last.Control, RecordedAction.PressKey, key: new KeyChord(0x0D));
                break;
            case FeatureKind.Tabs:
                foreach (var tab in feature.Controls.Take(MaxTabsClicked)) Add(tab, RecordedAction.Click);
                break;
            case FeatureKind.Paging:
                // "Next" if it has a selector, otherwise page 2, otherwise any page link that has one.
                var scriptable = feature.Controls.Where(c => c.Result?.Best is not null).ToList();
                var next = scriptable.FirstOrDefault(c => !FeatureDetector.Label(c.Element).All(char.IsDigit))
                           ?? scriptable.FirstOrDefault(c => FeatureDetector.Label(c.Element) == "2")
                           ?? scriptable.FirstOrDefault();
                if (next is not null) Add(next, RecordedAction.Click);
                break;
            case FeatureKind.Filters or FeatureKind.List or FeatureKind.Menu or FeatureKind.Navigation or FeatureKind.Dialog:
                if (feature.Controls.FirstOrDefault(c => c.Result?.Best is not null) is { } first) Add(first, RecordedAction.Click);
                break;
        }
        if (steps.Count == 0) return [];

        return RecordingScripts.Render(steps)
            .Select(s => s with { Code = Header(s.Id, feature) + s.Code.Replace("Recorded with UiInspector: ", "Steps: ") })
            .ToList();
    }

    private static string Header(string id, TestFeature feature)
    {
        var comment = id is "flaui" or "playwright-js" or "playwright-cs" or "selenium-java" ? "//" : "#";
        var dataDriven = feature.Fields.Count > 0
            ? $"{comment} To run the other test cases, change the typed values (see the test plan for each case's data).\n"
            : "";
        return $"{comment} Test plan: the main flow of “{feature.Title}”, put together from the page by UiInspector.\n{dataDriven}";
    }
}

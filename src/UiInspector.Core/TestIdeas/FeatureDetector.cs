using System.Text.RegularExpressions;
using UiInspector.Core.Selectors;
using UiInspector.Core.Testability;

namespace UiInspector.Core.TestIdeas;

/// <summary>
/// Finds the features of a page or window that are worth testing (a login, a search box, forms,
/// filters, long lists, paging, tabs, menus, navigation, dialogs) from its structure alone, and
/// sorts its fields by what they're for (email, password, phone…). It goes by control types,
/// labels in English and Serbian/Croatian (plus a few common words in German, French and Spanish)
/// and the page's landmarks; it doesn't understand the site, so it suggests, the tester decides.
/// </summary>
public static partial class FeatureDetector
{
    private const int MaxContainerClimb = 6;
    private const int MinListItems = 5;
    private const int MaxControlsKept = 20;

    private sealed record Item(IReadOnlyList<ElementInfo> Path, ElementNode Node, int Order)
    {
        public ElementInfo Info => Path[^1];
    }

    /// <param name="root">The page (in a browser) or the window to look at.</param>
    /// <param name="ancestors">Desktop … up to <paramref name="root"/>'s parent.</param>
    /// <param name="report">The testability analysis of the same read, for selectors and grades.</param>
    public static List<TestFeature> Detect(ElementNode root, IReadOnlyList<ElementInfo> ancestors, TestabilityReport report)
    {
        var items = new List<Item>();
        Flatten(root, [.. ancestors], items);
        var byId = report.Findings.ToDictionary(f => Key(f.Element), f => f);
        var nodes = items.ToDictionary(i => Key(i.Info), i => i);
        FeatureControl Control(Item item) =>
            byId.TryGetValue(Key(item.Info), out var f) ? new FeatureControl(item.Path, f.Result, f.Grade) : new FeatureControl(item.Path, null, null);

        var features = new List<TestFeature>();
        var used = new HashSet<string>();

        // ---- fields, gathered into the forms they belong to
        var fields = items.Where(i => IsField(i.Info)).ToList();
        // Text fields make a form; check boxes and options only join one. A field in no form is a form of its own.
        var forms = fields
            .Where(i => i.Info.ControlType is not ("CheckBox" or "RadioButton"))
            .Select(i => (Field: i, Container: Container(i, nodes)))
            .GroupBy(x => x.Container is { } c ? Key(c.Info) : "field:" + Key(x.Field.Info))
            .ToList();
        foreach (var form in forms)
        {
            var container = form.First().Container;
            var inForm = container is null ? form.Select(x => x.Field).ToList() : fields.Where(f => IsInside(f, container)).ToList();
            var featureFields = inForm.Select(f => new FeatureField(Control(f), Classify(f.Info), Label(f.Info))).ToList();
            var buttons = container is null ? [] : Descendants(container).Where(i => IsButton(i.Info)).ToList();
            foreach (var f in inForm) used.Add(Key(f.Info));

            var passwords = featureFields.Count(f => f.Kind == FieldKind.Password);
            var texts = featureFields.Count(f => f.Kind is not (FieldKind.CheckBox or FieldKind.Radio or FieldKind.Choice));
            var registration = passwords >= 2 || (passwords == 1 && (texts > 3 || buttons.Any(b => RegisterWords().IsMatch(Label(b.Info)))));
            // A section with a search box is a search, even with drop-downs next to it (city, technology).
            var kind = passwords > 0 ? (registration ? FeatureKind.Registration : FeatureKind.Login)
                : featureFields.Any(f => f.Kind == FieldKind.Search) ? FeatureKind.Search
                : FeatureKind.Form;
            if (kind == FeatureKind.Search)
            {
                if (texts == 1) featureFields = featureFields.Select(f => f.Kind == FieldKind.Text ? f with { Kind = FieldKind.Search } : f).ToList();
                // The search box first: the cases and the script are about it.
                featureFields = featureFields.OrderBy(f => f.Kind == FieldKind.Search ? 0 : 1).ToList();
            }

            var action = ChooseAction(kind, buttons, inForm);
            if (action is not null) used.Add(Key(action.Info));
            var where = TestabilityAnalyzer.Where(inForm[0].Path);
            // The button names it, so two logins on one screen can be told apart.
            var button = action is null ? "" : $" “{Label(action.Info)}”";
            var title = kind switch
            {
                FeatureKind.Login => "Login" + button,
                FeatureKind.Registration => "Registration (sign-up) form" + button,
                FeatureKind.Search => $"Search “{featureFields[0].Label}”",
                _ => FormTitle(container?.Node, featureFields),
            };
            features.Add(new TestFeature
            {
                Kind = kind,
                Title = title,
                Where = where,
                Fields = featureFields,
                Action = action is null ? null : Control(action),
                Weight = kind switch { FeatureKind.Login => 100, FeatureKind.Registration => 95, FeatureKind.Form => 85, _ => 80 },
            });
        }

        // ---- filters: check boxes, options and drop-downs outside forms, and buttons with counts ("Programiranje [77]")
        var filters = items.Where(i => !used.Contains(Key(i.Info)) && (i.Info.ControlType is "CheckBox" or "RadioButton" or "ComboBox"
                                                                    || (IsButton(i.Info) && CountInName().IsMatch(TestabilityAnalyzer.Visible(i.Info.Name))))).ToList();
        if (filters.Count >= 2)
        {
            foreach (var f in filters) used.Add(Key(f.Info));
            features.Add(new TestFeature
            {
                Kind = FeatureKind.Filters,
                Title = $"Filters ({filters.Count})",
                Where = CommonWhere(filters),
                Controls = filters.Take(MaxControlsKept).Select(Control).ToList(),
                ControlCount = filters.Count,
                Weight = 70,
            });
        }

        // ---- paging: numbered page links, Next / Previous
        var paging = items.Where(i => !used.Contains(Key(i.Info)) && i.Info.ControlType is "Hyperlink" or "Button"
                                      && PageWords().IsMatch(TestabilityAnalyzer.Visible(i.Info.Name))).ToList();
        if (paging.Count(p => NextWords().IsMatch(TestabilityAnalyzer.Visible(p.Info.Name))) > 0 || paging.Count >= 2)
        {
            foreach (var p in paging) used.Add(Key(p.Info));
            features.Add(new TestFeature
            {
                Kind = FeatureKind.Paging,
                Title = "Paging",
                Where = CommonWhere(paging),
                Controls = paging.Take(MaxControlsKept).Select(Control).ToList(),
                ControlCount = paging.Count,
                Weight = 50,
            });
        }

        // ---- navigation: the links of navigation landmarks
        var navigation = items.Where(i => !used.Contains(Key(i.Info)) && i.Info.ControlType == "Hyperlink"
                                          && i.Path.Any(a => a.AriaRole.Equals("navigation", StringComparison.OrdinalIgnoreCase))).ToList();
        if (navigation.Count >= 3)
        {
            foreach (var n in navigation) used.Add(Key(n.Info));
            features.Add(new TestFeature
            {
                Kind = FeatureKind.Navigation,
                Title = $"Navigation ({navigation.Count} links)",
                Where = "in the navigation",
                Controls = navigation.Take(MaxControlsKept).Select(Control).ToList(),
                ControlCount = navigation.Count,
                Weight = 30,
            });
        }

        // ---- long lists: the testability groups of many look-alike links, items or rows. A real list's
        //      items have names of their own (job titles); a group of mostly repeated or very short
        //      names ("Save", "2.8") is page furniture that happens to look alike.
        foreach (var group in report.Groups
                     .Where(g => g.Count >= MinListItems && g.First.Element.ControlType is "Hyperlink" or "ListItem" or "DataItem" or "TreeItem")
                     .Where(g => !g.Findings.All(f => used.Contains(Key(f.Element))))
                     .Where(IsRealList)
                     .OrderByDescending(g => g.Count)
                     .Take(2))
        {
            features.Add(new TestFeature
            {
                Kind = FeatureKind.List,
                Title = $"List: {group.Title}",
                Where = group.Where,
                Controls = group.Findings.Take(MaxControlsKept).Select(f => new FeatureControl(f.Path, f.Result, f.Grade)).ToList(),
                ControlCount = group.Count,
                Weight = 60,
            });
        }

        // ---- tabs, menus and drop-downs, dialogs
        foreach (var tabs in items.Where(i => i.Info.ControlType == "TabItem").GroupBy(i => Key(i.Path[^2])).Where(g => g.Count() >= 2).Take(2))
        {
            var list = tabs.ToList();
            features.Add(new TestFeature
            {
                Kind = FeatureKind.Tabs,
                Title = $"Tabs ({list.Count}: {Examples(list)})",
                Where = TestabilityAnalyzer.Where(list[0].Path),
                Controls = list.Take(MaxControlsKept).Select(Control).ToList(),
                ControlCount = list.Count,
                Weight = 40,
            });
        }

        var menus = items.Where(i => !used.Contains(Key(i.Info)) && (i.Info.ControlType == "MenuItem" && !i.Info.IsOffscreen
                                                                  || IsButton(i.Info) && i.Info.Patterns.Contains("ExpandCollapse"))).ToList();
        if (menus.Count > 0)
        {
            features.Add(new TestFeature
            {
                Kind = FeatureKind.Menu,
                Title = $"Menus and drop-downs ({menus.Count})",
                Where = CommonWhere(menus),
                Controls = menus.Take(MaxControlsKept).Select(Control).ToList(),
                ControlCount = menus.Count,
                Weight = 35,
            });
        }

        foreach (var dialog in items.Where(i => i.Path.Count > ancestors.Count + 1
                                                && (i.Info.ControlType == "Window" || i.Info.AriaRole is "dialog" or "alertdialog")).Take(2))
        {
            var name = TestabilityAnalyzer.Visible(dialog.Info.Name);
            features.Add(new TestFeature
            {
                Kind = FeatureKind.Dialog,
                Title = name.Length > 0 ? $"Dialog “{name}”" : "Dialog",
                Controls = Descendants(dialog).Where(d => IsButton(d.Info)).Take(MaxControlsKept).Select(Control).ToList(),
                Weight = 45,
            });
        }

        features.Add(new TestFeature
        {
            Kind = FeatureKind.Page,
            Title = TestabilityAnalyzer.IsPage(root.Info) ? "The whole page" : "The whole window",
            Weight = 10,
        });

        // Most important first; of two logins, the one with a button (a real form) before a lone field.
        return features.OrderByDescending(f => f.Weight).ThenByDescending(f => f.Action is not null).ToList();
    }

    // ---------------------------------------------------------------- fields

    private static readonly HashSet<string> FieldTypes = ["Edit", "ComboBox", "CheckBox", "RadioButton", "Spinner"];

    private static bool IsField(ElementInfo e) =>
        FieldTypes.Contains(e.ControlType)
        || e.ControlType == "Document" && !TestabilityAnalyzer.IsPage(e) && e.Patterns.Contains("Value") && e.IsKeyboardFocusable;

    private static bool IsButton(ElementInfo e) => e.ControlType is "Button" or "SplitButton";

    /// <summary>What the field is for: its type first (a password box is a password box), then words in its label.</summary>
    internal static FieldKind Classify(ElementInfo e)
    {
        if (e.IsPassword) return FieldKind.Password;
        switch (e.ControlType)
        {
            case "CheckBox": return FieldKind.CheckBox;
            case "RadioButton": return FieldKind.Radio;
            case "Spinner": return FieldKind.Number;
            case "Document": return FieldKind.MultiLine;
            case "ComboBox" when !e.Patterns.Contains("Value"): return FieldKind.Choice;
        }
        var label = $"{e.Name} {e.HelpText} {e.AutomationId}".ToLowerInvariant();
        if (e.AriaRole is "searchbox" || SearchWords().IsMatch(label)) return FieldKind.Search;
        if (PasswordWords().IsMatch(label)) return FieldKind.Password;
        if (EmailWords().IsMatch(label)) return FieldKind.Email;
        if (PhoneWords().IsMatch(label)) return FieldKind.Phone;
        if (DateWords().IsMatch(label)) return FieldKind.Date;
        if (UrlWords().IsMatch(label)) return FieldKind.Url;
        if (NumberWords().IsMatch(label)) return FieldKind.Number;
        if (NameWords().IsMatch(label)) return FieldKind.PersonName;
        if (MultiLineWords().IsMatch(label)) return FieldKind.MultiLine;
        return e.ControlType == "ComboBox" ? FieldKind.Choice : FieldKind.Text;
    }

    /// <summary>What a tester would call the control: its visible name, else its description or a usable id.</summary>
    public static string Label(ElementInfo e)
    {
        var name = TestabilityAnalyzer.Visible(e.Name);
        if (name.Length == 0) name = TestabilityAnalyzer.Visible(e.HelpText);
        // A made-up id ("s2id_autogen7") says nothing to a tester.
        if (name.Length == 0 && AttributeStability.AnalyzeAutomationId(e.AutomationId, e.FrameworkId, e.NativeWindowHandle).IsUsable) name = e.AutomationId;
        if (name.Length == 0) name = e.ControlType == "Edit" ? "unlabelled field" : e.ControlType.ToLowerInvariant();
        return name.Length > 50 ? name[..49] + "…" : name;
    }

    // The form a field belongs to: the nearest section above it that also holds a button (or is a
    // form landmark). Fields with no such section share none, and each is a form of its own.
    private static Item? Container(Item field, Dictionary<string, Item> nodes)
    {
        for (var i = field.Path.Count - 2; i >= Math.Max(2, field.Path.Count - 1 - MaxContainerClimb); i--)
        {
            var ancestor = field.Path[i];
            if (TestabilityAnalyzer.IsPage(ancestor) || ancestor.ControlType == "Window") break;
            if (!nodes.TryGetValue(Key(ancestor), out var item)) continue;
            if (ancestor.AriaRole.Equals("form", StringComparison.OrdinalIgnoreCase) || Descendants(item).Any(d => IsButton(d.Info))) return item;
        }
        return null;
    }

    private static bool IsInside(Item item, Item container) => item.Path.Any(a => a.IsSameElement(container.Info));

    // With full paths (desktop first), so the controls found can be scripted.
    private static IEnumerable<Item> Descendants(Item item) => Descendants(item.Node, item.Path);

    private static IEnumerable<Item> Descendants(ElementNode node, IReadOnlyList<ElementInfo> path)
    {
        foreach (var child in node.Children)
        {
            var childPath = path.Append(child.Info).ToList();
            yield return new Item(childPath, child, 0);
            foreach (var d in Descendants(child, childPath)) yield return d;
        }
    }

    private static Item? ChooseAction(FeatureKind kind, List<Item> buttons, List<Item> fields)
    {
        if (buttons.Count == 0) return null;
        var words = kind switch
        {
            FeatureKind.Login => LoginWords(),
            FeatureKind.Registration => RegisterWords(),
            FeatureKind.Search => SearchWords(),
            _ => SubmitWords(),
        };
        return buttons.FirstOrDefault(b => words.IsMatch(Label(b.Info).ToLowerInvariant()))
               ?? buttons.FirstOrDefault(b => SubmitWords().IsMatch(Label(b.Info).ToLowerInvariant()))
               ?? (buttons.Count == 1 ? buttons[0] : null);
    }

    private static string FormTitle(ElementNode? container, List<FeatureField> fields)
    {
        var name = container is null ? "" : TestabilityAnalyzer.Visible(container.Info.Name);
        var count = $"{fields.Count} field{(fields.Count == 1 ? "" : "s")}";
        if (name.Length is > 0 and <= 50) return $"Form “{name}” ({count})";
        return $"Form with “{fields[0].Label}”{(fields.Count > 1 ? $" and {fields.Count - 1} more" : "")}";
    }

    private static bool IsRealList(TestabilityGroup group)
    {
        // Link lists are cards, each in a section of its own, so they're graded one by one. Links that
        // mostly sit side by side (and so were only counted) are a toolbar's or a card's own links.
        if (group.First.Element.ControlType == "Hyperlink" && group.Findings.Sum(f => f.SimilarNotListed) > group.Findings.Count) return false;
        var names = group.Findings.Select(f => TestabilityAnalyzer.Visible(f.Element.Name)).ToList();
        var distinct = names.Distinct().Count();
        return distinct >= 0.8 * names.Count && names.Count(n => n.Length >= 6) >= 0.6 * names.Count;
    }

    private static string Examples(List<Item> items) =>
        string.Join(", ", items.Take(3).Select(i => $"“{Label(i.Info)}”")) + (items.Count > 3 ? "…" : "");

    private static string CommonWhere(List<Item> items)
    {
        var places = items.Select(i => TestabilityAnalyzer.Where(i.Path)).Distinct().ToList();
        return places.Count == 1 ? places[0] : "";
    }

    private static void Flatten(ElementNode node, List<ElementInfo> path, List<Item> into)
    {
        var here = new List<ElementInfo>(path) { node.Info };
        into.Add(new Item(here, node, into.Count));
        foreach (var child in node.Children) Flatten(child, here, into);
    }

    private static string Key(ElementInfo e) => string.Join('.', e.RuntimeId);

    // ---------------------------------------------------------------- words in labels

    [GeneratedRegex(@"search|pretra[zž]|tra[zž]i|find|query|suche|recherche|buscar|поиск|претра")]
    private static partial Regex SearchWords();

    [GeneratedRegex(@"password|lozink|šifr|sifr|passwort|mot de passe|contraseña|pin\b")]
    private static partial Regex PasswordWords();

    [GeneratedRegex(@"e-?mail|mejl|e-?po[šs]t|correo|courriel|адрес")]
    private static partial Regex EmailWords();

    [GeneratedRegex(@"phone|tel\b|telefon|mobil|cell|gsm|број|broj tel")]
    private static partial Regex PhoneWords();

    [GeneratedRegex(@"date|datum|birth|ro[đd]en|fecha|geburt|naissance")]
    private static partial Regex DateWords();

    [GeneratedRegex(@"\burl\b|website|web ?site|sajt|homepage|link\b")]
    private static partial Regex UrlWords();

    [GeneratedRegex(@"amount|quantity|qty|\bage\b|number|\bbroj|koli[čc]in|iznos|price|cena|cijena|zip|postal|po[šs]tanski|godin|year|salary|plata")]
    private static partial Regex NumberWords();

    [GeneratedRegex(@"name|\bime\b|prezime|nom\b|nombre|vorname|nachname")]
    private static partial Regex NameWords();

    [GeneratedRegex(@"message|comment|description|poruk|komentar|opis|note|napomen|bio\b|about")]
    private static partial Regex MultiLineWords();

    [GeneratedRegex(@"log ?in|sign ?in|prijav|uloguj|anmeld|connexion|iniciar|entrar|войти")]
    private static partial Regex LoginWords();

    [GeneratedRegex(@"regist|sign ?up|create account|napravi nalog|kreiraj|otvori nalog|join")]
    private static partial Regex RegisterWords();

    [GeneratedRegex(@"submit|send|po[šs]alji|save|sa[čc]uvaj|continue|nastavi|next|dalje|\bok\b|confirm|potvrdi|apply|primeni|order|naru[čc]i|subscribe|prijavi se|book|rezervi")]
    private static partial Regex SubmitWords();

    // "Programiranje [77]", "Java (12)", "Remote 4"
    [GeneratedRegex(@"[\[(]\s*\d+\s*[\])]\s*$")]
    private static partial Regex CountInName();

    [GeneratedRegex(@"^(\d{1,3}|next|previous|prev|older|newer|first|last|slede[ćc]a|sledeca|slede[ćc]e|prethodna|prva|poslednja|dalje|nazad|weiter|zurück|suivant|précédent|siguiente|anterior|›|»|‹|«|>|<|>>|<<)$", RegexOptions.IgnoreCase)]
    private static partial Regex PageWords();

    [GeneratedRegex(@"^(next|slede[ćc]a|sledeca|dalje|weiter|suivant|siguiente|›|»|>|>>)$", RegexOptions.IgnoreCase)]
    private static partial Regex NextWords();
}

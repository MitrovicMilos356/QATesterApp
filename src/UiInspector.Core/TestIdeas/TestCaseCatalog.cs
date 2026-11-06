using UiInspector.Core.Reporting;
using UiInspector.Core.Testability;

namespace UiInspector.Core.TestIdeas;

/// <summary>
/// The test cases suggested for each kind of feature and field: the happy path, then everything
/// a tester would otherwise have to think up (empty, too long, only symbols, accents, emoji,
/// script and SQL, keyboard only…), with the exact data, what should happen, and for security
/// cases what it means if it doesn't. Steps name the controls found on the page. Where only the
/// tester knows the rule (the minimum password length, the exact message), the case says so
/// instead of guessing.
/// </summary>
public static class TestCaseCatalog
{
    private const string AllSymbols = "!@#$%^&*()_+-=[]{};':\",./<>?\\|`~";
    private const string Script = "<script>alert('x')</script>";

    // Runs where a <script> tag wouldn't: pages that insert text with innerHTML (DOM-based XSS).
    private const string ImgScript = "<img src=x onerror=alert('x')>";
    private const string Sql = "' OR '1'='1' --";
    private const string Accents = "ČćŠšĐđŽž Ђорђе";
    private const string Emoji = "😀🔥👍";

    // Long values, typed out so they can be copied as they are.
    private static readonly string A1000 = new('a', 1000);
    private static readonly string A10000 = new('a', 10_000);
    private static readonly string Email254 = new string('a', 64) + "@" + string.Concat(Enumerable.Repeat(new string('b', 63) + ".", 2)) + new string('b', 57) + ".com";
    private static readonly string Email260 = new string('a', 64) + "@" + string.Concat(Enumerable.Repeat(new string('b', 63) + ".", 3)) + "com";
    private static readonly string Url2100 = "https://example.com/" + new string('a', 2080);

    // ---------------------------------------------------------------- what a failure means

    private const string XssRisk =
        "If an alert pops up (or the text vanishes and something else happens), the page runs code that users type: cross-site scripting (XSS). " +
        "The alert itself is harmless; it proves that an attacker's code would run too. Put in a link or a saved field, it runs in other people's " +
        "browsers while they're logged in, and can take over their session, act as them, or show a fake login form. Severity: high, critical if " +
        "the text is saved and shown to others. Report it with the exact input and the page where it ran.";

    private const string SqlRisk =
        "If you see a database error (words like SQL, syntax, ODBC, query), more results than there should be, or you get in without the right " +
        "password, the input reaches the database unchecked: SQL injection. An attacker could read, change or delete the data, or log in as " +
        "anyone. Severity: critical. Report the input and what you saw; don't try further.";

    private const string LinkRisk =
        "If saving it makes a link that shows the alert when clicked, anyone who clicks it runs code chosen by whoever typed it (XSS). Severity: high.";

    private const string Silent = "If it saves a different value than it shows, the data in the system is wrong without anyone noticing.";

    /// <summary>One line of a data table: a case that only differs from the others in its data.</summary>
    private sealed record Row(string Title, TestCategory Category, TestPriority Priority, string? Data, string Expected, bool Automatable = true)
    {
        /// <summary>The data is the text to type; false when it describes what to type ("minimum length − 1").</summary>
        public bool Exact { get; init; } = true;

        /// <summary>The text to type when it differs from what's shown (spaces without quotes, the long text itself).</summary>
        public string? Value { get; init; }

        public string? Risk { get; init; }

        public string? TypedValue => Value ?? (Exact ? Data : null);
    }

    public static IReadOnlyList<TestCase> For(TestFeature feature, bool isWeb)
    {
        var builder = new Builder(Prefix(feature.Kind))
        {
            DefaultTarget = feature.Action ?? feature.Fields.FirstOrDefault()?.Control ?? feature.Controls.FirstOrDefault(),
        };
        switch (feature.Kind)
        {
            case FeatureKind.Login: Login(builder, feature, isWeb); break;
            case FeatureKind.Registration: Registration(builder, feature, isWeb); break;
            case FeatureKind.Search: Search(builder, feature, isWeb); break;
            case FeatureKind.Form: Form(builder, feature, isWeb); break;
            case FeatureKind.Filters: Filters(builder, feature, isWeb); break;
            case FeatureKind.List: List(builder, feature, isWeb); break;
            case FeatureKind.Paging: Paging(builder, feature, isWeb); break;
            case FeatureKind.Tabs: Tabs(builder, feature); break;
            case FeatureKind.Menu: Menu(builder, feature); break;
            case FeatureKind.Navigation: Navigation(builder, feature, isWeb); break;
            case FeatureKind.Dialog: Dialog(builder, feature); break;
            case FeatureKind.Page: Page(builder, isWeb); break;
        }
        return builder.Cases;
    }

    private static string Prefix(FeatureKind kind) => kind switch
    {
        FeatureKind.Login => "LOGIN",
        FeatureKind.Registration => "SIGNUP",
        FeatureKind.Search => "SEARCH",
        FeatureKind.Form => "FORM",
        FeatureKind.Filters => "FILTER",
        FeatureKind.List => "LIST",
        FeatureKind.Paging => "PAGING",
        FeatureKind.Tabs => "TABS",
        FeatureKind.Menu => "MENU",
        FeatureKind.Navigation => "NAV",
        FeatureKind.Dialog => "DIALOG",
        _ => "PAGE",
    };

    private sealed class Builder(string prefix)
    {
        public List<TestCase> Cases { get; } = [];

        /// <summary>The control shown in the app for cases about the feature as a whole.</summary>
        public FeatureControl? DefaultTarget { get; init; }

        public void Add(string title, TestCategory category, TestPriority priority, IEnumerable<string> steps, string expected,
            string? data = null, bool automatable = false, string? value = null, string? risk = null, FeatureControl? target = null,
            bool fieldData = false, bool emptyAll = false) =>
            Cases.Add(new TestCase
            {
                Id = $"{prefix}-{Cases.Count + 1:00}",
                Title = title,
                Category = category,
                Priority = priority,
                Steps = steps.ToList(),
                Data = data,
                Value = value,
                Expected = expected,
                IfItFails = risk,
                Target = target ?? DefaultTarget,
                Automatable = automatable,
                IsFieldData = fieldData,
                EmptiesEverything = emptyAll,
            });
    }

    // ---------------------------------------------------------------- wording

    /// <summary>"the “Email” field", without icon glyphs.</summary>
    private static string The(FeatureControl control) => StepWording.Element(control.Element with { Name = TestabilityAnalyzer.Visible(control.Element.Name) });

    private static string The(FeatureField field) => The(field.Control);

    private static string Submit(TestFeature f) => f.Action is { } action ? $"Click {The(action)}." : "Press Enter (or click the button that sends it).";

    private static string FillOthers(TestFeature f, FeatureField except) =>
        f.Fields.Count > 1 ? $"Fill in the other fields ({string.Join(", ", f.Fields.Where(x => x != except).Select(x => $"“{x.Label}”"))}) with valid data." : "";

    private static IEnumerable<string> Steps(params string[] steps) => steps.Where(s => s.Length > 0);

    // ---------------------------------------------------------------- fields

    /// <summary>Data-table cases for one field, run through the feature (fill the rest, type this, submit).</summary>
    private static void FieldCases(Builder b, TestFeature f, FeatureField field, bool isWeb, IEnumerable<Row>? rows = null)
    {
        // Lists built from shared parts can repeat a case (free text has its own "very long text"): the first one counts.
        foreach (var row in (rows ?? Rows(field.Kind)).DistinctBy(r => r.Title))
        {
            var typed = row.Data is null ? $"Leave {The(field)} empty." : $"Type the data below into {The(field)}.";
            b.Add($"{field.Label}: {row.Title}", row.Category, row.Priority, Steps(FillOthers(f, field), typed, Submit(f)), row.Expected,
                row.Data, row.Automatable, row.TypedValue, row.Risk, field.Control, fieldData: true);
        }
        // What's the same for every field.
        b.Add($"{field.Label}: has a visible label that screen readers read", TestCategory.Accessibility, TestPriority.Medium,
            Steps($"Move to {The(field)} with Tab, with a screen reader (Narrator: Ctrl+Win+Enter) on."),
            "The label (not just a placeholder) is read out, together with the field's type and whether it's required.",
            target: field.Control, risk: "If nothing meaningful is read, people who use a screen reader can't tell what to type here.");
        if (f.Kind != FeatureKind.Login)
        {
            b.Add($"{field.Label}: error message is clear and next to the field", TestCategory.Usability, TestPriority.Medium,
                Steps($"Type something invalid into {The(field)}.", Submit(f)),
                "A message says what's wrong and how to fix it, shown next to the field (and read by a screen reader); what was typed isn't lost.",
                target: field.Control);
        }
    }

    private static IEnumerable<Row> Rows(FieldKind kind) => kind switch
    {
        FieldKind.Email => EmailRows,
        FieldKind.Password => PasswordRows,
        FieldKind.Phone => PhoneRows,
        FieldKind.Number => NumberRows,
        FieldKind.Date => DateRows,
        FieldKind.Url => UrlRows,
        FieldKind.PersonName => NameRows,
        FieldKind.MultiLine => MultiLineRows,
        FieldKind.Search => SearchRows,
        FieldKind.Choice => ChoiceRows,
        FieldKind.CheckBox or FieldKind.Radio => CheckRows,
        _ => TextRows,
    };

    // Everything typed into a text field, whatever it's for.
    private static readonly Row[] CommonTextRows =
    [
        new("only spaces", TestCategory.Negative, TestPriority.Medium, "\"   \" (3 spaces)", "Treated like an empty field: refused if required, never saved as blanks.") { Value = "   " },
        new("spaces before and after", TestCategory.Boundary, TestPriority.Low, "your valid value with 2 spaces before and after", "Accepted, and the spaces are trimmed (check what gets saved or shown).") { Exact = false, Risk = Silent },
        new("very long text", TestCategory.Boundary, TestPriority.Medium, "1,000 × the letter a", "Either the field stops at its limit, or a message says how long it may be. The page layout doesn't break.") { Value = A1000 },
        new("only symbols", TestCategory.Negative, TestPriority.Medium, AllSymbols, "Refused with a message if symbols aren't allowed; if they are, saved and shown exactly as typed."),
        new("accents and Cyrillic", TestCategory.Positive, TestPriority.Medium, Accents, "Accepted and shown exactly as typed (no ? or Ä characters).") { Risk = "If letters come back as ? or Ä, names and addresses get garbled for everyone who uses them." },
        new("emoji", TestCategory.Boundary, TestPriority.Low, Emoji, "Either accepted and shown correctly, or refused with a message. No error page, no broken characters."),
        new("HTML and script", TestCategory.Security, TestPriority.High, Script, "Shown as plain text wherever it appears later; no alert pops up and nothing runs.") { Risk = XssRisk },
        new("HTML with an event handler", TestCategory.Security, TestPriority.High, ImgScript, "Shown as plain text (a broken image or an alert means it was treated as HTML).") { Risk = XssRisk + " This form runs even where a <script> tag doesn't: in pages that insert text with innerHTML (DOM-based XSS)." },
        new("SQL injection", TestCategory.Security, TestPriority.High, Sql, "Treated as plain text: no error page, no database message, no other data shown.") { Risk = SqlRisk },
    ];

    private static readonly Row[] TextRows =
    [
        new("valid value", TestCategory.Positive, TestPriority.High, "a typical value for this field", "Accepted and saved; shown the same way later.") { Exact = false },
        new("left empty", TestCategory.Negative, TestPriority.High, null, "If it's required: a message says so and nothing is sent. If optional: accepted."),
        .. CommonTextRows,
        new("one character", TestCategory.Boundary, TestPriority.Low, "a", "Accepted, or refused with a message giving the minimum length."),
    ];

    private static readonly Row[] EmailRows =
    [
        new("valid address", TestCategory.Positive, TestPriority.High, "tester@example.com", "Accepted."),
        new("with plus and dots", TestCategory.Positive, TestPriority.Medium, "first.last+qa@mail.example.co.uk", "Accepted (these are valid addresses).") { Risk = "If refused, people with such addresses (common with Gmail's +labels) can't sign up." },
        new("upper case", TestCategory.Positive, TestPriority.Low, "Tester@Example.COM", "Accepted; treated as the same address as tester@example.com.") { Risk = "If treated as a different address, the same person can end up with two accounts, or can't log in." },
        new("left empty", TestCategory.Negative, TestPriority.High, null, "If required: a message says so and nothing is sent."),
        new("missing @", TestCategory.Negative, TestPriority.High, "tester.example.com", "Refused with a message that the address isn't valid."),
        new("two @", TestCategory.Negative, TestPriority.Medium, "tester@@example.com", "Refused."),
        new("missing domain", TestCategory.Negative, TestPriority.Medium, "tester@", "Refused."),
        new("missing name before @", TestCategory.Negative, TestPriority.Medium, "@example.com", "Refused."),
        new("missing top-level domain", TestCategory.Negative, TestPriority.Low, "tester@example", "Refused (or accepted only if the app allows local addresses)."),
        new("space inside", TestCategory.Negative, TestPriority.Medium, "tes ter@example.com", "Refused."),
        new("two dots in a row", TestCategory.Negative, TestPriority.Low, "tester..qa@example.com", "Refused."),
        new("spaces before and after", TestCategory.Boundary, TestPriority.Medium, "\"  tester@example.com  \" (with the spaces)", "Accepted with the spaces trimmed (not refused, not saved with spaces).") { Value = "  tester@example.com  ", Risk = "If saved with the spaces, the person may not be able to log in or get emails later." },
        new("longest allowed (254 characters)", TestCategory.Boundary, TestPriority.Low, "a 254-character address (64 a's, @, a long domain)", "Accepted.") { Value = Email254 },
        new("too long (260 characters)", TestCategory.Boundary, TestPriority.Low, "a 260-character address", "Refused with a message, or cut off at the limit.") { Value = Email260 },
        new("accented domain", TestCategory.Boundary, TestPriority.Low, "tester@exämple.com", "Accepted, or refused with a clear message (no error page)."),
        new("HTML and script", TestCategory.Security, TestPriority.High, "\"<script>alert(1)</script>\"@example.com", "Refused, or shown as plain text later; nothing runs.") { Risk = XssRisk },
        new("SQL injection", TestCategory.Security, TestPriority.High, "tester'--@example.com", "Refused or treated as text; no database error.") { Risk = SqlRisk },
    ];

    // How the password box itself behaves, wherever it is.
    private static readonly Row[] PasswordHandlingRows =
    [
        new("is hidden while typing", TestCategory.Security, TestPriority.High, "Valid#Pass2024", "Shown as dots, never in plain text (unless a show-password button is used).", false) { Risk = "If it shows in plain text, anyone nearby, a screenshot or a shared screen can see it. Severity: medium." },
        new("show/hide button, if there is one", TestCategory.Usability, TestPriority.Low, "Valid#Pass2024", "Shows the password on click and hides it again; the button has a name a screen reader reads.", false),
        new("can't be copied out", TestCategory.Security, TestPriority.Low, "Valid#Pass2024", "Selecting the dots and pressing Ctrl+C copies nothing (or only dots).", false) { Risk = "If it can be copied, anyone at an unlocked computer can take the password from the box. Severity: low." },
        new("can be pasted in", TestCategory.Usability, TestPriority.Low, "Valid#Pass2024 (paste it from the clipboard)", "Pasting with Ctrl+V works, so password managers can be used.", false) { Value = "Valid#Pass2024" },
        new("Caps Lock on", TestCategory.Usability, TestPriority.Low, "valid#pass2024 typed with Caps Lock on", "Ideally a Caps Lock warning; at least the same message as any wrong password.", false) { Value = "valid#pass2024" },
    ];

    // Creating a password (sign-up, change password): the rules apply here.
    private static readonly Row[] PasswordRows =
    [
        new("valid strong password", TestCategory.Positive, TestPriority.High, "Valid#Pass2024", "Accepted."),
        new("left empty", TestCategory.Negative, TestPriority.High, null, "Refused with a message; nothing is sent."),
        new("only symbols", TestCategory.Boundary, TestPriority.Medium, "!@#$%^&*()", "Behaves as the password rules say (often refused for missing letters or digits); the message names the rule."),
        new("only digits", TestCategory.Negative, TestPriority.Medium, "12345678", "Refused if the rules ask for letters, with a message naming the rule.") { Risk = "If accepted, accounts get passwords that are guessed in seconds." },
        new("only lower-case letters", TestCategory.Negative, TestPriority.Medium, "password", "Refused if the rules ask for capitals, digits or symbols."),
        new("only capitals", TestCategory.Negative, TestPriority.Low, "PASSWORD", "Refused if the rules ask for lower case, digits or symbols."),
        new("one character", TestCategory.Boundary, TestPriority.Medium, "a", "Refused with a message giving the minimum length."),
        new("one shorter than the minimum", TestCategory.Boundary, TestPriority.High, "minimum length − 1 (e.g. 7 characters if the minimum is 8)", "Refused with a message giving the minimum length.") { Exact = false },
        new("exactly the minimum length", TestCategory.Boundary, TestPriority.High, "minimum length (e.g. 8 characters: Abcd#123)", "Accepted.") { Exact = false },
        new("very long", TestCategory.Boundary, TestPriority.Medium, "1,000 × the letter a", "Accepted up to the limit, or refused with a message. No error page, no slowdown.") { Value = A1000, Risk = "If very long passwords make the server slow or crash, an attacker can use that to take the site down." },
        new("only spaces", TestCategory.Negative, TestPriority.Medium, "\"        \" (8 spaces)", "Refused.") { Value = "        " },
        new("spaces at start and end", TestCategory.Boundary, TestPriority.Medium, "\" Valid#Pass2024 \" (with the spaces)", "Either refused, or kept exactly (the same spaces needed to log in). Never trimmed silently on one screen only.") { Value = " Valid#Pass2024 ", Risk = "If sign-up trims and login doesn't (or the other way round), the person can't log in with the password they chose." },
        new("space inside", TestCategory.Positive, TestPriority.Low, "Valid Pass#2024", "Accepted, unless the rules forbid spaces (then a clear message)."),
        new("accents and Cyrillic", TestCategory.Boundary, TestPriority.Low, "Šifra#Ђорђе1", "Accepted and works for login afterwards, or refused with a clear message."),
        new("emoji", TestCategory.Boundary, TestPriority.Low, "Pass😀word1", "Accepted and works for login afterwards, or refused with a clear message. No error page."),
        new("common password", TestCategory.Security, TestPriority.Medium, "Password123", "Refused (or warned) as too common, if the app checks that.") { Risk = "If accepted, attackers who try the most common passwords get into those accounts first. Severity: medium (a policy decision)." },
        new("SQL injection", TestCategory.Security, TestPriority.High, Sql, "Saved as an ordinary password (or refused); no database error.") { Risk = SqlRisk },
        new("HTML and script", TestCategory.Security, TestPriority.Medium, Script, "Saved as an ordinary password (or refused); nothing runs.") { Risk = XssRisk },
        .. PasswordHandlingRows,
    ];

    // At login nothing is checked for format or strength: a value either opens the account or doesn't.
    private const string NotLoggedIn = "Not logged in; the same general message as for any wrong login; no error page.";

    private static readonly Row[] LoginUserRows =
    [
        new("left empty", TestCategory.Negative, TestPriority.High, null, "A message asks for it; nothing is sent."),
        new("only spaces", TestCategory.Negative, TestPriority.Medium, "\"   \" (3 spaces)", "Treated as empty.") { Value = "   " },
        new("the right user with spaces around it", TestCategory.Boundary, TestPriority.Medium, "your test user with 2 spaces before and after", "Ideally logged in (the spaces trimmed); at least the general message, never an error.") { Exact = false },
        new("very long", TestCategory.Boundary, TestPriority.Low, "1,000 × the letter a", NotLoggedIn) { Value = A1000 },
        new("only symbols", TestCategory.Negative, TestPriority.Low, AllSymbols, NotLoggedIn),
        new("accents and Cyrillic", TestCategory.Boundary, TestPriority.Low, Accents, NotLoggedIn),
        new("HTML and script", TestCategory.Security, TestPriority.High, Script, NotLoggedIn + " If the page repeats what was typed, it shows it as text; no alert pops up.") { Risk = XssRisk },
        new("HTML with an event handler", TestCategory.Security, TestPriority.High, ImgScript, NotLoggedIn + " No broken image, no alert.") { Risk = XssRisk },
        new("SQL injection", TestCategory.Security, TestPriority.High, Sql, NotLoggedIn) { Risk = SqlRisk },
        new("SQL comment after a real user", TestCategory.Security, TestPriority.High, "your test user followed by '-- (e.g. tester@example.com'--)", "Not logged in without the password.") { Exact = false, Risk = SqlRisk },
    ];

    private static readonly Row[] LoginPasswordRows =
    [
        new("left empty", TestCategory.Negative, TestPriority.High, null, "A message asks for it; nothing is sent."),
        new("only spaces", TestCategory.Negative, TestPriority.Medium, "\"        \" (8 spaces)", NotLoggedIn) { Value = "        " },
        new("the right password with spaces around it", TestCategory.Boundary, TestPriority.Medium, "the test user's password with a space before and after", "Not logged in (unless the password really has those spaces).") { Exact = false, Risk = "If accepted, the app trims passwords, so they're weaker than they look." },
        new("very long", TestCategory.Boundary, TestPriority.Medium, "1,000 × the letter a", NotLoggedIn + " The answer comes as quickly as for a short one.") { Value = A1000, Risk = "If very long passwords make login slow or crash it, an attacker can use that to take the site down." },
        new("SQL injection", TestCategory.Security, TestPriority.High, Sql, NotLoggedIn) { Risk = SqlRisk },
        new("HTML and script", TestCategory.Security, TestPriority.Medium, Script, NotLoggedIn) { Risk = XssRisk },
        .. PasswordHandlingRows,
    ];

    private static readonly Row[] PhoneRows =
    [
        new("valid local number", TestCategory.Positive, TestPriority.High, "061 234 5678", "Accepted."),
        new("international format", TestCategory.Positive, TestPriority.High, "+381 61 234 5678", "Accepted.") { Risk = "If refused, people with foreign numbers can't use the form." },
        new("with dashes and brackets", TestCategory.Positive, TestPriority.Low, "(061) 234-5678", "Accepted, or a message saying which format to use."),
        new("left empty", TestCategory.Negative, TestPriority.Medium, null, "If required: a message says so."),
        new("letters", TestCategory.Negative, TestPriority.High, "abcdefg", "Refused."),
        new("too short", TestCategory.Boundary, TestPriority.Medium, "12", "Refused with a message."),
        new("too long", TestCategory.Boundary, TestPriority.Medium, "123456789012345678901", "Refused, or cut off at the limit."),
        new("only a plus sign", TestCategory.Negative, TestPriority.Low, "+", "Refused."),
        new("spaces only", TestCategory.Negative, TestPriority.Low, "\"     \" (5 spaces)", "Treated as empty.") { Value = "     " },
        new("SQL injection", TestCategory.Security, TestPriority.Medium, "061' OR '1'='1", "Refused or treated as text; no database error.") { Risk = SqlRisk },
    ];

    private static readonly Row[] NumberRows =
    [
        new("valid number", TestCategory.Positive, TestPriority.High, "5", "Accepted."),
        new("zero", TestCategory.Boundary, TestPriority.High, "0", "Accepted or refused as the rules say, with a message if refused."),
        new("negative", TestCategory.Negative, TestPriority.High, "-1", "Refused if only positive numbers make sense (amounts, ages, quantities).") { Risk = "If accepted for an amount or a quantity, totals can go negative: refunds instead of payments, stock that grows when sold." },
        new("smallest allowed", TestCategory.Boundary, TestPriority.Medium, "the minimum (check the rules)", "Accepted.") { Exact = false },
        new("one below the smallest", TestCategory.Boundary, TestPriority.Medium, "minimum − 1", "Refused with a message giving the range.") { Exact = false },
        new("largest allowed", TestCategory.Boundary, TestPriority.Medium, "the maximum (check the rules)", "Accepted.") { Exact = false },
        new("one above the largest", TestCategory.Boundary, TestPriority.Medium, "maximum + 1", "Refused with a message giving the range.") { Exact = false },
        new("huge number", TestCategory.Boundary, TestPriority.Medium, "99999999999999999999", "Refused with a message; no error page or overflowing result.") { Risk = "If accepted, it can overflow and turn into a wrong (even negative) number in totals." },
        new("decimal with a point", TestCategory.Boundary, TestPriority.Medium, "1.5", "Accepted if decimals are allowed (and read as one and a half), otherwise refused."),
        new("decimal with a comma", TestCategory.Boundary, TestPriority.Medium, "1,5", "Same as 1.5 in a locale that writes decimals with a comma (Serbian, German), or a clear message.") { Risk = "If read as 15, amounts are silently ten times too big." },
        new("letters", TestCategory.Negative, TestPriority.High, "abc", "Refused."),
        new("leading zeros", TestCategory.Boundary, TestPriority.Low, "007", "Accepted as 7 (or kept as typed, if it's a code)."),
        new("scientific notation", TestCategory.Boundary, TestPriority.Low, "1e5", "Refused, or read as 100000 on purpose."),
        new("left empty", TestCategory.Negative, TestPriority.Medium, null, "If required: a message says so."),
        new("arrow keys / spin buttons", TestCategory.Usability, TestPriority.Low, "press Up and Down", "Change the value by one step and stop at the limits.", false) { Exact = false },
    ];

    private static readonly Row[] DateRows =
    [
        new("valid date", TestCategory.Positive, TestPriority.High, "today's date", "Accepted.") { Exact = false },
        new("29 February in a leap year", TestCategory.Boundary, TestPriority.Medium, "29.02.2024", "Accepted."),
        new("29 February in a normal year", TestCategory.Negative, TestPriority.Medium, "29.02.2023", "Refused.") { Risk = "If accepted, it's stored as 1 March (or fails later), and nobody notices." },
        new("day that doesn't exist", TestCategory.Negative, TestPriority.Medium, "31.04.2025", "Refused."),
        new("wrong format", TestCategory.Negative, TestPriority.Medium, "2025/13/45", "Refused with a message showing the expected format."),
        new("other order (month first)", TestCategory.Boundary, TestPriority.Low, "04/05/2025", "Read the way the page says (4 May or 5 April), not guessed silently.") { Risk = "If guessed, appointments and deadlines end up a month off." },
        new("letters", TestCategory.Negative, TestPriority.Medium, "yesterday", "Refused (or understood, if the app says it supports that)."),
        new("far past", TestCategory.Boundary, TestPriority.Low, "01.01.1900", "Accepted or refused as the rules say (e.g. birth dates)."),
        new("far future", TestCategory.Boundary, TestPriority.Low, "31.12.2100", "Accepted or refused as the rules say (e.g. no future birth dates)."),
        new("left empty", TestCategory.Negative, TestPriority.Medium, null, "If required: a message says so."),
        new("picked from the calendar", TestCategory.Usability, TestPriority.Medium, "a date chosen with the date picker", "The chosen date appears in the field in the right format.", false) { Exact = false },
        new("around midnight / time zones", TestCategory.Boundary, TestPriority.Low, "a date saved just before midnight", "Shown as the same day after saving (not a day earlier or later).", false) { Exact = false, Risk = Silent },
    ];

    private static readonly Row[] UrlRows =
    [
        new("valid address", TestCategory.Positive, TestPriority.High, "https://example.com", "Accepted."),
        new("without https://", TestCategory.Boundary, TestPriority.Medium, "example.com", "Accepted (and completed), or refused with a message."),
        new("plain http", TestCategory.Boundary, TestPriority.Low, "http://example.com", "Accepted, or a warning if only https is allowed."),
        new("not an address", TestCategory.Negative, TestPriority.Medium, "not a url", "Refused."),
        new("javascript: link", TestCategory.Security, TestPriority.High, "javascript:alert(1)", "Refused; never turned into a link that runs.") { Risk = LinkRisk },
        new("very long", TestCategory.Boundary, TestPriority.Low, "an address of 2,100 characters", "Refused with a message, or accepted without breaking the layout.") { Value = Url2100 },
        .. CommonTextRows.Where(r => r.Category == TestCategory.Security),
    ];

    private static readonly Row[] NameRows =
    [
        new("valid name", TestCategory.Positive, TestPriority.High, "Ana", "Accepted."),
        new("with a hyphen", TestCategory.Positive, TestPriority.Medium, "Ana-Marija", "Accepted."),
        new("with an apostrophe", TestCategory.Positive, TestPriority.Medium, "O'Brien", "Accepted, with no database error.") { Risk = "If it causes a database error, the apostrophe reaches the database unescaped: real names break the app, and it's a sign of SQL injection. " + "Severity: high." },
        new("accented letters", TestCategory.Positive, TestPriority.High, "Đorđe Čolić", "Accepted and shown exactly as typed."),
        new("Cyrillic", TestCategory.Positive, TestPriority.Medium, "Ђорђе", "Accepted and shown exactly as typed."),
        new("one letter", TestCategory.Boundary, TestPriority.Low, "A", "Accepted (real names can be one letter), or a clear message."),
        new("digits", TestCategory.Negative, TestPriority.Low, "Ana2", "Refused if names can't hold digits."),
        new("left empty", TestCategory.Negative, TestPriority.High, null, "If required: a message says so."),
        .. CommonTextRows,
    ];

    private static readonly Row[] MultiLineRows =
    [
        new("several lines", TestCategory.Positive, TestPriority.High, "three lines of text, with Enter between them", "Accepted; the line breaks are kept when shown again.") { Value = "First line\nSecond line\nThird line" },
        new("very long text", TestCategory.Boundary, TestPriority.Medium, "10,000 × the letter a", "Stops at the limit (ideally with a counter), or a message; no layout break.") { Value = A10000 },
        new("pasted formatted text", TestCategory.Boundary, TestPriority.Low, "text copied from Word or a web page", "Pasted as plain text (or kept formatted, if it's a rich editor), without hidden markup.") { Exact = false },
        new("left empty", TestCategory.Negative, TestPriority.Medium, null, "If required: a message says so."),
        .. CommonTextRows,
    ];

    private static readonly Row[] SearchRows =
    [
        new("a term that has results", TestCategory.Positive, TestPriority.High, "a word you know is on the site, e.g. Java", "Results appear, and each one matches the term.") { Value = "Java" },
        new("part of a word", TestCategory.Positive, TestPriority.Medium, "Jav", "Matching results, if the search supports partial words."),
        new("different case", TestCategory.Positive, TestPriority.Medium, "JAVA", "The same results as java."),
        new("without accents", TestCategory.Boundary, TestPriority.Medium, "inzenjer", "Ideally the same results as inženjer; at least no error."),
        new("with accents", TestCategory.Positive, TestPriority.Medium, "inženjer", "Results with the accented word."),
        new("two words", TestCategory.Positive, TestPriority.Medium, "senior developer", "Results with both words (check whether it's both or either)."),
        new("a term with no results", TestCategory.Negative, TestPriority.High, "xqzwv", "A clear \"no results\" message, not an empty page or an error."),
        new("empty search", TestCategory.Boundary, TestPriority.Medium, null, "Shows everything, or asks for a term; no error."),
        new("only spaces", TestCategory.Boundary, TestPriority.Low, "\"   \" (3 spaces)", "Same as an empty search.") { Value = "   " },
        new("spaces before and after", TestCategory.Boundary, TestPriority.Low, "\"  Java  \" (with the spaces)", "Same results as Java.") { Value = "  Java  " },
        new("only symbols", TestCategory.Negative, TestPriority.Medium, AllSymbols, "\"No results\" or results for the text; no error page."),
        new("very long term", TestCategory.Boundary, TestPriority.Low, "1,000 × the letter a", "No error page; the term is cut off or refused.") { Value = A1000 },
        new("quotes", TestCategory.Boundary, TestPriority.Low, "\"senior developer\"", "Exact-phrase results if supported, otherwise the same as without quotes; no error."),
        new("HTML and script", TestCategory.Security, TestPriority.High, Script, "The term is shown as text on the results page (\"No results for …\"); nothing runs.")
        {
            Risk = XssRisk + " In a search it's usually \"reflected\": the term is in the results page's address, so a link to that address runs the code for whoever opens it.",
        },
        new("HTML with an event handler", TestCategory.Security, TestPriority.High, ImgScript, "The term is shown as text; no broken image, no alert.") { Risk = XssRisk + " This form runs even where a <script> tag doesn't: in pages that insert text with innerHTML (DOM-based XSS)." },
        new("SQL injection", TestCategory.Security, TestPriority.High, Sql, "No results or results for the text; no database error, no extra data.") { Risk = SqlRisk },
        new("emoji", TestCategory.Boundary, TestPriority.Low, Emoji, "No error page."),
    ];

    private static readonly Row[] ChoiceRows =
    [
        new("each option can be chosen", TestCategory.Positive, TestPriority.High, "every option in the list, one by one", "Each one is shown as chosen and does what it says.", false) { Exact = false },
        new("default value", TestCategory.Positive, TestPriority.Medium, null, "The pre-selected value is a sensible one (or a \"choose…\" prompt)."),
        new("nothing chosen", TestCategory.Negative, TestPriority.Medium, null, "If required: a message says so."),
        new("keyboard: arrows and typing", TestCategory.Accessibility, TestPriority.Medium, "open with Alt+Down, move with arrows, type the first letters", "Works without the mouse; the chosen option is read by a screen reader.", false) { Exact = false },
        new("long option text", TestCategory.Usability, TestPriority.Low, "the longest option", "Shown whole or with a clean cut, not overlapping other controls.", false) { Exact = false },
    ];

    private static readonly Row[] CheckRows =
    [
        new("ticking and unticking", TestCategory.Positive, TestPriority.High, null, "The state changes each time, and is what gets saved.", false),
        new("Space key", TestCategory.Accessibility, TestPriority.Medium, null, "Space toggles it when it has keyboard focus.", false),
        new("clicking its label", TestCategory.Usability, TestPriority.Low, null, "Clicking the text next to it toggles it too.", false),
        new("required box left unticked", TestCategory.Negative, TestPriority.Medium, null, "If it's required (terms and conditions): sending is refused with a message.", false),
    ];

    // ---------------------------------------------------------------- features

    private static void Login(Builder b, TestFeature f, bool isWeb)
    {
        var user = f.Fields.FirstOrDefault(x => x.Kind != FieldKind.Password && x.Kind is not (FieldKind.CheckBox or FieldKind.Radio));
        var password = f.Fields.First(x => x.Kind == FieldKind.Password);
        var u = user is null ? "the user name" : The(user);
        var p = The(password);
        var go = Submit(f);

        b.Add("Log in with a valid account", TestCategory.Positive, TestPriority.High, Steps($"Type a valid user into {u}.", $"Type its password into {p}.", go),
            "You're logged in and see the right start page with your name.", "a test account", true);
        b.Add("Wrong password", TestCategory.Negative, TestPriority.High, Steps($"Type a valid user into {u}.", $"Type a wrong password into {p}.", go),
            "Login refused with a general message (\"wrong user name or password\"), not saying which one was wrong.", "a valid user, a wrong password", true,
            risk: "If the message says the password (not the user) was wrong, attackers learn which users exist.");
        b.Add("User that doesn't exist", TestCategory.Negative, TestPriority.High, Steps($"Type the data below into {u}.", $"Type any password into {p}.", go),
            "The same message as for a wrong password (so attackers can't find out which users exist).", "nobody@example.com", true, "nobody@example.com",
            "If the messages differ (\"no such user\" against \"wrong password\"), attackers can test a list of emails, learn which have accounts, and target those. Severity: medium.", user?.Control);
        b.Add("Both empty", TestCategory.Negative, TestPriority.High, Steps($"Leave {u} and {p} empty.", go),
            "Messages for both fields; nothing is sent.", null, true, emptyAll: true);
        b.Add("Password in the wrong case", TestCategory.Security, TestPriority.High, Steps($"Type a valid user into {u}.", $"Type the password with letters in the wrong case into {p}.", go),
            "Refused: passwords are case-sensitive.", "the right password, case swapped (e.g. vALID#pASS2024)", true,
            risk: "If accepted, passwords are compared without case, which makes them much easier to guess. Severity: medium.", target: password.Control);
        b.Add("User name in a different case", TestCategory.Boundary, TestPriority.Medium, Steps($"Type the valid user in capitals into {u}.", $"Type its password into {p}.", go),
            "Logged in if user names (emails) aren't case-sensitive, which is usual for emails.", "e.g. TESTER@EXAMPLE.COM", true, target: user?.Control);
        b.Add("Log in with the Enter key", TestCategory.Usability, TestPriority.Medium, Steps($"Fill in {u} and {p}.", $"Press Enter in {p}."), "Logged in, same as clicking the button.");
        b.Add("Locked after repeated failures", TestCategory.Security, TestPriority.High, Steps("Try a wrong password for the same user 5–10 times.", "Then try the right password."),
            "After a few tries the account or the attempts are limited (a wait, a CAPTCHA, a lock), and the message says so.",
            risk: "If you can keep trying forever, a program can guess passwords automatically (brute force) until one works. Severity: high.");
        b.Add("Double click on the login button", TestCategory.Boundary, TestPriority.Low, Steps("Fill in valid data.", $"Double-click {(f.Action is { } a ? The(a) : "the login button")} quickly."),
            "Logged in once; no error from a second request.");
        b.Add("Still logged in after refresh", TestCategory.Usability, TestPriority.Medium, Steps("Log in.", isWeb ? "Press F5." : "Close and reopen the window."),
            "Still logged in (unless the app says otherwise).");
        b.Add("Logged out after logging out", TestCategory.Security, TestPriority.High, Steps("Log in, then log out.", isWeb ? "Press the browser's Back button." : "Try to reach a page that needs login."),
            "The logged-in page isn't shown again; you're asked to log in.",
            risk: "If the page still shows, the next person at a shared computer sees (and maybe uses) the account. Severity: medium.");
        if (isWeb)
        {
            b.Add("Password isn't sent in the address", TestCategory.Security, TestPriority.High, Steps("Log in (with right and with wrong data).", "Look at the address bar and the history (Ctrl+H)."),
                "The password never appears in the address bar or the history; the page uses https.",
                risk: "If the password is in the address, it's saved in the browser history, in server logs and by proxies, where others can read it. Severity: high.");
            b.Add("Login works in two tabs", TestCategory.Boundary, TestPriority.Low, Steps("Open the login page in two tabs.", "Log in in one, then refresh the other."),
                "The other tab shows you as logged in, with no error.");
        }
        b.Add("Forgotten password link", TestCategory.Positive, TestPriority.Medium, Steps("Click the link for a forgotten password, if there is one."),
            "It leads to a page to reset the password.");
        b.Add("Keyboard order", TestCategory.Accessibility, TestPriority.Medium, Steps("Start before the form and press Tab repeatedly."),
            $"Focus goes {u} → {p} → the login button, in that order, always visibly.");
        // At login the fields aren't checked for format or strength, so their own lists are the login ones.
        foreach (var field in f.Fields)
        {
            var rows = field.Kind switch
            {
                FieldKind.Password => LoginPasswordRows,
                FieldKind.CheckBox or FieldKind.Radio => CheckRows,
                _ => LoginUserRows,
            };
            FieldCases(b, f, field, isWeb, rows);
        }
    }

    private static void Registration(Builder b, TestFeature f, bool isWeb)
    {
        var passwords = f.Fields.Where(x => x.Kind == FieldKind.Password).ToList();
        var go = Submit(f);
        b.Add("Sign up with valid data", TestCategory.Positive, TestPriority.High, Steps("Fill in every field with valid data.", go),
            "The account is created; a confirmation (page or email) follows; you can log in with it.", "a new email address and valid data", true);
        b.Add("All fields empty", TestCategory.Negative, TestPriority.High, Steps(go), "A message for each required field; nothing is created.", null, true, emptyAll: true);
        b.Add("Email that's already registered", TestCategory.Negative, TestPriority.High, Steps("Fill in valid data with an email that already has an account.", go),
            "Refused with a message (or a neutral \"check your email\"), and no second account is created.", "an existing account's email", true,
            risk: "If a second account is created, one person has two accounts and the old one may be taken over; if the message says \"already registered\", attackers can learn who has an account.");
        if (passwords.Count >= 2)
        {
            b.Add("Passwords don't match", TestCategory.Negative, TestPriority.High,
                Steps($"Type Valid#Pass2024 into {The(passwords[0])}.", $"Type Valid#Pass2025 into {The(passwords[1])}.", go),
                "Refused with a message that the passwords don't match.", "Valid#Pass2024 and Valid#Pass2025", true,
                risk: "If accepted, the person may not know their own password.", target: passwords[1].Control);
            b.Add("Passwords differ only in case", TestCategory.Negative, TestPriority.Medium,
                Steps($"Type Valid#Pass2024 into {The(passwords[0])}.", $"Type valid#pass2024 into {The(passwords[1])}.", go),
                "Refused: they don't match.", "Valid#Pass2024 and valid#pass2024", true, target: passwords[1].Control);
        }
        b.Add("Double click on sign-up", TestCategory.Boundary, TestPriority.Medium, Steps("Fill in valid data.", "Double-click the sign-up button quickly."), "Exactly one account is created.",
            risk: "If two accounts are created, the person gets two confirmation emails and two accounts with the same email.");
        b.Add("Data kept after an error", TestCategory.Usability, TestPriority.Medium, Steps("Fill in everything, but one field invalid.", go),
            "Only the invalid field needs fixing; everything else is still filled in (except passwords, which may be cleared).");
        b.Add("Terms and conditions", TestCategory.Positive, TestPriority.Medium, Steps("Open the terms link, if there is one, and try to sign up without accepting them."),
            "The terms open; signing up without accepting them is refused, if they're required.");
        foreach (var field in f.Fields) FieldCases(b, f, field, isWeb);
    }

    private static void Search(Builder b, TestFeature f, bool isWeb)
    {
        var field = f.Fields[0];
        b.Add("Search with the Enter key", TestCategory.Usability, TestPriority.Medium, Steps($"Type the data below into {The(field)}.", "Press Enter."), "Same results as with the search button.", "Java", true, "Java", target: field.Control);
        b.Add("Clearing the search", TestCategory.Usability, TestPriority.Medium, Steps("Search for something.", $"Clear {The(field)} (or use its clear button)."),
            "The full list (or the starting state) comes back.", target: field.Control);
        b.Add("Number of results", TestCategory.Positive, TestPriority.Medium, Steps("Search for a term."), "The shown count matches the number of results listed (across all pages).");
        if (isWeb)
        {
            b.Add("Search address can be shared", TestCategory.Usability, TestPriority.Low, Steps("Search for something.", "Copy the address and open it in a new tab."), "The same results show.");
            b.Add("Back after opening a result", TestCategory.Usability, TestPriority.Medium, Steps("Search, open a result, press Back."), "The same results, at the same scroll position, with the term still in the box.");
        }
        b.Add("Search while typing (if it suggests)", TestCategory.Usability, TestPriority.Low, Steps($"Type slowly into {The(field)}."), "Suggestions update without flickering or lag; Down and Enter pick one.", target: field.Control);
        FieldCases(b, f, field, isWeb);
    }

    private static void Form(Builder b, TestFeature f, bool isWeb)
    {
        var go = Submit(f);
        var texts = f.Fields.Where(x => x.Kind is not (FieldKind.CheckBox or FieldKind.Radio)).ToList();
        b.Add("Send with valid data", TestCategory.Positive, TestPriority.High, Steps("Fill in every field with valid data.", go),
            "A success message; the data is saved and shown correctly afterwards.", "valid data in every field", true);
        b.Add("Send with everything empty", TestCategory.Negative, TestPriority.High, Steps(go), "A message for each required field; nothing is sent.", null, true, emptyAll: true);
        // With one field, "only this one empty" is "everything empty", which is already there.
        foreach (var field in texts.Count > 1 ? texts : [])
        {
            b.Add($"Only “{field.Label}” left empty", TestCategory.Negative, TestPriority.Medium, Steps(FillOthers(f, field), $"Leave {The(field)} empty.", go),
                "If it's required: a message at that field only. If optional: sent.", null, true, target: field.Control, fieldData: true);
        }
        b.Add("Data kept after an error", TestCategory.Usability, TestPriority.Medium, Steps("Fill in everything, but one field invalid.", go),
            "Only the invalid field needs fixing; the rest is still filled in.");
        b.Add("Double click on send", TestCategory.Boundary, TestPriority.Medium, Steps("Fill in valid data.", "Double-click the send button quickly."), "Sent exactly once.",
            risk: "If sent twice, there are duplicate orders, payments, messages or records.");
        b.Add("Send with the Enter key", TestCategory.Usability, TestPriority.Low, Steps("Fill in valid data.", "Press Enter in the last field."), "Sent, same as clicking the button (except in multi-line fields).");
        if (isWeb) b.Add("Refresh after sending", TestCategory.Boundary, TestPriority.Low, Steps("Send the form.", "Press F5."), "Not sent a second time (or the browser asks first).", risk: "If sent again, there are duplicate records.");
        b.Add("Keyboard order", TestCategory.Accessibility, TestPriority.Medium, Steps("Press Tab through the form."), "Focus goes through the fields in visual order, always visibly, ending at the send button.");
        foreach (var field in f.Fields) FieldCases(b, f, field, isWeb);
    }

    private static void Filters(Builder b, TestFeature f, bool isWeb)
    {
        var first = f.Controls.FirstOrDefault();
        var example = first is null ? "a filter" : The(first);
        // A filter on this page that shows its count, if there is one ("Programiranje [77]").
        var counted = f.Controls.Select(c => TestabilityAnalyzer.Visible(c.Element.Name)).FirstOrDefault(n => n.Length > 0 && char.IsDigit(n.TrimEnd(')', ']', ' ')[^1]));
        b.Add("Each filter narrows the list", TestCategory.Positive, TestPriority.High, Steps($"Use {example} (then each other filter, one at a time)."),
            "Only matching items remain; each item really matches the filter.", null, true);
        b.Add("Counts match the results", TestCategory.Positive, TestPriority.High,
            Steps(counted is null ? "Note a filter's count, if filters show one." : $"Note the count of “{counted}”.", "Use that filter."),
            "Exactly that many results (across all pages).", target: f.Controls.FirstOrDefault(c => TestabilityAnalyzer.Visible(c.Element.Name) == counted));
        b.Add("Two filters together", TestCategory.Positive, TestPriority.High, Steps("Use two filters of different kinds (e.g. a technology and a city)."),
            "Only items matching both remain (or either, if the page says so).");
        b.Add("Two options of the same filter", TestCategory.Boundary, TestPriority.Medium, Steps("Choose two options of one filter (e.g. two cities)."), "Items matching either option.");
        b.Add("A combination with no results", TestCategory.Negative, TestPriority.Medium, Steps("Combine filters until nothing matches."), "A clear \"no results\" message, and a way to clear the filters.");
        b.Add("Clearing filters", TestCategory.Positive, TestPriority.High, Steps("Use some filters.", "Clear them (one by one, then all)."), "The full list comes back; the counts are back too.");
        b.Add("Filter together with search", TestCategory.Positive, TestPriority.Medium, Steps("Search for a term, then use a filter."), "Results match both the term and the filter.");
        if (isWeb)
        {
            b.Add("Filters kept after refresh and Back", TestCategory.Usability, TestPriority.Medium, Steps("Use filters.", "Press F5; then open an item and press Back."), "The same filters and results are still there.");
            b.Add("Filters in the address", TestCategory.Usability, TestPriority.Low, Steps("Use filters.", "Copy the address and open it in a new tab."), "The same filtered results.");
        }
        b.Add("Filters by keyboard", TestCategory.Accessibility, TestPriority.Medium, Steps("Reach the filters with Tab and use them with Space or Enter."), "Every filter works without the mouse; its state is read by a screen reader.");
        b.Add("Filtering is quick", TestCategory.Usability, TestPriority.Low, Steps("Use several filters quickly one after another."), "The list follows the last choice, without flicker or out-of-date results.");
    }

    private static void List(Builder b, TestFeature f, bool isWeb)
    {
        var first = f.Controls.FirstOrDefault();
        var item = first is null ? "the first item" : The(first);
        b.Add("The list shows items", TestCategory.Positive, TestPriority.High, Steps("Open the page."), $"The list shows items (here {f.ControlCount}), each with its title and details.", null, true);
        b.Add("An item opens its details", TestCategory.Positive, TestPriority.High, Steps($"Click {item}."), "Its own details open, matching what the list said (title, company, place).", null, true);
        b.Add("The count matches", TestCategory.Positive, TestPriority.Medium, Steps("Compare a shown total (e.g. \"77 results\") with the items listed on all pages."), "They match.");
        b.Add("No duplicates", TestCategory.Boundary, TestPriority.Medium, Steps("Look through the whole list (all pages)."), "No item appears twice.");
        b.Add("Back keeps the place", TestCategory.Usability, TestPriority.Medium, Steps("Scroll down, open an item, go back."), "The list is at the same place, not back at the top.");
        b.Add("Long titles", TestCategory.Usability, TestPriority.Low, Steps("Find the item with the longest title."), "It wraps or is cut cleanly; nothing overlaps.");
        b.Add("Empty list", TestCategory.Boundary, TestPriority.Medium, Steps("Make the list empty (filters or search with no results)."), "A clear message instead of an empty space.");
        b.Add("Sorting, if offered", TestCategory.Positive, TestPriority.Medium, Steps("Sort by each option (newest, A–Z…)."), "The order really follows the option, also across pages.");
        if (isWeb)
        {
            b.Add("Open an item in a new tab", TestCategory.Usability, TestPriority.Low, Steps($"Middle-click (or Ctrl+click) {item}."), "It opens in a new tab (links are real links).");
            b.Add("Images load", TestCategory.Usability, TestPriority.Low, Steps("Scroll through the list."), "Logos and pictures load; missing ones show a placeholder, not a broken image.");
        }
        b.Add("List by keyboard", TestCategory.Accessibility, TestPriority.Medium, Steps("Move through the items with Tab and open one with Enter."), "Every item can be reached and opened without the mouse.");
    }

    private static void Paging(Builder b, TestFeature f, bool isWeb)
    {
        b.Add("Next page", TestCategory.Positive, TestPriority.High, Steps("Go to the next page."), "New items, none repeated from page 1; the page number shows it.", null, true);
        b.Add("Previous page", TestCategory.Positive, TestPriority.High, Steps("Go to page 2, then back."), "Page 1's items again.", null, true);
        b.Add("First page: no Previous", TestCategory.Boundary, TestPriority.Medium, Steps("Look at page 1."), "Previous is hidden or disabled.");
        b.Add("Last page: no Next", TestCategory.Boundary, TestPriority.Medium, Steps("Go to the last page."), "Next is hidden or disabled; the last items are there.");
        b.Add("Jump to a page number", TestCategory.Positive, TestPriority.Medium, Steps("Click a page number (e.g. 3)."), "That page opens and is marked as current.");
        b.Add("Paging keeps filters and search", TestCategory.Positive, TestPriority.Medium, Steps("Filter or search, then go to the next page."), "The next page is still filtered.");
        b.Add("Items across pages add up", TestCategory.Boundary, TestPriority.Low, Steps("Count the items on all pages."), "The total matches the count shown; nothing missing or repeated.");
        if (isWeb)
        {
            b.Add("Page number out of range", TestCategory.Negative, TestPriority.Low, Steps("Change the page number in the address to each value below, one at a time."),
                "A friendly empty or not-found page, not an error.", "9999, then -1, then abc", value: "9999",
                risk: "If an error page shows details (file paths, stack traces, database names), it tells attackers how the site is built.");
        }
    }

    private static void Tabs(Builder b, TestFeature f)
    {
        b.Add("Each tab shows its content", TestCategory.Positive, TestPriority.High, Steps($"Click each tab ({string.Join(", ", f.Controls.Take(5).Select(c => $"“{FeatureDetector.Label(c.Element)}”"))})."), "Its own content shows, and the tab is marked as selected.", null, true);
        b.Add("Tabs by keyboard", TestCategory.Accessibility, TestPriority.Medium, Steps("Focus a tab, then use the arrow keys."), "Arrows move between tabs; the selected tab is read by a screen reader.");
        b.Add("Content kept when switching", TestCategory.Usability, TestPriority.Medium, Steps("Type something in one tab, switch away and back."), "What was typed is still there (or the app warns first).");
    }

    private static void Menu(Builder b, TestFeature f)
    {
        b.Add("Each menu opens", TestCategory.Positive, TestPriority.High, Steps("Open each menu and drop-down."), "It opens next to its button, fully visible.", null, true);
        b.Add("Each item works", TestCategory.Positive, TestPriority.High, Steps("Choose each menu item."), "It does what it says.");
        b.Add("Escape and clicking outside close it", TestCategory.Usability, TestPriority.Medium, Steps("Open a menu, press Escape; open it again and click elsewhere."), "It closes both times; focus returns to its button.");
        b.Add("Menus by keyboard", TestCategory.Accessibility, TestPriority.Medium, Steps("Open a menu with Enter or Space, move with arrows, choose with Enter."), "Works without the mouse; open/closed is read by a screen reader.");
    }

    private static void Navigation(Builder b, TestFeature f, bool isWeb)
    {
        b.Add("Each link opens its page", TestCategory.Positive, TestPriority.High, Steps($"Click each link ({string.Join(", ", f.Controls.Take(5).Select(c => $"“{FeatureDetector.Label(c.Element)}”"))}…)."), "The right page opens; no \"not found\" pages.", null, true);
        b.Add("The current page is marked", TestCategory.Usability, TestPriority.Low, Steps("Open each page from the navigation."), "Its link is highlighted as the current page.");
        b.Add("Logo leads home", TestCategory.Usability, TestPriority.Low, Steps("Click the logo."), "The start page opens.");
        if (isWeb) b.Add("Links open in a new tab", TestCategory.Usability, TestPriority.Low, Steps("Middle-click a link."), "It opens in a new tab.");
        b.Add("Navigation by keyboard", TestCategory.Accessibility, TestPriority.Medium, Steps("Press Tab from the top of the page."), "The links can be reached in order; a \"skip to content\" link comes first, ideally.");
    }

    private static void Dialog(Builder b, TestFeature f)
    {
        b.Add("Dialog opens and closes", TestCategory.Positive, TestPriority.High, Steps("Open the dialog, then close it with its close button."), "It opens over the page and closes cleanly.", null, true);
        b.Add("Escape closes it", TestCategory.Usability, TestPriority.Medium, Steps("Open the dialog, press Escape."), "It closes (unless it needs an answer), and focus goes back to what opened it.");
        b.Add("Focus stays inside", TestCategory.Accessibility, TestPriority.Medium, Steps("Open the dialog and press Tab many times."), "Focus moves through the dialog only; the page behind can't be used.");
        b.Add("Each button does what it says", TestCategory.Positive, TestPriority.High, Steps($"Use each button ({string.Join(", ", f.Controls.Take(4).Select(c => $"“{FeatureDetector.Label(c.Element)}”"))})."), "OK/Confirm applies, Cancel changes nothing.");
    }

    private static void Page(Builder b, bool isWeb)
    {
        b.Add("Opens without errors", TestCategory.Positive, TestPriority.High, Steps(isWeb ? "Open the page with the developer tools (F12) Console open." : "Open the window."),
            isWeb ? "Everything loads; no errors in the Console, no failed requests in the Network tab." : "Everything shows; no error dialogs.");
        b.Add("Keyboard only", TestCategory.Accessibility, TestPriority.High, Steps("Use the whole page with Tab, Shift+Tab, Enter, Space, arrows and Escape only."), "Everything can be reached and used, and the focus is always visible.",
            risk: "If something can't be reached without a mouse, people who can't use one can't use the app (and it may break accessibility law, e.g. the European Accessibility Act).");
        b.Add("Screen reader", TestCategory.Accessibility, TestPriority.Medium, Steps("Turn on Narrator (Ctrl+Win+Enter) and go through the page."), "Headings, buttons, links and fields are read with meaningful names (see the testability report for the ones without names).");
        b.Add(isWeb ? "Zoom to 200%" : "Larger text (Windows text size 150%)", TestCategory.Accessibility, TestPriority.Medium,
            Steps(isWeb ? "Press Ctrl and + until 200%." : "Settings → Accessibility → Text size → 150%, then reopen."), "Everything is still readable and usable; nothing is cut off or overlaps.");
        b.Add("High contrast", TestCategory.Accessibility, TestPriority.Low, Steps("Turn on a contrast theme (Left Alt+Left Shift+Print Screen)."), "Text, buttons and focus are still visible.");
        if (isWeb)
        {
            b.Add("Phone and tablet widths", TestCategory.Usability, TestPriority.High, Steps("Open the developer tools (F12), device toolbar (Ctrl+Shift+M); try 375, 768 and 1280 px wide."), "The layout adapts; no sideways scrolling; menus are reachable.");
            b.Add("Refresh, Back and Forward", TestCategory.Usability, TestPriority.Medium, Steps("Use the page, then F5, Back and Forward."), "The page comes back in a sensible state; no \"resend form\" surprises.");
            b.Add("Slow connection", TestCategory.Boundary, TestPriority.Low, Steps("Developer tools → Network → throttling \"Slow 4G\"; reload."), "Loading indicators show; nothing breaks while waiting.");
            b.Add("Other browsers", TestCategory.Positive, TestPriority.Medium, Steps("Open the page in Chrome, Edge and Firefox (and Safari, if the users have it)."), "It looks and works the same.");
        }
        else
        {
            b.Add("Resize, maximize, minimize", TestCategory.Usability, TestPriority.Medium, Steps("Make the window small, maximize it, minimize and restore it."), "The layout adapts; nothing is cut off; the content stays.");
            b.Add("Display scaling", TestCategory.Usability, TestPriority.Low, Steps("Run it at 100% and 150% display scaling (or move it to a monitor with other scaling)."), "Sharp and correctly laid out on both.");
        }
        b.Add("Another language or region", TestCategory.Boundary, TestPriority.Low, Steps("Switch the app's (or Windows'/browser's) language and region."), "Texts are translated, dates and numbers use the local format, nothing is cut off.");
    }
}

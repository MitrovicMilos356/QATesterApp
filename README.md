# UiInspector — Windows UI element inspector

Reads any Windows app's UI through UI Automation (UIA) and shows each element's properties,
patterns and position in the UI tree, then generates a **verified selector** and ready-to-paste
code for FlaUI (C#), Python `uiautomation`, pywinauto and Appium. For elements inside Chromium
pages (Electron, WebView2, Chrome, Edge) it also generates **Playwright** (JS, Python, C#) and
**Selenium** (Python, Java) code from the real DOM. A **recorder** turns a workflow you click
through into a complete script, and a **bug report** builder turns the same recording into a
report with steps, screenshots and environment. This covers Phases 1–4 and the recorder from Phase 5 of
[windows_ui_inspector_spec.md](windows_ui_inspector_spec.md): a desktop GUI and a console tool
that share one engine.

## Requirements

- Windows 10 (21H2+) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual Studio is **not** required.

## GUI

From the repository root:

```bash
dotnet run --project src/UiInspector.App
```

| How | What it does |
| :--- | :--- |
| **Hold `Ctrl`** and hover over any app | Live preview: an orange frame follows the cursor and the details panel updates |
| **Release `Ctrl`** (after ~0.4 s) | Picks that element: red frame, full tree with siblings at every level, details |
| A quick `Ctrl` tap (e.g. `Ctrl+C`) | Ignored, so normal shortcuts don't change your selection |
| `Ctrl+Shift+F9` / `F10` / `F11` | Pick under cursor / focused element / delayed capture, from any app |
| `Ctrl+Shift+F12` | Turn live pick on or off |
| Click a node in the tree | Shows its details and frames it on screen |

- **Smart target** (on by default): if you point at a label or icon inside a control, such as the status
  text inside a Discord chat link, the inspector selects the control itself. A banner explains the
  switch, and **Select what I pointed at** returns to the exact element.
- **Copy details** puts the element's properties and its path from the desktop on the clipboard.
- **Browse windows** lists every top-level window in the tree, so you can pick elements the mouse
  can't reach (hidden, covered or off-screen).
- The highlight frame lets clicks through and is never picked itself.

### Selector & code tab

Selecting any element generates a selector and **checks it against the live app** before showing it:

1. A unique attribute inside the window (AutomationId first, then Name, then ClassName, always
   together with the ControlType).
2. If that matches several elements: the same, scoped to the nearest ancestor that is itself
   unique (e.g. *Button "Delete" inside Pane #ordersPanel*).
3. If no ancestor is unique by itself: the nearest one told apart by **text it contains**, the
   way a person would (*Button "Delete" inside the Pane containing "Order 1042"*). This survives
   rows being added, removed or reordered; it does tie the test to that data. If even inside that
   container only a position tells the element apart (unnamed items of a popup menu), it uses the
   position there: *Button #2 inside the Group containing "Edit Profile"* only breaks if that
   menu changes, not when anything else in the window does.
4. Last resort, when nothing at all tells look-alikes apart: its position among the matches,
   clearly marked as fragile. The fix then is an AutomationId from the developers (WPF
   `AutomationProperties.AutomationId`, WinForms `Name`, web `data-testid`).

Attributes that would break are rejected or flagged with a reason: generated hashes
(`content_d125d2`), GUIDs, WinForms per-build class names, window handles used as IDs, designer
default names (`textBox17`), and live status text inside names (only `Dyno (direct message)` is
used from `Dyno (direct message), Online`), and times and dates (only `GoAway` from
`GoAway 11:21 PM`). A **click check** says whether a real mouse click at
the element's center would reach it, or would hit another window, an overlay, or nothing
(off-screen). Generated code prefers pattern actions (Invoke, Toggle, SetValue…) over mouse clicks.

Other verified selectors for the same element are in the drop-down above the steps.

### Web apps: Playwright and Selenium code

For an element inside a web page (Chrome, Edge, Electron apps like Discord, Slack or VS Code,
WebView2 apps, and Firefox), the Selector tab, the Recorder and the Test plan also give Playwright
(JavaScript, Python, C#) and Selenium (Python, Java) code. Desktop apps that aren't web pages
(WinForms, WPF, WinUI, Win32, Qt) get FlaUI, uiautomation, pywinauto and Appium only: Playwright
and Selenium drive web content, not native windows. **No special setup is needed:**

- **Firefox:** the scripts open their own Firefox. Playwright uses its own build of Firefox, so
  install it once (`playwright install firefox`, or `npx playwright install firefox`); Selenium uses
  the installed Firefox through geckodriver. Attaching to a running Firefox isn't possible (that
  needs Chromium's debug port), and Firefox's locators always come from the accessibility tree.

- **Locators come from the accessibility tree.** Chromium shows UI Automation each element's ARIA
  role, accessible name, HTML `id` and classes, so the inspector builds `getByRole('button', { name: 'Save' })`,
  `#email-input` and the like from the selector it already verified there. Iframes are entered by
  their id or title; Shadow DOM is no obstacle for Playwright.
- **The generated script reaches the app by itself.** For a website it opens its own Edge or Chrome
  window and goes to the page. For an Electron or WebView2 app it attaches if the app already has a
  debug port; otherwise it starts the app with one and attaches. Apps that update themselves with
  Squirrel (Discord, Slack…) are started through their `Update.exe`, so the script survives updates.
- **Close the app before running such a script**, completely: apps like Discord only hide to the
  system tray when their window is closed (right-click the tray icon → Quit). A running copy
  ignores a debug port, so the script checks for one and says so at once instead of waiting.
- **Clickable areas without a name** (the `<div>`s chat apps are full of) are clicked through a text
  inside them that appears nowhere else on the page.

**Optional: start the app with a debug port** while inspecting to get more: `data-testid`
attributes, which are invisible in the accessibility tree, and every locator verified inside the
page itself. Scripts then attach to the running app. Quit the app completely first, then start it
like this:

| App | How to start it |
| :--- | :--- |
| Electron apps (VS Code, Slack…) | `app.exe --remote-debugging-port=9222` |
| Discord | `"%LOCALAPPDATA%\Discord\Update.exe" --processStart Discord.exe --process-start-args "--remote-debugging-port=9222"` |
| WebView2 hosts (new Outlook, LOB apps) | set the environment variable `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, then start the app |
| Chrome / Edge | `msedge.exe --remote-debugging-port=9222 --user-data-dir=C:\temp\edge-debug` |

The port is read from the app's command line automatically; type one into **Debug port** to
override it. The web section of the Selector tab says which way the locators were made.

> **Security:** while the port is open, any program on this computer can fully control the app
> (read its pages, run script). It only listens on `127.0.0.1`, but close the app when you're done.

The web locators follow Playwright's own preferences: `getByTestId` > `getByRole(role, { name })` >
`#id` > stable CSS classes, anchored to a named region or list when needed (*button "Delete" inside
region "Orders"*). When rows have nothing like that (React-style `<div>`s with hashed classes),
the element is found through a text only its row has:
`getByText('Order 1042', { exact: true }).locator('xpath=ancestor::div[1]').getByRole('button', { name: 'Delete' })`,
and for Selenium the same as XPath, then CSS inside that row. An index is the last resort. Each one is **verified in the live page**:
CSS by counting matches (reaching into open Shadow DOM, as Playwright does), roles by checking each
candidate's computed role and name in Chromium's accessibility tree. Selenium gets a separate CSS
locator that is unique within its Shadow DOM root, plus steps through each shadow host.

Limits: elements inside **cross-origin iframes** (e.g. payment providers) can be entered but not
read yet; use the native selector for them. Electron apps need a chromedriver that matches the
app's Chromium version for Selenium.

### Recorder: click through a workflow, get a script

Open the **Recorder** tab and press **● Record** (or `Ctrl+Shift+F8` from any app), then use the app
you want to test. Each action becomes a step with a verified selector:

| You do | Step in the script |
| :--- | :--- |
| Click a button, link, menu item, tab… | Its pattern action: `Invoke`, `Toggle` (check boxes), `Select` (list items, tabs), `Expand` |
| Click a control without one | A mouse click (marked "moves the real mouse") |
| Double-click, right-click | A mouse double-click / right-click |
| Type into a field | `SetValue` with the field's final text (the click into the field is dropped) |
| Type into an editor without a settable value (rich text, web editors) | The text typed as keys |
| Type a password | `SetValue` from an environment variable (`UIINSPECTOR_SECRET_1`…); the password is never read |
| Press Enter, Escape, F-keys, shortcuts (Ctrl+S, Alt+F4), arrows/Delete outside text fields | A key press on the focused element (repeats are counted: "Down ×3") |
| `Ctrl+Shift+F7` over an element | A check: the text it shows now must match, or (no text) it must exist |

Stop with the same button or `Ctrl+Shift+F8`. Pick a language (FlaUI, uiautomation, pywinauto,
Appium, and for steps inside a web page also Playwright JavaScript/Python/C# and Selenium
Python/Java), then **Copy script** or **Save…**. **Delete step** removes a step you didn't mean to
record. Recording again adds steps to the end. Every step finds its element from the desktop
again and waits up to 10 s, so dialogs and windows that open along the way are waited for.

How it works, and what it doesn't capture:

- Each click is resolved to its element **the moment the button goes down**, before the app reacts,
  and a **snapshot of the window's whole element tree** is taken at the same moment (about 65 ms
  for Discord's ~800 elements). The selector is then built and verified against that snapshot, so
  a click that closes its own popup or dialog gets the same quality of selector as any other: the
  popup's content still tells its buttons apart. Key steps (Escape closes popups too) work the same
  way. Without a snapshot, the step is checked live and marked **unverified** if its element is gone.
- That work starts **straight from the input hook**, on a worker of its own, so a quick click never
  waits behind the previous step's screenshots and checks (about 0.3 s per click in Discord); by
  then the app could already have closed what was clicked.
- **Typed text is never captured as keystrokes.** The keyboard hook reduces every printable key to
  "something was typed"; the text comes from the field itself via UI Automation, when focus
  leaves it or the next action happens. Password fields are never read.
- Drags, scrolling, mouse wheel and Tab aren't recorded: scripts find elements without scrolling,
  and target fields directly instead of tabbing to them. Clicks on window backgrounds, title
  bars, scrollbars, the taskbar and the inspector itself are ignored.
- The mouse and keyboard hooks exist only while recording. Input from automation scripts
  (including generated ones) is ignored.
- Live pick (Ctrl-hover) pauses while recording, so shortcuts like Ctrl+S don't pick elements.

### Replay: check a recording before using its script

**▶ Play** in the Recorder tab plays the steps back in the app, the way the FlaUI script would:
each step's selector is searched from the desktop (waiting up to 10 s), then the same action is
performed, a UIA pattern call (Invoke, Toggle, SetValue…) where the element has one and the real
mouse or keyboard only where the script uses them too. Each step is highlighted as it runs and
gets a line under it: *Passed*, *FAILED* with the reason, or *Skipped* (no selector, as in the
scripts). The replay stops at the first failure, like the scripts do.

- Failures say **which part of the selector found nothing** (*Nothing matches Pane containing
  Text "Order 1042" in the window*), that the window isn't open, that a position no longer exists
  (*Only 2 elements match…; the step wants #3*), or what a check got instead (*Expected "Saved",
  but it shows "Error"*). When several elements match, the first is used and the step says so.
- **Report this failure** opens the Bug report tab with the steps up to the failed one, the
  window as it was at the failure (with the element outlined, if it was found), and the title,
  expected and actual result filled in where they were still empty. *Use all recorded steps* goes
  back to the whole recording.
- **Text steps are read back.** Some fields accept "set your text" from UI Automation without
  taking it (rich editors) or refuse it; replay then types the text with the keyboard and marks
  the step *Passed, with a warning*, since the scripts set it directly there and may fail. If
  the field still doesn't show the text, the step fails with what it shows. A step whose text
  couldn't be read while recording fails with that reason instead of typing nothing.
- Passwords aren't in the recording, so Play asks for each one; it's only kept for that replay.
- **■ Stop replay** or Ctrl+Shift+F8 stops it. A short pause before each step makes it easy to
  follow; otherwise it runs at the script's pace.

### Bug report: from a recording to a report a developer can act on

Record the steps that lead to the bug, then open the **Bug report** tab. Fill in the title,
severity, expected and actual result (and notes, if any); the steps, screenshots and environment
come from the recording. The preview on the right shows exactly what **Save HTML** writes and
updates as you change anything.

Every part has its own check box:

| Part | Default | What it adds |
| :--- | :--- | :--- |
| Steps to reproduce | on | Numbered steps in plain words: *Type “ada” into the “User” field.*, *Click the “Sign in” button.* Passwords appear as "Type the password into…" |
| ↳ Screenshots | on | The app's window at each step. Untick single steps under **Screenshots per step** if they show something private |
| ↳ Outline the element | on | A red frame around the element each step acted on |
| Expected and actual result | on | Side by side; flagged if not filled in |
| Severity, Notes | on | Shown when set |
| Environment | on | With its own boxes for Windows version, app name and version, app install path (off by default), screens and scaling, language, date and time |
| Element details | off | Each step's verified selector, for developers |
| Automation script | off | The script that replays the steps, in the chosen language |

**Save HTML…** writes one file with everything embedded (ready to attach or e-mail).
**Save Markdown…** writes a .md file plus a folder with the screenshots. **Copy as text**
puts Markdown without images on the clipboard, for Jira, Azure DevOps or GitHub.

Screenshots show only the app's own window (it's asked to draw itself), never the screen, so
other apps and overlapping windows don't appear. Switch them off with **Screenshots** in the
Recorder tab. The preview files are kept in a temporary folder that's deleted when the app closes.

### Test plan: what to test, and how

The **Test plan** tab looks at a window or web page and suggests what to test. Pick it with
**Analyze window under cursor (3 s)**, or **Analyze picked element's window** for the element
selected in the Inspector tab. Nothing in the app is clicked: the window is read in one go
(about 1.5 s for a job site's page). Two views come out of that one read: **What to test**
(below) and **Testability** (the next section).

It finds the features from the page's structure, by control types, page landmarks and labels in
English and Serbian/Croatian (and some German, French, Spanish):

| Feature | Found by |
| :--- | :--- |
| Login / Registration | A password field with the fields and button next to it; two password fields make a sign-up |
| Search | A search box (label, role or search landmark), even with drop-downs next to it |
| Form | Fields that share a section with a button |
| Filters | Check boxes, options and drop-downs outside forms; buttons with counts (*Programiranje [77]*) |
| List | Many look-alike links or items with names of their own (job titles, not a row of "Save" buttons) |
| Paging, Tabs, Menus, Navigation, Dialogs | Page numbers and Next; tab items; menus and drop-down buttons; the navigation landmark's links; dialogs |
| The whole page or window | Always: keyboard only, screen reader, zoom, phone widths, refresh and Back, other browsers… |

Each field is sorted by what it's for (email, password, phone, number, date, URL, name, free
text, search, choice, check box), and gets **its own data table of test cases**: for a password,
only symbols `!@#$%^&*()`, only digits, one shorter than the minimum, exactly the minimum, 1,000
characters, only spaces, spaces at the ends, accents and Cyrillic, emoji, a common password, SQL
injection, a script tag, hidden while typing, can't be copied out, can be pasted in, Caps Lock.
Emails get missing @, two @, no domain, plus addressing, 254/255 characters; numbers get 0, −1,
the limits ± 1, 1.5 and 1,5, huge numbers, letters; dates get 29 February in leap and normal
years, 31 April, other formats; and so on. Features add their own cases (a login: wrong
password and unknown user give the same message, locked after repeated failures, still logged
in after refresh, logged out after Back…). A login on a web page comes to about 60 cases.

Every case has an id (`LOGIN-07`), a kind (works, bad input, limits, security, accessibility,
usability), a priority, steps that name the controls found (*Type the data below into the
“Email” field*), the data, and the expected result. Where only the tester can know the rule
(the minimum length, the exact message), the case says so instead of guessing. Filter the
table by priority, kind or "worth automating".

- **Select a row** to see its steps, the exact value in a box you can select, and buttons to
  copy the value, the steps or the whole case (also by right-click, and Ctrl+C copies the value).
  The value is exactly what to type: "1,000 × the letter a" copies the 1,000 characters, a value
  shown in quotes copies its spaces without the quotes. Values only the tester can choose (their
  own minimum length) copy as their description.
- **The control is outlined in the app**, like a Ctrl pick, while its row or feature is selected:
  a field further down the page is scrolled into view first. Leaving the tab removes the outline.
- **Security cases say what a failure means**, under "If it fails". An alert from
  `<script>alert('x')</script>` is harmless in itself, but proves the page runs code users type
  (cross-site scripting): put in a link or a saved field, an attacker's code would run in other
  people's browsers while they're logged in. The texts give the risk and how serious it is, for
  XSS, SQL injection, `javascript:` links, user enumeration ("no such user" against "wrong
  password"), unlimited login attempts, passwords in the address bar, and more.
- **A login isn't a sign-up.** Password strength and email format rules apply where a password
  or address is *created*; at login every wrong value just gets the same general message, so the
  login's field cases expect exactly that.

Only test security on apps you're allowed to test (your own, or with the owner's permission).
If you find something on someone else's site, report it to them (many have a security contact or
a `/.well-known/security.txt`) rather than testing further.

**How:** each feature's main flow comes as a script in every language the recorder writes
(Playwright and Selenium for web pages in Edge, Chrome, Electron apps and Firefox, without a
debug port; FlaUI, uiautomation, pywinauto, Appium for any app), put together from the same verified selectors: fill the fields with
sample data, the password from an environment variable, click the button. The other cases run
by changing the typed values. Features with fragile controls say so.

**Test suites: every case, run automatically.** For each feature with fields (and for a web
page's navigation and lists), the script list starts with data-driven test suites:

| Suite | For | Runs with |
| :--- | :--- | :--- |
| Playwright (Python, pytest) | Web pages in Edge, Chrome, Firefox | `pytest login_test.py -v` |
| Playwright Test (JavaScript) | Web pages | `npx playwright test login.spec.js` |
| Playwright (C#, NUnit) | Web pages | `dotnet test` in a test project |
| Selenium (Python, pytest) | Web pages | `pytest login_selenium_test.py -v` |
| Selenium (Java, JUnit 5) | Web pages | `mvn test` (Java 11+) |
| Python uiautomation (pytest) | Desktop apps | `pytest login_test.py -v`, with the app open on the form |
| FlaUI (C#, xUnit) | Desktop apps | `dotnet test` in a test project |
| pywinauto (Python, pytest) | Desktop apps | `pytest login_test.py -v` (Pillow for screenshots) |
| Appium (Python, pytest) | Desktop apps | `pytest login_test.py -v`, with an Appium server and a Windows driver |

Selenium can't see the server's answers, so its suites catch server errors only through error
text on the page; it can't type emoji into Chrome or Edge either, so those values are set through
JavaScript with an input event, as a paste would. Saving a Java or C# suite names the file after
its class, as Java requires.

Each suite holds the feature's cases as a table (id, title, field, exact value, expected) and runs
them one by one: a fresh page (web), valid data in every field, the case's value in its field,
then send. After each case it **checks automatically what can be checked without knowing the
app**, and fails the case if:

- an alert appeared, or a typed `<script>` is in the page as HTML: the page runs code from the
  input (XSS);
- the page shows database or crash text (`SQL syntax`, `SQLSTATE`, `ORA-…`, `Unclosed quotation
  mark`, stack traces, `Internal Server Error`…): input reached the database unchecked, or the app
  crashed and shows its insides;
- the server answered with an error (5xx);
- the password appears in the address;
- (desktop) the window disappeared, or an error dialog opened.

Every result is saved as a screenshot (`results/<case id>.png`). Whether the app gave the *right*
answer ("refused with a message", "logged in") depends on the app, so each case carries its
expected result and the suite has an empty `check_expected()` where you add checks once you know
your app's messages. Web login suites also check that the password field hides what's typed, and
(with `UIINSPECTOR_USER` set to a real test account) that an unknown user and a wrong password get
the same message. Navigation and list suites click each link from a fresh page and fail on error
pages, 4xx/5xx answers and "not found" text. Cases that need your app's own values (the minimum
password length) are listed at the top of the suite to do by hand. `HEADLESS=1` runs the web
suites without a window. Long values are written as code (`"a" * 1000`).

**A script for each case.** The script box follows the table: with a test case selected, it shows
that case's own script in each suite language (open, fill, type that case's value, send, the
automatic checks, a screenshot), named after the case when saved (`login_04_test.py`). Cases a
script can't judge (keyboard order, screen reader, zoom) or that need your app's own value (the
minimum password length) show their steps and expected result instead. "Password is hidden while
typing" and "User that doesn't exist" bring their own checks. **Whole feature** goes back to the
suite with every case.

Only run the security cases against apps you're allowed to test.

**Save HTML…** writes the whole plan as one page; **Save CSV…** writes one row per case (UTF-8,
steps numbered in one cell, values like `-1` protected from Excel's formula handling), which
opens in Excel and imports into TestRail, Xray, Zephyr and similar tools; **Copy as Markdown**
gives tables for a wiki or ticket.

These are suggestions from patterns, not from understanding the app: review them, drop what
doesn't apply, and fill in the app's own rules.

### Testability: what the developers should change

The **Testability** view grades every control of the same window for test automation, and
writes a report the developers can act on. Each control's selector is built and checked
against the copy of the window (17 controls in about 1.5 s; a few hundred take a few seconds).
It starts with **In short**: the three changes that help most, in plain words.

Each control gets a grade from the best selector the generator can build for it:

| Grade | Means | Example |
| :--- | :--- | :--- |
| Good | A stable, unique id finds it, whatever the language, data or layout | `saveButton` |
| Works, with caveats | Found by its text, text near it or its type: works, but that can change | *Button "Delete" inside the Pane containing "Order 1042"* |
| Fragile | Only its position tells it apart, or nothing finds it | the 2nd of 3 identical "Action" buttons |

It also flags, whatever the grade: ids that change between runs (React's `useId`, hashes,
GUIDs), ids shared by several controls, and controls with **no accessible name**, which screen
readers can't announce either. Each finding comes with **a fix in the app's own technology**:
`AutomationProperties.AutomationId="ordersDeleteButton"` for WPF and WinUI, the control's `Name`
for WinForms, a fixed `id` or `data-testid` for web pages, `setObjectName` for Qt, a unique
control ID for Win32. Suggested ids come from the control's name, kind and container; rows that
repeat get "add the row's key to the id", and identical controls "each need their own id".

The report starts with a **score** (good controls count fully, those with caveats half), then
**what to fix first** (e.g. *12 of 17 controls need a stable, unique id*; *5 are only found by
their position*), the window with every control outlined in its grade's color and numbered,
and each control to improve with how it's found now, why that's fragile and the fix.
Selecting a control in the list outlines it in the app. **Save HTML…** writes one file,
screenshot included; **Copy as text** gives Markdown for a ticket.

- **Grouped, not one long list.** Controls with the same grade and problems, the same kind,
  built the same way (same parent and grandparent, same page landmark) and found the same way
  whatever their text are one entry with one fix: *25 links like “Cloud Data Inženjer”, “C++
  Developer”…, in the main content*. The app lists the groups; selecting one shows its controls
  below, and selecting a control outlines it in the app. The report numbers them as on the screenshot.
- **Where each one is:** its nearest named section, or the page landmark (*in the navigation*,
  *in the footer*, *in the main content*).
- In a browser (Edge, Chrome, Firefox…), only the page is graded, not the browser's toolbar: the
  page that holds the element pointed at, otherwise the visible one. (Browsers keep other tabs'
  pages in the tree too, so a YouTube tab could otherwise end up in a job site's report.)
  Chromium marks its pages with the AutomationId `RootWebArea`; Firefox shows each tab's page as a
  plain Document (background tabs off-screen), and its toolbars and menus are never Documents.
  Firefox also reports an element's whole CSS class list as its ClassName, so web class rules
  (class lists and utility classes aren't identity) apply to it as they do to Chromium.
- **Icon-font names** (a button "named" by one icon glyph) count as no name: screen readers
  can't read them, and the text shown leaves the glyphs out.
- Title bars and scrollbars are left out. In long lists, the first 3 items of a kind are graded
  and the rest are counted (*"+40 like it"*).
- Up to 5,000 elements are read and 400 controls graded per window; the report says when a
  window had more.

## Console tool

```bash
dotnet run --project src/UiInspector.Cli
```

This starts **interactive mode**. Leave the console open, switch to any app and use the hotkeys:

| Hotkey | Action |
| :--- | :--- |
| `Ctrl+Shift+F9` | Inspect the element under the mouse cursor |
| `Ctrl+Shift+F10` | Inspect the element with keyboard focus |
| `Ctrl+Shift+F11` | Delayed capture (3 s): open a menu or tooltip, then wait |
| `Ctrl+Shift+F12` | Toggle hover tracking (one line per element you move over) |
| `Ctrl+Shift+F8` | Start/stop recording; each step is printed as it's recorded, the script on stop |
| `Ctrl+Shift+F7` | While recording: add a check for the element under the cursor |
| `Ctrl+C` | Quit (in the console window; stops recording first) |

To record from the start and save the script to a file:

```bash
dotnet run --project src/UiInspector.Cli -- --record --lang pywinauto --out recorded_test.py
```

On laptops you may need to hold `Fn` for the F-keys. If another app already uses a hotkey, the
tool says so at startup and the other hotkeys still work.

### One-shot mode (scripts)

```bash
dotnet run --project src/UiInspector.Cli -- --point 800 450
```

```bash
dotnet run --project src/UiInspector.Cli -- --focused --no-ancestors
```

Add `--code` to also print the verified selector and the code for every framework (plus
Playwright/Selenium for web elements; `--cdp-port N` overrides the detected debugging port):

```bash
dotnet run --project src/UiInspector.Cli -- --point 800 450 --code
```

Run with `--help` for all options. Exit codes: `0` found, `1` no element, `2` error or timeout, `64` bad arguments.

### Build a standalone .exe

```bash
dotnet publish src/UiInspector.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The output `uiinspect.exe` (in `src/UiInspector.Cli/bin/Release/net10.0-windows/win-x64/publish/`)
runs on machines without .NET installed.

## Tests

```bash
dotnet test
```

`dotnet test` only rebuilds what the tests depend on. Run `dotnet build` before trying the CLI exe directly.
Quit any running `uiinspect` first (`Ctrl+C`). Windows locks the files of a running program, so the build can't replace them.

## Project layout

| Path | Contents |
| :--- | :--- |
| `src/UiInspector.Core` | Shared engine: `UiaWorker` (dedicated MTA thread, timeouts, replaces itself if a target app hangs), `ElementInspector` (cached property reads, ancestor walk, children), `InspectionService` (waits out Chromium's lazy tree), `SmartTarget`, `HotkeyListener` (`RegisterHotKey`, no input hooks), `FailureMessages`, `ScreenInfo`, `WindowResponsiveness` |
| `src/UiInspector.App` | WPF GUI: highlight overlay, element tree (lazy-loaded), details panel, live pick |
| `src/UiInspector.Cli` | Console front end: argument parsing, output formatting |
| `src/UiInspector.Core/Selectors` | `AttributeStability` (which values can be trusted), `SelectorGenerator` (build, scope and live-verify selectors; click check), `CodeTemplates` (FlaUI, uiautomation, pywinauto, Appium) |
| `src/UiInspector.Core/Web` | `CdpSession` (minimal DevTools Protocol client), `DebugPort` (finds `--remote-debugging-port` in the process tree), `PageScript` (in-page locate/count script), `WebBridge` (UIA element → DOM node, Playwright/Selenium locators, verification), `WebCodeTemplates` |
| `src/UiInspector.Core/Recording` | `InputHook` (low-level mouse/keyboard hooks, only while recording), `KeyClassifier` (decides inside the hook what a key means; printable keys become "typed"), `TextTracker` (what was typed where, from field values), `StepBuilder` (double-clicks, drags, merging), `Recorder` (resolves each click at mouse-down on its own UIA worker, generates selectors in the background), `RecordingScripts` |
| `src/UiInspector.Core/Reporting` | `WindowCapture` (PrintWindow of one window; element outlines), `EnvironmentInfo` (Windows version, app versions, monitors), `BugReport` (options, step wording), `BugReportRenderer` (HTML, Markdown, text) |
| `tests/UiInspector.Core.Tests` | Unit tests, plus integration tests (`Category=Desktop`) against `Fixtures/selector_fixture.ps1` (WinForms window with duplicate captions, identical buttons, HWND-based IDs, a check box and a password field) and `Fixtures/web_fixture.html` (opened in Edge: test id, status-in-name links, duplicate captions with hashed classes, labelled input, iframe, Shadow DOM). Both open off-screen. The web tests need Microsoft Edge. The recorder tests feed it element-level events instead of real input, so they never move the mouse or type |

### Generated code: what has been run

| Framework | Status |
| :--- | :--- |
| FlaUI (C#) | Compiled and run unedited against the fixture for all three selector shapes (ID, anchored name, anchored index); each clicked exactly the intended button |
| Python `uiautomation` 2.0.29 | Same three cases, all passed |
| pywinauto 0.6.9 | Same three cases, all passed |
| Appium (Windows driver) | Generated and unit-tested, **not run**: needs an Appium server with the Windows driver |
| Playwright 1.64 (JavaScript, Python, C#) | Run unedited against the web fixture in Edge 156 for six cases (test id, status-in-name link, anchored duplicate, input fill, iframe, Shadow DOM): 18/18 acted on exactly the intended element; the app kept running afterwards |
| Selenium 4.52 (Python) | Same six cases, 6/6 passed |
| Selenium (Java) | Generated, **not run**: this machine has Java 8, and Selenium 4 needs Java 11+ |
| Recorded scripts (FlaUI, uiautomation, pywinauto) | A recorded 6-step workflow (text, password from an environment variable, check box, button, anchored duplicate button, text check) run unedited against a fresh fixture: 3/3 left it in exactly the recorded state and passed their check. Key and mouse steps were checked by unit tests only, since running them would type into or click on whatever is in front |
| Rows found by their text (FlaUI, uiautomation, pywinauto) | The Delete button of "Order 1042", in a list of rows with no IDs and identical buttons, run against the fixture (3/3), then against a later version of it with a new row on top and the rows reordered: 3/3 still deleted Order 1042. The old position-based selector deleted Order 1043 there, without any error |
| Web rows found by their text (Playwright JS/Python/C#, Selenium Python) | The Delete button of "Order 1042" in React-style rows (no ids, hashed classes), run against the page and against a later version of it (new row on top, rows reordered): 8/8 deleted Order 1042. A position-based locator deleted Order 1043 on the later version, without any error. The earlier 24 web cases still pass |
| Web code without a debug port | Locators built from the accessibility tree for the 8 web fixture cases (including an unnamed clickable tile), with each script opening its own (headless) Edge: Playwright JS/Python/C# 24/24, Selenium Python 7/8. The miss is the Shadow DOM button: Selenium's XPath can't look inside Shadow DOM, and without a port the inspector can't tell it's there (Playwright handles it). A recorded web workflow (fill, delete order 1042, check): 4/4. Scripts that start the app with a port themselves, with a stand-in Electron launcher: 4/4; with "the app" already running without a port, the script stops after 4 s with the "quit it completely" message. The 24 debug-port cases still pass |
| Test suites in the other languages | Run for real on the same fixtures: Playwright C# (NUnit 4.3, Playwright 1.55) and Selenium Python (4.52, Edge driver) gave the same verdict as the Playwright suites (26 passed, the two planted XSS cases failed), the emoji cases included; pywinauto 15/15 on the WinForms sign-in panel (the first run caught `exists()` called on the found window, which only the window's specification has; fixed). Playwright C# compiled without warnings after removing a constant that could be null. **Not run here**: Selenium Java (Java 8 on this machine; Selenium 4 needs 11+; read through instead) and Appium (no Appium server; compiles with `py_compile`) |
| Test suites | Generated from the test fixtures and **run for real**. Web (the fixture page in headless Edge): the Playwright Python suite with pytest 9.2 and the Playwright Test suite (JavaScript, 1.56): login 14/14 plus the hidden-password check (the same-message check skipped without a real account), and a deliberately unsafe comment form (inserted with `innerHTML`) failed exactly on its two XSS cases: the `<img onerror>` alert, and the `<script>` in the page as HTML; 25 passed, 2 failed, as intended. Desktop (the WinForms fixture's sign-in panel): the uiautomation suite 15/15 with pytest, the FlaUI suite compiled with FlaUI 5.0 and xUnit 2.9 and passed 15/15, 15 screenshots each, through UI Automation only (no real mouse or keyboard). Compiling the FlaUI suite caught an ambiguous `Capture` (FlaUI's against .NET's regex class), fixed |
| Test plan in Firefox | The open Firefox window, read in memory (only structure printed): every feature with a script got all 9 languages; Playwright opens `p.firefox.launch(headless=False)` at the page's https address read from Firefox. Templates for Firefox checked per language by unit tests. **Not run**: the Firefox scripts themselves (Playwright's Firefox build isn't installed here) |
| Test plan | The web fixture in Edge: the sign-in form found as a login with ~60 cases, its Playwright script fills `#login-user`, reads the password from `UIINSPECTOR_SECRET_1` and clicks *Sign in*; the search box found as a search. Feature detection (login, sign-up, search, filters, paging, navigation) and field classification (English and Serbian labels) unit-tested on made-up pages; CSV checked for Excel safety. A real job site (helloworld.rs, throwaway Edge, nothing clicked): search, filters, two lists (job titles, companies), paging, navigation and the page, 80 cases in 1.5 s. In the GUI, driven through UI Automation: features listed, cases shown and filtered, script shown. **Not run here**: the generated scripts against a real site, and desktop apps other than the fixture |
| Testability | The WinForms fixture: 17 controls graded in about 1.5 s, the expected grade for each case (unique id, panel-scoped duplicate, identical buttons, text-anchored rows, unnamed popup items, unnamed password box), fixes per technology and suggested ids checked by unit tests. The report viewed in a browser. The web fixture in Edge: only the page graded, the React search box flagged for its changing id with a `data-testid` fix. In the GUI, driven through UI Automation: a window picked in the tree, graded, summary and list filled, preview files deleted on exit. A real job site (helloworld.rs, in a throwaway Edge, nothing clicked): 77 controls in 1.1 s, 23 groups (header, job list and footer apart), Tailwind classes no longer used, icon-only buttons flagged. Choosing the page in a browser with a background tab: unit-tested with a made-up tree. **Not run here**: desktop apps other than the fixture |
| Bug report | A report built from the recorded 6-step workflow, rendered in Edge: every screenshot shows the window as it was at that step (typed text, password dots, check box before and after). Each option checked by tests to add or remove its part. In the GUI, driven through UI Automation: title and severity reach the preview, unticking a part removes it, the preview folder is deleted on exit |
| Clicks that close their popup | A popup with an OK button that closes it, next to another OK: checked against the snapshot taken at the click, its selector is "OK inside the Pane containing *Confirm delete?*", verified. Checked live after the click, the two can't be told apart |
| Unnamed items in a popout | Like Discord's "View profile": an unnamed button, 6 levels deep in a menu that is one of 121 unnamed groups. Checked against a click snapshot: "Button #2 inside the Group containing *Profile menu*". Checked live (where the search stays near the element), it falls back to its position in the whole window |
| Discord (real app, no setup) | A recorded 4-step Playwright script started Discord with a debug port through `Update.exe` and attached. Steps 1–2 ran; step 3 relied on a text that only appears while hovering, which led to the click snapshot above |
| Replay | Against its own fixture window, with pattern actions only (text, button, anchored duplicate button, text check): all passed and left the app in the recorded state. A failing value check stopped the replay with "Expected …, but it shows …" and a screenshot, the next step left unplayed. A renamed button and a closed window got "Nothing matches Button [AutomationId = …] in the window" and "isn't open". Text typed into a web field in Edge (no debug port): the page's own input handler saw it. In the GUI: the Play button shows and is off until there are steps. **Not run in the GUI**: a whole record → play → report-the-failure round, since recording needs real input; mouse and keyboard steps are not replayed in tests either |
| Recorded scripts (Appium) | Generated and unit-tested, **not run** (no Appium server) |
| Recorder with real mouse and keyboard | **Not run here**: the hook was installed and removed in a test, but recording real input would have captured the person using this computer. Try it on your apps |

## Notes from testing (useful for later phases)

- **Unnamed WinForms controls report their window handle as AutomationId** (e.g. `132646`). It looks like a stable number but changes every run, so selectors never use it.
- **Some apps give every window the same AutomationId** (Firefox: `main-window`). The generator then adds the stable part of the title, and if that's still ambiguous it says so.
- **Win32 controls report numeric AutomationIds** (Notepad's editor is `15`, its control ID). Selectors need ClassName + Name, as the spec predicts.
- **WinForms ClassNames are randomized per build** (`WindowsForms10.BUTTON.app.0.3a8ddef_r41_ad1`). Never use them verbatim in selectors.
- **UWP apps span two processes.** Calculator's outer window belongs to `ApplicationFrameHost`, its buttons to `CalculatorApp`. Attaching by process must pick the right one.
- **Chromium/Electron apps build their accessibility tree lazily.** The first query often hits an empty `Document #RootWebArea`. The inspector now detects this and retries for up to 3 s by itself: a fresh Edge window resolved to the real button in about 0.5 s from one press. If the page stays empty, it suggests restarting the app with `--force-renderer-accessibility`.
- **Chromium maps the HTML `id` to the UIA AutomationId** (`<button id="go-button">` → `go-button`).
- **Hung windows make UIA fail fast with a misleading error** (`0x80070002`, "file not found"). The tool detects the hung window and explains this instead.
- Framework boundaries (Win32 → XAML, Win32 → WinForm, Win32 → Gecko) are flagged in the ancestor chain. This is the groundwork for spec §7.3.
- **CDP `Accessibility.queryAXTree` never answers while a page isn't being rendered** (window covered, off-screen, or behind the inspector). Role locators are therefore verified element by element with `getPartialAXTree`, which always answers.
- **Searching the UIA tree doesn't wake Chromium's accessibility; a hit-test does.** Tools that only search (no point-picking) may see an empty page.
- **A fresh Edge profile opens a "syncing your data" page** (Windows account sign-in). `--disable-sync` prevents it.
- **Iframe content shows up in UIA as a nested `Document #RootWebArea`.** The web bridge uses the outermost one as the page.
- **WinForms check boxes support both Invoke and Toggle.** Code uses Toggle for check boxes, since it says what the step does.
- **A WinForms TextBox takes its Name from the label before it** (the fixture's notes box is named "Status: 3 items"). Its AutomationId is what identifies it.
- **Clicks change the UI faster than a selector can be checked.** Discord starts closing a popup as soon as the mouse button comes up, while checking a selector live takes several searches. Before snapshots, every click that closed a popup lost the popup as an anchor and fell back to a position.
- **Chromium answers a hit-test at a new point with a guess from its cache**, then works out the precise element in the background (~15 ms); only the next question gets it. In Discord's profile window the first answer for the X button was the whole content panel. The recorder asks at the moment the button goes down, waits 20 ms, and uses the second answer, which describes the page as it was at the click; all before a fast click's button comes up (~80 ms) and the app changes the page. (Asking only after the tree snapshot was too late for fast clicks; warming the cache by hit-testing where the cursor rests made it worse, since a cached big container "contains" every later point.)
- **Quick clicks used to queue up.** Each click's hit-test, tree snapshot and screenshots ran one after another (~250–300 ms in Discord), so in a fast sequence the last click (closing a profile window) was resolved after the window had faded, landing on the backdrop. Recording with the window already open didn't show it, since that click was first in line. Chromium's positions were checked and were right.
- **FlaUI's caching tree walker throws instead of returning null** when there's no previous sibling; that walk runs without a cache.
- **A busy app can drop out of a single tree search**: under load, one selector check right after a recorded click found no window. The recorder retries once before falling back to an unverified selector.
- **Window screenshots have two modes, each blind in one case.** The compositor's copy (`PW_RENDERFULLCONTENT`) is the only one with Chromium/WPF/WinUI content, but Windows never repaints the parts of a window that are off every monitor, so there it's old or empty. Asking the window to paint itself (plain `PrintWindow`) is current for classic windows but black for DirectX content. Captures use the first for windows fully on screen and the second otherwise, each falling back to the other when the result is blank or mostly black.
- **A maximized window reaches past the screen.** Its outer rectangle includes invisible borders a few pixels beyond the monitor, so it looked "partly off-screen", got the paint-yourself capture, and a maximized Edge came out black with only the outlines. The on-screen check now uses the visible frame, and a mostly black capture also tries the other mode.
- **Windows' own programs name the OS as their product** ("Microsoft® Windows® Operating System"), so reports use their file description ("Notepad") instead.
- **Chromium doesn't always show UI Automation the same tree.** Depending on its accessibility mode it exposes plain `<div>`s (as groups, with their classes) or leaves them out, so rows of a list may exist as elements or their contents may sit side by side. Locators handle both: "the innermost element holding *Order 1042* and a Delete button" (`filter({ has })`), or "the first Delete button after the text *Order 1042*" (`following::`).
- **Chromium only updates its accessibility tree while it renders**, and it pauses rendering for windows it thinks are hidden, so off-screen test windows need `--disable-backgrounding-occluded-windows --disable-renderer-backgrounding`. Apps on screen aren't affected.
- **Chromium's document element carries the page URL** as its UI Automation value, which is how scripts know where to go without a debug port.
- **On web pages, "the div containing this text" isn't specific enough**: every outer wrapper `<div>` contains it too. The locator starts from the element showing exactly that text and goes up to its nearest container (`ancestor::div[1]`).
- **Chromium can leave plain `<div>`s out of the UIA tree**, so the UIA parent of a label isn't necessarily its row.
- **Web pages styled with Tailwind or Bootstrap have class names like `flex-1`, `p-4`, `d-flex`.** They describe one bit of styling and change with any redesign, so selectors no longer use them (helloworld.rs's job cards were found "inside Group .flex-1"). Real class names that merely start the same way (`text-editor`, `content-wrapper`) are still used.
- **A busy PC can make one click's read take more than 3 s.** The recorder used to drop that click with a "did not respond" message (seen in a test run under load). It now tries once more with 10 s; only a frozen app loses the step.
- **An off-screen test window can still take the keyboard.** A WinForms window activates itself when it opens, so text typed at the computer during a test run landed in it, unseen (a replay test once read "Oh What the app shows"). The fixture now opens without activating (`ShowWithoutActivation`).
- **Parallel test classes opening the same page see each other's windows.** Two Edge fixtures with the same page title made the window step ambiguous, and a replay typed into the other class's Edge ("3 windows match; the first one was used"). Each fixture now sets its own title.
- **React makes up element ids at runtime** (`useId`: `:r1a:` in React 18, `«r1»`, `_r_b3r_` in React 19; Discord's friend search box had `_r_b3r_`). They look like real ids, but change when the app restarts or redraws, so a replay a minute later found nothing. They're rejected now, along with MUI, Angular Material, Ember, jQuery UI, Ext JS and React Aria counters; the field is found by its label instead.
- **Discord's popouts sit among 600+ unnamed groups**, and their items are several wrappers deep. Live searches only look 4 levels up and give up past 100 look-alikes (each check is a UIA round trip); a click snapshot is searched in memory, so it looks at every container. A nameless WinForms GroupBox borrows a nearby label's text as its name, which the fixture avoids with a blank title.
- **The .NET Framework UI Automation client (`System.Windows.Automation`) doesn't see WinForms AutomationIds**, unlike UIA3 (FlaUI, uiautomation). Tools built on it see a different tree.
- Mixed-DPI monitors haven't been tested yet (the development machine has one 100 % monitor).

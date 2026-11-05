# UiInspector — Windows UI element inspector

Reads any Windows app's UI through UI Automation (UIA) and shows each element's properties,
patterns and position in the UI tree, then generates a **verified selector** and ready-to-paste
code for FlaUI (C#), Python `uiautomation`, pywinauto and Appium. For elements inside Chromium
pages (Electron, WebView2, Chrome, Edge) it also generates **Playwright** (JS, Python, C#) and
**Selenium** (Python, Java) code from the real DOM. A **recorder** turns a workflow you click
through into a complete script. This covers Phases 1–4 and the recorder from Phase 5 of
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
3. Last resort: its position among the matches, clearly marked as fragile.

Attributes that would break are rejected or flagged with a reason: generated hashes
(`content_d125d2`), GUIDs, WinForms per-build class names, window handles used as IDs, designer
default names (`textBox17`), and live status text inside names (only `Dyno (direct message)` is
used from `Dyno (direct message), Online`). A **click check** says whether a real mouse click at
the element's center would reach it, or would hit another window, an overlay, or nothing
(off-screen). Generated code prefers pattern actions (Invoke, Toggle, SetValue…) over mouse clicks.

Other verified selectors for the same element are in the drop-down above the steps.

### Web apps: Playwright and Selenium code

For an element inside a Chromium page, the inspector connects to the app over the **Chrome
DevTools Protocol** and reads the page itself: `id`, `data-testid`, ARIA role and accessible name,
iframes and Shadow DOM. This needs the app to run with a remote-debugging port. Quit the app
completely first, then start it like this:

| App | How to start it |
| :--- | :--- |
| Electron apps (VS Code, Slack…) | `app.exe --remote-debugging-port=9222` |
| Discord | `"%LOCALAPPDATA%\Discord\Update.exe" --processStart Discord.exe --process-start-args "--remote-debugging-port=9222"` |
| WebView2 hosts (new Outlook, LOB apps) | set the environment variable `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, then start the app |
| Chrome / Edge | `msedge.exe --remote-debugging-port=9222 --user-data-dir=C:\temp\edge-debug` |

The port is read from the app's command line automatically; type one into **Debug port** to
override it. Without a port, the Selector tab explains how to get one.

> **Security:** while the port is open, any program on this computer can fully control the app
> (read its pages, run script). It only listens on `127.0.0.1`, but close the app when you're done.

The web locators follow Playwright's own preferences: `getByTestId` > `getByRole(role, { name })` >
`#id` > stable CSS classes, anchored to a named region or list when needed (*button "Delete" inside
region "Orders"*), and an index only as a last resort. Each one is **verified in the live page**:
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
Appium), then **Copy script** or **Save…**. **Delete step** removes a step you didn't mean to
record. Recording again adds steps to the end. Every step finds its element from the desktop
again and waits up to 10 s, so dialogs and windows that open along the way are waited for.

How it works, and what it doesn't capture:

- Each click is resolved to its element **the moment the button goes down**, before the app reacts.
  A selector is then generated and verified in the background. If the element is already gone
  (its dialog closed), the step gets a selector built from the snapshot and is marked
  **unverified**.
- **Typed text is never captured as keystrokes.** The keyboard hook reduces every printable key to
  "something was typed"; the text comes from the field itself via UI Automation, when focus
  leaves it or the next action happens. Password fields are never read.
- Drags, scrolling, mouse wheel and Tab aren't recorded: scripts find elements without scrolling,
  and target fields directly instead of tabbing to them. Clicks on window backgrounds, title
  bars, scrollbars, the taskbar and the inspector itself are ignored.
- The mouse and keyboard hooks exist only while recording. Input from automation scripts
  (including generated ones) is ignored.
- Live pick (Ctrl-hover) pauses while recording, so shortcuts like Ctrl+S don't pick elements.

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
- **A busy app can drop out of a single tree search**: under load, one selector check right after a recorded click found no window. The recorder retries once before falling back to an unverified selector.
- **The .NET Framework UI Automation client (`System.Windows.Automation`) doesn't see WinForms AutomationIds**, unlike UIA3 (FlaUI, uiautomation). Tools built on it see a different tree.
- Mixed-DPI monitors haven't been tested yet (the development machine has one 100 % monitor).

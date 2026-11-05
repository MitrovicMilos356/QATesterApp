# UiInspector — Windows UI element inspector

Reads any Windows app's UI through UI Automation (UIA) and shows each element's properties,
patterns and position in the UI tree, then generates a **verified selector** and ready-to-paste
code for FlaUI (C#), Python `uiautomation`, pywinauto and Appium. This covers Phases 1–3 of
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
| `Ctrl+C` | Quit (in the console window) |

On laptops you may need to hold `Fn` for the F-keys. If another app already uses a hotkey, the
tool says so at startup and the other hotkeys still work.

### One-shot mode (scripts)

```bash
dotnet run --project src/UiInspector.Cli -- --point 800 450
```

```bash
dotnet run --project src/UiInspector.Cli -- --focused --no-ancestors
```

Add `--code` to also print the verified selector and the code for all four frameworks:

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
| `tests/UiInspector.Core.Tests` | Unit tests, plus integration tests (`Category=Desktop`) against `Fixtures/selector_fixture.ps1`, a WinForms window with duplicate captions, identical buttons and HWND-based IDs that opens off-screen |

### Generated code: what has been run

| Framework | Status |
| :--- | :--- |
| FlaUI (C#) | Compiled and run unedited against the fixture for all three selector shapes (ID, anchored name, anchored index); each clicked exactly the intended button |
| Python `uiautomation` 2.0.29 | Same three cases, all passed |
| pywinauto 0.6.9 | Same three cases, all passed |
| Appium (Windows driver) | Generated and unit-tested, **not run**: needs an Appium server with the Windows driver |

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
- Mixed-DPI monitors haven't been tested yet (the development machine has one 100 % monitor).

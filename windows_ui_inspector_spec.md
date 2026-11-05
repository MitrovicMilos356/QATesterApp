# Windows UI Inspector & Selector Generator
## Product & Technical Specification (Windows-only, v2)

> This replaces the cross-platform draft (`desktop_ui_inspector_architecture.md`). Linux is out of scope. That draft had factual errors, which are corrected here. They're listed in [Appendix A](#appendix-a--corrections-to-the-original-draft).

---

## 1. Summary

A Windows desktop tool for QA and automation engineers. You point at any element in any running application, and it shows:

1. **What the element is.** Its properties, its supported UIA patterns, and its position in the UI tree.
2. **Whether it can be targeted reliably.** A stability score, plus a live check that the selector finds exactly one element.
3. **Ready-to-paste code** for the framework you use, scoped correctly through windows, panes, iframes and embedded web views.

It also helps with the most frustrating QA situations: elements stacked on top of each other, invisible overlays that swallow clicks, iframes, embedded browsers, popups that close when you look away, and virtualized lists. See [Section 7](#7-complex-layouts-overlapping-elements-iframes--other-frustrations).

### What makes it different from existing tools

| Tool | What it does well | What it lacks |
| :--- | :--- | :--- |
| Inspect.exe (Windows SDK) | Raw UIA properties, live hover | No code generation, no selector checking, dated UI |
| Accessibility Insights | Accessibility auditing | Built for a11y compliance, not automation |
| FlaUInspect | Open-source UIA tree browser | No code generation, no verification |
| Appium Inspector | Appium sessions | Needs a running driver session, slow, Appium only |
| UiPath / Power Automate pickers | Strong selectors | Locked to their own platforms |

**This tool's niche:** verified, multi-framework selectors, explanations of stacked and covered elements, and a bridge between the native and web worlds for Electron and WebView2 apps.

---

## 2. Scope

### In scope (v1)
- Windows 10 (21H2+) and Windows 11, x64 and ARM64
- Any app that exposes UI Automation (UIA), including legacy apps exposed through UIA's built-in MSAA proxy
- Electron, Chromium and WebView2 content, both through UIA and optionally through the Chrome DevTools Protocol (CDP)
- Code generation for FlaUI (C#), Python `uiautomation`, pywinauto, Appium (Windows drivers) and Playwright (Electron/WebView2)

### Out of scope
- Linux and macOS
- Games and custom-rendered canvases (Unity, Unreal, raw DirectX/OpenGL). They have no accessibility tree.
- OCR and image-based detection
- Remote sessions (Citrix, RDP, VDI). The local machine only receives pixels.
- Java Swing/AWT in v1. These apps need the Java Access Bridge, a separate API. Planned for a later phase.

---

## 3. Architecture

```
+-------------------------------------------------------------------+
|                  Inspector UI  (WPF, .NET 8)                      |
|   Tree view | Properties | Element stack | Selector & Code panel  |
+-------------------------------------------------------------------+
        |  in-process calls / channels                ^
        v                                             | results
+-------------------------------------------------------------------+
|                  Core Engine  (.NET 8 library)                    |
|  - UIA worker (dedicated MTA thread, timeouts, cache requests)    |
|  - Hit-testing & element-stack builder                            |
|  - Selector generator + stability scorer                          |
|  - Verifier (re-runs selectors against the live tree)             |
|  - Code template renderer                                         |
+-------------------------------------------------------------------+
        |                    |                         |
        v                    v                         v
+----------------+  +--------------------+  +---------------------------+
| UIA3 (FlaUI)   |  | Overlay window     |  | Optional CDP bridge       |
| UIA2 fallback  |  | (layered, click-   |  | (Electron / WebView2 with |
| MSAA via proxy |  |  through, per-     |  |  --remote-debugging-port) |
+----------------+  |  monitor DPI)      |  |  + Node/Playwright sidecar|
                    +--------------------+  |    for locator checks     |
                                            +---------------------------+
```

### Technology choice

**Recommended: C# / .NET 8 + WPF + FlaUI (UIA3).**
- FlaUI is a mature, maintained wrapper around both UIA COM interfaces: UIA3 (current) and UIA2 (managed, sometimes better for WinForms).
- FlaUInspect is open source and shows how to handle most of the edge cases.
- WPF makes the overlay, the tree view and DPI handling straightforward.

**Alternative: Rust + `windows-rs` + Tauri.** This is viable and gives a smaller binary. But all UIA COM handling, threading and caching would be written by hand, so expect it to take roughly 2–3× longer to build.

### Threading model (important)
- All UIA calls run on **one dedicated MTA background thread** behind a request queue. They never run on the UI thread.
- Every cross-process call has a **timeout**, defaulting to 2 s. An app that's frozen or deadlocked must never freeze the inspector.
- Use UIA **CacheRequests** to fetch many properties in one round trip. Calling properties one at a time is 10–50× slower on big trees.

---

## 4. Features

### 4.1 Pick modes

| Mode | How it works | When to use |
| :--- | :--- | :--- |
| **Hover + hold** | Hold `Ctrl` (configurable). The element under the cursor is highlighted live (~50–100 ms throttle). | Most of the time |
| **Freeze hotkey** | While hovering, press `Ctrl+Shift+F`. The current element is locked and a tree snapshot is taken. | Menus, tooltips, dropdowns that close when you move away |
| **Delayed capture** | Click "Capture in 3 s", open the menu or hover state in the target app, and wait. | Hover-only and focus-sensitive UI |
| **Follow focus** | Inspect whatever has keyboard focus (UIA FocusChanged event). | Tab through a form; items with no mouse position |
| **Tree pick** | Browse the tree and select a node. Its bounds are highlighted on screen. | Hidden, off-screen or zero-size elements |

Getting the cursor position uses `GetCursorPos` polling plus `RegisterHotKey`. **No global low-level mouse hook is needed for hover.** That avoids antivirus false positives and input lag. A `WH_MOUSE_LL` hook is installed only for the optional "click to pick" mode, and only while that mode is active.

### 4.2 Highlight overlay
- A borderless, layered, topmost, click-through window (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`) that draws a rectangle around the element.
- It is **excluded from hit-testing**. Any element found that belongs to the inspector's own process is ignored.
- It is **per-monitor-DPI aware (v2)**. UIA rectangles are in physical screen pixels, so the overlay draws in physical pixels on each monitor.
- Different colours mean different things: the picked element, its parent, elements that cover it, and duplicate matches.

### 4.3 Tree & properties panel
- A tree from the desktop root down to the element, built lazily. You can switch between the Control view and the Raw view, because some elements only appear in the Raw view.
- Properties shown: `Name`, `AutomationId`, `ControlType`, `LocalizedControlType`, `ClassName`, `FrameworkId` (Win32, WPF, WinForm, XAML, Chrome, DirectUI…), `ProcessId` and process name, `BoundingRectangle`, `IsOffscreen`, `IsEnabled`, `IsKeyboardFocusable`, `HelpText`, `RuntimeId`, `NativeWindowHandle`.
- The supported UIA patterns (Invoke, Value, Toggle, SelectionItem, ExpandCollapse, Scroll, ItemContainer, VirtualizedItem, Text…). These decide which action a script should use: `Invoke()` rather than a mouse click, `SetValue()` rather than typing, and so on.
- **"Try action" buttons:** run Invoke, Toggle, Expand or SetValue live on the element to confirm the action works before you put it in a script.

### 4.4 Selector generation & scoring
See [Section 6](#6-selector-strategy).

### 4.5 Live verification (key feature)
Every generated selector is **run again against the live tree** before it's shown:

- ✅ **Unique:** exactly one match, and it's the picked element
- ⚠️ **Ambiguous:** N matches. All of them are highlighted and the tool suggests a tighter scope.
- ❌ **Broken:** no match, or a different element matches

For Playwright selectors, verification goes through the CDP sidecar, which runs `locator.count()` and checks the element's identity.

### 4.6 Code generation
Output targets, each with a "how to attach to the app" snippet:

| Target | Language | Best for |
| :--- | :--- | :--- |
| FlaUI | C# | Native Windows apps, .NET test suites |
| `uiautomation` | Python | Quick scripts, RPA-style automation |
| pywinauto (UIA backend) | Python | Existing pywinauto codebases. The project is in low-maintenance mode. |
| Appium Windows Driver / NovaWindows Driver | Any Appium client | Teams already on Appium/Selenium grids |
| Playwright | JS/TS, Python, C# | Electron and WebView2 content via CDP |

### 4.7 Recorder (Phase 4)
Click through a workflow, and the tool writes a full script: attach, then a sequence of actions using pattern-based actions, then waits. This is the feature that makes Playwright's `codegen` so popular, and no Windows-native tool does it well.

---

## 5. Data flow (how one inspection works)

1. **Activation:** you hold the hover modifier or press the freeze hotkey.
2. **Cursor position:** `GetCursorPos` returns physical screen coordinates when the process is per-monitor-DPI aware.
3. **Hit test:** `IUIAutomation::ElementFromPoint(x, y)` on the UIA worker thread, with a timeout.
4. **Self-filter:** if the hit belongs to the inspector's own process, ignore it and keep the previous result.
5. **Property fetch:** one CacheRequest fetches the properties and patterns for the element and all its ancestors up to the top-level window.
6. **Element stack:** build the list of every element under the point, not just the topmost one. See §7.1.
7. **Occlusion check:** call `ElementFromPoint` on the element's clickable point. If it returns something else, the element is **covered**. See §7.2.
8. **Web bridge (optional):** if `FrameworkId == "Chrome"` and CDP is connected, map the element to its DOM node and frame. See §7.3.
9. **Selector generation:** build candidate selectors, score them, and verify the best ones.
10. **Render:** draw the highlight and update the panels and code.

---

## 6. Selector strategy

### 6.1 Identify the window first, never by title alone
Window titles change constantly ("Untitled – Notepad", "Invoice #4411", unsaved markers). Prefer, in this order:
1. Process name, plus the window's `AutomationId`
2. Process name, plus the window's `ClassName`
3. Process name, plus a **regex** on the title, with a warning

### 6.2 Element attribute ranking (native apps)

| Rank | Strategy | Notes |
| :--- | :--- | :--- |
| 1 | `AutomationId` (if it passes the stability check) | Set by developers. Doesn't change with language. |
| 2 | `AutomationId` + `ControlType` | When an ID is reused across control types |
| 3 | `Name` + `ControlType`, scoped to a unique ancestor | **Changes with UI language** and with dynamic text ("3 unread"). Flagged when the text looks dynamic. |
| 4 | `ClassName` + `ControlType` + scope | Common for Win32/MFC/Delphi |
| 5 | Relative path from the nearest uniquely identifiable ancestor | e.g. `Pane[AutomationId=orders] → DataItem[3]`. Index flagged as fragile. |
| 6 | Absolute tree path | Last resort. Breaks easily. |
| ✗ | Screen coordinates | Never generated unless you explicitly ask. Shown with a big warning. |

### 6.3 Stability checks for IDs
An `AutomationId` is marked **unstable** if any of these hold:
- It looks like a GUID, a hash (`btn-x9281f`, `css-1q2w3e`) or an auto-increment value (`TextBox17`, `Panel_42`)
- It is **purely numeric**. Win32 control IDs are numeric. They're often stable, but they collide across dialogs, so they're scored lower.
- It changed between two snapshots (the "Re-check stability" button snapshots again after you reopen the window or restart the app)
- It appears more than once inside the same window

### 6.4 Scoping
If an element isn't unique, the generator walks up to the **nearest ancestor that is unique** and builds the selector relative to it. It doesn't fall back to an absolute path. Usually the result is short and stable:

```
Window(process=OrderApp, AutomationId=MainWindow)
  └ Pane(AutomationId=ShippingSection)
      └ Button(Name="Edit")        ← "Edit" appears 4 times in the window, only once in this pane
```

### 6.5 Corrected code examples

**FlaUI (C#)**
```csharp
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

using var app = Application.Attach("OrderApp.exe");
using var automation = new UIA3Automation();
var window = app.GetMainWindow(automation);

var button = window
    .FindFirstDescendant(cf => cf.ByAutomationId("checkoutBtn")
                                 .And(cf.ByControlType(ControlType.Button)))
    ?.AsButton();

button?.Invoke();   // InvokePattern: no mouse movement, works when the window is behind others
```

**Python `uiautomation`**
```python
import uiautomation as auto

win = auto.WindowControl(searchDepth=1, AutomationId="MainWindow")
btn = win.ButtonControl(AutomationId="checkoutBtn")
if btn.Exists(maxSearchSeconds=5):
    btn.GetInvokePattern().Invoke()
```

**pywinauto (UIA backend)**
```python
from pywinauto import Application

app = Application(backend="uia").connect(path="OrderApp.exe")
main = app.window(auto_id="MainWindow")          # not title= : titles change
main.child_window(auto_id="checkoutBtn", control_type="Button").invoke()
```

**Appium (Windows Driver, Python client)**
```python
from appium import webdriver
from appium.options.windows import WindowsOptions
from appium.webdriver.common.appiumby import AppiumBy

opts = WindowsOptions()
opts.app = r"C:\Program Files\OrderApp\OrderApp.exe"
driver = webdriver.Remote("http://127.0.0.1:4723", options=opts)

driver.find_element(AppiumBy.ACCESSIBILITY_ID, "checkoutBtn").click()   # ACCESSIBILITY_ID = UIA AutomationId
```

**Playwright (Electron app)**
```javascript
const { _electron: electron } = require('playwright');

// Playwright must LAUNCH the app; it cannot attach to an instance that is already running.
const app = await electron.launch({ executablePath: 'C:\\Program Files\\MyApp\\MyApp.exe' });
const page = await app.firstWindow();

await page.getByRole('button', { name: 'Submit Order' }).click();
await page.locator('#checkout-btn').click();                 // UIA AutomationId == HTML id
await page.getByTestId('checkout-btn').click();              // data-testid is only visible via CDP, not UIA
```

**Playwright (attach to a running Electron/WebView2 app via CDP)**
```javascript
const { chromium } = require('playwright');
// App must be started with --remote-debugging-port=9222
// (WebView2: set env WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222)
const browser = await chromium.connectOverCDP('http://127.0.0.1:9222');
const page = browser.contexts()[0].pages()[0];
```

---

## 7. Complex layouts: overlapping elements, iframes & other frustrations

These cases cause the most QA confusion, and existing inspectors handle them badly. Each one below gets a dedicated feature.

### 7.1 Stacked / nested elements ("I keep picking the wrong thing")
**Problem:** a button sits inside a panel, inside a card, inside a list item. All of them are under the cursor. `ElementFromPoint` returns only the deepest or topmost one, and it's often a meaningless `Text` or `Image` child, not the clickable `Button`.

**What the tool does:**
- **Element stack panel:** lists *every* element whose bounds contain the point, from topmost to bottom.
  - Native: built from the hit element's ancestor chain plus a cached walk of its siblings and their subtrees, filtered by `BoundingRectangle`.
  - Web (via CDP): `document.elementsFromPoint(x, y)` in each frame.
- **Cycle with the scroll wheel** (or `Alt+↑ / Alt+↓`) while picking, to move the selection up and down the stack. A breadcrumb shows where you are.
- **"Smart target" toggle:** automatically moves up from a non-interactive leaf (Text, Image, unnamed Pane) to the nearest ancestor that supports Invoke, Toggle, SelectionItem or Value, or is keyboard-focusable. You get "the button", not "the label inside the button".

### 7.2 Invisible overlays & covered elements ("the click goes somewhere else")
**Problem:** a transparent loading layer, a modal backdrop, a toast or a sticky header sits on top of the target. Test clicks hit the overlay, or Playwright fails with *"element intercepts pointer events"*.

**What the tool does:**
- **Occlusion check:** for the picked element, hit-test its clickable point (and its four inner corners). If any of them return a different element, show:
  > ⚠️ *Covered by `Pane (ClassName=LoadingOverlay)` at center point. A mouse click will hit the overlay.*
- Covering elements are highlighted in a separate colour.
- Code generation then prefers **pattern-based actions** (`Invoke()`, `Toggle()`) that don't depend on the mouse, and/or adds a **wait-until-gone** step for the overlay.
- **"Ignore transparent layers" pick option:** skips elements that have no name, no patterns and a full-window size, so you pick what's underneath.

### 7.3 iframes, embedded browsers & Shadow DOM
**Problem:**
- An Electron, WebView2 or CEF app shows a payment form inside an `<iframe>`. A plain selector won't find it, because the test has to enter the frame first.
- Through UIA, the iframe is just another `Document`/`Pane` node, so it's not obvious that a frame boundary exists.
- Web components use Shadow DOM, which UIA flattens and hides.

**What the tool does:**
- **Frame boundary markers:** in the tree, nodes where the content switches are badged:
  - 🌐 Native → Web (a WebView2/Chromium host inside a WPF/WinForms/Win32 window)
  - 🧩 Frame boundary (iframe)
  - 🌑 Shadow root
- **CDP bridge (optional, best results):**
  1. Convert the element's screen rectangle to page CSS pixels. This accounts for the client-area offset, `devicePixelRatio` and page zoom.
  2. Call CDP `DOM.getNodeForLocation`, which returns the DOM node **and the frame it lives in**, including iframes, and can pierce shadow DOM.
  3. Walk the frame chain to the top.
- **Frame-aware code generation:**
  ```javascript
  await page.frameLocator('#checkout-iframe')
            .getByRole('button', { name: 'Pay now' })
            .click();
  ```
  For a native host with embedded web content, the snippet is split into two parts: FlaUI/UIA code for the native window, and Playwright-via-CDP code for the web part. A comment explains why.
- **Cross-origin iframes** (e.g. payment providers) are flagged. The tool can show them, but the app's settings may prevent automation from entering them.
- **Without CDP:** the tool still detects that the element is inside a Chromium `Document` nested inside another `Document`, and warns: *"This element is inside a frame. Enable CDP to generate a frame-aware selector."*

### 7.4 Popups, menus, tooltips, dropdowns ("it disappears when I move to the inspector")
**Problem:** context menus, combobox dropdowns and tooltips close when the target app loses focus or the mouse leaves.

**What the tool does:**
- **Freeze hotkey and delayed capture** (§4.1): you never need to click the inspector while the popup is open.
- **Tree snapshots:** the frozen tree stays browsable after the popup closes. Properties are cached; live actions are disabled and marked "stale".
- **Popup ownership:** menus and dropdowns are often **separate top-level windows**, not children of the app window. The tool detects this (same process, owner window) and generates code that searches from the desktop root, scoped by process, instead of from the main window.

### 7.5 Virtualized lists & grids ("the row exists but the selector can't find it")
**Problem:** WPF/WinUI `ListView`, `DataGrid` and Chromium long lists only create UI elements for the rows currently visible. Row 500 doesn't exist in the tree until you scroll to it.

**What the tool does:**
- Detects `ItemContainerPattern` / `VirtualizedItemPattern` and badges the list "virtualized".
- Generates code that **finds the item through the container**, then calls `Realize()` and `ScrollIntoView()`, instead of a plain descendant search that only works when the row is on screen.
- Warns when a selector depends on a row index inside a virtualized list.

### 7.6 Duplicate elements ("there are 12 Delete buttons")
**What the tool does:**
- Verification (§4.5) highlights **all** matches on screen at once, numbered.
- Suggests a scope (§6.4), e.g. "the Delete button **in the row whose Name cell = 'INV-4411'**". This is a relative selector anchored on a stable neighbour, not an index.
- Uses an index only as a last resort, and labels it fragile.

### 7.7 Hidden, off-screen & zero-size elements
- Elements with `IsOffscreen = true`, zero-size bounds, or hidden behind collapsed panels are shown dimmed in the tree, with the reason.
- The generated code includes the needed step first, such as "expand parent" (ExpandCollapsePattern) or "scroll into view" (ScrollItemPattern).

### 7.8 Multiple windows, modal dialogs, multiple instances
- Modal dialogs are usually separate top-level windows owned by the main window. The tool generates code that waits for the dialog by its own identity.
- If two instances of the same app are running, the tool notes it and adds a process-ID or window-handle scoping option.

### 7.9 Timing & dynamic content
- The tool can't see timing issues in a single snapshot. So it adds **wait helpers** to the generated code by default: `Retry.WhileNull` (FlaUI), `Exists(maxSearchSeconds)` (uiautomation), Playwright's auto-waiting locators.
- **Watch mode:** records UIA StructureChanged and PropertyChanged events for the selected subtree for N seconds, so you can see what appears and disappears, e.g. spinners and lazily loaded panels.

---

## 8. Application compatibility (Windows)

| App type | Examples | Expected quality | Notes |
| :--- | :--- | :--- | :--- |
| **WPF** | Many enterprise LOB apps, Visual Studio parts | ⭐⭐⭐⭐⭐ Excellent | `AutomationProperties.AutomationId` or `x:Name` becomes the AutomationId. Custom controls without an AutomationPeer appear as generic `Custom`. |
| **WinUI 3 / UWP** | Windows 11 Settings, Calculator, Photos | ⭐⭐⭐⭐⭐ Excellent | Clean IDs and patterns. |
| **Avalonia** | Cross-platform .NET apps | ⭐⭐⭐⭐ Good | Has a native UIA implementation. |
| **WinForms** | Older internal tools | ⭐⭐⭐⭐ Good | The control's `Name` property becomes the AutomationId. Owner-drawn and third-party grids (DevExpress, Infragistics, Telerik) vary from great to poor depending on vendor support. UIA2 sometimes gives better results than UIA3 here. |
| **Electron / Chromium** | VS Code, Slack, Discord, Teams (new), Figma desktop | ⭐⭐⭐⭐ Good (UIA) / ⭐⭐⭐⭐⭐ (with CDP) | Chromium builds its accessibility tree only when it detects an assistive tool. UIA clients usually trigger it, but the first query can return an empty tree. Retry, or start the app with `--force-renderer-accessibility`. |
| **WebView2 hosts** | New Outlook, many modern LOB apps | ⭐⭐⭐⭐ Good / ⭐⭐⭐⭐⭐ (with CDP) | Native shell through UIA, web content through UIA or CDP. Shows the frame boundary handling of §7.3. |
| **CEF apps** | Spotify, some launchers | ⭐⭐⭐ Fair–Good | Depends on whether the app turned accessibility on in CEF. |
| **Microsoft Office** | Word, Excel, Outlook classic | ⭐⭐⭐⭐ Good for UI chrome | The ribbon, dialogs and task panes are good. The document and spreadsheet surface is mostly opaque; use the Office COM object model for that. |
| **Qt (5/6)** | Some engineering and CAD tools | ⭐⭐⭐ Fair–Good | Qt 6 has native UIA. Qt 5 relies on MSAA. QML/Qt Quick quality depends on how much `Accessible.*` markup the developers added. |
| **Win32 / MFC / ATL** | Old system tools, legacy enterprise apps | ⭐⭐⭐ Fair | Standard controls work through UIA's Win32 proxies. The AutomationId is the numeric control ID, so selectors rely on ClassName + Name. Custom-painted controls show up as one blank pane. |
| **Delphi VCL / C++Builder** | Accounting, medical, ERP apps | ⭐⭐ Fair–Poor | Standard VCL controls are OK through MSAA. `TPanel`- and custom-painted UI, and many third-party VCL grids, expose nothing useful. |
| **Java SWT** | Eclipse-based tools | ⭐⭐⭐ Fair | Uses native Win32 controls under the hood. |
| **Java Swing / AWT / JavaFX** | Older banking/telecom clients, IntelliJ-based IDEs | ⭐ Poor (v1) | Not exposed through UIA. Needs the Java Access Bridge (`jabswitch -enable`). Planned for Phase 5. |
| **Flutter (Windows)** | Newer cross-platform apps | ⭐⭐⭐ Fair | Exposes its semantics tree through UIA. Quality depends on the developers' semantics labels. |
| **Console / terminals** | cmd, PowerShell, Windows Terminal | ⭐⭐ Limited | Text only. No per-line or per-cell elements. |
| **Browsers (Chrome, Edge, Firefox)** | Websites | ⭐⭐⭐⭐ Works | But use Playwright/Selenium directly for web testing. The tool should suggest this. |
| **Games / custom engines** | Unity, Unreal, DirectX apps | ❌ None | No accessibility tree. |
| **Citrix / RDP / VDI windows** | Remote enterprise apps | ❌ None | The local machine only gets pixels. You'd need to install the inspector inside the remote session. |
| **Elevated (Admin) apps** | Installers, system utilities | ⚠️ Blocked by default | See §9.3. |

---

## 9. Hard parts & risks

### 9.1 DPI scaling and multi-monitor coordinates — *High difficulty*
- Monitors at different scale factors (100 %, 150 %, 200 %), apps that aren't DPI-aware and get bitmap-stretched, and monitors at negative coordinates (to the left of or above the primary).
- If the inspector isn't per-monitor-DPI-aware v2, highlights and hit-tests land in the wrong place.
- **Mitigation:** declare PMv2 in the manifest, keep everything in physical pixels, and build a test matrix with mixed-DPI monitors early.

### 9.2 Frozen or slow target apps — *High difficulty*
- UIA calls are cross-process COM calls. If the target's UI thread is blocked, the call can hang for a long time.
- Huge trees (an Excel ribbon, a grid with 10 000 cells, VS Code) make naive full-tree walks take seconds.
- **Mitigation:** a dedicated worker thread, per-call timeouts, CacheRequests, lazy tree expansion, and a cap on the size of the element-stack search.

### 9.3 Elevation & UIPI — *Medium difficulty, release-process work*
- Windows blocks a normal-integrity process from automating a higher-integrity (Admin) window.
- Workaround 1: run the inspector as Administrator.
- Proper solution: ship with `uiAccess="true"` in the manifest. This **requires the exe to be code-signed and installed in a secure location** (e.g. `Program Files`). That means a code-signing certificate and an installer.

### 9.4 Chromium accessibility activation — *Medium*
- The Electron/Chrome tree can be empty or partial the first time it's queried, and it fills in asynchronously.
- **Mitigation:** detect `FrameworkId == "Chrome"` with an empty subtree, wait and retry, and offer a "Relaunch with accessibility forced" helper.

### 9.5 Mapping UIA elements to DOM nodes — *High difficulty*
- No official API links a UIA element to a DOM node. The mapping goes through coordinates: screen → window client → CSS pixels (zoom, DPR) → `DOM.getNodeForLocation`.
- Things that break it: page zoom, CSS transforms, scrolling during capture, and elements with `pointer-events: none`.
- **Mitigation:** cross-check the result. Compare role and name between the UIA element and the DOM node's accessibility node, and show "mapping uncertain" if they disagree.
- Some production apps strip or block `--remote-debugging-port`. CDP is optional, and the tool still works through UIA alone.

### 9.6 Selector stability is a heuristic — *Medium*
- No tool can know for certain whether an ID will survive the next build.
- **Mitigation:** the two-snapshot stability check, clear confidence labels, and letting users mark attributes as "never use", per app profile.

### 9.7 Localization
- `Name` values change per UI language. A selector built on an English UI breaks on a German one.
- **Mitigation:** score Name lower, show a "language-dependent" badge, and prefer AutomationId.

### 9.8 Code generation correctness across library versions — *Medium, ongoing*
- FlaUI, pywinauto, `uiautomation`, Appium drivers and Playwright all change their APIs over time.
- **Mitigation:** versioned templates, and a CI job that compiles or runs every template against a test app containing WPF, WinForms, Win32 and WebView2 windows.

### 9.9 Antivirus / EDR false positives — *Low–Medium*
- Global hooks, reading other processes' UI, and an overlay window together look like spyware to some heuristics.
- **Mitigation:** avoid `WH_MOUSE_LL` except in click-to-pick mode, sign the binaries, and document exactly what the tool does.

### 9.10 Element identity across sessions
- `RuntimeId` and window handles change every time the app restarts, so they must never appear in generated selectors. They're only used inside the tool to track the "same element" during a session.

### 9.11 UIA event handler leaks
- UIA event subscriptions that aren't removed properly leak memory and can slow down the *target* app.
- **Mitigation:** strict subscribe/unsubscribe lifecycle, and Watch mode limited by time.

---

## 10. Roadmap

| Phase | Deliverable | Rough effort* |
| :--- | :--- | :--- |
| **1. Core PoC** | Console app: hover + hotkey → print properties, ancestors, patterns. DPI-correct. Worker thread with timeouts. | Small |
| **2. Inspector UI** | WPF app: overlay highlight, tree, properties, pick modes (hover, freeze, delayed, focus), element stack + cycling | Medium |
| **3. Selectors & code** | Ranking, stability checks, scoping, live verification, templates for FlaUI / uiautomation / pywinauto / Appium, occlusion check | Medium |
| **4. Web bridge** | CDP connection, DOM mapping, frame & shadow-DOM awareness, Playwright templates, Node sidecar for verification | Medium–Large |
| **5. Recorder & extras** | Action recording to script, Watch mode, app profiles, Java Access Bridge backend | Large |
| **6. Release** | Signed installer, `uiAccess`, auto-update, docs | Medium (mostly process and certificates) |

\* Effort covers writing the code *and* testing against real apps. Testing is where most of the time goes.

---

## 11. Success criteria

- The selector is unique and verified on the first try for at least 90 % of elements in WPF, WinUI, WinForms and Electron test apps
- Hover highlight latency under 100 ms on typical apps
- The inspector never freezes when the target app hangs
- Correct highlights on mixed-DPI, multi-monitor setups
- Every generated snippet runs without edits against the reference test app

---

## Appendix A — Corrections to the original draft

| Original claim | Correction |
| :--- | :--- |
| Playwright selectors can be generated for any running Electron app | Playwright must launch the app (`_electron.launch`), or connect via CDP to an app started with `--remote-debugging-port`. It can't attach to an arbitrary running instance. |
| `[data-automation-id="..."]` locator from UIA AutomationId | Chromium maps the HTML `id` to UIA AutomationId. `data-*` attributes are only visible through CDP/DOM. |
| `import dogtail.rawitch` | That module doesn't exist. The whole Linux section is dropped from this spec anyway. |
| `atspi_accessible_get_at_index_at_point` | That function doesn't exist (the real one is `atspi_component_get_accessible_at_point`). Linux dropped. |
| Selector ranking: Name > AutomationID | AutomationId (if stable) > Name. Name is localized and often dynamic. |
| Separate MSAA backend needed | UIA wraps MSAA apps automatically through built-in proxies. A separate MSAA backend isn't needed. |
| Continuous hover is too expensive and needs heavy throttling | `ElementFromPoint` takes milliseconds. A ~50–100 ms throttle is enough. The real risk is hung target apps, which need timeouts. |
| Global `WH_MOUSE_LL` hook for inspection | Not needed for hover (`GetCursorPos` + `RegisterHotKey`). Only use the hook for click-to-pick. |
| Elevation mitigation: "run with equal permissions" | Proper solution is `uiAccess="true"`, which needs a signed binary in a secure install location. |
| `app.window(title="Main Dashboard")` | Titles are fragile. Identify windows by process + AutomationId or ClassName. |
| pywinauto as the main Python target | pywinauto is in low-maintenance mode. Offer it, but also support `uiautomation` and FlaUI. |
| Missing: DPI, hung apps, overlays, iframes, popups, virtualization, duplicates | Covered in §7 and §9. |

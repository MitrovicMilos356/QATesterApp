using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UiInspector.Core;
using UiInspector.Core.Recording;

namespace UiInspector.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PickInterval = TimeSpan.FromMilliseconds(50);

    private readonly UiaWorker _worker = new();
    private readonly UiaWorker _replayWorker = new(); // a replay waits for elements; inspecting stays responsive meanwhile
    private readonly HighlightOverlay _overlay = new();
    private readonly HotkeyListener _hotkeys = new();
    private readonly DispatcherTimer _pickTimer;
    private readonly MainViewModel _viewModel;
    private readonly Recorder _recorder;
    private readonly BugReportViewModel _bugReport;
    private readonly TestabilityViewModel _testability;
    private bool _tickRunning;

    public MainWindow()
    {
        InitializeComponent();

        var inspector = new ElementInspector();
        var service = new InspectionService(_worker, inspector);
        _recorder = new Recorder(service, inspector);
        _bugReport = new BugReportViewModel(_recorder, Dispatcher);
        var replayer = new Replayer(_replayWorker);
        _viewModel = new MainViewModel(service, _overlay, new RecorderViewModel(_recorder, replayer, _overlay, Dispatcher), _bugReport);
        // Analysing a window takes seconds; it shares the replay's worker, so inspecting stays responsive meanwhile.
        _testability = new TestabilityViewModel(new Core.Testability.TestabilityAnalyzer(_replayWorker, inspector), service, _overlay, Dispatcher,
            () => _viewModel.SelectedNode?.Info);
        _viewModel.Testability = _testability;
        DataContext = _viewModel;
        _testability.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TestabilityViewModel.PreviewUri) && _testability.PreviewUri is { } uri) TestabilityBrowser.Navigate(uri);
        };

        // WebBrowser.Source isn't reliably bindable; navigate whenever a new preview file is written.
        _bugReport.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BugReportViewModel.PreviewUri) && _bugReport.PreviewUri is { } uri) PreviewBrowser.Navigate(uri);
        };

        Toast.Shown += ShowToast;
        ApplySettings();

        _pickTimer = new DispatcherTimer(PickInterval, DispatcherPriority.Input, OnPickTimer, Dispatcher);
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // ---------------------------------------------------------------- remembered layout

    private readonly AppSettings _settings = AppSettings.Load();

    private IEnumerable<ColumnDefinition> RememberedColumns() =>
    [
        InspectTreeColumn, InspectDetailsColumn, RecordStepsColumn, RecordScriptColumn,
        PlanFeaturesColumn, PlanDetailsColumn, TestabilityListColumn, ReportFormColumn,
    ];

    private static readonly GridLengthConverter Lengths = new();

    private void ApplySettings()
    {
        var s = _settings;
        SetTheme(s.Theme);
        if (s is { Left: { } left, Top: { } top, Width: { } width, Height: { } height } && width >= MinWidth && height >= MinHeight
            && new Rect(left, top, width, height).IntersectsWith(new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)))
        {
            // Only when some of it is still on a screen (a monitor may have been unplugged).
            WindowStartupLocation = WindowStartupLocation.Manual;
            (Left, Top, Width, Height) = (left, top, width, height);
        }
        if (s.Maximized) WindowState = WindowState.Maximized;

        foreach (var column in RememberedColumns())
        {
            if (!s.Columns.TryGetValue(column.Name, out var text)) continue;
            try
            {
                if (Lengths.ConvertFromInvariantString(text) is GridLength length && length.Value > 0) column.Width = length;
            }
            catch (FormatException)
            {
                // A hand-edited file: keep the default width.
            }
        }

        if (s.Page is >= 0 and <= 3) _viewModel.SelectedMainTab = s.Page;
        if (s.SnippetLanguage is { } snippet) _viewModel.PreferredSnippetId = snippet;
        if (s.RecorderLanguage is { } recorder) _viewModel.Recorder.PreferredScriptId = recorder;
        if (s.PlanLanguage is { } plan) _testability.PreferredScriptId = plan;
    }

    private void SaveSettings()
    {
        var s = _settings;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty) (s.Left, s.Top, s.Width, s.Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        s.Maximized = WindowState == WindowState.Maximized;
        foreach (var column in RememberedColumns()) s.Columns[column.Name] = Lengths.ConvertToInvariantString(column.Width) ?? "";
        s.Page = _viewModel.SelectedMainTab;
        s.SnippetLanguage = _viewModel.PreferredSnippetId;
        s.RecorderLanguage = _viewModel.Recorder.PreferredScriptId;
        s.PlanLanguage = _testability.PreferredScriptId;
        s.Save();
    }

    // ---------------------------------------------------------------- toast

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastPopup.IsOpen = false; // reopening places it again, should the window have moved
        ToastPopup.IsOpen = true;
        var fade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.Zero));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(1300)));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(1700)));
        fade.Completed += (_, _) =>
        {
            // A newer notice restarted the animation; only the last one closes the popup.
            if (ToastCard.Opacity == 0) ToastPopup.IsOpen = false;
        };
        ToastCard.BeginAnimation(OpacityProperty, fade);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var failed = _hotkeys.Start().Where(r => r.Error is not null).Select(r => r.Hotkey.Display).ToList();
        if (failed.Count > 0)
        {
            _viewModel.Status = $"Some hotkeys are used by another app and won't work: {string.Join(", ", failed)}";
        }
        _pickTimer.Start();

        // The listener's channel completes when the window closes and the listener is disposed.
        await foreach (var command in _hotkeys.Commands.ReadAllAsync())
        {
            await _viewModel.HandleHotkeyAsync(command);
        }
    }

    private async void OnPickTimer(object? sender, EventArgs e)
    {
        if (_tickRunning) return;
        _tickRunning = true;
        try
        {
            await _viewModel.PickTickAsync(Native.IsKeyDown(Native.VK_CONTROL), ScreenInfo.GetCursorPosition());
        }
        catch (InvalidOperationException)
        {
            // GetCursorPos can fail briefly, e.g. while the secure desktop (UAC prompt) is shown.
        }
        finally
        {
            _tickRunning = false;
        }
    }

    private void ElementTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeNodeViewModel { IsPlaceholder: false } node) _viewModel.SelectedNode = node;
    }

    private void ElementTree_ItemSelected(object sender, RoutedEventArgs e)
    {
        // Bring only the item's left edge into view: deep nodes have long rows, and the default
        // BringIntoView would scroll the tree sideways until their indentation and type are hidden.
        if (e.OriginalSource is not TreeViewItem item) return;
        var rowHeight = item.Template.FindName("PART_Header", item) is FrameworkElement header ? header.ActualHeight : 20;
        item.BringIntoView(new Rect(0, 0, 1, rowHeight));

        // BringIntoView still nudges the tree sideways to the item's indent; keep the left edge
        // (the top-level rows) visible unless the item would then be off the right side.
        Dispatcher.BeginInvoke(() =>
        {
            if (FindScrollViewer(ElementTree) is not { } scroller) return;
            var itemLeft = item.TransformToAncestor(scroller).Transform(new Point(0, 0)).X + scroller.HorizontalOffset;
            if (itemLeft < scroller.ViewportWidth * 0.6) scroller.ScrollToHorizontalOffset(0);
        }, DispatcherPriority.Loaded);
    }

    // A right-click selects the row first, so the context menu copies from the row that was clicked.
    private void TestCases_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is DataGridRow row)
            {
                row.IsSelected = true;
                return;
            }
        }
    }

    /// <summary>The Keys sheet.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Shortcuts { get; } =
    [
        new("Ctrl+Shift+F9", "Pick the element under the cursor"),
        new("Ctrl+Shift+F10", "Inspect the focused element"),
        new("Ctrl+Shift+F11", "Delayed pick, for menus and tooltips"),
        new("Ctrl+Shift+F12", "Live pick on or off"),
        new("Ctrl+Shift+F8", "Start or stop recording (stops a replay)"),
        new("Ctrl+Shift+F7", "Add a check while recording"),
    ];

    private DateTime _keysClosedAt;

    // Clicking the button while the sheet is open closes it (StaysOpen) and must not reopen it.
    private void KeysButton_Click(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow - _keysClosedAt > TimeSpan.FromMilliseconds(300)) KeysPopup.IsOpen = true;
    }

    private void KeysPopup_Closed(object? sender, EventArgs e) => _keysClosedAt = DateTime.UtcNow;

#pragma warning disable WPF0001 // ThemeMode is marked experimental
    private void ThemeButton_Click(object sender, RoutedEventArgs e) =>
        SetTheme(_settings.Theme switch { "System" => "Light", "Light" => "Dark", _ => "System" });

    /// <summary>"System", "Light" or "Dark"; the button shows the one in use.</summary>
    private void SetTheme(string theme)
    {
        if (theme is not ("Light" or "Dark")) theme = "System";
        Application.Current.ThemeMode = new ThemeMode(theme);
        ThemeButton.Content = theme;
        _settings.Theme = theme;
        UpdateTitleBar();
    }

    // The Fluent theme colours the window but not, on Windows 10, its title bar.
    private void UpdateTitleBar()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return; // SourceInitialized calls again
        var dark = _settings.Theme == "Dark" || (_settings.Theme == "System" && !WindowsUsesLightApps());
        Native.SetDarkTitleBar(handle, dark);
    }

    private static bool WindowsUsesLightApps()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateTitleBar();
    }
#pragma warning restore WPF0001

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    // F1 opens the keyboard shortcuts.
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F1)
        {
            KeysPopup.IsOpen = !KeysPopup.IsOpen;
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SaveSettings();
        Toast.Shown -= ShowToast;
        _pickTimer.Stop();
        _hotkeys.Dispose();
        // Removes the input hook first thing; the rest only waits briefly for background work.
        // (On the thread pool: its awaits would otherwise need this blocked UI thread.)
        Task.Run(() => _recorder.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
        _overlay.Close();
        _bugReport.Dispose();
        _testability.Dispose();
        _worker.Dispose();
        _replayWorker.Dispose();
    }
}

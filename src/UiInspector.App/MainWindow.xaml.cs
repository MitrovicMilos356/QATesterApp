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

        _pickTimer = new DispatcherTimer(PickInterval, DispatcherPriority.Input, OnPickTimer, Dispatcher);
        Loaded += OnLoaded;
        Closed += OnClosed;
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

    private void OnClosed(object? sender, EventArgs e)
    {
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

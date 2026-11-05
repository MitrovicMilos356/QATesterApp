using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UiInspector.Core;

namespace UiInspector.App;

/// <summary>
/// One node in the element tree. Children load lazily from UIA the first time the node expands;
/// until then a placeholder child gives the node an expander arrow.
/// </summary>
public sealed partial class TreeNodeViewModel : ObservableObject
{
    private const int MaxClassNameLength = 40;
    private static readonly ElementInfo EmptyInfo = new();

    private readonly Func<ElementInfo, Task<ChildrenResult>>? _loadChildren;
    private bool _childrenLoaded;
    private bool _loading;

    public TreeNodeViewModel(ElementInfo info, Func<ElementInfo, Task<ChildrenResult>> loadChildren, TreeNodeViewModel? parent)
    {
        Info = info;
        Parent = parent;
        _loadChildren = loadChildren;
        Children.Add(Placeholder("Loading…"));
    }

    private TreeNodeViewModel(string placeholderText)
    {
        Info = EmptyInfo;
        IsPlaceholder = true;
        PlaceholderText = placeholderText;
        _childrenLoaded = true;
    }

    public ElementInfo Info { get; }

    public TreeNodeViewModel? Parent { get; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public bool IsPlaceholder { get; }

    public string PlaceholderText { get; } = "";

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>On the path from the desktop to the element that was picked.</summary>
    [ObservableProperty]
    private bool _isOnPath;

    /// <summary>The exact element the cursor was over (before smart-target adjustment).</summary>
    [ObservableProperty]
    private bool _isHitElement;

    public string TypeText => IsPlaceholder ? "" : (string.IsNullOrEmpty(Info.ControlType) ? "?" : Info.ControlType);

    public string NameText => IsPlaceholder ? PlaceholderText : Format.Quote(Info.Name);

    public string IdText => IsPlaceholder || string.IsNullOrEmpty(Info.AutomationId) ? "" : "#" + Info.AutomationId;

    public string ClassText => IsPlaceholder || string.IsNullOrEmpty(Info.ClassName)
        ? ""
        : "." + Format.Truncate(Info.ClassName, MaxClassNameLength);

    /// <summary>What screen readers (and UI tests) hear for this tree item, e.g. <c>Button "Save" #saveButton</c>.</summary>
    public string AccessibleName => IsPlaceholder ? PlaceholderText : $"{TypeText} {NameText} {IdText}".TrimEnd();

    /// <summary>Replaces the placeholder with a single known child, before the real children are loaded.</summary>
    public void SetPathChild(TreeNodeViewModel child)
    {
        Children.Clear();
        Children.Add(child);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = LoadChildrenAsync(keep: null);
    }

    /// <summary>
    /// Loads the real children. <paramref name="keep"/> is an existing child node (on the picked
    /// path) that stays in place, so its selection and expansion survive the reload.
    /// </summary>
    public async Task LoadChildrenAsync(TreeNodeViewModel? keep)
    {
        if (_childrenLoaded || _loading || _loadChildren is null) return;
        _loading = true;
        keep ??= Children.FirstOrDefault(c => !c.IsPlaceholder);

        try
        {
            var result = await _loadChildren(Info);
            var nodes = result.Children
                .Select(c => keep is not null && c.IsSameElement(keep.Info) ? keep : new TreeNodeViewModel(c, _loadChildren, this))
                .ToList();
            if (keep is not null && !nodes.Contains(keep)) nodes.Insert(0, keep);
            if (result.Truncated) nodes.Add(Placeholder($"… only the first {result.Children.Count} children are shown"));

            ReplaceChildrenAround(keep, nodes);
        }
        catch (Exception ex)
        {
            var nodes = new List<TreeNodeViewModel>();
            if (keep is not null) nodes.Add(keep);
            nodes.Add(Placeholder($"(couldn't load children: {FailureMessages.Describe(ex, null)})"));
            ReplaceChildrenAround(keep, nodes);
        }
        finally
        {
            _childrenLoaded = true;
            _loading = false;
        }
    }

    // Edits the collection in place around `keep` instead of clearing it, so the TreeView keeps
    // that item's container (and the user's scroll position) instead of rebuilding it.
    private void ReplaceChildrenAround(TreeNodeViewModel? keep, List<TreeNodeViewModel> nodes)
    {
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(Children[i], keep)) Children.RemoveAt(i);
        }

        var keepIndex = keep is null ? -1 : nodes.IndexOf(keep);
        for (var i = 0; i < nodes.Count; i++)
        {
            if (i < keepIndex) Children.Insert(i, nodes[i]);
            else if (i > keepIndex) Children.Add(nodes[i]);
        }
    }

    private static TreeNodeViewModel Placeholder(string text) => new(text);
}

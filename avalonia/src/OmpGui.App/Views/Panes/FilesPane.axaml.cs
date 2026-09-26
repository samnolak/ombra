using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views.Panes;

/// <summary>
/// The Files pane's view-only parts: a click opens a row, the keyboard walks the tree (Enter opens, → opens a folder,
/// ← closes it or goes up), "Go to file" hands Enter and ↓ to its results, and the list and the viewer share the height
/// (a splitter between them; the viewer can take it all).
/// </summary>
public partial class FilesPane : UserControl
{
    private FilesViewModel? _vm;

    public FilesPane()
    {
        InitializeComponent();
        Tree.AddHandler(TappedEvent, OnRowTapped);
        Results.AddHandler(TappedEvent, OnRowTapped);
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        Results.AddHandler(KeyDownEvent, OnResultsKeyDown, RoutingStrategies.Tunnel);
        FilterBox.AddHandler(KeyDownEvent, OnFilterKeyDown, RoutingStrategies.Tunnel);
        // A right click picks the row the menu is for
        Tree.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(Tree).Properties.IsRightButtonPressed && NodeAt(e.Source) is { } n && _vm is { } vm) vm.SelectedNode = n;
        }, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.RevealRequested -= OnRevealRequested;
        }
        _vm = DataContext as FilesViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.RevealRequested += OnRevealRequested;
        }
        UpdateRows();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilesViewModel.HasViewer) or nameof(FilesViewModel.IsViewerExpanded)) UpdateRows();
    }

    /// <summary>
    /// List above, file below (2 : 3, the splitter between them; each keeps a minimum); the file alone when expanded;
    /// the list alone when no file is open.
    /// </summary>
    private void UpdateRows()
    {
        var rows = Layout.RowDefinitions;
        var viewer = _vm?.HasViewer == true;
        var expanded = viewer && _vm!.IsViewerExpanded;
        FilterArea.IsVisible = _vm?.HasProject == true && !expanded;
        ListArea.IsVisible = !expanded;
        rows[2].Height = expanded ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        rows[2].MinHeight = viewer && !expanded ? 96 : 0;
        rows[4].Height = viewer ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        rows[4].MinHeight = viewer ? 160 : 0;
        ViewerSplitter.IsVisible = ViewerLine.IsVisible = viewer && !expanded;
    }

    private void OnRevealRequested(FileNodeViewModel node) =>
        Dispatcher.UIThread.Post(() => Tree.ScrollIntoView(node), DispatcherPriority.Background);

    private static FileNodeViewModel? NodeAt(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as FileNodeViewModel;

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || NodeAt(e.Source) is not { } node) return;
        _vm.ActivateCommand.Execute(node);
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm?.SelectedNode is not { } node || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter or Key.Space:
                _vm.ActivateCommand.Execute(node);
                e.Handled = true;
                break;
            case Key.Right when node.IsDirectory && !node.IsExpanded:
                _ = _vm.ToggleAsync(node);
                e.Handled = true;
                break;
            case Key.Left when node.IsDirectory && node.IsExpanded:
                _ = _vm.ToggleAsync(node);
                e.Handled = true;
                break;
            case Key.Left when node.Parent is { Depth: >= 0 } parent:
                _vm.SelectedNode = parent;
                Tree.ScrollIntoView(parent);
                FocusSelected(Tree);
                e.Handled = true;
                break;
        }
    }

    private void OnResultsKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key is Key.Enter or Key.Space && _vm.SelectedResult is { } r)
        {
            _vm.ActivateCommand.Execute(r);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && Results.SelectedIndex <= 0)
        {
            FilterBox.Focus();
            e.Handled = true;
        }
    }

    /// <summary>"Go to file": Enter opens the first match, ↓ goes into the list, Esc clears.</summary>
    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Enter when _vm.ShowResults && _vm.Results.Count > 0:
                _vm.SelectedResult = _vm.SelectedResult ?? _vm.Results[0];
                _vm.ActivateCommand.Execute(_vm.SelectedResult);
                e.Handled = true;
                break;
            case Key.Down:
                var list = _vm.ShowResults ? Results : Tree;
                if (list.ItemCount == 0) break;
                if (list.SelectedIndex < 0) list.SelectedIndex = 0;
                FocusSelected(list);
                e.Handled = true;
                break;
            case Key.Escape when _vm.HasFilter:
                _vm.Filter = "";
                e.Handled = true;
                break;
        }
    }

    private static void FocusSelected(ListBox list) =>
        Dispatcher.UIThread.Post(() => (list.ContainerFromIndex(Math.Max(0, list.SelectedIndex)) as InputElement ?? list).Focus(NavigationMethod.Directional),
            DispatcherPriority.Background);
}

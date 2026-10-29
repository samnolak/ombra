using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.ViewModels;

namespace Ombra.App.Views;

/// <summary>
/// The rail's Editor page (EditorPageViewModel): the tree's and the hits' clicks and keys, each group's editor events,
/// and tab dragging (a tab dragged along its strip takes the place of the one under the pointer).
/// </summary>
public partial class EditorPage : UserControl
{
    /// <summary>The pointer moves this far with the button down before a press becomes a drag.</summary>
    internal const double DragThreshold = 6;

    private readonly HashSet<DocumentEditor> _wired = [];
    private EditorPageViewModel? _subscribed;
    private EditorTabViewModel? _dragTab;
    private Point _dragStart;
    private bool _dragging;

    public EditorPage()
    {
        InitializeComponent();
        EditorTree.Tapped += (_, _) => OpenSelected();
        EditorTree.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; OpenSelected(); } };
        EditorSearchHits.Tapped += (_, _) => OpenHit();
        EditorSearchHits.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; OpenHit(); } };
        EditorSearchBox.KeyDown += OnSearchKey;
        EditorReplaceBox.KeyDown += OnSearchKey;
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null) _subscribed.PropertyChanged -= OnVmChanged;
            _subscribed = Vm;
            if (_subscribed is not null) _subscribed.PropertyChanged += OnVmChanged;
        };
    }

    private EditorPageViewModel? Vm => DataContext as EditorPageViewModel;

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm) return;
        e.Handled = true;
        vm.SearchCommand.Execute(null);
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorPageViewModel.IsSearchShown) && Vm is { IsSearchShown: true })
            Dispatcher.UIThread.Post(() => EditorSearchBox.Focus(), DispatcherPriority.Loaded);
        else if (e.PropertyName == nameof(EditorPageViewModel.RevealedNode) && Vm?.RevealedNode is { } node)
        {
            EditorTree.SelectedItem = node;
            EditorTree.ScrollIntoView(node);
        }
    }

    /// <summary>A click or a key anywhere in a group makes it the one files open in.</summary>
    private void OnGroupLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control group || group.Tag is true) return;
        group.Tag = true;
        group.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (group.DataContext is EditorGroupViewModel g && Vm is { } vm) vm.ActiveGroup = g;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>A group's editor: its links open in its group, its marks and caret go to its active tab.</summary>
    private void OnGroupEditorLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not DocumentEditor editor || !_wired.Add(editor)) return;
        editor.NavigateRequested += async (path, line) =>
        {
            if (Vm is not { } vm || editor.DataContext is not EditorGroupViewModel group) return;
            vm.ActiveGroup = group;
            await vm.OpenAsync(path, line);
        };
        editor.MarkRequested += request => (editor.DataContext as EditorGroupViewModel)?.ActiveTab?.AddEditorMark(request);
        editor.CaretMoved += (line, character) => (editor.DataContext as EditorGroupViewModel)?.ActiveTab?.CaretMoved(line, character);
        editor.Editor.TextArea.GotFocus += (_, _) =>
        {
            if (editor.DataContext is EditorGroupViewModel group && Vm is { } vm) vm.ActiveGroup = group;
        };
    }

    private void OpenSelected()
    {
        if (EditorTree.SelectedItem is FileNodeViewModel node) Vm?.OpenNodeCommand.Execute(node);
    }

    private void OpenHit()
    {
        if (EditorSearchHits.SelectedItem is EditorSearchHit hit) Vm?.OpenHitCommand.Execute(hit);
    }

    private void OnTabTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is EditorTabViewModel tab) tab.ActivateCommand.Execute(null);
    }

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: EditorTabViewModel tab } control || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        _dragTab = tab;
        _dragStart = e.GetPosition(this);
        _dragging = false;
    }

    private void OnTabMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTab is not { } tab || sender is not Control control || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        var position = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _dragStart.X) < DragThreshold && Math.Abs(position.Y - _dragStart.Y) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(control);
        }
        if (control.FindAncestorOfType<ItemsControl>() is not { } strip) return;
        var x = e.GetPosition(strip).X;
        for (var i = 0; i < tab.Group.Tabs.Count; i++)
        {
            if (strip.ContainerFromIndex(i) is not { } container) continue;
            var left = container.TranslatePoint(new Point(0, 0), strip)?.X ?? 0;
            if (x < left || x >= left + container.Bounds.Width) continue;
            if (i != tab.Group.Tabs.IndexOf(tab)) tab.Group.MoveTab(tab, i);
            break;
        }
        e.Handled = true;
    }

    private void OnTabReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging) e.Pointer.Capture(null);
        _dragTab = null;
        _dragging = false;
    }
}

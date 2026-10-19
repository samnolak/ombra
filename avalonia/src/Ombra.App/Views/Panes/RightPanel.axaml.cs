using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Ombra.App.Controls;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Panes;

/// <summary>The right panel: its strip of tabs over the tab shown (MainViewModel.Panes.cs).</summary>
public partial class RightPanel : UserControl
{
    private MainViewModel? _vm;

    public RightPanel()
    {
        InitializeComponent();
        SideScrollStrip.Attach(PanelTabScroller);
    }

    /// <summary>The browser tab's view (the window's Esc handling, tests).</summary>
    internal PreviewPanel Browser => PreviewPanel;

    /// <summary>
    /// The panel takes the height the chat column gives it and asks for none: it spans the column's rows, and a page that
    /// wants all the height it is offered (the Nightly pane, as a web view does) made the grid share that height out to
    /// the rows once more, its two star rows (the new-session and setup layouts, MainWindow.FitEmptyState) rounded to a
    /// device pixel each — half a point taller than the window in all, so the page got a fractional height and its frame
    /// was no longer shown 1:1.
    /// </summary>
    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize) => base.MeasureOverride(availableSize).WithHeight(0);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is { } old)
        {
            old.PropertyChanged -= OnVmChanged;
            old.Preview.PropertyChanged -= OnVmChanged;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmChanged;
        _vm.Preview.PropertyChanged += OnVmChanged;
    }

    // Another tab shown (a tool, or another page of the browser): it is scrolled into the strip's view
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ActivePane) or nameof(PreviewViewModel.ActiveTab)) RevealActiveTab();
    }

    private void RevealActiveTab() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm?.PanelTabs.FirstOrDefault(t => t.IsActive) is not { } active) return;
            if (PanelTabItems.ContainerFromItem(active) is { } container) container.BringIntoView();
        }, DispatcherPriority.Loaded);
}

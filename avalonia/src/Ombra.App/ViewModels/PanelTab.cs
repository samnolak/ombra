using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ombra.App.ViewModels;

/// <summary>
/// A tab of the right panel's strip (<see cref="MainViewModel.PanelTabs"/>): one of its tools (Files, Changes,
/// Background tasks, Plan, the new-tab page) or a page of the browser (<see cref="Page"/>, one per
/// <see cref="PreviewViewModel.Tabs"/> entry while the browser is in the panel).
/// </summary>
public sealed partial class PanelTab : ObservableObject, IDisposable
{
    public PanelTab(SidePane pane) => Pane = pane;

    public PanelTab(PreviewTab page)
    {
        Pane = SidePane.Browser;
        Page = page;
        page.PropertyChanged += OnPageChanged;
    }

    /// <summary>What the panel shows while this tab is active.</summary>
    public SidePane Pane { get; }

    /// <summary>The browser's page this tab stands for; null for a tool.</summary>
    public PreviewTab? Page { get; }

    public bool IsPage => Page is not null;

    /// <summary>The page's title (or host), else the tool's name.</summary>
    public string Header => Page?.Header ?? MainViewModel.PaneTitle(Pane);

    /// <summary>The page's whole title and whose it is, else the tool's name and its shortcut.</summary>
    public string ToolTip => Page?.ToolTip ?? MainViewModel.PaneTip(Pane);

    public Geometry? Icon => Application.Current?.TryGetResource(MainViewModel.PaneIconKey(Pane), null, out var v) == true ? v as Geometry : null;

    /// <summary>The strip lists it: always for a tool; a page as the browser's own strip did (an empty, unshown tab is not).</summary>
    public bool ShowsInStrip => Page?.ShowsInStrip ?? true;

    /// <summary>The strip shows it as a tab: a tool, or a page in no site group.</summary>
    public bool ShowsAsTab => Page?.ShowsAsTab ?? true;

    /// <summary>The page's site group, whose chip the strip shows in place of its first page (<see cref="TabSiteGroup"/>).</summary>
    public TabSiteGroup? SiteGroup => Page?.SiteGroup;

    public bool IsGroupHead => Page?.IsGroupHead == true;

    /// <summary>The strip has something in this tab's place: the tab, or its group's chip.</summary>
    public bool HasStripItem => ShowsAsTab || IsGroupHead;

    public bool IsLoading => Page?.IsLoading == true;

    public bool IsPopup => Page?.IsPopup == true;

    /// <summary>The panel shows this tab.</summary>
    [ObservableProperty] private bool _isActive;

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PreviewTab.Header) or nameof(PreviewTab.Title) or nameof(PreviewTab.Url):
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(ToolTip));
                OnPropertyChanged(nameof(ShowsInStrip));
                break;
            case nameof(PreviewTab.ShowsInStrip) or nameof(PreviewTab.IsActive) or nameof(PreviewTab.ShowsAsTab):
                OnPropertyChanged(nameof(ShowsInStrip));
                OnPropertyChanged(nameof(ShowsAsTab));
                OnPropertyChanged(nameof(HasStripItem));
                break;
            case nameof(PreviewTab.SiteGroup):
                OnPropertyChanged(nameof(SiteGroup));
                OnPropertyChanged(nameof(ShowsAsTab));
                OnPropertyChanged(nameof(HasStripItem));
                break;
            case nameof(PreviewTab.IsGroupHead):
                OnPropertyChanged(nameof(IsGroupHead));
                OnPropertyChanged(nameof(HasStripItem));
                break;
            case nameof(PreviewTab.IsLoading):
                OnPropertyChanged(nameof(IsLoading));
                break;
        }
    }

    public void Dispose()
    {
        if (Page is { } page) page.PropertyChanged -= OnPageChanged;
    }
}

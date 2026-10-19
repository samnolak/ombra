using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;

namespace Ombra.App.Controls;

/// <summary>Close actions always target the tab under the pointer, not the selected tab.</summary>
public static class TabMenus
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enabled", typeof(TabMenus));
    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);
    public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);

    static TabMenus() => EnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
    {
        control.RemoveHandler(InputElement.ContextRequestedEvent, OnContextRequested);
        control.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        if (e.NewValue is not true) return;
        control.AddHandler(InputElement.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        control.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    });

    private static T? Owner<T>(Control control) where T : class =>
        control.GetVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<T>().FirstOrDefault();

    internal static Flyout? Build(Control control)
    {
        if (control.DataContext is PreviewTab page && Owner<PreviewViewModel>(control) is { } browser)
            return Build(browser.Tabs, page, t => t.CanClose, t => browser.CloseTabCommand.Execute(t), browser, page);
        if (control.DataContext is PanelTab panel && Owner<MainViewModel>(control) is { } main)
            return Build(main.PanelTabs, panel, _ => true, t => main.ClosePanelTabCommand.Execute(t), main.Preview, panel.Page);
        if (control.DataContext is TerminalViewModel terminal && Owner<MainViewModel>(control) is { } vm)
            return Build(vm.Terminals, terminal, _ => true, t => vm.CloseTerminalCommand.Execute(t));
        return null;
    }

    private static Flyout Build<T>(IList<T> tabs, T tab, Func<T, bool> canClose, Action<T> close,
        PreviewViewModel? browser = null, PreviewTab? page = null) => ContextMenus.Menu(
        new MenuAction(Loc.T("Close tab"), "IconClose", _ => close(tab), () => tabs.Contains(tab) && canClose(tab)),
        new MenuAction(Loc.T("Close other tabs"), "IconClose", _ => CloseGroup(tabs, tab, canClose, close, false),
            () => tabs.Any(t => !EqualityComparer<T>.Default.Equals(t, tab) && canClose(t))),
        new MenuAction(Loc.T("Close tabs to the right"), "IconClose", _ => CloseGroup(tabs, tab, canClose, close, true),
            () => tabs.Skip(tabs.IndexOf(tab) + 1).Any(canClose)),
        null,
        page is not null ? new MenuAction(Loc.T("Copy page URL"), "IconLink",
            c => { if (page.Url is { } uri) _ = ContextMenus.CopyAsync(c, uri.AbsoluteUri); }, () => page.Url is not null) : null,
        page is not null && browser is not null ? new MenuAction(Loc.T("Reload"), "IconReload", _ =>
        {
            browser.SelectTabCommand.Execute(page);
            browser.ReloadCommand.Execute(null);
        }, () => page.Url is not null) : null);

    internal static void CloseGroup<T>(IList<T> tabs, T tab, Func<T, bool> canClose, Action<T> close, bool rightOnly)
    {
        var index = tabs.IndexOf(tab);
        if (index < 0) return;
        // Closing a page can remove more than one strip entry (the permanent first browser tab).
        var targets = tabs.Where((t, i) => i != index && (!rightOnly || i > index) && canClose(t)).ToArray();
        foreach (var target in targets)
            if (tabs.Contains(target)) close(target);
    }

    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control || Build(control) is not { } menu) return;
        control.ContextFlyout = menu;
        menu.ShowAt(control, showAtPointer: true);
        e.Handled = true;
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || !e.GetCurrentPoint(control).Properties.IsMiddleButtonPressed) return;
        switch (control.DataContext)
        {
            case PreviewTab { CanClose: true } page when Owner<PreviewViewModel>(control) is { } browser:
                browser.CloseTabCommand.Execute(page);
                break;
            case PanelTab tab when Owner<MainViewModel>(control) is { } main:
                main.ClosePanelTabCommand.Execute(tab);
                break;
            case TerminalViewModel terminal when Owner<MainViewModel>(control) is { } vm:
                vm.CloseTerminalCommand.Execute(terminal);
                break;
            default: return;
        }
        e.Handled = true;
    }
}

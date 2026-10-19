using Avalonia;
using Avalonia.VisualTree;

namespace Ombra.App.Views;

public partial class MainWindow
{
    private bool IsBrowserShortcutContext() => Vm?.ShowBrowserPage == true
        || (FocusManager?.GetFocusedElement() as Visual)?.GetSelfAndVisualAncestors().OfType<Controls.PreviewPanel>().Any() == true;

    private void CloseActiveSurface()
    {
        if (Vm is not { } vm) { Close(); return; }
        var focused = FocusManager?.GetFocusedElement() as Visual;
        if (focused is not null && TerminalHost.IsVisualAncestorOf(focused) && vm.SelectedTerminal is { } terminal)
        {
            vm.CloseTerminalCommand.Execute(terminal);
            return;
        }
        if (vm.ShowBrowserPage)
        {
            if (vm.Browser.ActiveTab.CanClose) vm.Browser.CloseTabCommand.Execute(vm.Browser.ActiveTab);
            else vm.ShowChatsCommand.Execute(null);
            return;
        }
        if (focused is not null && RightPanel.IsVisualAncestorOf(focused)
            && vm.PanelTabs.FirstOrDefault(t => t.IsActive) is { } tab)
        {
            vm.ClosePanelTabCommand.Execute(tab);
            return;
        }
        Close();
    }
}

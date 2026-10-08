using OmpGui.App.Controls;

namespace OmpGui.App.Views;

public sealed partial class MainWindow
{
    /// <summary>Right-click menus window-wide: Control+click on macOS, and text deferring to the menu around it.</summary>
    static MainWindow() => ContextMenus.Attach<MainWindow>();
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Controls;

/// <summary>The header's branch chip. Reads the branch again when the window comes back to the front (a branch switched
/// in another terminal shows at once); its menu closes once an item is chosen.</summary>
public sealed partial class BranchChip : UserControl
{
    private Window? _window;

    public BranchChip()
    {
        InitializeComponent();
        BranchMenu.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Classes: var c } && c.Contains("menu-item"))
                Dispatcher.UIThread.Post(() => ChipButton.Flyout?.Hide(), DispatcherPriority.Background);
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.Activated += OnActivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.Activated -= OnActivated;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.BranchChip.Refresh();
    }

    /// <summary>Opens the menu (tests, keyboard).</summary>
    internal void OpenMenu() => ChipButton.Flyout?.ShowAt(ChipButton);

    internal Button Chip => ChipButton;
}

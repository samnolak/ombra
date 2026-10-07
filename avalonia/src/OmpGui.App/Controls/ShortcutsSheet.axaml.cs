using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Controls;

/// <summary>
/// The keyboard shortcut sheet (DataContext = <see cref="MainViewModel"/>). It takes the keyboard when it opens, so Esc
/// closes it before anything else Esc does (closing a request, stopping omp); a click on the dimmed window closes it too.
/// </summary>
public sealed partial class ShortcutsSheet : UserControl
{
    public ShortcutsSheet()
    {
        InitializeComponent();
        Scrim.PointerPressed += (_, e) =>
        {
            Close();
            e.Handled = true;
        };
    }

    private void Close()
    {
        if (DataContext is MainViewModel vm) vm.CloseShortcutsCommand.Execute(null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.NewValue is true)
            Dispatcher.UIThread.Post(() => CloseButton.Focus(NavigationMethod.Pointer), DispatcherPriority.Background);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}

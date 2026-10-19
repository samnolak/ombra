using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.ViewModels;

namespace Ombra.App.Controls;

/// <summary>
/// The command palette's card (DataContext = <see cref="MainViewModel"/>, MainViewModel.Palette.cs). It takes the
/// keyboard when it opens and keeps it in its box: ↑↓ and Page Up/Down choose, Enter runs, Esc closes. Closing gives the
/// keyboard back to what had it before (the message box when that is gone).
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    /// <summary>What had the keyboard when the palette opened.</summary>
    private IInputElement? _returnFocus;
    /// <summary>Shown since the last time it closed (the binding hiding it at start gives nothing back).</summary>
    private bool _shown;

    public CommandPalette()
    {
        InitializeComponent();
        Scrim.PointerPressed += (_, e) =>
        {
            Vm?.ClosePaletteCommand.Execute(null);
            e.Handled = true;
        };
        PaletteBox.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
        // A click on an entry runs it (the list never takes the keyboard from the box)
        PaletteList.AddHandler(TappedEvent, (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: PaletteItemViewModel item } && Vm is { } vm)
            {
                e.Handled = true;
                _ = vm.RunPaletteItemCommand.ExecuteAsync(item);
            }
        });
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;
        if (change.NewValue is true)
        {
            _shown = true;
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            _returnFocus = focused is Visual v && this.IsVisualAncestorOf(v) ? null : focused;
            if (!PaletteBox.Focus(NavigationMethod.Pointer))
                Dispatcher.UIThread.Post(() => PaletteBox.Focus(NavigationMethod.Pointer), DispatcherPriority.Background);
        }
        else if (_shown)
        {
            _shown = false;
            var back = _returnFocus;
            _returnFocus = null;
            if (back is Control { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true } control
                && TopLevel.GetTopLevel(control) is not null && control.Focus())
                return;
            Vm?.FocusComposer();
        }
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        switch (e.Key)
        {
            case Key.Up: vm.MovePaletteSelection(-1); break;
            case Key.Down: vm.MovePaletteSelection(1); break;
            case Key.PageUp: vm.MovePaletteSelection(-8); break;
            case Key.PageDown: vm.MovePaletteSelection(8); break;
            case Key.Enter:
                _ = vm.RunPaletteItemCommand.ExecuteAsync(vm.SelectedPaletteItem);
                break;
            case Key.Escape:
                vm.ClosePaletteCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Esc with the keyboard elsewhere on the card (after a click on its edge).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm is { } vm)
        {
            vm.ClosePaletteCommand.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}

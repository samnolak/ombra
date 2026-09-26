using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Controls;

/// <summary>
/// Dictation mode for the composer (DataContext = <see cref="MainViewModel"/>). Shown in place of the composer's text
/// area while <see cref="MainViewModel.IsDictationBarVisible"/>; takes keyboard focus when it appears so Esc / Enter
/// reach it (see <see cref="HandleKey"/>).
/// </summary>
public sealed partial class DictationBar : UserControl
{
    public DictationBar()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The dictation keys, for the window's KeyDown handler (tunnel) as well as the bar itself: Ctrl/⌘+Shift+Space
    /// toggles dictation anywhere; while the bar shows, Esc cancels (or dismisses an error) and Enter finishes.
    /// Returns true (and marks the event handled) when the key was one of them.
    /// </summary>
    public static bool HandleKey(MainViewModel vm, KeyEventArgs e)
    {
        if (e.Handled) return false;
        if (IsToggleGesture(e.Key, e.KeyModifiers))
        {
            vm.ToggleDictationCommand.Execute(null);
            return e.Handled = true;
        }
        if (!vm.IsDictationBarVisible || e.KeyModifiers != KeyModifiers.None) return false;
        switch (e.Key)
        {
            case Key.Escape:
                vm.CancelDictationCommand.Execute(null);
                return e.Handled = true;
            case Key.Enter or Key.Return:
                // Finish while recording; swallowed otherwise (it must not reach the hidden composer and send).
                if (vm.HasDictationError) vm.CancelDictationCommand.Execute(null);
                else if (vm.FinishDictationCommand.CanExecute(null)) vm.FinishDictationCommand.Execute(null);
                return e.Handled = true;
            default:
                return false;
        }
    }

    /// <summary>Ctrl+Shift+Space (⌘+Shift+Space on macOS).</summary>
    public static bool IsToggleGesture(Key key, KeyModifiers modifiers) =>
        key == Key.Space && modifiers == (CommandModifier | KeyModifiers.Shift);

    /// <summary>The platform's command key: ⌘ on macOS, Ctrl elsewhere.</summary>
    public static KeyModifiers CommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>The toggle shortcut as a gesture (for a Window KeyBinding or a tooltip).</summary>
    public static KeyGesture ToggleGesture => new(Key.Space, CommandModifier | KeyModifiers.Shift);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is MainViewModel vm) HandleKey(vm, e);
        base.OnKeyDown(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>()) FocusSoon();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsVisible) FocusSoon();
    }

    // After layout: the composer's text box it replaces may still hold focus in this pass.
    private void FocusSoon() => Dispatcher.UIThread.Post(() =>
    {
        if (IsEffectivelyVisible && !IsKeyboardFocusWithin) Focus();
    }, DispatcherPriority.Background);
}

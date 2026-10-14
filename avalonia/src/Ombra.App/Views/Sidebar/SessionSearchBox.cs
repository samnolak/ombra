using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Sidebar;

/// <summary>The sidebar's search field, opened by the header's search icon: focused when it appears; leaving it while
/// empty closes it again (Esc clears the text, then hands the keyboard back to the message box: MainWindow.Escape).</summary>
public sealed class SessionSearchBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    public SessionSearchBox() => LostFocus += (_, _) => Dispatcher.UIThread.Post(CloseIfEmpty, DispatcherPriority.Background);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && DataContext is MainViewModel { IsSessionSearchOpen: true })
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Background);
    }

    /// <summary>Closes only when the keyboard went elsewhere in the window: a window sent to the background keeps it.</summary>
    private void CloseIfEmpty()
    {
        if (IsFocused || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is null) return;
        if (DataContext is MainViewModel { SessionFilter.Length: 0 } vm) vm.IsSessionSearchOpen = false;
    }
}

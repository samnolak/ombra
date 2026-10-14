using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Sidebar;

/// <summary>A sidebar project's name field: focused with its text selected when shown, Enter or leaving it keeps the
/// name, Esc drops it.</summary>
public sealed class ProjectNameBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    public ProjectNameBox()
    {
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        LostFocus += (_, _) => (DataContext as SessionGroupViewModel)?.CommitRenameCommand.Execute(null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
            Dispatcher.UIThread.Post(() => { Focus(); SelectAll(); }, DispatcherPriority.Background);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SessionGroupViewModel g) return;
        if (e.Key == Key.Enter) g.CommitRenameCommand.Execute(null);
        else if (e.Key == Key.Escape) g.CancelRenameCommand.Execute(null);
        else return;
        e.Handled = true;
    }
}

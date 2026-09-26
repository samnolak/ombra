using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views.Session;

/// <summary>The session card; a card that asks for text puts the keyboard there, Enter confirms and Esc closes.</summary>
public partial class SessionCard : UserControl
{
    public SessionCard()
    {
        InitializeComponent();
        CardInput.AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SessionCardViewModel { ShowInput: true })
            Dispatcher.UIThread.Post(() => CardInput.Focus(), DispatcherPriority.Background);
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SessionCardViewModel card) return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && card.Primary?.Command is { } go && go.CanExecute(null))
        {
            e.Handled = true;
            go.Execute(null);
        }
        else if (e.Key == Key.Escape && card.DismissCommand is { } close)
        {
            e.Handled = true;
            close.Execute(null);
        }
    }
}

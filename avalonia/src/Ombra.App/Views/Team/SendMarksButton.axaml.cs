using Avalonia.Controls;
using Avalonia.Interactivity;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Team;

public partial class SendMarksButton : UserControl
{
    public SendMarksButton() => InitializeComponent();

    private async void OnSend(object? sender, RoutedEventArgs e)
    {
        SendMarksToButton.Flyout?.Hide();
        if (DataContext is MainViewModel vm) await vm.SendMarksToTeamCommand.ExecuteAsync(null);
    }
}

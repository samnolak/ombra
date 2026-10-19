using Avalonia.Controls;
using Avalonia.Input;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Team;

public partial class ShareDialog : UserControl
{
    public ShareDialog()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            // Opened: the keyboard goes to Share (Enter sends, Esc cancels from there).
            if (e.Property == IsVisibleProperty && IsVisible) Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<Button>("ShareSend")?.Focus());
        };
    }

    /// <summary>A click outside the card cancels.</summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Share.CancelShareCommand.Execute(null);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ombra.App.ViewModels;
using Ombra.App.Localization;

namespace Ombra.App.Controls;

/// <summary>
/// The image viewer overlay (DataContext = <see cref="MainViewModel"/>). It takes the keyboard when it opens and keeps it
/// (Tab cycles inside), so Esc, ←/→ and Space act on the image before anything behind it sees them.
/// </summary>
public sealed partial class ImageViewer : UserControl
{
    public ImageViewer()
    {
        InitializeComponent();
        Backdrop.PointerPressed += (_, e) =>
        {
            Vm?.CloseImageViewerCommand.Execute(null);
            e.Handled = true;
        };
        FitImage.Tapped += (_, _) => Vm?.ToggleImageZoomCommand.Execute(null);
        ActualImage.Tapped += (_, _) => Vm?.ToggleImageZoomCommand.Execute(null);
        CopyImageButton.Click += async (_, _) => await CopyAsync();
        // Before a focused button: Space must zoom (not press it), and arrows must step (not move the focus)
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (Vm is not { IsImageViewerOpen: true } vm || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Escape: vm.CloseImageViewerCommand.Execute(null); break;
            case Key.Left: vm.PreviousImageCommand.Execute(null); break;
            case Key.Right: vm.NextImageCommand.Execute(null); break;
            case Key.Space: vm.ToggleImageZoomCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    private async Task CopyAsync()
    {
        if (Vm is not { ViewerImage: { } bitmap } vm) return;
        vm.ViewerNote = "";
        await ContextMenus.CopyImageAsync(CopyImageButton, bitmap, failure: error => vm.ViewerNote = Loc.T("Couldn't copy") + ": " + error);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.NewValue is true)
            Dispatcher.UIThread.Post(() => CloseButton.Focus(NavigationMethod.Pointer), DispatcherPriority.Background);
    }
}

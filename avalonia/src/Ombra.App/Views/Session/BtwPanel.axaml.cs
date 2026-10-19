using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Session;

/// <summary>
/// The BTW panel; opening it puts the keyboard in its question box, Enter asks, Esc closes the panel (never stops omp's
/// run), and a new answer keeps the list at its top (newest first).
/// </summary>
public partial class BtwPanel : UserControl
{
    private MainViewModel? _vm;

    public BtwPanel()
    {
        InitializeComponent();
        BtwQuestionBox.AddHandler(KeyDownEvent, OnQuestionKey, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName is nameof(MainViewModel.IsBtwOpen) or nameof(MainViewModel.BtwFollowUp) && _vm.IsBtwOpen)
            Dispatcher.UIThread.Post(() => BtwQuestionBox.Focus(), DispatcherPriority.Background);
        else if (e.PropertyName == nameof(MainViewModel.IsBtwRunning) && _vm.IsBtwRunning)
            BtwScroller.ScrollToHome();
    }

    private void OnQuestionKey(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm) return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (vm.AskBtwCommand.CanExecute(null)) vm.AskBtwCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.IsBtwOpen = false;
        }
    }
}

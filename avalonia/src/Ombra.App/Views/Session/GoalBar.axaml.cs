using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Session;

/// <summary>The goal bar; its editor takes the keyboard when it opens, Enter sets the goal and Esc closes the editor.</summary>
public partial class GoalBar : UserControl
{
    private MainViewModel? _vm;

    public GoalBar()
    {
        InitializeComponent();
        GoalDraftBox.AddHandler(KeyDownEvent, OnDraftKey, RoutingStrategies.Tunnel);
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
        if (e.PropertyName == nameof(MainViewModel.IsEditingGoal) && _vm?.IsEditingGoal == true)
            Dispatcher.UIThread.Post(() => GoalDraftBox.Focus(), DispatcherPriority.Background);
    }

    private void OnDraftKey(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm) return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (vm.SaveGoalCommand.CanExecute(null)) vm.SaveGoalCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelGoalEditCommand.Execute(null);
        }
    }
}

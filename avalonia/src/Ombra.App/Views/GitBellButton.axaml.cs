using System.ComponentModel;
using Avalonia.Controls;
using Ombra.App.ViewModels;

namespace Ombra.App.Views;

/// <summary>The header's bell and its popover; the view model's IsOpen follows it both ways (the composer's hint opens it).</summary>
public partial class GitBellButton : UserControl
{
    private GitBellViewModel? _vm;

    public GitBellButton()
    {
        InitializeComponent();
        if (BellButton.Flyout is not Flyout flyout) return;
        flyout.Opened += (_, _) => { if (_vm is { IsOpen: false }) _vm.IsOpen = true; };
        flyout.Closed += (_, _) => { if (_vm is { IsOpen: true }) _vm.IsOpen = false; };
    }

    /// <summary>The button that opens the popover (tests, the feature smoke).</summary>
    public Button Button => BellButton;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as GitBellViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GitBellViewModel.IsOpen) || BellButton.Flyout is not Flyout flyout || _vm is null) return;
        if (_vm.IsOpen && !flyout.IsOpen) flyout.ShowAt(BellButton);
        else if (!_vm.IsOpen && flyout.IsOpen) flyout.Hide();
    }
}

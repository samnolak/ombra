using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Settings;

public partial class McpServerForm : UserControl
{
    private ConnectorsViewModel? _vm;

    public McpServerForm()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as ConnectorsViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    /// <summary>The form opens with the caret in its first field (the name; the command or URL when editing).</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectorsViewModel.IsFormOpen) || _vm is not { IsFormOpen: true } vm) return;
        Dispatcher.UIThread.Post(() =>
        {
            TextBox box = !vm.IsEditing ? ConnectorNameBox : vm.FormIsStdio ? ConnectorCommandBox : ConnectorUrlBox;
            if (box.IsEffectivelyVisible) box.Focus();
        }, DispatcherPriority.Background);
    }
}

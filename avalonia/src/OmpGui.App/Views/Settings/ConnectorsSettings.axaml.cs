using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views.Settings;

public partial class ConnectorsSettings : UserControl
{
    private readonly HashSet<Flyout> _menus = [];
    private ConnectorsViewModel? _vm;

    public ConnectorsSettings()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, (_, e) => ConnectorsSettingsMenus.CloseOnChoice(e.Source, _menus), RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.CopyRequested -= Copy;
                _vm.PropertyChanged -= OnVmChanged;
            }
            _vm = (DataContext as MainViewModel)?.Connectors;
            if (_vm is not null)
            {
                _vm.CopyRequested += Copy;
                _vm.PropertyChanged += OnVmChanged;
            }
        };
    }

    private void Copy(string text) => ConnectorsSettingsMenus.Copy(this, text);

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

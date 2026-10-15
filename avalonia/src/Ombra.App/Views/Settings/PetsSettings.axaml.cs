using Avalonia.Controls;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Settings;

/// <summary>Settings page "Pets" (DataContext = <see cref="MainViewModel"/>; its content binds <see cref="MainViewModel.Pets"/>).</summary>
public partial class PetsSettings : UserControl
{
    public PetsSettings() => InitializeComponent();
}

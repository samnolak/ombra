using Avalonia.Controls;

namespace OmpGui.App.Views.Settings;

/// <summary>Settings → Model providers, plan usage (DataContext = MainViewModel; binds its ProviderUsage).</summary>
public partial class ProviderUsageSettings : UserControl
{
    public ProviderUsageSettings() => InitializeComponent();
}

using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OmpGui.App.ViewModels;

/// <summary>A page of the settings (left navigation, as in Claude Code's settings).</summary>
/// <param name="group">The heading listed above this page when it starts a group of pages; null inside a group.</param>
public sealed partial class SettingsCategoryViewModel(string key, string title, string iconKey, string? group = null) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string? Group { get; } = group;
    public bool HasGroup => Group is not null;
    public Geometry? Icon => Application.Current?.TryGetResource(iconKey, null, out var v) == true ? v as Geometry : null;

    [ObservableProperty]
    private bool _isCurrent;
}

public partial class MainViewModel
{
    /// <summary>Settings pages in the order they are listed, in groups. Each new page is a UserControl in Views/Settings.</summary>
    public IReadOnlyList<SettingsCategoryViewModel> SettingsCategories { get; } =
    [
        new("general", "General", "IconSettings"),
        new("providers", "Model providers", "IconKey", "omp"),
        new("connectors", "Connectors", "IconPlug"),
        new("plugins", "Plugins and skills", "IconGrid"),
        new("computer", "Computer use", "IconMonitor"),
        new("git", "Git and worktrees", "IconBranch"),
        new("ssh", "SSH hosts", "IconServer"),
        new("pets", "Pets", "IconPaw", "This app"),
        new("updates", "Updates", "IconDownload"),
        new("advanced", "Advanced", "IconCode", "Troubleshooting"),
        new("diagnostics", "Diagnostics", "IconPulse"),
    ];

    /// <summary>The settings page shown (a <see cref="SettingsCategoryViewModel.Key"/>).</summary>
    [ObservableProperty]
    private string _settingsCategory = "general";

    /// <summary>Raised with the page's key when a settings page is shown (settings opened, or another page picked): load its data then.</summary>
    public event Action<string>? SettingsCategoryShown;

    partial void OnSettingsCategoryChanged(string value)
    {
        foreach (var c in SettingsCategories) c.IsCurrent = c.Key == value;
        if (IsSettingsOpen) SettingsCategoryShown?.Invoke(value);
    }

    partial void OnIsSettingsOpenChanged(bool value)
    {
        foreach (var c in SettingsCategories) c.IsCurrent = c.Key == SettingsCategory;
        OnPropertyChanged(nameof(ShowSidebar)); // the settings page's own navigation takes the sidebar's place
        if (value) SettingsCategoryShown?.Invoke(SettingsCategory);
    }

    [RelayCommand]
    private void ShowSettingsCategory(string? key)
    {
        if (key is not null && SettingsCategories.Any(c => c.Key == key)) SettingsCategory = key;
    }

    /// <summary>Opens the settings on one page (e.g. "connectors" from the "+" menu).</summary>
    public async Task OpenSettingsAtAsync(string key)
    {
        ShowSettingsCategory(key);
        await OpenSettingsCommand.ExecuteAsync(null);
    }
}

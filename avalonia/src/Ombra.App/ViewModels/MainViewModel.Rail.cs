using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>
/// The icon rail left of the sidebar (MainWindow): Chats, Plugins, MCP (Connectors) and Skills at the top, Settings at
/// the bottom. Chats goes back to the conversation; the others open their settings page. The one shown is lit. Skills is
/// a section of the Plugins and skills page: the rail scrolls the page to it (<see cref="SettingsAnchorRequested"/>).
/// </summary>
public partial class MainViewModel
{
    /// <summary>The part of the settings page the rail asked for ("skills"), until another page is shown or settings close.</summary>
    [ObservableProperty] private string? _settingsAnchor;

    /// <summary>The view scrolls the settings page to this part ("skills": the Skills section of Plugins and skills).</summary>
    public event Action<string>? SettingsAnchorRequested;

    public bool IsChatsRailOn => !IsSettingsOpen;
    public bool IsPluginsRailOn => IsSettingsOpen && SettingsCategory == "plugins" && SettingsAnchor != "skills";
    public bool IsSkillsRailOn => IsSettingsOpen && SettingsCategory == "plugins" && SettingsAnchor == "skills";
    public bool IsConnectorsRailOn => IsSettingsOpen && SettingsCategory == "connectors";
    public bool IsSettingsRailOn => IsSettingsOpen && SettingsCategory is not ("plugins" or "connectors");

    public static string ChatsRailTip { get; } = Loc.T("Chats");
    public static string PluginsRailTip { get; } = Loc.T("Plugins");
    public static string ConnectorsRailTip { get; } = Loc.T("MCP servers (Connectors)");
    public static string SkillsRailTip { get; } = Loc.T("Skills");

    partial void OnSettingsAnchorChanged(string? value) => OnRailChanged();

    private void OnRailChanged()
    {
        OnPropertyChanged(nameof(IsChatsRailOn));
        OnPropertyChanged(nameof(IsPluginsRailOn));
        OnPropertyChanged(nameof(IsSkillsRailOn));
        OnPropertyChanged(nameof(IsConnectorsRailOn));
        OnPropertyChanged(nameof(IsSettingsRailOn));
    }

    /// <summary>The rail's Skills: Settings → Plugins and skills, scrolled to its Skills section.</summary>
    [RelayCommand]
    private async Task OpenSkillsPage()
    {
        await OpenSettingsAtAsync("plugins");
        if (!IsSettingsOpen) return;
        SettingsAnchor = "skills";
        SettingsAnchorRequested?.Invoke("skills");
    }

    /// <summary>The rail's Settings: the settings as last left, but not on a page the rail has its own button for.</summary>
    [RelayCommand]
    private Task OpenSettingsFromRail() =>
        SettingsCategory is "plugins" or "connectors" ? OpenSettingsAtAsync("general") : OpenSettingsCommand.ExecuteAsync(null);
}

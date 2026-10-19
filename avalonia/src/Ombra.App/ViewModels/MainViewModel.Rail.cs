using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>
/// The icon rail left of the sidebar (MainWindow): Chats, Plugins (plugins, MCP servers and skills on one page), Team and
/// Browser at the top, Settings at the bottom. Chats goes back to the conversation; Plugins opens its settings page. The
/// one shown is lit.
/// </summary>
public partial class MainViewModel
{
    public bool IsChatsRailOn => !IsSettingsOpen && !IsBrowserPageOpen && !IsTeamPageOpen;
    public bool IsBrowserRailOn => !IsSettingsOpen && IsBrowserPageOpen;
    public bool IsTeamRailOn => !IsSettingsOpen && IsTeamPageOpen;
    public bool IsPluginsRailOn => IsSettingsOpen && SettingsCategory == "plugins";
    public bool IsSettingsRailOn => IsSettingsOpen && SettingsCategory != "plugins";
    /// <summary>The pet lives with the conversation (on its message box); no page (settings, Team, Browser) shows it over its content.</summary>
    public bool ShowPet => IsChatsRailOn;

    public static string ChatsRailTip { get; } = Loc.T("Chats");
    public static string PluginsRailTip { get; } = Loc.T("Plugins, MCP and skills");
    public static string BrowserRailTip { get; } = Loc.T("Browser");

    private void OnRailChanged()
    {
        OnPropertyChanged(nameof(IsChatsRailOn));
        OnPropertyChanged(nameof(IsPluginsRailOn));
        OnPropertyChanged(nameof(IsSettingsRailOn));
        OnPropertyChanged(nameof(IsBrowserRailOn));
        OnPropertyChanged(nameof(ShowBrowserPage));
        OnPropertyChanged(nameof(IsTeamRailOn));
        OnPropertyChanged(nameof(ShowTeamPage));
        OnPropertyChanged(nameof(ShowPet));
    }

    /// <summary>The rail's Settings: the settings as last left, but not on the page the rail has its own button for.</summary>
    [RelayCommand]
    private Task OpenSettingsFromRail() =>
        SettingsCategory == "plugins" ? OpenSettingsAtAsync("general") : OpenSettingsCommand.ExecuteAsync(null);
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ombra.App.ViewModels;

/// <summary>
/// The rail's Browser: the user's own browsing, apart from the agent's pane (<see cref="Preview"/>). A full-size page
/// in the conversation's and the sidebar's place (MainWindow BrowserPage): with Nightly, one whole Nightly browser
/// window of the same runtime (its own sidebar, tabs, spaces, URL bar, library and menus; MainWindow.BrowserWindow.cs);
/// with the built-in preview, its own tabs with the same cards, downloads and site permissions. The agent never drives it.
/// </summary>
public sealed partial class MainViewModel
{
    public PreviewViewModel Browser => field ??= NewBrowser();

    /// <summary>The Browser page is the one shown (unless settings are open over it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrowserPage))]
    private bool _isBrowserPageOpen;

    public bool ShowBrowserPage => IsBrowserPageOpen && !IsSettingsOpen;

    partial void OnIsBrowserPageOpenChanged(bool value) => OnRailChanged();

    private PreviewViewModel NewBrowser()
    {
        // The agent's pane's site permissions and download folder (one file, one owner): the same object.
        var b = new PreviewViewModel(Preview.SiteSettings)
        {
            NeedsNightlyConsent = Preview.NeedsNightlyConsent,
            NightlyConsentChosen = Preview.NightlyConsentChosen,
            CheckNightly = Preview.CheckNightly,
            InstallNightlyRuntime = Preview.InstallNightlyRuntime,
        };
        b.WantsNightly = Preview.WantsNightly;
        return b;
    }

    /// <summary>The rail's Browser: settings close, the Browser page shows.</summary>
    [RelayCommand]
    private void OpenBrowserPage()
    {
        if (IsSettingsOpen) CloseSettingsCommand.Execute(null);
        IsTeamPageOpen = false;
        IsBrowserPageOpen = true;
    }

    /// <summary>The rail's Chats: back to the conversation (settings and the Browser page close; its tabs stay).</summary>
    [RelayCommand]
    private void ShowChats()
    {
        if (IsSettingsOpen) CloseSettingsCommand.Execute(null);
        IsBrowserPageOpen = false;
        IsTeamPageOpen = false;
    }
}

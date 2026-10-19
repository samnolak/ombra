using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// Browser preview (Controls/PreviewPanel, DataContext = <see cref="Preview"/>), a tab of the right panel: shows the web
/// app the agent is building. Local addresses in tool output (dev servers) are offered as suggestions; nothing opens on
/// its own.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How far back a streaming output is scanned again, for an address split across two updates.</summary>
    private const int PreviewRescan = 256;

    private readonly Dictionary<long, int> _previewScanned = [];
    private long _previewEpoch = -1;

    public PreviewViewModel Preview => field ??= NewPreview();

    /// <summary>
    /// The right panel shows the browser. Set true, the panel opens on it (its pages join the strip); set false while it
    /// is shown, its pages leave the strip and the panel shows its next tab (or hides).
    /// </summary>
    public bool IsPreviewOpen
    {
        get => ActivePane == SidePane.Browser;
        set
        {
            if (value) ActivePane = SidePane.Browser;
            else if (IsPreviewOpen) CloseBrowser();
        }
    }

    /// <summary>The browser's pages leave the right panel; the tab next to them is shown, or the panel hides.</summary>
    private void CloseBrowser()
    {
        var at = PanelTabs.FirstOrDefault(t => t.IsPage) is { } first ? PanelTabs.IndexOf(first) : 0;
        RemoveBrowserTabs();
        if (!HasTabFor(ActivePane)) ShowNeighbour(at);
    }

    /// <summary>Ctrl/⌘+Shift+S: the preview opens if needed and element selection toggles (when a page is shown).</summary>
    [RelayCommand]
    private void SelectElement()
    {
        IsPreviewOpen = true;
        if (Preview.ToggleAnnotateCommand.CanExecute(null)) Preview.ToggleAnnotateCommand.Execute(null);
    }

    /// <summary>Ctrl/⌘+Shift+M: the preview opens if needed and device mode turns on or off (Chrome's device toolbar key).</summary>
    [RelayCommand]
    private void ToggleDeviceMode()
    {
        IsPreviewOpen = true;
        Preview.ToggleDeviceCommand.Execute(null);
    }


    private PreviewViewModel NewPreview()
    {
        // "Always allow on this site" and the download folder are kept next to the client settings (this run only without them)
        var folder = _settings?.Path is { } settingsPath ? Path.GetDirectoryName(settingsPath) : null;
        var p = new PreviewViewModel(new Ombra.ClientCore.Browser.BrowserSiteSettings(
            string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "browser-sites.json"))) { Marks = Marks };
        p.ShowRequested += () => IsPreviewOpen = true;
        // The right panel's strip lists the browser's pages: it follows their list and the page shown
        p.Tabs.CollectionChanged += (_, _) => SyncPageTabs();
        p.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewViewModel.ActiveTab)) SyncActiveTab();
        };
        // Marks on the page are the chat's marks (MainViewModel.Marks.cs): the panel's pins follow the same list
        return p;
    }

    /// <summary>
    /// Called from <see cref="Apply"/> for every transcript item before its row is created or updated: offers the local
    /// addresses in a tool's new output to the preview. Unchanged items and text already scanned are skipped.
    /// </summary>
    private void OfferPreviewUrls(TranscriptItem item)
    {
        if (item is not ToolItem { Output: { Length: > 0 } output }) return;
        if (_open.RowsByKey.TryGetValue(item.Key, out var row) && ReferenceEquals(row.Model, item)) return;
        if (_previewEpoch != _open.TranscriptEpoch)
        {
            _previewScanned.Clear();
            _previewEpoch = _open.TranscriptEpoch;
        }
        var from = _previewScanned.TryGetValue(item.Key, out var done) && done <= output.Length ? Math.Max(0, done - PreviewRescan) : 0;
        _previewScanned[item.Key] = output.Length;
        if (from < output.Length) Preview.OfferUrlsFrom(from == 0 ? output : output[from..]);
    }
}

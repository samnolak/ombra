using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// Browser preview panel (Controls/PreviewPanel, DataContext = <see cref="Preview"/>): shows the web app the agent is
/// building. Local addresses in tool output (dev servers) are offered as suggestions; nothing opens on its own.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How far back a streaming output is scanned again, for an address split across two updates.</summary>
    private const int PreviewRescan = 256;

    private readonly Dictionary<long, int> _previewScanned = [];
    private long _previewEpoch = -1;

    public PreviewViewModel Preview => field ??= NewPreview();

    [ObservableProperty] private bool _isPreviewOpen;

    [RelayCommand]
    private void TogglePreview() => IsPreviewOpen = !IsPreviewOpen;

    [RelayCommand]
    private void ShowPreview() => IsPreviewOpen = true;

    /// <summary>Ctrl/⌘+Shift+S: the preview opens if needed and element selection toggles (when a page is shown).</summary>
    [RelayCommand]
    private void SelectElement()
    {
        IsPreviewOpen = true;
        if (Preview.ToggleAnnotateCommand.CanExecute(null)) Preview.ToggleAnnotateCommand.Execute(null);
    }

    /// <summary>The comments on the page, as text after the message; they are cleared once taken.</summary>
    private string TakeAnnotations()
    {
        if (Preview.Annotations.Count == 0) return "";
        var text = PreviewViewModel.BuildPrompt(Preview.Annotations);
        Preview.IsAnnotating = false;
        Preview.Annotations.Clear();
        return text;
    }

    private PreviewViewModel NewPreview()
    {
        var p = new PreviewViewModel();
        p.CloseRequested += () => IsPreviewOpen = false;
        // Comments on the page go with the next message: they count as content, and the panel's Send sends it
        p.Annotations.CollectionChanged += (_, _) =>
        {
            SendCommand.NotifyCanExecuteChanged();
            SteerCommand.NotifyCanExecuteChanged();
        };
        return p;
    }

    /// <summary>
    /// Called from <see cref="Apply"/> for every transcript item before its row is created or updated: offers the local
    /// addresses in a tool's new output to the preview. Unchanged items and text already scanned are skipped.
    /// </summary>
    private void OfferPreviewUrls(TranscriptItem item)
    {
        if (item is not ToolItem { Output: { Length: > 0 } output }) return;
        if (_rowsByKey.TryGetValue(item.Key, out var row) && ReferenceEquals(row.Model, item)) return;
        if (_previewEpoch != _transcriptEpoch)
        {
            _previewScanned.Clear();
            _previewEpoch = _transcriptEpoch;
        }
        var from = _previewScanned.TryGetValue(item.Key, out var done) && done <= output.Length ? Math.Max(0, done - PreviewRescan) : 0;
        _previewScanned[item.Key] = output.Length;
        if (from < output.Length) Preview.OfferUrlsFrom(from == 0 ? output : output[from..]);
    }
}

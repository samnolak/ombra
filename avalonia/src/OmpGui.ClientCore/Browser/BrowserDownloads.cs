using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OmpGui.ClientCore.Browser;

public enum BrowserDownloadState
{
    /// <summary>Waiting for where to save it (the download card, or the save panel).</summary>
    Asking,
    Saving,
    Finished,
    Failed,
    /// <summary>Not saved: nobody chose a place, or it was stopped.</summary>
    Canceled,
}

/// <summary>
/// One download of the browser pane: what the engine reports (name, size, progress, result) and how to stop it. Set by
/// the engine hooks on the main thread; observable for the pane's downloads list.
/// </summary>
public sealed class BrowserDownload : INotifyPropertyChanged
{
    private static long _nextId;

    public BrowserDownload(Uri? url, Uri? pageUrl = null)
    {
        Url = url;
        PageUrl = pageUrl;
    }

    public long Id { get; } = Interlocked.Increment(ref _nextId);
    public Uri? Url { get; }
    public Uri? PageUrl { get; }

    /// <summary>omp's browser tool caused it.</summary>
    public bool CausedByAgent { get; init; }

    public string FileName { get; set => Set(ref field, value); } = "";

    /// <summary>Where it is saved (set once a place was chosen).</summary>
    public string? Path { get; set => Set(ref field, value); }

    public BrowserDownloadState State { get; set => Set(ref field, value, nameof(IsActive), nameof(IsFinished), nameof(StatusText)); }

    public long BytesReceived { get; set => Set(ref field, value, nameof(StatusText), nameof(Progress)); }

    /// <summary>The size, when the server said it (else -1).</summary>
    public long TotalBytes { get; set => Set(ref field, value, nameof(StatusText), nameof(Progress)); } = -1;

    public string? Error { get; set => Set(ref field, value, nameof(StatusText)); }

    public bool IsActive => State is BrowserDownloadState.Asking or BrowserDownloadState.Saving;
    public bool IsFinished => State == BrowserDownloadState.Finished;

    /// <summary>0…1 while the size is known; -1 otherwise.</summary>
    public double Progress => TotalBytes > 0 ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1) : -1;

    /// <summary>What the downloads list says under the name.</summary>
    public string StatusText => State switch
    {
        BrowserDownloadState.Asking => "Waiting for a place to save it",
        BrowserDownloadState.Saving => TotalBytes > 0
            ? $"{FormatSize(BytesReceived)} of {FormatSize(TotalBytes)}"
            : BytesReceived > 0 ? FormatSize(BytesReceived) : "Starting…",
        BrowserDownloadState.Finished => TotalBytes > 0 ? $"Saved · {FormatSize(TotalBytes)}" : "Saved",
        BrowserDownloadState.Failed => Error is { Length: > 0 } e ? $"Failed: {e}" : "Failed",
        _ => "Canceled",
    };

    /// <summary>Stops it (set by the engine hooks while it runs); false when it can no longer be stopped.</summary>
    public Func<bool>? Canceller { get; set; }

    public bool Cancel() => IsActive && Canceller is { } cancel && cancel();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string? also1 = null, string? also2 = null, string? also3 = null,
        [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Notify(name);
        Notify(also1);
        Notify(also2);
        Notify(also3);
    }

    private void Notify(string? name)
    {
        if (name is not null) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

/// <summary>
/// The pane's downloads: where each one is saved, and the list the pane shows. A download is only written where the
/// user chose: "Save" on the card goes to the folder they picked last time (<see cref="BrowserSiteSettings.DownloadFolder"/>);
/// until they picked one, and for "Save as…", the system's save panel asks (<see cref="PickSaveLocation"/>). Nothing
/// is saved to the Downloads folder (or anywhere) without that choice.
/// </summary>
public sealed class BrowserDownloads(BrowserSiteSettings settings)
{
    public BrowserSiteSettings Settings { get; } = settings;

    /// <summary>The downloads shown in the pane, newest last (saving, finished, failed, stopped).</summary>
    public ObservableCollection<BrowserDownload> Items { get; } = [];

    /// <summary>
    /// The system's save panel (set by the view): the suggested file name and the folder to start in → the chosen file
    /// path, or null. A file the user chose to replace may exist.
    /// </summary>
    public Func<string, string?, Task<string?>>? PickSaveLocation { get; set; }

    /// <summary>
    /// Where a download goes: asks <paramref name="broker"/> (the download card), then the remembered folder or the save
    /// panel. Null: not downloaded (no broker, card cancelled, panel cancelled).
    /// </summary>
    public async Task<string?> ChooseDestinationAsync(DownloadRequest request, DialogBroker? broker, CancellationToken ct)
    {
        if (broker is null) return null;
        var answer = await broker.DownloadAsync(request, ct).ConfigureAwait(true);
        var name = SafeFileName(request.FileName);
        return answer switch
        {
            DownloadAnswer.Save when Settings.DownloadFolder is { } folder && Directory.Exists(folder) => UniquePath(folder, name),
            DownloadAnswer.Save or DownloadAnswer.SaveAs => await PickAsync(name, ct).ConfigureAwait(true),
            _ => null,
        };
    }

    private async Task<string?> PickAsync(string name, CancellationToken ct)
    {
        if (PickSaveLocation is not { } pick || ct.IsCancellationRequested) return null;
        var path = await pick(name, Settings.DownloadFolder).ConfigureAwait(true);
        if (string.IsNullOrEmpty(path) || ct.IsCancellationRequested) return null;
        var folder = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder)) return null;
        Settings.DownloadFolder = folder;
        // The user confirmed replacing it in the panel; the engine refuses to write over an existing file
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            return UniquePath(folder, System.IO.Path.GetFileName(path));
        }
        return path;
    }

    /// <summary>Lists <paramref name="download"/> once it is being saved (asked-and-cancelled ones are not shown).</summary>
    public void Track(BrowserDownload download)
    {
        if (download.State is BrowserDownloadState.Asking || download.Path is null) return;
        if (!Items.Contains(download)) Items.Add(download);
    }

    /// <summary>Removes the finished, failed and stopped downloads from the list (files stay where they are).</summary>
    public void ClearDone()
    {
        foreach (var d in Items.Where(d => !d.IsActive).ToList()) Items.Remove(d);
    }

    /// <summary>A file name without folders or characters the file system refuses; "download" when nothing is left.</summary>
    public static string SafeFileName(string? name)
    {
        var s = System.IO.Path.GetFileName((name ?? "").Replace('\\', '/').Split('/')[^1]).Trim();
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Replace(':', '_').TrimStart('.');
        return s.Length == 0 ? "download" : s;
    }

    /// <summary><paramref name="name"/> in <paramref name="folder"/>, as "name (2).ext" when taken; always inside the folder.</summary>
    public static string UniquePath(string folder, string name)
    {
        name = SafeFileName(name);
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var ext = System.IO.Path.GetExtension(name);
        var path = System.IO.Path.Combine(folder, name);
        for (var i = 2; File.Exists(path) || Directory.Exists(path); i++) path = System.IO.Path.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }
}

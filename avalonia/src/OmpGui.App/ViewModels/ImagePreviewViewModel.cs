using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OmpGui.App.Services;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// One attached image as a thumbnail (a composer chip, a sent or queued message). It decodes off the UI thread the
/// first time something shows it, keeps the bitmap while its owner lives, and frees it with the owner.
/// <see cref="Group"/> is the images it sits with (its message's), for ←/→ in the viewer.
/// </summary>
public sealed partial class ImagePreview(ImageAttachment model, Func<IReadOnlyList<ImageAttachment>> group) : ObservableObject, IDisposable
{
    /// <summary>Thumbnail pixels on the shorter side: sharp at the largest preview (72 px) on a 2× display.</summary>
    public const int ThumbnailPixels = 160;

    private Bitmap? _thumbnail;
    private bool _loading, _disposed;

    public ImageAttachment Model { get; } = model;
    public IReadOnlyList<ImageAttachment> Group => group();
    public string Name => Model.Name;

    /// <summary>The decoded thumbnail; null until it is ready, and when the image cannot be shown (<see cref="Failed"/>).</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!_loading && !_disposed) _ = LoadAsync();
            return _thumbnail;
        }
    }

    /// <summary>The data is not an image the app can read (corrupt, an unsupported format), or omp kept no bytes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private bool _failed = model.Data.Length == 0;

    public string Tooltip => !Failed ? Name
        : Model.Data.Length == 0 ? "This image was not kept with the session"
        : $"{Name}: can't be shown (not an image the app can read)";

    private async Task LoadAsync()
    {
        _loading = true;
        var data = Model.Data;
        var bitmap = data.Length == 0 ? null : await Task.Run(() => ImageAttachments.DecodeThumbnail(data, ThumbnailPixels));
        if (_disposed)
        {
            bitmap?.Dispose();
            return;
        }
        if (bitmap is null)
        {
            Failed = true;
            return;
        }
        _thumbnail = bitmap;
        OnPropertyChanged(nameof(Thumbnail));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // After the frame that may still draw it
        if (_thumbnail is { } bitmap) Avalonia.Threading.Dispatcher.UIThread.Post(bitmap.Dispose, Avalonia.Threading.DispatcherPriority.Background);
        _thumbnail = null;
    }
}

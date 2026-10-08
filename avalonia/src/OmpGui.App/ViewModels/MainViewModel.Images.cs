using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Services;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// The image viewer (Controls/ImageViewer): an image from a message or the message box over the dimmed window, fitted
/// to it or at 100 %, ←/→ through the images it was sent with. The full image decodes off the UI thread.
/// </summary>
public sealed partial class MainViewModel
{
    private IReadOnlyList<ImageAttachment> _viewerImages = [];
    private int _viewerIndex;
    /// <summary>Counts the images shown, so a slow decode that finishes after the next one started is dropped.</summary>
    private int _viewerShown;

    [ObservableProperty] private bool _isImageViewerOpen;
    /// <summary>The image, decoded (null while it decodes, and when it cannot be shown).</summary>
    [ObservableProperty] private Bitmap? _viewerImage;
    [ObservableProperty] private bool _isViewerLoading;
    /// <summary>Why the image cannot be shown; null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViewerProblem))]
    private string? _viewerProblem;
    /// <summary>At 100 % (pixel for pixel, scrolling) instead of fitted to the window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewerZoomAction))]
    private bool _isViewerActualSize;
    [ObservableProperty] private string _viewerTitle = "";
    [ObservableProperty] private string _viewerDetail = "";
    /// <summary>What the last action did ("Copied"), until the next image.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViewerNote))]
    private string _viewerNote = "";

    public bool HasViewerProblem => ViewerProblem is not null;
    public bool HasViewerNote => ViewerNote.Length > 0;
    /// <summary>What the zoom button does now: its tooltip and accessible name. An icon, not "100 %", which read as the
    /// zoom level shown rather than as a button.</summary>
    public string ViewerZoomAction => IsViewerActualSize ? "Fit to window" : "Actual size";
    public bool HasViewerSiblings => _viewerImages.Count > 1;
    public ImageAttachment? ViewerAttachment => _viewerIndex < _viewerImages.Count ? _viewerImages[_viewerIndex] : null;

    [RelayCommand]
    private void OpenImage(ImagePreview? preview)
    {
        if (preview is null) return;
        var group = preview.Group;
        var index = IndexOf(group, preview.Model);
        _viewerImages = index >= 0 ? group : [preview.Model];
        IsViewerActualSize = false;
        ShowViewerImage(Math.Max(0, index));
        IsImageViewerOpen = true;
        OnPropertyChanged(nameof(HasViewerSiblings));
    }

    private static int IndexOf(IReadOnlyList<ImageAttachment> group, ImageAttachment image)
    {
        for (var i = 0; i < group.Count; i++)
            if (ReferenceEquals(group[i], image)) return i;
        return -1;
    }

    [RelayCommand]
    private void NextImage() => Step(1);

    [RelayCommand]
    private void PreviousImage() => Step(-1);

    private void Step(int by)
    {
        if (!IsImageViewerOpen || _viewerImages.Count < 2) return;
        IsViewerActualSize = false;
        ShowViewerImage((_viewerIndex + by + _viewerImages.Count) % _viewerImages.Count);
    }

    [RelayCommand]
    private void ToggleImageZoom()
    {
        if (ViewerImage is not null) IsViewerActualSize = !IsViewerActualSize;
    }

    [RelayCommand]
    private void CloseImageViewer()
    {
        if (!IsImageViewerOpen) return;
        IsImageViewerOpen = false;
        _viewerShown++;
        SetViewerImage(null);
        _viewerImages = [];
        FocusComposerRequested?.Invoke();
    }

    /// <summary>"Open in default app": the image as a file in the temp folder, opened by the system.</summary>
    [RelayCommand]
    private async Task OpenImageExternallyAsync()
    {
        if (ViewerAttachment is not { Data.Length: > 0 } image) return;
        try
        {
            var path = await Task.Run(() => ImageAttachments.SaveToTemp(image));
            ViewerNote = await FileOpeners.StartAsync(FileOpeners.OpenWithSystem(path), waitForSuccess: false) ? "" : "Could not open the image";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ViewerNote = "Could not save the image to open it: " + e.Message;
        }
    }

    private void ShowViewerImage(int index)
    {
        _viewerIndex = index;
        var shown = ++_viewerShown;
        var image = _viewerImages[index];
        SetViewerImage(null);
        ViewerTitle = image.Name;
        ViewerDetail = Position();
        ViewerNote = "";
        ViewerProblem = image.Data.Length == 0 ? "This image was not kept with the session." : null;
        OnPropertyChanged(nameof(ViewerAttachment));
        if (image.Data.Length > 0) _ = DecodeViewerImageAsync(image, shown);
    }

    private string Position() => _viewerImages.Count > 1 ? $"{_viewerIndex + 1} of {_viewerImages.Count}" : "";

    private async Task DecodeViewerImageAsync(ImageAttachment image, int shown)
    {
        IsViewerLoading = true;
        var decoded = await Task.Run(() => ImageAttachments.DecodeFull(image.Data));
        if (shown != _viewerShown)
        {
            decoded?.Bitmap.Dispose();
            return;
        }
        IsViewerLoading = false;
        if (decoded is not { } d)
        {
            ViewerProblem = "This image can't be shown: it is not an image the app can read.";
            return;
        }
        SetViewerImage(d.Bitmap);
        var size = $"{d.Size.Width} × {d.Size.Height}";
        ViewerDetail = Position() is { Length: > 0 } at ? $"{size} · {at}" : size;
    }

    private void SetViewerImage(Bitmap? bitmap)
    {
        var old = ViewerImage;
        ViewerImage = bitmap;
        IsViewerLoading = false;
        // After the frame that may still draw it
        if (old is not null) Avalonia.Threading.Dispatcher.UIThread.Post(old.Dispose, Avalonia.Threading.DispatcherPriority.Background);
    }
}

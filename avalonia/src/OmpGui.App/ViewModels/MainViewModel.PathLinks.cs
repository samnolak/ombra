using OmpGui.App.Controls;
using OmpGui.App.Services;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// Paths in the conversation (Controls/PathLinks): looked up in the open chat's project, opened where they belong — an
/// image in the image viewer, a file in the Files pane at its line, a folder in the Files tree.
/// </summary>
public sealed partial class MainViewModel : IPathLinkHandler
{
    /// <summary>The largest image a path opens in the viewer; a bigger one opens in the Files pane, which says why.</summary>
    private const long MaxViewerImageBytes = 40 * 1024 * 1024;

    public string? ProjectRoot => ProjectFolder;

    public PathTarget? ResolvePath(string text) => PathLinks.Resolve(ProjectFolder, text);

    public void OpenPath(PathTarget target)
    {
        if (target.Kind == PathKind.Image && OpenImageFile(target.FullPath)) return;
        Files.OpenLinkCommand.Execute(new FileLink(target.FullPath, target.Line));
    }

    /// <summary>A picture on disk in the image viewer; false when it cannot be read (too large, gone, not allowed).</summary>
    private bool OpenImageFile(string path)
    {
        byte[] data;
        try
        {
            if (new FileInfo(path).Length > MaxViewerImageBytes) return false;
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        var image = new ImageAttachment(Path.GetFileName(path), ImageAttachments.MimeOf(data) ?? "application/octet-stream", data);
        _viewerImages = [image];
        IsViewerActualSize = false;
        ShowViewerImage(0);
        IsImageViewerOpen = true;
        OnPropertyChanged(nameof(HasViewerSiblings));
        return true;
    }
}

using Avalonia.Controls;

namespace OmpGui.App.Controls;

/// <summary>An attached image as a square thumbnail (DataContext = <see cref="ViewModels.ImagePreview"/>).</summary>
public sealed partial class ImageThumb : UserControl
{
    public ImageThumb() => InitializeComponent();
}

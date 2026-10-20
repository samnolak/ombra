using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Ombra.App.Views.Session;

/// <summary>The chat's connectors chip and its popover.</summary>
public partial class ConnectorsIndicator : UserControl
{
    public ConnectorsIndicator()
    {
        InitializeComponent();
        // At most the room above the chip, less a margin (as the context ring's popover)
        ConnectorsPopup.Opened += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top || ConnectorsButton.TranslatePoint(default, top) is not { } at) return;
            ConnectorsScroll.MaxHeight = Math.Max(160, at.Y - 64);
        };
    }

    /// <summary>The chip (tests).</summary>
    public Button Button => ConnectorsButton;

    /// <summary>The popover (tests and the layout audit).</summary>
    public Popup Popup => ConnectorsPopup;
}

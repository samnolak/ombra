using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace OmpGui.App.Views.Session;

/// <summary>The context ring and its popover.</summary>
public partial class UsageIndicator : UserControl
{
    public UsageIndicator()
    {
        InitializeComponent();
        // At most the room above the ring, less a margin: a 600 px window had it from the ring to the window's top edge
        UsagePopup.Opened += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top || RingButton.TranslatePoint(default, top) is not { } at) return;
            UsageScroll.MaxHeight = Math.Max(160, at.Y - 64);
        };
    }

    /// <summary>The ring's button (tests).</summary>
    public Button Button => RingButton;

    /// <summary>The popover (tests and the layout audit).</summary>
    public Popup Popup => UsagePopup;
}

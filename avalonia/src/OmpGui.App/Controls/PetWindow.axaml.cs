using Avalonia;
using Avalonia.Controls;
using OmpGui.App.Pets;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Controls;

/// <summary>
/// The pet on the desktop (DataContext = <see cref="PetsViewModel"/>): a window of its own, borderless, see-through and
/// over other apps, as big as the pet (and its bubble or message box while they show) so clicks around it reach the
/// apps below. <see cref="PetPerch"/> owns it: it says where the pet is (<see cref="MoveTo"/>) and moves it when it is
/// dragged (<see cref="Stage"/>'s events). The pet stays put when the bubble or message box opens: the window grows
/// around it. On macOS it shows on every Space and over full-screen apps, and Mission Control leaves it alone
/// (<see cref="Platform.MacFloatingWindow"/>).
/// </summary>
public sealed partial class PetWindow : Window
{
    /// <summary>Room for the bubble's and the message box's shadows: the window clips anything outside it.</summary>
    private static readonly Thickness ShadowRoom = new(16, 12, 16, 28);
    /// <summary>No monitor known: nothing to keep the bubble and the message box inside.</summary>
    private static readonly Rect Unbounded = new(-1e6, -1e6, 2e6, 2e6);

    private IReadOnlyList<PetScreen> _screens = [];
    /// <summary>Closing with the app's window, not by the user.</summary>
    private bool _closing;

    public PetWindow()
    {
        InitializeComponent();
        // Popups are drawn inside their window (OverlayPopups, Program.cs) and this one is the pet's size: a tooltip
        // would cover the pet, and the message box's right-click menu would be cut off (its shortcuts still work)
        Stage.ShowsTip = false;
        Stage.PetChatBox.ContextFlyout = null;
        // The bubble or the message box opened, closed or changed size: the window follows, the pet stays put
        LayoutUpdated += (_, _) => Relayout();
    }

    /// <summary>The pet's top-left corner on the desktop, in desktop pixels.</summary>
    public PixelPoint At { get; private set; }

    /// <summary>The pet goes to <paramref name="at"/> (already an allowed place) on <paramref name="screens"/>.</summary>
    internal void MoveTo(PixelPoint at, IReadOnlyList<PetScreen> screens)
    {
        At = at;
        _screens = screens;
        Relayout();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsMacOS()) Platform.MacFloatingWindow.Configure(TryGetPlatformHandle());
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Only the app's window closes the pet for good (CloseWithApp, once it has closed). ⌘Q asks every window to
        // close, this one too, before the app's window has asked "Quit?": answered with Keep working, the pet must still
        // be out. Closed by the user (Alt+F4 while it has the keyboard), it goes home to the message box instead of
        // vanishing until the next start.
        if (!_closing)
        {
            e.Cancel = true;
            if (e.CloseReason == WindowCloseReason.WindowClosing && DataContext is PetsViewModel pets) pets.ReturnToPerchCommand.Execute(null);
        }
        base.OnClosing(e);
    }

    /// <summary>The app's window is closing: so does the pet's.</summary>
    internal void CloseWithApp()
    {
        _closing = true;
        Close();
    }

    /// <summary>
    /// Sizes and places the window around the pet, its bubble and its message box, these kept on the work area of the
    /// monitor the pet is on; the window's corner on whole desktop pixels, the pet exactly at <see cref="At"/>.
    /// </summary>
    private void Relayout()
    {
        if (DataContext is not PetsViewModel pets) return;
        var size = PetStage.PetSize(pets);
        var home = PetDesktop.Home(_screens, At, size);
        var scale = home?.Scaling is > 0 and var s ? s : DesktopScaling;
        var room = home is { WorkingArea: var a }
            ? new Rect((a.X - At.X) / scale, (a.Y - At.Y) / scale, a.Width / scale, a.Height / scale)
            : Unbounded;
        var layout = Stage.Plan(default, room);
        var extent = Stage.PetBubble.IsVisible || Stage.PetChat.IsVisible ? layout.Extent.Inflate(ShadowRoom) : layout.Extent;
        var origin = new PixelPoint(At.X + (int)Math.Floor(extent.X * scale), At.Y + (int)Math.Floor(extent.Y * scale));
        var shift = new Vector((At.X - origin.X) / scale, (At.Y - origin.Y) / scale);
        Stage.Apply(layout, shift);
        var width = Math.Ceiling(extent.Right + shift.X);
        var height = Math.Ceiling(extent.Bottom + shift.Y);
        if (Position != origin) Position = origin;
        if (Width != width) Width = width;
        if (Height != height) Height = height;
    }
}

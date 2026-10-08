using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using OmpGui.App.Pets;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Controls;

/// <summary>
/// The pet's home in the app's window and the owner of the pet on the desktop (DataContext = <see cref="MainViewModel"/>).
/// In the window it is a layer as big as the window where only the pet, its speech bubble and its message box
/// (<see cref="PetStage"/>) take the pointer. The pet sits on <see cref="Anchor"/> (the message box) until it is
/// dragged. With the desktop roam (the default) the drag lifts it at once into a window of its own
/// (<see cref="PetWindow"/>) that follows the pointer anywhere on the user's monitors; dropped, it stays there (saved
/// relative to its monitor) above other apps, also while this window is minimised or another app is in front. With
/// "Inside the window" it stays in this layer (as a fraction of the window, saved) at any window size. Dropped back
/// close to its perch, it sits there again. It tells the view model its window's height, so a short window gives the
/// pet's band back to the conversation.
/// </summary>
public sealed partial class PetPerch : UserControl
{
    public static readonly StyledProperty<Control?> AnchorProperty = AvaloniaProperty.Register<PetPerch, Control?>(nameof(Anchor));

    /// <summary>Dropped this close to its perch, the pet goes back on it.</summary>
    private const double SnapDistance = 16;

    private TopLevel? _top;
    private PetsViewModel? _pets;
    /// <summary>Where the pet is in the window (its top-left, in this control's coordinates).</summary>
    private Point _at;
    /// <summary>Dragged inside the window: where the pet was at the press, and where it is now.</summary>
    private Point _pressPet;
    private Point? _dragAt;
    /// <summary>The pet on the desktop; made the first time it goes there.</summary>
    private PetWindow? _window;
    /// <summary>Dragged on the desktop: where the pet was (desktop pixels) when the drag began.</summary>
    private PixelPoint? _desktopDrag;
    private IPetScreens? _screens;
    private bool _migrating;

    public PetPerch()
    {
        InitializeComponent();
        Stage.DragStarted += OnPerchDragStarted;
        Stage.Dragged += OnPerchDragged;
        Stage.Dropped += (_, _) => OnPerchDropped();
        // The message box moves (it grows, the sidebar opens): the perch moves with it
        LayoutUpdated += (_, _) => Place(Bounds.Size);
    }

    /// <summary>The message box: the pet's perch is its top edge, at its right end.</summary>
    public Control? Anchor { get => GetValue(AnchorProperty); set => SetValue(AnchorProperty, value); }

    /// <summary>The monitors the pet may roam: the window's own screens, or a test's.</summary>
    internal IPetScreens? Screens
    {
        get => _screens;
        set
        {
            if (_screens is not null) _screens.Changed -= OnScreensChanged;
            _screens = value;
            if (_screens is not null) _screens.Changed += OnScreensChanged;
            SyncDesktop();
        }
    }

    /// <summary>The pet on the desktop (null until it first goes there).</summary>
    internal PetWindow? DesktopWindow => _window;

    private IReadOnlyList<PetScreen> AllScreens => _screens?.All ?? [];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null) _top.PropertyChanged += OnTopChanged;
        if (_top is Window w) w.Closed += OnTopClosed;
        if (_screens is null && _top?.Screens is { } screens) Screens = new AvaloniaPetScreens(screens);
        ReportHeight();
        SyncDesktop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_top is not null) _top.PropertyChanged -= OnTopChanged;
        if (_top is Window w) w.Closed -= OnTopClosed;
        _top = null;
        CloseDesktopWindow();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_pets is not null) _pets.PropertyChanged -= OnPetsChanged;
        _pets = (DataContext as MainViewModel)?.Pets;
        if (_pets is not null) _pets.PropertyChanged += OnPetsChanged;
        if (_window is not null) _window.DataContext = _pets;
        ReportHeight();
        InvalidateArrange();
        SyncDesktop();
    }

    private void OnPetsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PetsViewModel.Position):
                InvalidateArrange();
                break;
            case nameof(PetsViewModel.PixelScale):
                InvalidateArrange();
                SyncDesktop();
                break;
            case nameof(PetsViewModel.IsOnDesktop) or nameof(PetsViewModel.DesktopPlace):
                SyncDesktop();
                break;
        }
    }

    private void OnTopChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty) ReportHeight();
        else if (e.Property == WindowBase.IsActiveProperty) SyncForeground();
    }

    private void OnTopClosed(object? sender, EventArgs e) => CloseDesktopWindow();

    private void ReportHeight()
    {
        if (_top is { } top && _pets is { } pets && top.ClientSize.Height > 0) pets.WindowHeight = top.ClientSize.Height;
    }

    // ───────────────────────────── In the window ─────────────────────────────

    // The message box is arranged before this layer (an earlier child of the window's grid): the perch is current here
    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize);
        return base.ArrangeOverride(finalSize);
    }

    /// <summary>Where the pet's top-left can be: the window less the pet.</summary>
    private static Size Room(Size window, Size pet) => new(Math.Max(0, window.Width - pet.Width), Math.Max(0, window.Height - pet.Height));

    private static Point Clamp(Point p, Size room) => new(Math.Clamp(p.X, 0, room.Width), Math.Clamp(p.Y, 0, room.Height));

    /// <summary>On the message box: its feet on the box's top edge, its right end <see cref="PetsViewModel.PetInset"/> from the box's.</summary>
    private Point? PerchAt(Size pet) =>
        Anchor is { IsEffectivelyVisible: true } a && a.Bounds.Width > 0
            ? a.TranslatePoint(PerchOn(a, pet), this)
            : null;

    private static Point PerchOn(Control anchor, Size pet) => new(anchor.Bounds.Width - PetsViewModel.PetInset - pet.Width, 1 - pet.Height);

    private void Place(Size size)
    {
        if (_pets is not { } pets || !IsVisible || size.Width <= 0 || size.Height <= 0) return;
        var pet = PetStage.PetSize(pets);
        var room = Room(size, pet);
        var at = _dragAt
                 ?? (pets.Position is { } f ? new Point(f.X * room.Width, f.Y * room.Height) : (Point?)null)
                 ?? PerchAt(pet)
                 ?? new Point(room.Width - PetsViewModel.PetInset, room.Height);
        _at = Clamp(at, room);
        Stage.Apply(Stage.Plan(_at, new Rect(size)), default);
        // A settings file from before the desktop roam: the pet goes to the same spot on the desktop once it is placed
        if (pets is { IsRoamDesktop: true, Position: not null } && _dragAt is null && !_migrating)
        {
            _migrating = true;
            Dispatcher.UIThread.Post(MoveToDesktop, DispatcherPriority.Background);
        }
    }

    private void MoveToDesktop()
    {
        _migrating = false;
        if (_pets is not { IsRoamDesktop: true, Position: not null } pets || _top is null || !IsEffectivelyVisible) return;
        var size = PetStage.PetSize(pets);
        var at = PetDesktop.Clamp(AllScreens, this.PointToScreen(_at), size);
        pets.MoveOnDesktop(PetDesktop.Place(AllScreens, at, size));
    }

    private void OnPerchDragStarted(PetPointer press)
    {
        if (_pets is not { } pets) return;
        if (pets.IsRoamWindow)
        {
            _pressPet = _at;
            return;
        }
        // Off the message box onto the desktop: the pet moves into its own window at once, where it is now. This
        // stage keeps the pointer until the drop, unseen; its message box closes (the keyboard stays in this window).
        pets.CloseChat();
        var size = PetStage.PetSize(pets);
        var at = PetDesktop.Clamp(AllScreens, this.PointToScreen(_at), size);
        _desktopDrag = at;
        Stage.Opacity = 0;
        var window = EnsureDesktopWindow(pets);
        window.MoveTo(at, AllScreens);
        if (!window.IsVisible) window.Show();
        SyncForeground();
    }

    private void OnPerchDragged(PetPointer press, PetPointer now)
    {
        if (_desktopDrag is not null)
        {
            DragOnDesktop(press, now);
            return;
        }
        _dragAt = _pressPet + (now.Local - press.Local);
        Place(Bounds.Size);
    }

    private void OnPerchDropped()
    {
        if (_desktopDrag is not null)
        {
            DropOnDesktop();
            return;
        }
        var drop = _dragAt;
        _dragAt = null;
        if (drop is not { } at || _pets is not { } pets) return;
        var size = Bounds.Size;
        var pet = PetStage.PetSize(pets);
        var room = Room(size, pet);
        at = Clamp(at, room);
        // Back on the message box when dropped close to it (and the window has room for its band)
        if (pets.HasRoom && PerchAt(pet) is { } perch && Math.Abs(at.X - perch.X) <= SnapDistance && Math.Abs(at.Y - perch.Y) <= SnapDistance)
            pets.ReturnToPerchCommand.Execute(null);
        else
            pets.MoveTo(new Point(room.Width > 0 ? at.X / room.Width : 0, room.Height > 0 ? at.Y / room.Height : 0));
        Place(size);
        Stage.Pet.Cheer();
    }

    // ───────────────────────────── On the desktop ─────────────────────────────

    private PetWindow EnsureDesktopWindow(PetsViewModel pets)
    {
        if (_window is not null) return _window;
        var window = new PetWindow { DataContext = pets };
        window.Stage.DragStarted += _ => _desktopDrag = window.At;
        window.Stage.Dragged += DragOnDesktop;
        window.Stage.Dropped += (_, _) => DropOnDesktop();
        window.Activated += (_, _) => SyncForeground();
        window.Deactivated += (_, _) => SyncForeground();
        // Closed with the app's window (CloseWithApp): a new one is made if the pet goes out again
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window)) _window = null;
        };
        _window = window;
        return window;
    }

    /// <summary>Shows the pet on the desktop where the view model says, or puts that window away.</summary>
    private void SyncDesktop()
    {
        if (_desktopDrag is not null || _top is null) return; // the drag moves it; the drop says where it stays
        if (_pets is not { IsOnDesktop: true, DesktopPlace: { } place } pets)
        {
            _window?.Hide();
            return;
        }
        var window = EnsureDesktopWindow(pets);
        window.MoveTo(PetDesktop.Resolve(AllScreens, place, PetStage.PetSize(pets)), AllScreens);
        if (!window.IsVisible) window.Show();
        SyncForeground();
    }

    /// <summary>The pointer moved: the pet follows it, kept on the monitors' work areas.</summary>
    private void DragOnDesktop(PetPointer press, PetPointer now)
    {
        if (_desktopDrag is not { } from || _window is null || _pets is not { } pets) return;
        var to = new PixelPoint(from.X + now.Screen.X - press.Screen.X, from.Y + now.Screen.Y - press.Screen.Y);
        _window.MoveTo(PetDesktop.Clamp(AllScreens, to, PetStage.PetSize(pets)), AllScreens);
    }

    /// <summary>Dropped on the desktop: it stays there (saved), or sits on the message box again when dropped close to it.</summary>
    private void DropOnDesktop()
    {
        if (_desktopDrag is null || _window is null || _pets is not { } pets) return;
        _desktopDrag = null;
        Stage.Opacity = 1;
        var at = _window.At;
        if (NearPerch(pets, at))
        {
            pets.ReturnToPerchCommand.Execute(null);
            SyncDesktop();
            Stage.Pet.Cheer();
            return;
        }
        pets.MoveOnDesktop(PetDesktop.Place(AllScreens, at, PetStage.PetSize(pets)));
        SyncDesktop();
        _window.Stage.Pet.Cheer();
    }

    /// <summary>The pet's top-left at <paramref name="at"/> on the desktop is close to its perch on the message box (seen, with room for the band).</summary>
    private bool NearPerch(PetsViewModel pets, PixelPoint at)
    {
        if (!pets.HasRoom || _top is not Window { IsVisible: true, WindowState: not WindowState.Minimized } top
            || Anchor is not { IsEffectivelyVisible: true } anchor || anchor.Bounds.Width <= 0)
            return false;
        var perch = anchor.PointToScreen(PerchOn(anchor, PetStage.PetSize(pets)));
        var snap = SnapDistance * top.DesktopScaling;
        return Math.Abs(at.X - perch.X) <= snap && Math.Abs(at.Y - perch.Y) <= snap;
    }

    /// <summary>The desktop pet moves while the app is in front (this window or the pet's own has the keyboard).</summary>
    private void SyncForeground()
    {
        if (_window is null) return;
        _window.Stage.Pet.InForeground = _top is WindowBase { IsActive: true } || _window.IsActive;
    }

    private void OnScreensChanged(object? sender, EventArgs e) => SyncDesktop();

    private void CloseDesktopWindow()
    {
        _window?.CloseWithApp();
        _window = null;
    }
}

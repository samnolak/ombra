using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Ombra.App.Pets;
using Ombra.App.Pets.Oni;
using Ombra.App.ViewModels;

namespace Ombra.App.Controls;

/// <summary>
/// The pet's home in the app's window and the owner of the pet on the desktop (DataContext = <see cref="MainViewModel"/>).
/// In the window it is a layer as big as the window where only the pet, its speech bubble and its message box
/// (<see cref="PetStage"/>) take the pointer. The pet sits on <see cref="Anchor"/> (the message box) until it is
/// dragged. A drag carries it (<see cref="OniCarry"/>): it hangs from the pointer, swings with its moves, and when let
/// go comes down onto the spot under it with a little dust. With the desktop roam (the default) the drag lifts it at
/// once into a window of its own (<see cref="PetWindow"/>) carried anywhere on the user's monitors; put down, it stays
/// there (saved relative to its monitor) above other apps, also while this window is minimised or another app is in
/// front. With "Inside the window" it stays in this layer (as a fraction of the window, saved) at any window size.
/// Put down close to its perch, it sits there again. It tells the view model its window's height, so a short window
/// gives the pet's band back to the conversation.
/// </summary>
public sealed partial class PetPerch : UserControl
{
    public static readonly StyledProperty<Control?> AnchorProperty = AvaloniaProperty.Register<PetPerch, Control?>(nameof(Anchor));

    /// <summary>Put down this close to its perch, the pet goes back on it.</summary>
    private const double SnapDistance = 16;

    private TopLevel? _top;
    private PetsViewModel? _pets;
    /// <summary>Where the pet is in the window (its top-left, in this control's coordinates).</summary>
    private Point _at;
    /// <summary>Carried inside the window: where its box is now.</summary>
    private Point? _dragAt;
    /// <summary>The pet on the desktop; made the first time it goes there.</summary>
    private PetWindow? _window;
    private IPetScreens? _screens;
    private bool _migrating;
    /// <summary>The pet being carried (until it comes to rest), and whether on the desktop (in desktop pixels / <see cref="_scale"/>).</summary>
    private OniCarry? _carry;
    private bool _onDesktop;
    private double _scale = 1;

    public PetPerch()
    {
        InitializeComponent();
        Stage.DragStarted += OnPerchDragStarted;
        Stage.Dragged += OnDragged;
        Stage.Dropped += (_, _) => _carry?.Release();
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

    /// <summary>The pet is being carried, or has not come to rest since (tests).</summary>
    internal OniCarry? Carry => _carry;

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
            case nameof(PetsViewModel.PetBox):
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

    /// <summary>Drawn around the pet's box (its spirits, the sign over its head, the dust): kept inside the window and on the screens too.</summary>
    private static Thickness Overflow(PetsViewModel pets) => OniView.OverflowFor(pets.PetHeight);

    /// <summary>How far the pet's top-left can move: the window less the pet and what is drawn around it.</summary>
    private static Size Room(Size window, Size pet, Thickness ov) =>
        new(Math.Max(0, window.Width - pet.Width - ov.Left - ov.Right), Math.Max(0, window.Height - pet.Height - ov.Top - ov.Bottom));

    private static Point Clamp(Point p, Size room, Thickness ov) =>
        new(Math.Clamp(p.X, ov.Left, ov.Left + room.Width), Math.Clamp(p.Y, ov.Top, ov.Top + room.Height));

    /// <summary>The nearest place on the monitors' work areas for the pet at <paramref name="at"/> with what is drawn around it.</summary>
    private PixelPoint ClampOnDesktop(PixelPoint at, PetsViewModel pets)
    {
        var box = pets.PetBox;
        var ov = Overflow(pets);
        var scale = PetDesktop.Home(AllScreens, at, box)?.Scaling ?? (_top as WindowBase)?.DesktopScaling ?? 1;
        var dx = (int)Math.Round(ov.Left * scale);
        var dy = (int)Math.Round(ov.Top * scale);
        var outer = new Size(box.Width + ov.Left + ov.Right, box.Height + ov.Top + ov.Bottom);
        var p = PetDesktop.Clamp(AllScreens, new PixelPoint(at.X - dx, at.Y - dy), outer);
        return new PixelPoint(p.X + dx, p.Y + dy);
    }

    /// <summary>On the message box: its feet on the box's top edge, its right end <see cref="PetsViewModel.PetInset"/> from the box's.</summary>
    private Point? PerchAt(Size pet) =>
        Anchor is { IsEffectivelyVisible: true } a && a.Bounds.Width > 0
            ? a.TranslatePoint(PerchOn(a, pet), this)
            : null;

    private static Point PerchOn(Control anchor, Size pet) => new(anchor.Bounds.Width - PetsViewModel.PetInset - pet.Width, 1 - pet.Height);

    private void Place(Size size)
    {
        if (_pets is not { } pets || !IsVisible || size.Width <= 0 || size.Height <= 0) return;
        var pet = pets.PetBox;
        var ov = Overflow(pets);
        var room = Room(size, pet, ov);
        var at = _dragAt
                 ?? (pets.Position is { } f ? new Point(ov.Left + f.X * room.Width, ov.Top + f.Y * room.Height) : (Point?)null)
                 ?? PerchAt(pet)
                 ?? new Point(room.Width - PetsViewModel.PetInset, room.Height);
        _at = Clamp(at, room, ov);
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
        var size = pets.PetBox;
        var at = ClampOnDesktop(this.PointToScreen(_at), pets);
        pets.MoveOnDesktop(PetDesktop.Place(AllScreens, at, size));
    }

    private void OnPerchDragStarted(PetPointer press)
    {
        if (_pets is not { } pets) return;
        if (pets.IsRoamWindow)
        {
            // Taken again while it is still coming down: the same body, caught where it is
            if (_carry is { Moving: true } && !_onDesktop)
            {
                _carry.Grab(press.Local);
                return;
            }
            var box = pets.PetBox;
            var ov = Overflow(pets);
            var room = Room(Bounds.Size, box, ov);
            var bounds = new Rect(ov.Left + box.Width / 2, ov.Top + box.Height, room.Width, room.Height);
            StartCarry(pets, Stage, new Point(_at.X + box.Width / 2, _at.Y + box.Height), bounds, desktop: false);
            _carry!.Grab(press.Local);
            return;
        }
        // Off the message box onto the desktop: the pet moves into its own window at once, where it is now. This
        // stage keeps the pointer until it is let go, unseen; its message box closes (the keyboard stays in this window).
        pets.CloseChat();
        var at = ClampOnDesktop(this.PointToScreen(_at), pets);
        var window = EnsureDesktopWindow(pets);
        window.MoveTo(at, AllScreens);
        if (!window.IsVisible) window.Show();
        Stage.PetShown = false;
        SyncForeground();
        StartDesktopCarry(pets, window, at, press.Screen, (_top as WindowBase)?.DesktopScaling ?? 1);
    }

    private void OnDragged(PetPointer press, PetPointer now)
    {
        if (_carry is not { } carry) return;
        carry.Move(_onDesktop ? new Point(now.Screen.X / _scale, now.Screen.Y / _scale) : now.Local);
    }

    /// <summary>A carry of the pet shown by <paramref name="stage"/>, its body at <paramref name="body"/>, kept inside <paramref name="bounds"/>.</summary>
    private void StartCarry(PetsViewModel pets, PetStage stage, Point body, Rect bounds, bool desktop)
    {
        EndCarry();
        _onDesktop = desktop;
        var carry = new OniCarry(pets.Oni, body, pets.PetHeight, bounds, pets.ReduceMotion);
        carry.Moved += () => OnCarryMoved(carry);
        carry.Settled += () => OnCarrySettled(carry);
        _carry = carry;
        stage.Pet.Carry = carry;
    }

    /// <summary>The carry before this one is over (a new drag began): it no longer moves anything.</summary>
    private void EndCarry()
    {
        if (_carry is null) return;
        _carry = null;
        Stage.Pet.Carry = null;
        if (_window is not null) _window.Stage.Pet.Carry = null;
    }

    private void OnCarryMoved(OniCarry carry)
    {
        if (!ReferenceEquals(carry, _carry) || _pets is not { } pets) return;
        var box = pets.PetBox;
        var b = carry.Body;
        if (_onDesktop)
        {
            if (_window is null) return;
            var at = new PixelPoint((int)Math.Round((b.X - box.Width / 2) * _scale), (int)Math.Round((b.Y - box.Height) * _scale));
            _window.MoveTo(ClampOnDesktop(at, pets), AllScreens);
            return;
        }
        _dragAt = new Point(b.X - box.Width / 2, b.Y - box.Height);
        Place(Bounds.Size);
    }

    /// <summary>Put down and at rest: it stays where it is (saved), or sits on its perch again when put down close to it.</summary>
    private void OnCarrySettled(OniCarry carry)
    {
        if (!ReferenceEquals(carry, _carry)) return;
        var desktop = _onDesktop;
        EndCarry();
        _onDesktop = false;
        if (desktop)
        {
            DropOnDesktop();
            return;
        }
        var drop = _dragAt;
        _dragAt = null;
        if (drop is not { } at || _pets is not { } pets) return;
        var size = Bounds.Size;
        var pet = pets.PetBox;
        var ov = Overflow(pets);
        var room = Room(size, pet, ov);
        at = Clamp(at, room, ov);
        // Back on the message box when put down close to it (and the window has room for its band)
        if (pets.HasRoom && PerchAt(pet) is { } perch && Math.Abs(at.X - perch.X) <= SnapDistance && Math.Abs(at.Y - perch.Y) <= SnapDistance)
            pets.ReturnToPerchCommand.Execute(null);
        else
            pets.MoveTo(new Point(room.Width > 0 ? (at.X - ov.Left) / room.Width : 0, room.Height > 0 ? (at.Y - ov.Top) / room.Height : 0));
        Place(size);
    }

    // ───────────────────────────── On the desktop ─────────────────────────────

    private PetWindow EnsureDesktopWindow(PetsViewModel pets)
    {
        if (_window is not null) return _window;
        var window = new PetWindow { DataContext = pets };
        window.Stage.DragStarted += press => OnDesktopDragStarted(window, press);
        window.Stage.Dragged += OnDragged;
        window.Stage.Dropped += (_, _) => _carry?.Release();
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

    private void OnDesktopDragStarted(PetWindow window, PetPointer press)
    {
        if (_pets is not { } pets) return;
        if (_carry is { Moving: true } carry && _onDesktop)
        {
            carry.Grab(new Point(press.Screen.X / _scale, press.Screen.Y / _scale));
            return;
        }
        StartDesktopCarry(pets, window, window.At, press.Screen, window.DesktopScaling);
    }

    /// <summary>
    /// Carries the desktop pet, its box's top-left at <paramref name="at"/>: in desktop pixels over
    /// <paramref name="scale"/> (the layout units of the screen it was taken on), kept over the monitors' work areas.
    /// </summary>
    private void StartDesktopCarry(PetsViewModel pets, PetWindow window, PixelPoint at, PixelPoint pointer, double scale)
    {
        _scale = scale > 0 ? scale : 1;
        var box = pets.PetBox;
        var area = AllScreens.Count > 0
            ? AllScreens.Select(s => s.WorkingArea).Aggregate((a, b) => a.Union(b))
            : new PixelRect(-100000, -100000, 200000, 200000);
        var bounds = new Rect(area.X / _scale + box.Width / 2, area.Y / _scale + box.Height,
            Math.Max(0, area.Width / _scale - box.Width), Math.Max(0, area.Height / _scale - box.Height));
        StartCarry(pets, window.Stage, new Point(at.X / _scale + box.Width / 2, at.Y / _scale + box.Height), bounds, desktop: true);
        _carry!.Grab(new Point(pointer.X / _scale, pointer.Y / _scale));
    }

    /// <summary>Shows the pet on the desktop where the view model says, or puts that window away.</summary>
    private void SyncDesktop()
    {
        if (_onDesktop || _top is null) return; // the carry moves it; where it comes to rest is where it stays
        if (_pets is not { IsOnDesktop: true, DesktopPlace: { } place } pets)
        {
            _window?.Hide();
            return;
        }
        var window = EnsureDesktopWindow(pets);
        window.MoveTo(ClampOnDesktop(PetDesktop.Resolve(AllScreens, place, pets.PetBox), pets), AllScreens);
        if (!window.IsVisible) window.Show();
        SyncForeground();
    }

    /// <summary>At rest on the desktop: it stays there (saved), or sits on the message box again when put down close to it.</summary>
    private void DropOnDesktop()
    {
        Stage.PetShown = true;
        if (_window is null || _pets is not { } pets) return;
        var at = _window.At;
        if (NearPerch(pets, at))
        {
            pets.ReturnToPerchCommand.Execute(null);
            SyncDesktop();
            return;
        }
        pets.MoveOnDesktop(PetDesktop.Place(AllScreens, at, pets.PetBox));
        SyncDesktop();
    }

    /// <summary>The pet's top-left at <paramref name="at"/> on the desktop is close to its perch on the message box (seen, with room for the band).</summary>
    private bool NearPerch(PetsViewModel pets, PixelPoint at)
    {
        if (!pets.HasRoom || _top is not Window { IsVisible: true, WindowState: not WindowState.Minimized } top
            || Anchor is not { IsEffectivelyVisible: true } anchor || anchor.Bounds.Width <= 0)
            return false;
        var perch = anchor.PointToScreen(PerchOn(anchor, pets.PetBox));
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
        EndCarry();
        _onDesktop = false;
        _window?.CloseWithApp();
        _window = null;
    }
}

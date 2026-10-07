using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OmpGui.App.Pets;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Controls;

/// <summary>
/// The pet over the window (DataContext = <see cref="MainViewModel"/>): a layer as big as the window where only the
/// pet, its speech bubble and its message box take the pointer. The pet sits on <see cref="Anchor"/> (the message box)
/// until it is dragged; dropped, it stays where it is (as a fraction of the window, saved) and inside the window at any
/// size; dropped back close to its perch, it sits there again. A click (a press that does not move) opens its message
/// box, focused, beside it: Enter sends to the conversation, Esc closes it, and the keyboard goes back where it was.
/// The pet itself never takes focus. It tells the view model its window's height, so a short window gives the pet's
/// band back to the conversation.
/// </summary>
public sealed partial class PetPerch : UserControl
{
    public static readonly StyledProperty<Control?> AnchorProperty = AvaloniaProperty.Register<PetPerch, Control?>(nameof(Anchor));

    /// <summary>A press that moves less than this (in either direction) is a click, not a drag.</summary>
    private const double DragThreshold = 4;
    /// <summary>Dropped this close to its perch, the pet goes back on it.</summary>
    private const double SnapDistance = 16;
    /// <summary>Between the pet and its message box; between the message box and the window's edges.</summary>
    private const double ChatGap = 6, ChatEdge = 8;
    /// <summary>A press on the pet right after its message box lost the keyboard (to that press) closes it, not reopens it.</summary>
    private static readonly TimeSpan ToggleWindow = TimeSpan.FromMilliseconds(250);

    private TopLevel? _top;
    private PetsViewModel? _pets;
    /// <summary>Where the pet is (its top-left, in this control's coordinates).</summary>
    private Point _at;
    /// <summary>Where a left press on the pet started, and where the pet was then.</summary>
    private Point? _press;
    private Point _pressPet;
    /// <summary>Where the pet is while dragged (past <see cref="DragThreshold"/>).</summary>
    private Point? _dragAt;
    /// <summary>The press was on the pet with its message box open: the click closes it.</summary>
    private bool _toggleOff;
    private DateTime _chatLostFocusAt;
    /// <summary>What had the keyboard when the message box opened: it gets it back when the box closes.</summary>
    private IInputElement? _returnFocus;

    public PetPerch()
    {
        InitializeComponent();
        Pet.PointerPressed += OnPetPressed;
        Pet.PointerMoved += OnPetMoved;
        Pet.PointerReleased += OnPetReleased;
        Pet.PointerCaptureLost += (_, _) => EndPress();
        PetChatBox.AddHandler(KeyDownEvent, OnChatKeyDown, RoutingStrategies.Tunnel);
        // Clicking elsewhere closes the message box; what was typed stays for the next time
        PetChatBox.LostFocus += (_, _) =>
        {
            if (_pets is not { IsChatOpen: true } pets) return;
            _chatLostFocusAt = DateTime.UtcNow;
            pets.CloseChat();
        };
        // The message box moves (it grows, the sidebar opens): the perch moves with it
        LayoutUpdated += (_, _) => Place(Bounds.Size);
    }

    /// <summary>The message box: the pet's perch is its top edge, at its right end.</summary>
    public Control? Anchor { get => GetValue(AnchorProperty); set => SetValue(AnchorProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null) _top.PropertyChanged += OnTopChanged;
        ReportHeight();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_top is not null) _top.PropertyChanged -= OnTopChanged;
        _top = null;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_pets is not null) _pets.PropertyChanged -= OnPetsChanged;
        _pets = (DataContext as MainViewModel)?.Pets;
        if (_pets is not null) _pets.PropertyChanged += OnPetsChanged;
        ReportHeight();
        InvalidateArrange();
    }

    private void OnPetsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PetsViewModel.Position) or nameof(PetsViewModel.PixelScale):
                InvalidateArrange();
                break;
            case nameof(PetsViewModel.IsChatOpen) when _pets?.IsChatOpen == true:
                // Shown first, then focused (with the caret after what was typed before)
                Dispatcher.UIThread.Post(() =>
                {
                    if (_pets?.IsChatOpen != true) return;
                    PetChatBox.Focus();
                    PetChatBox.CaretIndex = PetChatBox.Text?.Length ?? 0;
                }, DispatcherPriority.Loaded);
                break;
        }
    }

    private void OnTopChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty) ReportHeight();
    }

    private void ReportHeight()
    {
        if (_top is { } top && _pets is { } pets && top.ClientSize.Height > 0) pets.WindowHeight = top.ClientSize.Height;
    }

    // ───────────────────────────── Placing ─────────────────────────────

    // The message box is arranged before this layer (an earlier child of the window's grid): the perch is current here
    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize);
        return base.ArrangeOverride(finalSize);
    }

    private static Size PetSize(PetsViewModel pets) => new(PetFrames.Width * pets.PixelScale, PetFrames.Height * pets.PixelScale);

    /// <summary>Where the pet's top-left can be: the window less the pet.</summary>
    private static Size Room(Size window, Size pet) => new(Math.Max(0, window.Width - pet.Width), Math.Max(0, window.Height - pet.Height));

    private static Point Clamp(Point p, Size room) => new(Math.Clamp(p.X, 0, room.Width), Math.Clamp(p.Y, 0, room.Height));

    /// <summary>On the message box: its feet on the box's top edge, its right end <see cref="PetsViewModel.PetInset"/> from the box's.</summary>
    private Point? PerchAt(Size pet) =>
        Anchor is { IsEffectivelyVisible: true } a && a.Bounds.Width > 0
            ? a.TranslatePoint(new Point(a.Bounds.Width - PetsViewModel.PetInset - pet.Width, 1 - pet.Height), this)
            : null;

    private void Place(Size size)
    {
        if (_pets is not { } pets || !IsVisible || size.Width <= 0 || size.Height <= 0) return;
        var pet = PetSize(pets);
        var room = Room(size, pet);
        var at = _dragAt
                 ?? (pets.Position is { } f ? new Point(f.X * room.Width, f.Y * room.Height) : (Point?)null)
                 ?? PerchAt(pet)
                 ?? new Point(room.Width - PetsViewModel.PetInset, room.Height);
        _at = Clamp(at, room);
        Put(Pet, _at);
        PlaceBubble(pets, size, pet);
        PlaceChat(size, pet);
    }

    private static void Put(Control c, Point p)
    {
        Canvas.SetLeft(c, p.X);
        Canvas.SetTop(c, p.Y);
    }

    /// <summary>The bubble: beside the pet at the height of its head, on its left (right when the left has no room), its tail towards the pet.</summary>
    private void PlaceBubble(PetsViewModel pets, Size size, Size pet)
    {
        if (!PetBubble.IsVisible) return;
        var b = PetBubble.DesiredSize;
        var left = _at.X - 2 - b.Width;
        var right = _at.X + pet.Width + 2;
        var onLeft = left >= 0 || right + b.Width > size.Width && -left < right + b.Width - size.Width;
        PetBubbleTailRight.IsVisible = onLeft;
        PetBubbleTailLeft.IsVisible = !onLeft;
        var x = Math.Clamp(onLeft ? left : right, 0, Math.Max(0, size.Width - b.Width));
        var y = Math.Clamp(_at.Y + (pets.IsSmall ? 2 : 8), 0, Math.Max(0, size.Height - b.Height));
        Put(PetBubble, new Point(x, y));
    }

    /// <summary>The message box: above the pet (below near the window's top), its right end at the pet's.</summary>
    private void PlaceChat(Size size, Size pet)
    {
        if (!PetChat.IsVisible) return;
        var c = PetChat.DesiredSize;
        var x = Math.Clamp(_at.X + pet.Width - c.Width, ChatEdge, Math.Max(ChatEdge, size.Width - ChatEdge - c.Width));
        var above = _at.Y - ChatGap - c.Height;
        var y = above >= ChatEdge ? above : Math.Clamp(_at.Y + pet.Height + ChatGap, 0, Math.Max(0, size.Height - c.Height));
        Put(PetChat, new Point(x, y));
    }

    // ───────────────────────────── Drag and click ─────────────────────────────

    private void OnPetPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_pets is not { } pets || !e.GetCurrentPoint(Pet).Properties.IsLeftButtonPressed) return;
        _press = e.GetPosition(this);
        _pressPet = _at;
        _dragAt = null;
        _toggleOff = pets.IsChatOpen || DateTime.UtcNow - _chatLostFocusAt < ToggleWindow;
        if (!pets.IsChatOpen && !_toggleOff) _returnFocus = _top?.FocusManager?.GetFocusedElement();
        e.Pointer.Capture(Pet);
        e.Handled = true;
    }

    private void OnPetMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || !ReferenceEquals(e.Pointer.Captured, Pet)) return;
        var d = e.GetPosition(this) - press;
        if (_dragAt is null)
        {
            if (Math.Abs(d.X) < DragThreshold && Math.Abs(d.Y) < DragThreshold) return;
            Pet.Classes.Set("dragging", true);
            ToolTip.SetIsOpen(Pet, false);
            ToolTip.SetServiceEnabled(Pet, false);
        }
        _dragAt = _pressPet + d;
        Place(Bounds.Size);
    }

    private void OnPetReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is null || e.InitialPressMouseButton != MouseButton.Left) return;
        var clicked = _dragAt is null;
        EndPress();
        if (clicked) Click();
        e.Handled = true;
    }

    /// <summary>The press is over (released, or the pointer taken away): a drag drops the pet where it is.</summary>
    private void EndPress()
    {
        if (_press is null) return;
        _press = null;
        var drop = _dragAt;
        _dragAt = null;
        Pet.Classes.Set("dragging", false);
        ToolTip.SetServiceEnabled(Pet, true);
        if (drop is { } at && _pets is { } pets) Drop(pets, at);
    }

    private void Drop(PetsViewModel pets, Point at)
    {
        var size = Bounds.Size;
        var pet = PetSize(pets);
        var room = Room(size, pet);
        at = Clamp(at, room);
        // Back on the message box when dropped close to it (and the window has room for its band)
        if (pets.HasRoom && PerchAt(pet) is { } perch && Math.Abs(at.X - perch.X) <= SnapDistance && Math.Abs(at.Y - perch.Y) <= SnapDistance)
            pets.ReturnToPerchCommand.Execute(null);
        else
            pets.MoveTo(new Point(room.Width > 0 ? at.X / room.Width : 0, room.Height > 0 ? at.Y / room.Height : 0));
        Place(size);
        Pet.Cheer();
    }

    /// <summary>A click: the message box opens (the pet hops and says what omp is doing), or closes if it was open.</summary>
    private void Click()
    {
        if (_pets is not { } pets) return;
        if (_toggleOff)
        {
            pets.CloseChat();
            RestoreFocus();
            return;
        }
        pets.OpenChat();
        Pet.Cheer();
    }

    // ───────────────────────────── The message box ─────────────────────────────

    private void OnChatKeyDown(object? sender, KeyEventArgs e)
    {
        if (_pets is not { } pets) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            pets.CloseChat();
            RestoreFocus();
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            pets.SendChatCommand.Execute(null);
            if (pets.IsChatOpen) return; // not sent: the pet says why, the text stays
            RestoreFocus();
            Pet.Cheer();
        }
    }

    private void RestoreFocus()
    {
        var to = _returnFocus;
        _returnFocus = null;
        if (to is InputElement { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true } el && TopLevel.GetTopLevel(el) == _top
            && !ReferenceEquals(el, PetChatBox))
            el.Focus();
    }
}

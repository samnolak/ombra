using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OmpGui.App.Pets;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Controls;

/// <summary>Where the pointer is: in the stage's coordinates, and on the desktop (desktop pixels).</summary>
public readonly record struct PetPointer(Point Local, PixelPoint Screen);

/// <summary>Where the pet, its bubble and its message box go (stage coordinates), and the box around those shown.</summary>
public readonly record struct PetLayout(Point Pet, Point Bubble, bool BubbleOnLeft, Point Chat, Rect Extent);

/// <summary>
/// The pet with its speech bubble and its message box (DataContext = <see cref="PetsViewModel"/>), wherever it lives:
/// on the message box (<see cref="PetPerch"/>) or on the desktop (<see cref="PetWindow"/>). The host says where the pet
/// is and the room around it (<see cref="Plan"/>, <see cref="Apply"/>) and moves it when it is dragged
/// (<see cref="DragStarted"/>, <see cref="Dragged"/>, <see cref="Dropped"/>). A click (a press that does not move) opens
/// its message box, focused, beside it: Enter sends to the conversation, Esc closes it, and the keyboard goes back
/// where it was. The pet itself never takes focus.
/// </summary>
public sealed partial class PetStage : UserControl
{
    /// <summary>A press that moves less than this (in either direction) is a click, not a drag.</summary>
    private const double DragThreshold = 4;
    /// <summary>Between the pet and its message box; between the message box and the room's edges.</summary>
    private const double ChatGap = 6, ChatEdge = 8;
    /// <summary>A press on the pet right after its message box lost the keyboard (to that press) closes it, not reopens it.</summary>
    private static readonly TimeSpan ToggleWindow = TimeSpan.FromMilliseconds(250);

    private PetsViewModel? _pets;
    private bool _showsTip = true;
    /// <summary>Where a left press on the pet started, and where the pointer was last.</summary>
    private PetPointer? _press;
    private PetPointer _last;
    /// <summary>The press moved past <see cref="DragThreshold"/>: a drag.</summary>
    private bool _dragging;
    /// <summary>The press was on the pet with its message box open: the click closes it.</summary>
    private bool _toggleOff;
    private DateTime _chatLostFocusAt;
    /// <summary>What had the keyboard when the message box opened: it gets it back when the box closes.</summary>
    private IInputElement? _returnFocus;

    public PetStage()
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
    }

    /// <summary>The press on the pet became a drag (<see cref="DragThreshold"/>): where the press was.</summary>
    public event Action<PetPointer>? DragStarted;
    /// <summary>The pet is dragged: where the press was, where the pointer is now.</summary>
    public event Action<PetPointer, PetPointer>? Dragged;
    /// <summary>The drag is over (released, or the pointer taken away): where the press was, where the pointer was last.</summary>
    public event Action<PetPointer, PetPointer>? Dropped;

    /// <summary>
    /// The pet's tooltip shows on hover (on by default). Off on the desktop: popups are drawn inside their window
    /// (<c>OverlayPopups</c>, Program.cs), and the desktop pet's window is the pet's size, so a tooltip would cover it.
    /// </summary>
    public bool ShowsTip
    {
        get => _showsTip;
        set
        {
            _showsTip = value;
            ToolTip.SetServiceEnabled(Pet, value && !_dragging);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_pets is not null) _pets.PropertyChanged -= OnPetsChanged;
        _pets = DataContext as PetsViewModel;
        if (_pets is not null) _pets.PropertyChanged += OnPetsChanged;
    }

    private void OnPetsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PetsViewModel.IsChatOpen) || _pets?.IsChatOpen != true) return;
        // Shown first, then focused (with the caret after what was typed before); only where the pet is
        Dispatcher.UIThread.Post(() =>
        {
            if (_pets?.IsChatOpen != true || !PetChatBox.IsEffectivelyVisible) return;
            PetChatBox.Focus();
            PetChatBox.CaretIndex = PetChatBox.Text?.Length ?? 0;
        }, DispatcherPriority.Loaded);
    }

    // ───────────────────────────── Placing ─────────────────────────────

    /// <summary>The pet's size at its <see cref="PetsViewModel.PixelScale"/>.</summary>
    public static Size PetSize(PetsViewModel pets) => new(PetFrames.Width * pets.PixelScale, PetFrames.Height * pets.PixelScale);

    /// <summary>
    /// Where everything goes with the pet's top-left at <paramref name="at"/>, inside <paramref name="room"/> (the
    /// window, or the monitor's work area): the bubble on the pet's left (its right when the left has no room), the
    /// message box above it (below near the room's top), and the box around what is shown.
    /// </summary>
    public PetLayout Plan(Point at, Rect room)
    {
        var pet = _pets is { } pets ? PetSize(pets) : default;
        var extent = new Rect(at, pet);
        Point bubble = default, chat = default;
        var onLeft = true;
        if (PetBubble.IsVisible)
        {
            var b = PetBubble.DesiredSize;
            var left = at.X - 2 - b.Width;
            var right = at.X + pet.Width + 2;
            onLeft = left >= room.X || right + b.Width > room.Right && room.X - left < right + b.Width - room.Right;
            bubble = new Point(
                Math.Clamp(onLeft ? left : right, room.X, Math.Max(room.X, room.Right - b.Width)),
                Math.Clamp(at.Y + (_pets?.IsSmall == true ? 2 : 8), room.Y, Math.Max(room.Y, room.Bottom - b.Height)));
            extent = extent.Union(new Rect(bubble, b));
        }
        if (PetChat.IsVisible)
        {
            var c = PetChat.DesiredSize;
            var x = Math.Clamp(at.X + pet.Width - c.Width, room.X + ChatEdge, Math.Max(room.X + ChatEdge, room.Right - ChatEdge - c.Width));
            var above = at.Y - ChatGap - c.Height;
            var y = above >= room.Y + ChatEdge ? above : Math.Clamp(at.Y + pet.Height + ChatGap, room.Y, Math.Max(room.Y, room.Bottom - c.Height));
            chat = new Point(x, y);
            extent = extent.Union(new Rect(chat, c));
        }
        return new PetLayout(at, bubble, onLeft, chat, extent);
    }

    /// <summary>Puts everything where <paramref name="layout"/> says, moved by <paramref name="shift"/>.</summary>
    public void Apply(PetLayout layout, Vector shift)
    {
        Put(Pet, layout.Pet + shift);
        if (PetBubble.IsVisible)
        {
            PetBubbleTailRight.IsVisible = layout.BubbleOnLeft;
            PetBubbleTailLeft.IsVisible = !layout.BubbleOnLeft;
            Put(PetBubble, layout.Bubble + shift);
        }
        if (PetChat.IsVisible) Put(PetChat, layout.Chat + shift);
    }

    private static void Put(Control c, Point p)
    {
        Canvas.SetLeft(c, p.X);
        Canvas.SetTop(c, p.Y);
    }

    // ───────────────────────────── Drag and click ─────────────────────────────

    private PetPointer PointerAt(PointerEventArgs e)
    {
        var local = e.GetPosition(this);
        return new PetPointer(local, this.PointToScreen(local));
    }

    private void OnPetPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_pets is not { } pets || !e.GetCurrentPoint(Pet).Properties.IsLeftButtonPressed) return;
        _press = _last = PointerAt(e);
        _dragging = false;
        _toggleOff = pets.IsChatOpen || DateTime.UtcNow - _chatLostFocusAt < ToggleWindow;
        if (!pets.IsChatOpen && !_toggleOff) _returnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        e.Pointer.Capture(Pet);
        e.Handled = true;
    }

    private void OnPetMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || !ReferenceEquals(e.Pointer.Captured, Pet)) return;
        var now = PointerAt(e);
        if (!_dragging)
        {
            // The window has not moved yet: the stage's own coordinates measure the press
            var d = now.Local - press.Local;
            if (Math.Abs(d.X) < DragThreshold && Math.Abs(d.Y) < DragThreshold) return;
            _dragging = true;
            Pet.Classes.Set("dragging", true);
            ToolTip.SetIsOpen(Pet, false);
            ToolTip.SetServiceEnabled(Pet, false);
            DragStarted?.Invoke(press);
        }
        _last = now;
        Dragged?.Invoke(press, now);
    }

    private void OnPetReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is null || e.InitialPressMouseButton != MouseButton.Left) return;
        var clicked = !_dragging;
        EndPress();
        if (clicked) Click();
        e.Handled = true;
    }

    /// <summary>The press is over (released, or the pointer taken away): a drag drops the pet where it is.</summary>
    private void EndPress()
    {
        if (_press is not { } press) return;
        _press = null;
        var dropped = _dragging;
        _dragging = false;
        Pet.Classes.Set("dragging", false);
        ToolTip.SetServiceEnabled(Pet, _showsTip);
        if (dropped) Dropped?.Invoke(press, _last);
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
        if (to is InputElement { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true } el
            && TopLevel.GetTopLevel(el) == TopLevel.GetTopLevel(this) && !ReferenceEquals(el, PetChatBox))
            el.Focus();
    }
}

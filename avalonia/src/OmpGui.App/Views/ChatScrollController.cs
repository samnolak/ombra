using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using OmpGui.App.Controls;

namespace OmpGui.App.Views;

/// <summary>
/// The conversation's scroll position has one owner: this. While it follows the latest message, every change of the
/// extent or the viewport (a reply growing, rows coming in, a resize, a panel opening) puts the view on the bottom inside
/// the same layout pass, so no frame is drawn anywhere else. Only the reader stops the following, by scrolling up (the
/// wheel or trackpad, the scroll bar, the keys); scrolling back to within <see cref="ReattachDistance"/> of the bottom,
/// "Jump to latest", sending a message or opening a conversation follow again. Nothing else ever decides: no timers, no
/// guessing from offset changes, which layout (rows measured, scroll anchoring, a coerced offset) also makes.
/// </summary>
internal sealed class ChatScrollController
{
    /// <summary>How near the bottom a reader has to scroll for the conversation to follow its latest message again.</summary>
    public const double ReattachDistance = 24;
    /// <summary>One arrow key: the distance of one wheel notch (as the ScrollViewer scrolls a wheel step).</summary>
    private const double LineStep = 50;

    private readonly ScrollViewer _scroll;
    private readonly TranscriptPanel? _rows;

    public ChatScrollController(ScrollViewer scroll, InputElement keySource, TranscriptPanel? rows)
    {
        _scroll = scroll;
        _rows = rows;
        if (rows is not null)
        {
            rows.FollowsEnd = IsFollowing;
            rows.ShiftView = ShiftView;
        }
        _scroll.PropertyChanged += OnScrollPropertyChanged;
        // Wheel and trackpad: up leaves the latest message before the ScrollViewer moves (so no layout in between pulls the
        // view back down); after it moved (bubbling, once the presenter has scrolled), down may have reached the bottom.
        _scroll.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => { if (e.Delta.Y > 0) LeaveIfScrollable(); },
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => { if (e.Delta.Y < 0) FollowIfAtBottom(); },
            RoutingStrategies.Bubble, handledEventsToo: true);
        // Touch scrolling: a negative delta moves towards the start
        _scroll.AddHandler(InputElement.ScrollGestureEvent, (_, e) => { if (e.Delta.Y < 0) LeaveIfScrollable(); },
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(InputElement.ScrollGestureEvent, (_, e) => { if (e.Delta.Y > 0) FollowIfAtBottom(); },
            RoutingStrategies.Bubble, handledEventsToo: true);
        // The ScrollViewer's own bar (the rows hold scroll viewers of their own: code blocks, diffs)
        if (_scroll.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(b => b.TemplatedParent == _scroll && b.Orientation == Orientation.Vertical) is { } bar)
            bar.Scroll += OnScrollBarScroll;
        keySource.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        keySource.AddHandler(InputElement.GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble);
        // A click anywhere in the conversation (between blocks, on a code block, the padding) gives it the keys, as in
        // Claude: Page Up/Down, Home/End and the arrows then scroll it. Text and buttons take the focus themselves; any
        // other press left it in the message box, where those keys move the caret.
        keySource.AddHandler(InputElement.PointerPressedEvent, (_, _) => { if (!keySource.IsKeyboardFocusWithin) keySource.Focus(NavigationMethod.Pointer); },
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The view stays on the latest message as the conversation grows.</summary>
    public bool IsFollowing { get; private set; } = true;

    /// <summary>Raised when <see cref="IsFollowing"/> changes (the "Jump to latest" button shows while it is off).</summary>
    public event Action? FollowingChanged;

    /// <summary>To the latest message, following it again: "Jump to latest", a message sent, a conversation opened.</summary>
    public void FollowLatest()
    {
        SetFollowing(true);
        PinToBottom();
    }

    /// <summary>Where the reader is now, kept with a chat while another one is shown (<see cref="Restore"/>).</summary>
    public ChatScrollAnchor Save(TranscriptPanel rows)
    {
        if (IsFollowing || rows.TranslatePoint(default, _scroll) is not { } origin) return ChatScrollAnchor.Latest;
        // The view's top in the panel's coordinates (the panel starts below the list's padding, moved up by the offset)
        var (row, into) = rows.RowAtOffset(-origin.Y);
        return new ChatScrollAnchor(false, row, into);
    }

    /// <summary>
    /// Back where the reader was when the chat was left: on the latest message if it was following it (or for a chat not
    /// shown before), else with the same row at the top of the view, as far into it. The rows come back with the heights
    /// they were measured at (Controls/TranscriptPanel keeps them with the rows), so the first layout puts that row in
    /// place; the passes after it only correct for rows measured again differently (the window's width changed).
    /// </summary>
    public void Restore(ChatScrollAnchor? anchor, TranscriptPanel rows)
    {
        if (anchor is not { Following: false })
        {
            FollowLatest();
            return;
        }
        SetFollowing(false);
        try
        {
            for (var pass = 0; pass < 4; pass++)
            {
                // The rows are realized where the view is going, not where the last chat's was
                rows.PendingTop = rows.TopOfRow(anchor.Row) + anchor.IntoRow;
                _scroll.UpdateLayout();
                if (rows.TranslatePoint(default, _scroll) is not { } origin) return;
                var delta = rows.TopOfRow(anchor.Row) + anchor.IntoRow + origin.Y;
                var y = Math.Clamp(_scroll.Offset.Y + delta, 0, Bottom);
                if (Math.Abs(y - _scroll.Offset.Y) < 0.5) return;
                _scroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(_scroll.Offset.X, y));
            }
        }
        finally { rows.PendingTop = null; }
    }

    private double Bottom => Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);

    private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Raised inside the layout pass (the presenter sets them as it arranges): pinned before the frame is drawn
        if (IsFollowing && (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)) PinToBottom();
    }

    private void PinToBottom()
    {
        var bottom = Bottom;
        if (_scroll.Offset.Y == bottom) return;
        Services.PerfLog.Count("transcript_follow");
        _scroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(_scroll.Offset.X, bottom));
    }

    /// <summary>The panel holding the row at the top of the view through a width change (TranscriptPanel): the view
    /// moved by <paramref name="delta"/>, within the scroll range, unless it follows the latest message.</summary>
    private double ShiftView(double delta)
    {
        if (IsFollowing) return 0;
        var y = Math.Clamp(_scroll.Offset.Y + delta, 0, Bottom);
        var moved = y - _scroll.Offset.Y;
        if (Math.Abs(moved) < 0.5) return 0;
        _scroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(_scroll.Offset.X, y));
        return moved;
    }

    private void SetFollowing(bool value)
    {
        if (_rows is not null) _rows.FollowsEnd = value;
        if (IsFollowing == value) return;
        IsFollowing = value;
        FollowingChanged?.Invoke();
    }

    /// <summary>The reader moved up: away from the latest message, unless there is nowhere to go (all of it fits).</summary>
    private void LeaveIfScrollable()
    {
        if (Bottom > 0) SetFollowing(false);
    }

    /// <summary>The reader moved down: back to following once within <see cref="ReattachDistance"/> of the bottom.</summary>
    private void FollowIfAtBottom()
    {
        if (_scroll.Offset.Y < Bottom - ReattachDistance) return;
        SetFollowing(true);
        PinToBottom();
    }

    private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
    {
        // While the thumb is dragged each frame lands somewhere new: only the rows in view are built, the ones around
        // them once it is let go (TranscriptPanel.ViewportOnly)
        if (_rows is not null) _rows.ViewportOnly = e.ScrollEventType == ScrollEventType.ThumbTrack;
        // Raised after the bar moved the view: a drag or a click towards the start leaves, one ending near the bottom follows
        switch (e.ScrollEventType)
        {
            case ScrollEventType.SmallDecrement or ScrollEventType.LargeDecrement:
                LeaveIfScrollable();
                break;
            case ScrollEventType.ThumbTrack when _scroll.Offset.Y < Bottom - ReattachDistance:
                SetFollowing(false);
                break;
            default:
                FollowIfAtBottom();
                break;
        }
    }

    /// <summary>
    /// Page Up / Page Down, Home / End and the arrows scroll the conversation when the focus is in it (not in the message
    /// box): by a page, to either end, by a wheel notch. Modified keys are left alone (Shift+arrows extend a selection).
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox) return;
        var page = _scroll.Viewport.Height;
        var y = _scroll.Offset.Y;
        switch (e.Key)
        {
            case Key.End:
                FollowLatest();
                break;
            case Key.Home:
                ScrollBy(-y);
                break;
            case Key.PageUp:
                ScrollBy(-page);
                break;
            case Key.Up:
                ScrollBy(-LineStep);
                break;
            case Key.PageDown:
                ScrollBy(page);
                break;
            case Key.Down:
                ScrollBy(LineStep);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ScrollBy(double delta)
    {
        if (delta < 0) LeaveIfScrollable();
        _scroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(_scroll.Offset.X, Math.Clamp(_scroll.Offset.Y + delta, 0, Bottom)));
        if (delta > 0) FollowIfAtBottom();
    }

    /// <summary>
    /// The list does not scroll to whatever gets the focus (MainWindow.axaml, BringIntoViewOnFocusChange off: a menu
    /// closing gave it back to text clicked far away, and the view jumped there); only the keyboard moving the focus (Tab,
    /// the arrows) brings what it reached into view, leaving the latest message if that is above it.
    /// </summary>
    private void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.NavigationMethod is not (NavigationMethod.Tab or NavigationMethod.Directional) || e.Source is not Control control) return;
        control.BringIntoView();
        if (_scroll.Offset.Y < Bottom - ReattachDistance) SetFollowing(false);
    }
}

/// <summary>Where a reader was in a conversation: following its latest message, or a row at the top of the view and how
/// far into that row (by row, not by pixel: the rows are measured again when the chat is shown again).</summary>
internal sealed record ChatScrollAnchor(bool Following, int Row, double IntoRow)
{
    public static ChatScrollAnchor Latest { get; } = new(true, 0, 0);
}

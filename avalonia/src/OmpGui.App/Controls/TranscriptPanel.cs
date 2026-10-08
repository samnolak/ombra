using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace OmpGui.App.Controls;

/// <summary>
/// The conversation's items panel: a vertical virtualizing stack that knows where every row is.
/// Avalonia's VirtualizingStackPanel estimates every unmeasured row from the average of the rows on screen and, whenever
/// a realized row changes height, forgets where its rows are and estimates the screen again: a reply growing at the
/// bottom re-realized the visible rows about 50 times a second (rows blinking, the view jumping), and a 500-row chat
/// measured a quarter of its real height (the scroll bar lied). Here:
/// <list type="bullet">
/// <item>A row's position is the sum of the heights above it. A row seen once has its measured height, remembered with
/// the row itself, so a chat shown again knows its rows' heights. A row never seen has an estimate of its own (<see
/// cref="EstimateHeight"/>: from its text, at this width), which no other row changes: measuring rows moves only the
/// rows below them, never the ones above the view, and the length of the conversation does not swing from one guess to
/// the next (an average of the rows measured so far did both: one 14,000 px reply made the scroll bar three times too
/// long, and dragging its thumb showed blank frames and ended mid-track).</item>
/// <item>Rows are realized around the viewport only; while the scroll bar's thumb is dragged, the viewport alone
/// (<see cref="ViewportOnly"/>): each frame of a drag lands somewhere new, and rows built around it would be thrown
/// away by the next.</item>
/// <item>A row leaving the viewport stays built, hidden, still showing its row: scrolling back to it costs nothing,
/// and a new row of the same kind takes the container left the longest ago (its template stays, only the row
/// changes). Only beyond <see cref="HiddenRows"/> hidden containers is one released.</item>
/// <item>The visible rows are the ScrollViewer's anchor candidates, so its scroll anchoring holds them still when a row
/// above them changes height. A width change, which it does not follow, holds the row at the top of the view itself
/// (<see cref="HoldRowAfterWidthChange"/>).</item>
/// </list>
/// </summary>
public sealed class TranscriptPanel : VirtualizingPanel
{
    /// <summary>Rows realized above and below the viewport, in viewports: a wheel turn finds them built.</summary>
    private const double CacheViewports = 1;
    /// <summary>The height of an unseen row when there is no <see cref="EstimateHeight"/>.</summary>
    private const double FirstEstimate = 60;
    /// <summary>Containers kept built (hidden, each with its last row) once their row left the realized range: a
    /// conversation's worth of rows to scroll back to, bounded so a long session does not keep every row built.</summary>
    private const int HiddenRows = 120;

    private static readonly AttachedProperty<object?> RecycleKeyProperty =
        AvaloniaProperty.RegisterAttached<TranscriptPanel, Control, object?>("RecycleKey");
    private static readonly object ItemIsItsOwnContainer = new();

    /// <summary>Each row's measured height; NaN until it has been measured.</summary>
    private readonly List<double> _heights = [];
    /// <summary>Each unmeasured row's estimate, made at the width of the layout that first needed it; NaN until then.</summary>
    private readonly List<double> _estimates = [];
    /// <summary>The height each row was last measured at, and at what width, kept with the row (not its index): a
    /// chat shown again, or a row moved, finds its height.</summary>
    private readonly ConditionalWeakTable<object, MeasuredHeight> _measured = new();
    /// <summary>The top of each row (and, last, the end of the list); rebuilt when a height changes.</summary>
    private double[] _tops = [0];
    private bool _topsDirty = true;
    private readonly Dictionary<int, Control> _realized = [];
    /// <summary>The hidden containers, least recently shown first; each still shows its last row.</summary>
    private readonly List<HiddenRow> _hidden = [];
    private readonly Dictionary<object, HiddenRow> _hiddenByItem = new(ReferenceEqualityComparer.Instance);
    private Rect _viewport;
    private bool _hasViewport;
    /// <summary>The rows the last measure realized for the viewport (a row kept for its focus outside them is not counted).</summary>
    private (int First, int Last) _range = (0, -1);
    private double _width = double.NaN;
    private IScrollAnchorProvider? _anchors;
    private Func<object?, double, double>? _estimateHeight;
    private bool _viewportOnly;

    public TranscriptPanel() => EffectiveViewportChanged += OnEffectiveViewportChanged;

    /// <summary>The height of the rows themselves (raised when it changes): what the conversation's content takes,
    /// even when the list is stretched to fill a taller view.</summary>
    public double ContentHeight { get; private set; }

    public event EventHandler? ContentHeightChanged;

    /// <summary>
    /// How tall a row not measured yet will be at a width (Views/TranscriptRowHeights: from its text and kind). It must
    /// depend on the row alone: an estimate that learnt from other rows would move every unmeasured row each time one
    /// is measured.
    /// </summary>
    public Func<object?, double, double>? EstimateHeight
    {
        get => _estimateHeight;
        set
        {
            _estimateHeight = value;
            ForgetEstimates();
            InvalidateMeasure();
        }
    }

    /// <summary>
    /// Realize only the rows in the viewport, none around it: set while the scroll bar's thumb is dragged
    /// (Views/ChatScrollController), where each frame jumps and the rows around one view are not the next one's. The
    /// rows around the view are built once it is cleared.
    /// </summary>
    internal bool ViewportOnly
    {
        get => _viewportOnly;
        set
        {
            if (_viewportOnly == value) return;
            _viewportOnly = value;
            if (!value) InvalidateMeasure();
        }
    }

    /// <summary>
    /// The view is on the end of the rows (Views/ChatScrollController, while it follows the latest message): the rows
    /// realized are the last ones, wherever the viewport was. A chat opened on its latest message builds its last rows,
    /// not the ones where the previous chat's view happened to be (the ScrollViewer gets to the new end only once this
    /// layout has given it the new rows' extent).
    /// </summary>
    internal bool FollowsEnd
    {
        get => _followsEnd;
        set
        {
            if (_followsEnd == value) return;
            _followsEnd = value;
            if (value) InvalidateMeasure();
        }
    }

    private bool _followsEnd;

    /// <summary>Where the top of the view is going (the panel's coordinates), while a chat is shown again at the row its
    /// reader left it at (Views/ChatScrollController.Restore): realized there, not where the previous chat's view was.</summary>
    internal double? PendingTop { get; set; }

    /// <summary>
    /// Moves the view by a distance (down for a positive one) and returns how far it moved (clamped to the scroll range):
    /// set by Views/ChatScrollController, which owns the scroll position. Used to put the row at the top of the view back
    /// in place after a width change (<see cref="HoldRowAfterWidthChange"/>).
    /// </summary>
    internal Func<double, double>? ShiftView { get; set; }

    /// <summary>The row at the top of the view before the width changed, how far into it the view started, and how many
    /// corrections have been made; null when no width change is being held.</summary>
    private (int Row, double IntoRow, int Tries)? _widthHold;
    /// <summary>Corrections after one width change: moving the view realizes rows around it, which can move the row again.</summary>
    private const int WidthHoldTries = 3;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _anchors = this.FindAncestorOfType<IScrollAnchorProvider>();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _anchors = null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        if (_heights.Count != items.Count) ResetHeights(items);
        if (items.Count == 0)
        {
            HideAll();
            SetContentHeight(0);
            return default;
        }
        // A new width measures again the rows on screen and the ones realized above them, and the ScrollViewer's anchoring
        // does not follow rows measured again above the view through a resize: the row at the top moved by dozens of
        // pixels. The row at the top of the view and how far into it the view starts are kept, and put back once the
        // layout is done (HoldRowAfterWidthChange). A view following the end or going to a given place holds nothing.
        if (_widthHold is null && !_followsEnd && PendingTop is null && _hasViewport && double.IsFinite(_width)
            && double.IsFinite(availableSize.Width) && Math.Abs(availableSize.Width - _width) >= 0.5 && AnchorRow() is { Index: >= 0 } atTop)
            _widthHold = (atTop.Index, _viewport.Top - atTop.Top, 0);
        _width = availableSize.Width;
        var constraint = new Size(availableSize.Width, double.PositiveInfinity);
        var view = MeasureViewport(availableSize);
        // The row the scroll anchoring will hold still, and where it is now: rows measured above it move it, and the
        // viewport moves with it (the anchoring follows in the arrange), so the rows to realize are found where the
        // viewport is going to be, not where it was. A view going to the end or to a given place has no such row. (A new
        // width moves no row before they are measured again: the estimates stay, see EstimateAt.)
        var (anchor, anchorTop) = _followsEnd || PendingTop is not null ? (-1, 0.0) : AnchorRow();
        for (var pass = 0; pass < 4; pass++)
        {
            // The end moves as the last rows are measured: each pass realizes the rows at the end found by the last
            if (PendingTop is { } pending) view = new Rect(view.X, pending, view.Width, view.Height);
            else if (_followsEnd) view = new Rect(view.X, Math.Max(0, Tops[^1] - view.Height), view.Width, view.Height);
            var (first, last) = _range = RangeFor(view);
            HideOutside(first, last);
            var changed = false;
            for (var i = first; i <= last; i++)
            {
                var element = Realize(items, i);
                element.Measure(constraint);
                changed |= SetHeight(i, element.DesiredSize.Height);
            }
            // Rows still realized outside the range (the one holding the focus) keep their measured height
            foreach (var (i, element) in _realized)
                if (i < first || i > last)
                {
                    element.Measure(constraint);
                    changed |= SetHeight(i, element.DesiredSize.Height);
                }
            if (!changed) break;
            if (anchor >= 0 && anchor < _heights.Count)
            {
                var top = Tops[anchor];
                view = view.Translate(new Vector(0, top - anchorTop));
                anchorTop = top;
            }
        }
        var width = 0.0;
        foreach (var element in _realized.Values) width = Math.Max(width, element.DesiredSize.Width);
        var height = Tops[^1];
        SetContentHeight(height);
        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(width, availableSize.Width) : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var tops = Tops;
        foreach (var (i, element) in _realized)
        {
            var rect = new Rect(0, tops[i], finalSize.Width, _heights[i]);
            var was = element.Bounds;
            element.Arrange(rect);
            // The rows on screen are the ones the ScrollViewer may anchor to. A row that was on screen stays one through
            // this arrange even if it moves off: the ScrollViewer picked its anchor among the rows where they were, and
            // only once this arrange is done moves its offset by what the anchor moved. _viewport is still where the view
            // was, so a row above shrinking by more than the anchor's distance to the top of the view put the anchor
            // outside it, and unregistering it then dropped the anchor: nothing was held, the rows on screen jumped.
            if (element.IsVisible && _hasViewport && (_viewport.Intersects(rect) || _viewport.Intersects(was))) _anchors?.RegisterAnchorCandidate(element);
            else _anchors?.UnregisterAnchorCandidate(element);
        }
        return finalSize;
    }

    /// <summary>The viewport to realize rows for: the visible part of the panel, or its start before it has one.</summary>
    private Rect MeasureViewport(Size availableSize) =>
        _hasViewport && _viewport.Height > 0
            ? _viewport
            : new Rect(0, 0, availableSize.Width, double.IsFinite(availableSize.Height) ? availableSize.Height : 800);

    /// <summary>The realized row nearest the top of the viewport (as the ScrollViewer picks its anchor), and its top.</summary>
    private (int Index, double Top) AnchorRow()
    {
        if (!_hasViewport) return (-1, 0);
        var tops = Tops;
        var best = (Index: -1, Top: 0.0);
        var distance = double.MaxValue;
        foreach (var i in _realized.Keys)
        {
            var on = tops[i] < _viewport.Bottom && tops[i + 1] > _viewport.Top;
            var d = Math.Abs(tops[i] - _viewport.Top);
            if (on && d < distance) (distance, best) = (d, (i, tops[i]));
        }
        return best;
    }

    private double Cache(Rect view) => _viewportOnly ? 0 : view.Height * CacheViewports;

    /// <summary>The rows covering the viewport and the cache around it.</summary>
    private (int First, int Last) RangeFor(Rect view)
    {
        var cache = Cache(view);
        var tops = Tops;
        var first = RowAt(tops, view.Top - cache);
        var last = RowAt(tops, view.Bottom + cache);
        return (first, Math.Max(first, last));
    }

    /// <summary>The row at a position (binary search over the tops), clamped to the list.</summary>
    private static int RowAt(double[] tops, double y)
    {
        var count = tops.Length - 1;
        if (y <= 0) return 0;
        if (y >= tops[count]) return count - 1;
        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (tops[mid] <= y) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The row at <paramref name="y"/> (the panel's coordinates) and how far into it <paramref name="y"/> is: where
    /// a reader was, kept by row because the heights are measured again when the rows come back.</summary>
    internal (int Row, double IntoRow) RowAtOffset(double y)
    {
        if (Items.Count == 0) return (0, 0);
        if (_heights.Count != Items.Count) ResetHeights(Items);
        var tops = Tops;
        var row = RowAt(tops, y);
        return (row, y - tops[row]);
    }

    /// <summary>Where a row starts (the panel's coordinates): the rows above it at their measured height, the others estimated.</summary>
    internal double TopOfRow(int row)
    {
        var count = Items.Count;
        if (count == 0) return 0;
        if (_heights.Count != count) ResetHeights(Items);
        return Tops[Math.Clamp(row, 0, count - 1)];
    }

    /// <summary>The width rows are laid out at (the panel's, before its first measure).</summary>
    private double LayoutWidth => double.IsFinite(_width) && _width > 0 ? _width : Bounds.Width > 0 ? Bounds.Width : 800;

    private double[] Tops
    {
        get
        {
            if (!_topsDirty) return _tops;
            var count = _heights.Count;
            if (_tops.Length != count + 1) _tops = new double[count + 1];
            var y = 0.0;
            for (var i = 0; i < count; i++)
            {
                _tops[i] = y;
                var h = _heights[i];
                y += double.IsNaN(h) ? EstimateAt(i) : h;
            }
            _tops[count] = y;
            _topsDirty = false;
            return _tops;
        }
    }

    /// <summary>
    /// An unmeasured row's estimate, made once from the row alone, at the width of the layout that first needed it. A
    /// new width (a pane opening, the window narrowed) does not estimate the rows again, as it does not measure again
    /// the rows measured out of view: off screen, every row keeps its height until it is laid out. Estimated again,
    /// every row above the view moved at once (thousands of pixels in a long chat), and a reader scrolled up lost their
    /// place.
    /// </summary>
    private double EstimateAt(int index)
    {
        var e = _estimates[index];
        if (!double.IsNaN(e)) return e;
        var items = Items;
        e = index < items.Count && _estimateHeight is { } estimate ? estimate(items[index], LayoutWidth) : FirstEstimate;
        e = double.IsFinite(e) ? Math.Max(0, e) : FirstEstimate;
        _estimates[index] = e;
        return e;
    }

    private void ForgetEstimates()
    {
        for (var i = 0; i < _estimates.Count; i++) _estimates[i] = double.NaN;
        _topsDirty = true;
    }

    private bool SetHeight(int index, double height)
    {
        if (Items[index] is { } item && !item.GetType().IsValueType)
        {
            var known = _measured.GetValue(item, static _ => new MeasuredHeight());
            (known.Width, known.Height) = (LayoutWidth, height);
        }
        if (_heights[index].Equals(height)) return false;
        _heights[index] = height;
        _topsDirty = true;
        return true;
    }

    /// <summary>The height a row was measured at, if it was at this width (else it is measured or estimated again).</summary>
    private double KnownHeight(object? item) =>
        item is not null && _measured.TryGetValue(item, out var known) && Math.Abs(known.Width - LayoutWidth) < 0.5 ? known.Height : double.NaN;

    private void SetContentHeight(double height)
    {
        if (ContentHeight.Equals(height)) return;
        ContentHeight = height;
        ContentHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetHeights(IReadOnlyList<object?> items)
    {
        _heights.Clear();
        _estimates.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            _heights.Add(KnownHeight(items[i]));
            _estimates.Add(double.NaN);
        }
        _topsDirty = true;
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var viewport = e.EffectiveViewport.Intersect(new Rect(Bounds.Size));
        if (viewport.Height <= 0) return;
        var resized = !_hasViewport || !viewport.Size.Equals(_viewport.Size);
        _viewport = viewport;
        _hasViewport = true;
        if (_widthHold is { } hold) HoldRowAfterWidthChange(hold);
        // Measure again when the view comes near the edge of the realized rows (or changes size): moving within them
        // needs nothing, which keeps a wheel turn from laying the list out again.
        if (resized || !Covers(viewport)) InvalidateMeasure();
    }

    /// <summary>
    /// The layout at a new width is done (the viewport is raised after the arrange, inside the same layout pass): the
    /// view is moved so the row that was at its top is there again, as far into it as before. Moving it realizes and
    /// measures rows around the new view, which can move the row again, so the next viewport is checked too, a few times
    /// at most; a view that did not move (at either end of the scroll range) ends it, as nothing more will come.
    /// </summary>
    private void HoldRowAfterWidthChange((int Row, double IntoRow, int Tries) hold)
    {
        _widthHold = null;
        if (_followsEnd || PendingTop is not null || hold.Row >= _heights.Count || ShiftView is not { } shiftView) return;
        var shift = Tops[hold.Row] + hold.IntoRow - _viewport.Top;
        if (Math.Abs(shift) < 0.5) return;
        if (Math.Abs(shiftView(shift)) >= 0.5 && hold.Tries + 1 < WidthHoldTries) _widthHold = hold with { Tries = hold.Tries + 1 };
    }

    /// <summary>
    /// The rows the last measure realized reach half the cache beyond the viewport on both sides (or the ends of the
    /// list). A row realized only because it holds the focus does not count: the rows between it and the rest are not
    /// realized.
    /// </summary>
    private bool Covers(Rect viewport)
    {
        if (Items.Count == 0) return true;
        var (first, last) = _range;
        if (first < 0 || last < first || last >= _heights.Count) return false;
        var tops = Tops;
        var margin = Cache(viewport) / 2;
        var coversTop = first == 0 || tops[first] <= viewport.Top - margin;
        var coversBottom = last == _heights.Count - 1 || tops[last + 1] >= viewport.Bottom + margin;
        return coversTop && coversBottom;
    }

    private Control Realize(IReadOnlyList<object?> items, int index)
    {
        if (_realized.TryGetValue(index, out var realized)) return realized;
        var generator = ItemContainerGenerator!;
        var item = items[index];
        Control container;
        if (item is not null && _hiddenByItem.Remove(item, out var own))
        {
            // Its own container, still built: shown again as it was, measured whole (its row may have changed while
            // hidden, deep inside it, where the layout would measure only after this panel)
            _hidden.Remove(own);
            container = own.Container;
            container.SetCurrentValue(IsVisibleProperty, true);
            LayoutHelper.InvalidateSelfAndChildrenMeasure(container);
            generator.ItemContainerPrepared(container, item, index);
        }
        else if (!generator.NeedsContainer(item, index, out var recycleKey))
        {
            container = (Control)item!;
            if (!container.IsSet(RecycleKeyProperty))
            {
                generator.PrepareItemContainer(container, container, index);
                AddInternalChild(container);
                container.SetValue(RecycleKeyProperty, ItemIsItsOwnContainer);
                generator.ItemContainerPrepared(container, item, index);
            }
            container.SetCurrentValue(IsVisibleProperty, true);
        }
        else if (recycleKey is not null && TakeHidden(recycleKey, item?.GetType()) is { } reused)
        {
            // The container a row of the same kind left the longest ago: its template stays, only the row changes. The
            // item container generator only sets the content of a container that has none, so it is set here.
            container = reused;
            generator.PrepareItemContainer(container, item, index);
            if (container is ContentControl content && !ReferenceEquals(content.Content, item)) content.SetCurrentValue(ContentControl.ContentProperty, item);
            container.SetCurrentValue(IsVisibleProperty, true);
            // Its new row changed controls deep inside it, which the layout would measure after this panel: measured
            // whole now, the row's height is its own from its first measure, not the last row's
            LayoutHelper.InvalidateSelfAndChildrenMeasure(container);
            generator.ItemContainerPrepared(container, item, index);
        }
        else
        {
            container = generator.CreateContainer(item, index, recycleKey);
            container.SetValue(RecycleKeyProperty, recycleKey);
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
        }
        _realized[index] = container;
        return container;
    }

    /// <summary>The least recently shown hidden container for rows of a kind, taken out of the hidden ones.</summary>
    private Control? TakeHidden(object recycleKey, Type? type)
    {
        for (var i = 0; i < _hidden.Count; i++)
        {
            var hidden = _hidden[i];
            if (!Equals(hidden.RecycleKey, recycleKey) || hidden.Type != type) continue;
            _hidden.RemoveAt(i);
            if (hidden.Item is { } item && _hiddenByItem.TryGetValue(item, out var mapped) && ReferenceEquals(mapped, hidden)) _hiddenByItem.Remove(item);
            return hidden.Container;
        }
        return null;
    }

    /// <summary>Hides the rows outside a range, except one holding the keyboard focus (a selection in it stays).</summary>
    private void HideOutside(int first, int last)
    {
        List<int>? gone = null;
        foreach (var (i, element) in _realized)
            if ((i < first || i > last) && !element.IsKeyboardFocusWithin) (gone ??= []).Add(i);
        if (gone is null) return;
        foreach (var i in gone)
        {
            var element = _realized[i];
            _realized.Remove(i);
            Hide(element);
        }
    }

    private void HideAll()
    {
        var all = _realized.Values.ToList();
        _realized.Clear();
        foreach (var element in all) Hide(element);
    }

    /// <summary>
    /// A row out of the realized range (taken out of <see cref="_realized"/> first, see below): its container hidden,
    /// still showing it, so it comes back as it was or takes another row of its kind; the one hidden the longest is
    /// released past <see cref="HiddenRows"/>. The row is the container's DataContext (the generator sets it for every
    /// row that is not a control itself), which is right even after the list was replaced.
    /// </summary>
    private void Hide(Control element)
    {
        _anchors?.UnregisterAnchorCandidate(element);
        MarkdownView.ForgetAnchorsIn(element);
        var recycleKey = element.GetValue(RecycleKeyProperty);
        if (recycleKey == ItemIsItsOwnContainer)
        {
            element.SetCurrentValue(IsVisibleProperty, false);
            return;
        }
        if (recycleKey is null || element.DataContext is not { } item)
        {
            ItemContainerGenerator!.ClearItemContainer(element);
            RemoveInternalChild(element);
            return;
        }
        // Not selected while hidden: shown again (for this row or another) it takes the selection's state. Cleared once
        // out of the realized rows, so the list does not take it for the reader deselecting the row.
        if (element.IsSet(SelectingItemsControl.IsSelectedProperty)) element.ClearValue(SelectingItemsControl.IsSelectedProperty);
        element.SetCurrentValue(IsVisibleProperty, false);
        var hidden = new HiddenRow(element, item, recycleKey, item.GetType());
        if (_hiddenByItem.Remove(item, out var stale)) _hidden.Remove(stale);
        _hiddenByItem[item] = hidden;
        _hidden.Add(hidden);
        if (_hidden.Count <= HiddenRows) return;
        var oldest = _hidden[0];
        _hidden.RemoveAt(0);
        if (_hiddenByItem.TryGetValue(oldest.Item, out var mapped) && ReferenceEquals(mapped, oldest)) _hiddenByItem.Remove(oldest.Item);
        ItemContainerGenerator!.ClearItemContainer(oldest.Container);
        RemoveInternalChild(oldest.Container);
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);
        InvalidateMeasure();
        // Rows added after the held one (the conversation growing at its end) leave it where it is; any other change of
        // the rows ends holding it through a width change
        if (_widthHold is { } hold && !(e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex > hold.Row)) _widthHold = null;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex >= 0:
                Inserted(e.NewStartingIndex, e.NewItems!);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0:
                Removed(e.OldStartingIndex, e.OldItems!);
                break;
            case NotifyCollectionChangedAction.Replace when e.OldStartingIndex >= 0:
                for (var k = 0; k < e.OldItems!.Count; k++)
                {
                    var i = e.OldStartingIndex + k;
                    if (_realized.Remove(i, out var element)) Hide(element);
                    _heights[i] = KnownHeight(items[i]);
                    _estimates[i] = double.NaN;
                }
                _topsDirty = true;
                break;
            case NotifyCollectionChangedAction.Move when e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0:
                Removed(e.OldStartingIndex, e.OldItems!);
                Inserted(e.NewStartingIndex, e.NewItems!);
                break;
            default:
                HideAll();
                ResetHeights(items);
                break;
        }
    }

    private void Inserted(int index, System.Collections.IList added)
    {
        Reindex(index, added.Count);
        for (var k = 0; k < added.Count; k++)
        {
            _heights.Insert(index + k, KnownHeight(added[k]));
            _estimates.Insert(index + k, double.NaN);
        }
        _topsDirty = true;
    }

    private void Removed(int index, System.Collections.IList removed)
    {
        for (var k = 0; k < removed.Count; k++)
            if (_realized.Remove(index + k, out var element)) Hide(element);
        _heights.RemoveRange(index, removed.Count);
        _estimates.RemoveRange(index, removed.Count);
        Reindex(index + removed.Count, -removed.Count);
        _topsDirty = true;
    }

    /// <summary>Moves the realized rows at or after <paramref name="from"/> by <paramref name="delta"/> indexes.</summary>
    private void Reindex(int from, int delta)
    {
        var moved = _realized.Where(p => p.Key >= from).OrderBy(p => delta > 0 ? -p.Key : p.Key).ToList();
        foreach (var (i, element) in moved)
        {
            _realized.Remove(i);
            _realized[i + delta] = element;
            ItemContainerGenerator?.ItemContainerIndexChanged(element, i, i + delta);
        }
    }

    protected override IEnumerable<Control>? GetRealizedContainers() =>
        _realized.OrderBy(p => p.Key).Select(p => p.Value);

    protected override Control? ContainerFromIndex(int index) => _realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (i, element) in _realized)
            if (ReferenceEquals(element, container)) return i;
        return -1;
    }

    protected override Control? ScrollIntoView(int index)
    {
        var items = Items;
        if (index < 0 || index >= items.Count || !IsEffectivelyVisible) return null;
        if (_heights.Count != items.Count) ResetHeights(items);
        if (!_realized.TryGetValue(index, out var element))
        {
            // Realized and measured where it goes, so the ScrollViewer can bring it into view; the next measure keeps it
            // if it is then on screen.
            element = Realize(items, index);
            var width = LayoutWidth;
            element.Measure(new Size(width, double.PositiveInfinity));
            SetHeight(index, element.DesiredSize.Height);
            element.Arrange(new Rect(0, Tops[index], width, _heights[index]));
            InvalidateMeasure();
        }
        element.BringIntoView();
        return element;
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Items.Count;
        var fromIndex = from is Control c ? IndexFromContainer(c) : -1;
        if (count == 0 || (fromIndex < 0 && direction is not (NavigationDirection.First or NavigationDirection.Last))) return null;
        var toIndex = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Next or NavigationDirection.Down => fromIndex + 1,
            NavigationDirection.Previous or NavigationDirection.Up => fromIndex - 1,
            _ => fromIndex,
        };
        if (toIndex == fromIndex) return from;
        if (wrap) toIndex = (toIndex + count) % count;
        return toIndex < 0 || toIndex >= count ? null : ScrollIntoView(toIndex);
    }

    private sealed class MeasuredHeight
    {
        public double Width;
        public double Height;
    }

    /// <summary>A hidden container, the row it still shows, and the kind of row it can take.</summary>
    private sealed class HiddenRow(Control container, object item, object recycleKey, Type type)
    {
        public Control Container { get; } = container;
        public object Item { get; } = item;
        public object RecycleKey { get; } = recycleKey;
        public Type Type { get; } = type;
    }
}

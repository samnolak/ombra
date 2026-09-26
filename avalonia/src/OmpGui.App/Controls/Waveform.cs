using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OmpGui.App.Controls;

/// <summary>
/// A live microphone waveform: one rounded vertical bar per level (0..1), newest on the right, centred vertically.
/// Slots without a level yet are drawn as faint dots, so the shape of the control does not change as it fills.
/// Redraws when the bound collection changes; draws directly (no child controls, nothing allocated per frame).
/// </summary>
public sealed class Waveform : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> LevelsProperty =
        AvaloniaProperty.Register<Waveform, IReadOnlyList<double>?>(nameof(Levels));
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<Waveform, IBrush?>(nameof(Foreground));
    public static readonly StyledProperty<int> SlotsProperty = AvaloniaProperty.Register<Waveform, int>(nameof(Slots), 32);
    public static readonly StyledProperty<double> BarWidthProperty = AvaloniaProperty.Register<Waveform, double>(nameof(BarWidth), 3);
    public static readonly StyledProperty<double> BarGapProperty = AvaloniaProperty.Register<Waveform, double>(nameof(BarGap), 3);
    public static readonly StyledProperty<double> MinBarHeightProperty = AvaloniaProperty.Register<Waveform, double>(nameof(MinBarHeight), 4);
    public static readonly StyledProperty<double> MaxBarHeightProperty = AvaloniaProperty.Register<Waveform, double>(nameof(MaxBarHeight), 28);
    /// <summary>Opacity of the dots in slots that have no level yet.</summary>
    public static readonly StyledProperty<double> EmptySlotOpacityProperty = AvaloniaProperty.Register<Waveform, double>(nameof(EmptySlotOpacity), 0.3);

    private INotifyCollectionChanged? _subscribed;

    static Waveform()
    {
        AffectsRender<Waveform>(LevelsProperty, ForegroundProperty, BarWidthProperty, BarGapProperty, MinBarHeightProperty, MaxBarHeightProperty, EmptySlotOpacityProperty);
        AffectsMeasure<Waveform>(SlotsProperty, BarWidthProperty, BarGapProperty, MaxBarHeightProperty);
    }

    public IReadOnlyList<double>? Levels { get => GetValue(LevelsProperty); set => SetValue(LevelsProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>How many bars the control is wide (the levels beyond it, oldest first, are not drawn).</summary>
    public int Slots { get => GetValue(SlotsProperty); set => SetValue(SlotsProperty, value); }
    public double BarWidth { get => GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    public double BarGap { get => GetValue(BarGapProperty); set => SetValue(BarGapProperty, value); }
    public double MinBarHeight { get => GetValue(MinBarHeightProperty); set => SetValue(MinBarHeightProperty, value); }
    public double MaxBarHeight { get => GetValue(MaxBarHeightProperty); set => SetValue(MaxBarHeightProperty, value); }
    public double EmptySlotOpacity { get => GetValue(EmptySlotOpacityProperty); set => SetValue(EmptySlotOpacityProperty, value); }

    /// <summary>Bar height for a level: <see cref="MinBarHeight"/> at 0 up to <see cref="MaxBarHeight"/> at 1.</summary>
    public double BarHeight(double level) => MinBarHeight + Math.Clamp(double.IsFinite(level) ? level : 0, 0, 1) * (MaxBarHeight - MinBarHeight);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelsProperty && VisualRoot is not null) Subscribe();
    }

    // Subscribed only while in the visual tree: the view model outlives this control.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unsubscribe();
    }

    private void Subscribe()
    {
        Unsubscribe();
        if (Levels is INotifyCollectionChanged source)
        {
            source.CollectionChanged += OnLevelsChanged;
            _subscribed = source;
        }
        InvalidateVisual();
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null) _subscribed.CollectionChanged -= OnLevelsChanged;
        _subscribed = null;
    }

    private void OnLevelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsEffectivelyVisible) InvalidateVisual();
    }

    private double Pitch => BarWidth + BarGap;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Max(0, Slots * Pitch - BarGap);
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), MaxBarHeight);
    }

    public override void Render(DrawingContext context)
    {
        if (Foreground is not { } brush) return;
        var bounds = Bounds;
        var slots = Math.Min(Slots, (int)Math.Floor((bounds.Width + BarGap) / Pitch));
        if (slots <= 0) return;
        var levels = Levels;
        var count = levels?.Count ?? 0;
        var radius = BarWidth / 2;
        // Right-aligned inside the bounds, newest level in the rightmost slot.
        var right = bounds.Width;
        for (var i = 0; i < slots; i++)
        {
            var x = right - BarWidth - i * Pitch;
            var hasLevel = i < count;
            var h = hasLevel ? BarHeight(levels![count - 1 - i]) : MinBarHeight;
            var rect = new Rect(x, (bounds.Height - h) / 2, BarWidth, h);
            if (hasLevel) context.DrawRectangle(brush, null, new RoundedRect(rect, radius));
            else
            {
                using (context.PushOpacity(EmptySlotOpacity))
                    context.DrawRectangle(brush, null, new RoundedRect(rect, radius));
            }
        }
    }
}

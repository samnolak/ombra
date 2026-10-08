using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App.Pets;

namespace OmpGui.App.Controls;

/// <summary>
/// Draws a pixel pet alive: <see cref="PetLife"/> says what it does at each moment (breathing, blinks, glances, idle
/// actions, its mood's motion, reactions to the pointer, a click and typing) and <see cref="PetFrames"/> renders that at
/// the screen's own pixels, nearest-neighbour, so pet pixels stay crisp squares (at a scale that is not a whole number
/// of screen pixels it renders at the next whole one and lets the screen scale it down smoothly; moved to a screen of
/// another density, it renders again for that one). A one-shot timer wakes only when the picture changes next, and only
/// while the pet is on screen in the active window: hidden, detached or in a background window it shows a still and no
/// timer runs; a mood that lasts (asleep, a question nobody answers) comes to rest on a still too. On the desktop, in a
/// window of its own that is never the active one, <see cref="InForeground"/> says whether the app is in front. An
/// <see cref="Image"/>, so layout checks count it as the content of the button it sits in.
/// </summary>
public sealed class PetView : Image
{
    public static readonly StyledProperty<PetLook?> LookProperty = AvaloniaProperty.Register<PetView, PetLook?>(nameof(Look));
    public static readonly StyledProperty<PetMood> MoodProperty = AvaloniaProperty.Register<PetView, PetMood>(nameof(Mood));
    public static readonly StyledProperty<double> PixelScaleProperty = AvaloniaProperty.Register<PetView, double>(nameof(PixelScale), 2);
    public static readonly StyledProperty<bool> AnimateProperty = AvaloniaProperty.Register<PetView, bool>(nameof(Animate), true);
    public static readonly StyledProperty<bool> BodyOnlyProperty = AvaloniaProperty.Register<PetView, bool>(nameof(BodyOnly));
    public static readonly StyledProperty<int> AttentionProperty = AvaloniaProperty.Register<PetView, int>(nameof(Attention));
    public static readonly StyledProperty<bool?> InForegroundProperty = AvaloniaProperty.Register<PetView, bool?>(nameof(InForeground));

    private const int MaxCached = 64;

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly Dictionary<string, WriteableBitmap> _bitmaps = [];
    private PetLife _life;
    private string? _key;
    /// <summary>Draws the picture on screen.</summary>
    private readonly FrameView _view = new();
    private TopLevel? _top;
    private Window? _window;
    private bool _attached;
    private Visual[] _ancestors = [];

    public PetView()
    {
        Focusable = false;
        VisualChildren.Add(_view);
        _life = new PetLife(_now());
        _life.Begin(PetMood.Idle, _now());
        _timer.Tick += (_, _) => Tick();
    }

    public PetLook? Look { get => GetValue(LookProperty); set => SetValue(LookProperty, value); }
    public PetMood Mood { get => GetValue(MoodProperty); set => SetValue(MoodProperty, value); }
    /// <summary>Screen pixels per pet pixel (4/3 = small, 2 = medium; 32 and 48 px tall).</summary>
    public double PixelScale { get => GetValue(PixelScaleProperty); set => SetValue(PixelScaleProperty, value); }
    /// <summary>False: a still (e.g. many small previews).</summary>
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
    /// <summary>Only the 24×24 body, without the room for effects on its left (galleries, pickers: centred).</summary>
    public bool BodyOnly { get => GetValue(BodyOnlyProperty); set => SetValue(BodyOnlyProperty, value); }
    /// <summary>Changes when the user types: the pet glances at the message box.</summary>
    public int Attention { get => GetValue(AttentionProperty); set => SetValue(AttentionProperty, value); }
    /// <summary>
    /// Null (in the app's window): the pet moves while its window is active. On the desktop its window sets whether
    /// the app is in front: then it moves; behind other apps it moves only while omp is busy, asks or reports
    /// (what it is there for, at a glance) and rests on a still when idle or asleep.
    /// </summary>
    public bool? InForeground { get => GetValue(InForegroundProperty); set => SetValue(InForegroundProperty, value); }

    private int SourceWidth => BodyOnly ? PetArt.BodySize : PetFrames.Width;

    private Func<DateTimeOffset> _now = () => DateTimeOffset.UtcNow;

    /// <summary>The clock. Tests set their own (and a seed): the pet starts over, settled in its mood.</summary>
    internal Func<DateTimeOffset> Now => _now;

    internal void UseClock(Func<DateTimeOffset> now, int? seed = null)
    {
        _now = now;
        _life = new PetLife(now(), seed);
        _life.Begin(Mood, now());
        Restart();
    }

    /// <summary>True while the animation timer runs (tests: no timer when hidden, in the background or at rest).</summary>
    internal bool IsPlaying => _timer.IsEnabled;

    /// <summary>What is on screen (tests): equal keys, equal pixels.</summary>
    internal string? FrameKey => _key;

    /// <summary>The animation scheduler (tests).</summary>
    internal PetLife Life => _life;

    /// <summary>Does now what the timer would do when it is due (tests).</summary>
    internal void Step()
    {
        if (_timer.IsEnabled) Tick();
    }

    /// <summary>A happy little hop (the pet was clicked).</summary>
    public void Cheer()
    {
        _life.Pat(Now());
        Restart();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LookProperty) Restart();
        else if (change.Property == MoodProperty)
        {
            _life.SetMood(Mood, Now());
            Restart();
        }
        else if (change.Property == AttentionProperty)
        {
            _life.Attend(Now());
            Restart();
        }
        else if (change.Property == IsVisibleProperty) UpdateTimer();
        else if (change.Property == PixelScaleProperty || change.Property == BodyOnlyProperty) { InvalidateMeasure(); Redraw(); }
        else if (change.Property == AnimateProperty || change.Property == InForegroundProperty) Restart();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(SourceWidth * Math.Max(0.5, PixelScale), PetFrames.Height * Math.Max(0.5, PixelScale));
        _view.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = new Size(SourceWidth * Math.Max(0.5, PixelScale), PetFrames.Height * Math.Max(0.5, PixelScale));
        _view.Arrange(new Rect(size));
        return size;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _top = TopLevel.GetTopLevel(this);
        _window = _top as Window;
        // Moved to a screen of another density (the desktop pet crossing from a Retina screen to a 1× one)
        if (_top is not null) _top.ScalingChanged += OnScalingChanged;
        // Any ancestor hidden (the settings page closing, the pet switched off) or the window going to the background
        // (or minimised) stops the timer; shown or active again restarts it. The window is one of the ancestors.
        _ancestors = [.. this.GetVisualAncestors()];
        foreach (var a in _ancestors) a.PropertyChanged += OnAncestorChanged;
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        if (_top is not null) _top.ScalingChanged -= OnScalingChanged;
        _top = null;
        _window = null;
        foreach (var a in _ancestors) a.PropertyChanged -= OnAncestorChanged;
        _ancestors = [];
        UpdateTimer();
    }

    /// <summary>Shown and laid out (a window opening becomes visible after its content is attached): time to move.</summary>
    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Restart();
    }

    private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // After the change has reached this control
        if (e.Property == IsVisibleProperty || e.Property == WindowBase.IsActiveProperty || e.Property == Window.WindowStateProperty)
            Dispatcher.UIThread.Post(Restart, DispatcherPriority.Background);
    }

    private void OnScalingChanged(object? sender, EventArgs e) => Redraw();

    // ───────────────────────────── The pointer ─────────────────────────────

    /// <summary>Where the pointer is, in pet pixels from the pet's face (its first eye's row, the body's middle).</summary>
    private (double X, double Y) FromFace(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var scale = Math.Max(0.5, PixelScale);
        var eyeY = Look?.Body.Eyes[0].Y ?? 10;
        return (p.X / scale - (BodyOnly ? PetArt.BodySize / 2.0 : PetFrames.BodyX + PetArt.BodySize / 2.0), p.Y / scale - eyeY);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (!Animate) return;
        var (x, y) = FromFace(e);
        _life.Hover(true, x, y, Now());
        Restart();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!Animate) return;
        var (x, y) = FromFace(e);
        _life.Hover(true, x, y, Now());
        if (_timer.IsEnabled) Redraw();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _life.Hover(false, 0, 0, Now());
        if (_timer.IsEnabled) Redraw();
    }

    // ───────────────────────────── Timer and drawing ─────────────────────────────

    private bool Visible => _attached && IsEffectivelyVisible && _window?.WindowState != WindowState.Minimized && InForeground switch
    {
        null => _window is not { IsActive: false },
        true => true,
        false => Mood is not (PetMood.Idle or PetMood.Sleeping),
    };

    private bool ShouldAnimate(DateTimeOffset now) => Animate && Look is not null && Visible && !_life.IsResting(now);

    private void Restart()
    {
        _timer.Stop();
        Redraw();
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        var now = Now();
        if (ShouldAnimate(now))
        {
            if (_timer.IsEnabled) return;
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Start();
        }
        else if (_timer.IsEnabled)
        {
            _timer.Stop();
            Redraw();
        }
    }

    private void Tick()
    {
        _timer.Stop();
        var now = Now();
        if (!ShouldAnimate(now) || Look is not { } look)
        {
            Redraw();
            return;
        }
        _life.Plan(now);
        Redraw();
        var next = _life.NextChange(look.Body, now, RenderScale().Scale);
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(16, (next - now).TotalMilliseconds));
        _timer.Start();
    }

    /// <summary>Output pixels per pet pixel, and whether the screen scales the bitmap (a scale between whole screen pixels).</summary>
    private (double Scale, bool Smooth) RenderScale()
    {
        var device = Math.Max(0.5, PixelScale) * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        var whole = Math.Round(device);
        return Math.Abs(device - whole) < 0.01 ? (Math.Max(1, whole), false) : (Math.Ceiling(device), true);
    }

    private void Redraw()
    {
        if (Look is not { } look)
        {
            _view.Show(null, BitmapInterpolationMode.None);
            _key = null;
            return;
        }
        var now = Now();
        var pose = ShouldAnimate(now) ? _life.Sample(look.Body, now) : PetLife.Still(look.Body, Mood);
        var (scale, smooth) = RenderScale();
        var key = $"{look.Key}|{(BodyOnly ? 1 : 0)}|{scale}|{PetFrames.Key(pose, scale)}";
        if (key == _key) return;
        _key = key;
        if (!_bitmaps.TryGetValue(key, out var bmp))
        {
            if (_bitmaps.Count >= MaxCached) _bitmaps.Clear();
            bmp = ToBitmap(PetFrames.Render(look, pose, scale, BodyOnly));
            _bitmaps[key] = bmp;
        }
        _view.Show(bmp, smooth ? BitmapInterpolationMode.MediumQuality : BitmapInterpolationMode.None);
    }

    /// <summary>
    /// Shows the frames, stretched over the pet's box. A child of its own rather than <see cref="Image.Source"/>: a new
    /// source re-measures the image, so every frame would lay out the window around it (and raise its LayoutUpdated);
    /// a new frame here only repaints. What it draws takes the pointer for the pet (the events bubble up to it).
    /// </summary>
    private sealed class FrameView : Control
    {
        private WriteableBitmap? _frame;

        public void Show(WriteableBitmap? frame, BitmapInterpolationMode interpolation)
        {
            _frame = frame;
            RenderOptions.SetBitmapInterpolationMode(this, interpolation);
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (_frame is { } frame && Bounds.Width > 0 && Bounds.Height > 0)
                context.DrawImage(frame, new Rect(frame.Size), new Rect(Bounds.Size));
        }
    }

    private static WriteableBitmap ToBitmap(PetFrame f)
    {
        var bmp = new WriteableBitmap(new PixelSize(f.Width, f.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        // Premultiplied BGRA, little-endian: 0xAARRGGBB with the colour scaled by alpha
        var row = new int[f.Width];
        for (var y = 0; y < f.Height; y++)
        {
            for (var x = 0; x < f.Width; x++)
            {
                var c = f.Pixels[y * f.Width + x];
                var a = c >> 24;
                if (a is not (0 or 255))
                    c = a << 24 | (((c >> 16) & 0xFF) * a / 255) << 16 | (((c >> 8) & 0xFF) * a / 255) << 8 | (c & 0xFF) * a / 255;
                row[x] = unchecked((int)c);
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, f.Width);
        }
        return bmp;
    }
}

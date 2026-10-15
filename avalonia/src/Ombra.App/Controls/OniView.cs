using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Pets;
using Ombra.App.Pets.Oni;

namespace Ombra.App.Controls;

/// <summary>
/// ONI-01 on screen (<see cref="OniPet"/> says what it does; <see cref="OniPainter"/> draws it). Its box is the pet:
/// <see cref="BoxFor"/> wide and tall, the feet on the box's bottom edge, and only the box takes the pointer; the spirits,
/// the seal and the sign over its head are drawn around it (<see cref="OverflowFor"/>). Frames come from the screen's
/// own clock (<see cref="TopLevel.RequestAnimationFrame"/>) and only while there is something to move: on screen in the
/// active window (on the desktop: while the app is in front, or behind it while omp works, asks or reports), not asleep
/// or waiting for long, and not with reduced motion; otherwise it shows the pose's still and asks for no frames. Back
/// on screen it goes on from where the pet is now (its time ran on; the drawing moves at most 0.1 s at once). The
/// sheets are shared by every view of the same size (<see cref="OniAtlases"/>) and let go when the view is unloaded.
/// </summary>
public sealed class OniView : Control, ICustomHitTest
{
    public static readonly StyledProperty<OniPet?> PetProperty = AvaloniaProperty.Register<OniView, OniPet?>(nameof(Pet));
    public static readonly StyledProperty<double> HeightPxProperty = AvaloniaProperty.Register<OniView, double>(nameof(HeightPx), 80);
    public static readonly StyledProperty<bool> ReducedProperty = AvaloniaProperty.Register<OniView, bool>(nameof(Reduced));
    public static readonly StyledProperty<bool?> InForegroundProperty = AvaloniaProperty.Register<OniView, bool?>(nameof(InForeground));
    public static readonly StyledProperty<bool> SuspendedProperty = AvaloniaProperty.Register<OniView, bool>(nameof(Suspended));

    /// <summary>Asleep this long (seconds), the pet rests on its still; a question or a fault this long goes on at a calmer frame rate.</summary>
    public const double SleepRest = 30, AlertSlow = 120;

    /// <summary>Frames closer than this are skipped (the screen's next refresh is taken instead).</summary>
    private const double MinFrameMs = 11;

    private static readonly string[] Keys = ["x", "y", "scale", "alpha", "depth", "lean", "tempo"];

    private OniAtlases? _atlases;
    private OniPet? _subscribed;
    private TopLevel? _top;
    private Window? _window;
    private Visual[] _ancestors = [];
    private bool _attached, _frameRequested, _live;
    private TimeSpan? _lastFrame;
    private OniCarry? _carry;

    // The spirits follow where their pose wants them on springs (interaction-preview.js stageStep)
    private readonly double[][] _spirit = [new double[7], new double[7]];
    private readonly double[][] _spiritV = [new double[7], new double[7]];
    private readonly double[] _fire = new double[2];
    private bool _spiritsPlaced;
    private double _stageTime;

    public OniView()
    {
        Focusable = false;
        ClipToBounds = false;
    }

    public OniPet? Pet { get => GetValue(PetProperty); set => SetValue(PetProperty, value); }
    /// <summary>The pet's height in logical pixels (64, 80 or 96).</summary>
    public double HeightPx { get => GetValue(HeightPxProperty); set => SetValue(HeightPxProperty, value); }
    /// <summary>Reduced motion: the pose's still drawing, effects at rest, no frames.</summary>
    public bool Reduced { get => GetValue(ReducedProperty); set => SetValue(ReducedProperty, value); }
    /// <summary>Null in the app's window (moves while it is active). On the desktop: whether the app is in front.</summary>
    public bool? InForeground { get => GetValue(InForegroundProperty); set => SetValue(InForegroundProperty, value); }
    /// <summary>Its host hid it (the perch's pet while it is on the desktop): no frames.</summary>
    public bool Suspended { get => GetValue(SuspendedProperty); set => SetValue(SuspendedProperty, value); }

    /// <summary>The pet being carried: stepped with each frame; its lean and landing are drawn.</summary>
    public OniCarry? Carry
    {
        get => _carry;
        set
        {
            _carry = value;
            Pump();
        }
    }

    /// <summary>The box a pet <paramref name="height"/> tall takes: what takes the pointer and what layout places.</summary>
    public static Size BoxFor(double height) => new(Math.Round(height * 1.3), Math.Round(height * 1.1));

    /// <summary>Drawn around the box: the spirits, the seal, the sign over the head, the dust.</summary>
    public static Thickness OverflowFor(double height) => new(Math.Ceiling(height * .3), Math.Ceiling(height * .42), Math.Ceiling(height * .3), Math.Ceiling(height * .2));

    /// <summary>The point between the feet, in the view.</summary>
    public Point Feet => new(BoxFor(HeightPx).Width / 2, BoxFor(HeightPx).Height);

    /// <summary>True while frames are asked for (tests: none when hidden, in the background, at rest or reduced).</summary>
    internal bool IsPlaying => _frameRequested;

    /// <summary>Moving (frames come), not showing the pose's still.</summary>
    internal bool IsLive => _live && !Reduced;

    /// <summary>The sheets this view draws from (tests, measurements).</summary>
    internal OniAtlases? Atlases => _atlases;

    /// <summary>What the last picture showed (tests).</summary>
    internal OniScene? LastScene { get; private set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PetProperty)
        {
            Subscribe();
            _spiritsPlaced = false;
            Refresh();
        }
        else if (change.Property == HeightPxProperty)
        {
            InvalidateMeasure();
            Acquire();
            Refresh();
        }
        else if (change.Property == ReducedProperty || change.Property == InForegroundProperty || change.Property == SuspendedProperty
                 || change.Property == IsVisibleProperty)
            Refresh();
    }

    protected override Size MeasureOverride(Size availableSize) => BoxFor(HeightPx);

    // The box whatever room it is given: the pet's size is its own, not its slot's
    protected override Size ArrangeOverride(Size finalSize) => BoxFor(HeightPx);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _top = TopLevel.GetTopLevel(this);
        _window = _top as Window;
        if (_top is not null) _top.ScalingChanged += OnScalingChanged;
        _ancestors = [.. this.GetVisualAncestors()];
        foreach (var a in _ancestors) a.PropertyChanged += OnAncestorChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        Subscribe();
        Acquire();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        if (_top is not null) _top.ScalingChanged -= OnScalingChanged;
        foreach (var a in _ancestors) a.PropertyChanged -= OnAncestorChanged;
        _ancestors = [];
        _top = null;
        _window = null;
        ActualThemeVariantChanged -= OnThemeChanged;
        Subscribe();
        if (_atlases is { } atlases)
        {
            atlases.Ready -= OnAtlasesReady;
            atlases.Release();
            _atlases = null;
        }
        _lastFrame = null;
        if (_slow is { IsEnabled: true })
        {
            _slow.Stop();
            _frameRequested = false;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        Acquire();
        InvalidateVisual();
    }

    private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == WindowBase.IsActiveProperty || e.Property == Window.WindowStateProperty)
            Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    private void Subscribe()
    {
        var want = _attached ? Pet : null;
        if (ReferenceEquals(want, _subscribed)) return;
        if (_subscribed is not null) _subscribed.Changed -= OnPetChanged;
        _subscribed = want;
        if (want is not null) want.Changed += OnPetChanged;
    }

    private void OnPetChanged() => Refresh();

    /// <summary>The sheets for this height at this screen's density (another density or size: other sheets).</summary>
    private void Acquire()
    {
        if (!_attached || Pet is not { } pet) return;
        var device = HeightPx * (_top?.RenderScaling ?? 1);
        if (_atlases is { } held && held.Level == OniAtlases.LevelFor(device) && held.Spec == pet.Spec) return;
        var next = OniAtlases.Acquire(pet.Spec, device, OpenAsset);
        if (_atlases is { } old)
        {
            old.Ready -= OnAtlasesReady;
            old.Release();
        }
        _atlases = next;
        if (!next.IsReady) next.Ready += OnAtlasesReady;
    }

    private static Stream OpenAsset(string path) => AssetLoader.Open(new Uri(OniSpec.Root + path));

    private void OnAtlasesReady()
    {
        if (_atlases is { } a) a.Ready -= OnAtlasesReady;
        Refresh();
    }

    // ───────────────────────────── Frames ─────────────────────────────

    private bool OnScreen => _attached && !Suspended && IsEffectivelyVisible && _window?.WindowState != WindowState.Minimized;

    /// <summary>Asleep for a while: the sleeping pose's still (the spirits hidden, the z's at rest).</summary>
    private static bool Resting(OniDirector d) => d.Cue == "sleeping" && d.CueAge > SleepRest;

    private bool ShouldAnimate()
    {
        if (!OnScreen || Reduced || Pet is not { } pet || _atlases is not { IsReady: true }) return false;
        if (_carry is { Moving: true }) return true;
        var d = pet.Director;
        var foreground = InForeground switch
        {
            null => _window is not { IsActive: false },
            true => true,
            false => d.Carried || pet.Mood is not (PetMood.Idle or PetMood.Sleeping),
        };
        if (!foreground) return false;
        // Still settling into the pose: the transition plays out before it rests
        return !Resting(d) || d.Mode != "loop" || d.State != d.Cue;
    }

    /// <summary>
    /// Seconds from one frame to the next. Every frame costs a redraw of the whole window on macOS, so the pet moves at
    /// 30 frames a second, as the reference's recordings do (its drawings are held for tens of milliseconds; the
    /// spirits move slowly); carried, it follows the pointer at up to 90; a question or a fault nobody has answered
    /// for a while goes on at 15.
    /// </summary>
    private double FrameInterval(OniDirector d)
    {
        if (_carry is { Moving: true }) return MinFrameMs / 1000;
        if (d.Mode == "loop" && d.Cue is "waiting" or "error" && d.CueAge > AlertSlow) return 1 / 15.0;
        return 1 / 30.0;
    }

    /// <summary>Something changed: a new picture, and frames if it should move.</summary>
    private void Refresh()
    {
        InvalidateVisual();
        Pump();
    }

    private void Pump()
    {
        if (_frameRequested || _top is null) return;
        if (!ShouldAnimate())
        {
            Stop();
            return;
        }
        _frameRequested = true;
        // Asleep on a timer for most of the wait, then frame by frame of the screen until it is time: the frames stay
        // on the screen's refreshes, evenly spaced (every 5th at 160 Hz, every 4th at 120, every 2nd at 60)
        var sleep = _lastFrame is null ? 0 : FrameInterval(Pet!.Director) - 3.5 * _refresh;
        if (sleep < .004)
        {
            _top.RequestAnimationFrame(OnFrame);
            return;
        }
        _slow ??= new DispatcherTimer(DispatcherPriority.Render);
        _slow.Interval = TimeSpan.FromSeconds(sleep);
        _slow.Tick -= OnSlowTick;
        _slow.Tick += OnSlowTick;
        _slow.Start();
    }

    private DispatcherTimer? _slow;
    /// <summary>The screen's refresh interval, as its frames come (seconds).</summary>
    private double _refresh = 1 / 60.0;
    private TimeSpan? _lastCallback;

    private void OnSlowTick(object? sender, EventArgs e)
    {
        _slow?.Stop();
        if (_top is { } top) top.RequestAnimationFrame(OnFrame);
        else _frameRequested = false;
    }

    /// <summary>No more frames: the still is shown, and the next frame starts from a fresh clock.</summary>
    private void Stop()
    {
        _lastFrame = null;
        _lastCallback = null;
        _live = false;
    }

    private void OnFrame(TimeSpan at)
    {
        _frameRequested = false;
        // Two callbacks in a row a refresh apart tell the screen's rate
        if (_lastCallback is { } before && (at - before).TotalSeconds is > .002 and < .04 and var gap)
            _refresh = Math.Max(1 / 250.0, Math.Min(gap, _refresh * .9 + gap * .1));
        _lastCallback = at;
        if (!ShouldAnimate())
        {
            Stop();
            InvalidateVisual();
            return;
        }
        if (_lastFrame is { } prev && Pet is { } p && (at - prev).TotalSeconds < FrameInterval(p.Director) - _refresh / 2 && _top is { } top)
        {
            _frameRequested = true;
            top.RequestAnimationFrame(OnFrame);
            return;
        }
        var dt = _lastFrame is { } last ? (at - last).TotalSeconds : 1 / 60.0;
        _lastFrame = at;
        Advance(Math.Clamp(dt, 0, 1));
        InvalidateVisual();
        Pump();
    }

    /// <summary>One frame's worth of time: the carried body, the director, the spirits.</summary>
    internal void Advance(double dt)
    {
        if (Pet is not { } pet) return;
        _live = true;
        _carry?.Step(dt);
        pet.Sync();
        StepSpirits(Math.Min(dt, OniDirector.VisualDeltaLimit), pet.Director);
    }

    private void StepSpirits(double dt, OniDirector d)
    {
        _stageTime += dt;
        var id = d.Cue;
        for (var i = 0; i < 2; i++)
        {
            var t = id == "sleeping" ? d.CueAge : _stageTime;
            var target = OniEffects.SpiritPose(id, t, i);
            if (id is not ("sleeping" or "dragging"))
            {
                var focus = Math.Max(d.Attention, d.Hover ? .25 : 0);
                target.X *= 1 - focus * .22;
                target.Y -= focus * .035;
            }
            ReadOnlySpan<double> want = [target.X, target.Y, target.Scale, target.Alpha, target.Depth, target.Lean, target.Tempo];
            var v = _spirit[i];
            var vv = _spiritV[i];
            if (!_spiritsPlaced)
            {
                want.CopyTo(v);
                Array.Clear(vv);
            }
            else
            {
                var w = id == "dragging" ? 30 : 14;
                for (var k = 0; k < Keys.Length; k++) (v[k], vv[k]) = OniMotion.Spring(v[k], vv[k], want[k], w, dt);
            }
            _fire[i] += dt * v[6];
        }
        _spiritsPlaced = true;
    }

    private static OniSpiritPose Pose(double[] v) => new() { X = v[0], Y = v[1], Scale = v[2], Alpha = v[3], Depth = v[4], Lean = v[5], Tempo = v[6] };

    // ───────────────────────────── Drawing ─────────────────────────────

    public override void Render(DrawingContext context)
    {
        if (Pet is not { } pet || _atlases is not { IsReady: true } atlases) return;
        var d = pet.Director;
        var h = HeightPx;
        var feet = Feet;
        var still = !IsLive;
        if (!_spiritsPlaced) StepSpirits(0, d);
        var carry = _carry;
        var scene = new OniScene
        {
            H = h,
            X = feet.X,
            Y = feet.Y,
            Angle = carry?.Angle ?? 0,
            Light = ActualThemeVariant == ThemeVariant.Light,
            Still = still,
            State = d.State,
            Mode = d.Mode,
            Phase = d.Phase,
            Frame = d.Frame,
            Cue = d.Cue,
            CueSeconds = d.CueAge,
            Spirit0 = Pose(_spirit[0]),
            Spirit1 = Pose(_spirit[1]),
            Fire0 = _fire[0],
            Fire1 = _fire[1],
            DustAge = carry?.DustAge ?? double.PositiveInfinity,
            DustDx = carry?.DustDx ?? 0,
            GroundDy = carry?.GroundDy ?? 0,
        };
        LastScene = scene;
        var bounds = new Rect(Bounds.Size).Inflate(OverflowFor(h));
        context.Custom(new DrawOp(bounds, scene, atlases));
    }

    /// <summary>The pet's box takes the pointer; what is drawn around it does not.</summary>
    public bool HitTest(Point point)
    {
        var box = BoxFor(HeightPx);
        return point.X >= 0 && point.X <= box.Width && point.Y >= 0 && point.Y <= box.Height + 2;
    }

    // ───────────────────────────── The pointer ─────────────────────────────

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        Pet?.Hover(true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Pet?.Hover(false);
    }

    private sealed class DrawOp(Rect bounds, OniScene scene, OniAtlases atlases) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => other is DrawOp o && o.Bounds == Bounds && o._scene.Equals(_scene) && ReferenceEquals(o._atlases, _atlases);

        private readonly OniScene _scene = scene;
        private readonly OniAtlases _atlases = atlases;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            OniPainter.Paint(canvas, _scene, _atlases);
            canvas.Restore();
        }

        public void Dispose()
        {
        }
    }
}

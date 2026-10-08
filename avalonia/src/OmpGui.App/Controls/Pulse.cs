using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OmpGui.App.Controls;

/// <summary>
/// The slow "omp is working" pulse (the running dots, the live "Thinking" label), set from a style:
/// <c>&lt;Setter Property="ctl:Pulse.Low" Value="0.3" /&gt;</c> fades the element's opacity from 1 to 0.3 and back,
/// sine-eased, <see cref="DurationProperty"/> each way (by default 1.1 s), for as long as the style applies.
/// <para>
/// Not a style animation: Avalonia ticks those every vsync, 160 times a second on a fast display, and on macOS a frame
/// costs about the same whatever changed in it — Avalonia.Native's OpenGL target renders every frame into a new
/// window-sized IOSurface and copies the whole window into it, about 4 ms of CPU — so one 8 px dot kept most of a core
/// busy while omp worked. Here every pulse steps together, in phase, and only when its opacity visibly moves: in the
/// frames the window draws anyway (after a layout pass, as while a reply streams in), at most <see cref="MaxFps"/> times
/// a second, and on a timer of its own only when nothing else has drawn for 1/<see cref="OwnFps"/> s (a fade this slow
/// looks the same). A pulse that is hidden or out of the window costs nothing, and nothing ticks when none is shown.
/// </para>
/// </summary>
public static class Pulse
{
    /// <summary>Steps per second at most, in frames the window draws anyway.</summary>
    public const int MaxFps = 30;

    /// <summary>Steps per second when the pulse is all that changes (each step then costs a frame of its own).</summary>
    public const int OwnFps = 20;

    /// <summary>The opacity is drawn in steps of this size: a smaller change is not a new frame.</summary>
    private const double Quantum = 0.01;

    /// <summary>The lowest opacity of the pulse; 1 (the default) is no pulse.</summary>
    public static readonly AttachedProperty<double> LowProperty =
        AvaloniaProperty.RegisterAttached<Visual, double>("Low", typeof(Pulse), 1.0);

    /// <summary>How long the fade takes each way (1 → <see cref="LowProperty"/>, and back).</summary>
    public static readonly AttachedProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.RegisterAttached<Visual, TimeSpan>("Duration", typeof(Pulse), TimeSpan.FromSeconds(1.1));

    /// <summary>The elements styled to pulse that are in a window (shown or not): few, so each visibility change anywhere
    /// looks at all of them.</summary>
    private static readonly HashSet<Visual> InTree = [];
    private static readonly Dictionary<Visual, Pulsing> Shown = [];
    /// <summary>The windows of the shown pulses, whose layout passes they step in.</summary>
    private static readonly HashSet<TopLevel> Windows = [];
    private static readonly long Epoch = Stopwatch.GetTimestamp();
    private static DispatcherTimer? _timer;
    private static TimeSpan _lastStep;

    static Pulse()
    {
        LowProperty.Changed.AddClassHandler<Visual>((v, e) =>
        {
            var was = e.OldValue is double old && old < 1;
            var now = GetLow(v) < 1;
            if (now && !was)
            {
                v.AttachedToVisualTree += OnAttached;
                v.DetachedFromVisualTree += OnDetached;
            }
            else if (was && !now)
            {
                v.AttachedToVisualTree -= OnAttached;
                v.DetachedFromVisualTree -= OnDetached;
            }
            Update(v, restart: true);
        });
        DurationProperty.Changed.AddClassHandler<Visual>((v, _) => Update(v, restart: true));
        // Shown or hidden: Avalonia has no public event for IsEffectivelyVisible, but it follows IsVisible on the element
        // and its ancestors, and is already updated when the change is announced. Show only starts or stops pulses (the
        // set of elements in the window stays as it is), so it walks InTree as it is.
        Visual.IsVisibleProperty.Changed.AddClassHandler<Visual>((_, _) =>
        {
            foreach (var v in InTree) Show(v, v.IsEffectivelyVisible, restart: false);
        });
    }

    public static double GetLow(Visual element) => element.GetValue(LowProperty);
    public static void SetLow(Visual element, double value) => element.SetValue(LowProperty, value);
    public static TimeSpan GetDuration(Visual element) => element.GetValue(DurationProperty);
    public static void SetDuration(Visual element, TimeSpan value) => element.SetValue(DurationProperty, value);

    /// <summary>How many pulses are running now (tests).</summary>
    internal static int Running => Shown.Count;

    /// <summary>Whether <paramref name="element"/> pulses now (tests).</summary>
    internal static bool IsPulsing(Visual element) => Shown.ContainsKey(element);

    /// <summary>Whether the shared timer ticks (tests: it stops with the last pulse).</summary>
    internal static bool IsTicking => _timer?.IsEnabled == true;

    /// <summary>The opacity of a pulse <paramref name="elapsed"/> into the shared cycle: 1 at the start of each cycle.</summary>
    internal static double OpacityAt(TimeSpan elapsed, double low, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return low;
        // A sine-eased fade there and back: Alternate playback of SineEaseInOut from 1 to low, as a single cosine
        var fade = (1 - Math.Cos(Math.PI * elapsed.TotalSeconds / duration.TotalSeconds)) / 2;
        return Math.Round((1 - (1 - low) * fade) / Quantum) * Quantum;
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Update((Visual)sender!, restart: false);

    // Gone from the window: whatever the tree says while it is being taken apart
    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Update((Visual)sender!, restart: false, detached: true);

    /// <summary>
    /// Starts or stops the pulse of <paramref name="v"/> after its style or tree changed; <paramref name="restart"/>: its
    /// low or duration changed, a running pulse takes the new ones.
    /// </summary>
    private static void Update(Visual v, bool restart, bool detached = false)
    {
        var inTree = GetLow(v) < 1 && !detached && v.IsAttachedToVisualTree();
        if (inTree) InTree.Add(v);
        else InTree.Remove(v);
        Show(v, inTree && v.IsEffectivelyVisible, restart);
    }

    private static void Show(Visual v, bool run, bool restart)
    {
        if (run && !restart && Shown.ContainsKey(v)) return;
        if (Shown.Remove(v, out var old))
        {
            old.Dispose();
            if (old.Top is { } was && !HasPulses(was) && Windows.Remove(was)) was.LayoutUpdated -= OnLayoutUpdated;
        }
        if (!run)
        {
            if (Shown.Count == 0) _timer?.Stop();
            return;
        }
        var top = TopLevel.GetTopLevel(v);
        var pulsing = new Pulsing(GetLow(v), GetDuration(v), top);
        pulsing.Step(Now());
        pulsing.Binding = v.Bind(Visual.OpacityProperty, pulsing, BindingPriority.Animation);
        Shown[v] = pulsing;
        if (top is not null && Windows.Add(top)) top.LayoutUpdated += OnLayoutUpdated;
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(1.0 / OwnFps), DispatcherPriority.Normal, (_, _) => StepAll(Now()));
        _timer.Start();
    }

    private static bool HasPulses(TopLevel top)
    {
        foreach (var p in Shown.Values)
            if (p.Top == top) return true;
        return false;
    }

    private static TimeSpan Now() => Stopwatch.GetElapsedTime(Epoch);

    /// <summary>
    /// A layout pass: the window draws a frame now anyway (text streamed in, a row grew), and what changes here is drawn
    /// in it. Step there, and put the timer's own step a whole interval off, so it only draws when no layout pass did.
    /// </summary>
    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var now = Now();
        if (now - _lastStep < TimeSpan.FromSeconds(1.0 / MaxFps)) return;
        StepAll(now);
        _timer?.Stop();
        _timer?.Start();
    }

    private static void StepAll(TimeSpan now)
    {
        _lastStep = now;
        foreach (var p in Shown.Values) p.Step(now);
    }

    /// <summary>One element's pulse: the opacity it is drawn with, bound at animation priority (over what styles set, as a
    /// style animation is) until disposed; <see cref="Top"/>, the window it is in.</summary>
    private sealed class Pulsing(double low, TimeSpan duration, TopLevel? top) : IObservable<double>, IDisposable
    {
        private IObserver<double>? _observer;
        private double _value = double.NaN;
        public IDisposable? Binding;
        public TopLevel? Top { get; } = top;

        public void Step(TimeSpan now)
        {
            var value = OpacityAt(now, low, duration);
            if (value == _value) return;
            _value = value;
            _observer?.OnNext(value);
        }

        public IDisposable Subscribe(IObserver<double> observer)
        {
            _observer = observer;
            observer.OnNext(_value);
            return new Unsubscribe(this);
        }

        public void Dispose()
        {
            var binding = Binding;
            Binding = null;
            binding?.Dispose();
            _observer = null;
        }

        private sealed class Unsubscribe(Pulsing owner) : IDisposable
        {
            public void Dispose() => owner._observer = null;
        }
    }
}

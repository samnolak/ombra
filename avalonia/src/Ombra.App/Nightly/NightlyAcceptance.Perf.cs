using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Ombra.App.Controls;
using Ombra.App.Platform.Mac;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Nightly;

/// <summary>
/// When the frames of a <see cref="NightlyPane"/> arrive and are shown (development checks only: set
/// <see cref="NightlyPane.Timing"/>; null records nothing). Times in seconds of <c>mach_absolute_time</c>, the clock of
/// <see cref="NightlyProtocol.FrameInfo.PresentedAt"/>.
/// </summary>
internal sealed class NightlyFrameTiming
{
    private readonly Lock _gate = new();
    private readonly List<(double Presented, double Received, double Shown)> _shown = [];
    private int _received;

    public static double Now() => MachSeconds(mach_absolute_time());

    public static double MachSeconds(ulong ticks)
    {
        if (s_timebase.Denom == 0) mach_timebase_info(out s_timebase);
        return ticks * (double)s_timebase.Numer / s_timebase.Denom / 1e9;
    }

    public void Received()
    {
        lock (_gate) _received++;
    }

    public void Shown(NightlyProtocol.FrameInfo frame, double received)
    {
        var now = Now();
        lock (_gate) _shown.Add((MachSeconds(frame.PresentedAt), received, now));
    }

    public (int Received, List<(double Presented, double Received, double Shown)> Shown) Take()
    {
        lock (_gate)
        {
            var result = (_received, _shown.ToList());
            _received = 0;
            _shown.Clear();
            return result;
        }
    }

    private struct Timebase
    {
        public uint Numer, Denom;
    }

    private static Timebase s_timebase;

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern ulong mach_absolute_time();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int mach_timebase_info(out Timebase info);
}

internal static partial class NightlyAcceptance
{
    private sealed partial class Run
    {
        private (double Start, TimeSpan OmbraCpu, TimeSpan NightlyCpu)? _statsFrom;

        /// <summary>
        /// Performance commands of the driver: <c>pan X Y STEPS PX MS</c> (a trackpad pan over the browser window pane at
        /// X, Y: began, STEPS changes of PX points, ended, one every MS ms), <c>keys CODE CHARS N MS</c> (N presses of a key,
        /// one every MS ms, each logged as <c>KEYT</c> with its wall-clock ms), <c>stats begin</c> / <c>stats end</c>
        /// (frames received and shown in between, the shown frames' intervals, Nightly present → shown, and the CPU of
        /// Ombra and of Nightly's processes), <c>frames</c> (<see cref="FrameChecks"/>).
        /// </summary>
        private async Task<bool> PerfCommand(string[] command, NightlyPane? pane)
        {
            var parts = string.Join(' ', command).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double D(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
            switch (parts[0])
            {
                case "pan" when pane is not null:
                    await Pan(pane, D(1), D(2), (int)D(3), D(4), D(5));
                    return true;
                case "move":
                    window.Position = new PixelPoint((int)D(1), (int)D(2));
                    await Task.Delay(300);
                    return true;
                case "keys":
                {
                    var chars = parts[2] == "-" ? "" : parts[2];
                    for (var i = 0; i < (int)D(3); i++)
                    {
                        Log(FormattableString.Invariant($"KEYT {WallMs():0.000}"));
                        KeyPress(chars, Convert.ToUInt16(parts[1], 16), 0);
                        await Task.Delay((int)D(4));
                    }
                    return true;
                }
                case "frames" when pane is not null:
                    await FrameChecks(pane);
                    return true;
                case "stats" when pane is not null:
                    if (parts[1] == "begin")
                    {
                        pane.Timing = new NightlyFrameTiming();
                        _statsFrom = (NightlyFrameTiming.Now(), Cpu(Environment.ProcessId), NightlyCpu(pane.RuntimePid));
                        return true;
                    }
                    if (TakeStats(pane) is { } stats) Log("STATS " + stats);
                    return true;
            }
            return false;
        }

        /// <summary>What <see cref="NightlyFrameTiming"/> recorded since <c>stats begin</c> (stops recording); null when it
        /// was not recording.</summary>
        private FrameStats? TakeStats(NightlyPane pane)
        {
            if (pane.Timing is not { } timing || _statsFrom is not { } from) return null;
            var (received, shown) = timing.Take();
            pane.Timing = null;
            _statsFrom = null;
            var seconds = NightlyFrameTiming.Now() - from.Start;
            var intervals = shown.Zip(shown.Skip(1), (a, b) => (b.Shown - a.Shown) * 1000).ToList();
            // Frames shown in the same display refresh as the one before them (under 3 ms apart) never reach the screen.
            var distinct = shown.Count == 0 ? 0 : 1 + intervals.Count(i => i >= 3);
            var span = shown.Count > 1 ? shown[^1].Shown - shown[0].Shown : 0;
            var latency = shown.Select(f => (f.Shown - f.Presented) * 1000).ToList();
            var transport = shown.Select(f => (f.Received - f.Presented) * 1000).ToList();
            var ombra = (Cpu(Environment.ProcessId) - from.OmbraCpu).TotalSeconds / seconds * 100;
            var nightly = (NightlyCpu(pane.RuntimePid) - from.NightlyCpu).TotalSeconds / seconds * 100;
            return new FrameStats(seconds, received, shown.Count, distinct, span > 0 ? (distinct - 1) / span : 0, span,
                Percentiles(intervals), Percentiles(transport), Percentiles(latency), ombra, nightly);
        }

        private sealed record FrameStats(double Seconds, int Received, int Shown, int Distinct, double Fps, double Span,
            string Interval, string Transport, string Latency, double OmbraCpu, double NightlyCpu)
        {
            public override string ToString() => FormattableString.Invariant(
                $"seconds={Seconds:0.00} received={Received} shown={Shown} distinct={Distinct} fps={Fps:0.0} over {Span:0.00} s ")
                + $"interval={Interval} present→received={Transport} present→shown={Latency} "
                + FormattableString.Invariant($"cpu ombra={OmbraCpu:0}% nightly={NightlyCpu:0}%");
        }

        /// <summary>A long page for <see cref="FrameChecks"/>: blue, a striped column down its left side that scrolling moves.</summary>
        private const string PerfPage = """
            <!doctype html><title>perf</title>
            <body style="margin:0;background:#2080c0;height:30000px">
            <div style="width:20%;height:100%;background:repeating-linear-gradient(#fff 0 20px,#000 20px 40px)"></div>
            """;

        /// <summary>
        /// The Browser page's frames go to the screen as Nightly makes them: during a trackpad pan over a long page (a new
        /// tab of the window), at least 90 % of the frames Nightly presents reach the screen, at least 120 a second (Nightly
        /// draws at the display's rate). What Ombra draws over the pane stays above the page (a box in the window's adorner
        /// layer over the frames' layer: its colour in a shot of the window, the page's round it).
        /// </summary>
        private async Task FrameChecks(NightlyPane pane)
        {
            File.WriteAllText(Path.Combine(dir, "perf.html"), PerfPage);
            vm.OpenInBrowserWindow?.Invoke(new Uri(Path.Combine(dir, "perf.html")));
            (int R, int G, int B) blue = (32, 128, 192), magenta = (255, 0, 255), page = default;
            var loaded = await Until(() => Near(page = ContentColor(pane, ContentMiddle), blue), 30);
            await Task.Delay(500);
            var stats = default(FrameStats);
            for (var attempt = 0; attempt < 3 && loaded; attempt++)
            {
                pane.Timing = new NightlyFrameTiming();
                _statsFrom = (NightlyFrameTiming.Now(), Cpu(Environment.ProcessId), NightlyCpu(pane.RuntimePid));
                await Pan(pane, pane.Bounds.Width * 0.6, pane.Bounds.Height * 0.6, 150, 8, 8);
                await Task.Delay(300);
                stats = TakeStats(pane);
                Log($"frames pan {attempt + 1}: {stats}");
                if (stats is { } s && s.Distinct >= 0.9 * s.Received && s.Fps >= 120) break;
            }
            Check("browser-window-frames", loaded && stats is { } f && f.Distinct >= 0.9 * f.Received && f.Fps >= 120,
                $"page rgb={page} {stats}");

            var box = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 0, 255)),
                Width = 80, Height = 80,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            var adorners = Avalonia.Controls.Primitives.AdornerLayer.GetAdornerLayer(pane);
            Avalonia.Controls.Primitives.AdornerLayer.SetAdornedElement(box, pane);
            adorners?.Children.Add(box);
            await Task.Delay(500);
            var w = 30 / pane.Bounds.Width;
            var h = 30 / pane.Bounds.Height;
            var over = ContentColor(pane, new Rect(0.5 - w / 2, 0.5 - h / 2, w, h));
            var round = ContentColor(pane, new Rect(0.75, 0.25, 0.1, 0.1));
            Shot("p-browser-window-overlay");
            adorners?.Children.Remove(box);
            Check("browser-window-overlay", adorners is not null && pane.LayerShown && Near(over, magenta) && Near(round, blue),
                $"over the pane rgb={over} page rgb={round} layer shown={pane.LayerShown}");
            KeyPress("w", 0x0D, 1UL << 20); // the perf tab closes again
            await Task.Delay(500);
        }

        private static string Percentiles(List<double> values)
        {
            if (values.Count == 0) return "-";
            values.Sort();
            double P(double p) => values[Math.Min(values.Count - 1, (int)(p * values.Count))];
            return FormattableString.Invariant($"p50 {P(0.5):0.0} p95 {P(0.95):0.0} max {values[^1]:0.0} ms");
        }

        private static double WallMs() => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalMilliseconds;

        private static TimeSpan Cpu(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.TotalProcessorTime;
            }
            catch (Exception)
            {
                return TimeSpan.Zero;
            }
        }

        /// <summary>CPU time of the runtime's process and its children (content, GPU, sockets).</summary>
        private static TimeSpan NightlyCpu(int pid)
        {
            if (pid == 0 || NightlyMach.ExecutableOf(pid) is not { } exe) return TimeSpan.Zero;
            var app = exe[..(exe.IndexOf(".app/", StringComparison.Ordinal) + 4)];
            return NightlyProcesses(app).Aggregate(TimeSpan.Zero, (sum, id) => sum + Cpu(id));
        }

        /// <summary>A trackpad pan at a real cadence: began, <paramref name="steps"/> changes of <paramref name="px"/> points
        /// (the content moving up), ended, one event every <paramref name="ms"/> ms.</summary>
        private async Task Pan(NightlyPane pane, double x, double y, int steps, double px, double ms)
        {
            var at = ClientToWindow(pane.TranslatePoint(new Point(x, y), window) ?? default);
            var clock = Stopwatch.StartNew();
            var due = 0.0;
            async Task Next()
            {
                due += ms;
                while (clock.Elapsed.TotalMilliseconds < due) await Task.Delay(1);
            }
            Log(FormattableString.Invariant($"PAN start {WallMs():0.000}"));
            PanEvent(at, 1, 0);
            for (var i = 0; i < steps; i++)
            {
                await Next();
                PanEvent(at, 2, -(int)px);
            }
            await Next();
            PanEvent(at, 4, 0);
            Log(FormattableString.Invariant($"PAN end {WallMs():0.000} took {clock.Elapsed.TotalMilliseconds:0} ms"));
        }

        private unsafe void PanEvent(CGPoint at, long phase, int delta)
        {
            var screen = ((delegate* unmanaged<nint, nint, CGPoint, CGPoint>)MacObjC.MsgSend)(NsWindow, MacObjC.Sel("convertPointToScreen:"), at);
            var number = MacObjC.SendLong(NsWindow, "windowNumber");
            var cg = CGEventCreateScrollWheelEvent2(0, 0 /* pixels */, 1, delta, 0, 0);
            try
            {
                CGEventSetLocation(cg, new CGPoint(screen.X, PrimaryScreenHeight() - screen.Y));
                CGEventSetIntegerValueField(cg, 99 /* kCGScrollWheelEventScrollPhase */, phase);
                CGEventSetIntegerValueField(cg, 91, number);
                CGEventSetIntegerValueField(cg, 92, number);
                var ev = MacObjC.Send(MacObjC.Cls("NSEvent"), "eventWithCGEvent:", cg);
                if (MacObjC.RespondsTo(ev, "_eventRelativeToWindow:") && MacObjC.Send(ev, "_eventRelativeToWindow:", NsWindow) is var relative and not 0)
                    ev = relative;
                MacObjC.SendVoid(App, "sendEvent:", ev);
            }
            finally
            {
                NightlyMach.CFRelease(cg);
            }
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;

namespace OmpGui.App.Services;

/// <summary>
/// Opt-in performance log for lag reports (<c>OMPGUI_PERF=1</c>, read once at start; see docs/USER_GUIDE.md). The
/// window shows Avalonia's frame-rate, layout and render graphs, and stderr gets every UI apply slower than
/// <see cref="SlowApplyMs"/> with its stages, and once a second how often the expensive UI work ran. Off (the default)
/// every call returns at once and nothing ticks.
/// </summary>
internal static class PerfLog
{
    /// <summary>An apply this slow (or slower) takes a visible part of a 60 Hz frame: it is logged.</summary>
    public const double SlowApplyMs = 4;

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("OMPGUI_PERF") == "1";

    // UI thread only: the applies, and what the view counts with Count (Markdown rebuilds, transcript layouts…)
    private static readonly SortedDictionary<string, int> Counts = new(StringComparer.Ordinal);
    private static int _applies, _slowApplies;
    private static double _worstApplyMs;
    private static DispatcherTimer? _summary;

    /// <summary>Turns on the overlays for <paramref name="window"/> and starts the once-a-second summary.</summary>
    public static void Attach(TopLevel window)
    {
        if (!Enabled) return;
        window.RendererDiagnostics.DebugOverlays =
            RendererDebugOverlays.Fps | RendererDebugOverlays.LayoutTimeGraph | RendererDebugOverlays.RenderTimeGraph;
        _summary ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => WriteSummary());
        _summary.Start();
    }

    /// <summary>Counts one occurrence of <paramref name="what"/> for the per-second summary (UI thread).</summary>
    public static void Count(string what)
    {
        if (!Enabled) return;
        Counts[what] = Counts.GetValueOrDefault(what) + 1;
    }

    /// <summary>Starts timing one UI apply; null (nothing to time) when the log is off.</summary>
    public static ApplyTiming? StartApply() => Enabled ? new ApplyTiming() : null;

    private static void WriteSummary()
    {
        if (_applies == 0 && Counts.Count == 0) return;
        var line = new StringBuilder("perf 1s: ");
        line.Append(CultureInfo.InvariantCulture, $"applies {_applies} (slow {_slowApplies}, worst {_worstApplyMs:F1} ms)");
        foreach (var (what, n) in Counts) line.Append(CultureInfo.InvariantCulture, $", {what} {n}");
        Console.Error.WriteLine(line);
        Counts.Clear();
        _applies = _slowApplies = 0;
        _worstApplyMs = 0;
    }

    /// <summary>The stages of one apply, written to stderr when the whole apply was slow.</summary>
    internal sealed class ApplyTiming
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly StringBuilder _stages = new();
        private long _mark;

        public ApplyTiming() => _mark = _start;

        /// <summary>Ends the stage <paramref name="stage"/> (the time since the previous one ended).</summary>
        public void Lap(string stage)
        {
            var now = Stopwatch.GetTimestamp();
            _stages.Append(CultureInfo.InvariantCulture, $"{stage} {Stopwatch.GetElapsedTime(_mark, now).TotalMilliseconds:F1}, ");
            _mark = now;
        }

        public void End(int items, int rowsAdded, int rowsUpdated)
        {
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            _applies++;
            _worstApplyMs = Math.Max(_worstApplyMs, ms);
            if (ms < SlowApplyMs) return;
            _slowApplies++;
            Lap("events");
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"perf slow apply {ms:F1} ms: {_stages}rows +{rowsAdded} ~{rowsUpdated} of {items}"));
        }
    }
}

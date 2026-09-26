namespace OmpGui.App.Services.Speech;

/// <summary>
/// Input level of a recording, for the dictation waveform: the recorder feeds every chunk it reads (any thread), the UI
/// polls <see cref="Take"/> a few times per second. No allocation per chunk or per poll.
/// </summary>
public sealed class LevelMeter
{
    private readonly object _gate = new();
    private double _peak;
    private double _last;
    private bool _fresh;

    /// <summary>Adds a chunk of PCM (float, −1..1): its RMS counts towards the next <see cref="Take"/>.</summary>
    public void Add(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;
        var rms = Rms(samples);
        lock (_gate)
        {
            if (!_fresh || rms > _peak) _peak = rms;
            _fresh = true;
        }
    }

    /// <summary>
    /// The loudest chunk's RMS (0..1) since the previous call. When no audio arrived in between (recorders deliver in
    /// bursts), the previous value again, so the waveform does not flicker to zero between bursts.
    /// </summary>
    public double Take()
    {
        lock (_gate)
        {
            if (_fresh) { _last = _peak; _fresh = false; }
            return _last;
        }
    }

    /// <summary>Root mean square of the samples: 0 for silence, 1/√2 ≈ 0.707 for a full-scale sine.</summary>
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Min(1, Math.Sqrt(sum / samples.Length));
    }

    /// <summary>Floor of the display scale, in dBFS: quieter than this (room noise) shows as the smallest bar.</summary>
    public const double FloorDb = -50;
    /// <summary>Top of the display scale, in dBFS: this loud or louder shows as the tallest bar.</summary>
    public const double CeilingDb = -6;

    /// <summary>
    /// RMS mapped to 0..1 for display, on a decibel scale (speech at a normal distance sits around −35..−15 dBFS, so a
    /// linear scale would show it as barely moving bars).
    /// </summary>
    public static double ToDisplay(double rms)
    {
        if (!(rms > 0)) return 0;
        var db = 20 * Math.Log10(rms);
        return Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0, 1);
    }
}

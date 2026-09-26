namespace OmpGui.App.Services.Speech;

/// <summary>
/// Prepares recorded audio for the speech decoder: phrases split at pauses, long audio cut at quiet points, and
/// resampling to the model's rate.
/// </summary>
public static class AudioSegmentation
{
    /// <summary>Decoder-sized chunks cut at the quietest point near each boundary, so words are not cut.</summary>
    public static List<(int Start, int End)> SplitAtSilence(ReadOnlySpan<float> samples, int sampleRate = SpeechModel.SampleRate, double targetSec = 20, double windowSec = 6)
    {
        var total = samples.Length;
        var target = (int)Math.Round(targetSec * sampleRate);
        var window = (int)Math.Round(windowSec * sampleRate);
        if (total <= target + window) return [(0, total)];
        var frame = (int)Math.Round(0.1 * sampleRate);
        var ranges = new List<(int, int)>();
        var start = 0;
        while (total - start > target + window)
        {
            var lo = start + target - window;
            var hi = Math.Min(total - frame, start + target + window);
            var best = start + target;
            var bestEnergy = double.PositiveInfinity;
            for (var at = lo; at + frame <= hi; at += frame / 2)
            {
                double e = 0;
                for (var i = at; i < at + frame; i++) e += samples[i] * samples[i];
                if (e < bestEnergy) { bestEnergy = e; best = at + frame / 2; }
            }
            ranges.Add((start, best));
            start = best;
        }
        ranges.Add((start, total));
        return ranges;
    }

    /// <summary>
    /// Utterances split at pauses, so each phrase is decoded on its own (Parakeet detects the language per decode).
    /// Very short pieces are merged into the previous one; very long ones are split further at quiet points.
    /// </summary>
    public static List<(int Start, int End)> SplitUtterances(ReadOnlySpan<float> samples, int sampleRate = SpeechModel.SampleRate, double minPauseSec = 0.5, double minSegSec = 4, double maxSegSec = 25)
    {
        var frame = (int)Math.Round(0.03 * sampleRate);
        var n = samples.Length / frame;
        if (n < 2) return samples.Length > 0 ? [(0, samples.Length)] : [];
        var rms = new float[n];
        for (var f = 0; f < n; f++)
        {
            double e = 0;
            for (var i = f * frame; i < (f + 1) * frame; i++) e += samples[i] * samples[i];
            rms[f] = (float)Math.Sqrt(e / frame);
        }
        var sorted = rms.Order().ToArray();
        var floor = sorted[(int)(n * 0.05)];
        var loud = sorted[(int)(n * 0.9)];
        var threshold = Math.Max(0.003f, Math.Max(Math.Min(floor * 3, loud * 0.15f), loud * 0.06f));
        var minPause = (int)Math.Ceiling(minPauseSec * sampleRate / frame);
        var cuts = new List<int>();
        var run = 0;
        for (var f = 0; f <= n; f++)
        {
            if (f < n && rms[f] < threshold) { run++; continue; }
            if (run >= minPause && f - run > 0 && f < n) cuts.Add((f - run / 2) * frame);
            run = 0;
        }
        var bounds = new List<int> { 0 };
        bounds.AddRange(cuts);
        bounds.Add(samples.Length);
        var segs = new List<(int Start, int End)>();
        for (var i = 0; i + 1 < bounds.Count; i++)
        {
            var (a, b) = (bounds[i], bounds[i + 1]);
            if (segs.Count > 0 && (b - a < minSegSec * sampleRate || segs[^1].End - segs[^1].Start < minSegSec * sampleRate))
                segs[^1] = (segs[^1].Start, b);
            else segs.Add((a, b));
        }
        var result = new List<(int, int)>();
        foreach (var (a, b) in segs)
        {
            if (b - a <= maxSegSec * sampleRate) { result.Add((a, b)); continue; }
            foreach (var (x, y) in SplitAtSilence(samples[a..b], sampleRate)) result.Add((a + x, a + y));
        }
        return result;
    }

    /// <summary>Linear resampling (microphones often record at 44.1 or 48 kHz; the model takes 16 kHz).</summary>
    public static float[] Resample(ReadOnlySpan<float> input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input.ToArray();
        var length = (int)((long)input.Length * toRate / fromRate);
        var output = new float[length];
        var step = (double)fromRate / toRate;
        for (var i = 0; i < length; i++)
        {
            var pos = i * step;
            var j = (int)pos;
            var frac = (float)(pos - j);
            var a = input[Math.Min(j, input.Length - 1)];
            var b = input[Math.Min(j + 1, input.Length - 1)];
            output[i] = a + (b - a) * frac;
        }
        return output;
    }
}

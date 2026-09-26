using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace OmpGui.App.Services.Speech;

/// <summary>
/// Records the default input device through PortAudio (WASAPI/MME on Windows, Core Audio on macOS, ALSA/Pulse on Linux)
/// at the device's own rate, mono float, and hands back 16 kHz samples. The OS asks for microphone permission.
/// </summary>
public interface IMicrophone : IDisposable
{
    void Start();
    TimeSpan Recorded { get; }
    /// <summary>Stops and returns the recording as 16 kHz mono.</summary>
    float[] Stop();

    /// <summary>
    /// Input level for the dictation waveform: the loudest RMS (0..1) since the previous call, or null when this
    /// recorder has no meter (the waveform then stays flat). Polled on the UI thread while recording.
    /// </summary>
    double? ReadLevel() => null;
}

public sealed class Microphone : IMicrophone
{
    private const int MaxSeconds = 300;
    private readonly List<float> _samples = [];
    private readonly LevelMeter _meter = new();
    private readonly object _gate = new();
    private PaStream? _stream;
    private double _rate;
    private static bool _initialized;

    /// <exception cref="InvalidOperationException">No microphone, or it could not be opened (message says why).</exception>
    public void Start()
    {
        if (!_initialized)
        {
            PortAudio.Initialize();
            _initialized = true;
        }
        var device = PortAudio.DefaultInputDevice;
        if (device == PortAudio.NoDevice) throw new InvalidOperationException("No microphone found.");
        var info = PortAudio.GetDeviceInfo(device);
        _rate = info.defaultSampleRate;
        var parameters = new StreamParameters
        {
            device = device,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };
        var max = (int)(MaxSeconds * _rate);
        _stream = new PaStream(parameters, null, _rate, 0, StreamFlags.ClipOff, (input, _, frames, ref _, _, _) =>
        {
            var chunk = new float[frames];
            System.Runtime.InteropServices.Marshal.Copy(input, chunk, 0, (int)frames);
            _meter.Add(chunk);
            lock (_gate)
            {
                if (_samples.Count + chunk.Length > max) return StreamCallbackResult.Complete;
                _samples.AddRange(chunk);
            }
            return StreamCallbackResult.Continue;
        }, null);
        _stream.Start();
    }

    public TimeSpan Recorded
    {
        get { lock (_gate) return _rate > 0 ? TimeSpan.FromSeconds(_samples.Count / _rate) : TimeSpan.Zero; }
    }

    public double? ReadLevel() => _meter.Take();

    /// <summary>Stops and returns the recording as 16 kHz mono.</summary>
    public float[] Stop()
    {
        _stream?.Stop();
        _stream?.Dispose();
        _stream = null;
        float[] raw;
        lock (_gate) raw = [.. _samples];
        return AudioSegmentation.Resample(raw, (int)_rate, SpeechModel.SampleRate);
    }

    public void Dispose()
    {
        try { _stream?.Abort(); } catch (PortAudioException) { }
        _stream?.Dispose();
        _stream = null;
    }
}

/// <summary>
/// Records through a system recorder that writes raw 16 kHz mono float32 to its stdout (parecord, pw-record, arecord).
/// Used when PortAudio cannot be loaded — the bundled Linux build needs the JACK client library, which many desktops
/// do not install. The recorder is this client's own child process and is killed on stop.
/// </summary>
public sealed class CommandMicrophone(string file, IReadOnlyList<string> args) : IMicrophone
{
    private const int MaxSeconds = 300;
    private readonly List<float> _samples = [];
    private readonly LevelMeter _meter = new();
    private readonly object _gate = new();
    private System.Diagnostics.Process? _process;
    private Task? _reader;
    private Task<List<string>>? _stderr;

    public string Name => Path.GetFileName(file);

    /// <exception cref="InvalidOperationException">The recorder could not start or stopped at once (message says why).</exception>
    public void Start()
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try { _process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException($"{Name} did not start"); }
        catch (System.ComponentModel.Win32Exception e) { throw new InvalidOperationException($"{Name} could not start: {e.Message}", e); }
        // Its first lines, read directly (event-based capture could still be empty when the failure is reported).
        var errors = _process.StandardError;
        _stderr = Task.Run(async () =>
        {
            var lines = new List<string>();
            while (await errors.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (lines.Count < 20 && line.Trim().Length > 0) lines.Add(line.Trim());
            return lines;
        });
        var stdout = _process.StandardOutput.BaseStream;
        _reader = Task.Run(async () =>
        {
            var buffer = new byte[16384];
            var carry = 0;
            var max = MaxSeconds * SpeechModel.SampleRate;
            int n;
            while ((n = await stdout.ReadAsync(buffer.AsMemory(carry)).ConfigureAwait(false)) > 0)
            {
                var total = carry + n;
                var whole = total / 4;
                var chunk = new float[whole];
                Buffer.BlockCopy(buffer, 0, chunk, 0, whole * 4);
                carry = total - whole * 4;
                if (carry > 0) Buffer.BlockCopy(buffer, whole * 4, buffer, 0, carry);
                _meter.Add(chunk);
                lock (_gate)
                {
                    if (_samples.Count + chunk.Length > max) break;
                    _samples.AddRange(chunk);
                }
            }
        });
        // A recorder with no device, no server or bad arguments ends soon (parecord may first try to start a server):
        // wait until sound arrives, it exits, or 2 s pass (then it is running and simply quiet).
        var until = DateTime.UtcNow.AddSeconds(2);
        while (Recorded == TimeSpan.Zero && !_process.HasExited && DateTime.UtcNow < until) Thread.Sleep(20);
        if (_process.HasExited && Recorded == TimeSpan.Zero)
        {
            var reason = _stderr.Wait(TimeSpan.FromSeconds(2)) ? _stderr.Result.FirstOrDefault() : null;
            throw new InvalidOperationException($"{Name} stopped at once: {reason ?? $"exit code {_process.ExitCode}, no message"}");
        }
    }

    public TimeSpan Recorded
    {
        get { lock (_gate) return TimeSpan.FromSeconds(_samples.Count / (double)SpeechModel.SampleRate); }
    }

    /// <summary>Metered from the float32 samples read from the recorder's output, as for the PortAudio microphone.</summary>
    public double? ReadLevel() => _meter.Take();

    public float[] Stop()
    {
        Kill();
        try { _reader?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        lock (_gate) return [.. _samples];
    }

    private void Kill()
    {
        if (_process is not { } p) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); p.WaitForExit(2000); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        Kill();
        _process?.Dispose();
        _process = null;
    }

    /// <summary>Recorders found on PATH, in order of preference, each set up for raw 16 kHz mono float32 on stdout.</summary>
    public static IReadOnlyList<CommandMicrophone> SystemRecorders(Func<string, string?>? which = null)
    {
        which ??= OnPath;
        var found = new List<CommandMicrophone>();
        if (which("parecord") is { } pa) found.Add(new CommandMicrophone(pa, ["--raw", "--format=float32le", "--rate=16000", "--channels=1"]));
        if (which("pw-record") is { } pw) found.Add(new CommandMicrophone(pw, ["--format=f32", "--rate=16000", "--channels=1", "-"]));
        if (which("arecord") is { } al) found.Add(new CommandMicrophone(al, ["-q", "-t", "raw", "-f", "FLOAT_LE", "-r", "16000", "-c", "1"]));
        return found;
    }

    private static string? OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
}

/// <summary>PortAudio first; when its library cannot be loaded, a system recorder (see <see cref="CommandMicrophone"/>).</summary>
public sealed class AutoMicrophone(Func<IMicrophone>? primary = null, Func<IReadOnlyList<IMicrophone>>? fallbacks = null) : IMicrophone
{
    private readonly Func<IMicrophone> _primary = primary ?? (() => new Microphone());
    private readonly Func<IReadOnlyList<IMicrophone>> _fallbacks = fallbacks ?? (() => CommandMicrophone.SystemRecorders());
    private IMicrophone? _active;

    public void Start()
    {
        var first = _primary();
        try
        {
            first.Start();
            _active = first;
            return;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or BadImageFormatException or PortAudioException)
        {
            first.Dispose();
            var candidates = _fallbacks();
            if (candidates.Count == 0) throw;
            var reasons = new List<string>();
            foreach (var candidate in candidates)
            {
                try
                {
                    candidate.Start();
                    _active = candidate;
                    return;
                }
                catch (InvalidOperationException why)
                {
                    candidate.Dispose();
                    reasons.Add(why.Message);
                }
            }
            throw new InvalidOperationException("No microphone could be opened: " + string.Join("; ", reasons), e);
        }
        catch
        {
            first.Dispose();
            throw;
        }
    }

    public TimeSpan Recorded => _active?.Recorded ?? TimeSpan.Zero;
    public double? ReadLevel() => _active?.ReadLevel();
    public float[] Stop() => _active?.Stop() ?? [];
    public void Dispose() => _active?.Dispose();
}

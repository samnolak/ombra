using System.Net;
using System.Security.Cryptography;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.Services.Speech;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Dictation: verified model download, audio segmentation, the record → transcribe → insert flow, no-microphone path.</summary>
public sealed class DictationTests
{
    /// <summary>A tiny HTTP server that serves files by name under /repo/resolve/rev/.</summary>
    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        public readonly string BaseUrl;
        public readonly Dictionary<string, byte[]> Files = [];

        public Server()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    var name = Path.GetFileName(ctx.Request.Url!.AbsolutePath);
                    if (Files.TryGetValue(name, out var data))
                    {
                        ctx.Response.ContentLength64 = data.Length;
                        await ctx.Response.OutputStream.WriteAsync(data);
                    }
                    else ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() => _listener.Close();
    }

    private static SpeechModelFile Pin(string role, string name, byte[] data, bool hash = true) =>
        new(role, name, data.Length, hash ? Convert.ToHexStringLower(SHA256.HashData(data)) : null);

    [Fact]
    public async Task Download_admits_only_files_matching_the_pinned_size_and_hash()
    {
        using var server = new Server();
        var dir = TestProcesses.TempDir("model");
        byte[] a = [1, 2, 3, 4], b = "tokens"u8.ToArray();
        server.Files["a.onnx"] = a;
        server.Files["tokens.txt"] = b;
        var files = new[] { Pin("encoder", "a.onnx", a), Pin("tokens", "tokens.txt", b, hash: false) };
        var reports = new List<(long, long)>();
        await new SpeechModelDownloader(new HttpClient(), dir, server.BaseUrl, files).DownloadAsync(new SyncProgress(reports), CancellationToken.None);
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(dir, "a.onnx")));
        Assert.Equal((10L, 10L), reports[^1]);

        // Tampered content: refused, nothing left behind (neither the file nor its .part).
        var dir2 = TestProcesses.TempDir("model-bad");
        server.Files["a.onnx"] = [9, 9, 9, 9];
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => new SpeechModelDownloader(new HttpClient(), dir2, server.BaseUrl, files).DownloadAsync(null, CancellationToken.None));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFiles(dir2));

        // Wrong size: refused too.
        server.Files["a.onnx"] = [1, 2, 3, 4, 5];
        await Assert.ThrowsAsync<InvalidDataException>(() => new SpeechModelDownloader(new HttpClient(), dir2, server.BaseUrl, files).DownloadAsync(null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(dir2));

        // Cancelled: nothing half-written.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SpeechModelDownloader(new HttpClient(), dir2, server.BaseUrl, files).DownloadAsync(null, cts.Token));
        Assert.Empty(Directory.GetFiles(dir2));
    }

    private sealed class SyncProgress(List<(long, long)> list) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => list.Add(value);
    }

    [Fact]
    public void The_pinned_model_revision_size_and_hashes_are_fixed()
    {
        Assert.Equal("2bda32ec70b097a55adaa07d9a7173915b43cc78", SpeechModel.Revision);
        Assert.Equal(670_478_772L, SpeechModel.TotalBytes);
        Assert.Equal(3, SpeechModel.Files.Count(f => f.Sha256 is { Length: 64 }));
    }

    [Fact]
    public void Segmentation_cuts_at_pauses_and_quiet_points()
    {
        const int rate = SpeechModel.SampleRate;
        float[] Tone(double sec) => [.. Enumerable.Range(0, (int)(sec * rate)).Select(i => 0.3f * MathF.Sin(i * 0.05f))];
        float[] Silence(double sec) => new float[(int)(sec * rate)];
        var audio = Tone(5).Concat(Silence(1)).Concat(Tone(5)).ToArray();
        var parts = AudioSegmentation.SplitUtterances(audio);
        Assert.Equal(2, parts.Count);
        Assert.Equal(0, parts[0].Start);
        Assert.Equal(audio.Length, parts[^1].End);
        Assert.InRange(parts[1].Start, 5 * rate, 6 * rate);

        var longAudio = Tone(60);
        var chunks = AudioSegmentation.SplitAtSilence(longAudio);
        Assert.True(chunks.Count >= 2);
        Assert.Equal(longAudio.Length, chunks[^1].End);
        Assert.All(chunks.Zip(chunks.Skip(1)), p => Assert.Equal(p.First.End, p.Second.Start));

        Assert.Equal(16_000, AudioSegmentation.Resample(new float[48_000], 48_000, 16_000).Length);
    }

    private sealed class FakeMic(float[] samples) : IMicrophone
    {
        public bool Started;
        public void Start() => Started = true;
        public TimeSpan Recorded => TimeSpan.FromSeconds(1);
        public float[] Stop() => samples;
        public void Dispose() { }
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public int Calls;
        public string Transcribe(float[] samples) { Calls++; return "hello from dictation"; }
        public void Dispose() { }
    }

    private sealed class NoMic : IMicrophone
    {
        public void Start() => throw new InvalidOperationException("No microphone found.");
        public TimeSpan Recorded => TimeSpan.Zero;
        public float[] Stop() => [];
        public void Dispose() { }
    }

    [AvaloniaFact]
    public async Task Record_then_stop_inserts_the_text_and_a_missing_model_asks_first()
    {
        var installed = false;
        var mic = new FakeMic(new float[16_000]);
        var transcriber = new FakeTranscriber();
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs())
        {
            SpeechModelInstalled = _ => installed,
            MicrophoneFactory = () => mic,
            TranscriberFactory = _ => transcriber,
        };
        await vm.DictateCommand.ExecuteAsync(null);
        Assert.True(vm.IsDictationOfferOpen); // nothing downloads without consent
        Assert.False(mic.Started);
        vm.CloseDictationOfferCommand.Execute(null);

        installed = true;
        vm.ComposerText = "note:";
        await vm.DictateCommand.ExecuteAsync(null);
        Assert.True(vm.IsRecording && mic.Started);
        await vm.DictateCommand.ExecuteAsync(null);
        Assert.False(vm.IsRecording);
        Assert.Equal("note: hello from dictation", vm.ComposerText);
        Assert.Equal(1, transcriber.Calls);
    }

    [AvaloniaFact]
    public async Task No_microphone_is_explained_not_thrown()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs())
        {
            SpeechModelInstalled = _ => true,
            MicrophoneFactory = () => new NoMic(),
        };
        await vm.DictateCommand.ExecuteAsync(null);
        Assert.False(vm.IsRecording);
        Assert.False(vm.IsDictating);
        Assert.Equal("No microphone found.", vm.DictationError); // shown in the dictation bar
        Assert.True(vm.IsDictationBarVisible);
    }

    [Fact]
    public void The_real_microphone_without_a_device_fails_cleanly()
    {
        // CI runners and this container have no audio input: a clear exception, never a crash. Where PortAudio itself
        // cannot load (Linux without libjack), the recorder fallback tests cover dictation instead.
        using var mic = new Microphone();
        try
        {
            mic.Start();
        }
        catch (DllNotFoundException) { Assert.Skip("PortAudio cannot be loaded here"); }
        catch (TypeInitializationException e) when (e.InnerException is DllNotFoundException) { Assert.Skip("PortAudio cannot be loaded here"); }
        catch (Exception e) when (e is InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Message));
            return;
        }
        Assert.NotNull(mic.Stop()); // a machine with a microphone records
    }

    [Fact]
    public void A_recorder_that_fails_after_a_while_is_not_taken_for_a_working_one()
    {
        // Regression (review round 4): only 300 ms were watched; parecord starting a sound server first failed later
        // and a dead recorder was used (0:00, then "no speech"), without trying the next one.
        using var mic = FakeRecorder("recorder-slow-fail");
        var e = Assert.Throws<InvalidOperationException>(mic.Start);
        Assert.Contains("Connection refused", e.Message);
    }

    private sealed class BlockingTranscriber : ITranscriber
    {
        public readonly SemaphoreSlim Release = new(0);
        public readonly List<string> Events = [];
        public string Transcribe(float[] samples)
        {
            lock (Events) Events.Add("decode start");
            Release.Wait(TimeSpan.FromSeconds(20));
            lock (Events) Events.Add("decode end");
            return "late text";
        }
        public void Dispose() { lock (Events) Events.Add("dispose"); }
    }

    [AvaloniaFact]
    public async Task Closing_during_transcription_frees_the_recognizer_only_after_the_decode()
    {
        // Regression (review round 4): closing the window disposed the native recognizer while it was decoding.
        var transcriber = new BlockingTranscriber();
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs())
        {
            SpeechModelInstalled = _ => true,
            MicrophoneFactory = () => new FakeMic(new float[16_000]),
            TranscriberFactory = _ => transcriber,
        };
        await vm.DictateCommand.ExecuteAsync(null);
        var stopping = vm.DictateCommand.ExecuteAsync(null); // stop: the decode starts and blocks
        Assert.True(SpinWait.SpinUntil(() => { lock (transcriber.Events) return transcriber.Events.Contains("decode start"); }, TimeSpan.FromSeconds(10)));
        var closing = vm.DisposeAsync().AsTask();
        await Task.Delay(300);
        lock (transcriber.Events) Assert.DoesNotContain("dispose", transcriber.Events);
        transcriber.Release.Release();
        await closing;
        await stopping;
        lock (transcriber.Events) Assert.Equal(["decode start", "decode end", "dispose"], transcriber.Events);
    }

    [Fact]
    public void A_missing_system_audio_library_is_named_not_dumped()
    {
        // Regression (package self-test, Linux): the bundled PortAudio needs libjack.so.0; the loader's message listed a
        // dozen probed paths and would have been shown as is.
        var loader = new DllNotFoundException("Unable to load shared library 'portaudio' or one of its dependencies. In order to help diagnose loading problems, consider using a tool like strace.\n"
            + "/app/runtimes/linux-x64/native/portaudio.so: cannot open shared object file: No such file or directory\n"
            + "libjack.so.0: cannot open shared object file: No such file or directory\n"
            + "/app/libportaudio.so: cannot open shared object file: No such file or directory\n");
        var text = MainViewModel.MicrophoneError(loader);
        Assert.StartsWith("the audio library needs libjack.so.0", text);
        Assert.DoesNotContain("/app", text);
        Assert.Equal("the audio library could not be loaded on this system.", MainViewModel.MicrophoneError(new DllNotFoundException("portaudio.dll not found")));
        Assert.Equal("No microphone found.", MainViewModel.MicrophoneError(new InvalidOperationException("No microphone found.")));
    }

    private static CommandMicrophone FakeRecorder(string scenario)
    {
        var spec = TestProcesses.Fake(scenario);
        return new CommandMicrophone(spec.FileName, spec.Arguments);
    }

    [Fact]
    public async Task A_system_recorder_streams_16_khz_samples_and_ends_with_stop()
    {
        using var mic = FakeRecorder("recorder");
        mic.Start();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (mic.Recorded < TimeSpan.FromMilliseconds(300) && DateTime.UtcNow < deadline) await Task.Delay(50);
        var samples = mic.Stop();
        Assert.True(samples.Length >= 16000 * 0.3, $"{samples.Length} samples");
        Assert.InRange(samples.Max(), 0.45f, 0.51f); // the stand-in's 440 Hz sine at 0.5
        var after = mic.Recorded;
        await Task.Delay(300);
        Assert.Equal(after, mic.Recorded); // the recorder process is gone: nothing more arrives
    }

    [Fact]
    public void A_recorder_that_stops_at_once_says_why()
    {
        using var mic = FakeRecorder("recorder-fails");
        var e = Assert.Throws<InvalidOperationException>(mic.Start);
        Assert.Contains("Connection refused", e.Message);
    }

    [Fact]
    public void Portaudio_that_cannot_load_falls_back_to_a_system_recorder()
    {
        // The first recorder has no server (like parecord without PulseAudio); the next one works.
        IMicrophone[] recorders = [FakeRecorder("recorder-fails"), FakeRecorder("recorder")];
        using var mic = new AutoMicrophone(() => new ThrowingMic(new DllNotFoundException("libjack.so.0: cannot open shared object file")), () => recorders);
        mic.Start();
        Assert.True(SpinWait.SpinUntil(() => mic.Recorded > TimeSpan.Zero, TimeSpan.FromSeconds(15)));
        Assert.NotEmpty(mic.Stop());
    }

    [Fact]
    public void Without_a_system_recorder_the_library_error_is_kept()
    {
        using var mic = new AutoMicrophone(() => new ThrowingMic(new DllNotFoundException("libjack.so.0: cannot open shared object file")), () => []);
        var e = Assert.Throws<DllNotFoundException>(mic.Start);
        Assert.StartsWith("the audio library needs libjack.so.0", MainViewModel.MicrophoneError(e));
        // Recorders that all fail: each one's reason is shown.
        using var failing = new AutoMicrophone(() => new ThrowingMic(new DllNotFoundException("x")), () => [FakeRecorder("recorder-fails")]);
        var all = Assert.Throws<InvalidOperationException>(failing.Start);
        Assert.StartsWith("No microphone could be opened:", all.Message);
        Assert.Contains("Connection refused", MainViewModel.MicrophoneError(all));
        // A microphone that is simply missing is not a library problem: no fallback is tried.
        var tried = false;
        using var none = new AutoMicrophone(() => new ThrowingMic(new InvalidOperationException("No microphone found.")), () => { tried = true; return []; });
        Assert.Throws<InvalidOperationException>(none.Start);
        Assert.False(tried);
    }

    [Fact]
    public void System_recorders_are_preferred_in_order_with_raw_16_khz_float_output()
    {
        Assert.Equal(["parecord", "arecord"], CommandMicrophone.SystemRecorders(n => n is "parecord" or "arecord" ? "/usr/bin/" + n : null).Select(r => r.Name));
        Assert.Equal(["pw-record"], CommandMicrophone.SystemRecorders(n => n == "pw-record" ? "/usr/bin/pw-record" : null).Select(r => r.Name));
        Assert.Empty(CommandMicrophone.SystemRecorders(_ => null));
    }

    [Fact]
    public void Real_system_recorders_without_a_device_fail_fast_with_their_own_reason()
    {
        // Only where they are installed (alsa-utils / pulseaudio-utils); CI runners and this sandbox have no sound device.
        var recorders = CommandMicrophone.SystemRecorders();
        Assert.SkipWhen(recorders.Count == 0, "no parecord / pw-record / arecord on PATH");
        foreach (var r in recorders)
        {
            using (r)
            {
                try { r.Start(); r.Stop(); } // a machine with a microphone records; that is fine too
                catch (InvalidOperationException e) { Assert.StartsWith(r.Name + " stopped at once:", e.Message); }
            }
        }
    }

    private sealed class ThrowingMic(Exception e) : IMicrophone
    {
        public bool Disposed;
        public void Start() => throw e;
        public TimeSpan Recorded => TimeSpan.Zero;
        public float[] Stop() => [];
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void A_portaudio_device_error_also_falls_back_and_the_failed_microphone_is_released()
    {
        // Regression (review round 4): only library-load errors fell back to the system recorders, and the PortAudio
        // microphone that failed to open was never disposed.
        var failing = new ThrowingMic(new PortAudioSharp.PortAudioException(PortAudioSharp.ErrorCode.InvalidDevice, "Invalid device"));
        IMicrophone[] recorders = [FakeRecorder("recorder")];
        using (var mic = new AutoMicrophone(() => failing, () => recorders))
        {
            mic.Start();
            Assert.True(failing.Disposed);
            Assert.True(SpinWait.SpinUntil(() => mic.Recorded > TimeSpan.Zero, TimeSpan.FromSeconds(15)));
            mic.Stop();
        }
        var missing = new ThrowingMic(new InvalidOperationException("No microphone found."));
        using var none = new AutoMicrophone(() => missing, () => []);
        Assert.Throws<InvalidOperationException>(none.Start);
        Assert.True(missing.Disposed); // released on every failure path
    }
}

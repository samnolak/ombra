using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.Services.Speech;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Dictation mode: input level metering, the mode's states and commands, and the dictation bar.</summary>
public sealed class DictationModeTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    /// <summary>A microphone with a scripted level and elapsed time.</summary>
    private sealed class LevelMic(float[] samples) : IMicrophone
    {
        public bool Started, Stopped, Disposed;
        public double? Level = 0.1;
        public int LevelReads;
        public TimeSpan Elapsed = TimeSpan.FromSeconds(1);
        public void Start() => Started = true;
        public TimeSpan Recorded => Elapsed;
        public float[] Stop() { Stopped = true; return samples; }
        public double? ReadLevel() { LevelReads++; return Level; }
        public void Dispose() => Disposed = true;
    }

    private sealed class CountingTranscriber(string text = "dictated text") : ITranscriber
    {
        public int Calls;
        public string Transcribe(float[] samples) { Interlocked.Increment(ref Calls); return text; }
        public void Dispose() { }
    }

    private sealed class GatedTranscriber : ITranscriber
    {
        public readonly SemaphoreSlim Started = new(0), Release = new(0);
        public string Transcribe(float[] samples)
        {
            Started.Release();
            Release.Wait(TimeSpan.FromSeconds(20));
            return "late text";
        }
        public void Dispose() { }
    }

    private sealed class NoMic : IMicrophone
    {
        public void Start() => throw new InvalidOperationException("No microphone found.");
        public TimeSpan Recorded => TimeSpan.Zero;
        public float[] Stop() => [];
        public void Dispose() { }
    }

    private static MainViewModel NewVm(Func<IMicrophone> mic, ITranscriber? transcriber = null, SessionController? session = null) =>
        new(session ?? new SessionController(TestProcesses.Fake("normal")), new AppArgs())
        {
            SpeechModelInstalled = _ => true,
            MicrophoneFactory = mic,
            TranscriberFactory = _ => transcriber ?? new CountingTranscriber(),
        };

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static float[] Sine(double amplitude, int count, double hz = 440, int rate = 16_000) =>
        [.. Enumerable.Range(0, count).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate)))];

    [Fact]
    public void Level_is_the_rms_of_the_pcm()
    {
        Assert.Equal(0, LevelMeter.Rms(new float[1600]));
        Assert.Equal(0, LevelMeter.Rms([]));
        Assert.InRange(LevelMeter.Rms(Sine(1.0, 16_000)), 0.70, 0.71); // full-scale sine: 1/√2
        Assert.InRange(LevelMeter.Rms(Sine(0.5, 16_000)), 0.35, 0.36);

        // Display scale (dBFS): silence at the bottom, a full-scale sine near the top, speech in between, monotonic.
        Assert.Equal(0, LevelMeter.ToDisplay(0));
        Assert.Equal(0, LevelMeter.ToDisplay(0.0001)); // −80 dBFS: room noise
        Assert.InRange(LevelMeter.ToDisplay(0.707), 0.9, 1.0);
        Assert.InRange(LevelMeter.ToDisplay(0.03), 0.3, 0.7); // ≈ −30 dBFS: normal speech
        Assert.True(LevelMeter.ToDisplay(0.01) < LevelMeter.ToDisplay(0.1));

        // The meter reports the loudest chunk since the last read, and holds it while no audio arrives.
        var meter = new LevelMeter();
        Assert.Equal(0, meter.Take());
        meter.Add(Sine(0.1, 800));
        meter.Add(Sine(1.0, 800));
        meter.Add(new float[800]);
        Assert.InRange(meter.Take(), 0.70, 0.71);
        Assert.InRange(meter.Take(), 0.70, 0.71); // nothing new: held
        meter.Add(new float[800]);
        Assert.Equal(0, meter.Take());
    }

    [Fact]
    public async Task A_system_recorder_is_metered_from_its_output()
    {
        // The stand-in writes a 440 Hz sine at 0.5 as raw float32: RMS ≈ 0.354.
        var spec = TestProcesses.Fake("recorder");
        using var mic = new CommandMicrophone(spec.FileName, spec.Arguments);
        mic.Start();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (mic.Recorded < TimeSpan.FromMilliseconds(200) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.InRange(((IMicrophone)mic).ReadLevel() ?? -1, 0.33, 0.37);
        mic.Stop();

        // A recorder without a meter reports none (the waveform stays flat).
        IMicrophone plain = new NoMic();
        Assert.Null(plain.ReadLevel());
    }

    [AvaloniaFact]
    public async Task Levels_are_throttled_capped_at_32_and_the_elapsed_time_is_shown()
    {
        var mic = new LevelMic(new float[16_000]) { Level = 0.707, Elapsed = TimeSpan.FromSeconds(65) };
        var vm = NewVm(() => mic);
        await vm.StartDictationCommand.ExecuteAsync(null);
        Assert.True(vm.IsRecording && vm.IsDictating && vm.IsDictationBarVisible && vm.ShowDictationWaveform);
        Assert.False(vm.IsTranscribing);

        await Pump(1000);
        // ~16 reads per second (not one per audio chunk): loose bounds for a loaded machine.
        Assert.InRange(mic.LevelReads, 5, 25);
        Assert.InRange(vm.DictationLevels.Count, 5, 25);
        Assert.Equal("01:05", vm.DictationElapsed);
        Assert.Equal("1:05", vm.RecordingText);
        Assert.All(vm.DictationLevels, l => Assert.InRange(l, 0.9, 1.0));

        await Until(() => vm.DictationLevels.Count == MainViewModel.DictationLevelCount && mic.LevelReads > 40, "a full waveform");
        Assert.Equal(32, vm.DictationLevels.Count);

        // Pushing more never grows it; the newest level is last; out-of-range values are clamped.
        for (var i = 0; i < 100; i++) vm.PushDictationLevel(i / 100.0);
        vm.PushDictationLevel(7);
        Assert.Equal(32, vm.DictationLevels.Count);
        Assert.Equal(1.0, vm.DictationLevels[^1]);
        Assert.Equal(0.99, vm.DictationLevels[^2]);

        // A recorder without a meter: flat (zero) levels, not an error.
        mic.Level = null;
        await Pump(300);
        Assert.Equal(0, vm.DictationLevels[^1]);
        vm.CancelDictationCommand.Execute(null);
        Assert.Empty(vm.DictationLevels);
    }

    [AvaloniaFact]
    public async Task Cancel_discards_the_audio_and_leaves_the_composer_alone()
    {
        var mic = new LevelMic(new float[16_000]);
        var transcriber = new CountingTranscriber();
        var vm = NewVm(() => mic, transcriber);
        vm.ComposerText = "keep this";
        await vm.StartDictationCommand.ExecuteAsync(null);
        Assert.True(vm.IsRecording);
        vm.CancelDictationCommand.Execute(null);
        Assert.False(vm.IsDictating);
        Assert.False(vm.IsDictationBarVisible);
        await Until(() => mic.Disposed, "the microphone released");
        Assert.False(mic.Stopped); // the audio was never read
        Assert.Equal(0, transcriber.Calls);
        Assert.Equal("keep this", vm.ComposerText);

        // Cancelled while transcribing: the late result is dropped too.
        var gated = new GatedTranscriber();
        var vm2 = NewVm(() => new LevelMic(new float[16_000]), gated);
        vm2.ComposerText = "draft";
        await vm2.StartDictationCommand.ExecuteAsync(null);
        var finishing = vm2.FinishDictationCommand.ExecuteAsync(null);
        await Until(() => gated.Started.CurrentCount > 0, "decode started");
        Assert.True(vm2.IsTranscribing && vm2.IsDictating);
        vm2.CancelDictationCommand.Execute(null);
        Assert.False(vm2.IsDictating);
        gated.Release.Release();
        await finishing;
        Assert.Equal("draft", vm2.ComposerText);
        Assert.False(vm2.HasDictationError);
    }

    [AvaloniaFact]
    public async Task Finish_inserts_at_the_caret_and_never_sends()
    {
        var s = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("dm-sessions")), new LaunchRequest(TestProcesses.TempDir("dm-project")));
        var transcriber = new CountingTranscriber("dictated text");
        var vm = NewVm(() => new LevelMic(new float[16_000]), transcriber, s);
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        var rows = vm.Rows.Count;

        vm.ComposerText = "hello world";
        vm.ComposerCaretIndex = 5; // after "hello"
        await vm.ToggleDictationCommand.ExecuteAsync(null); // Ctrl/⌘+Shift+Space: starts
        Assert.True(vm.IsRecording);
        await vm.ToggleDictationCommand.ExecuteAsync(null); // again: finishes
        Assert.False(vm.IsDictating);
        Assert.Equal("hello dictated text world", vm.ComposerText);
        Assert.Equal("hello dictated text".Length, vm.ComposerCaretIndex);
        Assert.Equal(1, transcriber.Calls);

        // Caret unknown: appended with a space.
        vm.ComposerCaretIndex = -1;
        await vm.StartDictationCommand.ExecuteAsync(null);
        await vm.FinishDictationCommand.ExecuteAsync(null);
        Assert.Equal("hello dictated text world dictated text", vm.ComposerText);

        // Nothing was sent: no user message, the agent is idle, the text waits in the composer.
        await Pump(500);
        Assert.Equal(rows, vm.Rows.Count);
        Assert.DoesNotContain(vm.Rows, r => r is UserRowViewModel);
        Assert.Equal(SessionPhase.Ready, vm.Phase);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task No_speech_and_failures_stay_in_the_bar_until_dismissed()
    {
        var vm = NewVm(() => new LevelMic(new float[16_000]), new CountingTranscriber(""));
        vm.ComposerText = "x";
        await vm.StartDictationCommand.ExecuteAsync(null);
        await vm.FinishDictationCommand.ExecuteAsync(null);
        Assert.False(vm.IsDictating);
        Assert.Equal("No speech recognized.", vm.DictationError);
        Assert.True(vm.IsDictationBarVisible);
        Assert.Equal("x", vm.ComposerText);
        vm.CancelDictationCommand.Execute(null); // X / Esc dismisses
        Assert.False(vm.IsDictationBarVisible);

        // A missing model at decode time is reported, not thrown.
        var broken = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs())
        {
            SpeechModelInstalled = _ => true,
            MicrophoneFactory = () => new LevelMic(new float[16_000]),
            TranscriberFactory = _ => throw new InvalidOperationException("model file missing"),
        };
        await broken.StartDictationCommand.ExecuteAsync(null);
        await broken.FinishDictationCommand.ExecuteAsync(null);
        Assert.Equal("Dictation failed: model file missing", broken.DictationError);

        // Without the model, starting asks to download it (no microphone, no bar).
        var noModel = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs())
        {
            SpeechModelInstalled = _ => false,
            MicrophoneFactory = () => throw new InvalidOperationException("must not open the microphone"),
        };
        await noModel.ToggleDictationCommand.ExecuteAsync(null);
        Assert.True(noModel.IsDictationOfferOpen);
        Assert.False(noModel.IsDictationBarVisible);
    }

    private static (Window w, DictationBar bar) Host(MainViewModel vm)
    {
        var bar = new DictationBar { DataContext = vm };
        bar.Bind(Visual.IsVisibleProperty, new Avalonia.Data.Binding(nameof(MainViewModel.IsDictationBarVisible)));
        var w = new Window { Width = 640, Height = 120, Content = new Border { Padding = new Avalonia.Thickness(16), Child = bar }, DataContext = vm };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, bar);
    }

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(ShotDir);
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static T Part<T>(DictationBar bar, string name) where T : Control =>
        bar.FindControl<T>(name) ?? throw new InvalidOperationException("no part " + name);

    [AvaloniaFact]
    public async Task The_bar_renders_recording_transcribing_and_error_states()
    {
        var gated = new GatedTranscriber();
        var mic = new LevelMic(new float[16_000]) { Level = 0.05 };
        var vm = NewVm(() => mic, gated);
        var (w, bar) = Host(vm);
        Assert.False(bar.IsVisible);

        // Recording: waveform + elapsed + an enabled Finish; the bar takes focus for Esc / Enter.
        await vm.StartDictationCommand.ExecuteAsync(null);
        await Pump(700);
        Assert.True(bar.IsVisible);
        var wave = Part<Waveform>(bar, "Wave");
        Assert.True(wave.IsEffectivelyVisible);
        Assert.Equal(28, wave.Bounds.Height);
        Assert.Equal(32 * 6 - 3, wave.Bounds.Width);
        Assert.Equal(4, wave.BarHeight(0));
        Assert.Equal(28, wave.BarHeight(1));
        Assert.True(Part<TextBlock>(bar, "Elapsed").IsEffectivelyVisible);
        Assert.False(Part<Control>(bar, "TranscribingPanel").IsEffectivelyVisible);
        Assert.False(Part<Control>(bar, "ErrorPanel").IsEffectivelyVisible);
        Assert.True(Part<Button>(bar, "FinishButton").IsEffectivelyEnabled);
        Assert.True(bar.IsKeyboardFocusWithin);
        Shot(w, "dictation-recording");

        // Transcribing: "Transcribing…" in place of the waveform; Finish disabled.
        var finishing = vm.FinishDictationCommand.ExecuteAsync(null);
        await Until(() => gated.Started.CurrentCount > 0, "decode started");
        Assert.True(Part<Control>(bar, "TranscribingPanel").IsEffectivelyVisible);
        Assert.False(wave.IsEffectivelyVisible);
        Assert.False(Part<TextBlock>(bar, "Elapsed").IsEffectivelyVisible);
        Assert.False(Part<Button>(bar, "FinishButton").IsEffectivelyEnabled);
        Shot(w, "dictation-transcribing");
        gated.Release.Release();
        await finishing;
        Dispatcher.UIThread.RunJobs();
        Assert.False(bar.IsVisible);
        Assert.Equal("late text", vm.ComposerText);
        w.Close();

        // Error: the message inline, Try again in place of Finish; Esc dismisses it.
        var vm2 = NewVm(() => new NoMic());
        var (w2, bar2) = Host(vm2);
        await vm2.StartDictationCommand.ExecuteAsync(null);
        await Pump(100);
        Assert.True(bar2.IsVisible);
        Assert.True(Part<Control>(bar2, "ErrorPanel").IsEffectivelyVisible);
        Assert.Equal("No microphone found.", Part<TextBlock>(bar2, "ErrorText").Text);
        Assert.True(Part<Button>(bar2, "RetryButton").IsEffectivelyVisible);
        Assert.False(Part<Button>(bar2, "FinishButton").IsEffectivelyVisible);
        Assert.False(Part<Waveform>(bar2, "Wave").IsEffectivelyVisible);
        Shot(w2, "dictation-error");
        w2.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm2.HasDictationError);
        Assert.False(bar2.IsVisible);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task Keys_toggle_finish_and_cancel_and_enter_never_reaches_the_composer()
    {
        var mic = new LevelMic(new float[16_000]);
        var transcriber = new CountingTranscriber("spoken");
        var vm = NewVm(() => mic, transcriber);
        var composer = new TextBox { AcceptsReturn = false };
        var bar = new DictationBar { DataContext = vm };
        bar.Bind(Visual.IsVisibleProperty, new Avalonia.Data.Binding(nameof(MainViewModel.IsDictationBarVisible)));
        var w = new Window { Width = 640, Height = 160, DataContext = vm, Content = new StackPanel { Children = { composer, bar } } };
        // The host's contract: the window's tunnel KeyDown hands keys to DictationBar.HandleKey first.
        w.AddHandler(InputElement.KeyDownEvent, (_, e) => DictationBar.HandleKey(vm, e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        w.Show();
        composer.Focus();
        var command = DictationBar.CommandModifier == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control;

        w.KeyPress(Key.Space, command | RawInputModifiers.Shift, PhysicalKey.Space, null);
        await Until(() => vm.IsRecording, "recording");
        await Pump(100);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => !vm.IsDictating, "finished");
        Assert.Equal("spoken", vm.ComposerText);

        w.KeyPress(Key.Space, command | RawInputModifiers.Shift, PhysicalKey.Space, null);
        await Until(() => vm.IsRecording, "recording again");
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsDictating);
        Assert.Equal(1, transcriber.Calls);
        Assert.Equal("spoken", vm.ComposerText);
        Assert.True(DictationBar.IsToggleGesture(Key.Space, DictationBar.CommandModifier | KeyModifiers.Shift));
        Assert.False(DictationBar.IsToggleGesture(Key.Space, KeyModifiers.Shift));
        w.Close();
    }

    /// <summary>In the main window: the bar takes the message field's place, Esc cancels, the field gets focus back.</summary>
    [AvaloniaFact]
    public async Task In_the_window_the_bar_replaces_the_field_and_Esc_returns_to_it()
    {
        var mic = new LevelMic(Sine(0.4, 16_000));
        var vm = NewVm(() => mic);
        var w = new OmpGui.App.Views.MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        var composer = w.FindControl<TextBox>("Composer")!;
        var bar = w.FindControl<DictationBar>("DictationBar")!;
        Assert.True(composer.IsEffectivelyVisible);
        Assert.False(bar.IsEffectivelyVisible);

        w.FindControl<Button>("DictateButton")!.Command!.Execute(null);
        await Until(() => vm.IsRecording && bar.IsEffectivelyVisible, "recording in the bar");
        Assert.False(composer.IsEffectivelyVisible);
        await Pump(400);
        Shot(w, "dictation-in-window");

        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Until(() => !vm.IsDictating && composer.IsEffectivelyVisible && composer.IsFocused, "cancelled, back to typing");
        Assert.Equal("", vm.ComposerText ?? "");
        await vm.DisposeAsync();
        w.Close();
    }
}

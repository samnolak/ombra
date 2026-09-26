using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.Services.Speech;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// Real dictation engine: downloads the pinned model with the client's own downloader (real SHA-256 checks), then
/// transcribes a sample recording. Runs only when OMPGUI_SPEECH_MODEL_DIR is set (CI job "speech"; ~670 MB).
/// </summary>
public sealed class RealSpeechTests
{
    [Fact]
    public async Task Downloads_the_pinned_model_and_transcribes_a_sample()
    {
        if (Environment.GetEnvironmentVariable("OMPGUI_SPEECH_MODEL_DIR") is not { Length: > 0 } dir)
        {
            Assert.Skip("OMPGUI_SPEECH_MODEL_DIR is not set");
            return;
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        await new SpeechModelDownloader(http, dir).DownloadAsync(null, CancellationToken.None);
        Assert.True(SpeechModel.IsInstalled(dir));

        var wav = Environment.GetEnvironmentVariable("OMPGUI_SPEECH_WAV");
        if (wav is null || !File.Exists(wav))
        {
            Assert.Skip("no sample recording (OMPGUI_SPEECH_WAV)");
            return;
        }
        var (samples, rate) = ReadWav(wav);
        using var transcriber = new Transcriber(dir);
        var text = transcriber.Transcribe(AudioSegmentation.Resample(samples, rate, SpeechModel.SampleRate));
        TestContext.Current.TestOutputHelper?.WriteLine("transcript: " + text);
        Assert.True(text.Count(char.IsLetter) >= 10, "transcript too short: " + text);
    }

    /// <summary>A microphone that "hears" a recording (16 kHz mono), with a level like a real one.</summary>
    private sealed class RecordingMic(float[] samples) : IMicrophone
    {
        private readonly DateTime _start = DateTime.UtcNow;
        public void Start() { }
        public TimeSpan Recorded => DateTime.UtcNow - _start;
        public float[] Stop() => samples;
        public double? ReadLevel() => 0.3;
        public void Dispose() { }
    }

    /// <summary>
    /// The whole dictation path of the app with the real engine: the mic button starts dictation, finishing it
    /// transcribes the recording with the pinned model and puts the text into the message box (nothing is sent). Only
    /// the microphone device is replaced (a recording of speech from the model's repository).
    /// </summary>
    [AvaloniaFact]
    public async Task Dictation_in_the_app_puts_the_transcript_into_the_message_box()
    {
        if (Environment.GetEnvironmentVariable("OMPGUI_SPEECH_MODEL_DIR") is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable("OMPGUI_SPEECH_WAV") is not { } wav || !File.Exists(wav))
        {
            Assert.Skip("OMPGUI_SPEECH_MODEL_DIR / OMPGUI_SPEECH_WAV are not set");
            return;
        }
        if (!SpeechModel.IsInstalled(dir))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            await new SpeechModelDownloader(http, dir).DownloadAsync(null, CancellationToken.None);
        }
        var (samples, rate) = ReadWav(wav);
        var speech = AudioSegmentation.Resample(samples, rate, SpeechModel.SampleRate);
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs())
        {
            SpeechModelDirectory = dir,
            MicrophoneFactory = () => new RecordingMic(speech),
        };
        vm.OnWindowOpened();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        await vm.ToggleDictationCommand.ExecuteAsync(null); // the mic button: recording
        Assert.True(vm.IsRecording);
        await Task.Delay(300);
        await vm.ToggleDictationCommand.ExecuteAsync(null); // again: transcribe and insert
        deadline = DateTime.UtcNow.AddMinutes(3);
        while (vm.IsDictating && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(50); }
        TestContext.Current.TestOutputHelper?.WriteLine("message box: " + vm.ComposerText);
        Assert.False(vm.IsDictating);
        Assert.True((vm.ComposerText ?? "").Count(char.IsLetter) >= 10, "no transcript in the message box: " + vm.ComposerText);
        Assert.DoesNotContain(vm.Rows, r => r is UserRowViewModel); // dictation never sends
        await vm.DisposeAsync();
    }

    /// <summary>PCM 16-bit WAV, any channel count (first channel used).</summary>
    private static (float[] Samples, int Rate) ReadWav(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a RIFF file");
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAVE file");
        int rate = 0, channels = 1, bits = 16;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            var id = new string(r.ReadChars(4));
            var size = r.ReadInt32();
            if (id == "fmt ")
            {
                r.ReadInt16();
                channels = r.ReadInt16();
                rate = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt16();
                bits = r.ReadInt16();
                r.BaseStream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                if (bits != 16) throw new InvalidDataException($"{bits}-bit WAV not supported");
                var frames = size / (2 * channels);
                var samples = new float[frames];
                for (var i = 0; i < frames; i++)
                {
                    samples[i] = r.ReadInt16() / 32768f;
                    for (var c = 1; c < channels; c++) r.ReadInt16();
                }
                return (samples, rate);
            }
            else r.BaseStream.Seek(size, SeekOrigin.Current);
        }
        throw new InvalidDataException("no data chunk");
    }
}

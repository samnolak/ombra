using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Services.Speech;

namespace OmpGui.App.ViewModels;

/// <summary>
/// Dictation into the composer, on this computer: record with the microphone, transcribe with the pinned local model,
/// insert the text. The model (~670 MB) is downloaded only when the user agrees, and verified.
/// </summary>
/// <remarks>
/// Dictation mode (<c>Controls/DictationBar</c>) replaces the composer's text area from <see cref="StartDictationCommand"/>
/// until the text is inserted (<see cref="FinishDictationCommand"/>) or the audio discarded
/// (<see cref="CancelDictationCommand"/>); an error keeps the bar open until it is dismissed or retried. Nothing is ever
/// sent automatically.
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>Waveform history: this many latest levels are kept in <see cref="DictationLevels"/>.</summary>
    public const int DictationLevelCount = 32;
    /// <summary>How often the level and the elapsed time are read while recording (~16 per second).</summary>
    internal static readonly TimeSpan DictationTick = TimeSpan.FromMilliseconds(60);

    /// <summary>Seams for tests; the defaults are the real microphone, model and network.</summary>
    public Func<IMicrophone> MicrophoneFactory { get; init; } = () => new AutoMicrophone();
    public Func<string, ITranscriber> TranscriberFactory { get; init; } = dir => new Transcriber(dir);
    public string SpeechModelDirectory { get; init; } = SpeechModel.DefaultDirectory;
    public Func<string, bool> SpeechModelInstalled { get; init; } = SpeechModel.IsInstalled;
    public Func<string, SpeechModelDownloader> DownloaderFactory { get; init; } = dir => new SpeechModelDownloader(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, dir);

    private IMicrophone? _microphone;
    private ITranscriber? _transcriber;
    /// <summary>The decode in progress: the native recognizer must outlive it (see <see cref="DisposeDictationAsync"/>).</summary>
    private Task? _transcription;
    private bool _dictationDisposed;
    private bool _micStarting;
    /// <summary>Bumped by every cancel: a stop or decode still running for an older take drops its result.</summary>
    private int _dictationTake;
    private CancellationTokenSource? _downloadCts;
    private DispatcherTimer? _dictationTimer;
    private int _elapsedSeconds = -1;

    [ObservableProperty] private bool _isDictationOfferOpen;
    [ObservableProperty] private bool _isDownloadingModel;
    [ObservableProperty] private string _downloadText = "";

    /// <summary>The microphone is being opened (a system recorder is given up to 2 s); the bar already shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDictating), nameof(IsDictationBarVisible), nameof(ShowDictationWaveform))]
    private bool _isStartingMicrophone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDictating), nameof(IsDictationBarVisible), nameof(ShowDictationWaveform), nameof(DictationTip))]
    [NotifyCanExecuteChangedFor(nameof(FinishDictationCommand))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDictating), nameof(IsDictationBarVisible))]
    private bool _isTranscribing;

    /// <summary>Elapsed recording time as m:ss (the toolbar mic button).</summary>
    [ObservableProperty] private string _recordingText = "";

    /// <summary>Elapsed recording time as mm:ss (the dictation bar).</summary>
    [ObservableProperty] private string _dictationElapsed = "00:00";

    /// <summary>Why dictation stopped or found nothing; shown in the bar until dismissed (Cancel / Esc) or retried.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDictationError), nameof(IsDictationBarVisible))]
    private string _dictationError = "";

    /// <summary>
    /// Caret position in the composer, kept by the view (TwoWay binding to <c>TextBox.CaretIndex</c>): dictated text is
    /// inserted there. -1 or out of range: appended.
    /// </summary>
    [ObservableProperty] private int _composerCaretIndex = -1;

    /// <summary>The latest input levels (0..1, oldest first, at most <see cref="DictationLevelCount"/>), for the waveform.</summary>
    public ObservableCollection<double> DictationLevels { get; } = [];

    /// <summary>Recording (including opening the microphone) or transcribing.</summary>
    public bool IsDictating => IsStartingMicrophone || IsRecording || IsTranscribing;
    public bool HasDictationError => DictationError.Length > 0;
    /// <summary>The dictation bar replaces the composer's text area: while dictating, and while an error is shown.</summary>
    public bool IsDictationBarVisible => IsDictating || HasDictationError;
    /// <summary>The waveform and the elapsed time show (opening the microphone, recording).</summary>
    public bool ShowDictationWaveform => IsStartingMicrophone || IsRecording;

    public string ModelSizeText => string.Create(CultureInfo.InvariantCulture, $"{SpeechModel.TotalBytes / 1_000_000.0:0} MB");
    public string DictationTip => IsRecording ? "Stop and insert the text (Enter)" : "Dictate on this computer (Ctrl/⌘+Shift+Space)";

    /// <summary>The toolbar mic button: starts dictation, or finishes it while recording.</summary>
    [RelayCommand]
    private Task DictateAsync() => ToggleDictationAsync();

    /// <summary>Ctrl/⌘+Shift+Space: starts dictation, or finishes it while recording.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleDictationAsync()
    {
        if (IsRecording) return FinishDictationAsync();
        if (IsDictating) return Task.CompletedTask; // opening the microphone or transcribing: wait for it
        return StartDictationAsync();
    }

    /// <summary>Opens the microphone and records. Without the speech model, asks to download it first.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartDictationAsync()
    {
        if (IsDictating || IsDownloadingModel || _micStarting) return;
        DictationError = "";
        if (!SpeechModelInstalled(SpeechModelDirectory))
        {
            IsDictationOfferOpen = true; // asks before downloading anything
            return;
        }
        var mic = MicrophoneFactory();
        var take = _dictationTake;
        _micStarting = true;
        DictationLevels.Clear();
        _elapsedSeconds = -1;
        ShowElapsed(TimeSpan.Zero);
        IsStartingMicrophone = true;
        try
        {
            // Off the UI thread: a system recorder is given up to 2 s to deliver sound or fail.
            await Task.Run(mic.Start);
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or PortAudioSharp.PortAudioException)
        {
            mic.Dispose();
            _micStarting = false;
            if (take == _dictationTake) DictationError = Capitalize(MicrophoneError(e));
            IsStartingMicrophone = false;
            return;
        }
        _micStarting = false;
        if (_dictationDisposed || take != _dictationTake)
        {
            // Cancelled (or the window closed) while the microphone was opening.
            _ = Task.Run(mic.Dispose);
            if (take == _dictationTake) IsStartingMicrophone = false;
            return;
        }
        _microphone = mic;
        IsRecording = true; // before clearing "starting", so the bar does not blink
        IsStartingMicrophone = false;
        _dictationTimer = new DispatcherTimer(DictationTick, DispatcherPriority.Background, (_, _) => OnDictationTick());
        _dictationTimer.Start();
    }

    private bool CanFinishDictation() => IsRecording;

    /// <summary>Stops recording, transcribes, and inserts the text into the composer (at the caret). Never sends.</summary>
    [RelayCommand(CanExecute = nameof(CanFinishDictation), AllowConcurrentExecutions = true)]
    private async Task FinishDictationAsync()
    {
        if (!IsRecording || _microphone is not { } mic) return;
        StopDictationTimer();
        _microphone = null;
        var take = _dictationTake;
        IsTranscribing = true; // before clearing "recording", so the bar does not blink
        IsRecording = false;
        try
        {
            var dir = SpeechModelDirectory;
            var previous = _transcription; // a cancelled take may still be decoding: one decode at a time
            // Off the UI thread: a system recorder is killed and drained (up to a few seconds), then the decode.
            var decode = Task.Run(async () =>
            {
                float[] samples;
                try { samples = mic.Stop(); }
                finally { mic.Dispose(); }
                if (previous is not null) await Task.WhenAny(previous); // its outcome was handled by its own take
                if (_dictationDisposed || take != Volatile.Read(ref _dictationTake)) return ""; // cancelled: not decoded
                _transcriber ??= TranscriberFactory(dir);
                return _transcriber.Transcribe(samples);
            });
            _transcription = decode;
            var text = (await decode).Trim();
            if (take != _dictationTake) return; // cancelled meanwhile: discarded
            if (text.Length > 0) InsertDictatedText(text);
            else DictationError = "No speech recognized.";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or ArgumentException or PortAudioSharp.PortAudioException)
        {
            if (take == _dictationTake) DictationError = "Dictation failed: " + e.Message;
        }
        finally
        {
            if (take == _dictationTake) IsTranscribing = false;
        }
    }

    /// <summary>Stops recording and discards the audio (or a transcription still running); dismisses an error.</summary>
    [RelayCommand]
    private void CancelDictation()
    {
        Interlocked.Increment(ref _dictationTake);
        StopDictationTimer();
        if (_microphone is { } mic)
        {
            _microphone = null;
            _ = Task.Run(mic.Dispose); // a system recorder takes a moment to die; its audio is never read
        }
        IsStartingMicrophone = false;
        IsRecording = false;
        IsTranscribing = false;
        DictationError = "";
        DictationLevels.Clear();
    }

    /// <summary>Inserts at <see cref="ComposerCaretIndex"/> (or appends), with a space on either side where needed.</summary>
    internal void InsertDictatedText(string text)
    {
        var current = ComposerText;
        var at = ComposerCaretIndex is var c && c >= 0 && c <= current.Length ? c : current.Length;
        var before = current[..at];
        var after = current[at..];
        var lead = before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? " " : "";
        var trail = after.Length > 0 && !char.IsWhiteSpace(after[0]) ? " " : "";
        ComposerText = before + lead + text + trail + after;
        ComposerCaretIndex = at + lead.Length + text.Length + trail.Length;
    }

    private void OnDictationTick()
    {
        if (_microphone is not { } mic) return;
        PushDictationLevel(LevelMeter.ToDisplay(mic.ReadLevel() ?? 0));
        ShowElapsed(mic.Recorded);
    }

    /// <summary>Appends one level, dropping the oldest beyond <see cref="DictationLevelCount"/>.</summary>
    internal void PushDictationLevel(double level)
    {
        if (DictationLevels.Count >= DictationLevelCount) DictationLevels.RemoveAt(0);
        DictationLevels.Add(Math.Clamp(level, 0, 1));
    }

    /// <summary>Updates the elapsed texts once per second (no string per tick).</summary>
    private void ShowElapsed(TimeSpan recorded)
    {
        var seconds = (int)recorded.TotalSeconds;
        if (seconds == _elapsedSeconds) return;
        _elapsedSeconds = seconds;
        DictationElapsed = string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}");
        RecordingText = string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    private void StopDictationTimer()
    {
        _dictationTimer?.Stop();
        _dictationTimer = null;
    }

    private static string Capitalize(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>One readable line: the loader's message lists every path it tried, the user needs the missing library.</summary>
    internal static string MicrophoneError(Exception e)
    {
        if (e is InvalidOperationException) return e.Message;
        if (e is DllNotFoundException or TypeInitializationException { InnerException: DllNotFoundException })
        {
            var text = (e as DllNotFoundException ?? (DllNotFoundException)e.InnerException!).Message;
            var missing = System.Text.RegularExpressions.Regex.Matches(text, @"([\w.+-]+\.(?:so[.\d]*|dylib|dll)): cannot open shared object file")
                .Select(m => m.Groups[1].Value).FirstOrDefault(n => !n.Contains("portaudio", StringComparison.OrdinalIgnoreCase));
            return missing is not null
                ? $"the audio library needs {missing}, which is not installed on this system (for example libjack0 / jack-audio-connection-kit)."
                : "the audio library could not be loaded on this system.";
        }
        return "the microphone could not be opened (" + e.Message.Split('\n')[0] + ").";
    }

    [RelayCommand]
    private async Task DownloadSpeechModelAsync()
    {
        if (IsDownloadingModel) return;
        IsDownloadingModel = true;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<(long Done, long Total)>(p =>
            DownloadText = string.Create(CultureInfo.InvariantCulture, $"{p.Done / 1_000_000.0:0} of {p.Total / 1_000_000.0:0} MB"));
        try
        {
            await DownloaderFactory(SpeechModelDirectory).DownloadAsync(progress, _downloadCts.Token);
            IsDictationOfferOpen = false;
            ComposerMessage = "Speech model ready. Press the microphone to dictate.";
        }
        catch (OperationCanceledException)
        {
            DownloadText = "Download cancelled.";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            DownloadText = "Download failed: " + e.Message;
        }
        finally
        {
            IsDownloadingModel = false;
            _downloadCts.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelSpeechDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private void CloseDictationOffer()
    {
        _downloadCts?.Cancel();
        IsDictationOfferOpen = false;
    }

    /// <summary>Window closing: stops recording and downloads, and frees the recognizer only after a running decode ended.</summary>
    private async Task DisposeDictationAsync()
    {
        _dictationDisposed = true;
        StopDictationTimer();
        _microphone?.Dispose();
        _microphone = null;
        _downloadCts?.Cancel();
        if (_transcription is { IsCompleted: false } running)
        {
            // Decoding cannot be interrupted; freeing the native recognizer under it would crash. Wait (bounded).
            try { await running.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception e) when (e is TimeoutException or InvalidOperationException or ArgumentException) { }
            if (!running.IsCompleted) return; // still decoding: leak the recognizer rather than free it in use
        }
        _transcriber?.Dispose();
        _transcriber = null;
    }
}

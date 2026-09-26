using SherpaOnnx;

namespace OmpGui.App.Services.Speech;

/// <summary>Offline speech recognition with sherpa-onnx on the CPU. Not thread-safe: one transcription at a time.</summary>
public interface ITranscriber : IDisposable
{
    string Transcribe(float[] samples);
}

public sealed class Transcriber : ITranscriber
{
    private readonly OfflineRecognizer _recognizer;

    public Transcriber(string modelDirectory)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SpeechModel.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = SpeechModel.PathOf(modelDirectory, "encoder");
        config.ModelConfig.Transducer.Decoder = SpeechModel.PathOf(modelDirectory, "decoder");
        config.ModelConfig.Transducer.Joiner = SpeechModel.PathOf(modelDirectory, "joiner");
        config.ModelConfig.Tokens = SpeechModel.PathOf(modelDirectory, "tokens");
        config.ModelConfig.ModelType = SpeechModel.ModelType;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount, 1, 4);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
    }

    /// <summary>Text of 16 kHz mono audio, decoded utterance by utterance.</summary>
    public string Transcribe(float[] samples)
    {
        var parts = new List<string>();
        foreach (var (a, b) in AudioSegmentation.SplitUtterances(samples))
        {
            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(SpeechModel.SampleRate, samples[a..b]);
            _recognizer.Decode(stream);
            if (stream.Result.Text.Trim() is { Length: > 0 } text) parts.Add(text);
        }
        return string.Join(" ", parts);
    }

    public void Dispose() => _recognizer.Dispose();
}

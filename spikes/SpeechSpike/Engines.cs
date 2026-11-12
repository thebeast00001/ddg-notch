using System.Net.Http.Headers;
using System.Text.Json;
using SherpaOnnx;

namespace SpeechSpike;

/// <summary>The one interface every speech engine sits behind, local or cloud.</summary>
interface ISpeechEngine : IDisposable
{
    string Name { get; }
    string Transcribe(Clip clip);
    /// <summary>Gets ready for an utterance that is about to arrive, such as opening a connection. Returns at once.</summary>
    void Warm() { }
}

/// <summary>A model running on this PC through sherpa-onnx, on the CPU.</summary>
sealed class LocalEngine : ISpeechEngine
{
    readonly OfflineRecognizer _recognizer;
    public string Name { get; }

    public LocalEngine(string name, OfflineRecognizerConfig config)
    {
        Name = name;
        _recognizer = new OfflineRecognizer(config);
    }

    public string Transcribe(Clip clip)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(clip.SampleRate, clip.Samples);
        _recognizer.Decode(stream);
        return stream.Result.Text.Trim();
    }

    public void Dispose() => _recognizer.Dispose();
}

/// <summary>One downloadable model: where it lives, which languages it covers, how to configure it.</summary>
sealed record LocalModel(string Key, string Label, string[] Languages, int[] ThreadSweep,
                         Func<string, string, OfflineRecognizerConfig?> Configure)
{
    public string Folder(string modelsRoot) => Path.Combine(modelsRoot, Key);

    public bool IsPresent(string modelsRoot) =>
        Directory.Exists(Folder(modelsRoot)) && Configure(Folder(modelsRoot), Languages[0]) != null;

    public LocalEngine? Create(string modelsRoot, string language, int threads)
    {
        if (Configure(Folder(modelsRoot), language) is not { } config) return null;
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return new LocalEngine(Label, config);
    }

    static string? Find(string folder, params string[] patterns)
    {
        if (!Directory.Exists(folder)) return null;
        foreach (string pattern in patterns)
        {
            string? hit = Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories)
                .Where(f => !f.Contains("test_wavs", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Length).FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    public static readonly LocalModel[] All =
    {
        new("parakeet-v3", "Parakeet TDT 0.6b v3 (int8)", new[] { "en" }, new[] { 1, 2, 4, 8 }, (dir, _) =>
        {
            string? encoder = Find(dir, "encoder*.int8.onnx", "encoder*.onnx"), decoder = Find(dir, "decoder*.int8.onnx", "decoder*.onnx"),
                    joiner = Find(dir, "joiner*.int8.onnx", "joiner*.onnx"), tokens = Find(dir, "tokens.txt");
            if (encoder == null || decoder == null || joiner == null || tokens == null) return null;
            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Transducer.Encoder = encoder;
            config.ModelConfig.Transducer.Decoder = decoder;
            config.ModelConfig.Transducer.Joiner = joiner;
            config.ModelConfig.Tokens = tokens;
            config.ModelConfig.ModelType = "nemo_transducer";
            return config;
        }),
        new("moonshine-base-en", "Moonshine base v2, English (quantized)", new[] { "en" }, new[] { 1, 2, 4, 8 }, (dir, _) =>
        {
            string? encoder = Find(dir, "encoder_model.ort"), decoder = Find(dir, "decoder_model_merged.ort"), tokens = Find(dir, "tokens.txt");
            if (encoder == null || decoder == null || tokens == null) return null;
            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Moonshine.Encoder = encoder;
            config.ModelConfig.Moonshine.MergedDecoder = decoder;
            config.ModelConfig.Tokens = tokens;
            return config;
        }),
        new("omnilingual-300m", "Omnilingual 300M CTC v2 (int8)", new[] { "en", "hi" }, new[] { 1, 2, 4, 8 }, (dir, _) =>
        {
            string? model = Find(dir, "model.int8.onnx", "model*.onnx"), tokens = Find(dir, "tokens.txt");
            if (model == null || tokens == null) return null;
            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Omnilingual.Model = model;
            config.ModelConfig.Tokens = tokens;
            return config;
        }),
        new("whisper-turbo", "Whisper large-v3-turbo (int8)", new[] { "en", "hi" }, new[] { 4, 8 }, (dir, language) =>
        {
            string? encoder = Find(dir, "*encoder*.int8.onnx", "*encoder*.onnx"), decoder = Find(dir, "*decoder*.int8.onnx", "*decoder*.onnx"),
                    tokens = Find(dir, "*tokens.txt");
            if (encoder == null || decoder == null || tokens == null) return null;
            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Whisper.Encoder = encoder;
            config.ModelConfig.Whisper.Decoder = decoder;
            config.ModelConfig.Whisper.Language = language;
            config.ModelConfig.Whisper.Task = "transcribe";
            config.ModelConfig.Tokens = tokens;
            return config;
        }),
    };
}

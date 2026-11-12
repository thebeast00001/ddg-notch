using System.Net.Http.Headers;
using System.Text.Json;

namespace SpeechSpike;

/// <summary>
/// Sarvam's hosted speech-to-text. First choice for Hindi and Hinglish dictation: on the test clips
/// it had the fewest errors (2.5% of words) and answered in about half a second from India.
/// </summary>
sealed class SarvamEngine : ISpeechEngine
{
    readonly string _key, _languageCode, _mode;

    public string Name => $"Sarvam saaras:v4, {_mode} (cloud)";

    public SarvamEngine(string key, string language, string mode = "transcribe")
    {
        _key = key;
        _languageCode = language == "hi" ? "hi-IN" : "en-IN";
        _mode = mode;
    }

    public static string? FindKey() => Keys.Find(Keys.Sarvam);

    public void Warm() => Net.Warm("https://api.sarvam.ai/");

    public string Transcribe(Clip clip)
    {
        Fault.Apply("sarvam", "Sarvam");
        // This endpoint takes at most 30 seconds of audio. Longer speech is cut at pauses and the
        // pieces are sent together, so a long dictation takes about as long as a short one.
        if (clip.Seconds <= 28) return One(clip);
        var pieces = Speech.Segments(clip, maxSeconds: 24);
        if (pieces.Count == 0) return "";
        var texts = new string[pieces.Count];
        Parallel.For(0, pieces.Count, new ParallelOptions { MaxDegreeOfParallelism = 3 },
            i => texts[i] = One(Speech.Slice(clip, pieces[i].Start, pieces[i].End)));
        return string.Join(" ", texts.Where(t => t.Length > 0));
    }

    string One(Clip clip)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(clip.ToWavBytes());
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "clip.wav");
        form.Add(new StringContent("saaras:v4"), "model");
        form.Add(new StringContent(_mode), "mode");
        form.Add(new StringContent(_languageCode), "language_code");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sarvam.ai/speech-to-text") { Content = form };
        request.Headers.Add("api-subscription-key", _key);
        var (status, body) = Net.Send(request, timeoutSeconds: 6 + clip.Seconds * 0.5, "Sarvam");

        if (status is < 200 or >= 300)
        {
            string code = "", message = body.Length > 200 ? body[..200] : body;
            try
            {
                using var error = JsonDocument.Parse(body);
                if (error.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
                {
                    code = e.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                    message = e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : message;
                }
            }
            catch (JsonException) { }
            throw new ServiceException("Sarvam", $"Sarvam returned {status} {code}: {message}", retryElsewhere: status is not (401 or 403), status);
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("transcript", out var transcript) ? (transcript.GetString() ?? "").Trim() : "";
        }
        catch (JsonException) { throw new ServiceException("Sarvam", "Sarvam answered in a shape that could not be read", retryElsewhere: true); }
    }

    public void Dispose() { }
}

/// <summary>One word, gap or sound from ElevenLabs, with when it was said and by which voice.</summary>
sealed record ScribeWord(string Text, double Start, double End, string Kind, string? Speaker);

/// <summary>
/// ElevenLabs' hosted speech-to-text (Scribe). Used for two things: as the stand-in when Sarvam
/// is slow or down, and for call audio, because it can tell the voices in a recording apart.
/// Audio is sent as bare 16 kHz PCM, the format their docs describe as needing no conversion.
/// </summary>
sealed class ElevenLabsEngine : ISpeechEngine
{
    readonly string _key, _model;
    readonly string? _language;

    public string Name => $"ElevenLabs {_model} (cloud)";

    /// <param name="language">"en" or "hi", or null to let the service work it out.</param>
    public ElevenLabsEngine(string key, string? language, string model = "scribe_v3")
    {
        _key = key;
        _language = language;
        _model = model;
    }

    public static string? FindKey() => Keys.Find(Keys.ElevenLabs);

    public void Warm() => Net.Warm("https://api.elevenlabs.io/");

    public string Transcribe(Clip clip)
    {
        using var json = Ask(clip, speakers: false, timeoutSeconds: 8 + clip.Seconds * 0.6);
        return json.RootElement.TryGetProperty("text", out var text) ? (text.GetString() ?? "").Trim() : "";
    }

    /// <summary>The words with their times and, when <paramref name="speakers"/> is set, a label for each voice.</summary>
    public List<ScribeWord> Words(Clip clip, bool speakers)
    {
        // Long recordings are transcribed in the background on their side; allow for upload and work.
        using var json = Ask(clip, speakers, timeoutSeconds: 60 + clip.Seconds * 0.5);
        var words = new List<ScribeWord>();
        if (!json.RootElement.TryGetProperty("words", out var list) || list.ValueKind != JsonValueKind.Array) return words;
        foreach (var w in list.EnumerateArray())
        {
            string text = w.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            string kind = w.TryGetProperty("type", out var k) ? k.GetString() ?? "word" : "word";
            double start = w.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : -1;
            double end = w.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : start;
            string? speaker = w.TryGetProperty("speaker_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            words.Add(new ScribeWord(text, start, end, kind, speaker));
        }
        return words;
    }

    JsonDocument Ask(Clip clip, bool speakers, double timeoutSeconds)
    {
        Fault.Apply("eleven", "ElevenLabs");
        if (clip.SampleRate != 16000) throw new ArgumentException("ElevenLabs engine expects 16 kHz audio.");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(clip.ToPcm16Bytes());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "clip.pcm");
        form.Add(new StringContent(_model), "model_id");
        form.Add(new StringContent("pcm_s16le_16"), "file_format");
        if (_language != null) form.Add(new StringContent(_language), "language_code");
        form.Add(new StringContent("false"), "tag_audio_events");
        if (speakers)
        {
            form.Add(new StringContent("true"), "diarize");
            form.Add(new StringContent("word"), "timestamps_granularity");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text") { Content = form };
        request.Headers.Add("xi-api-key", _key);
        var (status, body) = Net.Send(request, timeoutSeconds, "ElevenLabs");

        if (status is < 200 or >= 300)
        {
            string detail = body.Length > 240 ? body[..240] : body;
            try
            {
                using var error = JsonDocument.Parse(body);
                if (error.RootElement.TryGetProperty("detail", out var d))
                    detail = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("message", out var m) ? m.GetString() ?? detail : d.ToString();
            }
            catch (JsonException) { }
            throw new ServiceException("ElevenLabs", $"ElevenLabs returned {status}: {detail}", retryElsewhere: status is not (401 or 403), status);
        }

        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new ServiceException("ElevenLabs", "ElevenLabs answered in a shape that could not be read", retryElsewhere: true); }
    }

    public void Dispose() { }
}

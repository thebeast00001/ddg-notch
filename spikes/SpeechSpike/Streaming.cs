using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SpeechSpike;

/// <summary>
/// An engine that listens while you speak. Audio goes up in small pieces during the utterance, so
/// that when you stop, most of the work is already done.
/// </summary>
interface IStreamingEngine
{
    string Name { get; }
    IStreamingSession Start(string language);
}

interface IStreamingSession : IDisposable
{
    /// <summary>Milliseconds from asking for a session to being able to send audio.</summary>
    double ReadyMs { get; }
    void Send(short[] pcm16k);
    /// <summary>Says the utterance is over and waits for the final text.</summary>
    string Finish();
    IReadOnlyList<string> Trace { get; }
}

abstract class WebSocketSession : IStreamingSession
{
    readonly ClientWebSocket _socket = new();
    readonly CancellationTokenSource _cancel = new();
    readonly List<string> _trace = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    protected string? Error;

    public double ReadyMs { get; protected set; }
    public IReadOnlyList<string> Trace { get { lock (_trace) return _trace.ToList(); } }

    protected WebSocketSession(string url, string keyHeader, string key)
    {
        _socket.Options.SetRequestHeader(keyHeader, key);
        _socket.ConnectAsync(new Uri(url), _cancel.Token).GetAwaiter().GetResult();
        ReadyMs = _clock.Elapsed.TotalMilliseconds;
        _ = Task.Run(ReceiveLoop);
    }

    async Task ReceiveLoop()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, _cancel.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Note($"closed {(int?)result.CloseStatus} {result.CloseStatusDescription}");
                    Error ??= $"connection closed: {result.CloseStatusDescription}";
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                Note(text.Length > 300 ? text[..300] + "…" : text);
                try { OnMessage(JsonDocument.Parse(text).RootElement); }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Error ??= ex.Message; Note("socket error " + ex.Message); }
        finally { OnClosed(); }
    }

    protected void Note(string line) { lock (_trace) _trace.Add($"{_clock.ElapsedMilliseconds,6} ms  {line}"); }

    protected void SendText(string json)
    {
        if (_socket.State != WebSocketState.Open) return;
        _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _cancel.Token).GetAwaiter().GetResult();
    }

    protected static string Base64(short[] pcm)
    {
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);   // little-endian on every Windows PC
        return Convert.ToBase64String(bytes);
    }

    protected abstract void OnMessage(JsonElement message);
    protected abstract void OnClosed();
    public abstract void Send(short[] pcm16k);
    public abstract string Finish();

    public void Dispose()
    {
        // Say goodbye properly. A connection that is simply dropped can linger on the server and
        // count against the limit on open sessions.
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var patience = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", patience.Token).GetAwaiter().GetResult();
            }
        }
        catch { }
        try { _cancel.Cancel(); _socket.Abort(); } catch { }
        _socket.Dispose();
    }
}

// ───────────────────────────── Sarvam ─────────────────────────────

sealed class SarvamStreamEngine(string key, string mode = "transcribe") : IStreamingEngine
{
    public string Name => $"Sarvam saaras:v4 streaming, {mode} (cloud)";
    public IStreamingSession Start(string language) => new SarvamStreamSession(key, language, mode);
}

/// <summary>
/// Sarvam cuts the audio at pauses and sends a transcript for each piece. A "flush" message asks for
/// whatever is left. The final text is every piece joined together.
/// </summary>
sealed class SarvamStreamSession : WebSocketSession
{
    readonly StringBuilder _text = new();
    readonly ManualResetEventSlim _afterFlush = new(false);
    volatile bool _flushed;

    public SarvamStreamSession(string key, string language, string mode)
        : base("wss://api.sarvam.ai/speech-to-text/ws" +
               $"?language-code={(language == "hi" ? "hi-IN" : "en-IN")}&model=saaras:v4&mode={mode}" +
               "&sample_rate=16000&input_audio_codec=pcm_s16le&flush_signal=true",
               "Api-Subscription-Key", key)
    {
    }

    protected override void OnMessage(JsonElement message)
    {
        string type = message.GetProperty("type").GetString() ?? "";
        if (type == "data")
        {
            string piece = message.GetProperty("data").GetProperty("transcript").GetString() ?? "";
            lock (_text) { if (piece.Length > 0) { if (_text.Length > 0) _text.Append(' '); _text.Append(piece.Trim()); } }
            if (_flushed) _afterFlush.Set();
        }
        else if (type == "error")
        {
            Error = message.GetProperty("data").ToString();
            _afterFlush.Set();
        }
    }

    protected override void OnClosed() => _afterFlush.Set();

    public override void Send(short[] pcm16k) =>
        SendText($"{{\"audio\":{{\"data\":\"{Base64(pcm16k)}\",\"sample_rate\":16000,\"encoding\":\"audio/wav\"}}}}");

    public override string Finish()
    {
        _flushed = true;
        SendText("{\"type\":\"flush\"}");
        _afterFlush.Wait(TimeSpan.FromSeconds(5));
        if (Error != null) throw new InvalidOperationException("Sarvam streaming: " + Error);
        lock (_text) return _text.ToString();
    }
}

// ───────────────────────────── ElevenLabs ─────────────────────────────

sealed class ElevenStreamEngine(string key, string model = "scribe_v2_realtime") : IStreamingEngine
{
    public string Name => $"ElevenLabs {model} streaming (cloud)";
    public IStreamingSession Start(string language) => new ElevenStreamSession(key, language, model);
}

/// <summary>
/// ElevenLabs sends a running guess while you speak and one committed transcript when the client
/// marks a chunk as the last. This session commits by hand, on the final chunk.
/// </summary>
sealed class ElevenStreamSession : WebSocketSession
{
    readonly ManualResetEventSlim _started = new(false), _done = new(false);
    short[]? _pending;
    string _partial = "";
    string? _committed;
    volatile bool _commitSent;

    public ElevenStreamSession(string key, string language, string model)
        : base($"wss://api.elevenlabs.io/v1/speech-to-text/realtime?model_id={model}&language_code={language}&audio_format=pcm_16000&commit_strategy=manual",
               "xi-api-key", key)
    {
        var wait = Stopwatch.StartNew();
        _started.Wait(TimeSpan.FromSeconds(4));
        ReadyMs += wait.Elapsed.TotalMilliseconds;
        if (Error != null) throw new InvalidOperationException("ElevenLabs streaming: " + Error);
    }

    protected override void OnMessage(JsonElement message)
    {
        string type = message.GetProperty("message_type").GetString() ?? "";
        switch (type)
        {
            case "session_started":
                _started.Set();
                break;
            case "partial_transcript":
                _partial = message.GetProperty("text").GetString() ?? "";
                break;
            case "committed_transcript":
            case "committed_transcript_with_timestamps":
                if (_commitSent) { _committed = message.GetProperty("text").GetString() ?? ""; _done.Set(); }
                break;
            default:
                if (message.TryGetProperty("error", out var error))
                {
                    Error = $"{type}: {error}";
                    _started.Set();
                    _done.Set();
                }
                break;
        }
    }

    protected override void OnClosed() { _started.Set(); _done.Set(); }

    void SendChunk(short[] pcm, bool commit) =>
        SendText($"{{\"message_type\":\"input_audio_chunk\",\"audio_base_64\":\"{Base64(pcm)}\",\"commit\":{(commit ? "true" : "false")},\"sample_rate\":16000}}");

    public override void Send(short[] pcm16k)
    {
        // Always hold one chunk back, so that the last one can carry the commit.
        if (_pending != null) SendChunk(_pending, commit: false);
        _pending = pcm16k;
    }

    public override string Finish()
    {
        _commitSent = true;
        SendChunk(_pending ?? new short[320], commit: true);
        _pending = null;
        _done.Wait(TimeSpan.FromSeconds(6));
        if (_committed == null && Error != null) throw new InvalidOperationException("ElevenLabs streaming: " + Error);
        return (_committed ?? _partial).Trim();
    }
}

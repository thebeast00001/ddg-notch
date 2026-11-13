using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SpeechSpike;

/// <summary>
/// Push-to-talk dictation: hold a key in any app, speak, let go, and the text is typed where the
/// cursor is. The microphone is open only while the key is held, and the audio is never saved.
/// </summary>
sealed class Dictation
{
    readonly string _language;
    readonly ISpeechEngine? _engine;          // hears the whole utterance after you let go
    readonly IStreamingEngine? _streaming;    // or: listens while you speak
    readonly int _talkKey;
    readonly bool _talkKeyIsRight;
    readonly string _talkKeyName;

    volatile bool _held, _stop, _cancelled;
    Thread? _worker;
    readonly IslandLink _island = new();

    public IslandLink Link => _island;
    public bool IslandConnected => _island.Connected;
    public void Greet()
    {
        _island.Received += line => { if (line == "state?") _island.Send(_listening ? "listening" : "idle"); };
        _island.Send("idle");
    }

    volatile bool _listening;   // the microphone is open

    public Dictation(string language, ISpeechEngine? engine, IStreamingEngine? streaming, string key)
    {
        _language = language;
        _engine = engine;
        _streaming = streaming;
        (_talkKey, _talkKeyIsRight, _talkKeyName) = key switch
        {
            "ralt" => (KeyWatcher.VkMenu, true, "Right Alt"),
            "f9" => (0x78, false, "F9"),
            _ => (KeyWatcher.VkControl, true, "Right Ctrl"),
        };
    }

    public string EngineName => _streaming?.Name ?? _engine?.Name ?? "none";
    public string KeyName => _talkKeyName;

    public void OnKey(int vkey, bool right, bool down)
    {
        bool talk = vkey == _talkKey && (right == _talkKeyIsRight || _talkKey == 0x78);
        if (talk)
        {
            if (down && !_held && (_worker == null || !_worker.IsAlive))
            {
                _held = true;
                _stop = false;
                _cancelled = false;
                _worker = new Thread(Utterance) { IsBackground = true, Name = "dictation" };
                _worker.Start();
            }
            else if (!down && _held)
            {
                _held = false;
                _stop = true;
            }
        }
        else if (down && _held && vkey != KeyWatcher.VkPacket)
        {
            // Another key while the talk key is held means a shortcut, not dictation.
            _cancelled = true;
            _held = false;
            _stop = true;
        }
    }

    void Utterance()
    {
        var inv = CultureInfo.InvariantCulture;
        try
        {
            Cues.Start();
            _listening = true;
            _island.Send("listening");
            _engine?.Warm();   // the connection opens while you speak, not after you stop

            // A streaming engine connects while you start speaking; audio waits in a queue until it is ready.
            var queue = new ConcurrentQueue<short[]>();
            Task<IStreamingSession>? connecting = _streaming == null ? null : Task.Run(() => _streaming.Start(_language));
            void Drain()
            {
                if (connecting is not { IsCompletedSuccessfully: true }) return;
                while (queue.TryDequeue(out var chunk)) connecting.Result.Send(chunk);
            }

            // With an engine that hears the whole utterance, long speech is worked on while you talk.
            var live = connecting == null && _engine != null ? new LivePieces(_engine) : null;

            var clip = Microphone.Record(() => _stop, chunk =>
            {
                _island.Send(string.Create(inv, $"level {IslandLink.Level(chunk):F2}"));
                if (connecting != null) { queue.Enqueue(chunk); Drain(); }
                live?.Add(chunk);
            });
            long keyUp = Stopwatch.GetTimestamp();
            _listening = false;
            Cues.Stop();

            if (_cancelled) { Close(connecting); _island.Send("idle"); Console.WriteLine("  (cancelled: another key was pressed)"); return; }
            if (clip.Seconds < 0.3) { Close(connecting); _island.Send("idle"); return; }   // an accidental tap
            _island.Send("working");

            string text;
            if (connecting != null)
            {
                using var session = connecting.GetAwaiter().GetResult();
                Drain();
                text = session.Finish();
            }
            else
            {
                text = live is { Pieces: > 0 } ? live.Finish() : _engine!.Transcribe(clip);
            }
            double heardMs = Stopwatch.GetElapsedTime(keyUp).TotalMilliseconds;

            if (text.Length == 0)
            {
                _island.Send("idle");
                Console.WriteLine(string.Create(inv, $"  {clip.Seconds:F1} s of audio, nothing recognised ({heardMs:F0} ms)"));
                return;
            }

            int typed = TextTyper.Type(text + " ");
            _island.Send(typed > 0 ? "done" : "failed");
            double totalMs = Stopwatch.GetElapsedTime(keyUp).TotalMilliseconds;
            Console.WriteLine(string.Create(inv, $"  {clip.Seconds:F1} s of audio -> text in {heardMs:F0} ms, typed by {totalMs:F0} ms") + AnsweredBy());
            Console.WriteLine("  " + text);
            if (typed == 0)
                Console.WriteLine("  Windows did not accept the typing. The app in front probably runs as administrator.");
        }
        catch (Exception ex)
        {
            _listening = false;
            _island.Send("failed");
            Console.WriteLine("  failed: " + ex.Message);
        }

        static void Close(Task<IStreamingSession>? connecting)
        {
            connecting?.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); });
        }
    }

    /// <summary>For a routed engine: which service answered, and what went wrong on the way if anything did.</summary>
    string AnsweredBy()
    {
        var route = _engine as RoutedEngine ?? (_engine as SharedEngine)?.Inner as RoutedEngine;
        if (route == null) return "";
        return $", by {route.LastAnsweredBy}" + (route.LastTrouble == null ? "" : $" ({route.LastTrouble})");
    }
}

/// <summary>
/// Keeps a long dictation from ending in a long wait. While you are still talking, every stretch of
/// at least twelve seconds that ends in a half-second pause is handed to the engine in the
/// background. When you let go, only the last stretch is left to do. Short dictation is untouched:
/// with no pause after twelve seconds there are no pieces, and the whole utterance goes in one go.
/// </summary>
sealed class LivePieces(ISpeechEngine engine)
{
    const int Rate = 16000;
    const double MinPieceSeconds = 12;
    const int PauseChunks = 10;                    // ten 50 ms chunks of quiet

    readonly List<float> _audio = new();           // what has been said since the last cut
    readonly List<double> _levels = new();         // loudness of every chunk so far
    Task<string> _text = Task.FromResult("");      // the pieces so far, joined, in order
    int _quiet;

    public int Pieces { get; private set; }

    public void Add(short[] chunk)
    {
        double sum = 0;
        foreach (short s in chunk) { float f = s / 32768f; _audio.Add(f); sum += f * f; }
        double level = Math.Sqrt(sum / Math.Max(1, chunk.Length));
        _levels.Add(level);
        if (_levels.Count < 40) return;            // two seconds before the room's quiet level is known

        // The quiet tenth of what has been heard is the room; speech stands clearly above it.
        double room = _levels.OrderBy(x => x).ElementAt(_levels.Count / 10);
        _quiet = level < Math.Max(room * 3.5, 0.002) ? _quiet + 1 : 0;
        if (_quiet < PauseChunks || _audio.Count < MinPieceSeconds * Rate) return;

        // Cut in the middle of the pause, so neither piece loses the edge of a word.
        int cut = _audio.Count - (_quiet / 2) * chunk.Length;
        var piece = new Clip(_audio.GetRange(0, cut).ToArray(), Rate);
        _audio.RemoveRange(0, cut);
        _quiet = 0;
        Pieces++;
        var before = _text;
        _text = Task.Run(() => Join(before.GetAwaiter().GetResult(), engine.Transcribe(piece)));
    }

    /// <summary>Transcribes what is left and returns everything, in the order it was said.</summary>
    public string Finish()
    {
        string earlier = _text.GetAwaiter().GetResult();
        var rest = new Clip(_audio.ToArray(), Rate);
        return rest.Seconds < 0.3 ? earlier : Join(earlier, engine.Transcribe(rest));
    }

    static string Join(string a, string b) => a.Length == 0 ? b.Trim() : b.Length == 0 ? a : a + " " + b.Trim();
}

/// <summary>
/// Measures streaming engines as dictation would use them: each test clip is sent at the speed it
/// was spoken, then the utterance is ended, and the wait from that moment to the final text is timed.
/// </summary>
static class StreamBench
{
    public static void Run(string clipsRoot, string outDir, IEnumerable<(string Tag, Func<IStreamingEngine?> Make)> engines)
    {
        var inv = CultureInfo.InvariantCulture;
        var report = new StringBuilder();
        var traces = new StringBuilder();
        var transcripts = new StringBuilder("language\tclip\tengine\tready_ms\tafter_stop_ms\treference\theard\n");
        void Say(string line = "") { Console.WriteLine(line); report.AppendLine(line); }

        // How much audio goes in each message. Smaller pieces mean more messages a second.
        int pieceMs = int.TryParse(Environment.GetEnvironmentVariable("FORTY_PIECE_MS"), out int given) ? Math.Clamp(given, 20, 2000) : 100;
        Say($"S4 streaming test — {DateTime.Now:yyyy-MM-dd HH:mm}");
        Say($"Each clip is sent in {pieceMs} ms pieces at real speed. \"After you stop\" is the wait from the last piece to the final text.");

        foreach (string language in new[] { "en", "hi" })
        {
            var clips = TestClips.Load(clipsRoot, language);
            Say();
            Say($"══ {(language == "en" ? "English" : "Hindi")}: {clips.Count} clips ══");
            Say($"  {"engine",-48} {"WER",7}  {"CER",7}  {"ready",9}  {"after you stop",16}");

            foreach (var (tag, make) in engines)
            {
                var engine = make();
                if (engine == null) { Say($"  {tag,-48} key not set, skipped"); continue; }

                var tally = new Scoring.Tally();
                var ready = new List<double>();
                var after = new List<double>();
                int failures = 0;
                string? firstError = null;

                foreach (var clip in clips)
                {
                    try
                    {
                        using var session = engine.Start(language);
                        var samples = clip.Audio.Samples;
                        int piece = pieceMs * 16;   // samples at 16 kHz
                        var pacing = Stopwatch.StartNew();
                        for (int offset = 0, index = 0; offset < samples.Length; offset += piece, index++)
                        {
                            int count = Math.Min(piece, samples.Length - offset);
                            var pcm = new short[count];
                            for (int i = 0; i < count; i++) pcm[i] = (short)Math.Clamp((int)MathF.Round(samples[offset + i] * 32767f), short.MinValue, short.MaxValue);
                            session.Send(pcm);
                            int due = (int)((long)(offset + count) * 1000 / 16000);   // when this much audio would have been spoken
                            int wait = due - (int)pacing.ElapsedMilliseconds;
                            if (wait > 0) Thread.Sleep(wait);
                        }
                        long stopped = Stopwatch.GetTimestamp();
                        string text = session.Finish();
                        double afterMs = Stopwatch.GetElapsedTime(stopped).TotalMilliseconds;

                        tally += Scoring.Score(clip.Reference, text, language);
                        ready.Add(session.ReadyMs);
                        after.Add(afterMs);
                        transcripts.Append(string.Create(inv, $"{language}\t{clip.Name}\t{engine.Name}\t{session.ReadyMs:F0}\t{afterMs:F0}\t{clip.Reference}\t{text}\n"));
                        if (clip == clips[0])
                        {
                            traces.AppendLine($"── {engine.Name}, {language}, {clip.Name} ──");
                            foreach (string line in session.Trace) traces.AppendLine(line);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        firstError ??= ex.Message;
                        tally += Scoring.Score(clip.Reference, "", language);
                    }
                    Thread.Sleep(1500);   // nobody dictates sentences back to back with no gap at all
                }

                string Middle(List<double> v) => v.Count == 0 ? "-" : v.OrderBy(x => x).ElementAt(v.Count / 2).ToString("F0", inv);
                string range = after.Count == 0 ? "" : string.Create(inv, $" ({after.Min():F0}-{after.Max():F0})");
                Say($"  {engine.Name,-48} {tally.Wer.ToString("F1", inv),6}%  {tally.Cer.ToString("F1", inv),6}%  {Middle(ready),6} ms  {Middle(after),6} ms{range}" +
                    (failures > 0 ? $"   {failures} failed: {firstError}" : ""));
            }
        }

        File.WriteAllText(Path.Combine(outDir, "s4-stream.txt"), report.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "s4-stream-transcripts.tsv"), transcripts.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "s4-stream-trace.txt"), traces.ToString(), new UTF8Encoding(false));
    }
}

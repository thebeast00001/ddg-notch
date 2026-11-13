using System.Globalization;
using System.Text;

namespace SpeechSpike;

/// <summary>Something one person said in a call: when, who, what.</summary>
sealed record SpokenLine(double At, string Who, string Text);

/// <summary>Turns one recorded stream of a call into lines.</summary>
interface ICallTranscriber
{
    string Name { get; }
    /// <param name="who">"You" for the microphone stream, "Others" for the call app's audio.</param>
    /// <param name="manyVoices">The stream may hold more than one person.</param>
    List<SpokenLine> Lines(Clip audio, string who, bool manyVoices);
}

/// <summary>
/// Cuts the stream at pauses and gives each piece to a speech engine. Works with a model on this
/// PC, so the call stays here. It cannot tell the voices on the others' side apart.
/// </summary>
sealed class SegmentTranscriber(ISpeechEngine engine) : ICallTranscriber
{
    public string Name => engine.Name;

    public List<SpokenLine> Lines(Clip audio, string who, bool manyVoices)
    {
        var lines = new List<SpokenLine>();
        foreach (var (start, end) in Speech.Segments(audio))
        {
            string text;
            try { text = engine.Transcribe(Speech.Slice(audio, start, end)); }
            catch (Exception ex) { text = $"[not transcribed: {Keys.Plain(ex)}]"; }
            if (text.Length > 0) lines.Add(new SpokenLine(start, who, text));
        }
        return lines;
    }
}

/// <summary>
/// Sends a stream to ElevenLabs, which returns every word with its time and a label for each
/// voice, so the others' side comes back as "Speaker 1", "Speaker 2" and so on.
/// Silence is cut out before sending: ElevenLabs charges for silence, and a call is often half
/// silence on each side. If the service fails, the stream goes to <paramref name="fallback"/>
/// instead, so a call never ends without a transcript.
/// </summary>
sealed class ScribeTranscriber(string key, string? language, ICallTranscriber fallback) : ICallTranscriber
{
    const double Gap = 0.4;   // seconds of silence left between stretches of speech

    public string Name => "ElevenLabs scribe_v3 (cloud)";
    /// <summary>Set when the last stream had to go to the fallback, with the reason.</summary>
    public string? FellBack { get; private set; }
    /// <summary>Seconds of audio actually sent, across all streams so far.</summary>
    public double SecondsSent { get; private set; }
    /// <summary>How many different voices ElevenLabs reported in the last stream it was asked to separate.</summary>
    public int VoicesFound { get; private set; }
    public string FallbackName => fallback.Name;

    public List<SpokenLine> Lines(Clip audio, string who, bool manyVoices)
    {
        var stretches = Speech.Segments(audio, maxSeconds: 60);
        if (stretches.Count == 0) return new();

        // Speech only, back to back, with a note of where each stretch came from.
        var packed = new List<float>();
        var map = new List<(double Packed, double Original, double Length)>();
        int gap = (int)(Gap * audio.SampleRate);
        foreach (var (start, end) in stretches)
        {
            var piece = Speech.Slice(audio, start, end);
            map.Add((packed.Count / (double)audio.SampleRate, start, piece.Seconds));
            packed.AddRange(piece.Samples);
            packed.AddRange(new float[gap]);
        }
        var toSend = new Clip(packed.ToArray(), audio.SampleRate);

        List<ScribeWord> words;
        try
        {
            using var engine = new ElevenLabsEngine(key, language);
            words = engine.Words(toSend, speakers: manyVoices);
            SecondsSent += toSend.Seconds;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            FellBack = Keys.Plain(ex);
            return fallback.Lines(audio, who, manyVoices);
        }

        double Original(double packedTime)
        {
            var entry = map[0];
            foreach (var m in map) { if (m.Packed <= packedTime + 0.05) entry = m; else break; }
            return entry.Original + Math.Clamp(packedTime - entry.Packed, 0, entry.Length);
        }

        // Words become lines: a new line when the voice changes, after a pause, or when a line has
        // grown long and a sentence has just ended.
        var voices = words.Where(w => w.Kind == "word" && w.Speaker != null).Select(w => w.Speaker!).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (manyVoices) VoicesFound = voices.Count;
        string Label(string? speaker) =>
            !manyVoices || voices.Count < 2 || speaker == null ? who : $"Speaker {voices.IndexOf(speaker) + 1}";

        var lines = new List<SpokenLine>();
        var text = new System.Text.StringBuilder();
        string? lineVoice = null;
        double lineStart = 0, lastEnd = -1;
        void Close()
        {
            string said = text.ToString().Trim();
            if (said.Length > 0) lines.Add(new SpokenLine(lineStart, Label(lineVoice), said));
            text.Clear();
        }
        foreach (var word in words)
        {
            if (word.Kind == "audio_event") continue;
            if (word.Kind == "word")
            {
                // Pauses are judged in the call's own time, not in the packed audio where they were shortened.
                double start = Original(Math.Max(0, word.Start)), end = Original(Math.Max(word.Start, word.End));
                bool sentenceEnded = text.Length > 0 && ".?!।".Contains(text.ToString().TrimEnd()[^1]);
                bool newLine = text.Length == 0 || word.Speaker != lineVoice || start - lastEnd > 1.0 || (text.Length > 220 && sentenceEnded);
                if (newLine) { Close(); lineVoice = word.Speaker; lineStart = start; }
                lastEnd = end;
            }
            if (text.Length > 0 || word.Kind == "word") text.Append(word.Text);
        }
        Close();
        return lines;
    }
}

/// <summary>Who transcribes each side of a call, plus anything that has to be closed afterwards.</summary>
sealed record CallEngines(ICallTranscriber Others, ICallTranscriber Mine, IDisposable? Owned = null) : IDisposable
{
    public void Dispose() => Owned?.Dispose();
}

/// <summary>Turns the two recorded streams of a call into a transcript file.</summary>
static class CallTranscript
{
    /// <summary>
    /// Writes the transcript, then, if a language model is given, the summary, decisions and to-dos
    /// above it. The transcript is on disk before the model is asked, so a slow or failing model
    /// can never cost the transcript.
    /// </summary>
    public static (string Markdown, string Path, CallSummary? Notes) Write(string app, Clip theirAudio, Clip? myAudio, ICallTranscriber others, ICallTranscriber mine, string outDir, bool keepAudio = false, ChatModel? notesModel = null)
    {
        var inv = CultureInfo.InvariantCulture;
        // The two sides are transcribed at the same time when they go to different places.
        Task<List<SpokenLine>>? mineTask = myAudio == null ? null
            : ReferenceEquals(others, mine) ? null : Task.Run(() => mine.Lines(myAudio, "You", manyVoices: false));
        var spoken = others.Lines(theirAudio, "Others", manyVoices: true);
        if (myAudio != null) spoken.AddRange(mineTask?.GetAwaiter().GetResult() ?? mine.Lines(myAudio, "You", manyVoices: false));
        spoken.Sort((a, b) => a.At.CompareTo(b.At));
        var lines = spoken.Select((line, index) => new TranscriptLine(index + 1, line.At, line.Who, line.Text)).ToList();

        bool voicesApart = lines.Any(l => l.Who.StartsWith("Speaker ", StringComparison.Ordinal));
        // Named by who actually did the work: a service that failed and handed over is not credited.
        static string Did(ICallTranscriber t) => t is ScribeTranscriber { FellBack: not null } failed ? failed.FallbackName : t.Name;
        string engines = myAudio == null || Did(others) == Did(mine) ? Did(others) : $"{Did(others)} for the others, {Did(mine)} for you";
        string fellBack = (others as ScribeTranscriber)?.FellBack ?? (mine as ScribeTranscriber)?.FellBack ?? "";
        var head = new StringBuilder();
        head.AppendLine($"# Call on {app}, {DateTime.Now:d MMMM yyyy, HH:mm}");
        head.AppendLine();
        head.AppendLine(string.Create(inv, $"Length {theirAudio.Seconds / 60:F1} minutes. Engine: {engines}. ") +
            (voicesApart ? "\"You\" is your microphone. \"Speaker 1\", \"Speaker 2\" and so on are the voices in the call app's audio, told apart by ElevenLabs; they are labels, not names."
                         : "Speaker labels come from which stream the words were on, not from recognising voices."));
        if (fellBack.Length > 0)
            head.AppendLine($"ElevenLabs was not used for part of this call ({fellBack}). The standby engine transcribed that part instead, so its voices are not told apart.");
        if (myAudio != null)
        {
            double leak = Speech.Leak(theirAudio, myAudio);
            head.AppendLine(string.Create(inv, $"Echo check: how much of the others' audio is in your microphone stream, {leak:F2} on a scale from 0 (none) to 1. Above about 0.15 means their voices are leaking into your channel."));
        }
        head.AppendLine();

        var body = new StringBuilder();
        body.AppendLine("## Transcript");
        body.AppendLine();
        foreach (var line in lines)
            body.AppendLine(string.Create(inv, $"**{line.Who}** ({(int)(line.At / 60)}:{(int)(line.At % 60):00}) {line.Text}"));
        body.AppendLine();

        var utf8 = new UTF8Encoding(false);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", inv);
        string path = System.IO.Path.Combine(outDir, $"call-{stamp}.md");
        File.WriteAllText(path, head.ToString() + body, utf8);
        if (keepAudio)
        {
            File.WriteAllBytes(System.IO.Path.Combine(outDir, $"call-{stamp}-others.wav"), theirAudio.ToWavBytes());
            if (myAudio != null) File.WriteAllBytes(System.IO.Path.Combine(outDir, $"call-{stamp}-you.wav"), myAudio.ToWavBytes());
        }

        CallSummary? notes = null;
        string middle = "";
        if (lines.Count > 0 && notesModel == null)
            middle = "No summary was written: no language model is set up. Run `SpeechSpike summarize` to see the choices.\n\n";
        else if (lines.Count > 0)
        {
            try
            {
                notes = Summariser.Summarise(notesModel!, lines);
                middle = Summariser.Render(notes, lines, notesModel!.Name);
            }
            catch (InvalidOperationException ex) { middle = $"No summary was written: {ex.Message}\n\n"; }
        }
        string all = head.ToString() + middle + body;
        if (middle.Length > 0) File.WriteAllText(path, all, utf8);
        return (all, path, notes);
    }

    /// <summary>"chrome.exe" becomes "Chrome": a name a person would recognise.</summary>
    public static string AppLabel(string exe)
    {
        string name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
        return name.ToLowerInvariant() switch
        {
            "chrome" => "Chrome", "msedge" => "Edge", "firefox" => "Firefox", "ms-teams" or "teams" => "Teams",
            "zoom" => "Zoom", "discord" => "Discord", "slack" => "Slack", "whatsapp" or "whatsapp.root" => "WhatsApp",
            "speechspike" => "the test player",
            _ => name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : exe,
        };
    }
}

/// <summary>
/// Watches for a call, asks through the island whether to take notes, and records only after a
/// yes. "Not now", or no answer, means nothing is opened and nothing is written. Recording ends
/// when you press Stop on the island or the call itself ends.
/// </summary>
sealed class CallWatcher
{
    enum State { Idle, Offered, Declined, Recording }

    readonly IslandLink _link;
    readonly Func<CallEngines> _engines;
    readonly string _outDir;
    readonly string? _appFilter;
    readonly bool _useMicrophone, _once;
    readonly Func<ChatModel?> _notesModel;
    readonly AutoResetEvent _wake = new(false);
    volatile string? _answer;
    volatile string? _lastNotes;   // the file "Open" on the island opens
    volatile string? _stateLine;   // what to tell an island that asks "state?"

    /// <param name="notesModel">Finds the language model that writes the summary, or null for a transcript only. Asked when a call ends, so a model started later is still found.</param>
    public CallWatcher(IslandLink link, Func<CallEngines> engines, Func<ChatModel?> notesModel, string outDir, string? appFilter, bool useMicrophone, bool once)
    {
        _link = link;
        _engines = engines;
        _notesModel = notesModel;
        _outDir = outDir;
        _appFilter = appFilter;
        _useMicrophone = useMicrophone;
        _once = once;
        _link.Received += line =>
        {
            if (line is "accept" or "decline" or "stop") { _answer = line; _wake.Set(); }
            else if (line == "open-notes") OpenLastNotes();
            else if (line == "state?" && _stateLine is { } state) _link.Send(state);
        };
    }

    /// <summary>
    /// Opens the newest notes file in whatever program opens Markdown files. The island sends only
    /// the word "open-notes"; the path never travels over the pipe, so nothing else can be opened this way.
    /// </summary>
    void OpenLastNotes()
    {
        if (_lastNotes is not { } path || !File.Exists(path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception)
        {
            // No program is set to open .md files: Notepad reads them fine.
            try { System.Diagnostics.Process.Start("notepad.exe", $"\"{path}\"")?.Dispose(); }
            catch (System.ComponentModel.Win32Exception ex) { Console.WriteLine("  could not open the notes: " + ex.Message); }
        }
    }

    AudioSession? FindCall()
    {
        var sessions = AudioSessions.Snapshot();
        var call = AudioSessions.FindCall(sessions, _appFilter);
        // For testing: a named app that is merely playing sound counts as a call.
        if (call == null && _appFilter != null)
            call = sessions.FirstOrDefault(s => !s.IsCapture && s.Active && s.ProcessId != Environment.ProcessId &&
                (s.App.Contains(_appFilter, StringComparison.OrdinalIgnoreCase) || s.ProcessName.Contains(_appFilter, StringComparison.OrdinalIgnoreCase)));
        return call;
    }

    public void Run(CancellationToken cancel)
    {
        var state = State.Idle;
        AudioSession? current = null;
        WasapiCapture? others = null;
        FollowingMicrophone? mine = null;
        long from = 0;
        int seen = 0, gone = 0;
        string label = "";

        while (!cancel.IsCancellationRequested)
        {
            _wake.WaitOne(_link.Saver ? 2000 : 1000);   // look once a second (every two in battery saver), or at once when the island answers
            string? answer = _answer;
            _answer = null;
            AudioSession? call;
            try { call = FindCall(); }
            catch (Exception ex) { Console.WriteLine("  could not read audio sessions: " + ex.Message); continue; }

            switch (state)
            {
                case State.Idle:
                    seen = call != null ? seen + 1 : 0;
                    if (seen >= 2 && call != null)   // there for two looks in a row, not a blip
                    {
                        current = call;
                        label = CallTranscript.AppLabel(call.App);
                        _link.Send("call-offer " + label);
                        _stateLine = "call-offer " + label;
                        Console.WriteLine($"  call noticed on {label}; asked whether to take notes");
                        state = State.Offered;
                        gone = 0;
                    }
                    break;

                case State.Offered:
                    if (answer == "accept" && current != null)
                    {
                        try
                        {
                            from = WasapiCapture.Now;
                            others = WasapiCapture.ForProcess((call ?? current).ProcessId, label);
                            mine = _useMicrophone ? new FollowingMicrophone() : null;
                            others.Start();
                            mine?.Start();
                            _link.Send("recording " + label);
                            _stateLine = "recording " + label;
                            Console.WriteLine($"  taking notes on {label}{(_useMicrophone ? "" : " (their side only)")}");
                            state = State.Recording;
                            gone = 0;
                        }
                        catch (Exception ex)
                        {
                            others?.Dispose(); mine?.Dispose(); others = null; mine = null;
                            _link.Send("call-failed");
                            Console.WriteLine("  could not start: " + ex.Message);
                            state = State.Declined;
                        }
                    }
                    else if (answer == "decline")
                    {
                        _stateLine = null;
                        Console.WriteLine("  not now: nothing recorded");
                        state = State.Declined;
                        gone = 0;
                    }
                    else if (call == null && ++gone >= 2)
                    {
                        _link.Send("call-gone");
                        _stateLine = null;
                        state = State.Idle;
                        seen = 0;
                    }
                    break;

                case State.Declined:   // stay quiet until this call is over
                    gone = call == null ? gone + 1 : 0;
                    if (gone >= 3) { state = State.Idle; seen = 0; }
                    break;

                case State.Recording:
                    gone = call == null ? gone + 1 : 0;
                    if (answer == "stop" || gone >= 4)
                    {
                        others!.Stop();
                        mine?.Stop();
                        long to = WasapiCapture.Now;
                        _link.Send("call-working");
                        _stateLine = null;
                        try
                        {
                            var theirAudio = Speech.Normalise(others.Assemble(from, to));
                            var myAudio = mine == null ? null : Speech.Normalise(mine.Assemble(from, to));
                            using var engines = _engines();
                            var model = _notesModel();
                            if (model != null) Console.WriteLine($"  the summary will be written by {model.Name}{(model.IsRemote ? "; the transcript text is sent there" : "")}");
                            var (_, path, notes) = CallTranscript.Write(label, theirAudio, myAudio, engines.Others, engines.Mine, _outDir, notesModel: model);
                            if ((engines.Others as ScribeTranscriber)?.FellBack is { } why) Console.WriteLine($"  ElevenLabs was not used ({why}); the standby engine wrote the transcript");
                            _lastNotes = path;
                            // What the island says next to "Notes ready".
                            string result = notes == null ? "transcript only"
                                          : notes.Todos.Count == 0 ? "no to-dos"
                                          : notes.Todos.Count == 1 ? "1 to-do" : $"{notes.Todos.Count} to-dos";
                            _link.Send("call-done " + result);
                            Console.WriteLine($"  notes saved to {path} ({result})");
                        }
                        catch (Exception ex)
                        {
                            _link.Send("call-failed");
                            Console.WriteLine("  could not write the notes: " + ex.Message);
                        }
                        others.Dispose(); mine?.Dispose(); others = null; mine = null;
                        if (_once) return;
                        state = State.Declined;   // the same call is not offered twice
                        gone = 0;
                    }
                    break;
            }
        }
        others?.Dispose();
        mine?.Dispose();
    }
}

/// <summary>Lets dictation and call notes use one loaded model, one at a time, without either closing it.</summary>
sealed class SharedEngine(ISpeechEngine inner, object gate) : ISpeechEngine
{
    public ISpeechEngine Inner => inner;
    public string Name => inner.Name;
    public string Transcribe(Clip clip) { lock (gate) return inner.Transcribe(clip); }
    public void Warm() => inner.Warm();
    public void Dispose() { }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;
using SpeechSpike;

// Spike S4: which speech engine, how accurate, how fast.
//   SpeechSpike fetch-clips          download the public test clips
//   SpeechSpike bench                run every available engine over the clips and write a report
//   SpeechSpike record en|hi         speak into the microphone and see what each engine hears

Console.OutputEncoding = Encoding.UTF8;
var inv = CultureInfo.InvariantCulture;
string root = FindSpikesRoot();
string modelsRoot = Path.Combine(root, "models"), clipsRoot = Path.Combine(root, "clips"), outDir = Path.Combine(root, "out");
Directory.CreateDirectory(outDir);

switch (args.Length > 0 ? args[0] : "bench")
{
    case "fetch-clips":
        await TestClips.Fetch(clipsRoot);
        return 0;
    case "record":
        Record(args.Length > 1 ? args[1] : "en");
        return 0;
    case "bench":
        Bench(args.Skip(1).ToArray());
        return 0;
    case "bench-stream":
        var wanted = args.Skip(1).ToArray();
        var streamers = new (string Tag, Func<IStreamingEngine?> Make)[]
        {
            ("sarvam-stream", () => SarvamEngine.FindKey() is { } k ? new SarvamStreamEngine(k) : null),
            ("elevenlabs-stream", () => ElevenLabsEngine.FindKey() is { } k ? new ElevenStreamEngine(k) : null),
        };
        StreamBench.Run(clipsRoot, outDir, streamers.Where(s => wanted.Length == 0 || wanted.Contains(s.Tag)));
        return 0;
    case "type-test":
        return TypingSelfTest.Run();
    case "demo-island":
        return DemoIsland();
    case "sessions":
        return Sessions();
    case "play":   // test use: play <wav> <volume 0..1> <delay ms>
        TestPlayer.Play(args[1], double.Parse(args[2], inv), int.Parse(args[3], inv));
        return 0;
    case "loopback-test":
        return LoopbackTest(args.Length > 1 ? double.Parse(args[1], inv) / 100 : 0);
    case "call-notes":
        return CallNotes(args.Skip(1).ToArray());
    case "watch-calls":
        return WatchCalls(args.Skip(1).ToArray());
    case "capture-selftest":
    {
        // Exercises the device-capture plumbing for a second and a half against the speaker output.
        // The microphone is not opened and the audio is thrown away; only counts are reported.
        long begin = WasapiCapture.Now;
        using var probe = WasapiCapture.ForSpeakerOutput();
        probe.Start();
        Thread.Sleep(1500);
        probe.Stop();
        var heard = probe.Assemble(begin, WasapiCapture.Now);
        Console.WriteLine(string.Create(inv, $"Opened {probe.Description}; category request result 0x{probe.PropertiesResult:X}; {probe.PacketCount} packets; {heard.Seconds:F1} s assembled."));
        return 0;
    }
    case "dictate":
        return Dictate(args.Skip(1).ToArray());
    case "summarize":
        return Summarize(args.Skip(1).ToArray());
    case "policy-selftest":
        return PolicySelfTest.Run();
    case "mic-watch":
        return MicWatch.Run(args.Length > 1 && double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out double watchSeconds) ? watchSeconds : 60);
    case "summary-selftest":
        return SummarySelfTest();
    case "keys":
        if (args.Contains("--selftest")) return Keys.SelfTest();
        if (args.Contains("--status")) { Keys.PrintStatus(); return 0; }
        if (args.Contains("--forget")) { Keys.Save(Keys.Sarvam, null); Keys.Save(Keys.ElevenLabs, null); Console.WriteLine("Stored keys removed."); return 0; }
        return Keys.Setup(CheckSample());
    case "check":
        return Check(args.Skip(1).ToArray());
    case "route-test":
        return RouteTest(args.Skip(1).ToArray());
    case "long-test":
        return LongTest(args.Skip(1).ToArray());
    case "speakers-test":
        return SpeakersTest(args.Skip(1).ToArray());
    default:
        Console.WriteLine("Use: fetch-clips | bench [local|sarvam|elevenlabs] | bench-stream | record en|hi | type-test | dictate [en|hi] [--engine name] [--key rctrl|ralt|f9]");
        return 1;
}

// Which programs are using the microphone and which are playing sound, right now. Reads a list;
// opens nothing and records nothing.
int Sessions()
{
    var sessions = AudioSessions.Snapshot();
    string Describe(IEnumerable<AudioSession> list) =>
        string.Join(", ", list.GroupBy(s => s.App).Select(g => g.Key + (g.Any(s => s.Active) ? " (live)" : " (open, silent)"))) is { Length: > 0 } text ? text : "nothing";
    Console.WriteLine("Using the microphone: " + Describe(sessions.Where(s => s.IsCapture)));
    Console.WriteLine("Playing sound:        " + Describe(sessions.Where(s => !s.IsCapture)));
    var call = AudioSessions.FindCall(sessions);
    Console.WriteLine(call == null ? "No call detected: no app is both recording and playing right now."
                                   : $"Looks like a call: {call.App} (process {call.ProcessName}, id {call.ProcessId}).");
    return 0;
}

// Proves that capturing one program's audio really captures that program and no other. Two copies
// of this program each play a different known sentence at the same moment; only the first is
// captured; the capture is then compared with both sentences, by waveform and by transcript.
int LoopbackTest(double volume)
{
    var english = TestClips.Load(clipsRoot, "en");
    if (english.Count < 6) { Console.WriteLine("Test clips are missing; run fetch-clips first."); return 1; }
    TestClip wanted = english[2], other = english[5];
    string exe = Environment.ProcessPath!;

    Process StartPlayer(TestClip clip)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "play", Path.Combine(clipsRoot, "en", clip.Name), volume.ToString(inv), "1800" }) info.ArgumentList.Add(arg);
        info.Environment["FORTY_SPIKES"] = root;
        return Process.Start(info)!;
    }

    var report = new StringBuilder();
    void Say(string line) { Console.WriteLine(line); report.AppendLine(line); }
    Say($"S5 single-app capture test — {DateTime.Now:yyyy-MM-dd HH:mm}, test players at {volume:P0} of full volume");

    long from = WasapiCapture.Now;
    using var playerA = StartPlayer(wanted);
    using var playerB = StartPlayer(other);
    using var capture = WasapiCapture.ForProcess((uint)playerA.Id, "test player A");
    capture.Start();
    playerA.WaitForExit(40_000);
    playerB.WaitForExit(40_000);
    Thread.Sleep(400);
    capture.Stop();
    long to = WasapiCapture.Now;
    var captured = capture.Assemble(from, to);

    double peak = captured.Samples.Length == 0 ? 0 : captured.Samples.Max(s => Math.Abs(s));
    Say($"  captured {captured.Seconds.ToString("F1", inv)} s as {capture.Description}; {capture.PacketCount} packets; loudest sample {peak.ToString("F3", inv)} of full scale");

    double hasWanted = Speech.Leak(wanted.Audio, captured, 6), hasOther = Speech.Leak(other.Audio, captured, 6);
    Say($"  waveform match with the captured player's sentence: {hasWanted.ToString("F2", inv)}   (1 is identical)");
    Say($"  waveform match with the other player's sentence:    {hasOther.ToString("F2", inv)}   (0 is nothing in common)");

    // A check on the meter itself: the same two sentences, one mixed into the other on purpose.
    var mixed = new float[other.Audio.Samples.Length];
    for (int i = 0; i < mixed.Length; i++)
        mixed[i] = other.Audio.Samples[i] + (i >= 1280 && i - 1280 < wanted.Audio.Samples.Length ? 0.3f * wanted.Audio.Samples[i - 1280] : 0);
    Say($"  meter check: clean pair {Speech.Leak(wanted.Audio, other.Audio).ToString("F2", inv)}, pair with a 30% leak added {Speech.Leak(wanted.Audio, new Clip(mixed, 16000)).ToString("F2", inv)}");

    if (peak < 0.0005) { Say("  the capture is silent, so there is nothing to transcribe at this volume"); }
    else
    {
        var model = LocalModel.All.First(m => m.Key == "parakeet-v3");
        using var engine = model.Create(modelsRoot, "en", threads: 4)!;
        // Bring quiet captures up to a normal level before recognising them.
        float gain = (float)Math.Min(200, 0.5 / peak);
        var loud = new Clip(captured.Samples.Select(s => s * gain).ToArray(), 16000);
        string heard = string.Join(" ", Speech.Segments(loud).Select(seg => engine.Transcribe(Speech.Slice(loud, seg.Start, seg.End))));
        var asWanted = Scoring.Score(wanted.Reference, heard, "en");
        var asOther = Scoring.Score(other.Reference, heard, "en");
        Say($"  transcript of the capture: {heard}");
        Say($"  word errors against the captured player's sentence: {asWanted.Wer.ToString("F1", inv)}%");
        Say($"  word errors against the other player's sentence:    {asOther.Wer.ToString("F1", inv)}%  (high means it was not picked up)");
    }
    File.WriteAllText(Path.Combine(outDir, $"s5-loopback-{(int)(volume * 100)}.txt"), report.ToString(), new UTF8Encoding(false));
    return 0;
}

// Records a call as two streams, the call app's audio and your microphone, then writes a transcript
// with "You" and "Others". Meant to be started by a person during a real call.
int CallNotes(string[] options)
{
    string? Option(string name)
    {
        int at = Array.IndexOf(options, name);
        return at >= 0 && at + 1 < options.Length ? options[at + 1] : null;
    }
    string? appFilter = Option("--app");
    double minutes = double.TryParse(Option("--minutes"), System.Globalization.NumberStyles.Float, inv, out double m) ? m : 10;
    string language = options.Contains("hi") ? "hi" : "en";
    bool useMicrophone = !options.Contains("--no-mic"), keepAudio = options.Contains("--keep-audio");

    var sessions = AudioSessions.Snapshot();
    var call = AudioSessions.FindCall(sessions, appFilter);
    if (call == null && appFilter != null)
        call = sessions.FirstOrDefault(s => !s.IsCapture && s.Active &&
            (s.App.Contains(appFilter, StringComparison.OrdinalIgnoreCase) || s.ProcessName.Contains(appFilter, StringComparison.OrdinalIgnoreCase)));
    if (call == null)
    {
        Console.WriteLine("No call found. A call is an app that is using the microphone and playing sound at the same time.");
        Sessions();
        Console.WriteLine("Start the call first, or name an app that is playing sound:  call-notes --app chrome");
        return 1;
    }

    bool cloudCalls = options.Contains("--cloud") || options.Contains("--cloud-calls");
    bool cloudNotes = options.Contains("--cloud") || options.Contains("--cloud-notes");
    using var engines = MakeCallEngines(language, null, cloudCalls);
    Console.WriteLine(CallsLine(language, cloudCalls));

    Console.WriteLine($"Call found: {call.App}. Taking notes{(useMicrophone ? "" : " (their side only, microphone not used)")}.");
    Console.WriteLine($"Press Enter to stop (stops by itself after {minutes.ToString("0.#", inv)} minutes). Audio stays in memory{(keepAudio ? " and is also saved because --keep-audio was given" : " and is not saved")}.");

    long from = WasapiCapture.Now;
    using var others = WasapiCapture.ForProcess(call.ProcessId, call.App);
    using var mine = useMicrophone ? WasapiCapture.ForMicrophone() : null;
    others.Start();
    mine?.Start();
    if (Console.IsInputRedirected) Thread.Sleep(TimeSpan.FromMinutes(minutes));   // no keyboard to press Enter on
    else Task.Run(Console.ReadLine).Wait(TimeSpan.FromMinutes(minutes));
    others.Stop();
    mine?.Stop();
    long to = WasapiCapture.Now;

    var theirAudio = Speech.Normalise(others.Assemble(from, to));
    var myAudio = mine == null ? null : Speech.Normalise(mine.Assemble(from, to));
    Console.WriteLine(string.Create(inv, $"Stopped after {theirAudio.Seconds:F0} s. Writing the transcript..."));

    var notesModel = ChatModel.Find(allowHosted: cloudNotes);
    Console.WriteLine(notesModel == null ? ChatModel.HowToEnable
        : $"The summary will be written by {notesModel.Name}.{(notesModel.IsRemote ? " The transcript text is sent there." : " Nothing leaves this PC.")}");
    var (notes, path, _) = CallTranscript.Write(CallTranscript.AppLabel(call.App), theirAudio, myAudio, engines.Others, engines.Mine, outDir, keepAudio, notesModel);
    Console.WriteLine();
    Console.WriteLine(notes);
    Console.WriteLine("Saved to " + path);
    return 0;
}

// Plays the states of three utterances to the island, with no microphone and no typing: one that
// succeeds, one that is cancelled, one that fails. The level meter is fed the loudness of a real
// test sentence at the speed it was spoken.
int DemoIsland()
{
    using var link = new IslandLink();
    var sentence = TestClips.Load(clipsRoot, "en").FirstOrDefault()?.Audio ?? new Clip(new float[16000 * 4], 16000);

    void Speak(double seconds)
    {
        const int piece = 800;   // 50 ms
        var pacing = Stopwatch.StartNew();
        for (int offset = 0, index = 0; offset < sentence.Samples.Length && pacing.Elapsed.TotalSeconds < seconds; offset += piece, index++)
        {
            var pcm = new short[Math.Min(piece, sentence.Samples.Length - offset)];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp((int)(sentence.Samples[offset + i] * 32767f), short.MinValue, short.MaxValue);
            link.Send(string.Create(inv, $"level {IslandLink.Level(pcm):F2}"));
            int wait = (index + 1) * 50 - (int)pacing.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    link.Send("idle");
    if (!link.Connected) { Console.WriteLine("The island is not running, so there is nothing to show."); return 1; }
    Thread.Sleep(700);

    link.Send("listening"); Speak(3.0); link.Send("working"); Thread.Sleep(900); link.Send("done"); Thread.Sleep(2200);
    link.Send("listening"); Speak(1.7); link.Send("idle"); Thread.Sleep(1600);
    link.Send("listening"); Speak(1.7); link.Send("working"); Thread.Sleep(750); link.Send("failed"); Thread.Sleep(2000);
    link.Send("bye");
    Thread.Sleep(300);
    Console.WriteLine("Sent three utterances to the island: one done, one cancelled, one failed.");
    return 0;
}

// Writes the notes (summary, decisions, open questions, to-dos) for a transcript file.
//   summarize [file] [--cloud-notes] [--show-prompt]
// With no file it uses the made-up sample call. --show-prompt prints exactly what would be sent and sends nothing.
int Summarize(string[] options)
{
    string file = options.FirstOrDefault(o => !o.StartsWith("--")) ?? Path.Combine(root, "SpeechSpike", "samples", "sample-call.md");
    if (!File.Exists(file)) { Console.WriteLine("No such file: " + file); return 1; }
    string markdown = File.ReadAllText(file);
    var lines = Summariser.ParseTranscript(markdown);
    if (lines.Count == 0) { Console.WriteLine("No transcript lines of the form \"**You** (0:05) text\" were found in " + file); return 1; }

    if (options.Contains("--show-prompt"))
    {
        Console.WriteLine(Summariser.Numbered(lines));
        Console.WriteLine();
        Console.WriteLine(Summariser.LanguageLine(lines));
        Console.WriteLine($"{lines.Count} lines, {Summariser.Numbered(lines).Length} characters. Nothing was sent.");
        return 0;
    }

    var model = ChatModel.Find(allowHosted: options.Contains("--cloud-notes"));
    if (model == null) { Console.WriteLine(ChatModel.HowToEnable); return 2; }
    Console.WriteLine($"Sending {lines.Count} transcript lines to {model.Name}.{(model.IsRemote ? " The text leaves this PC." : " Nothing leaves this PC.")}");

    long start = Stopwatch.GetTimestamp();
    CallSummary notes;
    try { notes = Summariser.Summarise(model, lines); }
    catch (InvalidOperationException ex) { Console.WriteLine("No notes: " + ex.Message); return 1; }
    double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;

    string rendered = Summariser.Render(notes, lines, model.Name);
    Console.WriteLine();
    Console.WriteLine(rendered);
    Console.WriteLine(string.Create(inv, $"Took {seconds:F1} s. {notes.Decisions.Count} decisions, {notes.OpenQuestions.Count} open questions, {notes.Todos.Count} to-dos, {notes.Removed.Count} things left out by the checker."));
    if (model.LastUsage is { } usage) Console.WriteLine($"Tokens: {usage.Sent} sent, {usage.Written} written.");
    string saved = Path.Combine(outDir, "notes-test.md");
    File.WriteAllText(saved, rendered + "## Transcript\n\n" + string.Join("\n", lines.Select(l => $"**{l.Who}** ({(int)(l.At / 60)}:{(int)(l.At % 60):00}) {l.Text}")) + "\n", new UTF8Encoding(false));
    Console.WriteLine("Saved to " + saved);
    return 0;
}

// Checks the part that guards against a model making things up, with fixed answers and no model:
// items that cite no real line are removed, owners nobody spoke as are marked unsure, dates nobody
// said are dropped.
int SummarySelfTest()
{
    var lines = Summariser.ParseTranscript(File.ReadAllText(Path.Combine(root, "SpeechSpike", "samples", "sample-call.md")));
    int failed = 0;
    void Expect(bool ok, string what)
    {
        Console.WriteLine((ok ? "  pass  " : "  FAIL  ") + what);
        if (!ok) failed++;
    }

    Expect(lines.Count == 20 && lines[0] is { Number: 1, Who: "Others", At: 2 } && lines[19] is { Number: 20, Who: "You", At: 95 },
        "the sample transcript is read back as 20 numbered lines with speaker and time");
    Expect(Summariser.Numbered(lines).Split('\n')[4] == "[5] (0:29) Others: Okay. Then let's say the beta goes to the Store on the twenty-fourth, not the seventeenth.",
        "lines are numbered for the model as [n] (m:ss) Who: text");

    // A sound answer, wrapped in a code fence the way models often send it.
    var good = Summariser.Check("""
        Here are the notes:
        ```json
        {"summary":"The beta date moved.","decisions":[{"text":"Beta goes to the Store on the twenty-fourth","lines":[5,6]}],
         "open_questions":[{"text":"Why does the island drop frames?","lines":[9,10]}],
         "todos":[{"text":"Write the Store listing text","owner":"Others","due":"Friday","lines":[7]},
                  {"text":"Send the Hindi benchmark numbers","owner":"You","due":"tonight","lines":[17,18]}]}
        ```
        """, lines);
    Expect(good.Summary == "The beta date moved." && good.Decisions.Count == 1 && good.OpenQuestions.Count == 1 && good.Todos.Count == 2 && good.Removed.Count == 0,
        "a sound answer inside a code fence is read whole, with nothing removed");
    Expect(good.Todos[0] is { Owner: "Others", OwnerCertain: true, Due: "Friday" } && good.Todos[1] is { Owner: "You", OwnerCertain: true, Due: "tonight" },
        "owners and due dates are kept when the cited lines back them");
    string rendered = Summariser.Render(good, lines, "test");
    Expect(rendered.Contains("- Beta goes to the Store on the twenty-fourth (0:29, 0:36)") && rendered.Contains("- [ ] **You:** Send the Hindi benchmark numbers, due tonight (1:25, 1:30)"),
        "each item is followed by the times of the lines it rests on");

    // An answer with four faults.
    var bad = Summariser.Check("""
        {"summary":"Pricing was settled.",
         "decisions":[{"text":"Hosted notes will cost five dollars a month","lines":[]},
                      {"text":"Launch in Japan first","lines":[44]}],
         "open_questions":[],
         "todos":[{"text":"Borrow a high refresh monitor","owner":"You","due":null,"lines":[11]},
                  {"text":"Record the demo video","owner":"You","due":"next Monday","lines":[8]},
                  {"text":"Book the launch venue","owner":"Priya","lines":[1]}]}
        """, lines);
    Expect(bad.Decisions.Count == 0 && bad.Removed.Count(r => r.Contains("no line of the transcript")) == 2,
        "a decision that cites no line, or a line that does not exist, is removed and listed");
    Expect(bad.Todos.Count == 3 && bad.Todos[0] is { Owner: "You", OwnerCertain: false },
        "a to-do given to You that rests only on something Others said is marked unsure");
    Expect(bad.Todos[1] is { Due: null, OwnerCertain: true } && bad.Removed.Any(r => r.Contains("next Monday")),
        "a due date nobody said in the cited lines is dropped and listed");
    Expect(bad.Todos[2] is { Owner: "Unsure who", OwnerCertain: false },
        "an owner who is neither You nor Others becomes \"Unsure who\"");
    Expect(Summariser.Render(bad, lines, "test").Contains("**You (unsure):** Borrow a high refresh monitor"),
        "the unsure owner is shown as unsure in the notes");

    var none = Summariser.Check("I could not find anything worth noting in this call.", lines);
    Expect(none.Summary.Length == 0 && none.Todos.Count == 0 && none.Removed.Count == 1, "an answer that is not JSON gives no notes and says why");
    var broken = Summariser.Check("{\"summary\": \"cut off in the mid", lines);
    Expect(broken.Summary.Length == 0 && broken.Removed.Count == 1, "an answer cut off part-way gives no notes and says why");
    var odd = Summariser.Check("{\"summary\": 7, \"decisions\": \"none\", \"todos\": [\"call back\", {\"text\": \"\", \"lines\": [1]}, {\"lines\": [2]}]}", lines);
    Expect(odd.Summary.Length == 0 && odd.Decisions.Count == 0 && odd.Todos.Count == 0, "fields of the wrong type are ignored without an error");

    // A call where the other voices were told apart.
    var three = new List<TranscriptLine>
    {
        new(1, 2, "Speaker 1", "Can someone send the contract by Monday?"),
        new(2, 6, "Speaker 2", "I'll send it by Monday."),
        new(3, 9, "You", "And I'll book the room."),
    };
    var apart = Summariser.Check("""
        {"summary":"Contract and room.","decisions":[],"open_questions":[],
         "todos":[{"text":"Send the contract","owner":"speaker 2","due":"by Monday","lines":[2]},
                  {"text":"Book the room","owner":"You","lines":[3]},
                  {"text":"Send the contract","owner":"Speaker 1","lines":[2]},
                  {"text":"Order lunch","owner":"Speaker 3","lines":[1]}]}
        """, three);
    Expect(apart.Todos[0] is { Owner: "Speaker 2", OwnerCertain: true, Due: "by Monday" } && apart.Todos[1] is { Owner: "You", OwnerCertain: true },
        "with voices told apart, a to-do belongs to the voice that took it");
    Expect(apart.Todos[2] is { Owner: "Speaker 1", OwnerCertain: false } && apart.Todos[3] is { Owner: "Unsure who" },
        "a voice that did not speak in the cited lines is unsure, and a voice that is not in the call is nobody");

    Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} checks FAILED.");
    return failed == 0 ? 0 : 1;
}

// One line saying who will write the summary after a call, as things stand right now.
string NotesLine(bool cloudNotes) => ChatModel.Find(cloudNotes) switch
{
    null => "After a call you get the transcript only: no language model is set up for the summary. Run \"SpeechSpike summarize\" to see the choices.",
    { IsRemote: true } model => $"After a call, {model.Name} writes the summary and to-dos. The transcript text is sent there; the audio is not.",
    var model => $"After a call, {model.Name} writes the summary and to-dos. Nothing leaves this PC.",
};

// The engine call notes use when dictation has no local model loaded to share.
ISpeechEngine CallEngine(string language)
{
    if (language == "hi" && SarvamEngine.FindKey() is { } key) return new SarvamEngine(key, "hi");
    var model = LocalModel.All.First(x => x.Key == (language == "hi" ? "omnilingual-300m" : "parakeet-v3"));
    return model.Create(modelsRoot, language, threads: 4)!;
}

string CallEngineName(string language) =>
    language == "hi" && SarvamEngine.FindKey() != null ? "Sarvam saaras:v4, transcribe (cloud)"
    : LocalModel.All.First(x => x.Key == (language == "hi" ? "omnilingual-300m" : "parakeet-v3")).Label;

// Hindi and Hinglish dictation: Sarvam first. If it has not answered after a head start, ElevenLabs
// is asked too and the first answer wins. If neither answers, a model on this PC does. Returns null
// when no key is set at all.
RoutedEngine? HindiRoute(string mode)
{
    var services = new List<(ISpeechEngine, string)>();
    if (SarvamEngine.FindKey() is { } sarvam) services.Add((new SarvamEngine(sarvam, "hi", mode), "Sarvam"));
    if (ElevenLabsEngine.FindKey() is { } eleven) services.Add((new ElevenLabsEngine(eleven, "hi"), "ElevenLabs"));
    if (services.Count == 0) return null;
    var local = LocalModel.All.First(x => x.Key == "omnilingual-300m");
    var lastResort = new Lazy<ISpeechEngine?>(() => local.IsPresent(modelsRoot) ? local.Create(modelsRoot, "hi", threads: 4) : null);
    int headStart = int.TryParse(Environment.GetEnvironmentVariable("FORTY_HEAD_START_MS"), out int given) ? Math.Clamp(given, 100, 10_000) : 1200;
    return new RoutedEngine(services.ToArray(), local.IsPresent(modelsRoot) ? () => lastResort.Value : null, "Omnilingual", headStart);
}

// Who transcribes a call. With cloudCalls, ElevenLabs takes the others' side because it can tell
// their voices apart; your own side is one voice, so in English the model on this PC does it for
// free. Without cloudCalls, or if ElevenLabs fails, everything stays with the standby engine.
CallEngines MakeCallEngines(string language, ISpeechEngine? shared, bool cloudCalls)
{
    LazyEngine? owned = shared == null ? new LazyEngine(CallEngineName(language), () => new SharedEngine(CallEngine(language), new object())) : null;
    var standby = new SegmentTranscriber(shared ?? owned!);
    if (cloudCalls && ElevenLabsEngine.FindKey() is { } key)
    {
        var scribe = new ScribeTranscriber(key, language, standby);
        return new CallEngines(scribe, language == "hi" ? scribe : standby, owned);
    }
    return new CallEngines(standby, standby, owned);
}

// One line saying who will transcribe a call, as things stand right now.
string CallsLine(string language, bool cloudCalls) =>
    cloudCalls && ElevenLabsEngine.FindKey() != null
        ? "Call audio goes to ElevenLabs, which tells the other voices apart" + (language == "hi" ? "." : "; your own side is transcribed on this PC.") +
          " ElevenLabs keeps what it is sent. If it fails, " + CallEngineName(language) + " writes the transcript."
    : cloudCalls ? "ElevenLabs was asked for but has no key, so " + CallEngineName(language) + " transcribes calls. Run setup-keys.cmd to add the key."
    : "Calls are transcribed by " + CallEngineName(language) + "; the other voices are not told apart.";

// A short stretch of a public test sentence, for checking that a service answers.
Clip CheckSample() => TestClips.Load(clipsRoot, "en").FirstOrDefault()?.Audio.Take(2.5) ?? new Clip(new float[16000 * 2], 16000);

// Checks everything the daily flow depends on and prints who does what.
//   check [hi] [--cloud]
int Check(string[] options)
{
    string language = options.Contains("hi") ? "hi" : "en";
    bool cloudCalls = options.Contains("--cloud") || options.Contains("--cloud-calls"), cloudNotes = options.Contains("--cloud") || options.Contains("--cloud-notes");
    var problems = new List<string>();
    var sample = CheckSample();

    Console.WriteLine("Keys");
    Keys.PrintStatus();

    Console.WriteLine("Services");
    foreach (var (service, key, check) in new (string, string?, Func<string, Keys.Verdict>)[]
    {
        ("Sarvam", SarvamEngine.FindKey(), k => Keys.CheckSarvam(k, sample)),
        ("ElevenLabs", ElevenLabsEngine.FindKey(), k => Keys.CheckElevenLabs(k, sample)),
    })
    {
        if (key == null) { Console.WriteLine($"  {service,-11} no key"); problems.Add($"{service} has no key. Run setup-keys.cmd."); continue; }
        var verdict = check(key);
        Console.WriteLine(string.Create(inv, $"  {service,-11} {(verdict.Works ? "works" : "PROBLEM")}: {verdict.Detail} ({verdict.Ms / 1000:F1} s)"));
        if (!verdict.Works) problems.Add($"{service}: {verdict.Detail}.");
    }

    Console.WriteLine("Models on this PC");
    foreach (var (key, use) in new[] { ("parakeet-v3", "English dictation and calls"), ("omnilingual-300m", "Hindi when no service answers") })
    {
        var model = LocalModel.All.First(m => m.Key == key);
        bool present = model.IsPresent(modelsRoot);
        Console.WriteLine($"  {model.Label,-32} {(present ? "present" : "MISSING")}  ({use})");
        if (!present) problems.Add($"{model.Label} is missing from the models folder.");
    }

    Console.WriteLine("Island");
    bool island = Process.GetProcessesByName("IslandSpike").Length > 0;
    Console.WriteLine(island ? "  running" : "  not running (start-forty.cmd starts it)");

    Console.WriteLine("Who does what");
    Console.WriteLine("  English dictation   Parakeet on this PC. No internet needed.");
    using (var hindi = HindiRoute("transcribe"))
        Console.WriteLine("  Hindi dictation     " + (hindi?.Name ?? "Omnilingual on this PC only (no service key is set)") + ".");
    Console.WriteLine("  Call transcript     " + CallsLine(language, cloudCalls));
    Console.WriteLine("  Call summary        " + NotesLine(cloudNotes));

    Console.WriteLine();
    Console.WriteLine(problems.Count == 0 ? "Everything needed is in place." : "To fix:");
    foreach (string problem in problems) Console.WriteLine("  - " + problem);
    return problems.Count == 0 ? 0 : 1;
}

// Sends Hindi test clips through the dictation route and times each one. With FORTY_FAULT set
// (sarvam-down, sarvam-hang, eleven-down) it shows what happens when a service fails.
//   route-test [--clips n]
int RouteTest(string[] options)
{
    int at = Array.IndexOf(options, "--clips");
    int count = at >= 0 && at + 1 < options.Length && int.TryParse(options[at + 1], out int n) ? n : 10;
    var clips = TestClips.Load(clipsRoot, "hi").Take(count).ToList();
    using var route = HindiRoute("transcribe");
    if (route == null) { Console.WriteLine("No service key is set."); return 1; }
    Console.WriteLine($"Route: {route.Name}. Fault: {Environment.GetEnvironmentVariable("FORTY_FAULT") ?? "none"}.");

    var tally = new Scoring.Tally();
    var times = new List<double>();
    int failures = 0;
    foreach (var clip in clips)
    {
        if (!options.Contains("--cold")) route.Warm();   // what key-down does
        Thread.Sleep(700);                               // you are speaking
        long start = Stopwatch.GetTimestamp();
        string text;
        try { text = route.Transcribe(clip.Audio); }
        catch (InvalidOperationException ex) { failures++; Console.WriteLine($"  {clip.Name}  FAILED: {ex.Message}"); continue; }
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        times.Add(ms);
        tally += Scoring.Score(clip.Reference, text, "hi");
        Console.WriteLine(string.Create(inv, $"  {clip.Name}  {clip.Audio.Seconds,4:F1} s of speech  {ms,6:F0} ms  by {route.LastAnsweredBy}{(route.LastTrouble == null ? "" : "  [" + route.LastTrouble + "]")}"));
        Thread.Sleep(800);
    }
    if (times.Count > 0)
        Console.WriteLine(string.Create(inv, $"{times.Count} answered, {failures} failed. Wait after speaking: middle {Median(times):F0} ms, longest {times.Max():F0} ms. Word errors {tally.Wer:F1}%."));
    return failures == 0 ? 0 : 1;
}

// Long dictation: a minute of English through the model on this PC, and 40 seconds of Hindi
// through Sarvam, which takes at most 30 seconds in one request and so has to be sent in pieces.
int LongTest(string[] options)
{
    Clip Join(IEnumerable<TestClip> clips) =>
        Clip.Concat(clips.SelectMany(c => new[] { c.Audio, new Clip(new float[(int)(16000 * 0.8)], 16000) }));

    // The same audio fed in 50 ms chunks at the speed it was spoken, as the microphone delivers it,
    // with pieces worked on meanwhile. The wait is from the last chunk to the full text.
    (string Text, double WaitSeconds, int Pieces) Spoken(ISpeechEngine engine, Clip audio)
    {
        var live = new LivePieces(engine);
        var pacing = Stopwatch.StartNew();
        for (int offset = 0, index = 0; offset < audio.Samples.Length; offset += 800, index++)
        {
            var chunk = new short[Math.Min(800, audio.Samples.Length - offset)];
            for (int i = 0; i < chunk.Length; i++) chunk[i] = (short)Math.Clamp((int)MathF.Round(audio.Samples[offset + i] * 32767f), short.MinValue, short.MaxValue);
            live.Add(chunk);
            int wait = (index + 1) * 50 - (int)pacing.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
        long stopped = Stopwatch.GetTimestamp();
        string text = live.Pieces > 0 ? live.Finish() : engine.Transcribe(audio);
        return (text, Stopwatch.GetElapsedTime(stopped).TotalSeconds, live.Pieces);
    }

    if (!options.Contains("hi"))
    {
        var english = TestClips.Load(clipsRoot, "en").Take(6).ToList();
        var model = LocalModel.All.First(m => m.Key == "parakeet-v3");
        using var engine = model.Create(modelsRoot, "en", threads: 4)!;
        engine.Transcribe(new Clip(new float[16000], 16000));
        var audio = Join(english);
        string reference = string.Join(" ", english.Select(c => c.Reference));
        long start = Stopwatch.GetTimestamp();
        string text = engine.Transcribe(audio);
        Console.WriteLine(string.Create(inv, $"English, {audio.Seconds:F0} s, {engine.Name}"));
        Console.WriteLine(string.Create(inv, $"  all at the end:        wait {Stopwatch.GetElapsedTime(start).TotalSeconds:F1} s, word errors {Scoring.Score(reference, text, "en").Wer:F1}%"));
        var spoken = Spoken(engine, audio);
        Console.WriteLine(string.Create(inv, $"  worked on as you talk: wait {spoken.WaitSeconds:F1} s, word errors {Scoring.Score(reference, spoken.Text, "en").Wer:F1}%, {spoken.Pieces} pieces done before you stopped"));
    }
    if (!options.Contains("en") && SarvamEngine.FindKey() is { } key)
    {
        var hindi = TestClips.Load(clipsRoot, "hi").Take(4).ToList();
        using var engine = new SarvamEngine(key, "hi");
        var audio = Join(hindi);
        string reference = string.Join(" ", hindi.Select(c => c.Reference));
        engine.Warm();
        Thread.Sleep(600);
        long start = Stopwatch.GetTimestamp();
        string text = engine.Transcribe(audio);
        Console.WriteLine(string.Create(inv, $"Hindi, {audio.Seconds:F0} s, Sarvam"));
        Console.WriteLine(string.Create(inv, $"  all at the end:        wait {Stopwatch.GetElapsedTime(start).TotalSeconds:F1} s, word errors {Scoring.Score(reference, text, "hi").Wer:F1}%"));
        var spoken = Spoken(engine, audio);
        Console.WriteLine(string.Create(inv, $"  worked on as you talk: wait {spoken.WaitSeconds:F1} s, word errors {Scoring.Score(reference, spoken.Text, "hi").Wer:F1}%, {spoken.Pieces} pieces done before you stopped"));
    }
    return 0;
}

// Call transcript with voices told apart, with no call: a made-up "others" stream is built from
// test clips of different readers with pauses between them, including one long silence, and goes
// through the same code a real call does. Shows the lines, their times against where each clip
// really started, and how much audio was sent.
//   speakers-test [--local]
int SpeakersTest(string[] options)
{
    // The Hindi test set has two studio readers: clips 0-4 are one, clips 5-9 the other. (The
    // English set turned out to be a single reader, so it cannot test this.)
    var hindi = TestClips.Load(clipsRoot, "hi");
    if (hindi.Count < 10) { Console.WriteLine("Test clips are missing; run fetch-clips first."); return 1; }
    var order = new[] { hindi[0], hindi[5], hindi[1], hindi[6] };
    var gaps = new[] { 0.9, 5.0, 0.9, 0.5 };

    // How high each voice is, as a check that the two readers really are different people.
    static double Pitch(Clip clip)
    {
        const int frame = 640, hop = 320;
        var found = new List<double>();
        var loud = new List<double>();
        for (int at = 0; at + frame < clip.Samples.Length; at += hop)
        {
            double energy = 0;
            for (int i = 0; i < frame; i++) energy += clip.Samples[at + i] * clip.Samples[at + i];
            loud.Add(energy);
        }
        double strong = loud.OrderBy(x => x).ElementAt(loud.Count * 6 / 10);
        for (int f = 0, at = 0; at + frame + 230 < clip.Samples.Length; at += hop, f++)
        {
            if (loud[f] < strong) continue;
            double best = 0; int bestLag = 0;
            for (int lag = 46; lag <= 229; lag++)   // 348 Hz down to 70 Hz
            {
                double sum = 0;
                for (int i = 0; i < frame; i++) sum += clip.Samples[at + i] * clip.Samples[at + i + lag];
                if (sum > best) { best = sum; bestLag = lag; }
            }
            if (bestLag > 0 && best > 0.4 * loud[f]) found.Add(16000.0 / bestLag);
        }
        return found.Count == 0 ? 0 : found.OrderBy(x => x).ElementAt(found.Count / 2);
    }
    Console.WriteLine("Voice pitch of the four clips: " + string.Join(", ", order.Select(c => $"{Pitch(c.Audio):F0} Hz")) + " (clips 1 and 3 are one reader, 2 and 4 the other)");
    var samples = new List<float>();
    var starts = new List<double>();
    for (int i = 0; i < order.Length; i++)
    {
        starts.Add(samples.Count / 16000.0);
        samples.AddRange(order[i].Audio.Samples);
        samples.AddRange(new float[(int)(gaps[i] * 16000)]);
    }
    var others = new Clip(samples.ToArray(), 16000);

    using var engines = MakeCallEngines("hi", null, cloudCalls: !options.Contains("--local"));
    Console.WriteLine(CallsLine("hi", !options.Contains("--local")));
    long begin = Stopwatch.GetTimestamp();
    var (markdown, path, _) = CallTranscript.Write("the speakers test", others, null, engines.Others, engines.Mine, outDir);
    double took = Stopwatch.GetElapsedTime(begin).TotalSeconds;

    Console.WriteLine(markdown);
    Console.WriteLine("Each clip really started at: " + string.Join(", ", starts.Select(s => $"{(int)(s / 60)}:{(int)(s % 60):00}")));
    Console.WriteLine("What was really said:");
    foreach (var clip in order) Console.WriteLine("  " + clip.Reference);
    if (engines.Others is ScribeTranscriber scribe)
        Console.WriteLine(string.Create(inv, $"Stream length {others.Seconds:F0} s; sent to ElevenLabs {scribe.SecondsSent:F0} s after cutting silence; voices found: {scribe.VoicesFound}{(scribe.FellBack == null ? "" : "; FELL BACK: " + scribe.FellBack)}."));
    Console.WriteLine(string.Create(inv, $"Took {took:F1} s. Saved to {path}"));
    return 0;
}

// Call notes by themselves: watch for a call, ask through the island, record after a yes.
//   watch-calls [hi] [--app name] [--no-mic] [--once]
int WatchCalls(string[] options)
{
    int at = Array.IndexOf(options, "--app");
    string? app = at >= 0 && at + 1 < options.Length ? options[at + 1] : null;
    string language = options.Contains("hi") ? "hi" : "en";
    using var link = new IslandLink();
    link.Send("idle");
    Console.WriteLine(link.Connected ? "Watching for a call. The island will ask before anything is recorded."
                                     : "The island is not running. Start it first; without it there is nobody to ask.");
    if (!link.Connected) return 1;
    bool cloudCalls = options.Contains("--cloud") || options.Contains("--cloud-calls");
    bool cloudNotes = options.Contains("--cloud") || options.Contains("--cloud-notes");
    Console.WriteLine(CallsLine(language, cloudCalls));
    Console.WriteLine(NotesLine(cloudNotes));
    var calls = new CallWatcher(link, () => MakeCallEngines(language, null, cloudCalls), () => ChatModel.Find(cloudNotes), outDir, app, useMicrophone: !options.Contains("--no-mic"), once: options.Contains("--once"));
    calls.Run(CancellationToken.None);
    return 0;
}

// Hold a key anywhere, speak, let go: the text is typed where the cursor is.
int Dictate(string[] options)
{
    string? Option(string name)
    {
        int at = Array.IndexOf(options, name);
        return at >= 0 && at + 1 < options.Length ? options[at + 1] : null;
    }

    string language = options.FirstOrDefault(o => o is "en" or "hi") ?? "en";
    string key = Option("--key") ?? "rctrl";
    string mode = Option("--mode") ?? "transcribe";
    string? sarvamKey = SarvamEngine.FindKey(), elevenKey = ElevenLabsEngine.FindKey();
    // English never needs the internet. Hindi takes the route: Sarvam, ElevenLabs behind it, this PC behind both.
    string pick = Option("--engine") ?? (language == "en" ? "parakeet" : sarvamKey != null || elevenKey != null ? "route" : "omnilingual");

    ISpeechEngine? engine = null;
    IStreamingEngine? streaming = null;
    IslandLink? linkForPolicy = null;   // set once dictation exists; the model asks it whether the PC is in battery saver

    switch (pick)
    {
        case "route":
            engine = HindiRoute(mode);
            engine!.Warm();
            break;
        case "sarvam" when sarvamKey != null: engine = new SarvamEngine(sarvamKey, language, mode); break;
        case "sarvam-stream" when sarvamKey != null: streaming = new SarvamStreamEngine(sarvamKey, mode); break;
        case "elevenlabs" when elevenKey != null: engine = new ElevenLabsEngine(elevenKey, language); break;
        case "elevenlabs-stream" when elevenKey != null: streaming = new ElevenStreamEngine(elevenKey); break;
        case "sarvam" or "sarvam-stream":
            Console.WriteLine("There is no Sarvam key. Run setup-keys.cmd and paste it there.");
            return 1;
        case "elevenlabs" or "elevenlabs-stream":
            Console.WriteLine("There is no ElevenLabs key. Run setup-keys.cmd and paste it there.");
            return 1;
        default:
            var model = LocalModel.All.FirstOrDefault(m => m.Key.StartsWith(pick, StringComparison.OrdinalIgnoreCase));
            if (model == null) { Console.WriteLine($"Unknown engine '{pick}'. Choices: parakeet, moonshine, omnilingual, whisper, sarvam, sarvam-stream, elevenlabs, elevenlabs-stream."); return 1; }
            if (!model.Languages.Contains(language)) { Console.WriteLine($"{model.Label} does not cover this language."); return 1; }
            if (!model.IsPresent(modelsRoot)) { Console.WriteLine($"{model.Label} is not in the models folder."); return 1; }
            Console.WriteLine($"Loading {model.Label}...");
            // In battery saver the island says so, and the model is let go after a minute unused (F128).
            engine = new IdleEngine(model.Label, () =>
            {
                var made = model.Create(modelsRoot, language, threads: 4)!;
                made.Transcribe(new Clip(new float[16000], 16000));   // first run pays one-time costs
                return made;
            }, () => linkForPolicy?.Saver == true, preload: true);
            break;
    }

    // Dictation and call notes take turns on one loaded model.
    ISpeechEngine? shared = engine == null ? null : new SharedEngine(engine, new object());
    var dictation = new Dictation(language, shared, streaming, key);
    linkForPolicy = dictation.Link;
    var watcher = new KeyWatcher();
    watcher.Key += dictation.OnKey;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; watcher.Stop(); };

    bool watchCalls = options.Contains("--calls");
    // --local wins over everything: nothing about a call leaves this PC.
    bool local = options.Contains("--local");
    bool cloudCalls = !local && (options.Contains("--cloud") || options.Contains("--cloud-calls"));
    bool cloudNotes = !local && (options.Contains("--cloud") || options.Contains("--cloud-notes"));
    if (watchCalls)
    {
        var calls = new CallWatcher(dictation.Link, () => MakeCallEngines(language, shared, cloudCalls), () => ChatModel.Find(cloudNotes), outDir, Option("--calls-app"),
                                    useMicrophone: !options.Contains("--no-mic"), once: false);
        new Thread(() => calls.Run(CancellationToken.None)) { IsBackground = true, Name = "call watcher" }.Start();
    }

    Console.WriteLine();
    Console.WriteLine($"Dictation is on. Language: {(language == "hi" ? "Hindi" : "English")}. Engine: {dictation.EngineName}.");
    Console.WriteLine($"Click into any app, hold {dictation.KeyName}, speak, and let go. The text is typed where the cursor is.");
    Console.WriteLine("The microphone is open only while the key is held. Nothing is saved. Press Ctrl+C in this window to quit.");
    dictation.Greet();
    Console.WriteLine(dictation.IslandConnected
        ? "The island is running and will show when it is listening."
        : "The island is not running; start it with run-island.cmd to see when it is listening.");
    if (watchCalls)
    {
        Console.WriteLine("Call notes are on: when a call starts, the island asks whether to take notes. Nothing is recorded before you say yes.");
        Console.WriteLine(CallsLine(language, cloudCalls));
        Console.WriteLine(NotesLine(cloudNotes));
    }
    if (language == "hi" && sarvamKey == null && elevenKey == null)
        Console.WriteLine("No service key is set, so Hindi uses the model on this PC, which gets about one word in six wrong. Run setup-keys.cmd.");
    Console.WriteLine();
    watcher.Run();
    engine?.Dispose();
    return 0;
}

static string FindSpikesRoot()
{
    if (Environment.GetEnvironmentVariable("FORTY_SPIKES") is { Length: > 0 } given && Directory.Exists(given)) return given;
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, "packages")) && Directory.Exists(Path.Combine(dir.FullName, "SpeechSpike")))
            return dir.FullName;
    throw new DirectoryNotFoundException("Could not find the spikes folder above " + AppContext.BaseDirectory);
}

static double Median(List<double> values)
{
    var sorted = values.OrderBy(v => v).ToList();
    return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
}

static (string Text, double Ms) Timed(ISpeechEngine engine, Clip clip)
{
    long start = Stopwatch.GetTimestamp();
    string text = engine.Transcribe(clip);
    return (text, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
}

void Bench(string[] only)
{
    bool runLocal = only.Length == 0 || only.Contains("local");
    // Cloud engines: each is included when it was asked for (or nothing was specified) and its key is set.
    var clouds = new (string Tag, string KeyName, Func<string, ISpeechEngine?> Make)[]
    {
        ("sarvam", "SARVAM_API_KEY", language => SarvamEngine.FindKey() is { } k ? new SarvamEngine(k, language) : null),
        ("elevenlabs", "ELEVENLABS_API_KEY", language => ElevenLabsEngine.FindKey() is { } k ? new ElevenLabsEngine(k, language) : null),
    };
    var report = new StringBuilder();
    var transcripts = new StringBuilder("language\tclip\tengine\tms\treference\theard\n");
    void Say(string line = "") { Console.WriteLine(line); report.AppendLine(line); }

    Say($"S4 speech spike — {DateTime.Now:yyyy-MM-dd HH:mm}");
    Say($"CPU only, {Environment.ProcessorCount} logical cores. Times are from handing over the audio to having the text.");
    foreach (var cloud in clouds.Where(c => only.Length == 0 || only.Contains(c.Tag)))
    {
        using var probe = cloud.Make("en");
        Say(probe == null ? $"{cloud.Tag}: {cloud.KeyName} is not set, so it was skipped." : $"{cloud.Tag}: key found, included.");
    }

    foreach (string language in new[] { "en", "hi" })
    {
        var clips = TestClips.Load(clipsRoot, language);
        Say();
        Say($"══ {(language == "en" ? "English" : "Hindi")}: {clips.Count} clips, {clips.Sum(c => c.Audio.Seconds).ToString("F0", inv)} s in total ══");
        if (clips.Count == 0) { Say("  no clips; run fetch-clips first"); continue; }

        var tenSeconds = Clip.Concat(clips.Select(c => c.Audio)).Take(10.0);
        var accuracy = new List<string>();
        var latency = new List<string>();

        if (runLocal)
            foreach (var model in LocalModel.All.Where(m => m.Languages.Contains(language)))
            {
                if (!model.IsPresent(modelsRoot)) { accuracy.Add($"  {model.Label,-42} not downloaded yet"); continue; }

                GC.Collect();
                long memoryBefore = Process.GetCurrentProcess().PrivateMemorySize64;
                long loadStart = Stopwatch.GetTimestamp();
                using (var engine = model.Create(modelsRoot, language, threads: 4)!)
                {
                    engine.Transcribe(clips[0].Audio.Take(2.0));   // first run pays one-time costs
                    double loadSeconds = Stopwatch.GetElapsedTime(loadStart).TotalSeconds;
                    double memoryMb = (Process.GetCurrentProcess().PrivateMemorySize64 - memoryBefore) / 1048576.0;

                    var tally = new Scoring.Tally();
                    var times = new List<double>();
                    double audio = 0;
                    foreach (var clip in clips)
                    {
                        var (text, ms) = Timed(engine, clip.Audio);
                        tally += Scoring.Score(clip.Reference, text, language);
                        times.Add(ms);
                        audio += clip.Audio.Seconds;
                        transcripts.Append(string.Create(inv, $"{language}\t{clip.Name}\t{model.Label}\t{ms:F0}\t{clip.Reference}\t{text}\n"));
                    }
                    accuracy.Add(string.Create(inv, $"  {model.Label,-42} {tally.Wer,6:F1}%  {tally.Cer,6:F1}%  {Median(times),7:F0} ms  {audio * 1000 / times.Sum(),5:F0}x  {loadSeconds,5:F1} s  {memoryMb,6:F0} MB"));
                }

                var row = new StringBuilder($"  {model.Label,-42}");
                foreach (int threads in new[] { 1, 2, 4, 8 })
                {
                    if (!model.ThreadSweep.Contains(threads)) { row.Append($"{"-",10}"); continue; }
                    using var engine = model.Create(modelsRoot, language, threads)!;
                    engine.Transcribe(tenSeconds.Take(2.0));
                    var runs = new List<double>();
                    for (int i = 0; i < 5; i++) runs.Add(Timed(engine, tenSeconds).Ms);
                    row.Append(string.Create(inv, $"{Median(runs),7:F0} ms"));
                }
                latency.Add(row.ToString());
            }

        foreach (var cloud in clouds.Where(c => only.Length == 0 || only.Contains(c.Tag)))
        {
            using var engine = cloud.Make(language);
            if (engine == null) continue;
            try
            {
                engine.Transcribe(clips[0].Audio.Take(2.0));   // opens the connection
                var tally = new Scoring.Tally();
                var times = new List<double>();
                foreach (var clip in clips)
                {
                    Thread.Sleep(1100);   // stay well inside 60 requests a minute
                    var (text, ms) = Timed(engine, clip.Audio);
                    tally += Scoring.Score(clip.Reference, text, language);
                    times.Add(ms);
                    transcripts.Append(string.Create(inv, $"{language}\t{clip.Name}\t{engine.Name}\t{ms:F0}\t{clip.Reference}\t{text}\n"));
                }
                accuracy.Add(string.Create(inv, $"  {engine.Name,-42} {tally.Wer,6:F1}%  {tally.Cer,6:F1}%  {Median(times),7:F0} ms  {"",6}  {"",7}  {"",9}   range {times.Min():F0}-{times.Max():F0} ms"));

                var runs = new List<double>();
                for (int i = 0; i < 4; i++) { Thread.Sleep(1100); runs.Add(Timed(engine, tenSeconds).Ms); }
                latency.Add(string.Create(inv, $"  {engine.Name,-42}{Median(runs),7:F0} ms over the network (4 runs, {runs.Min():F0}-{runs.Max():F0} ms)"));
            }
            catch (Exception ex)
            {
                accuracy.Add($"  {engine.Name,-42} failed: {ex.Message}");
            }
        }

        Say($"  {"engine",-42} {"WER",7}  {"CER",7}  {"median",10}  {"speed",6}  {"load",7}  {"memory",9}");
        accuracy.ForEach(Say);
        Say();
        Say($"  A 10-second utterance, by CPU threads:      {"1",10}{"2",10}{"4",10}{"8",10}");
        latency.ForEach(Say);
    }

    Say();
    Say("WER and CER: word and character error rates after removing punctuation and capitals; lower is better.");
    Say("Speed: seconds of audio processed per second. Memory: growth of this process after loading the model.");
    string suffix = only.Length == 0 ? "" : "-" + string.Join("-", only);
    File.WriteAllText(Path.Combine(outDir, $"s4-speech{suffix}.txt"), report.ToString(), new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(outDir, $"s4-transcripts{suffix}.tsv"), transcripts.ToString(), new UTF8Encoding(false));
}

void Record(string language)
{
    Console.WriteLine($"Language: {(language == "hi" ? "Hindi" : "English")}. Press Enter, speak one sentence, press Enter again.");
    Console.ReadLine();
    Console.WriteLine("Recording... press Enter to stop.");
    var stop = Task.Run(Console.ReadLine);
    var clip = Microphone.Record(() => stop.IsCompleted);
    Console.WriteLine(string.Create(inv, $"Recorded {clip.Seconds:F1} s. The audio stays in memory and is not saved."));
    if (clip.Seconds < 0.5) { Console.WriteLine("That was too short to transcribe."); return; }

    foreach (var model in LocalModel.All.Where(m => m.Languages.Contains(language) && m.IsPresent(modelsRoot)))
    {
        using var engine = model.Create(modelsRoot, language, threads: 4)!;
        engine.Transcribe(clip.Take(1.0));
        var (text, ms) = Timed(engine, clip);
        Console.WriteLine(string.Create(inv, $"\n{model.Label}  ({ms:F0} ms)\n  {text}"));
    }

    if (SarvamEngine.FindKey() is { } key)
        foreach (string mode in language == "hi" ? new[] { "transcribe", "codemix", "translit" } : new[] { "transcribe" })
        {
            using var engine = new SarvamEngine(key, language, mode);
            try
            {
                var (text, ms) = Timed(engine, clip);
                Console.WriteLine(string.Create(inv, $"\n{engine.Name}  ({ms:F0} ms, includes the network)\n  {text}"));
            }
            catch (Exception ex) { Console.WriteLine($"\n{engine.Name} failed: {ex.Message}"); }
        }
    else
        Console.WriteLine("\nSarvam skipped: SARVAM_API_KEY is not set.");

    if (ElevenLabsEngine.FindKey() is { } elevenKey)
    {
        using var engine = new ElevenLabsEngine(elevenKey, language);
        try
        {
            var (text, ms) = Timed(engine, clip);
            Console.WriteLine(string.Create(inv, $"\n{engine.Name}  ({ms:F0} ms, includes the network)\n  {text}"));
        }
        catch (Exception ex) { Console.WriteLine($"\n{engine.Name} failed: {ex.Message}"); }
    }
    else
        Console.WriteLine("\nElevenLabs skipped: ELEVENLABS_API_KEY is not set.");
}

using System.Globalization;

namespace Forty.Core;

/// <summary>What a speech-process message changes on the island.</summary>
/// <param name="Post">Activities to post (or update in place).</param>
/// <param name="Remove">Activity keys to remove.</param>
/// <param name="Level">A new microphone level from 0 to 1, for the listening meter; null when unchanged.</param>
public sealed record SpeechChanges(IReadOnlyList<Activity> Post, IReadOnlyList<string> Remove, double? Level = null)
{
    public static readonly SpeechChanges None = new(Array.Empty<Activity>(), Array.Empty<string>());
    public bool IsEmpty => Post.Count == 0 && Remove.Count == 0 && Level == null;
}

/// <summary>
/// The island's side of the speech process (F116). The speech process says what it is doing in one
/// short line per event ("listening", "level 0.42", "recording Teams", …); this turns each line into
/// activities and keeps one rule above all others: a mark stays on until capture has certainly stopped.
///
/// <list type="bullet">
/// <item>The speech process says capture stopped ("idle", "working", "call-working", "capture-stopped"):
/// the mark goes.</item>
/// <item>The connection breaks while something is capturing: the mark stays, worded as "may still be
/// on", because a speech process that stopped answering may still have the microphone open.</item>
/// <item>The speech process has exited: nothing can be capturing any more, so every mark goes.</item>
/// </list>
///
/// Time is passed in (seconds on any steady clock) so every rule can be tested exactly.
/// </summary>
public sealed class SpeechSession
{
    public const string DictationKey = "speech.dictation", CallKey = "speech.call", WakeKey = "speech.wake", ScreenKey = "speech.screen";
    const string Source = "speech";

    // Segoe Fluent Icons code points.
    public const string MicGlyph = "", RecordGlyph = "", WorkingGlyph = "", DoneGlyph = "",
                        ErrorGlyph = "", NotesGlyph = "", CallGlyph = "", ScreenGlyph = "";

    readonly HashSet<Sense> _on = new();

    /// <summary>Whether a speech process is connected and answering.</summary>
    public bool Connected { get; private set; }

    /// <summary>The connection broke while something was capturing; marks stay until this clears.</summary>
    public bool Unconfirmed { get; private set; }

    /// <summary>When the current call capture started, in clock seconds, and in which app.</summary>
    public double? CallSince { get; private set; }
    public string? CallApp { get; private set; }

    public IReadOnlyCollection<Sense> On => _on;

    /// <summary>Applies one line from the speech process. Unknown or damaged lines change nothing.</summary>
    public SpeechChanges Apply(string line, double now)
    {
        line = line.Trim();
        if (line.Length == 0 || line.Length > 400) return SpeechChanges.None;
        int space = line.IndexOf(' ');
        string kind = (space < 0 ? line : line[..space]).ToLowerInvariant();
        string arg = space < 0 ? "" : line[(space + 1)..].Trim();
        Connected = true;

        switch (kind)
        {
            case "hello":
                return SpeechChanges.None;

            case "level":
                return double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out double level) && _on.Contains(Sense.Microphone)
                    ? SpeechChanges.None with { Level = Math.Clamp(level, 0, 1) }
                    : SpeechChanges.None;

            // ── Dictation ──
            case "listening":
                _on.Add(Sense.Microphone);
                Confirmed();
                return Post(Listening());
            case "working":
                _on.Remove(Sense.Microphone);
                Confirmed();
                return Post(Activity.Live(DictationKey, Source, Priority.Timely, "Writing it down", null, WorkingGlyph, "Writing down what you said"));
            case "done":
                _on.Remove(Sense.Microphone);
                Confirmed();
                return Post(new Activity(DictationKey, Source, Priority.Ambient, "Typed", null, DoneGlyph, "Typed where your cursor is", TimeSpan.FromSeconds(1.6), Accent.Done));
            case "failed":
                _on.Remove(Sense.Microphone);
                Confirmed();
                return Post(new Activity(DictationKey, Source, Priority.Timely, "Couldn't type that", null, ErrorGlyph, "Dictation failed; nothing was typed", TimeSpan.FromSeconds(3), Accent.Error));
            case "idle":
                _on.Remove(Sense.Microphone);
                Confirmed();
                return Gone(DictationKey);

            // ── Call notes ──
            case "call-offer":
                return Post(new Activity(CallKey, Source, Priority.ActNow, "Take call notes?", null, CallGlyph,
                    $"A call started in {Short(arg)}. Take notes? Nothing is recorded unless you say yes.", null, Accent.NeedsYou,
                    Actions: new ActivityActions("Yes", "call-accept", "Not now", "call-decline")));
            case "call-gone":
                return _on.Contains(Sense.Call) ? SpeechChanges.None : Gone(CallKey);
            case "recording":
                _on.Add(Sense.Call);
                Confirmed();
                if (CallSince == null || CallApp != arg) CallSince = now;
                CallApp = arg;
                return Post(Recording());
            case "call-working":
                StopCall();
                return Post(Activity.Live(CallKey, Source, Priority.Timely, "Writing notes", null, WorkingGlyph, "Writing the call notes"));
            case "call-done":
                StopCall();
                return Post(new Activity(CallKey, Source, Priority.Timely, "Notes ready", arg.Length is > 0 and <= 24 ? arg : null, NotesGlyph,
                    "Call notes are ready" + (arg.Length > 0 ? ": " + arg : ""), TimeSpan.FromSeconds(30), Accent.Done,
                    Actions: new ActivityActions("Open", "open-notes")));
            case "call-failed":
                StopCall();
                return Post(new Activity(CallKey, Source, Priority.Timely, "Notes failed", null, ErrorGlyph, "The call notes could not be written", TimeSpan.FromSeconds(6), Accent.Error));

            // ── Reserved for phase 2: the wake word and screen reading ──
            case "armed":
                _on.Add(Sense.WakeWord);
                return Post(Mark(Sense.WakeWord));
            case "disarmed":
                _on.Remove(Sense.WakeWord);
                return Gone(WakeKey);
            case "reading":
                _on.Add(Sense.Screen);
                return Post(Mark(Sense.Screen));
            case "reading-done":
                _on.Remove(Sense.Screen);
                return Gone(ScreenKey);

            // The speech process confirms nothing is captured, or is quitting cleanly.
            case "capture-stopped":
            case "bye":
                return ClearAll(connected: kind != "bye");

            default:
                return SpeechChanges.None;
        }
    }

    /// <summary>The connection broke. Anything that may still be capturing keeps its mark, worded as unconfirmed.</summary>
    public SpeechChanges LinkLost(double now)
    {
        Connected = false;
        if (_on.Count == 0) return ClearAll(connected: false);
        Unconfirmed = true;
        var post = new List<Activity>();
        var remove = new List<string>();
        if (_on.Contains(Sense.Microphone)) post.Add(Listening()); else remove.Add(DictationKey);
        if (_on.Contains(Sense.Call)) post.Add(Recording()); else remove.Add(CallKey);
        if (_on.Contains(Sense.WakeWord)) post.Add(Mark(Sense.WakeWord));
        if (_on.Contains(Sense.Screen)) post.Add(Mark(Sense.Screen));
        return new SpeechChanges(post, remove);
    }

    /// <summary>The speech process has exited: nothing can be capturing, so everything goes.</summary>
    public SpeechChanges ProcessGone() => ClearAll(connected: false);

    // ── Building the activities ──

    Activity Listening() => Unconfirmed
        ? new Activity(DictationKey, Source, Priority.ActNow, "Microphone may be on", null, MicGlyph,
            "The speech process stopped answering while the microphone was on. This stays until it confirms the microphone is off.", null, Accent.Recording, Sense.Microphone)
        : new Activity(DictationKey, Source, Priority.ActNow, "Listening", null, MicGlyph, "Listening", null, Accent.Recording, Sense.Microphone);

    Activity Recording() => Unconfirmed
        ? new Activity(CallKey, Source, Priority.Ambient, "Notes may still be recording", CallApp, RecordGlyph,
            "The speech process stopped answering during call notes. This stays until it confirms recording has stopped.", null, Accent.Recording, Sense.Call,
            new ActivityActions("Stop", "call-stop"))
        : new Activity(CallKey, Source, Priority.Ambient, "Taking notes", CallApp, RecordGlyph,
            $"Taking notes on the {Short(CallApp ?? "")} call", null, Accent.Recording, Sense.Call, new ActivityActions("Stop", "call-stop"));

    static Activity Mark(Sense sense) => sense == Sense.WakeWord
        ? new Activity(WakeKey, Source, Priority.Ambient, "Listening for your wake word", null, MicGlyph, "Listening for your wake word, on this PC only", null, Accent.None, Sense.WakeWord)
        : new Activity(ScreenKey, Source, Priority.Ambient, "Reading your screen", null, ScreenGlyph, "Reading your screen", null, Accent.None, Sense.Screen);

    void Confirmed()
    {
        // A fresh report from the speech process replaces any doubt.
        if (Unconfirmed) Unconfirmed = false;
    }

    void StopCall()
    {
        _on.Remove(Sense.Call);
        Confirmed();
        CallSince = null;
    }

    SpeechChanges ClearAll(bool connected)
    {
        _on.Clear();
        Unconfirmed = false;
        CallSince = null;
        CallApp = null;
        Connected = connected;
        return new SpeechChanges(Array.Empty<Activity>(), new[] { DictationKey, CallKey, WakeKey, ScreenKey });
    }

    static SpeechChanges Post(Activity activity) => new(new[] { activity }, Array.Empty<string>());
    static SpeechChanges Gone(string key) => new(Array.Empty<Activity>(), new[] { key });
    static string Short(string app) => app.Length == 0 ? "current" : app.Length > 18 ? app[..17] + "…" : app;
}

using Forty.Core;

namespace Forty.Tests;

/// <summary>F116: marks that cannot be hidden, and the speech process's messages.</summary>
public static class SpeechTests
{
    static void Apply(ActivityQueue q, SpeechChanges changes, double now)
    {
        foreach (var key in changes.Remove) q.Remove(key, now);
        foreach (var activity in changes.Post) q.Post(activity, now);
    }

    static Activity Song() => Activity.Live("media.now-playing", "media", Priority.Ambient, "Song", null, Glyphs.Music, "Song");

    [Test]
    public static void Listening_takes_the_capsule_with_a_red_microphone()
    {
        var s = new SpeechSession();
        var q = new ActivityQueue();
        q.Post(Song(), 0);
        Apply(q, s.Apply("listening", 1), 1);
        Check.Equal(SpeechSession.DictationKey, q.View.Primary?.Key, "dictation in the capsule");
        Check.Equal(Accent.Recording, q.View.Primary?.Accent, "red");
        Check.Equal(Sense.Microphone, q.View.Primary?.Mark, "it is a mark");
        Check.Equal("media.now-playing", q.View.Satellite?.Key, "the song waits beside it");
        Check.True(s.Apply("level 0.5", 1.1).Level is 0.5, "levels reach the meter");
        Apply(q, s.Apply("working", 2), 2);
        Check.True(q.View.Marks.Count == 0 && s.On.Count == 0, "no mark once the microphone is closed");
    }

    [Test]
    public static void A_call_capture_shows_as_the_dot_at_rest_and_as_the_satellite_beside_anything_else()
    {
        var s = new SpeechSession();
        var q = new ActivityQueue();
        Apply(q, s.Apply("call-offer Teams", 0), 0);
        Check.Equal("Yes", q.View.Primary?.Actions?.Label, "the offer asks first");
        Apply(q, s.Apply("recording Teams", 1), 1);
        Check.True(q.View.Primary == null, "recording does not hold the capsule");
        Check.Equal(SpeechSession.CallKey, q.View.Dots.FirstOrDefault()?.Key, "it is the dot");

        q.Post(Song(), 2);
        Check.Equal("media.now-playing", q.View.Primary?.Key, "music in the capsule");
        Check.Equal(SpeechSession.CallKey, q.View.Satellite?.Key, "the recording mark beside it");

        var approval = Activity.Event("approval", "agent", Priority.ActNow, "Approve?", null, Glyphs.Info, "Approve?");
        q.Post(approval, 3);
        Check.Equal("approval", q.View.Primary?.Key, "an approval takes the capsule");
        Check.Equal(SpeechSession.CallKey, q.View.Satellite?.Key, "the mark keeps the satellite");
        Check.Equal(1, q.View.Waiting, "the song waits");
    }

    [Test]
    public static void No_mode_hides_a_mark()
    {
        foreach (var mode in Enum.GetValues<Mode>())
        {
            var s = new SpeechSession();
            var q = new ActivityQueue();
            q.SetMode(mode, 0);
            Apply(q, s.Apply("recording Zoom", 0), 0);
            bool visible = q.View.Primary?.Mark != null || q.View.Satellite?.Mark != null || q.View.Dots.FirstOrDefault()?.Mark != null;
            Check.True(visible, $"call mark visible in {mode}");

            var s2 = new SpeechSession();
            var q2 = new ActivityQueue();
            q2.SetMode(mode, 0);
            Apply(q2, s2.Apply("listening", 0), 0);
            visible = q2.View.Primary?.Mark != null || q2.View.Satellite?.Mark != null || q2.View.Dots.FirstOrDefault()?.Mark != null;
            Check.True(visible, $"microphone mark visible in {mode}");
        }
    }

    [Test]
    public static void Holding_something_else_never_hides_a_mark()
    {
        var s = new SpeechSession();
        var q = new ActivityQueue();
        q.Post(Song(), 0);
        q.Hold("media.now-playing", 0.5);
        Apply(q, s.Apply("recording Meet", 1), 1);
        Check.Equal("media.now-playing", q.View.Primary?.Key, "what you point at stays");
        Check.Equal(SpeechSession.CallKey, q.View.Satellite?.Key, "and the mark shows beside it");
    }

    [Test]
    public static void A_broken_connection_keeps_the_mark_until_the_process_is_gone()
    {
        var s = new SpeechSession();
        var q = new ActivityQueue();
        Apply(q, s.Apply("recording Teams", 0), 0);
        Apply(q, s.LinkLost(1), 1);
        Check.True(s.Unconfirmed, "capture is unconfirmed");
        Check.True(q.View.Marks.Count == 1, "the mark stays");
        Check.True(q.View.Marks[0].Title.Contains("may", StringComparison.Ordinal), "worded as unconfirmed");
        Apply(q, s.ProcessGone(), 2);
        Check.True(q.View.Marks.Count == 0 && q.Count == 0, "nothing can capture once the process has exited");
    }

    [Test]
    public static void A_broken_connection_with_nothing_capturing_clears_everything()
    {
        var s = new SpeechSession();
        var q = new ActivityQueue();
        Apply(q, s.Apply("call-offer Teams", 0), 0);
        Apply(q, s.LinkLost(1), 1);
        Check.True(!s.Unconfirmed && q.Count == 0, "an unanswered offer goes with the connection");
    }

    [Test]
    public static void A_fresh_report_after_reconnecting_replaces_the_doubt()
    {
        var s = new SpeechSession();
        var q = new ActivityQueue();
        Apply(q, s.Apply("listening", 0), 0);
        Apply(q, s.LinkLost(1), 1);
        Check.Equal("Microphone may be on", q.View.Primary?.Title, "unconfirmed wording");
        Apply(q, s.Apply("idle", 2), 2);
        Check.True(!s.Unconfirmed && q.View.Marks.Count == 0, "the process says the microphone is closed");
    }

    [Test]
    public static void Damaged_or_unknown_lines_change_nothing()
    {
        var s = new SpeechSession();
        foreach (var line in new[] { "", "   ", "level", "level abc", "sing a song", new string('x', 1000), "recording\u0000" })
        {
            var changes = s.Apply(line, 0);
            Check.True(line.StartsWith("recording", StringComparison.Ordinal) || changes.IsEmpty, $"ignored: {line[..Math.Min(line.Length, 12)]}");
        }
        Check.True(s.Apply("level 0.4", 0).Level == null, "a level without an open microphone is ignored");
    }

    [Test]
    public static void Call_notes_flow_from_offer_to_open()
    {
        var s = new SpeechSession();
        var q = new ActivityQueue();
        Apply(q, s.Apply("call-offer Teams", 0), 0);
        Apply(q, s.Apply("recording Teams", 1), 1);
        Check.Equal(1.0, s.CallSince ?? -1, "the capture's start is kept for the elapsed time");
        Check.Equal("Stop", q.View.Dots[0].Actions?.Label, "the mark can be stopped");
        Apply(q, s.Apply("call-working", 60), 60);
        Check.True(q.View.Marks.Count == 0, "no mark while writing notes");
        Apply(q, s.Apply("call-done 3 to-dos", 62), 62);
        Check.Equal("Open", q.View.Primary?.Actions?.Label, "the notes can be opened");
        Check.Equal("3 to-dos", q.View.Primary?.Detail, "with the count");
    }
}

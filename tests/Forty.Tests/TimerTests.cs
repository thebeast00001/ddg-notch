using Forty.Core;
using Forty.Motion;

namespace Forty.Tests;

public static class TimerTests
{
    [Test]
    public static void A_timer_counts_down_pauses_resumes_and_rings_once()
    {
        var t = new CountdownTimer();
        Check.Equal(TimerPhase.Off, t.Phase, "starts off");
        t.Start(TimeSpan.FromMinutes(10), 100);
        Check.Equal(TimerPhase.Running, t.Phase, "running");
        Check.Near(600, t.Remaining(100).TotalSeconds, 1e-9, "the whole time is left at the start");
        Check.Near(540, t.Remaining(160).TotalSeconds, 1e-9, "a minute later");
        Check.Near(0.1, t.Progress(160), 1e-9, "a tenth has gone");

        t.Pause(160);
        Check.Near(540, t.Remaining(400).TotalSeconds, 1e-9, "paused, the time left holds still");
        t.Resume(400);
        Check.Near(530, t.Remaining(410).TotalSeconds, 1e-9, "resumed, it counts from where it was");

        Check.True(!t.Tick(900), "not yet");
        Check.True(t.Tick(940), "rings when time is up");
        Check.Equal(TimerPhase.Ringing, t.Phase, "ringing");
        Check.True(!t.Tick(941), "rings once, not on every tick");
        Check.Equal(TimeSpan.Zero, t.Remaining(941), "nothing is left while it rings");
        t.Tick(940 + CountdownTimer.RingSeconds + 0.1);
        Check.Equal(TimerPhase.Off, t.Phase, "after a minute of ringing it stops by itself");
    }

    [Test]
    public static void A_timer_is_kept_between_a_second_and_a_day_and_more_time_can_be_added()
    {
        var t = new CountdownTimer();
        t.Start(TimeSpan.Zero, 0);
        Check.Equal(CountdownTimer.Shortest, t.Duration, "at least a second");
        t.Start(TimeSpan.FromDays(3), 0);
        Check.Equal(CountdownTimer.Longest, t.Duration, "at most a day");

        t.Start(TimeSpan.FromMinutes(5), 0);
        t.Add(TimeSpan.FromMinutes(1), 30);
        Check.Near(330, t.Remaining(30).TotalSeconds, 1e-9, "+1 min while running");
        Check.Equal(TimeSpan.FromMinutes(6), t.Duration, "the length grows with it, so the bar stays true");
        t.Pause(30);
        t.Add(TimeSpan.FromMinutes(1), 99);
        Check.Near(390, t.Remaining(99).TotalSeconds, 1e-9, "+1 min while paused");
        t.Cancel();
        Check.Equal(TimerPhase.Off, t.Phase, "cancelled");
        t.Add(TimeSpan.FromMinutes(1), 100);
        Check.Equal(TimerPhase.Off, t.Phase, "a cancelled timer doesn't come back by adding time");
    }

    [Test]
    public static void The_countdown_reads_like_a_clock_and_never_shows_zero_while_running()
    {
        Check.Equal("15:00", CountdownTimer.Format(TimeSpan.FromMinutes(15)), "minutes");
        Check.Equal("0:01", CountdownTimer.Format(TimeSpan.FromSeconds(0.2)), "a fraction of a second still reads as one");
        Check.Equal("4:59", CountdownTimer.Format(TimeSpan.FromSeconds(298.4)), "rounded up");
        Check.Equal("1:04:59", CountdownTimer.Format(new TimeSpan(1, 4, 59)), "hours");
        Check.Equal("0:00", CountdownTimer.Format(TimeSpan.Zero), "zero only at the end");
        Check.Equal("1 hour 30 minutes", CountdownTimer.Words(TimeSpan.FromMinutes(90)), "in words");
        Check.Equal("1 minute 5 seconds", CountdownTimer.Words(TimeSpan.FromSeconds(65)), "singular and plural");
        Check.Equal("24 hours", CountdownTimer.Words(TimeSpan.FromHours(24)), "a whole day in hours");
    }

    [Test]
    public static void Durations_are_read_the_way_people_say_them()
    {
        TimeSpan? D(string s) => Durations.Find(s);
        Check.Equal(TimeSpan.FromMinutes(10), D("10 minutes"), "10 minutes");
        Check.Equal(TimeSpan.FromMinutes(25), D("25 min"), "25 min");
        Check.Equal(TimeSpan.FromMinutes(90), D("1 hour 30 minutes"), "1 hour 30 minutes");
        Check.Equal(TimeSpan.FromMinutes(90), D("1h30m"), "1h30m");
        Check.Equal(TimeSpan.FromSeconds(90), D("90 sec"), "90 sec");
        Check.Equal(TimeSpan.FromMinutes(30), D("half an hour"), "half an hour");
        Check.Equal(TimeSpan.FromMinutes(15), D("a quarter of an hour"), "a quarter of an hour");
        Check.Equal(TimeSpan.FromSeconds(90), D("a minute and a half"), "a minute and a half");
        Check.Equal(TimeSpan.FromMinutes(5), D("five minutes"), "words for numbers");
        Check.Equal(TimeSpan.FromMinutes(45), D("forty-five minutes"), "forty-five");
        Check.Equal(TimeSpan.FromSeconds(150), D("2.5 minutes"), "decimals");
        Check.Equal((TimeSpan?)null, D("open Pandas"), "\"as\" inside a word isn't a duration");
        Check.Equal((TimeSpan?)null, D("set a timer"), "no duration at all");
    }

    [Test]
    public static void Timer_and_mirror_requests_go_to_the_right_place()
    {
        Request R(string s) => RequestRules.Parse(s);
        var set = R("Set a timer for 10 minutes");
        Check.True(set.Kind == RequestKind.Timer && set.Agent == Cast.Tempo, "the timer is Tempo's");
        Check.Equal("start:600", set.Argument, "with its length");
        Check.Equal("start:1500", R("start a 25 min timer").Argument, "a 25 minute timer (not \"open\" an app called timer)");
        Check.Equal("start:300", R("hey forty, timer 5 minutes please").Argument, "politely");
        Check.Equal(TimerCommand.Open, R("set a timer").Argument, "no length: the ruler opens");
        Check.Equal(TimerCommand.Open, R("timer").Argument, "just \"timer\"");
        Check.Equal(TimerCommand.Cancel, R("cancel the timer").Argument, "cancel");
        Check.Equal(TimerCommand.Cancel, R("stop the timer").Argument, "stop");
        Check.Equal(TimerCommand.Pause, R("pause the timer").Argument, "pause");
        Check.Equal(TimerCommand.Resume, R("resume the timer").Argument, "resume");
        Check.Equal(TimerCommand.Left, R("how much time is left").Argument, "what's left");
        Check.Equal(TimerCommand.Left, R("how long is left on the timer?").Argument, "what's left on it");
        Check.Equal(TimerCommand.Start(TimeSpan.FromMinutes(5)), R("Tempo, set a timer for 5 minutes").Argument, "by Tempo's name");
        Check.Equal(RequestKind.Tell, R("what time is it").Kind, "the time is still Iris's");
        Check.Equal(RequestKind.Unknown, R("Bolt, set a timer for 5 minutes").Kind, "another agent named: the brain routes it to Tempo");

        Check.True(R("how do I look").Kind == RequestKind.Mirror && R("how do I look").Argument == "open", "the mirror");
        Check.Equal("open", R("open the mirror").Argument, "open the mirror (not an app)");
        Check.Equal("open", R("show me my face").Argument, "my face");
        Check.Equal("close", R("close the mirror").Argument, "close it");
        Check.Equal(RequestKind.OpenApp, R("open Mirror Magic").Kind, "an app with mirror in its name still opens");
    }

    [Test]
    public static void The_brain_can_set_a_timer_or_show_the_mirror_and_nothing_else_of_the_kind()
    {
        var set = DecisionParser.Parse("{\"agent\":\"tempo\",\"skill\":\"timer\",\"args\":{\"seconds\":1500}}");
        Check.True(set is { Argument: "start:1500" } && set.Agent == Cast.Tempo, "25 minutes");
        Check.Equal("start:90", DecisionParser.Parse("{\"agent\":\"tempo\",\"skill\":\"timer\",\"args\":{\"seconds\":\"90\"}}")?.Argument, "seconds as text");
        Check.Equal("cancel", DecisionParser.Parse("{\"agent\":\"tempo\",\"skill\":\"timer\",\"args\":{\"action\":\"cancel\"}}")?.Argument, "an action");
        Check.True(DecisionParser.Parse("{\"agent\":\"tempo\",\"skill\":\"timer\",\"args\":{\"seconds\":0}}") == null, "no zero timers");
        Check.True(DecisionParser.Parse("{\"agent\":\"tempo\",\"skill\":\"timer\",\"args\":{\"seconds\":999999}}") == null, "nothing longer than a day");
        Check.True(DecisionParser.Parse("{\"agent\":\"tempo\",\"skill\":\"timer\",\"args\":{\"action\":\"delete\"}}") == null, "an action the timer doesn't have");
        Check.True(DecisionParser.Parse("{\"agent\":\"bolt\",\"skill\":\"timer\",\"args\":{\"seconds\":60}}") == null, "the timer is Tempo's, not Bolt's");
        var mirror = DecisionParser.Parse("{\"agent\":\"none\",\"skill\":\"mirror\",\"args\":{}}");
        Check.True(mirror is { Argument: "open" } && mirror.Agent == null, "the mirror is Forty's own");
        Check.True(BrainPrompt.System().Contains("\"timer\"") && BrainPrompt.System().Contains("\"mirror\""), "the model is told about both");
    }

    [Test]
    public static void The_ruler_follows_the_pointer_coasts_snaps_and_resists_at_the_ends()
    {
        var r = new RulerMotion(10, 1, 180, 15);
        Check.Near(140, r.Position, 1e-9, "15 minutes sits 14 steps along");
        r.Grab(0);
        r.Drag(-25, 0.01);
        Check.Near(165, r.Position, 1e-9, "dragged left 25 DIPs, 1:1");
        Check.Equal(18, r.Value, "the minute under the line follows");
        r.Drag(-1, 0.30);   // held still for a while, then let go
        r.Release(0.30);
        for (double t = 0.30; r.Advance(t); t += 1 / 120.0) { }
        Check.Equal(18, r.Value, "let go slowly: it settles on the nearest minute");
        Check.Near(r.PositionOf(18), r.Position, 1e-9, "exactly on it");

        // A throw coasts on and lands on a whole minute further along, never past the last one.
        r.Grab(1);
        for (int i = 1; i <= 8; i++) r.Drag(-12, 1 + i * 0.008);
        r.Release(1 + 8 * 0.008);
        Check.True(r.Target > 30, "a fast throw goes well past where it was let go");
        double last = r.Position;
        bool backwards = false;
        for (double t = 1.07; r.Advance(t); t += 1 / 120.0) { if (r.Position < last - 1e-6) backwards = true; last = r.Position; }
        Check.True(!backwards, "it never swings back over a tick it already passed");
        Check.Near(r.PositionOf(r.Target), r.Position, 1e-9, "and rests on a whole minute");

        // Past the start it resists: pulled 100 DIPs, it gives far less, and comes back to 1.
        var edge = new RulerMotion(10, 1, 180, 1);
        edge.Grab(0);
        edge.Drag(100, 0.05);
        Check.True(edge.Position < 0 && edge.Position > -RulerMotion.Stretch, "the rubber band");
        edge.Release(0.5);
        for (double t = 0.5; edge.Advance(t); t += 1 / 120.0) { }
        Check.Equal(1, edge.Value, "back to the first minute");

        // Keys and the wheel add up, even mid-flight.
        var keys = new RulerMotion(10, 1, 180, 10);
        keys.Nudge(1, 0);
        keys.Nudge(1, 0.02);
        keys.Nudge(5, 0.04);
        Check.Equal(17, keys.Target, "quick presses add up instead of being lost");
        keys.GoTo(500, 0.05);
        Check.Equal(180, keys.Target, "never past the last minute");
    }

    [Test]
    public static void Ticks_are_paced_so_a_fast_throw_is_a_ratchet_not_a_buzz()
    {
        var pacer = new TickPacer();
        int heard = 0;
        for (int i = 0; i < 100; i++) if (pacer.Allow(i * 0.005)) heard++;   // 100 minutes in half a second
        Check.True(heard is >= 15 and <= 19, $"about one tick every {TickPacer.Gap * 1000:0} ms ({heard})");
    }

    [Test]
    public static void The_sounds_are_made_here_short_and_quiet()
    {
        var tick = SoundMaker.Tick();
        Check.Equal("RIFF", System.Text.Encoding.ASCII.GetString(tick, 0, 4), "a WAV file");
        Check.True(SoundMaker.Seconds(tick) < 0.02, "the tick is a click, not a note");
        Check.True(SoundMaker.Peak(tick) is > 0.1 and <= 0.21, "and quiet");
        Check.True(SoundMaker.Seconds(SoundMaker.Start()) < 0.7, "the start chime is short");
        var alarm = SoundMaker.Alarm();
        Check.True(SoundMaker.Seconds(alarm) is > 2 and < 3, "the alarm's loop is a figure and a breath");
        Check.True(SoundMaker.Peak(alarm) <= 0.43, "and it never clips");
    }
}

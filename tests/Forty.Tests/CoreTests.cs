using Forty.Core;

namespace Forty.Tests;

public static class LayoutTests
{
    [Test]
    public static void The_capsule_grows_with_its_content_between_200_and_320()
    {
        Check.Equal(200.0, CapsuleLayout.Measure(40, 0, touch: false).Width, "short label: minimum width");
        double medium = CapsuleLayout.Measure(120, 36, touch: false).Width;
        Check.True(medium > 200 && medium < 320, $"medium label in between ({medium})");
        Check.Equal(320.0, CapsuleLayout.Measure(600, 40, touch: false).Width, "long label: maximum width");
    }

    [Test]
    public static void The_corner_is_always_half_the_height_so_it_stays_a_pill()
    {
        foreach (bool touch in new[] { false, true })
        {
            var shape = CapsuleLayout.Measure(100, 30, touch);
            Check.Equal(shape.Height / 2, shape.Radius, $"radius, touch={touch}");
        }
        Check.Equal(44.0, CapsuleLayout.Measure(100, 0, touch: true).Height, "touch targets are 44 tall");
    }

    [Test]
    public static void A_label_too_long_for_the_widest_capsule_gets_the_room_that_is_left()
    {
        var shape = CapsuleLayout.Measure(600, 40, touch: false);
        double room = CapsuleLayout.LabelRoom(shape.Width, 40);
        Check.True(room > 100 && room < 600, $"room for an ellipsis-trimmed label ({room})");
    }
}

public static class PowerTests
{
    static PowerSnapshot Battery(int percent, bool plugged, int rate = 0, int remaining = 0, int full = 50_000) =>
        new(percent, plugged, plugged && rate > 0, false, rate, remaining, full);

    [Test]
    public static void Plugging_in_and_out_each_show_one_capsule()
    {
        var on = PowerRules.Changes(Battery(40, false), Battery(40, true), PowerAlerts.Default, null).ToList();
        Check.Equal(1, on.Count, "one activity on plug-in");
        Check.Equal("Charging", on[0].Title, "says charging");
        Check.Equal(Accent.Done, on[0].Accent, "green");
        var off = PowerRules.Changes(Battery(40, true), Battery(40, false), PowerAlerts.Default, null).ToList();
        Check.Equal("On battery", off.Single().Title, "says on battery");
    }

    [Test]
    public static void Alerts_fire_once_when_crossing_a_level_on_the_way_down()
    {
        Check.Equal(1, PowerRules.Changes(Battery(21, false), Battery(20, false), PowerAlerts.Default, null).Count(), "crossing 20%");
        Check.Equal(0, PowerRules.Changes(Battery(20, false), Battery(19, false), PowerAlerts.Default, null).Count(), "already below: no repeat");
        Check.Equal(0, PowerRules.Changes(Battery(21, true), Battery(20, true), PowerAlerts.Default, null).Count(), "not while plugged in");
    }

    [Test]
    public static void A_pc_without_a_battery_says_nothing()
    {
        var none = new PowerSnapshot(null, true, false, false);
        Check.Equal(0, PowerRules.Changes(none with { PluggedIn = false }, none, PowerAlerts.Default, null).Count(), "no battery, no capsule");
    }

    [Test]
    public static void Time_to_full_appears_only_after_two_minutes_of_steady_charging()
    {
        var estimator = new ChargeEstimator();
        for (int s = 0; s <= 110; s += 10) estimator.Add(s, Battery(50, true, rate: 30_000, remaining: 25_000));
        Check.True(estimator.TimeToFull(110) == null, "no estimate before two minutes");
        for (int s = 120; s <= 130; s += 10) estimator.Add(s, Battery(50, true, rate: 30_000, remaining: 25_000));
        var estimate = estimator.TimeToFull(130);
        Check.True(estimate != null, "an estimate after two steady minutes");
        Check.Near(57.5, estimate!.Value.TotalMinutes, 1, "25 Wh to go at 30 W, plus the slower last stretch");
    }

    [Test]
    public static void A_wobbling_charge_rate_gives_no_estimate()
    {
        var estimator = new ChargeEstimator();
        for (int s = 0; s <= 140; s += 10) estimator.Add(s, Battery(50, true, rate: s % 20 == 0 ? 30_000 : 12_000, remaining: 25_000));
        Check.True(estimator.TimeToFull(140) == null, "rate jumps around: no estimate");
    }

    [Test]
    public static void Unplugging_forgets_the_readings()
    {
        var estimator = new ChargeEstimator();
        for (int s = 0; s <= 130; s += 10) estimator.Add(s, Battery(50, true, rate: 30_000, remaining: 25_000));
        estimator.Add(140, Battery(50, false));
        Check.True(estimator.TimeToFull(140) == null, "estimate gone");
    }

    [Test]
    public static void Battery_glyphs_follow_the_level()
    {
        Check.Equal("", Glyphs.Battery(0), "empty");
        Check.Equal("", Glyphs.Battery(50), "half");
        Check.Equal("", Glyphs.Battery(100), "full");
    }
}

public static class ModeTests
{
    [Test]
    public static void A_game_beats_everything_then_a_presentation_then_your_choice()
    {
        var all = new SystemState(true, true, true, true, true);
        Check.Equal(Mode.Gaming, ModeResolver.Resolve(all, Mode.Meeting), "game first");
        Check.Equal(Mode.Presenting, ModeResolver.Resolve(all with { ExclusiveGame = false }, Mode.Meeting), "then presentation");
        Check.Equal(Mode.Meeting, ModeResolver.Resolve(SystemState.Calm with { WindowsFocus = true }, Mode.Meeting), "then your choice");
    }

    [Test]
    public static void Windows_Focus_or_a_low_battery_mean_low_distraction()
    {
        Check.Equal(Mode.Focus, ModeResolver.Resolve(SystemState.Calm with { WindowsFocus = true }, null), "Focus on");
        Check.Equal(Mode.Focus, ModeResolver.Resolve(SystemState.Calm with { LowBatteryUnplugged = true }, null), "low battery");
        Check.Equal(Mode.Normal, ModeResolver.Resolve(SystemState.Calm, null), "otherwise normal");
    }
}

public static class FlagTests
{
    [Test]
    public static void Unknown_flags_are_off_and_you_means_on_only_for_your_builds()
    {
        string path = Path.Combine(Path.GetTempPath(), $"forty-flags-{Environment.ProcessId}.json");
        File.WriteAllText(path, """{ "glass": "you", "visualiser": "on", "satellite": "off", "broken": 7 }""");
        try
        {
            using var mine = new FeatureFlags(path, developer: true, watch: false);
            using var theirs = new FeatureFlags(path, developer: false, watch: false);
            Check.True(mine.IsOn("glass") && !theirs.IsOn("glass"), "\"you\" is on for the developer only");
            Check.True(mine.IsOn("visualiser") && theirs.IsOn("visualiser"), "\"on\" is on for everyone");
            Check.True(!mine.IsOn("satellite") && !mine.IsOn("never-heard-of-it") && !mine.IsOn("broken"), "off, unknown and malformed are off");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void Editing_the_file_applies_without_a_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"forty-flags-live-{Environment.ProcessId}.json");
        File.WriteAllText(path, """{ "glass": "off" }""");
        try
        {
            using var flags = new FeatureFlags(path, developer: false, watch: true);
            using var changed = new ManualResetEventSlim();
            flags.Changed += changed.Set;
            File.WriteAllText(path, """{ "glass": "on" }""");
            Check.True(changed.Wait(TimeSpan.FromSeconds(3)), "change noticed within three seconds");
            Check.True(flags.IsOn("glass"), "new value in effect");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void Faults_are_read_from_a_comma_separated_list()
    {
        var faults = Faults.Parse(" media-down , power-down,");
        Check.True(faults.Contains("media-down") && faults.Contains("POWER-DOWN") && faults.Count == 2, "two faults, any case");
    }
}

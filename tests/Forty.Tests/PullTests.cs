using Forty.Motion;

namespace Forty.Tests;

/// <summary>F12: pulling the island open and pushing it closed by touch.</summary>
public static class PullTests
{
    // Opening: grabbed at the capsule's 36 DIPs, the panel is 300 tall.
    static PullGesture Open() => new(startY: 20, fromHeight: 36, toHeight: 300, now: 0);

    [Test]
    public static void A_small_movement_is_still_a_tap()
    {
        var pull = Open();
        Check.True(pull.Move(26, 0.05) == null, "6 DIPs is not a drag");
        var (tap, commit, _) = pull.Release(0.1);
        Check.True(tap && !commit, "released as a tap");
    }

    [Test]
    public static void The_body_follows_the_finger_one_to_one()
    {
        var pull = Open();
        Check.Near(36 + 100, pull.Move(120, 0.1) ?? -1, 0.001, "100 DIPs down, 100 DIPs taller");
        Check.Near(100 / 264.0, pull.Progress(136), 0.001, "progress toward the panel");
    }

    [Test]
    public static void Past_either_end_it_resists_and_never_stops_dead()
    {
        var pull = Open();
        double a = pull.Move(20 + 264 + 50, 0.1) ?? 0, b = pull.Move(20 + 264 + 150, 0.2) ?? 0;
        Check.True(a > 300 && a < 350, "50 DIPs past the panel shows as less than 50");
        Check.True(b > a && b - 300 < 150, "further still, more resistance, still moving");
        double up = pull.Move(-60, 0.3) ?? 0;
        Check.True(up < 36 && up > 36 - 80, "pushed above the capsule, it resists too");
    }

    [Test]
    public static void Release_direction_decides_not_distance()
    {
        var pull = Open();
        for (int i = 1; i <= 10; i++) pull.Move(20 + i * 20, i * 0.016);            // pulled 200 DIPs down…
        for (int i = 1; i <= 4; i++) pull.Move(220 - i * 15, 0.16 + i * 0.016);      // …then flicked back up
        var (tap, commit, velocity) = pull.Release(0.23);
        Check.True(!tap && !commit && velocity < 0, "a flick back up cancels, however far it went");

        var short_ = Open();
        for (int i = 1; i <= 4; i++) short_.Move(20 + i * 8, i * 0.016);             // only 32 DIPs, but moving down fast
        Check.True(short_.Release(0.07).Commit, "a short flick down opens");
    }

    [Test]
    public static void A_slow_release_goes_by_where_it_was_dragged()
    {
        var pull = Open();
        pull.Move(140, 0.1);
        Check.True(pull.Release(0.5).Commit, "paused after pulling down: it opens");
        var closing = new PullGesture(startY: 200, fromHeight: 300, toHeight: 36, now: 0);
        closing.Move(150, 0.1);
        Check.True(closing.Release(0.5).Commit, "paused after pushing up: it closes");
    }
}

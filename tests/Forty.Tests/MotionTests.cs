using Forty.Motion;

namespace Forty.Tests;

public static class MotionTests
{
    [Test]
    public static void A_retarget_never_moves_the_value_or_changes_its_speed_at_that_instant()
    {
        var spring = new AdditiveSpring(148);
        var random = new Random(7);
        double now = 0;
        spring.Retarget(420, Springs.Move, now);
        for (int i = 0; i < 200; i++)
        {
            now += random.NextDouble() * 0.3;
            var before = spring.Sample(now);
            spring.Retarget(100 + random.NextDouble() * 400, random.Next(2) == 0 ? Springs.Move : Springs.Quick, now);
            var after = spring.Sample(now);
            Check.Near(before.X, after.X, 1e-6, $"position at interruption {i}");
            Check.Near(before.V, after.V, 1e-4, $"velocity at interruption {i}");
        }
    }

    [Test]
    public static void Twenty_changes_in_a_fraction_of_a_second_still_never_jump()
    {
        // More changes than there are slots, faster than any term can decay.
        var spring = new AdditiveSpring(0);
        double now = 0;
        for (int i = 0; i < 40; i++)
        {
            now += 0.01;
            var before = spring.Sample(now);
            spring.Retarget(i % 2 == 0 ? 300 : 0, Springs.Quick, now);
            var after = spring.Sample(now);
            Check.Near(before.X, after.X, 1e-6, $"position at change {i}");
            Check.Near(before.V, after.V, 1e-4, $"velocity at change {i}");
        }
        Check.True(spring.IsSettled(now + 1.5), "and it still settles");
    }

    [Test]
    public static void A_critically_damped_spring_arrives_without_overshoot()
    {
        var spring = new AdditiveSpring(0);
        spring.Retarget(100, Springs.Move, 0);
        double max = 0;
        for (double t = 0; t < 2; t += 1 / 240.0) max = Math.Max(max, spring.Sample(t).X);
        Check.True(max <= 100.0001, $"never passes the target (peak {max:0.###})");
        Check.True(spring.IsSettled(1.0), "settled within a second");
    }

    [Test]
    public static void A_move_spring_settles_to_one_percent_in_about_0_37_seconds()
    {
        var spring = new AdditiveSpring(0);
        spring.Retarget(100, Springs.Move, 0);
        double settled = 0;
        for (double t = 0; t < 2; t += 0.001)
            if (Math.Abs(spring.Sample(t).X - 100) > 1) settled = t;
        Check.Near(0.40, settled, 0.04, "seconds to within 1%");
        Check.Near(settled, Springs.Move.SettleSeconds(), 0.05, "SettleSeconds agrees with the simulation");
    }

    [Test]
    public static void A_thrown_spring_overshoots_because_the_hand_carried_momentum()
    {
        var spring = new AdditiveSpring(0);
        spring.Release(100, Springs.Thrown, 0, velocity: 800);
        double max = 0;
        for (double t = 0; t < 2; t += 1 / 240.0) max = Math.Max(max, spring.Sample(t).X);
        Check.True(max > 101, $"overshoots a little (peak {max:0.#})");
        Check.True(max < 130, $"but not wildly (peak {max:0.#})");
        Check.Near(800, spring.Sample(0).V, 1e-6, "starts at the release velocity");
    }

    [Test]
    public static void A_direct_set_drops_every_term_and_reports_it()
    {
        var spring = new AdditiveSpring(0);
        int cleared = 0;
        spring.TermsCleared += () => cleared++;
        spring.Retarget(100, Springs.Move, 0);
        spring.SetDirect(42);
        Check.Equal(42.0, spring.Sample(0.05).X, "value is exactly what it was told");
        Check.Equal(1, cleared, "the compositor is told to clear its slots once");
        spring.SetDirect(43);
        Check.Equal(1, cleared, "nothing to clear the second time");
    }

    [Test]
    public static void Slots_are_reused_in_rotation_after_sixteen_terms()
    {
        var spring = new AdditiveSpring(0);
        var slots = new List<int>();
        spring.TermAdded += term => slots.Add(term.Slot);
        for (int i = 0; i < 18; i++) spring.Retarget(i, Springs.Quick, i * 0.01);
        Check.Equal(0, slots[16], "the seventeenth term takes slot 0");
        Check.Equal(1, slots[17], "the eighteenth takes slot 1");
    }

    [Test]
    public static void Velocity_is_measured_over_the_last_tenth_of_a_second_and_a_pause_zeroes_it()
    {
        var tracker = new VelocityTracker();
        for (int i = 0; i <= 20; i++) tracker.Add(i * 0.008, i * 0.008 * 500);   // 500 DIPs a second
        Check.Near(500, tracker.Velocity(0.16), 1, "steady drag");
        Check.Equal(0.0, tracker.Velocity(0.16 + 0.15), "a pause before letting go counts as zero");
    }

    [Test]
    public static void A_throw_lands_on_the_snap_point_nearest_where_it_was_heading()
    {
        double projected = Gesture.Project(100, 1000);   // 1000 DIPs a second, scroll-like deceleration
        Check.Near(599, projected, 1, "projected resting point");
        Check.Equal(560.0, Gesture.NearestSnap(projected, new[] { 0.0, 200, 560 }), "nearest snap to the projection, not the release point");
    }

    [Test]
    public static void An_edge_resists_more_the_further_it_is_pulled_and_never_stops_dead()
    {
        double a = Gesture.RubberBand(50, 400), b = Gesture.RubberBand(200, 400), c = Gesture.RubberBand(2000, 400);
        Check.True(a < 50 && b < 200 && c < 2000, "always less than the pull");
        Check.True(a < b && b < c, "but still growing with the pull");
        Check.True(b / 200 < a / 50, "resistance rises with distance");
    }
}

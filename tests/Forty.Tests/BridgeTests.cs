using Forty.Core;
using Forty.Motion;

namespace Forty.Tests;

/// <summary>F5: the liquid bridge between the capsule and the satellite.</summary>
public static class BridgeTests
{
    const double A = CapsuleLayout.Height / 2, B = CapsuleLayout.SatelliteSize / 2, Gap = CapsuleLayout.SatelliteGap;

    [Test]
    public static void There_is_no_neck_when_merged_or_at_rest()
    {
        Check.True(LiquidBridge.At(A, B, Gap, 0).Empty, "merged: nothing to join");
        Check.True(LiquidBridge.At(A, B, Gap, 1).Empty, "at rest: a separate drop");
        Check.True(!LiquidBridge.At(A, B, Gap, 0.5).Empty, "half way out: joined");
    }

    [Test]
    public static void The_neck_only_thins_as_the_drop_pulls_away()
    {
        // From the moment the two shapes stop overlapping until it snaps, the neck never thickens again.
        double touching = 2 * B / (Gap + 2 * B);
        float last = float.MaxValue;
        for (double split = touching; split < 1; split += 0.01)
        {
            var o = LiquidBridge.At(A, B, Gap, split);
            if (o.Empty) break;
            Check.True(o.MidThickness <= last + 0.01f, $"thinner or equal at {split:0.00}");
            Check.True(o.MidThickness >= 0, "never inside out");
            last = o.MidThickness;
        }
        Check.True(last < 2 * B, "it does thin");
    }

    [Test]
    public static void The_morph_has_no_jumps_between_its_steps()
    {
        // Neighbouring steps of the precomputed morph differ by a few DIPs at most, except where the
        // neck collapses to a point (a shrink, which the compositor interpolates smoothly).
        const int steps = 24;
        for (int i = 1; i <= steps; i++)
        {
            var before = LiquidBridge.At(A, B, Gap, (i - 1) / (double)steps);
            var after = LiquidBridge.At(A, B, Gap, i / (double)steps);
            if (before.Empty || after.Empty) continue;
            float move = new[] { before.P1 - after.P1, before.P2 - after.P2, before.P3 - after.P3, before.P4 - after.P4 }.Max(v => v.Length());
            Check.True(move < 6, $"step {i} moves {move:0.0} DIPs");
        }
    }

    [Test]
    public static void A_touch_sized_capsule_still_has_a_clean_neck()
    {
        double a = CapsuleLayout.TouchHeight / 2;
        Check.True(LiquidBridge.At(a, B, Gap, 0).Empty && LiquidBridge.At(a, B, Gap, 1).Empty, "no neck at either end");
        var mid = LiquidBridge.At(a, B, Gap, 0.6);
        Check.True(!mid.Empty && mid.MidThickness > 0 && float.IsFinite(mid.MidThickness), "a real neck in between");
    }
}

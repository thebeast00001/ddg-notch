using System.Numerics;

namespace Forty.Motion;

/// <summary>One outline of the liquid bridge: a cubic curve along the top, a line, a cubic curve back along the bottom.</summary>
public readonly record struct BridgeOutline(Vector2 P1, Vector2 H1, Vector2 H3, Vector2 P3, Vector2 P4, Vector2 H4, Vector2 H2, Vector2 P2)
{
    /// <summary>True when every point is the same: no neck at all.</summary>
    public bool Empty => P1 == P2 && P1 == P3 && P1 == P4;

    /// <summary>The neck's thickness halfway between the two shapes, from the curves' midpoints.</summary>
    public float MidThickness
    {
        get
        {
            static Vector2 Mid(Vector2 a, Vector2 b, Vector2 c, Vector2 d) => (a + 3 * b + 3 * c + d) / 8;
            return Empty ? 0 : Mid(P4, H4, H2, P2).Y - Mid(P1, H1, H3, P3).Y;
        }
    }
}

/// <summary>
/// The neck that joins the capsule's rounded end to the satellite while it pulls away (F5, MOTION.md 5):
/// the classic metaball join of two circles. Coordinates are relative to the capsule's right edge, on
/// its centre line, with y pointing down as on screen.
/// </summary>
public static class LiquidBridge
{
    const double V = 0.5, HandleSize = 2.4;

    /// <summary>How far the gap is crossed before the neck snaps, so the satellite rests as a separate drop.</summary>
    public const double SnapFraction = 0.7;

    /// <param name="a">The radius of the capsule's rounded end (half its height).</param>
    /// <param name="b">The satellite's radius.</param>
    /// <param name="gap">The gap between them at rest.</param>
    /// <param name="split">How far the satellite has travelled out: 0 inside the capsule's end, 1 at rest beside it.</param>
    public static BridgeOutline At(double a, double b, double gap, double split)
    {
        double x1 = -a, x2 = -b + (gap + 2 * b) * split;   // the two centres
        double d = x2 - x1;
        double maxDist = a + b + gap * SnapFraction;
        if (d <= Math.Abs(a - b) + 0.01 || d >= maxDist)
        {
            // No neck: one point (inside the satellite while merged, at the pinch once it has snapped),
            // so the morph to and from it shrinks or grows the neck rather than sliding it.
            var at = new Vector2((float)(d >= maxDist ? (x1 + a + x2 - b) / 2 : x2), 0);
            return new BridgeOutline(at, at, at, at, at, at, at, at);
        }

        double u1 = 0, u2 = 0;
        if (d < a + b)
        {
            u1 = Math.Acos(Math.Clamp((a * a + d * d - b * b) / (2 * a * d), -1, 1));
            u2 = Math.Acos(Math.Clamp((b * b + d * d - a * a) / (2 * b * d), -1, 1));
        }
        double spread = Math.Acos(Math.Clamp((a - b) / d, -1, 1));
        double angle1 = u1 + (spread - u1) * V;                       // on the capsule's end, above the centre line
        double angle3 = Math.PI - u2 - (Math.PI - u2 - spread) * V;   // on the satellite, above

        static Vector2 On(double cx, double angle, double r) => new((float)(cx + r * Math.Cos(angle)), (float)(-r * Math.Sin(angle)));
        var p1 = On(x1, angle1, a);
        var p2 = On(x1, -angle1, a);
        var p3 = On(x2, angle3, b);
        var p4 = On(x2, -angle3, b);

        double handle = Math.Min(V * HandleSize, Vector2.Distance(p1, p3) / (a + b)) * Math.Min(1, d * 2 / (a + b));
        static Vector2 Handle(Vector2 from, double angle, double length) =>
            from + new Vector2((float)(length * Math.Cos(angle)), (float)(-length * Math.Sin(angle)));
        return new BridgeOutline(
            p1, Handle(p1, angle1 - Math.PI / 2, a * handle), Handle(p3, angle3 + Math.PI / 2, b * handle), p3,
            p4, Handle(p4, -angle3 - Math.PI / 2, b * handle), Handle(p2, -angle1 + Math.PI / 2, a * handle), p2);
    }
}

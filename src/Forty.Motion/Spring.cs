namespace Forty.Motion;

/// <summary>
/// A spring described the way MOTION.md describes it: a damping ratio (1 settles with no overshoot,
/// lower overshoots) and a response in seconds (roughly how long it takes to arrive). A spring has no
/// fixed duration.
/// </summary>
public readonly record struct Spring(double Damping, double Response)
{
    /// <summary>Natural angular frequency, 2π ÷ response.</summary>
    public double Omega => 2 * Math.PI / Response;

    /// <summary>For a solver with mass 1: stiffness = (2π ÷ response)².</summary>
    public double Stiffness => Omega * Omega;

    /// <summary>For a solver with mass 1: damping coefficient = 4π × ratio ÷ response.</summary>
    public double DampingCoefficient => 4 * Math.PI * Damping / Response;

    /// <summary>
    /// Seconds until the value stays within <paramref name="fraction"/> of the distance it set out to
    /// cover. Used to know when a window region can shrink and when a motion is truly over.
    /// </summary>
    public double SettleSeconds(double fraction = 0.01)
    {
        double zeta = Math.Min(Damping, 0.9999);
        // The envelope of the decaying term, including the polynomial factor of near-critical damping.
        double zw = zeta * Omega;
        double t = Math.Log(1 / fraction) / zw;
        return zeta > 0.95 ? t * 1.35 : t;
    }

    /// <summary>A copy of this spring that answers faster or slower; used by the debug slow-motion switch.</summary>
    public Spring Scaled(double factor) => this with { Response = Response * factor };
}

/// <summary>
/// The named springs. Every transition in Forty uses one of these; a call site never invents its own
/// numbers. Values from MOTION.md section 3 and ROADMAP.md section 2.2.
/// </summary>
public static class Springs
{
    /// <summary>Pointer-down scale, hover swell.</summary>
    public static readonly Spring Press = new(1.0, 0.15);
    /// <summary>Value changes in a HUD, icon swaps, sliver and pill, one activity to another.</summary>
    public static readonly Spring Quick = new(1.0, 0.25);
    /// <summary>Any expand or collapse started by hover, click, shortcut or a system event.</summary>
    public static readonly Spring Move = new(1.0, 0.38);
    /// <summary>Collapse: the same path as an expand, a little quicker, because exits are shorter.</summary>
    public static readonly Spring MoveOut = new(1.0, 0.32);
    /// <summary>Anything released from a drag or flick. Bounces because your hand carried momentum.</summary>
    public static readonly Spring Thrown = new(0.8, 0.35);
    /// <summary>The full page pulled down and let go.</summary>
    public static readonly Spring Sheet = new(0.8, 0.30);
    /// <summary>Rotation.</summary>
    public static readonly Spring Turn = new(0.8, 0.40);
    /// <summary>A task card travelling from one agent to another.</summary>
    public static readonly Spring Baton = new(1.0, 0.45);
    /// <summary>A new line of live text arriving; the lane moving up to make room.</summary>
    public static readonly Spring Stream = new(1.0, 0.22);
    /// <summary>Jumping to a transcript line from its evidence link.</summary>
    public static readonly Spring Focus = new(1.0, 0.30);
    /// <summary>Steps of a plan, brief or workflow appearing in order.</summary>
    public static readonly Spring Unfold = new(1.0, 0.34);
}

/// <summary>
/// One flight of a spring from a known position and velocity toward a target. The closed-form
/// solution gives position and velocity at any instant. A damping ratio of 1 is evaluated at 0.9999,
/// where the under-damped form cannot be told apart from the critically damped one.
/// </summary>
public readonly struct SpringSegment
{
    public readonly double T0, X0, V0, Target;
    public readonly Spring Spring;
    /// <summary>Decay rate, damped angular frequency, and the two amplitudes of the solution.</summary>
    public readonly double Zw, Wd, A, C;

    public SpringSegment(double t0, double x0, double v0, double target, Spring spring)
    {
        T0 = t0; X0 = x0; V0 = v0; Target = target; Spring = spring;
        (Zw, Wd) = Coefficients(spring);
        A = x0 - target;
        C = (v0 + Zw * A) / Wd;
    }

    public static (double Zw, double Wd) Coefficients(Spring spring)
    {
        double zeta = Math.Min(spring.Damping, 0.9999), omega = spring.Omega;
        return (zeta * omega, omega * Math.Sqrt(1 - zeta * zeta));
    }

    public (double X, double V) Eval(double now)
    {
        double tau = Math.Max(0, now - T0);
        double decay = Math.Exp(-Zw * tau), cos = Math.Cos(Wd * tau), sin = Math.Sin(Wd * tau);
        return (Target + decay * (A * cos + C * sin),
                decay * ((C * Wd - Zw * A) * cos - (A * Wd + Zw * C) * sin));
    }
}

/// <summary>Seconds on the system's high-resolution counter. Every motion timestamp uses this clock.</summary>
public static class Clock
{
    public static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}

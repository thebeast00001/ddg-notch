namespace IslandSpike.Motion;

/// <summary>
/// A spring described the way MOTION.md describes it: a damping ratio (1 = no overshoot)
/// and a response in seconds (roughly how long it takes to arrive).
/// </summary>
public readonly record struct Spring(double Damping, double Response)
{
    public double Omega => 2 * Math.PI / Response;

    public static readonly Spring Press = new(1.0, 0.15);
    public static readonly Spring Quick = new(1.0, 0.25);
    public static readonly Spring Move = new(1.0, 0.38);
    public static readonly Spring Thrown = new(0.8, 0.35);
    public static readonly Spring Sheet = new(0.8, 0.30);
}

/// <summary>
/// One flight of a spring: where it started, how fast it was going, where it is headed.
/// The closed-form solution gives position and velocity at any instant, which is what
/// lets a new flight start exactly where and how fast the previous one was.
///
/// One formula covers both cases. A damping ratio of 1 is evaluated at 0.9999, where the
/// under-damped form is numerically indistinguishable from the critically damped one.
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
        double zeta = Math.Min(spring.Damping, 0.9999);
        double omega = spring.Omega;
        Zw = zeta * omega;
        Wd = omega * Math.Sqrt(1 - zeta * zeta);
        A = x0 - target;
        C = (v0 + Zw * A) / Wd;
    }

    public (double X, double V) Eval(double now, double maxElapsed = double.MaxValue)
    {
        double tau = Math.Clamp(now - T0, 0, maxElapsed);
        double decay = Math.Exp(-Zw * tau);
        double cos = Math.Cos(Wd * tau), sin = Math.Sin(Wd * tau);
        double x = Target + decay * (A * cos + C * sin);
        double v = decay * ((C * Wd - Zw * A) * cos - (A * Wd + Zw * C) * sin);
        return (x, v);
    }
}

public static class Clock
{
    /// <summary>Seconds on the system's high-resolution counter.</summary>
    public static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}

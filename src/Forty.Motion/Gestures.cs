namespace Forty.Motion;

/// <summary>
/// The pointer's velocity at release, measured the way MOTION.md section 6 says: over the last
/// 80–100 ms of samples, and zero if the pointer paused for more than 100 ms before letting go.
/// </summary>
public sealed class VelocityTracker
{
    const double Window = 0.1, Pause = 0.1;
    readonly List<(double T, double X)> _samples = new();

    public void Reset() => _samples.Clear();

    public void Add(double t, double x)
    {
        _samples.Add((t, x));
        while (_samples.Count > 2 && t - _samples[0].T > Window * 2) _samples.RemoveAt(0);
    }

    /// <summary>Units per second at <paramref name="now"/>.</summary>
    public double Velocity(double now)
    {
        if (_samples.Count < 2) return 0;
        var last = _samples[^1];
        if (now - last.T > Pause) return 0;
        var first = _samples[0];
        foreach (var s in _samples) { if (last.T - s.T <= Window) { first = s; break; } }
        double span = last.T - first.T;
        return span < 0.008 ? 0 : (last.X - first.X) / span;
    }
}

/// <summary>Where a throw is heading, and how an edge resists. Both from MOTION.md section 6.</summary>
public static class Gesture
{
    /// <summary>
    /// The resting point a released motion is heading for, as scrolling decelerates:
    /// position + (velocity ÷ 1000) × d ÷ (1 − d). The snap point nearest this wins, not the one
    /// nearest the release point.
    /// </summary>
    public static double Project(double position, double velocity, double decelerationRate = 0.998) =>
        position + velocity / 1000 * decelerationRate / (1 - decelerationRate);

    /// <summary>The snap point nearest the projected resting point.</summary>
    public static double NearestSnap(double projected, IReadOnlyList<double> snapPoints)
    {
        double best = snapPoints[0];
        foreach (double p in snapPoints) if (Math.Abs(p - projected) < Math.Abs(best - projected)) best = p;
        return best;
    }

    /// <summary>
    /// Past a limit the surface keeps following with rising resistance and never stops dead:
    /// (x × size × c) ÷ (size + c × |x|).
    /// </summary>
    public static double RubberBand(double overshoot, double size, double constant = 0.55) =>
        overshoot * size * constant / (size + constant * Math.Abs(overshoot));

    /// <summary>Commit or cancel is decided by the direction of the release, not by how far it went.</summary>
    public static bool Commits(double velocity, double distanceTravelled, double towardCommitSign) =>
        Math.Abs(velocity) > 50 ? Math.Sign(velocity) == Math.Sign(towardCommitSign) : distanceTravelled * towardCommitSign > 0;
}

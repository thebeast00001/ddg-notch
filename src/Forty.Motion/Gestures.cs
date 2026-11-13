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

/// <summary>
/// Pulling the island open or pushed closed with a finger or pen (F12, MOTION.md 6): a tap until the
/// pointer has moved 10 DIPs, then the body's height follows the finger 1:1 between the two shapes,
/// with rising resistance past either end; on release it goes to the other shape or back, by the
/// direction it was moving, carrying that speed.
/// </summary>
public sealed class PullGesture
{
    public const double Hysteresis = 10;

    readonly double _startY, _from, _to;
    readonly VelocityTracker _velocity = new();
    double _lastY;

    /// <param name="startY">Where the finger went down, in DIPs.</param>
    /// <param name="fromHeight">The body's height when it was grabbed.</param>
    /// <param name="toHeight">The height at the other end (the panel's when opening, the capsule's when closing).</param>
    public PullGesture(double startY, double fromHeight, double toHeight, double now)
    {
        _startY = _lastY = startY;
        _from = fromHeight;
        _to = toHeight;
        _velocity.Add(now, startY);
    }

    /// <summary>True once the finger has moved far enough to be a drag rather than a tap.</summary>
    public bool Dragging { get; private set; }

    /// <summary>The body's height for the finger at <paramref name="y"/>, or null while it could still be a tap.</summary>
    public double? Move(double y, double now)
    {
        _velocity.Add(now, y);
        _lastY = y;
        double dy = y - _startY;
        if (!Dragging && Math.Abs(dy) < Hysteresis) return null;
        Dragging = true;
        double raw = _from + dy;
        double low = Math.Min(_from, _to), high = Math.Max(_from, _to);
        if (raw > high) return high + Gesture.RubberBand(raw - high, high);
        if (raw < low) return low + Gesture.RubberBand(raw - low, Math.Max(low, 1));
        return raw;
    }

    /// <summary>How far between the two shapes a height is: 0 at the grab, 1 at the other end.</summary>
    public double Progress(double height) => Math.Clamp((height - _from) / (_to - _from), 0, 1);

    /// <summary>On release: a tap, or a drag that goes to the other end (commit) or back, with the finger's speed in DIPs per second.</summary>
    public (bool Tap, bool Commit, double Velocity) Release(double now)
    {
        if (!Dragging) return (true, false, 0);
        double v = _velocity.Velocity(now);
        bool commit = Gesture.Commits(v, _lastY - _startY, Math.Sign(_to - _from));
        return (false, commit, v);
    }
}

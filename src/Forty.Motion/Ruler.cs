namespace Forty.Motion;

/// <summary>
/// The timer's ruler as physics (MOTION.md 14i). The strip follows the pointer 1:1; past either end it
/// resists (the rubber band) and never stops dead. Let go and the throw is projected to where it would
/// coast to, that point is snapped to the nearest whole step, and a critically damped spring carries the
/// strip there starting at the throw's own speed, so a flick and its landing are one motion. A wheel
/// notch or an arrow key moves one step on the same spring.
///
/// Positions are in DIPs along the strip: 0 is the first value, <c>(max - min) × step</c> the last.
/// Time comes in from outside, so every rule is testable.
/// </summary>
public sealed class RulerMotion
{
    /// <summary>The settle: critically damped, so it never swings back over a tick it already passed.</summary>
    public static readonly Spring Settle = new(1.0, 0.32);
    /// <summary>How far the strip can be pulled past an end before it resists completely.</summary>
    public const double Stretch = 90;

    readonly VelocityTracker _velocity = new();
    SpringSegment? _flight;
    double _raw;   // where the pointer has dragged it, before the rubber band

    public double Step { get; }
    public int Min { get; }
    public int Max { get; }

    /// <summary>Where the strip is now, in DIPs from the first value.</summary>
    public double Position { get; private set; }

    /// <summary>The value under the centre line now (the nearest step, kept in range).</summary>
    public int Value => Math.Clamp(Min + Nearest(Position / Step), Min, Max);

    /// <summary>The value the strip will come to rest on.</summary>
    public int Target { get; private set; }

    public bool Dragging { get; private set; }

    /// <summary>True while it is still on its way somewhere: dragged, or settling.</summary>
    public bool Moving => Dragging || _flight != null;

    public RulerMotion(double step, int min, int max, int value)
    {
        Step = step; Min = min; Max = Math.Max(min, max);
        Target = Math.Clamp(value, Min, Max);
        Position = _raw = PositionOf(Target);
    }

    /// <summary>Halfway between two minutes goes to the further one, the same way in both directions.</summary>
    static int Nearest(double steps) => (int)Math.Round(steps, MidpointRounding.AwayFromZero);

    public double PositionOf(int value) => (Math.Clamp(value, Min, Max) - Min) * Step;

    double Length => (Max - Min) * Step;

    /// <summary>The pointer went down: whatever was moving stops where it is, under the finger.</summary>
    public void Grab(double now)
    {
        _flight = null;
        Dragging = true;
        _raw = Position;
        _velocity.Reset();
        _velocity.Add(now, _raw);
    }

    /// <summary>The pointer moved by <paramref name="dx"/> DIPs (right is positive, which runs the values down).</summary>
    public void Drag(double dx, double now)
    {
        if (!Dragging) return;
        _raw -= dx;
        _velocity.Add(now, _raw);
        Position = Banded(_raw);
    }

    double Banded(double raw) =>
        raw < 0 ? Gesture.RubberBand(raw, Stretch)
        : raw > Length ? Length + Gesture.RubberBand(raw - Length, Stretch)
        : raw;

    /// <summary>The pointer let go: project the throw, snap it to a step, settle there at the throw's speed.</summary>
    public void Release(double now)
    {
        if (!Dragging) return;
        Dragging = false;
        double v = _velocity.Velocity(now);
        // Pulled past an end, the throw's speed is mostly spent in the band: it comes back from where it is.
        if (Position < 0 || Position > Length) v *= 0.3;
        double projected = Gesture.Project(Position, v);
        Fly(Math.Clamp(Min + Nearest(projected / Step), Min, Max), v, now);
    }

    /// <summary>Settles on <paramref name="value"/> from wherever the strip is (a wheel notch, a key, a preset).</summary>
    public void GoTo(int value, double now)
    {
        double v = _flight is { } f ? f.Eval(now).V : 0;
        Dragging = false;
        Fly(Math.Clamp(value, Min, Max), v, now);
    }

    /// <summary>One step from the value it is heading for, so quick presses add up instead of being lost.</summary>
    public void Nudge(int steps, double now) => GoTo(Target + steps, now);

    void Fly(int value, double v, double now)
    {
        Target = value;
        _flight = new SpringSegment(now, Position, v, PositionOf(value), Settle);
    }

    /// <summary>Moves the strip on to <paramref name="now"/>; returns true while it is still moving.</summary>
    public bool Advance(double now)
    {
        if (_flight is not { } f) return Dragging;
        var (x, v) = f.Eval(now);
        if (Math.Abs(x - f.Target) < 0.05 && Math.Abs(v) < 4)
        {
            Position = _raw = f.Target;
            _flight = null;
            return false;
        }
        Position = _raw = x;
        return true;
    }
}

/// <summary>
/// Ticks heard as a ruler turns: one per step crossed, but never closer together than a click can be
/// heard as separate (fast throws become a soft ratchet instead of a buzz).
/// </summary>
public sealed class TickPacer
{
    public const double Gap = 0.028;
    double _last = double.NegativeInfinity;

    /// <summary>True if a tick should sound now.</summary>
    public bool Allow(double now)
    {
        if (now - _last < Gap) return false;
        _last = now;
        return true;
    }
}

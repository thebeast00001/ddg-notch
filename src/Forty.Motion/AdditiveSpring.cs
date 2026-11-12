namespace Forty.Motion;

/// <summary>
/// One decaying term of an additive spring, as the compositor needs it: which slot it occupies, when
/// it started, and its four coefficients. The term's value at elapsed time τ is
/// <c>e^(−zw·τ) · (a·cos(wd·τ) + c·sin(wd·τ))</c>.
/// </summary>
public readonly record struct SpringTerm(int Slot, double T0, double A, double C, double Zw, double Wd);

/// <summary>
/// The motion engine chosen by spike S2: additive springs, the technique behind iOS's interruptible
/// animations. A change of target never replaces what is running. The target moves at once, and a new
/// decaying term is added that starts out exactly cancelling the move. Earlier terms keep running
/// untouched, so on screen the position and velocity stay continuous whenever the change lands.
///
/// value = target + Σ terms. This class is the mathematical model; the app mirrors every term onto
/// the compositor, which evaluates the same formula at the display's own rate.
/// </summary>
public sealed class AdditiveSpring
{
    /// <summary>Terms are recycled in rotation. By the time a slot comes round again its term has decayed to nothing.</summary>
    public const int SlotCount = 16;

    /// <summary>A term's clock stops here; every token settles well inside it.</summary>
    public const double ClockSeconds = 4.0;

    readonly SpringTerm?[] _terms = new SpringTerm?[SlotCount];
    int _next;

    public AdditiveSpring(double initial) => Target = initial;

    /// <summary>Where the value is heading.</summary>
    public double Target { get; private set; }

    /// <summary>Raised whenever a term is added; the compositor binding copies it.</summary>
    public event Action<SpringTerm>? TermAdded;

    /// <summary>Raised when every term is cleared (a direct set); the compositor binding zeroes its slots.</summary>
    public event Action? TermsCleared;

    /// <summary>Raised when the target changes, after <see cref="TermAdded"/> or <see cref="TermsCleared"/>.</summary>
    public event Action<double>? TargetChanged;

    /// <summary>
    /// Head for a new target. <paramref name="kick"/> is extra velocity in units per second, used when a
    /// drag is released: the pointer's velocity minus the velocity already on screen.
    /// </summary>
    public SpringTerm Retarget(double target, Spring spring, double now, double kick = 0)
    {
        var (zw, wd) = SpringSegment.Coefficients(spring);
        double a = Target - target;        // cancels the target's move at τ = 0, so nothing jumps
        double v = kick;                   // adds no velocity of its own unless a drag supplied a kick

        // The slot about to be reused may still hold a term that has not died away (many changes in
        // quick succession). Its remaining position and speed are folded into the new term, so
        // nothing on screen jumps; from here on they decay together on the new spring.
        if (_terms[_next] is { } evicted && now - evicted.T0 < ClockSeconds)
        {
            var (x0, v0) = Evaluate(evicted, now);
            a += x0;
            v += v0;
        }
        double c = (v + zw * a) / wd;
        var term = new SpringTerm(_next, now, a, c, zw, wd);
        _terms[_next] = term;
        _next = (_next + 1) % SlotCount;
        Target = target;
        TermAdded?.Invoke(term);
        TargetChanged?.Invoke(target);
        return term;
    }

    /// <summary>
    /// Head for a new target so that, at this instant, the velocity becomes <paramref name="velocity"/>
    /// (a drag release). The kick is whatever separates that from the velocity already present.
    /// </summary>
    public SpringTerm Release(double target, Spring spring, double now, double velocity) =>
        Retarget(target, spring, now, velocity - Sample(now).V);

    /// <summary>Follow a pointer 1:1: the value is exactly what it is told, and every running term is dropped.</summary>
    public void SetDirect(double value)
    {
        bool hadTerms = false;
        for (int k = 0; k < SlotCount; k++)
        {
            hadTerms |= _terms[k] != null;
            _terms[k] = null;
        }
        Target = value;
        if (hadTerms) TermsCleared?.Invoke();
        TargetChanged?.Invoke(value);
    }

    /// <summary>Position and velocity at <paramref name="now"/>, from the model.</summary>
    public (double X, double V) Sample(double now)
    {
        double x = Target, v = 0;
        foreach (var maybe in _terms)
        {
            if (maybe is not { } s || now - s.T0 >= ClockSeconds) continue;   // spent: the compositor's clock has stopped
            var (tx, tv) = Evaluate(s, now);
            x += tx;
            v += tv;
        }
        return (x, v);
    }

    /// <summary>One term's contribution to position and velocity at <paramref name="now"/>.</summary>
    static (double X, double V) Evaluate(in SpringTerm s, double now)
    {
        double tau = Math.Clamp(now - s.T0, 0, ClockSeconds);
        double decay = Math.Exp(-s.Zw * tau), cos = Math.Cos(s.Wd * tau), sin = Math.Sin(s.Wd * tau);
        return (decay * (s.A * cos + s.C * sin),
                decay * ((s.C * s.Wd - s.Zw * s.A) * cos - (s.A * s.Wd + s.Zw * s.C) * sin));
    }

    /// <summary>True when the value is within <paramref name="tolerance"/> of its target and nearly still.</summary>
    public bool IsSettled(double now, double tolerance = 0.25)
    {
        var (x, v) = Sample(now);
        return Math.Abs(x - Target) <= tolerance && Math.Abs(v) <= tolerance * 4;
    }
}

using Microsoft.UI.Composition;

namespace IslandSpike.Motion;

public enum MotionEventKind { Spring, Direct }

/// <summary>
/// One instruction given to a motion value, kept so the test harness can rebuild what should have
/// reached the screen. <c>Commit</c> groups the instructions issued together (one morph).
/// </summary>
public readonly record struct MotionEvent(int Commit, double T, MotionEventKind Kind, double X0, double V0,
                                          double Kick, double OldTarget, double Target, Spring Spring);

/// <summary>
/// One animated number (a width, a height, a corner radius). The compositor does the drawing; this
/// class keeps a mathematical mirror so the app always knows roughly where the value is and how fast
/// it is moving. Visuals read the number as the scalar "v" of <see cref="Props"/>.
///
/// Three engines implement it. The spike exists to choose between them.
/// </summary>
public abstract class MotionValue
{
    protected readonly Compositor Compositor;
    public CompositionPropertySet Props { get; }
    public List<MotionEvent> Events { get; } = new();

    protected MotionValue(Compositor compositor, double initial)
    {
        Compositor = compositor;
        Props = compositor.CreatePropertySet();
        Props.InsertScalar("v", (float)initial);
        Events.Add(new MotionEvent(-1, Clock.Now, MotionEventKind.Direct, initial, 0, 0, initial, initial, default));
    }

    public abstract double Target { get; }

    /// <summary>Position and velocity right now, from the mirror.</summary>
    public abstract (double X, double V) Sample(double now);

    /// <summary>
    /// Head for a new target, continuing from the current position at the current velocity. A drag
    /// release passes the pointer's velocity instead.
    /// </summary>
    public void Retarget(double target, Spring spring, double now, int commit = -1, double? velocity = null)
    {
        var (x, v) = Sample(now);
        double v0 = velocity ?? v;
        Events.Add(new MotionEvent(commit, now, MotionEventKind.Spring, x, v0, v0 - v, Target, target, spring));
        OnRetarget(x, v0, v0 - v, target, spring, now);
    }

    /// <summary>Follow a pointer 1:1. No spring; the value is exactly what it is told.</summary>
    public void SetDirect(double x, double now)
    {
        Events.Add(new MotionEvent(-1, now, MotionEventKind.Direct, x, 0, 0, x, x, default));
        OnDirect(x, now);
    }

    protected abstract void OnRetarget(double x, double v0, double kick, double target, Spring spring, double now);
    protected abstract void OnDirect(double x, double now);

    /// <summary>
    /// For the test harness: a function giving the value that should be on screen at display time t,
    /// if each commit took <paramref name="latency"/>(commit) seconds to get there.
    /// </summary>
    public abstract Func<double, double> Predictor(Func<int, double> latency);

    /// <summary>When each instruction took effect on screen, never out of order.</summary>
    protected double[] AppliedTimes(Func<int, double> latency)
    {
        var at = new double[Events.Count];
        double previous = double.MinValue;
        for (int i = 0; i < at.Length; i++)
        {
            at[i] = Math.Max(previous, Events[i].T + latency(Events[i].Commit));
            previous = at[i];
        }
        return at;
    }

    protected static int LastAtOrBefore(double[] times, double t)
    {
        int low = 0, high = times.Length - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (times[mid] <= t) { found = mid; low = mid + 1; } else high = mid - 1;
        }
        return found;
    }

    /// <summary>While following a pointer the value moves between samples, so interpolate to the next one.</summary>
    protected double DirectValue(double[] at, int index, double t)
    {
        var e = Events[index];
        if (index + 1 < Events.Count && Events[index + 1].Kind == MotionEventKind.Direct)
        {
            double span = at[index + 1] - at[index];
            if (span > 1e-6 && span < 0.1) return e.X0 + (Events[index + 1].X0 - e.X0) * (t - at[index]) / span;
        }
        return e.X0;
    }
}

/// <summary>Shared mirror for the two engines that run one spring at a time.</summary>
public abstract class SegmentMotionValue : MotionValue
{
    SpringSegment _segment;
    bool _isSpring;
    double _static;

    protected virtual double MaxSpringSeconds => double.MaxValue;

    protected SegmentMotionValue(Compositor compositor, double initial) : base(compositor, initial) => _static = initial;

    public override double Target => _isSpring ? _segment.Target : _static;

    public override (double X, double V) Sample(double now) =>
        _isSpring ? _segment.Eval(now, MaxSpringSeconds) : (_static, 0);

    protected override void OnRetarget(double x, double v0, double kick, double target, Spring spring, double now)
    {
        _segment = new SpringSegment(now, x, v0, target, spring);
        _isSpring = true;
        StartSpring(_segment);
    }

    protected override void OnDirect(double x, double now)
    {
        _isSpring = false;
        _static = x;
        SetStatic(x);
    }

    protected abstract void StartSpring(in SpringSegment segment);
    protected abstract void SetStatic(double x);
}

/// <summary>
/// Option A: Composition's built-in spring. It starts from whatever is on screen when it takes effect,
/// so position can never jump; the mirror supplies the starting velocity, which the built-in spring
/// does not inherit by itself. Calibrated in this spike: Period = 1 / omega.
/// </summary>
public sealed class NaturalMotionValue : SegmentMotionValue
{
    readonly double _periodTimesOmega;
    readonly bool _carryVelocity;

    public NaturalMotionValue(Compositor compositor, double initial, double periodTimesOmega, bool carryVelocity)
        : base(compositor, initial)
    {
        _periodTimesOmega = periodTimesOmega;
        _carryVelocity = carryVelocity;
    }

    protected override void StartSpring(in SpringSegment s)
    {
        var spring = Compositor.CreateSpringScalarAnimation();
        spring.DampingRatio = (float)s.Spring.Damping;
        spring.Period = TimeSpan.FromSeconds(_periodTimesOmega / s.Spring.Omega);
        if (_carryVelocity) spring.InitialVelocity = (float)s.V0;
        spring.FinalValue = (float)s.Target;
        Props.StartAnimation("v", spring);
    }

    protected override void SetStatic(double x)
    {
        Props.StopAnimation("v");
        Props.InsertScalar("v", (float)x);
    }

    public override Func<double, double> Predictor(Func<int, double> latency)
    {
        // Each spring starts from where the previous motion actually was when it took effect.
        var at = AppliedTimes(latency);
        var segments = new SpringSegment[Events.Count];
        var isSpring = new bool[Events.Count];
        for (int i = 0; i < Events.Count; i++)
        {
            var e = Events[i];
            if (e.Kind != MotionEventKind.Spring) continue;
            double x0 = i == 0 ? e.X0
                      : isSpring[i - 1] ? segments[i - 1].Eval(at[i]).X
                      : Events[i - 1].X0;
            segments[i] = new SpringSegment(at[i], x0, _carryVelocity ? e.V0 : 0, e.Target, e.Spring);
            isSpring[i] = true;
        }
        return t =>
        {
            int i = LastAtOrBefore(at, t);
            if (i < 0) return Events[0].X0;
            return isSpring[i] ? segments[i].Eval(t).X : DirectValue(at, i, t);
        };
    }
}

/// <summary>
/// Option B: the spring's closed-form solution as an expression animation driven by a clock. The
/// same formula runs here and on the compositor. Each new flight starts from the position the
/// mirror computed, so if a commit reaches the screen later than usual the position can jump.
/// </summary>
public sealed class ExprMotionValue : SegmentMotionValue
{
    const float ClockSeconds = 4f;
    readonly ScalarKeyFrameAnimation _clock;

    protected override double MaxSpringSeconds => ClockSeconds;

    public ExprMotionValue(Compositor compositor, double initial) : base(compositor, initial)
    {
        Props.InsertScalar("t", 0f);
        Props.InsertScalar("A", 0f);
        Props.InsertScalar("C", 0f);
        Props.InsertScalar("zw", 1f);
        Props.InsertScalar("wd", 1f);
        Props.InsertScalar("target", (float)initial);

        var solution = compositor.CreateExpressionAnimation(
            "p.target + Pow(2.718281828459, -p.zw * p.t) * (p.A * Cos(p.wd * p.t) + p.C * Sin(p.wd * p.t))");
        solution.SetReferenceParameter("p", Props);
        Props.StartAnimation("v", solution);

        _clock = compositor.CreateScalarKeyFrameAnimation();
        _clock.InsertKeyFrame(0f, 0f);
        _clock.InsertKeyFrame(1f, ClockSeconds, compositor.CreateLinearEasingFunction());
        _clock.Duration = TimeSpan.FromSeconds(ClockSeconds);
    }

    protected override void StartSpring(in SpringSegment s)
    {
        Props.InsertScalar("target", (float)s.Target);
        Props.InsertScalar("A", (float)s.A);
        Props.InsertScalar("C", (float)s.C);
        Props.InsertScalar("zw", (float)s.Zw);
        Props.InsertScalar("wd", (float)s.Wd);
        Props.StartAnimation("t", _clock);
    }

    protected override void SetStatic(double x)
    {
        Props.StopAnimation("t");
        Props.InsertScalar("t", 0f);
        Props.InsertScalar("A", 0f);
        Props.InsertScalar("C", 0f);
        Props.InsertScalar("target", (float)x);
    }

    public override Func<double, double> Predictor(Func<int, double> latency)
    {
        var at = AppliedTimes(latency);
        return t =>
        {
            int i = LastAtOrBefore(at, t);
            if (i < 0) return Events[0].X0;
            var e = Events[i];
            return e.Kind == MotionEventKind.Spring
                ? new SpringSegment(at[i], e.X0, e.V0, e.Target, e.Spring).Eval(t, ClockSeconds).X
                : DirectValue(at, i, t);
        };
    }
}

/// <summary>
/// Option C: additive springs, the technique behind iOS's interruptible animations. A change of
/// target never replaces what is running. The target moves at once, and a new decaying term is added
/// that starts out exactly cancelling the move. Earlier terms keep running untouched, so position and
/// velocity are continuous on screen no matter when the commit lands.
///
/// value = target + sum of terms, each term = e^(-zw t) (a cos(wd t) + c sin(wd t)) on its own clock.
/// </summary>
public sealed class AdditiveMotionValue : MotionValue
{
    const int SlotCount = 16;
    const float ClockSeconds = 4f;

    struct Slot { public double T0, A, C, Zw, Wd; public bool Live; }

    readonly Slot[] _slots = new Slot[SlotCount];
    readonly ScalarKeyFrameAnimation _clock;
    int _next;
    double _target;
    bool _slotsClear = true;

    public AdditiveMotionValue(Compositor compositor, double initial) : base(compositor, initial)
    {
        _target = initial;
        Props.InsertScalar("target", (float)initial);

        var sum = new System.Text.StringBuilder("p.target");
        for (int k = 0; k < SlotCount; k++)
        {
            Props.InsertScalar($"a{k}", 0f);
            Props.InsertScalar($"c{k}", 0f);
            Props.InsertScalar($"zw{k}", 1f);
            Props.InsertScalar($"wd{k}", 1f);
            Props.InsertScalar($"t{k}", 0f);
            Props.InsertScalar($"s{k}", 0f);
            var term = compositor.CreateExpressionAnimation(
                $"Pow(2.718281828459, -p.zw{k} * p.t{k}) * (p.a{k} * Cos(p.wd{k} * p.t{k}) + p.c{k} * Sin(p.wd{k} * p.t{k}))");
            term.SetReferenceParameter("p", Props);
            Props.StartAnimation($"s{k}", term);
            sum.Append($" + p.s{k}");
        }
        var total = compositor.CreateExpressionAnimation(sum.ToString());
        total.SetReferenceParameter("p", Props);
        Props.StartAnimation("v", total);

        _clock = compositor.CreateScalarKeyFrameAnimation();
        _clock.InsertKeyFrame(0f, 0f);
        _clock.InsertKeyFrame(1f, ClockSeconds, compositor.CreateLinearEasingFunction());
        _clock.Duration = TimeSpan.FromSeconds(ClockSeconds);
    }

    public override double Target => _target;

    public override (double X, double V) Sample(double now)
    {
        double x = _target, v = 0;
        for (int k = 0; k < SlotCount; k++)
        {
            ref var s = ref _slots[k];
            if (!s.Live) continue;
            double tau = Math.Min(now - s.T0, ClockSeconds);
            double decay = Math.Exp(-s.Zw * tau), cos = Math.Cos(s.Wd * tau), sin = Math.Sin(s.Wd * tau);
            x += decay * (s.A * cos + s.C * sin);
            v += decay * ((s.C * s.Wd - s.Zw * s.A) * cos - (s.A * s.Wd + s.Zw * s.C) * sin);
        }
        return (x, v);
    }

    static (double A, double C, double Zw, double Wd) Term(double oldTarget, double target, double kick, Spring spring)
    {
        double zeta = Math.Min(spring.Damping, 0.9999), omega = spring.Omega;
        double zw = zeta * omega, wd = omega * Math.Sqrt(1 - zeta * zeta);
        double a = oldTarget - target;        // cancels the target's move at t = 0, so nothing jumps
        double c = (kick + zw * a) / wd;      // adds no velocity of its own unless a drag supplied a kick
        return (a, c, zw, wd);
    }

    protected override void OnRetarget(double x, double v0, double kick, double target, Spring spring, double now)
    {
        var (a, c, zw, wd) = Term(_target, target, kick, spring);
        int k = _next;
        _next = (_next + 1) % SlotCount;   // the oldest term is reused; by then it has decayed to nothing
        _slots[k] = new Slot { T0 = now, A = a, C = c, Zw = zw, Wd = wd, Live = true };
        _target = target;
        _slotsClear = false;

        Props.InsertScalar("target", (float)target);
        Props.InsertScalar($"a{k}", (float)a);
        Props.InsertScalar($"c{k}", (float)c);
        Props.InsertScalar($"zw{k}", (float)zw);
        Props.InsertScalar($"wd{k}", (float)wd);
        Props.StartAnimation($"t{k}", _clock);
    }

    protected override void OnDirect(double x, double now)
    {
        _target = x;
        if (!_slotsClear)
        {
            for (int k = 0; k < SlotCount; k++)
            {
                _slots[k].Live = false;
                Props.InsertScalar($"a{k}", 0f);
                Props.InsertScalar($"c{k}", 0f);
            }
            _slotsClear = true;
        }
        Props.InsertScalar("target", (float)x);
    }

    public override Func<double, double> Predictor(Func<int, double> latency)
    {
        var at = AppliedTimes(latency);
        int n = Events.Count;
        var terms = new (double A, double C, double Zw, double Wd)[n];
        var lastDirect = new int[n];
        int direct = 0;
        for (int i = 0; i < n; i++)
        {
            var e = Events[i];
            if (e.Kind == MotionEventKind.Direct) direct = i;
            else terms[i] = Term(e.OldTarget, e.Target, e.Kick, e.Spring);
            lastDirect[i] = direct;
        }
        return t =>
        {
            int i = LastAtOrBefore(at, t);
            if (i < 0) return Events[0].X0;
            if (Events[i].Kind == MotionEventKind.Direct) return DirectValue(at, i, t);
            double x = Events[i].Target;
            for (int j = Math.Max(lastDirect[i] + 1, i - SlotCount + 1); j <= i; j++)
            {
                double tau = Math.Min(t - at[j], ClockSeconds);
                var (a, c, zw, wd) = terms[j];
                x += Math.Exp(-zw * tau) * (a * Math.Cos(wd * tau) + c * Math.Sin(wd * tau));
            }
            return x;
        };
    }
}

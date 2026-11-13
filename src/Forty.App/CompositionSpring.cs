using Forty.Motion;
using Microsoft.UI.Composition;

namespace Forty.App;

/// <summary>
/// One animated number on the compositor (a width, a corner radius, an opacity), driven by the
/// additive spring model in Forty.Motion. Every term the model adds is copied into one of sixteen
/// expression slots, and the compositor evaluates <c>target + Σ terms</c> at the display's own rate.
/// The target change and its cancelling term are written together, so they reach the screen in the
/// same commit and nothing jumps, however late the commit lands (spike S2).
/// Visuals read the value as the scalar "v" of <see cref="Props"/>.
///
/// A settled spring rests: its seventeen expressions stop and "v" holds the target as a plain value.
/// The compositor evaluates every running expression on every frame it draws, so while anything moves
/// (music's fluid colours, say), a hundred springs that are standing still would otherwise cost more
/// than everything that is actually moving. A new term wakes the spring on the same frame.
/// </summary>
sealed class CompositionSpring
{
    readonly ScalarKeyFrameAnimation _clock;
    readonly ExpressionAnimation[] _terms = new ExpressionAnimation[AdditiveSpring.SlotCount];
    readonly ExpressionAnimation _total;
    bool _running;
    int _restVersion;

    public AdditiveSpring Model { get; }
    public CompositionPropertySet Props { get; }

    /// <summary>How many springs exist and how many are running right now (for diagnostics).</summary>
    public static int Created, Running;

    public CompositionSpring(Compositor compositor, double initial)
    {
        Created++;
        Model = new AdditiveSpring(initial);
        Props = compositor.CreatePropertySet();
        Props.InsertScalar("target", (float)initial);

        var sum = new System.Text.StringBuilder("p.target");
        for (int k = 0; k < AdditiveSpring.SlotCount; k++)
        {
            foreach (string name in new[] { "a", "c", "t", "s" }) Props.InsertScalar($"{name}{k}", 0f);
            Props.InsertScalar($"zw{k}", 1f);
            Props.InsertScalar($"wd{k}", 1f);
            var term = _terms[k] = compositor.CreateExpressionAnimation(
                $"Pow(2.718281828459, -p.zw{k} * p.t{k}) * (p.a{k} * Cos(p.wd{k} * p.t{k}) + p.c{k} * Sin(p.wd{k} * p.t{k}))");
            term.SetReferenceParameter("p", Props);
            Props.InsertScalar($"s{k}", 0f);
            sum.Append($" + p.s{k}");
        }
        Props.InsertScalar("v", (float)initial);
        _total = compositor.CreateExpressionAnimation(sum.ToString());
        _total.SetReferenceParameter("p", Props);
        // Starts at rest: nothing is evaluated until the first move. FORTY_SPRING_REST=0 keeps every
        // spring running, as before, to measure what resting saves.
        if (!RestEnabled) Wake();

        // Each term runs on its own linear clock from 0 to 4 seconds, restarted when its slot is reused.
        _clock = compositor.CreateScalarKeyFrameAnimation();
        _clock.InsertKeyFrame(0f, 0f);
        _clock.InsertKeyFrame(1f, (float)AdditiveSpring.ClockSeconds, compositor.CreateLinearEasingFunction());
        _clock.Duration = TimeSpan.FromSeconds(AdditiveSpring.ClockSeconds);

        Model.TermAdded += term =>
        {
            Wake();
            int k = term.Slot;
            Props.InsertScalar($"a{k}", (float)term.A);
            Props.InsertScalar($"c{k}", (float)term.C);
            Props.InsertScalar($"zw{k}", (float)term.Zw);
            Props.InsertScalar($"wd{k}", (float)term.Wd);
            Props.StartAnimation($"t{k}", _clock);
        };
        Model.TermsCleared += () =>
        {
            for (int k = 0; k < AdditiveSpring.SlotCount; k++)
            {
                Props.InsertScalar($"a{k}", 0f);
                Props.InsertScalar($"c{k}", 0f);
            }
        };
        Model.TargetChanged += target =>
        {
            Props.InsertScalar("target", (float)target);
            if (!_running) Props.InsertScalar("v", (float)target);   // at rest the value is the target itself
        };
    }

    void Wake()
    {
        int version = ++_restVersion;
        if (!_running)
        {
            _running = true;
            Running++;
            for (int k = 0; k < _terms.Length; k++) Props.StartAnimation($"s{k}", _terms[k]);
            Props.StartAnimation("v", _total);
        }
        _ = RestLater(version);
    }

    /// <summary>Every term's clock runs for <see cref="AdditiveSpring.ClockSeconds"/>; after that, if nothing new arrived, rest.</summary>
    static readonly bool RestEnabled = Environment.GetEnvironmentVariable("FORTY_SPRING_REST") != "0";

    async Task RestLater(int version)
    {
        if (!RestEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(AdditiveSpring.ClockSeconds + 0.1));
        if (version != _restVersion || !_running) return;
        if (!Model.IsSettled(Clock.Now)) { _ = RestLater(version); return; }
        _running = false;
        Running--;
        Props.StopAnimation("v");
        for (int k = 0; k < _terms.Length; k++) { Props.StopAnimation($"s{k}"); Props.InsertScalar($"s{k}", 0f); }
        Props.InsertScalar("v", (float)Model.Target);
    }

    public double Target => Model.Target;

    /// <summary>Where the value is right now, according to the model.</summary>
    public double Current => Model.Sample(Clock.Now).X;

    /// <summary>
    /// Head for <paramref name="target"/>. With reduced motion on, sizes and positions change in one
    /// step; fades (<paramref name="isFade"/>) still ease, because a fade carries no motion.
    /// </summary>
    public void To(double target, Spring spring, bool isFade = false)
    {
        if (target == Model.Target && Model.IsSettled(Clock.Now)) return;
        if (MotionPolicy.Reduced && !isFade) Model.SetDirect(target);
        else Model.Retarget(target, isFade && MotionPolicy.Reduced ? MotionPolicy.ReducedFade : spring, Clock.Now);
    }

    /// <summary>A release from a drag: head for <paramref name="target"/> carrying <paramref name="velocity"/> (units per second).</summary>
    public void Throw(double target, Spring spring, double velocity)
    {
        if (MotionPolicy.Reduced) { Model.SetDirect(target); return; }
        Model.Release(target, spring, Clock.Now, velocity);
    }

    /// <summary>Jump to a value with no motion (a pointer being followed 1:1, or the first frame).</summary>
    public void Set(double value) => Model.SetDirect(value);

    /// <summary>Binds a visual's property to this value through an expression such as "Vector3(s.v, s.v, 1)".</summary>
    public void Drive(CompositionObject target, string property, string expression)
    {
        var animation = target.Compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("s", Props);
        target.StartAnimation(property, animation);
    }
}

/// <summary>Windows' "Animation effects" switch, plus FORTY_REDUCED_MOTION=1 for testing (F10).</summary>
static class MotionPolicy
{
    /// <summary>The 150 ms cross-fade MOTION.md section 11 asks for, as a spring that settles in about that time.</summary>
    public static readonly Spring ReducedFade = new(1.0, 0.14);

    public static bool Reduced { get; set; } = Read();

    public static bool Read() =>
        Environment.GetEnvironmentVariable("FORTY_REDUCED_MOTION") == "1" ||
        !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
}

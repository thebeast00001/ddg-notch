using System.Numerics;
using Forty.Core;
using Forty.Motion;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace Forty.App;

/// <summary>What a face is doing. Each is a set of spring targets for the eyes, so any can interrupt any other.</summary>
enum Expression { Neutral, Perk, Happy, Surprised, Focused, Determined, Sleepy, Asleep, Sad, Curious, Thinking, Listening, Wink }

/// <summary>A face's eyes in a 24 × 24 box: size, corner, spacing between their centres, and tilt (degrees, top inward).</summary>
sealed record FaceStyle(float EyeW, float EyeH, float Radius, float Spacing, float Tilt)
{
    /// <summary>Forty, the host: two tall soft capsules.</summary>
    public static readonly FaceStyle Host = new(5.0f, 9.4f, 2.5f, 9.4f, 0);

    public static FaceStyle For(Eyes eyes) => eyes switch
    {
        Eyes.Round => new(6.6f, 6.6f, 3.3f, 9.8f, 0),      // Echo: round, attentive
        Eyes.Tall => new(4.8f, 8.8f, 2.4f, 9.0f, 7),       // Iris: tall, tilted in, curious
        Eyes.Square => new(6.8f, 6.4f, 1.8f, 10.0f, 0),    // Bolt: square, determined
        _ => new(7.8f, 4.2f, 2.1f, 10.4f, 0),              // Tempo: wide, calm
    };
}

/// <summary>
/// A face made of two eyes, drawn by the compositor. Each eye is a rounded rectangle whose width,
/// height, position and tilt follow springs, plus a "smile" amount that squashes it and crossfades
/// it into an arc (happy eyes). Shared springs carry the gaze, the blink lid, and a mouth line that
/// speaking moves. An expression is only a set of targets, so it morphs from wherever the eyes are.
///
/// Blinking is the one thing a face does by itself: every few seconds while it is shown (never a
/// loop: one blink is a single close and open), and not at all with reduced motion, in battery saver
/// or while asleep, so a resting island draws nothing between blinks.
/// </summary>
sealed class Face
{
    const float Mid = 12;
    static readonly Spring PoseSpring = new(0.92, 0.26), Smile = new(0.78, 0.30), Gaze = new(1.0, 0.16);

    sealed class Eye
    {
        public required CompositionSpring W, H, X, Y, Rot, Arc;
    }

    readonly FaceStyle _style;
    readonly Compositor _c;
    readonly Eye[] _eyes = new Eye[2];
    readonly CompositionSpring _gx, _gy, _mouth, _mouthShown;
    // The lid is not a spring: a blink is one fixed gesture, played as a 200 ms keyframe animation, so
    // the compositor draws frames only while the eyes are actually closing and opening. (A spring here
    // would keep its clock running for seconds after every blink, and with a blink every few seconds
    // the island would never stop drawing.)
    readonly CompositionPropertySet _lid;
    readonly CompositionColorBrush _ink;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _blinker;
    static readonly Random Chance = new();
    double _level;
    bool _autoBlink;

    public Grid Element { get; }
    public Expression Expression { get; private set; } = Expression.Neutral;

    public Face(FaceStyle style, Compositor c, double size, Color colour)
    {
        _style = style;
        _c = c;
        _ink = c.CreateColorBrush(colour);
        _gx = new CompositionSpring(c, 0);
        _gy = new CompositionSpring(c, 0);
        _lid = c.CreatePropertySet();
        _lid.InsertScalar("v", 0f);
        _mouth = new CompositionSpring(c, 0);
        _mouthShown = new CompositionSpring(c, 0);

        Element = new Grid { Width = size, Height = size, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(Element, AccessibilityView.Raw);   // decoration: words say what it means
        var visual = c.CreateShapeVisual();
        visual.Size = new Vector2((float)size);
        visual.ViewBox = c.CreateViewBox();
        visual.ViewBox.Size = new Vector2(24, 24);
        var clear = c.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));

        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1 : 1;
            var eye = _eyes[i] = new Eye
            {
                W = new CompositionSpring(c, style.EyeW), H = new CompositionSpring(c, style.EyeH),
                X = new CompositionSpring(c, 0), Y = new CompositionSpring(c, 0),
                Rot = new CompositionSpring(c, -side * style.Tilt), Arc = new CompositionSpring(c, 0),
            };
            string cx = F(Mid + side * style.Spacing / 2);
            // The eye's centre, with the gaze added: eyes look a little, never wander off the face.
            string centreX = $"({cx} + x.v + gx.v * 2.2)", centreY = $"({F(Mid)} + y.v + gy.v * 1.6)";
            string height = "Max(h.v * (1 - 0.92 * Clamp(lid.v, 0, 1)) * (1 - Clamp(arc.v, 0, 1)), 0.45)";
            string ratio = F(style.Radius / style.EyeW);

            var rect = c.CreateRoundedRectangleGeometry();
            Bind(rect, "Size", $"Vector2(w.v, {height})", eye);
            Bind(rect, "Offset", $"Vector2({centreX} - w.v * 0.5, {centreY} - {height} * 0.5)", eye);
            Bind(rect, "CornerRadius", $"Vector2(Min(w.v * {ratio}, Min(w.v, {height}) * 0.5), Min(w.v * {ratio}, Min(w.v, {height}) * 0.5))", eye);
            var eyeShape = c.CreateSpriteShape(rect);
            eyeShape.FillBrush = _ink;
            Bind(eyeShape, "CenterPoint", $"Vector2({centreX}, {centreY})", eye);
            Bind(eyeShape, "RotationAngleInDegrees", "rot.v", eye);
            visual.Shapes.Add(eyeShape);

            // Happy eyes: the eye squashes away as an arc (∩) of the same width fades in.
            float half = style.EyeW / 2;
            var arcPath = c.CreatePathGeometry(PathMaker.Make(b =>
            {
                b.BeginFigure(-half, 1.5f);
                b.AddCubicBezier(new Vector2(-half, -2.1f), new Vector2(half, -2.1f), new Vector2(half, 1.5f));
                b.EndFigure(CanvasFigureLoop.Open);
            }));
            var arc = c.CreateSpriteShape(arcPath);
            arc.StrokeThickness = 2.15f;
            arc.StrokeStartCap = arc.StrokeEndCap = CompositionStrokeCap.Round;
            var arcInk = c.CreateColorBrush();
            Bind(arcInk, "Color", "ColorLerp(clear.Color, ink.Color, Clamp(arc.v * 1.4 - 0.2, 0, 1))", eye);
            arc.StrokeBrush = arcInk;
            Bind(arc, "Offset", $"Vector2({centreX}, {centreY} - 0.4)", eye);
            Bind(arc, "Scale", $"Vector2(w.v / {F(style.EyeW)}, 1)", eye);
            visual.Shapes.Add(arc);

            void Bind(CompositionObject target, string property, string expression, Eye e)
            {
                var animation = c.CreateExpressionAnimation(expression);
                animation.SetReferenceParameter("w", e.W.Props);
                animation.SetReferenceParameter("h", e.H.Props);
                animation.SetReferenceParameter("x", e.X.Props);
                animation.SetReferenceParameter("y", e.Y.Props);
                animation.SetReferenceParameter("rot", e.Rot.Props);
                animation.SetReferenceParameter("arc", e.Arc.Props);
                animation.SetReferenceParameter("gx", _gx.Props);
                animation.SetReferenceParameter("gy", _gy.Props);
                animation.SetReferenceParameter("lid", _lid);
                animation.SetReferenceParameter("ink", _ink);
                animation.SetReferenceParameter("clear", clear);
                target.StartAnimation(property, animation);
            }
        }

        // The mouth: a short line under the eyes that only speaking shows, its length following the voice.
        var mouth = c.CreateLineGeometry();
        var start = c.CreateExpressionAnimation("Vector2(12 - (1.2 + 3.6 * Clamp(m.v, 0, 1)), 19.2)");
        start.SetReferenceParameter("m", _mouth.Props);
        mouth.StartAnimation("Start", start);
        var end = c.CreateExpressionAnimation("Vector2(12 + (1.2 + 3.6 * Clamp(m.v, 0, 1)), 19.2)");
        end.SetReferenceParameter("m", _mouth.Props);
        mouth.StartAnimation("End", end);
        var mouthShape = c.CreateSpriteShape(mouth);
        mouthShape.StrokeThickness = 1.7f;
        mouthShape.StrokeStartCap = mouthShape.StrokeEndCap = CompositionStrokeCap.Round;
        var mouthInk = c.CreateColorBrush();
        var mouthColour = c.CreateExpressionAnimation("ColorLerp(clear.Color, ink.Color, Clamp(s.v, 0, 1))");
        mouthColour.SetReferenceParameter("clear", clear);
        mouthColour.SetReferenceParameter("ink", _ink);
        mouthColour.SetReferenceParameter("s", _mouthShown.Props);
        mouthInk.StartAnimation("Color", mouthColour);
        mouthShape.StrokeBrush = mouthInk;
        visual.Shapes.Add(mouthShape);

        ElementCompositionPreview.SetElementChildVisual(Element, visual);

        _blinker = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _blinker.IsRepeating = false;
        _blinker.Tick += (_, _) => Guard.Run(() =>
        {
            // While the app closes, the compositor can go before a blink that was already due: it just stops.
            try { Blink(@double: Chance.NextDouble() < 0.18); ScheduleBlink(); }
            catch (ObjectDisposedException) { _blinker.Stop(); }
        }, "blink");
    }

    static string F(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    public void SetColour(Color colour) => _ink.Color = colour;

    /// <summary>Moves to an expression from wherever the eyes are.</summary>
    public void Express(Expression expression)
    {
        Expression = expression;
        var s = _style;
        for (int i = 0; i < 2; i++)
        {
            var e = _eyes[i];
            float side = i == 0 ? -1 : 1;
            double w = s.EyeW, h = s.EyeH, x = 0, y = 0, rot = -side * s.Tilt, arc = 0;
            switch (expression)
            {
                case Expression.Perk: w *= 1.08; h *= 1.12; y = -0.4; break;
                case Expression.Happy: arc = 1; y = -0.5; break;
                case Expression.Surprised: w *= 1.3; h *= 1.12; y = -0.7; rot = 0; break;
                case Expression.Focused: h *= 0.5; y = 0.6; break;
                case Expression.Determined: h *= 0.62; y = 0.4; rot = -side * (s.Tilt + 11); break;
                case Expression.Sleepy: h *= 0.26; y = 1.4; break;
                case Expression.Asleep: h *= 0.08; y = 1.8; break;
                case Expression.Sad: h *= 0.8; y = 0.8; rot = side * 13; break;
                case Expression.Curious: if (i == 0) { h *= 0.74; y = 0.4; } else { h *= 1.14; w *= 1.06; y = -0.3; } break;
                case Expression.Thinking: h *= 0.86; break;
                case Expression.Listening: w *= 1.08; h *= 1.06 + 0.22 * _level; break;
                case Expression.Wink: if (i == 1) { arc = 1; y = -0.4; } break;
            }
            e.W.To(w, PoseSpring);
            e.H.To(h, PoseSpring);
            e.X.To(x, PoseSpring);
            e.Y.To(y, PoseSpring);
            e.Rot.To(rot, PoseSpring);
            e.Arc.To(arc, arc > 0 ? Smile : PoseSpring);
        }
        // Some expressions carry their own gaze; the rest keep whatever the eyes were looking at.
        if (expression == Expression.Thinking) Look(0.7, -0.8);
        else if (expression == Expression.Curious) Look(0.45, -0.35);
        else if (expression is Expression.Asleep or Expression.Sleepy or Expression.Happy or Expression.Sad) Look(0, 0);
        if (expression != Expression.Listening) _level = 0;
    }

    /// <summary>Where the eyes look: -1 to 1 on each axis (left/up negative).</summary>
    public void Look(double x, double y)
    {
        _gx.To(Math.Clamp(x, -1, 1), Gaze);
        _gy.To(Math.Clamp(y, -1, 1), Gaze);
    }

    /// <summary>The voice level while listening (the eyes open with it) or speaking (the mouth follows it).</summary>
    public void SetLevel(double level)
    {
        _level = Math.Clamp(level, 0, 1);
        if (Expression == Expression.Listening)
            foreach (var e in _eyes) e.H.To(_style.EyeH * (1.06 + 0.22 * _level), Springs.Quick);
        _mouth.To(_level, Springs.Quick);
    }

    /// <summary>Shows or hides the mouth line (speaking).</summary>
    public void Speak(bool on) => _mouthShown.To(on ? 1 : 0, PoseSpring, isFade: true);

    /// <summary>One blink: the lids close in about 50 ms and open in about 120, like a real one. Twice, now and then.</summary>
    public void Blink(bool @double = false)
    {
        if (MotionPolicy.Reduced || Expression is Expression.Asleep or Expression.Happy) return;
        var blink = _c.CreateScalarKeyFrameAnimation();
        var close = _c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));    // the lid falls
        var open = _c.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.3f, 1f));     // and lifts, easing out
        float total = @double ? 0.52f : 0.2f;
        void One(float start)
        {
            blink.InsertKeyFrame(start / total, 0f);
            blink.InsertKeyFrame((start + 0.05f) / total, 1f, close);
            blink.InsertKeyFrame((start + 0.08f) / total, 1f);
            blink.InsertKeyFrame((start + 0.2f) / total, 0f, open);
        }
        One(0);
        if (@double) One(0.32f);
        blink.Duration = TimeSpan.FromSeconds(total);
        _lid.StartAnimation("v", blink);
    }

    /// <summary>Blinks by itself every few seconds while on; off for reduced motion, battery saver, sleep or when hidden.</summary>
    public void AutoBlink(bool on)
    {
        on &= !MotionPolicy.Reduced;
        if (on == _autoBlink) return;
        _autoBlink = on;
        if (on) ScheduleBlink(); else _blinker.Stop();
    }

    void ScheduleBlink()
    {
        if (!_autoBlink) return;
        _blinker.Interval = TimeSpan.FromSeconds(3.5 + Chance.NextDouble() * 4.5);   // 3.5 to 8 s: calm, and each blink costs about a dozen frames
        _blinker.Start();
    }

    /// <summary>For the layout check: every spring at its target now, the lid open or at <paramref name="lid"/>.</summary>
    public void Settle(double lid = 0)
    {
        foreach (var e in _eyes) foreach (var s in new[] { e.W, e.H, e.X, e.Y, e.Rot, e.Arc }) s.Set(s.Target);
        foreach (var s in new[] { _gx, _gy, _mouth, _mouthShown }) s.Set(s.Target);
        _lid.StopAnimation("v");
        _lid.InsertScalar("v", (float)lid);
    }
}

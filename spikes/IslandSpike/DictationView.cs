using System.Numerics;
using IslandSpike.Motion;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;

namespace IslandSpike;

/// <summary>
/// What the island shows while you dictate: a level meter that follows your voice, then a small
/// working indicator, then a check that draws once. Everything is drawn and animated by the
/// compositor, and nothing moves when none of it is showing.
/// </summary>
sealed class DictationView
{
    const double BarWidth = 3, BarGap = 3, BarMin = 3, BarMax = 20, MeterHeight = 24;
    static readonly double[] Weights = { 0.55, 0.85, 1.0, 0.8, 0.5 };

    readonly Compositor _c;
    readonly MotionValue[] _bars = new MotionValue[Weights.Length];
    readonly MotionValue _meterOpacity, _dotsOpacity, _checkOpacity, _failOpacity;
    readonly CompositionPropertySet _spin;
    readonly ScalarKeyFrameAnimation _spinLoop, _draw;
    readonly CompositionPathGeometry _checkPath;
    uint _noise = 12345;

    public DictationView(Island island)
    {
        _c = island.Compositor;
        var white = _c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));

        // ── Level meter: five bars, each on its own spring ──
        double meterWidth = Weights.Length * BarWidth + (Weights.Length - 1) * BarGap;
        var meter = _c.CreateShapeVisual();
        meter.Size = new Vector2((float)meterWidth, (float)MeterHeight);
        meter.Offset = new Vector3((float)(-meterWidth / 2), (float)(-MeterHeight / 2), 0);
        for (int i = 0; i < Weights.Length; i++)
        {
            _bars[i] = new AdditiveMotionValue(_c, BarMin);
            var bar = _c.CreateRoundedRectangleGeometry();
            bar.CornerRadius = new Vector2((float)(BarWidth / 2));

            var size = _c.CreateExpressionAnimation("Vector2(barWidth, Max(b.v, barMin))");
            size.SetReferenceParameter("b", _bars[i].Props);
            size.SetScalarParameter("barWidth", (float)BarWidth);
            size.SetScalarParameter("barMin", (float)BarMin);
            bar.StartAnimation("Size", size);

            var offset = _c.CreateExpressionAnimation("Vector2(x, (meterHeight - Max(b.v, barMin)) * 0.5)");
            offset.SetReferenceParameter("b", _bars[i].Props);
            offset.SetScalarParameter("x", (float)(i * (BarWidth + BarGap)));
            offset.SetScalarParameter("meterHeight", (float)MeterHeight);
            offset.SetScalarParameter("barMin", (float)BarMin);
            bar.StartAnimation("Offset", offset);

            var shape = _c.CreateSpriteShape(bar);
            shape.FillBrush = white;
            meter.Shapes.Add(shape);
        }
        _meterOpacity = Fade(meter);

        // ── Working: three dots chasing each other round a small circle, once a second ──
        _spin = _c.CreatePropertySet();
        _spin.InsertScalar("t", 0f);
        _spinLoop = _c.CreateScalarKeyFrameAnimation();
        _spinLoop.InsertKeyFrame(0f, 0f);
        _spinLoop.InsertKeyFrame(1f, 1f, _c.CreateLinearEasingFunction());
        _spinLoop.Duration = TimeSpan.FromSeconds(1);
        _spinLoop.IterationBehavior = AnimationIterationBehavior.Forever;

        var dots = _c.CreateShapeVisual();
        dots.Size = new Vector2(24, 24);
        dots.Offset = new Vector3(-12, -12, 0);
        for (int i = 0; i < 3; i++)
        {
            var dot = _c.CreateEllipseGeometry();
            dot.Radius = new Vector2(2f - i * 0.3f);
            var shape = _c.CreateSpriteShape(dot);
            shape.FillBrush = _c.CreateColorBrush(Windows.UI.Color.FromArgb((byte)(255 >> i), 255, 255, 255));
            var orbit = _c.CreateExpressionAnimation(
                "Vector2(12 + 7 * Cos((s.t - phase) * 6.2831853), 12 + 7 * Sin((s.t - phase) * 6.2831853))");
            orbit.SetReferenceParameter("s", _spin);
            orbit.SetScalarParameter("phase", i * 0.09f);
            shape.StartAnimation("Offset", orbit);
            dots.Shapes.Add(shape);
        }
        _dotsOpacity = Fade(dots);

        // ── Done: a check that draws itself once ──
        using (var builder = new CanvasPathBuilder(CanvasDevice.GetSharedDevice()))
        {
            builder.BeginFigure(1.5f, 7.5f);
            builder.AddLine(6.5f, 12.5f);
            builder.AddLine(16.5f, 2.5f);
            builder.EndFigure(CanvasFigureLoop.Open);
            _checkPath = _c.CreatePathGeometry(new CompositionPath(CanvasGeometry.CreatePath(builder)));
        }
        var checkShape = _c.CreateSpriteShape(_checkPath);
        checkShape.StrokeBrush = _c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0x3B, 0xD6, 0x71));
        checkShape.StrokeThickness = 2.2f;
        checkShape.StrokeStartCap = CompositionStrokeCap.Round;
        checkShape.StrokeEndCap = CompositionStrokeCap.Round;
        checkShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        var check = _c.CreateShapeVisual();
        check.Size = new Vector2(18, 15);
        check.Offset = new Vector3(-9, -7.5f, 0);
        check.Shapes.Add(checkShape);
        _checkOpacity = Fade(check);

        _draw = _c.CreateScalarKeyFrameAnimation();
        _draw.InsertKeyFrame(0f, 0f);
        _draw.InsertKeyFrame(1f, 1f, _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f)));
        _draw.Duration = TimeSpan.FromMilliseconds(280);

        // ── Failed: a small red dot ──
        var failDot = _c.CreateEllipseGeometry();
        failDot.Radius = new Vector2(4f);
        failDot.Center = new Vector2(6f, 6f);
        var failShape = _c.CreateSpriteShape(failDot);
        failShape.FillBrush = _c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x45, 0x3A));
        var fail = _c.CreateShapeVisual();
        fail.Size = new Vector2(12, 12);
        fail.Offset = new Vector3(-6, -6, 0);
        fail.Shapes.Add(failShape);
        _failOpacity = Fade(fail);

        foreach (var visual in new Visual[] { meter, dots, check, fail }) island.Content.Children.InsertAtTop(visual);
    }

    /// <summary>Gives a visual an opacity that is a motion value, starting invisible.</summary>
    MotionValue Fade(Visual visual)
    {
        var opacity = new AdditiveMotionValue(_c, 0);
        var bind = _c.CreateExpressionAnimation("Clamp(o.v, 0, 1)");
        bind.SetReferenceParameter("o", opacity.Props);
        visual.StartAnimation("Opacity", bind);
        return opacity;
    }

    void Show(MotionValue? which)
    {
        double now = Clock.Now;
        foreach (var opacity in new[] { _meterOpacity, _dotsOpacity, _checkOpacity, _failOpacity })
        {
            double target = opacity == which ? 1 : 0;
            if (Math.Abs(opacity.Target - target) > 0.001) opacity.Retarget(target, Spring.Quick, now);
        }
        if (which != _dotsOpacity) { _spin.StopAnimation("t"); _spin.InsertScalar("t", 0f); }
    }

    public void ShowListening()
    {
        SetLevel(0);
        Show(_meterOpacity);
    }

    /// <summary>Voice level from 0 to 1. Each bar heads for its own share of it.</summary>
    public void SetLevel(double level)
    {
        double now = Clock.Now;
        level = Math.Clamp(level, 0, 1);
        for (int i = 0; i < _bars.Length; i++)
        {
            _noise = _noise * 1664525 + 1013904223;                    // a little variety, so the bars are not a block
            double variety = 0.78 + 0.22 * ((_noise >> 8) & 0xFFFF) / 65535.0;
            _bars[i].Retarget(BarMin + (BarMax - BarMin) * level * Weights[i] * variety, Spring.Press, now);
        }
    }

    public void ShowWorking()
    {
        Show(_dotsOpacity);
        _spin.StartAnimation("t", _spinLoop);
    }

    public void ShowDone()
    {
        Show(_checkOpacity);
        _checkPath.TrimEnd = 0f;
        _checkPath.StartAnimation("TrimEnd", _draw);
    }

    public void ShowFailed() => Show(_failOpacity);

    public void Hide() => Show(null);
}

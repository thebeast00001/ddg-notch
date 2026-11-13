using System.Numerics;
using Forty.Core;
using Forty.Motion;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Forty.App;

/// <summary>
/// An agent's mark (F14, F15, F38): its silhouette in its hue, drawn by the compositor from a few
/// shapes in a 20 × 20 box (so it is crisp at any size), with one pose per state. Every pose is a set
/// of spring targets, so a state change can interrupt another mid-way like everything else.
///
/// - Idle: still, at 85%.
/// - Listening: opens up, a little larger, and grows with the voice's level.
/// - Working: the agent's own motion, at most one cycle a second: Scribe's stroke writes itself,
///   Scout's ring sweeps a quarter turn and back, Operator's square turns and settles, Planner's bars
///   trade places. It runs only while work runs, and settles the moment work stops.
/// - Using a tool: the same motion, with the tool's glyph on a badge.
/// - Waiting for approval: still, with an amber badge. Needs an answer: an amber badge with a question mark.
/// - Done: the silhouette gives way to a green check that draws itself once.
/// - Paused: dimmed, with a pause badge. Failed: a red cross draws itself once.
/// - Speaking: a level line in the agent's hue follows the reply.
/// With reduced motion every pose is reached in one step, nothing loops, and the check and cross appear whole.
/// </summary>
sealed class AgentMark
{
    const float Box = 20;
    static readonly Spring Shift = new(1.0, 0.30), Draw = new(1.0, 0.36);

    readonly Agent _agent;
    readonly Compositor _c;
    readonly CompositionContainerShape _figure;
    readonly List<CompositionGeometry> _strokes = new();
    readonly List<CompositionObject> _offsets = new();
    readonly CompositionSpring _markOpacity, _markScale, _lift, _check, _checkShown, _cross, _crossShown, _level, _levelShown;
    readonly Border _badge;
    readonly FontIcon _badgeGlyph;
    readonly Part _badgePart;
    bool _looping;

    public Grid Element { get; }
    public AgentState State { get; private set; } = AgentState.Idle;
    public Agent Agent => _agent;

    public AgentMark(Agent agent, Compositor c, double size)
    {
        _agent = agent;
        _c = c;
        var hue = Colours.Of(agent.Hue);
        Element = new Grid { Width = size, Height = size };
        AutomationProperties.SetName(Element, agent.Name);

        _markOpacity = new CompositionSpring(c, 0.85);
        _markScale = new CompositionSpring(c, 1);
        _check = new CompositionSpring(c, 0);
        _checkShown = new CompositionSpring(c, 0);
        _cross = new CompositionSpring(c, 0);
        _crossShown = new CompositionSpring(c, 0);
        _level = new CompositionSpring(c, 0);
        _levelShown = new CompositionSpring(c, 0);

        var root = c.CreateContainerVisual();
        root.Size = new Vector2((float)size);

        // The silhouette.
        var mark = Layer(size);
        _markOpacity.Drive(mark, "Opacity", "Clamp(s.v, 0, 1)");
        mark.CenterPoint = new Vector3((float)size / 2, (float)size / 2, 0);
        // While speaking, the silhouette draws in and lifts, so the level line beneath it has room of its own.
        _lift = new CompositionSpring(c, 0);
        var scale = c.CreateExpressionAnimation("Vector3(s.v * (1 - 0.18 * l.v), s.v * (1 - 0.18 * l.v), 1)");
        scale.SetReferenceParameter("s", _markScale.Props);
        scale.SetReferenceParameter("l", _lift.Props);
        mark.StartAnimation("Scale", scale);
        var lift = c.CreateExpressionAnimation($"Vector3(0, -l.v * {(size * 0.1).ToString(System.Globalization.CultureInfo.InvariantCulture)}, 0)");
        lift.SetReferenceParameter("l", _lift.Props);
        mark.StartAnimation("Offset", lift);
        _figure = c.CreateContainerShape();
        var ink = c.CreateColorBrush(hue);
        BuildSilhouette(ink);
        mark.Shapes.Add(_figure);
        root.Children.InsertAtTop(mark);

        // The check (done) and the cross (failed), drawn by trimming their strokes.
        var check = Layer(size);
        _checkShown.Drive(check, "Opacity", "Clamp(s.v, 0, 1)");
        var checkPath = c.CreatePathGeometry(Path(b => { b.BeginFigure(5.4f, 10.4f); b.AddLine(8.7f, 13.7f); b.AddLine(14.7f, 6.6f); b.EndFigure(CanvasFigureLoop.Open); }));
        _check.Drive(checkPath, "TrimEnd", "Clamp(s.v, 0, 1)");
        check.Shapes.Add(Stroke(checkPath, c.CreateColorBrush(Palette.Done), 2.6f));
        root.Children.InsertAtTop(check);

        var cross = Layer(size);
        _crossShown.Drive(cross, "Opacity", "Clamp(s.v, 0, 1)");
        var a = c.CreateLineGeometry(); a.Start = new Vector2(6.2f, 6.2f); a.End = new Vector2(13.8f, 13.8f);
        var b2 = c.CreateLineGeometry(); b2.Start = new Vector2(13.8f, 6.2f); b2.End = new Vector2(6.2f, 13.8f);
        _cross.Drive(a, "TrimEnd", "Clamp(s.v * 2, 0, 1)");
        _cross.Drive(b2, "TrimEnd", "Clamp(s.v * 2 - 1, 0, 1)");
        var red = c.CreateColorBrush(Palette.Error);
        cross.Shapes.Add(Stroke(a, red, 2.6f));
        cross.Shapes.Add(Stroke(b2, red, 2.6f));
        root.Children.InsertAtTop(cross);

        // Speaking: a level line under the silhouette that follows the reply.
        var level = Layer(size);
        _levelShown.Drive(level, "Opacity", "Clamp(s.v, 0, 1)");
        var line = c.CreateLineGeometry();
        var levelLine = c.CreateExpressionAnimation("Vector2(10 - (2 + 6 * Clamp(s.v, 0, 1)), 19)");
        levelLine.SetReferenceParameter("s", _level.Props);
        line.StartAnimation("Start", levelLine);
        var levelEnd = c.CreateExpressionAnimation("Vector2(10 + (2 + 6 * Clamp(s.v, 0, 1)), 19)");
        levelEnd.SetReferenceParameter("s", _level.Props);
        line.StartAnimation("End", levelEnd);
        level.Shapes.Add(Stroke(line, ink, 1.8f));
        root.Children.InsertAtTop(level);

        // In its own host: an element's composition child draws above its XAML children, and the badge must sit on top.
        var host = new Grid { Width = size, Height = size };
        ElementCompositionPreview.SetElementChildVisual(host, root);
        Element.Children.Add(host);

        // The badge: a small disc on the lower right, ringed in the body's own colour so it sits apart from the silhouette.
        double badge = Math.Round(size * 0.5);
        _badgeGlyph = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = badge * 0.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _badge = new Border
        {
            Width = badge, Height = badge, CornerRadius = new CornerRadius(badge / 2), Child = _badgeGlyph,
            BorderThickness = new Thickness(1.5), BorderBrush = new SolidColorBrush(Palette.Fill),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -badge * 0.3, -badge * 0.3), IsHitTestVisible = false,
        };
        _badge.CenterPoint = new Vector3((float)badge / 2, (float)badge / 2, 0);
        Element.Children.Add(_badge);
        _badgePart = new Part(_badge, c);
    }

    ShapeVisual Layer(double size)
    {
        var layer = _c.CreateShapeVisual();
        layer.Size = new Vector2((float)size);
        layer.ViewBox = _c.CreateViewBox();
        layer.ViewBox.Size = new Vector2(Box);
        return layer;
    }

    CompositionSpriteShape Stroke(CompositionGeometry geometry, CompositionBrush brush, float width)
    {
        var shape = _c.CreateSpriteShape(geometry);
        shape.StrokeBrush = brush;
        shape.StrokeThickness = width;
        shape.StrokeStartCap = shape.StrokeEndCap = CompositionStrokeCap.Round;
        shape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        return shape;
    }

    static CompositionPath Path(Action<CanvasPathBuilder> draw)
    {
        using var builder = new CanvasPathBuilder(CanvasDevice.GetSharedDevice());
        draw(builder);
        return new CompositionPath(CanvasGeometry.CreatePath(builder));
    }

    /// <summary>Each silhouette, from ROADMAP 2.3, centred on its visual weight in the 20 × 20 box.</summary>
    void BuildSilhouette(CompositionBrush ink)
    {
        switch (_agent.Silhouette)
        {
            case Silhouette.Stroke:
            {
                // A short stroke with a rounded nib, and the dot of ink it left.
                var stroke = _c.CreateLineGeometry();
                stroke.Start = new Vector2(5.6f, 14.6f);
                stroke.End = new Vector2(15.2f, 5.0f);
                _strokes.Add(stroke);
                _figure.Shapes.Add(Stroke(stroke, ink, 3.4f));
                var dot = _c.CreateEllipseGeometry();
                dot.Center = new Vector2(4.2f, 17.0f);
                dot.Radius = new Vector2(1.45f);
                var drop = _c.CreateSpriteShape(dot);
                drop.FillBrush = ink;
                _figure.Shapes.Add(drop);
                break;
            }
            case Silhouette.Ring:
            {
                // A ring with a small handle; it turns about the ring's centre.
                var ring = _c.CreateEllipseGeometry();
                ring.Center = new Vector2(8.7f, 8.7f);
                ring.Radius = new Vector2(5.0f);
                _figure.Shapes.Add(Stroke(ring, ink, 2.6f));
                var handle = _c.CreateLineGeometry();
                handle.Start = new Vector2(12.6f, 12.6f);
                handle.End = new Vector2(16.0f, 16.0f);
                _figure.Shapes.Add(Stroke(handle, ink, 3.0f));
                _figure.CenterPoint = new Vector2(8.7f, 8.7f);
                break;
            }
            case Silhouette.Square:
            {
                var square = _c.CreateRoundedRectangleGeometry();
                square.Size = new Vector2(11.4f);
                square.Offset = new Vector2(4.3f);
                square.CornerRadius = new Vector2(3.2f);
                var body = _c.CreateSpriteShape(square);
                body.FillBrush = ink;
                _figure.Shapes.Add(body);
                _figure.CenterPoint = new Vector2(10, 10);
                break;
            }
            case Silhouette.Bars:
            {
                foreach (var (width, y) in new[] { (12.0f, 5.6f), (8.6f, 10.8f) })
                {
                    var bar = _c.CreateRoundedRectangleGeometry();
                    bar.Size = new Vector2(width, 3.6f);
                    bar.CornerRadius = new Vector2(1.8f);
                    var shape = _c.CreateSpriteShape(bar);
                    shape.FillBrush = ink;
                    shape.Offset = new Vector2(4.0f, y);
                    _offsets.Add(shape);
                    _figure.Shapes.Add(shape);
                }
                break;
            }
        }
    }

    Panel? _holder;

    /// <summary>
    /// Moves the mark into <paramref name="panel"/>, out of wherever it was. The old container is
    /// remembered rather than asked for: once a panel's content is cleared, WinUI no longer reports
    /// the element's parent, but the element still belongs to it.
    /// </summary>
    public FrameworkElement PlaceIn(Panel panel)
    {
        _holder?.Children.Remove(Element);
        if (Element.Parent is Panel other && other != panel) other.Children.Remove(Element);
        panel.Children.Add(Element);
        _holder = panel;
        return Element;
    }

    /// <summary>Moves to the pose for <paramref name="state"/>. <paramref name="toolGlyph"/> is the tool's Segoe Fluent glyph while using one.</summary>
    public void Show(AgentState state, string? toolGlyph = null)
    {
        var was = State;
        State = state;
        bool silhouette = state is not (AgentState.Done or AgentState.Failed);
        _markOpacity.To(state switch { AgentState.Idle => 0.85, AgentState.Paused => 0.4, AgentState.Done or AgentState.Failed => 0, _ => 1 }, Shift, isFade: true);
        _markScale.To(state switch { AgentState.Listening => 1.08 + 0.14 * _level.Target, AgentState.Done or AgentState.Failed => 0.7, _ => 1 }, Shift);

        // Done and failed draw once, from nothing, each time they arrive.
        if (state == AgentState.Done && was != AgentState.Done) { _check.Set(0); _checkShown.Set(1); _check.To(1, Draw); }
        else if (state != AgentState.Done) _checkShown.To(0, Shift, isFade: true);
        if (state == AgentState.Failed && was != AgentState.Failed) { _cross.Set(0); _crossShown.Set(1); _cross.To(1, Draw); }
        else if (state != AgentState.Failed) _crossShown.To(0, Shift, isFade: true);
        if (MotionPolicy.Reduced) { if (state == AgentState.Done) _check.Set(1); if (state == AgentState.Failed) _cross.Set(1); }

        _levelShown.To(state == AgentState.Speaking ? 1 : 0, Shift, isFade: true);
        _lift.To(state == AgentState.Speaking ? 1 : 0, Shift);
        Loop(state is AgentState.Working or AgentState.UsingTool && silhouette);
        Badge(state, toolGlyph);
    }

    /// <summary>The live level (0 to 1) while listening or speaking.</summary>
    public void SetLevel(double level)
    {
        level = Math.Clamp(level, 0, 1);
        _level.To(level, Springs.Quick);
        if (State == AgentState.Listening) _markScale.To(1.08 + 0.14 * level, Springs.Quick);
    }

    void Badge(AgentState state, string? toolGlyph)
    {
        (Color plate, Color ink, string glyph)? look = state switch
        {
            AgentState.UsingTool => (Palette.Primary, Palette.OnPrimary, toolGlyph ?? ""),
            AgentState.WaitingForApproval => (Palette.NeedsYou, Color.FromArgb(255, 20, 20, 20), ""),
            AgentState.NeedsInput => (Palette.NeedsYou, Color.FromArgb(255, 20, 20, 20), ""),
            AgentState.Paused => (Color.FromArgb(255, 150, 150, 155), Color.FromArgb(255, 20, 20, 20), ""),
            _ => null,
        };
        if (look is not { } l)
        {
            if (_badgePart.Shown) _badgePart.Leave(toScale: 0.5);
            return;
        }
        _badge.Background = new SolidColorBrush(l.plate);
        _badgeGlyph.Foreground = new SolidColorBrush(l.ink);
        _badgeGlyph.Glyph = l.glyph;
        if (!_badgePart.Shown) _badgePart.Enter(fromScale: 0.5, spring: Feel.Pop);
    }

    /// <summary>
    /// The working motion. It runs only while work runs (at most one cycle a second) and, when work
    /// stops, settles to rest from wherever it is instead of jumping there.
    /// </summary>
    void Loop(bool on)
    {
        on &= !MotionPolicy.Reduced;
        if (on == _looping) return;
        _looping = on;
        var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.2f, 1f));
        switch (_agent.Silhouette)
        {
            case Silhouette.Stroke:
                // Writes itself, then lifts off from where it began.
                foreach (var geometry in _strokes)
                {
                    if (on)
                    {
                        geometry.StartAnimation("TrimEnd", Cycle(1.3, (0, 0), (0.5, 1), (1, 1)));
                        geometry.StartAnimation("TrimStart", Cycle(1.3, (0, 0), (0.6, 0), (0.95, 1), (1, 1)));
                    }
                    else
                    {
                        geometry.StartAnimation("TrimEnd", Settle(1));
                        geometry.StartAnimation("TrimStart", Settle(0));
                    }
                }
                break;
            case Silhouette.Ring:
                _figure.StartAnimation("RotationAngleInDegrees", on ? Cycle(1.4, (0, 0), (0.45, 90), (0.9, 0), (1, 0)) : Settle(0));
                break;
            case Silhouette.Square:
                // A quarter turn with a little overshoot, then a rest; a square turned 90° is the same square.
                if (on) _figure.StartAnimation("RotationAngleInDegrees", Cycle(1.2, (0, 0), (0.42, 90), (1, 90), overshoot: true));
                else _figure.StartAnimation("RotationAngleInDegrees", Settle(0, fromMod90: true));
                break;
            case Silhouette.Bars:
                for (int i = 0; i < _offsets.Count; i++)
                {
                    float mine = i == 0 ? 5.6f : 10.8f, other = i == 0 ? 10.8f : 5.6f;
                    var move = _c.CreateVector2KeyFrameAnimation();
                    if (on)
                    {
                        move.InsertKeyFrame(0f, new Vector2(4, mine));
                        move.InsertKeyFrame(0.45f, new Vector2(4, other), ease);
                        move.InsertKeyFrame(0.9f, new Vector2(4, mine), ease);
                        move.InsertKeyFrame(1f, new Vector2(4, mine));
                        move.Duration = TimeSpan.FromSeconds(1.6);
                        move.IterationBehavior = AnimationIterationBehavior.Forever;
                    }
                    else
                    {
                        move.InsertExpressionKeyFrame(0f, "this.StartingValue");
                        move.InsertKeyFrame(1f, new Vector2(4, mine), ease);
                        move.Duration = TimeSpan.FromMilliseconds(260);
                    }
                    _offsets[i].StartAnimation("Offset", move);
                }
                break;
        }
    }

    ScalarKeyFrameAnimation Cycle(double seconds, params (double At, double Value)[] frames) => Cycle(seconds, false, frames);

    ScalarKeyFrameAnimation Cycle(double seconds, (double At, double Value) a, (double At, double Value) b, (double At, double Value) c, bool overshoot) => Cycle(seconds, overshoot, a, b, c);

    ScalarKeyFrameAnimation Cycle(double seconds, bool overshoot, params (double At, double Value)[] frames)
    {
        var animation = _c.CreateScalarKeyFrameAnimation();
        var ease = overshoot
            ? _c.CreateCubicBezierEasingFunction(new Vector2(0.34f, 0f), new Vector2(0.25f, 1.25f))   // passes the quarter turn a touch and comes back
            : _c.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.25f, 1f));
        foreach (var (at, value) in frames) animation.InsertKeyFrame((float)at, (float)value, ease);
        animation.Duration = TimeSpan.FromSeconds(seconds);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        return animation;
    }

    ScalarKeyFrameAnimation Settle(double rest, bool fromMod90 = false)
    {
        var animation = _c.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0f, "this.StartingValue");
        // A square part-way through a turn completes it (it looks the same at 90° as at 0°).
        if (fromMod90) animation.InsertExpressionKeyFrame(1f, "this.StartingValue > 1 ? 90 : 0", _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0.2f, 1f)));
        else animation.InsertKeyFrame(1f, (float)rest, _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0.2f, 1f)));
        animation.Duration = TimeSpan.FromMilliseconds(260);
        return animation;
    }

    /// <summary>For the layout check: puts the mark in a state at a level, with every spring at its target (the working motion still runs).</summary>
    public void Pose(AgentState state, double level, string? toolGlyph = null)
    {
        Show(state, toolGlyph);
        _level.Set(level);
        foreach (var spring in new[] { _markOpacity, _markScale, _lift, _check, _checkShown, _cross, _crossShown, _levelShown })
            spring.Set(spring.Target);
        if (state == AgentState.Listening) _markScale.Set(1.08 + 0.14 * level);
    }
}

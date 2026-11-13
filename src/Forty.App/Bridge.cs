using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;

namespace Forty.App;

/// <summary>
/// The liquid bridge between the capsule and the satellite (F5, MOTION.md 5): as the satellite leaves
/// the capsule's right end, the two stay joined by a neck of the body's own fill that thins and snaps,
/// like a drop pulling away. When it returns, the same neck forms again and the drop is absorbed.
///
/// The neck is the classic metaball join of two circles: the capsule's rounded end and the satellite.
/// Its outline is precomputed for 25 points of the satellite's journey and handed to the compositor as
/// one path animation, whose progress is bound to the satellite's own spring. So the neck is wherever
/// the satellite is, on every frame, including mid-interruption, with nothing computed per frame.
/// </summary>
static class Bridge
{
    public const int Steps = 24;

    /// <summary>
    /// The neck when the satellite has travelled <paramref name="split"/> of its way out (0: inside the
    /// capsule's end; 1: at rest beside it). Coordinates are relative to the capsule's right edge, on its
    /// centre line. Every path has the same commands, so the compositor can morph between them.
    /// </summary>
    /// <param name="a">The radius of the capsule's rounded end (half its height).</param>
    /// <param name="b">The satellite's radius.</param>
    /// <param name="gap">The gap between them at rest.</param>
    public static CompositionPath Path(ICanvasResourceCreator device, double a, double b, double gap, double split)
    {
        var o = Forty.Motion.LiquidBridge.At(a, b, gap, split);
        using var builder = new CanvasPathBuilder(device);
        builder.BeginFigure(o.P1);
        builder.AddCubicBezier(o.H1, o.H3, o.P3);
        builder.AddLine(o.P4);
        builder.AddCubicBezier(o.H4, o.H2, o.P2);
        builder.EndFigure(CanvasFigureLoop.Closed);
        return new CompositionPath(CanvasGeometry.CreatePath(builder));
    }

    /// <summary>Builds the morph for one capsule height and binds its progress to the satellite's journey.</summary>
    public static void Bind(CompositionPathGeometry geometry, double a, double b, double gap, CompositionPropertySet split)
    {
        var c = geometry.Compositor;
        var device = CanvasDevice.GetSharedDevice();
        var animation = c.CreatePathKeyFrameAnimation();
        var linear = c.CreateLinearEasingFunction();
        for (int i = 0; i <= Steps; i++) animation.InsertKeyFrame(i / (float)Steps, Path(device, a, b, gap, i / (double)Steps), linear);
        animation.Duration = TimeSpan.FromSeconds(1);
        geometry.StartAnimation("Path", animation);
        var controller = geometry.TryGetAnimationController("Path");
        controller.Pause();
        var progress = c.CreateExpressionAnimation("Clamp(split.v, 0, 1)");
        progress.SetReferenceParameter("split", split);
        controller.StartAnimation("Progress", progress);
    }
}

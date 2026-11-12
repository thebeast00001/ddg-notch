using System.Numerics;
using IslandSpike.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace IslandSpike;

/// <summary>A resting or expanded form of the island, in device-independent pixels.</summary>
public readonly record struct Shape(string Name, double W, double H, double R, double Top)
{
    public static readonly Shape Sliver = new("sliver", 56, 4, 2, 0);
    public static readonly Shape Rest = new("rest", 148, 32, 16, 6);
    public static readonly Shape Peek = new("peek", 154, 33.3, 16.6, 6);
    public static readonly Shape Capsule = new("capsule", 260, 36, 18, 6);
    public static readonly Shape Listen = new("listen", 168, 36, 18, 6);
    public static readonly Shape Ask = new("ask", 348, 40, 20, 6);
    public static readonly Shape Recording = new("recording", 224, 36, 18, 6);
    public static readonly Shape Ready = new("ready", 292, 36, 18, 6);
    public static readonly Shape Panel = new("panel", 420, 200, 32, 6);
    public static readonly Shape Full = new("full", 480, 560, 36, 6);
}

/// <summary>
/// The island's body: one rounded rectangle whose width, height, corner radius and top offset are
/// each a <see cref="MotionValue"/>. It hangs from the top centre and is never destroyed or rebuilt.
/// </summary>
sealed class Island
{
    public const double WindowWidth = 520, WindowHeight = 640;
    const double RestPad = 10;         // room around the body for the pointer's hit area
    const double MotionPadBottom = 48; // extra room below while a spring may overshoot

    public IslandWindow Win { get; }
    public Compositor Compositor { get; }
    public MotionValue W { get; }
    public MotionValue H { get; }
    public MotionValue R { get; }
    public MotionValue Top { get; }
    public Shape Current { get; private set; } = Shape.Rest;

    int _regionVersion;

    public Island(IslandWindow win, Func<Compositor, double, MotionValue> makeValue, Windows.UI.Color fill, bool hairline)
    {
        Win = win;
        Compositor = ElementCompositionPreview.GetElementVisual(win.Root).Compositor;

        W = makeValue(Compositor, Shape.Rest.W);
        H = makeValue(Compositor, Shape.Rest.H);
        R = makeValue(Compositor, Shape.Rest.R);
        Top = makeValue(Compositor, Shape.Rest.Top);

        var layout = Compositor.CreatePropertySet();
        layout.InsertScalar("hostW", (float)win.WidthDip);
        layout.InsertScalar("inset", (float)win.TopInsetDip);   // the window starts slightly above the monitor

        var body = Compositor.CreateRoundedRectangleGeometry();

        var size = Compositor.CreateExpressionAnimation("Vector2(Max(w.v, 1), Max(h.v, 1))");
        size.SetReferenceParameter("w", W.Props);
        size.SetReferenceParameter("h", H.Props);
        body.StartAnimation("Size", size);

        // A corner can never be rounder than half the shorter side.
        var radius = Compositor.CreateExpressionAnimation(
            "Vector2(Min(r.v, Min(w.v, h.v) * 0.5), Min(r.v, Min(w.v, h.v) * 0.5))");
        radius.SetReferenceParameter("r", R.Props);
        radius.SetReferenceParameter("w", W.Props);
        radius.SetReferenceParameter("h", H.Props);
        body.StartAnimation("CornerRadius", radius);

        // Anchored at the top centre: the body grows outward and downward from there.
        var offset = Compositor.CreateExpressionAnimation("Vector2((layout.hostW - w.v) * 0.5, top.v + layout.inset)");
        offset.SetReferenceParameter("layout", layout);
        offset.SetReferenceParameter("w", W.Props);
        offset.SetReferenceParameter("top", Top.Props);
        body.StartAnimation("Offset", offset);

        var sprite = Compositor.CreateSpriteShape(body);
        sprite.FillBrush = Compositor.CreateColorBrush(fill);
        if (hairline)
        {
            sprite.StrokeBrush = Compositor.CreateColorBrush(Windows.UI.Color.FromArgb(38, 255, 255, 255));
            sprite.StrokeThickness = 1f;
        }

        var visual = Compositor.CreateShapeVisual();
        visual.Size = new Vector2((float)win.WidthDip, (float)win.HeightDip);
        visual.Shapes.Add(sprite);

        // Whatever sits inside the body hangs from this container, whose origin is always the
        // body's centre. Content therefore travels with the body and never has to be re-laid out.
        Content = Compositor.CreateContainerVisual();
        var centre = Compositor.CreateExpressionAnimation("Vector3(layout.hostW * 0.5, top.v + layout.inset + h.v * 0.5, 0)");
        centre.SetReferenceParameter("layout", layout);
        centre.SetReferenceParameter("top", Top.Props);
        centre.SetReferenceParameter("h", H.Props);
        Content.StartAnimation("Offset", centre);

        var root = Compositor.CreateContainerVisual();
        root.Size = visual.Size;
        root.Children.InsertAtBottom(visual);
        root.Children.InsertAtTop(Content);
        ElementCompositionPreview.SetElementChildVisual(win.BodyHost, root);
    }

    /// <summary>Container for content inside the body. Its origin is the body's centre.</summary>
    public ContainerVisual Content { get; }

    /// <summary>Where the body is on screen right now, from the motion mirrors.</summary>
    public (double W, double H, double Top) BoundsNow(double now) =>
        (W.Sample(now).X, H.Sample(now).X, Top.Sample(now).X);

    /// <summary>
    /// Morph to a shape. The window region first grows to cover both where the body is and where it
    /// is going, and shrinks to the destination only after the springs have settled.
    /// </summary>
    public void GoTo(Shape shape, Spring spring, double now, bool manageRegion = true, double? heightVelocity = null)
    {
        if (manageRegion)
        {
            var (w, h, top) = BoundsNow(now);
            Win.SetRegionDip(Math.Max(w, shape.W) + 2 * RestPad,
                             Math.Max(top + h, shape.Top + shape.H) + MotionPadBottom);
            int version = ++_regionVersion;
            _ = ShrinkRegionLater(shape, version);
        }

        int commit = CommitTimes.Count;
        CommitTimes.Add(now);
        W.Retarget(shape.W, spring, now, commit);
        H.Retarget(shape.H, spring, now, commit, heightVelocity);
        R.Retarget(shape.R, spring, now, commit);
        Top.Retarget(shape.Top, spring, now, commit);
        Current = shape;
    }

    /// <summary>When each morph was asked for. One entry per <see cref="GoTo"/>; the index is the commit id.</summary>
    public List<double> CommitTimes { get; } = new();

    async Task ShrinkRegionLater(Shape shape, int version)
    {
        await Task.Delay(750);
        if (version != _regionVersion) return;   // another morph started meanwhile
        Win.SetRegionDip(shape.W + 2 * RestPad, shape.Top + shape.H + RestPad);
    }

    public void SnapRegionTo(Shape shape) => Win.SetRegionDip(shape.W + 2 * RestPad, shape.Top + shape.H + RestPad);
}

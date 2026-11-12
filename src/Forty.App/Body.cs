using System.Numerics;
using Forty.Core;
using Forty.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Forty.App;

/// <summary>
/// The island's body: one black rounded rectangle hanging from the top centre whose width, height,
/// corner radius and top offset are springs, plus the satellite circle beside it and the dot at its
/// right end. It is never destroyed or rebuilt; every state is this shape changing size (MOTION.md 2).
/// </summary>
sealed class Body
{
    const double RegionPad = 10;          // room around the body for the pointer
    const double OvershootPad = 48;       // extra room below while a spring could overshoot
    public const double SatelliteGap = CapsuleLayout.SatelliteGap, SatelliteSize = CapsuleLayout.SatelliteSize;

    readonly IslandWindow _win;
    readonly CompositionPropertySet _layout;
    readonly CompositionColorBrush _dotBrush, _fill, _hairline;
    Microsoft.UI.Xaml.Controls.SystemBackdropElement? _glass;
    bool _glassOn;
    int _regionVersion;

    public CompositionSpring W { get; }
    public CompositionSpring H { get; }
    public CompositionSpring R { get; }
    public CompositionSpring Top { get; }
    public CompositionSpring Satellite { get; }
    public CompositionSpring SatelliteOpacity { get; }
    public CompositionSpring Dot { get; }

    public BodyShape Shape { get; private set; } = BodyShape.Rest;
    public bool SatelliteShown => Satellite.Target > 0;

    public Body(IslandWindow win)
    {
        _win = win;
        var c = win.Compositor;
        W = new CompositionSpring(c, BodyShape.Rest.Width);
        H = new CompositionSpring(c, BodyShape.Rest.Height);
        R = new CompositionSpring(c, BodyShape.Rest.Radius);
        Top = new CompositionSpring(c, BodyShape.Rest.Top);
        Satellite = new CompositionSpring(c, 0);
        SatelliteOpacity = new CompositionSpring(c, 0);
        Dot = new CompositionSpring(c, 0);

        _layout = c.CreatePropertySet();
        _layout.InsertScalar("hostW", (float)win.WidthDip);
        _layout.InsertScalar("inset", (float)win.TopInsetDip);   // the window starts slightly above the monitor
        _layout.InsertScalar("gap", (float)SatelliteGap);

        var fill = _fill = c.CreateColorBrush(Palette.Fill);
        var hairline = _hairline = c.CreateColorBrush(Palette.Hairline);   // 10% white separates it from dark title bars

        // The body.
        var body = c.CreateRoundedRectangleGeometry();
        Bind(body, "Size", "Vector2(Max(w.v, 1), Max(h.v, 1))");
        Bind(body, "CornerRadius", "Vector2(Min(r.v, Min(w.v, h.v) * 0.5), Min(r.v, Min(w.v, h.v) * 0.5))");   // never rounder than a pill
        Bind(body, "Offset", "Vector2((layout.hostW - w.v) * 0.5, top.v + layout.inset)");
        var bodySprite = c.CreateSpriteShape(body);
        bodySprite.FillBrush = fill;
        var bodyEdge = c.CreateSpriteShape(body);   // the hairline is drawn above every layer, so the edge stays crisp
        bodyEdge.StrokeBrush = hairline;
        bodyEdge.StrokeThickness = 1f;

        // The satellite grows out of the gap beside the capsule's right edge.
        var satellite = c.CreateEllipseGeometry();
        Bind(satellite, "Center", "Vector2(layout.hostW * 0.5 + w.v * 0.5 + layout.gap + sat.v * 0.5, top.v + layout.inset + h.v * 0.5)");
        Bind(satellite, "Radius", "Vector2(Max(sat.v * 0.5, 0.01), Max(sat.v * 0.5, 0.01))");
        var satelliteSprite = c.CreateSpriteShape(satellite);
        satelliteSprite.FillBrush = fill;
        var satelliteEdge = c.CreateSpriteShape(satellite);
        satelliteEdge.StrokeBrush = hairline;
        satelliteEdge.StrokeThickness = 1f;

        var shapes = c.CreateShapeVisual();
        shapes.Size = new Vector2((float)win.WidthDip, (float)win.HeightDip);
        shapes.Shapes.Add(bodySprite);
        shapes.Shapes.Add(satelliteSprite);
        var edges = c.CreateShapeVisual();
        edges.Size = shapes.Size;
        edges.Shapes.Add(bodyEdge);
        edges.Shapes.Add(satelliteEdge);
        Layers = c.CreateContainerVisual();
        Layers.Size = shapes.Size;

        // The dot: a 4 DIP mark at the body's right end, for things a mode allows only as a dot (F132–F134).
        var dot = c.CreateEllipseGeometry();
        dot.Radius = new Vector2(2, 2);
        Bind(dot, "Center", "Vector2(layout.hostW * 0.5 + w.v * 0.5 - Min(h.v * 0.5, 10), top.v + layout.inset + h.v * 0.5)");
        _dotBrush = c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        var dotSprite = c.CreateSpriteShape(dot);
        dotSprite.FillBrush = _dotBrush;
        var dotVisual = c.CreateShapeVisual();
        dotVisual.Size = shapes.Size;
        dotVisual.Shapes.Add(dotSprite);
        Dot.Drive(dotVisual, "Opacity", "Clamp(s.v, 0, 1)");

        // Bottom to top: the tinted fill, the layers (the fluid backdrop), the hairline edge, the dot.
        var root = c.CreateContainerVisual();
        root.Size = shapes.Size;
        root.Children.InsertAtTop(shapes);
        root.Children.InsertAtTop(Layers);
        root.Children.InsertAtTop(edges);
        root.Children.InsertAtTop(dotVisual);
        ElementCompositionPreview.SetElementChildVisual(win.BodyHost, root);
        SnapRegion();
    }

    /// <summary>Visuals placed here sit above the body's fill and below its hairline edge.</summary>
    public ContainerVisual Layers { get; }

    /// <summary>A clip in the body's exact shape, for layers that must not spill outside it.</summary>
    public CompositionGeometricClip CreateClip()
    {
        var geometry = _win.Compositor.CreateRoundedRectangleGeometry();
        Bind(geometry, "Size", "Vector2(Max(w.v, 1), Max(h.v, 1))");
        Bind(geometry, "CornerRadius", "Vector2(Min(r.v, Min(w.v, h.v) * 0.5), Min(r.v, Min(w.v, h.v) * 0.5))");
        Bind(geometry, "Offset", "Vector2((layout.hostW - w.v) * 0.5, top.v + layout.inset)");
        return _win.Compositor.CreateGeometricClip(geometry);
    }

    /// <summary>
    /// Tints the body (a song's colour) or returns it to Ink. The colour crosses over in 0.7 s, so a new
    /// song's mood arrives rather than switches.
    /// </summary>
    public void SetTint(Windows.UI.Color? tint)
    {
        _tint = tint;
        if (_glassOn) return;
        var target = tint ?? Palette.Fill;
        if (MotionPolicy.Reduced) { _fill.Color = target; return; }
        var animation = _win.Compositor.CreateColorKeyFrameAnimation();
        animation.InsertKeyFrame(1f, target);
        animation.Duration = TimeSpan.FromSeconds(Feel.ColourSeconds);
        _fill.StartAnimation("Color", animation);
    }

    Windows.UI.Color? _tint;

    /// <summary>Binds a composition property to the body's springs; "w", "h", "r", "top", "sat" and "layout" are available.</summary>
    public void Bind(CompositionObject target, string property, string expression, params (string Name, CompositionObject Value)[] extra)
    {
        var animation = target.Compositor.CreateExpressionAnimation(expression);
        foreach (var (name, value) in extra) animation.SetReferenceParameter(name, value);
        animation.SetReferenceParameter("w", W.Props);
        animation.SetReferenceParameter("h", H.Props);
        animation.SetReferenceParameter("r", R.Props);
        animation.SetReferenceParameter("top", Top.Props);
        animation.SetReferenceParameter("sat", Satellite.Props);
        animation.SetReferenceParameter("layout", _layout);
        target.StartAnimation(property, animation);
    }

    /// <summary>
    /// Clips an overlay element (laid out over the whole window at the origin) to the body, so content
    /// never draws outside the shape while it morphs.
    /// </summary>
    public void ClipToBody(UIElement element)
    {
        var c = _win.Compositor;
        var geometry = c.CreateRoundedRectangleGeometry();
        Bind(geometry, "Size", "Vector2(Max(w.v, 1), Max(h.v, 1))");
        Bind(geometry, "CornerRadius", "Vector2(Min(r.v, Min(w.v, h.v) * 0.5), Min(r.v, Min(w.v, h.v) * 0.5))");
        Bind(geometry, "Offset", "Vector2((layout.hostW - w.v) * 0.5, top.v + layout.inset)");
        ElementCompositionPreview.GetElementVisual(element).Clip = c.CreateGeometricClip(geometry);
    }

    /// <summary>
    /// Spike S6, the Glass look (not working yet; FORTY_GLASS=1 turns it on for testing): Windows' acrylic, inside the body shape only. An acrylic backdrop
    /// element covers the window behind the body, is clipped to the body's spring-driven shape, and the
    /// black fill becomes a translucent tint over it. Falls back to Ink (and returns false) when Windows'
    /// transparency effects are off or battery saver is on (MOTION.md 8).
    /// </summary>
    public bool SetGlass(bool on)
    {
        bool allowed = on && new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled &&
                       Windows.System.Power.PowerManager.EnergySaverStatus != Windows.System.Power.EnergySaverStatus.On;
        if (allowed && _glass == null)
        {
            _glass = new Microsoft.UI.Xaml.Controls.SystemBackdropElement
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(),
                Width = _win.WidthDip, Height = _win.HeightDip,
            };
            _win.BodyHost.Children.Insert(0, _glass);   // under the body's compositor shapes, which draw the tint and hairline
            ClipToBody(_glass);
            // Spike S6 result (12 Nov 2026): this draws no blur. Acrylic follows the window's activation,
            // and the island is never activated, so the element shows nothing (with the window's clear
            // backdrop) or a solid fallback (without it). Off by default; see ROADMAP.md phase 1 progress.
        }
        if (_glass != null) _glass.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        _glassOn = allowed;
        RefreshColours();
        return allowed;
    }

    /// <summary>Re-reads the palette after a contrast-theme change (F11).</summary>
    public void SetContrast(bool contrast)
    {
        Palette.SetContrast(contrast);
        RefreshColours();
    }

    void RefreshColours()
    {
        _fill.StopAnimation("Color");
        _fill.Color = _glassOn ? Windows.UI.Color.FromArgb(120, 0, 0, 0) : Palette.Contrast ? Palette.Fill : _tint ?? Palette.Fill;
        _hairline.Color = Palette.Hairline;
    }

    /// <summary>
    /// Drives an element's Translation so that its top-left corner sits where the satellite's top-left
    /// is (the element should be <see cref="SatelliteSize"/> square, laid out at the window's origin).
    /// </summary>
    public void PinToSatellite(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var animation = _win.Compositor.CreateExpressionAnimation(
            "Vector3(layout.hostW * 0.5 + w.v * 0.5 + layout.gap, top.v + layout.inset + h.v * 0.5 - 18, 0)");
        animation.Target = "Translation";
        animation.SetReferenceParameter("w", W.Props);
        animation.SetReferenceParameter("h", H.Props);
        animation.SetReferenceParameter("top", Top.Props);
        animation.SetReferenceParameter("layout", _layout);
        element.StartAnimation(animation);
    }

    /// <summary>
    /// Morphs to a shape. The window region first grows to cover where the body is and where it is
    /// going, and shrinks to the destination only after the springs have settled.
    /// </summary>
    public void GoTo(BodyShape shape, Spring spring)
    {
        // Asked for the shape it already has and is resting in: nothing to do, not even the region.
        double now = Clock.Now;
        if (shape == Shape && W.Model.IsSettled(now) && H.Model.IsSettled(now) && Top.Model.IsSettled(now)) return;
        Shape = shape;
        GrowRegion(shape);
        W.To(shape.Width, spring);
        H.To(shape.Height, spring);
        R.To(shape.Radius, spring);
        Top.To(shape.Top, spring);
    }

    /// <summary>Shows or hides the satellite circle (F5).</summary>
    public void ShowSatellite(bool show)
    {
        if (show == SatelliteShown) return;
        // Targets first, so the region below already makes room for a satellite that is arriving.
        Satellite.To(show ? SatelliteSize : 0, show ? Springs.Move : Springs.Quick);
        SatelliteOpacity.To(show ? 1 : 0, show ? Springs.Move : Springs.Quick, isFade: true);
        GrowRegion(Shape);
    }

    /// <summary>Shows the dot in the colour of what it stands for, or hides it.</summary>
    public void ShowDot(Accent? accent)
    {
        if (accent is { } a) _dotBrush.Color = Palette.Colour(a, Windows.UI.Color.FromArgb(200, 255, 255, 255));
        Dot.To(accent != null ? 1 : 0, Springs.Quick, isFade: true);
    }

    void GrowRegion(BodyShape destination)
    {
        double now = Clock.Now;
        double w = Math.Max(W.Model.Sample(now).X, destination.Width);
        double bottom = Math.Max(Top.Model.Sample(now).X + H.Model.Sample(now).X, destination.Top + destination.Height);
        double satellite = SatelliteShown || Satellite.Model.Sample(now).X > 0.5 ? 2 * (SatelliteGap + SatelliteSize) : 0;
        _win.SetRegionDip(w + satellite + 2 * RegionPad, bottom + OvershootPad);
        int version = ++_regionVersion;
        _ = ShrinkRegionLater(version);
    }

    async Task ShrinkRegionLater(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(Springs.Move.SettleSeconds() + 0.35));
        if (version == _regionVersion) SnapRegion();   // otherwise another morph started meanwhile
    }

    void SnapRegion()
    {
        double satellite = SatelliteShown ? 2 * (SatelliteGap + SatelliteSize) : 0;
        _win.SetRegionDip(Shape.Width + satellite + 2 * RegionPad, Shape.Top + Shape.Height + RegionPad);
    }
}

/// <summary>
/// The colour roles of MOTION.md section 8. In a Windows contrast theme every role becomes a system
/// colour (F11): the body takes the window colour, text and the hairline take the window-text colour,
/// and the state accents take the highlight colour, so nothing depends on our own palette.
/// </summary>
static class Palette
{
    static readonly Windows.UI.ViewManagement.UISettings System = new();
    public static bool Contrast { get; private set; }
    public static void SetContrast(bool on) => Contrast = on;

    static Windows.UI.Color Sys(Windows.UI.ViewManagement.UIElementType type) => System.UIElementColor(type);
    static Windows.UI.Color Ink(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

    public static Windows.UI.Color Done => Contrast ? Sys(Windows.UI.ViewManagement.UIElementType.Highlight) : Ink(255, 0x3B, 0xD6, 0x71);
    public static Windows.UI.Color NeedsYou => Contrast ? Sys(Windows.UI.ViewManagement.UIElementType.Highlight) : Ink(255, 0xFF, 0xB0, 0x20);
    public static Windows.UI.Color Error => Contrast ? Sys(Windows.UI.ViewManagement.UIElementType.Highlight) : Ink(255, 0xFF, 0x45, 0x3A);
    public static Windows.UI.Color Primary => Contrast ? Sys(Windows.UI.ViewManagement.UIElementType.WindowText) : Ink(255, 255, 255, 255);
    public static Windows.UI.Color Secondary => Contrast ? Sys(Windows.UI.ViewManagement.UIElementType.WindowText) : Ink(153, 255, 255, 255);
    public static Windows.UI.Color Tertiary => Contrast ? Sys(Windows.UI.ViewManagement.UIElementType.WindowText) : Ink(102, 255, 255, 255);
    public static Windows.UI.Color Fill => Contrast ? Sys(Windows.UI.ViewManagement.UIElementType.Window) : Ink(255, 0, 0, 0);
    /// <summary>10% white on Ink; a solid border in a contrast theme.</summary>
    public static Windows.UI.Color Hairline => Contrast ? Sys(Windows.UI.ViewManagement.UIElementType.WindowText) : Ink(26, 255, 255, 255);

    public static Windows.UI.Color Colour(Accent accent, Windows.UI.Color otherwise) => accent switch
    {
        Accent.Done => Done,
        Accent.NeedsYou => NeedsYou,
        Accent.Error => Error,
        _ => otherwise,
    };
}

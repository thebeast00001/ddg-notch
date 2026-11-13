using System.Numerics;
using Forty.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Forty.App;

/// <summary>How a button's plate looks at rest, under the pointer and while pressed.</summary>
sealed record ButtonLook(Color Rest, Color Hover, Color Pressed)
{
    static Color White(byte a) => Color.FromArgb(a, 255, 255, 255);

    /// <summary>No plate at rest; a soft one under the pointer (transport, back, footer links).</summary>
    public static ButtonLook Quiet => new(White(0), White(24), White(40));
    /// <summary>A faint plate always (toggles that are off, secondary answers).</summary>
    public static ButtonLook Soft => new(White(26), White(40), White(56));
    /// <summary>The filled white plate (play, primary answers, toggles that are on).</summary>
    public static ButtonLook Filled => new(Palette.Primary, Blend(Palette.Primary, 0.9), Blend(Palette.Primary, 0.8));
    /// <summary>A plate in an accent colour, faint (shuffle or repeat when on).</summary>
    public static ButtonLook Tinted(Color accent) => new(Color.FromArgb(46, accent.R, accent.G, accent.B), Color.FromArgb(66, accent.R, accent.G, accent.B), Color.FromArgb(86, accent.R, accent.G, accent.B));
    /// <summary>Nothing at all (a switch draws its own track).</summary>
    public static ButtonLook None => new(White(0), White(0), White(0));

    static Color Blend(Color c, double f) => Color.FromArgb(c.A, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
}

/// <summary>
/// The island's one button. WinUI's own button paints its hover and pressed states from theme
/// resources, which is where the cheap grey square came from; here those are all transparent and the
/// button carries its own plate, which eases between rest, hover and pressed in 120 ms.
///
/// The press answers on pointer-down: the button sinks on a fast critically damped spring, by the same
/// depth whatever its size (its longer side gives way about 3 DIPs, so a 56 DIP play button and a
/// wide chip feel equally solid), and on release it comes back with the smallest give. Content is
/// centred in the plate on its visual weight. Keyboard focus shows a 2 DIP ring that follows the
/// plate's shape; the pointer never does.
/// </summary>
sealed class IslandButton
{
    readonly Border _plate;
    readonly Part _press;
    readonly bool _sinks;
    ButtonLook _look;
    bool _hover, _pressed;
    double _depth = 0.95;

    public Button Button { get; }

    /// <param name="sinks">False for controls that answer the press themselves (a switch stretches its knob instead).</param>
    public IslandButton(UIElement content, string name, ButtonLook look, double width, double height, double radius, Compositor compositor, Thickness? padding = null, bool sinks = true)
    {
        _sinks = sinks;
        _look = look;
        if (content is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; }
        _plate = new Border
        {
            Child = content, CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(0),
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(120) },
        };
        Button = new Button
        {
            Content = _plate, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(radius),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            MinWidth = 0, MinHeight = 0, Height = height,
            UseSystemFocusVisuals = true, FocusVisualMargin = new Thickness(-3), FocusVisualPrimaryThickness = new Thickness(2),
            FocusVisualSecondaryThickness = new Thickness(0), FocusVisualPrimaryBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
        };
        if (!double.IsNaN(width)) Button.Width = width;
        foreach (string key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled",
                                       "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" })
            Button.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(Button, name);

        Button.CenterPoint = new Vector3((float)(double.IsNaN(width) ? 0 : width / 2), (float)height / 2, 0);
        _depth = Depth(double.IsNaN(width) ? height : Math.Max(width, height));
        Button.SizeChanged += (_, e) =>
        {
            Button.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            _depth = Depth(Math.Max(e.NewSize.Width, e.NewSize.Height));
        };
        _press = new Part(Button, compositor, 1);
        Button.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, _) => { _hover = true; Paint(); }), true);
        Button.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => { _hover = false; Release(); }), true);
        Button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _pressed = true;
            Paint();
            if (_sinks) _press.ScaleTo(_depth, Springs.Press);
        }), true);
        Button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Release()), true);
        Button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => Release()), true);
        Button.IsEnabledChanged += (_, _) => _plate.Opacity = Button.IsEnabled ? 1 : 0.35;
        Paint();
    }

    void Release()
    {
        bool was = _pressed;
        _pressed = false;
        Paint();
        if (was && _sinks) _press.ScaleTo(1, Feel.Release);
    }

    /// <summary>The scale a press sinks to: the longer side gives way by about 3 DIPs, never less than 1.5% or more than 6%.</summary>
    static double Depth(double longerSide) => Math.Clamp(1 - 3 / Math.Max(1, longerSide), 0.94, 0.985);

    public ButtonLook Look { get => _look; set { _look = value; Paint(); } }

    /// <summary>The plate that holds the content.</summary>
    public Border Plate => _plate;

    void Paint() => _plate.Background = new SolidColorBrush(_pressed ? _look.Pressed : _hover ? _look.Hover : _look.Rest);

    public static implicit operator Button(IslandButton b) => b.Button;
}

/// <summary>Colour changes on text and icons ease like the plates behind them, instead of switching on one frame.</summary>
static class Ease
{
    public static void Colour(SolidColorBrush brush, Color to, int ms = 120)
    {
        if (brush.Color == to) return;
        if (MotionPolicy.Reduced) { brush.Color = to; return; }
        var animation = new Microsoft.UI.Xaml.Media.Animation.ColorAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms) };
        var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, brush);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Color");
        story.Children.Add(animation);
        story.Begin();
    }
}

/// <summary>
/// The island's switch, drawn by the compositor so every frame is exact. It behaves like a physical one:
/// - Pressing it stretches the knob toward where it will go (feedback on pointer-down, before anything is decided).
/// - Dragging moves the knob with the finger, 1:1 from where it was grabbed, and stops at either end.
/// - Letting go decides by where the throw would carry the knob, not where it happens to be, and the
///   knob leaves at the finger's own speed. A tap flips it on a critically damped spring.
/// - The track's colour follows the knob's position on every frame, so half-way is half-way.
/// On is the filled white of every "on" control in the island (the state greens are reserved for done).
/// Space and Enter flip it from the keyboard.
/// </summary>
sealed class IslandSwitch
{
    public const double Width = 42, Height = 24;
    const double Knob = 20, Inset = 2, Stretch = 6, Travel = Width - Knob - 2 * Inset;
    static readonly Spring Slide = new(1.0, 0.30);
    static readonly Spring Thrown = new(0.82, 0.30);

    readonly CompositionSpring _p, _stretch;
    readonly Grid _host;
    readonly VelocityTracker _velocity = new();
    bool _on, _down, _dragging;
    double _downX, _downP;

    public Button Button { get; }
    public bool IsOn => _on;
    public event Action<bool>? Toggled;

    public IslandSwitch(bool on, string name, Compositor c)
    {
        _on = on;
        _p = new CompositionSpring(c, on ? 1 : 0);
        _stretch = new CompositionSpring(c, 0);
        _host = new Grid { Width = Width, Height = Height };

        var shapes = c.CreateShapeVisual();
        shapes.Size = new Vector2((float)Width, (float)Height);
        var track = c.CreateRoundedRectangleGeometry();
        track.Size = shapes.Size;
        track.CornerRadius = new Vector2((float)Height / 2);
        var trackFill = c.CreateColorBrush();
        Follow(trackFill, "Color", "ColorLerp(off, on, Clamp(p.v, 0, 1))", ("off", Color.FromArgb(56, 255, 255, 255)), ("on", Palette.Primary));
        var trackSprite = c.CreateSpriteShape(track);
        trackSprite.FillBrush = trackFill;

        var knob = c.CreateRoundedRectangleGeometry();
        knob.CornerRadius = new Vector2((float)Knob / 2);
        Follow(knob, "Size", $"Vector2({F(Knob)} + {F(Stretch)} * Clamp(s.v, 0, 1), {F(Knob)})");
        // Anchored on the side it rests on, so the stretch reaches toward where it will go.
        Follow(knob, "Offset", $"Vector2({F(Inset)} + Clamp(p.v, 0, 1) * ({F(Travel)} - {F(Stretch)} * Clamp(s.v, 0, 1)), {F(Inset)})");
        // The knob inverts as it crosses the middle (white over the grey track, dark over the white one).
        // A linear blend would leave a grey knob on a grey track half-way through a drag.
        var knobFill = c.CreateColorBrush();
        Follow(knobFill, "Color", "ColorLerp(off, on, Clamp((p.v - 0.42) / 0.16, 0, 1))", ("off", Color.FromArgb(255, 255, 255, 255)), ("on", Palette.OnPrimary));
        var knobSprite = c.CreateSpriteShape(knob);
        knobSprite.FillBrush = knobFill;

        shapes.Shapes.Add(trackSprite);
        shapes.Shapes.Add(knobSprite);
        ElementCompositionPreview.SetElementChildVisual(_host, shapes);

        Button = new IslandButton(_host, name, ButtonLook.None, Width, Height, Height / 2, c, sinks: false).Button;
        Button.Tag = "switch";
        AutomationProperties.SetItemStatus(Button, on ? "On" : "Off");
        Button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        Button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
        Button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        // WinUI's button gives up the pointer while it is still handling the release, before the release
        // reaches the handler above, so a lost pointer is only a cancel if no release has followed it.
        Button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => CancelLater()), true);
        Button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => CancelLater()), true);
        // The pointer is handled above; Click here is the keyboard (Space or Enter).
        Button.Click += (_, _) => { if (!_down) Commit(!_on, 0); };
    }

    void Follow(CompositionObject target, string property, string expression, params (string Name, Color Value)[] colours)
    {
        var animation = target.Compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("p", _p.Props);
        animation.SetReferenceParameter("s", _stretch.Props);
        foreach (var (n, v) in colours) animation.SetColorParameter(n, v);
        target.StartAnimation(property, animation);
    }

    static string F(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _down = true;
        _dragging = false;
        _downX = e.GetCurrentPoint(_host).Position.X;
        _downP = _on ? 1 : 0;
        _velocity.Reset();
        _velocity.Add(Clock.Now, _downX);
        _stretch.To(1, Springs.Press);
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_down) return;
        double x = e.GetCurrentPoint(_host).Position.X;
        _velocity.Add(Clock.Now, x);
        if (!_dragging && Math.Abs(x - _downX) > 4) _dragging = true;   // a little hysteresis, so a tap that wobbles is still a tap
        if (_dragging) _p.Set(Math.Clamp(_downP + (x - _downX) / Travel, 0, 1));
    }

    void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_down) return;
        _down = false;
        _stretch.To(0, Springs.Quick);
        if (_dragging)
        {
            double velocity = _velocity.Velocity(Clock.Now) / Travel;   // knob travels per second
            bool on = Gesture.Project(_p.Current, velocity, 0.99) >= 0.5;
            Commit(on, velocity);
            return;
        }
        var at = e.GetCurrentPoint(_host).Position;
        bool inside = at.X > -10 && at.X < Width + 10 && at.Y > -10 && at.Y < Height + 10;   // released on it, with some slack
        if (inside) Commit(!_on, 0);
        else _p.To(_on ? 1 : 0, Slide);
    }

    void CancelLater() => Button.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Cancel);

    void Cancel()
    {
        if (!_down) return;
        _down = false;
        _stretch.To(0, Springs.Quick);
        _p.To(_on ? 1 : 0, Slide);
    }

    void Commit(bool on, double velocity)
    {
        bool changed = on != _on;
        _on = on;
        if (velocity != 0) _p.Throw(on ? 1 : 0, Thrown, velocity);
        else _p.To(on ? 1 : 0, Slide);
        AutomationProperties.SetItemStatus(Button, on ? "On" : "Off");
        if (changed) Toggled?.Invoke(on);
    }

    /// <summary>For the layout check: holds the switch in a pose (knob position and stretch) with no motion.</summary>
    public void Pose(double position, double stretch) { _p.Set(position); _stretch.Set(stretch); }
}

/// <summary>
/// Play and pause as one shape that changes, not two icons that swap. The triangle is cut in two at
/// the middle; each half becomes one of the pause bars, corners rounding as they square up, and the
/// whole glyph draws in a little (to 86%) at the half-way point, so the change reads as one gesture.
/// It is precomputed for 25 points and played by the compositor, its progress bound to a spring, so
/// pressing again mid-change simply turns it around. One glyph is kept for the life of the panel and
/// moves into each rebuilt transport, so the change runs from wherever it was.
/// </summary>
sealed class PlayPauseGlyph
{
    const int Steps = 24;
    readonly CompositionSpring _p;
    readonly CompositionColorBrush _brush;

    public Grid Element { get; }
    public bool Playing { get; private set; }

    public PlayPauseGlyph(Compositor c, double size, Color colour, bool playing)
    {
        Playing = playing;
        _p = new CompositionSpring(c, playing ? 1 : 0);
        Element = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var visual = c.CreateShapeVisual();
        visual.Size = new Vector2((float)size);
        visual.ViewBox = c.CreateViewBox();
        visual.ViewBox.Size = new Vector2(24, 24);
        _brush = c.CreateColorBrush(colour);
        var group = c.CreateContainerShape();
        group.CenterPoint = new Vector2(12, 12);
        var dip = c.CreateExpressionAnimation("Vector2(1 - 0.14 * Sin(3.14159265 * Clamp(p.v, 0, 1)), 1 - 0.14 * Sin(3.14159265 * Clamp(p.v, 0, 1)))");
        dip.SetReferenceParameter("p", _p.Props);
        group.StartAnimation("Scale", dip);

        var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        var linear = c.CreateLinearEasingFunction();
        for (int piece = 0; piece < 2; piece++)
        {
            var geometry = c.CreatePathGeometry();
            var morph = c.CreatePathKeyFrameAnimation();
            for (int i = 0; i <= Steps; i++) morph.InsertKeyFrame(i / (float)Steps, Shape(device, piece, i / (double)Steps), linear);
            morph.Duration = TimeSpan.FromSeconds(1);
            geometry.StartAnimation("Path", morph);
            var controller = geometry.TryGetAnimationController("Path");
            controller.Pause();
            var progress = c.CreateExpressionAnimation("Clamp(p.v, 0, 1)");
            progress.SetReferenceParameter("p", _p.Props);
            controller.StartAnimation("Progress", progress);
            var sprite = c.CreateSpriteShape(geometry);
            sprite.FillBrush = _brush;
            group.Shapes.Add(sprite);
        }
        visual.Shapes.Add(group);
        ElementCompositionPreview.SetElementChildVisual(Element, visual);
    }

    /// <summary>Shows playing (the pause bars) or paused (the triangle), changing shape from wherever it is.</summary>
    public void Set(bool playing)
    {
        if (playing == Playing) return;
        Playing = playing;
        _p.To(playing ? 1 : 0, Feel.Morph);
    }

    public void SetColour(Color colour) => _brush.Color = colour;

    /// <summary>For the layout check: holds the glyph part-way through its change.</summary>
    public void Pose(double progress) => _p.Set(progress);

    /// <summary>
    /// Takes the glyph out of the button it was in, so a rebuilt transport can hold it. The old
    /// button is remembered (<see cref="Holder"/>) rather than asked for: once a panel's content is
    /// cleared, WinUI no longer reports the element's parent, but the element still belongs to it.
    /// </summary>
    public FrameworkElement Detach()
    {
        if (Holder is { } held && held.Child == Element) held.Child = null;
        switch (Element.Parent)
        {
            case Border b: b.Child = null; break;
            case Panel p: p.Children.Remove(Element); break;
        }
        Holder = null;
        return Element;
    }

    /// <summary>The plate the glyph sits on now.</summary>
    public Border? Holder { get; set; }

    // The two halves, as four corners each (top left, top right, bottom right, bottom left), in a 24 × 24 box.
    // Play: a triangle with its centroid at (12, 12), cut at x = 13.9. Pause: two 4 × 14 bars.
    static readonly Vector2[][] PlayHalves =
    {
        new[] { new Vector2(8.4f, 4.8f), new Vector2(14.05f, 8.4f), new Vector2(14.05f, 15.6f), new Vector2(8.4f, 19.2f) },
        new[] { new Vector2(13.75f, 8.4f), new Vector2(19.4f, 11.6f), new Vector2(19.4f, 12.4f), new Vector2(13.75f, 15.6f) },
    };
    static readonly Vector2[][] PauseBars =
    {
        new[] { new Vector2(6, 5), new Vector2(10, 5), new Vector2(10, 19), new Vector2(6, 19) },
        new[] { new Vector2(14, 5), new Vector2(18, 5), new Vector2(18, 19), new Vector2(14, 19) },
    };

    static CompositionPath Shape(Microsoft.Graphics.Canvas.ICanvasResourceCreator device, int piece, double t)
    {
        var corners = new Vector2[4];
        for (int k = 0; k < 4; k++) corners[k] = Vector2.Lerp(PlayHalves[piece][k], PauseBars[piece][k], (float)t);
        // Corners round as the halves become bars. Where the two halves meet inside the triangle they
        // start square, so the triangle shows no notch down its middle.
        var radii = new float[4];
        for (int k = 0; k < 4; k++)
        {
            bool seam = piece == 0 ? k is 1 or 2 : k is 0 or 3;
            radii[k] = seam ? 1.2f * (float)t : 1.2f;
        }
        using var builder = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(device);
        for (int k = 0; k < 4; k++)
        {
            Vector2 prev = corners[(k + 3) % 4], at = corners[k], next = corners[(k + 1) % 4];
            float r = Math.Min(radii[k], 0.45f * Math.Min(Vector2.Distance(prev, at), Vector2.Distance(at, next)));
            Vector2 a = at + Toward(at, prev) * r, b = at + Toward(at, next) * r;
            if (k == 0) builder.BeginFigure(a); else builder.AddLine(a);
            builder.AddCubicBezier(Vector2.Lerp(a, at, 0.55f), Vector2.Lerp(b, at, 0.55f), b);
        }
        builder.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Closed);
        return new CompositionPath(Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(builder));
    }

    static Vector2 Toward(Vector2 from, Vector2 to)
    {
        var d = to - from;
        return d.LengthSquared() < 1e-6f ? Vector2.Zero : Vector2.Normalize(d);
    }
}

/// <summary>
/// Icons that sit exactly where they should. Font glyphs carry the font's line metrics, so in a
/// TextBlock they ride a little high or low; FontIcon centres the glyph's em box instead. The
/// transport icons and the recording mark are geometry, filled, centred on their visual weight:
/// the play triangle on its centroid, so it looks centred rather than measures centred.
/// </summary>
static class IconArt
{
    static readonly FontFamily Fluent = new("Segoe Fluent Icons");

    public static FontIcon Glyph(string glyph, double size, Color colour) => new()
    {
        Glyph = glyph, FontFamily = Fluent, FontSize = size, Foreground = new SolidColorBrush(colour),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    // 24 × 24 drawings.
    public const string Play = "M8.2 5.1 C8.2 4.3 9.1 3.8 9.8 4.2 L18.9 11.1 C19.5 11.5 19.5 12.5 18.9 12.9 L9.8 19.8 C9.1 20.2 8.2 19.7 8.2 18.9 Z";   // centroid at (12, 12)
    public const string Pause = "M7.2 5 H8.8 A1.2 1.2 0 0 1 10 6.2 V17.8 A1.2 1.2 0 0 1 8.8 19 H7.2 A1.2 1.2 0 0 1 6 17.8 V6.2 A1.2 1.2 0 0 1 7.2 5 Z " +
                                "M15.2 5 H16.8 A1.2 1.2 0 0 1 18 6.2 V17.8 A1.2 1.2 0 0 1 16.8 19 H15.2 A1.2 1.2 0 0 1 14 17.8 V6.2 A1.2 1.2 0 0 1 15.2 5 Z";
    public const string Next = "M2.8 7.3 C2.8 6.5 3.7 6 4.4 6.5 L11.6 11.2 C12.2 11.6 12.2 12.4 11.6 12.8 L4.4 17.5 C3.7 18 2.8 17.5 2.8 16.7 Z " +
                               "M11.8 7.3 C11.8 6.5 12.7 6 13.4 6.5 L20.6 11.2 C21.2 11.6 21.2 12.4 20.6 12.8 L13.4 17.5 C12.7 18 11.8 17.5 11.8 16.7 Z";
    public const string Previous = "M21.2 7.3 C21.2 6.5 20.3 6 19.6 6.5 L12.4 11.2 C11.8 11.6 11.8 12.4 12.4 12.8 L19.6 17.5 C20.3 18 21.2 17.5 21.2 16.7 Z " +
                                   "M12.2 7.3 C12.2 6.5 11.3 6 10.6 6.5 L3.4 11.2 C2.8 11.6 2.8 12.4 3.4 12.8 L10.6 17.5 C11.3 18 12.2 17.5 12.2 16.7 Z";

    /// <summary>One of the drawings above at <paramref name="size"/> DIPs, filled.</summary>
    public static FrameworkElement Drawing(string data, double size, Color colour)
    {
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
            Fill = new SolidColorBrush(colour), Width = 24, Height = 24, Stretch = Stretch.None,
        };
        return new Viewbox { Child = path, Width = size, Height = size, Stretch = Stretch.Uniform };
    }

    /// <summary>The recording mark: a ring and a dot, both exactly concentric, at any size.</summary>
    public static FrameworkElement Record(double size, Color colour)
    {
        var mark = new Grid { Width = size, Height = size };
        mark.Children.Add(new Ellipse { Stroke = new SolidColorBrush(colour), StrokeThickness = Math.Max(1.25, size * 0.11), Width = size, Height = size });
        double dot = size * 0.5;
        mark.Children.Add(new Ellipse { Fill = new SolidColorBrush(colour), Width = dot, Height = dot, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return mark;
    }

    /// <summary>Text for a button, centred on its letters (cap height to baseline), not on the line box.</summary>
    public static TextBlock Label(string text, double size, Color colour, FontFamily face) => new()
    {
        Text = text, FontFamily = face, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(colour),
        TextLineBounds = TextLineBounds.Tight, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
}

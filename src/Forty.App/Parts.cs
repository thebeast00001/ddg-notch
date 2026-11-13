using System.Numerics;
using Forty.Core;
using Forty.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Forty.App;

/// <summary>
/// The springs the new surface adds to MOTION.md's set. Small things get more life than big ones:
/// the same bounce on a large body would take longer to settle and look heavy.
/// </summary>
static class Feel
{
    /// <summary>Album art and glyphs arriving: a small element, so a little overshoot reads as alive.</summary>
    public static readonly Spring Pop = new(0.72, 0.36);
    /// <summary>The body widening for a new activity: barely elastic, like the island on a phone.</summary>
    public static readonly Spring Lively = new(0.86, 0.42);
    /// <summary>The body adjusting its width from one lyric line to the next.</summary>
    public static readonly Spring LyricWidth = new(0.9, 0.34);
    /// <summary>Colours crossing over from one song to the next.</summary>
    public const double ColourSeconds = 0.7;
}

/// <summary>
/// One moving part of the content: an element with its own springs for opacity, scale and offset, so
/// each part can enter and leave on its own beat. Positions come from XAML layout; the springs only
/// add translation and scale on top, which the compositor applies without any layout work.
/// </summary>
sealed class Part
{
    public UIElement Element { get; }
    readonly CompositionSpring _opacity, _scale, _x, _y;
    int _version;

    public Part(UIElement element, Compositor compositor, double opacity = 0)
    {
        Element = element;
        _opacity = new CompositionSpring(compositor, opacity);
        _scale = new CompositionSpring(compositor, 1);
        _x = new CompositionSpring(compositor, 0);
        _y = new CompositionSpring(compositor, 0);
        Drive("Opacity", "Clamp(s.v, 0, 1)", _opacity);
        Drive("Scale", "Vector3(s.v, s.v, 1)", _scale);
        var translation = compositor.CreateExpressionAnimation("Vector3(x.v, y.v, 0)");
        translation.Target = "Translation";
        translation.SetReferenceParameter("x", _x.Props);
        translation.SetReferenceParameter("y", _y.Props);
        element.StartAnimation(translation);
    }

    void Drive(string target, string expression, CompositionSpring spring)
    {
        var animation = spring.Props.Compositor.CreateExpressionAnimation(expression);
        animation.Target = target;
        animation.SetReferenceParameter("s", spring.Props);
        Element.StartAnimation(animation);
    }

    public bool Shown => _opacity.Target > 0.5;

    /// <summary>
    /// Arrives after <paramref name="delayMs"/>: fades in, grows from <paramref name="fromScale"/> and
    /// settles from <paramref name="fromY"/> DIPs below. If something else is asked of this part before
    /// the delay is up, the entrance is dropped, so rapid changes never stack.
    /// </summary>
    public void Enter(int delayMs = 0, double fromScale = 0.94, double fromY = 0, double fromX = 0, Spring? spring = null)
    {
        int version = ++_version;
        var feel = spring ?? Springs.Move;
        if (!MotionPolicy.Reduced && _opacity.Current < 0.05)
        {
            // Starting from nothing: place it at its starting pose with no motion.
            _scale.Set(fromScale);
            _y.Set(fromY);
            _x.Set(fromX);
        }
        void Go()
        {
            if (version != _version) return;
            _opacity.To(1, feel, isFade: true);
            _scale.To(1, feel);
            _y.To(0, feel);
            _x.To(0, feel);
        }
        if (delayMs <= 0 || MotionPolicy.Reduced) Go();
        else _ = Later(delayMs, Go);
    }

    /// <summary>Leaves at once (exits are shorter and never wait), shrinking to <paramref name="toScale"/> and drifting <paramref name="toY"/>.</summary>
    public void Leave(double toScale = 0.92, double toY = 0, double toX = 0, Spring? spring = null)
    {
        _version++;
        var feel = spring ?? Springs.Quick;
        _opacity.To(0, feel, isFade: true);
        _scale.To(toScale, feel);
        _y.To(toY, feel);
        _x.To(toX, feel);
    }

    /// <summary>
    /// Clearing the way for new content in the same place: gone in about 90 ms, so the new content can
    /// arrive without the two overlapping (a container that changes size between two rich states).
    /// </summary>
    public static readonly Spring Clear = new(1.0, 0.16);

    /// <summary>Moves by an offset while visible (hover parallax, a shared element's travel).</summary>
    public void Nudge(double x, double y, Spring spring)
    {
        _x.To(x, spring);
        _y.To(y, spring);
    }

    /// <summary>Gone on this frame, with no fade: its copy elsewhere has taken over (a shared element, F4).</summary>
    public void Vanish()
    {
        _version++;
        _opacity.Set(0);
    }

    /// <summary>Jumps to a pose with no motion, ready for a spring to carry it home.</summary>
    public void Place(double x, double y, double scale)
    {
        _x.Set(x);
        _y.Set(y);
        _scale.Set(scale);
    }

    static async Task Later(int ms, Action action)
    {
        await Task.Delay(ms);
        action();
    }
}

/// <summary>
/// The microphone's level while you dictate: four bars that follow your voice. Each new level eases
/// the bars there in 90 ms, so 20 readings a second read as one continuous movement; the middle bars
/// reach furthest, so even a steady voice has a shape. Nothing moves between readings.
/// </summary>
sealed class LevelMeter
{
    public StackPanel Root { get; }
    readonly Border[] _bars = new Border[4];
    readonly Compositor _compositor;
    static readonly float[] Reach = { 0.55f, 1.0f, 0.8f, 0.45f };

    public LevelMeter(Compositor compositor)
    {
        _compositor = compositor;
        Root = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center };
            _bars[i].CenterPoint = new Vector3(1.5f, 8f, 0);
            _bars[i].Scale = new Vector3(1, 0.2f, 1);
            Root.Children.Add(_bars[i]);
        }
        SetColour(Palette.Primary);
    }

    public void SetColour(Windows.UI.Color colour)
    {
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(colour);
    }

    public void Set(double level)
    {
        float l = (float)Math.Clamp(level, 0, 1);
        for (int i = 0; i < _bars.Length; i++)
        {
            float height = 0.2f + 0.8f * l * Reach[i];
            if (MotionPolicy.Reduced) { _bars[i].Scale = new Vector3(1, height, 1); continue; }
            var animation = _compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(1f, new Vector3(1, height, 1), _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.3f, 1f)));
            animation.Duration = TimeSpan.FromMilliseconds(90);
            animation.Target = "Scale";
            _bars[i].StartAnimation(animation);
        }
    }
}

/// <summary>
/// The equaliser in the music capsule: four thin bars in the song's highlight colour that move while
/// music plays and settle flat when it pauses. They are a sign of playback, not a measurement of the
/// sound (the real visualiser, which listens to the audio, comes later).
/// </summary>
sealed class Equaliser
{
    public StackPanel Root { get; }
    readonly Border[] _bars = new Border[4];
    readonly Compositor _compositor;
    bool _playing;

    public Equaliser(Compositor compositor)
    {
        _compositor = compositor;
        Root = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Border { Width = 3, Height = 14, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center };
            _bars[i].CenterPoint = new Vector3(1.5f, 7f, 0);
            Root.Children.Add(_bars[i]);
        }
        SetColour(Palette.Primary);
        Settle();
    }

    public void SetColour(Windows.UI.Color colour)
    {
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(colour);
    }

    static readonly float[][] Shapes =
    {
        new[] { 0.35f, 0.9f, 0.5f, 1.0f, 0.4f },
        new[] { 0.8f, 0.4f, 1.0f, 0.55f, 0.85f },
        new[] { 0.5f, 1.0f, 0.35f, 0.75f, 0.5f },
        new[] { 0.9f, 0.55f, 0.8f, 0.3f, 0.9f },
    };
    static readonly double[] Periods = { 0.94, 1.18, 0.82, 1.06 };
    static readonly float[] Rest = { 0.3f, 0.5f, 0.38f, 0.26f };
    CompositionPropertySet? _levels;   // one height per bar; each bar's scale is bound to it once
    Action<double>? _tick;

    /// <summary>
    /// Starts or stops the bars. Each bar has its own rhythm, so together they never look like a
    /// metronome. Their heights are stepped by the ambient clock (see <see cref="Ambient"/>) as
    /// compositor values, so neither XAML nor a never-ending animation runs while music plays.
    /// </summary>
    public void SetPlaying(bool playing)
    {
        playing &= Ambient.Allowed;
        if (playing == _playing) return;
        _playing = playing;
        if (!playing || MotionPolicy.Reduced) { Settle(); return; }
        Bind();
        for (int i = 0; i < _bars.Length; i++) _levels!.StopAnimation($"b{i}");   // a settle still in flight gives way
        _tick = now => { for (int i = 0; i < _bars.Length; i++) _levels!.InsertScalar($"b{i}", (float)Ambient.Loop(Shapes[i], Periods[i], now)); };
        Ambient.Add(_tick);
    }

    void Bind()
    {
        if (_levels != null) return;
        _levels = _compositor.CreatePropertySet();
        for (int i = 0; i < _bars.Length; i++)
        {
            _levels.InsertScalar($"b{i}", Rest[i]);
            var scale = _compositor.CreateExpressionAnimation($"Vector3(1, l.b{i}, 1)");
            scale.SetReferenceParameter("l", _levels);
            scale.Target = "Scale";
            _bars[i].StartAnimation(scale);
        }
    }

    /// <summary>The bars come to rest in 260 ms: each height eases there on the compositor, once.</summary>
    void Settle()
    {
        if (_tick != null) { Ambient.Remove(_tick); _tick = null; }
        Bind();
        for (int i = 0; i < _bars.Length; i++)
        {
            if (MotionPolicy.Reduced) { _levels!.InsertScalar($"b{i}", Rest[i]); continue; }
            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1f, Rest[i]);
            animation.Duration = TimeSpan.FromMilliseconds(260);
            _levels!.StartAnimation($"b{i}", animation);
        }
    }
}

/// <summary>
/// A drawn battery (F47): an outline, a cap, and a fill that sweeps to the level once when charging
/// starts. Green while charging, red at 20% or below, white otherwise.
/// </summary>
sealed class BatteryGauge
{
    public Grid Root { get; }
    readonly Border _fill, _outline, _cap;
    readonly CompositionSpring _level;
    readonly double _inner;

    public BatteryGauge(Compositor compositor, double width = 25, double height = 12)
    {
        _inner = width - 4;
        _outline = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(height * 0.3), BorderThickness = new Thickness(1.2) };
        _fill = new Border { Width = _inner, Height = height - 4, CornerRadius = new CornerRadius((height - 4) * 0.28), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
        _fill.CenterPoint = new Vector3(0, (float)(height - 4) / 2, 0);   // grows from its left edge
        _cap = new Border { Width = 2, Height = height * 0.4, CornerRadius = new CornerRadius(0, 1, 1, 0), Margin = new Thickness(width + 1, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var body = new Grid { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(_fill);
        body.Children.Add(_outline);
        Root = new Grid { Width = width + 3, Height = height, VerticalAlignment = VerticalAlignment.Center };
        Root.Children.Add(body);
        Root.Children.Add(_cap);

        _level = new CompositionSpring(compositor, 0);
        var scale = compositor.CreateExpressionAnimation("Vector3(Clamp(s.v, 0.04, 1), 1, 1)");
        scale.Target = "Scale";
        scale.SetReferenceParameter("s", _level.Props);
        _fill.StartAnimation(scale);
    }

    /// <summary>Shows a level; <paramref name="sweep"/> starts the fill from empty, the plug-in moment.</summary>
    public void Show(int percent, bool charging, bool sweep)
    {
        var colour = charging ? Palette.Done : percent <= 20 ? Palette.Error : Palette.Primary;
        _fill.Background = new SolidColorBrush(colour);
        _outline.BorderBrush = new SolidColorBrush(Palette.Secondary);
        _cap.Background = new SolidColorBrush(Palette.Secondary);
        if (sweep && !MotionPolicy.Reduced) _level.Set(0);
        _level.To(percent / 100.0, sweep ? new Spring(0.82, 0.7) : Springs.Quick);
    }
}

/// <summary>Album art as a rounded image. Decoded at twice its largest size on screen, never bigger.</summary>
static class Art
{
    public static async Task<BitmapImage?> DecodeAsync(byte[] bytes, int pixelSize)
    {
        try
        {
            var image = new BitmapImage { DecodePixelWidth = pixelSize, DecodePixelType = DecodePixelType.Logical };
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
            stream.Seek(0);
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException) { return null; }
    }

    public static Border Frame(double size, double radius) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(radius),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255)), BorderThickness = new Thickness(0.5),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static void Fill(Border frame, ImageSource? image)
    {
        frame.Background = image == null
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255))
            : new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
    }
}

static class Colours
{
    public static Windows.UI.Color Of(Argb c) => Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B);
}

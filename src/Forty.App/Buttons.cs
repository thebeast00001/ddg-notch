using System.Numerics;
using Forty.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
/// button carries its own plate, which eases between rest, hover and pressed in 120 ms and gives way
/// under the press (0.94, back on the press spring). Content is centred in the plate on its visual
/// weight. Keyboard focus shows a 2 DIP ring that follows the plate's shape; the pointer never does.
/// </summary>
sealed class IslandButton
{
    readonly Border _plate;
    readonly Part _press;
    ButtonLook _look;
    bool _hover, _pressed;

    public Button Button { get; }

    public IslandButton(UIElement content, string name, ButtonLook look, double width, double height, double radius, Compositor compositor, Thickness? padding = null)
    {
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
        Button.SizeChanged += (_, e) => Button.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        _press = new Part(Button, compositor, 1);
        Button.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, _) => { _hover = true; Paint(); }), true);
        Button.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => { _hover = false; Release(); }), true);
        Button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { _pressed = true; Paint(); _press.Place(0, 0, 0.94); }), true);
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
        if (was) _press.Enter(0, fromScale: 0.94, spring: Springs.Press);
    }

    public ButtonLook Look { get => _look; set { _look = value; Paint(); } }

    void Paint() => _plate.Background = new SolidColorBrush(_pressed ? _look.Pressed : _hover ? _look.Hover : _look.Rest);

    public static implicit operator Button(IslandButton b) => b.Button;
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

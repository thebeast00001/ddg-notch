using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Forty.App;

/// <summary>
/// F2: content that changes passes through a blur, as on the iPhone's island. Outgoing content blurs
/// from 0 to 6 as it shrinks and fades; incoming content arrives blurred at 8 and sharpens as it
/// settles (MOTION.md 5).
///
/// XAML has no blur of its own, so the compositor draws the element into a live "visual surface", a
/// blur effect reads that surface, and the blurred copy is shown exactly where the element is while
/// the element itself is hidden. The copy is live: the element's own entrance or exit (fade, scale,
/// drift) plays inside it. When the pass ends the copy goes and the element shows again. Nothing is
/// kept between passes, so a resting island has no surfaces and no effects.
/// </summary>
sealed class ContentBlur
{
    const float Pad = 16;   // room around the element for the blur to spread into

    readonly Compositor _c;
    readonly ContainerVisual _host;
    readonly CompositionEffectFactory _factory;
    readonly Dictionary<UIElement, (SpriteVisual Sprite, int Version)> _passes = new();
    int _version;

    /// <param name="host">The element whose child visual carries the copies; it must contain the blurred elements and share their clip.</param>
    public ContentBlur(Compositor compositor, UIElement host)
    {
        _c = compositor;
        _host = compositor.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(host, _host);
        var effect = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = 0,
            BorderMode = EffectBorderMode.Soft,
            Optimization = EffectOptimization.Speed,
            Source = new CompositionEffectSourceParameter("source"),
        };
        _factory = compositor.CreateEffectFactory(effect, new[] { "Blur.BlurAmount" });
    }

    /// <summary>
    /// Blurs <paramref name="element"/> from <paramref name="from"/> to <paramref name="to"/> over
    /// <paramref name="seconds"/>, after <paramref name="delayMs"/>.
    /// </summary>
    /// <param name="holdMs">For an exit: how long the copy stays in place, so the content it stands in for has finished fading by the time it shows again.</param>
    public void Pass(FrameworkElement element, float from, float to, double seconds, int delayMs = 0, int holdMs = 0)
    {
        if (MotionPolicy.Reduced || element.Width is double.NaN || element.Height is double.NaN) return;
        End(element);
        int version = ++_version;
        var source = ElementCompositionPreview.GetElementVisual(element);
        var size = new Vector2((float)element.Width + 2 * Pad, (float)element.Height + 2 * Pad);

        var surface = _c.CreateVisualSurface();
        surface.SourceVisual = source;
        surface.SourceOffset = new Vector2(-Pad, -Pad);
        surface.SourceSize = size;
        var surfaceBrush = _c.CreateSurfaceBrush(surface);
        var brush = _factory.CreateBrush();
        brush.SetSourceParameter("source", surfaceBrush);
        brush.Properties.InsertScalar("Blur.BlurAmount", from);

        var sprite = _c.CreateSpriteVisual();
        sprite.Size = size;
        sprite.Brush = brush;
        var follow = _c.CreateExpressionAnimation("Vector3(v.Offset.X - pad, v.Offset.Y - pad, 0)");
        follow.SetReferenceParameter("v", source);
        follow.SetScalarParameter("pad", Pad);
        sprite.StartAnimation("Offset", follow);
        _host.Children.InsertAtTop(sprite);
        _passes[element] = (sprite, version);
        source.Opacity = 0;   // the copy stands in for it

        var blur = _c.CreateScalarKeyFrameAnimation();
        blur.InsertKeyFrame(1f, to, _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0.2f, 1f)));
        blur.Duration = TimeSpan.FromSeconds(seconds);
        blur.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        brush.StartAnimation("Blur.BlurAmount", blur);
        _ = EndLater(element, version, Math.Max(delayMs + (int)(seconds * 1000) + 20, holdMs));
    }

    async Task EndLater(UIElement element, int version, int ms)
    {
        await Task.Delay(ms);
        if (_passes.TryGetValue(element, out var pass) && pass.Version == version) End(element);
    }

    /// <summary>Removes any copy standing in for the element and shows the element itself again.</summary>
    public void End(UIElement element)
    {
        if (!_passes.Remove(element, out var pass)) return;
        _host.Children.Remove(pass.Sprite);
        pass.Sprite.Brush?.Dispose();
        pass.Sprite.Dispose();
        ElementCompositionPreview.GetElementVisual(element).Opacity = 1;
    }
}

using System.Numerics;
using Forty.Core;
using Microsoft.UI.Composition;

namespace Forty.App;

/// <summary>
/// The fluid colour behind the music, the way Apple Music fills its player with a song's colours:
/// three soft glows in the album's colours drift slowly inside the body, under a scrim that keeps white
/// text readable. Everything runs on the compositor; the app's own thread does nothing per frame.
///
/// The glows move only while music plays and the backdrop is showing. Paused, hidden, in battery
/// saver or with animation effects off, they hold still: the island is still when nothing is happening.
/// </summary>
sealed class Backdrop
{
    readonly Compositor _c;
    readonly Body _body;
    readonly ContainerVisual _root;
    readonly SpriteVisual[] _glows = new SpriteVisual[3];
    readonly CompositionColorGradientStop[] _centres = new CompositionColorGradientStop[3];
    readonly CompositionColorGradientStop[] _middles = new CompositionColorGradientStop[3];
    readonly CompositionPropertySet[] _drift = new CompositionPropertySet[3];
    readonly CompositionSpring _opacity;
    bool _flowing;

    /// <summary>Where each glow wanders, as fractions of the body's size around its centre, and how long a loop takes.</summary>
    static readonly (Vector2[] Path, double Seconds)[] Paths =
    {
        (new[] { new Vector2(-0.30f, -0.20f), new Vector2(0.05f, 0.25f), new Vector2(0.32f, -0.10f), new Vector2(-0.30f, -0.20f) }, 13),
        (new[] { new Vector2(0.35f, 0.25f), new Vector2(-0.15f, 0.10f), new Vector2(0.10f, -0.30f), new Vector2(0.35f, 0.25f) }, 17),
        (new[] { new Vector2(0.00f, 0.35f), new Vector2(0.30f, -0.25f), new Vector2(-0.35f, 0.05f), new Vector2(0.00f, 0.35f) }, 21),
    };

    public Backdrop(Compositor compositor, Body body)
    {
        _c = compositor;
        _body = body;
        _root = compositor.CreateContainerVisual();
        _root.Size = body.Layers.Size;
        _root.Clip = body.CreateClip();
        body.Layers.Children.InsertAtTop(_root);

        for (int i = 0; i < 3; i++)
        {
            var brush = compositor.CreateRadialGradientBrush();
            brush.MappingMode = CompositionMappingMode.Relative;
            brush.EllipseCenter = new Vector2(0.5f, 0.5f);
            brush.EllipseRadius = new Vector2(0.5f, 0.5f);
            _centres[i] = compositor.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(0, 0, 0, 0));
            _middles[i] = compositor.CreateColorGradientStop(0.45f, Windows.UI.Color.FromArgb(0, 0, 0, 0));
            brush.ColorStops.Add(_centres[i]);
            brush.ColorStops.Add(_middles[i]);
            brush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(0, 0, 0, 0)));

            var glow = _glows[i] = compositor.CreateSpriteVisual();
            glow.Brush = brush;
            var drift = _drift[i] = compositor.CreatePropertySet();
            drift.InsertVector2("p", Paths[i].Path[0]);
            // A glow is a little larger than the body's longer side and wanders around its centre.
            body.Bind(glow, "Size", "Vector2(Max(w.v, h.v) * 1.15, Max(w.v, h.v) * 1.15)");
            body.Bind(glow, "Offset",
                "Vector3(layout.hostW * 0.5 + d.p.X * w.v - Max(w.v, h.v) * 0.575, top.v + layout.inset + h.v * 0.5 + d.p.Y * Max(h.v, 60) - Max(w.v, h.v) * 0.575, 0)",
                ("d", drift));
            _root.Children.InsertAtTop(glow);
        }

        // The scrim: a soft darkening over the glows so white text always reads.
        var scrim = compositor.CreateSpriteVisual();
        scrim.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(70, 0, 0, 0));
        scrim.Size = _root.Size;
        _root.Children.InsertAtTop(scrim);

        _opacity = new CompositionSpring(compositor, 0);
        _opacity.Drive(_root, "Opacity", "Clamp(s.v, 0, 1)");
    }

    public bool Shown => _opacity.Target > 0.5;

    /// <summary>Takes a song's colours; they cross over from the previous song's in 0.7 s.</summary>
    public void SetColours(TrackColours colours)
    {
        for (int i = 0; i < 3; i++)
        {
            var accent = colours.Accents[i % colours.Accents.Length];
            Animate(_centres[i], Colours.Of(accent.WithAlpha(230)));
            Animate(_middles[i], Colours.Of(accent.WithAlpha(120)));
        }
    }

    void Animate(CompositionColorGradientStop stop, Windows.UI.Color colour)
    {
        if (MotionPolicy.Reduced) { stop.Color = colour; return; }
        var animation = _c.CreateColorKeyFrameAnimation();
        animation.InsertKeyFrame(1f, colour);
        animation.Duration = TimeSpan.FromSeconds(Feel.ColourSeconds);
        stop.StartAnimation("Color", animation);
    }

    /// <summary>Fades in or out. It never moves on its own entrance; it is decoration, so it only fades.</summary>
    public void Show(bool show) => _opacity.To(show ? 1 : 0, show ? Forty.Motion.Springs.Move : Forty.Motion.Springs.Quick, isFade: true);

    /// <summary>
    /// Starts or stops the slow drift. Stopping leaves each glow where it is. The drift is stepped by the
    /// ambient clock (see <see cref="Ambient"/>): the glows move a few DIPs a second, so stepping them
    /// looks the same as a compositor animation and costs a fraction of it.
    /// </summary>
    public void Flow(bool flow)
    {
        flow &= !MotionPolicy.Reduced;
        if (flow == _flowing) return;
        _flowing = flow;
        if (!flow) { Ambient.Remove(Step); return; }
        Ambient.Add(Step);
    }

    static readonly (float[] X, float[] Y, double Seconds)[] Tracks =
        Paths.Select(p => (p.Path.Select(v => v.X).ToArray(), p.Path.Select(v => v.Y).ToArray(), p.Seconds)).ToArray();

    void Step(double now)
    {
        for (int i = 0; i < 3; i++)
        {
            var (xs, ys, seconds) = Tracks[i];
            _drift[i].InsertVector2("p", new Vector2((float)Ambient.Loop(xs, seconds, now), (float)Ambient.Loop(ys, seconds, now)));
        }
    }
}

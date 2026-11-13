using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;

namespace Forty.App;

/// <summary>
/// The island on another display (F7, "Show on every display"). Instead of a second island with its
/// own state, springs and content, a mirror is a window of the same size whose only content is a live
/// copy of the main island: the compositor draws the main window's tree into a visual surface, and
/// the mirror shows that surface. Every morph, glow and blur appears on every display on the same
/// frame, at no cost in logic. Its window region follows the main island's, so beside the island the
/// mirror's display belongs to the apps underneath. Pressing a mirror brings the real island there.
/// </summary>
sealed class MirrorIsland : IDisposable
{
    readonly IslandWindow _main;
    readonly SpriteVisual _copy;
    readonly CompositionVisualSurface _surface;

    public IslandWindow Window { get; }
    public string DisplayId { get; }

    /// <summary>Raised when you press the mirror: the island should come to this display.</summary>
    public event Action<MirrorIsland>? Pressed;

    public MirrorIsland(IslandWindow main, DisplayArea area, double widthDip, double heightDip, int demoOffsetPx = 0)
    {
        _main = main;
        DisplayId = area.DisplayId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Window = new IslandWindow(widthDip, heightDip);
        Window.Place(area, demoOffsetPx);
        var c = Window.Compositor;
        _surface = c.CreateVisualSurface();
        _surface.SourceVisual = ElementCompositionPreview.GetElementVisual(main.Root);
        _surface.SourceSize = new Vector2((float)main.WidthDip, (float)main.HeightDip);
        _copy = c.CreateSpriteVisual();
        _copy.Size = _surface.SourceSize;
        _copy.Brush = c.CreateSurfaceBrush(_surface);
        ElementCompositionPreview.SetElementChildVisual(Window.BodyHost, _copy);
        Window.Root.PointerPressed += (_, e) => { e.Handled = true; Pressed?.Invoke(this); };
        main.RegionChanged += Follow;
        Follow();
    }

    /// <summary>The mirror's region is the main island's region: the same shape, hanging from the same place.</summary>
    void Follow() => Window.SetRegionPx(_main.RegionPx);

    public void Show() => Window.Show();
    public void Hide() => Window.Hide();

    public void Dispose()
    {
        _main.RegionChanged -= Follow;
        _copy.Dispose();
        _surface.Dispose();
        Window.Window.Close();
        Window.Dispose();
    }
}

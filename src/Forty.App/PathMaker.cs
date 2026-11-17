using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;

namespace Forty.App;

/// <summary>
/// Builds the compositor's paths (the play/pause morph, the liquid bridge, the agents' check) without
/// keeping a graphics device for the life of the island. Win2D builds a path only through a device,
/// and a device holds about 11 MB; a finished path no longer needs it. So a private device is made
/// when paths are needed and released a few seconds after the last one, instead of Win2D's shared
/// device, which is never released.
/// </summary>
static class PathMaker
{
    static CanvasDevice? _device;
    static int _version;
    const int ReleaseAfterMs = 4000;

    public static CompositionPath Make(Action<CanvasPathBuilder> draw) => Use(device =>
    {
        using var builder = new CanvasPathBuilder(device);
        draw(builder);
        return new CompositionPath(CanvasGeometry.CreatePath(builder));
    });

    /// <summary>Runs <paramref name="work"/> with a device, keeping it only while paths are being made.</summary>
    public static T Use<T>(Func<ICanvasResourceCreator, T> work)
    {
        _device ??= new CanvasDevice();
        var result = work(_device);
        int version = ++_version;
        var queue = DispatcherQueue.GetForCurrentThread();
        _ = Task.Delay(ReleaseAfterMs).ContinueWith(_ =>
        {
            void Release()
            {
                if (version != _version || _device == null) return;
                _device.Dispose();
                _device = null;
            }
            if (queue == null) Release(); else queue.TryEnqueue(DispatcherQueuePriority.Low, Release);
        }, TaskScheduler.Default);
        return result;
    }
}

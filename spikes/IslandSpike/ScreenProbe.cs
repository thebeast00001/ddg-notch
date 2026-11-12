using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace IslandSpike;

/// <summary>
/// Measures what actually reached the screen. Every composed frame of the monitor arrives with the
/// time it was composed; from each one this reads a single row and a single column of pixels through
/// the island and records how wide and tall the (magenta) body was. Nothing is stored but those numbers.
/// </summary>
sealed class ScreenProbe : IDisposable
{
    public readonly record struct Frame(double T, double Arrival, int WidthPx, int HeightPx);

    readonly CanvasDevice _device;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    readonly List<Frame> _frames = new(8192);
    readonly int _rowY, _rowX, _rowW, _colX, _colY, _colH;

    public volatile bool Recording;
    public int Errors;
    public string? FirstError;

    public ScreenProbe(IslandWindow win, double rowDipFromTop)
    {
        _device = CanvasDevice.GetSharedDevice();
        var item = CreateItem(win);

        // Monitor-relative pixels. The window's top rows sit above the monitor, so rows count from the monitor's top.
        int wx = win.WindowPx.X - win.MonitorPx.X;
        _rowY = (int)Math.Round(rowDipFromTop * win.Scale);
        _rowX = Math.Max(0, wx);
        _rowW = Math.Min(win.WindowPx.Width, item.Size.Width - _rowX);
        _colX = wx + win.WindowPx.Width / 2;
        _colY = 0;
        _colH = Math.Min(win.WindowPx.Height - IslandWindow.TopInsetPx, item.Size.Height);

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
        _session = _pool.CreateCaptureSession(item);
        Quiet(_session);
        _pool.FrameArrived += OnFrame;
        _session.StartCapture();
    }

    public List<Frame> Snapshot()
    {
        lock (_frames) return new List<Frame>(_frames);
    }

    void OnFrame(Direct3D11CaptureFramePool pool, object args)
    {
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame == null || !Recording) return;
            double arrival = Motion.Clock.Now;
            double composed = frame.SystemRelativeTime.TotalSeconds;
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
            byte[] row = bitmap.GetPixelBytes(_rowX, _rowY, _rowW, 1);
            byte[] column = bitmap.GetPixelBytes(_colX, _colY, 1, _colH);
            var sample = new Frame(composed, arrival, Extent(row), Extent(column));
            lock (_frames) _frames.Add(sample);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref Errors) == 1) FirstError = ex.ToString();
        }
    }

    /// <summary>Length of the run of magenta pixels in a strip of BGRA pixels.</summary>
    static int Extent(byte[] bgra)
    {
        int first = -1, last = -1;
        for (int i = 0, n = bgra.Length / 4; i < n; i++)
        {
            byte b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2];
            if (r > 140 && b > 140 && g < 110)
            {
                if (first < 0) first = i;
                last = i;
            }
        }
        return first < 0 ? 0 : last - first + 1;
    }

    static GraphicsCaptureItem CreateItem(IslandWindow win)
    {
        var id = new DisplayId { Value = win.DisplayIdValue };
        return GraphicsCaptureItem.TryCreateFromDisplayId(id)
               ?? throw new InvalidOperationException("Could not create a capture item for the display.");
    }

    static void Quiet(GraphicsCaptureSession session)
    {
        try { session.IsCursorCaptureEnabled = false; } catch { }
        try { session.IsBorderRequired = false; } catch { }
    }

    /// <summary>Read the colour of a few monitor pixels (monitor-relative) from one composed frame, as BGRA.</summary>
    public static async Task<List<(byte B, byte G, byte R)>?> ReadPixelsAsync(IslandWindow win, IReadOnlyList<(int X, int Y)> points)
    {
        var device = CanvasDevice.GetSharedDevice();
        var item = CreateItem(win);
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        Quiet(session);

        var done = new TaskCompletionSource<List<(byte, byte, byte)>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (p, _) =>
        {
            using var frame = p.TryGetNextFrame();
            if (frame == null || done.Task.IsCompleted) return;
            try
            {
                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
                var colours = new List<(byte, byte, byte)>();
                foreach (var (x, y) in points)
                {
                    byte[] px = bitmap.GetPixelBytes(x, y, 1, 1);
                    colours.Add((px[0], px[1], px[2]));
                }
                done.TrySetResult(colours);
            }
            catch (Exception ex) { done.TrySetException(ex); }
        };
        session.StartCapture();
        var finished = await Task.WhenAny(done.Task, Task.Delay(3000));
        return finished == done.Task ? await done.Task : null;
    }

    /// <summary>Save one small rectangle of the monitor (pixels, monitor-relative) as a PNG.</summary>
    public static async Task<bool> SaveRegionAsync(IslandWindow win, RectInt32 rect, string path)
    {
        var device = CanvasDevice.GetSharedDevice();
        var item = CreateItem(win);
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        Quiet(session);

        var done = new TaskCompletionSource<CanvasRenderTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (p, _) =>
        {
            using var frame = p.TryGetNextFrame();
            if (frame == null || done.Task.IsCompleted) return;
            try
            {
                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
                var target = new CanvasRenderTarget(device, rect.Width, rect.Height, 96f);
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.DrawImage(bitmap,
                        new Rect(0, 0, rect.Width, rect.Height),
                        new Rect(rect.X, rect.Y, rect.Width, rect.Height));
                }
                done.TrySetResult(target);
            }
            catch (Exception ex) { done.TrySetException(ex); }
        };
        session.StartCapture();

        var finished = await Task.WhenAny(done.Task, Task.Delay(3000));
        if (finished != done.Task) return false;
        using var image = await done.Task;
        if (image == null) return false;
        await image.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return true;
    }

    public void Dispose()
    {
        Recording = false;
        _session.Dispose();
        _pool.Dispose();
    }
}

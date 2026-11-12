using System.Numerics;
using Forty.Core;
using Forty.Motion;
using Forty.Platform;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Forty.App;

/// <summary>
/// FORTY_DEMO=1: plays a scripted sequence through the real queue, body and surface, and saves a still
/// of each state plus filmstrips of the transitions (every composed frame for half a second, laid out
/// top to bottom) to FORTY_OUT. It uses made-up songs, made-up covers drawn here and made-up lyric
/// lines. It never touches your media, devices or settings.
/// </summary>
static class Demo
{
    public static async Task RunAsync(IslandWindow win, IslandController island, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var watchdog = new Timer(_ => Environment.Exit(3), null, 180_000, Timeout.Infinite);
        var rect = new RectInt32(win.WindowPx.X - win.MonitorPx.X, 0, win.WindowPx.Width, (int)(380 * win.Scale));
        var strip = new RectInt32(win.WindowPx.X - win.MonitorPx.X + (int)(60 * win.Scale), 0, (int)((App.WindowWidth - 120) * win.Scale), (int)(78 * win.Scale));
        var tall = rect with { Height = (int)(App.WindowHeight * win.Scale) };   // the whole settings page
        int n = 0;
        async Task Still(string name, int settleMs = 700, RectInt32? area = null)
        {
            await Task.Delay(settleMs);
            await SaveAsync(win, area ?? rect, Path.Combine(outDir, $"p1-{++n:00}-{name}.png"));
        }
        Task<bool> Film(string name, Action act, int ms = 560, RectInt32? area = null) =>
            FilmAsync(win, area ?? strip, Path.Combine(outDir, $"film-{name}.png"), act, ms);

        var q = island.Queue;
        await SaveAsync(win, rect, Path.Combine(outDir, "p1-00-warmup.png"));   // the first capture in a process is slow
        island.DemoFlag(MusicDirector.LyricsFlag, false);
        island.DemoFlag(MusicDirector.ColoursFlag, true);
        await Still("rest");

        // A made-up song with a made-up warm cover.
        byte[] sunset = await CoverAsync(new[] { (255, 94, 58), (255, 176, 59), (156, 39, 90) }, "SUN");
        byte[] ocean = await CoverAsync(new[] { (28, 92, 214), (40, 200, 190), (88, 40, 170) }, "SEA");
        var start = DateTimeOffset.Now;
        NowPlaying Song(string title, string artist, bool playing, TimeSpan at) =>
            new("demo.player", "Demo Player", title, artist, "Made-Up Album", playing, true, true, false, RepeatMode.Off,
                new PlaybackClock(at, DateTimeOffset.Now, playing, TimeSpan.FromSeconds(214)));

        await Film("music-enter", () => island.DemoMusic(Song("Golden Static", "Lumen Drive", true, TimeSpan.FromSeconds(42)), sunset, null), 640);
        await Still("music-capsule", 300);
        await Film("track-change", () => island.DemoMusic(Song("Glass Tides", "Northbound Echo", true, TimeSpan.Zero), ocean, Lyrics), 800);
        await Still("track-changed", 200);

        // Lyrics: the island shows only the line being sung and sizes itself to it.
        island.DemoFlag(MusicDirector.LyricsFlag, true);
        island.DemoPosition(new PlaybackClock(TimeSpan.FromSeconds(4.2), DateTimeOffset.Now, true, TimeSpan.FromSeconds(214)));
        await Still("lyric-short", 500);
        await Film("lyric-change", () => island.DemoPosition(new PlaybackClock(TimeSpan.FromSeconds(6.05), DateTimeOffset.Now, true, TimeSpan.FromSeconds(214))), 640);
        await Still("lyric-long", 250);
        island.DemoPosition(new PlaybackClock(TimeSpan.FromSeconds(11.1), DateTimeOffset.Now, true, TimeSpan.FromSeconds(214)));
        await Still("lyric-wrapped", 700);
        island.DemoFlag(MusicDirector.LyricsFlag, false);
        await Still("lyrics-off", 700);

        // Hover depth: the content leans toward the pointer over still glows.
        island.DemoLean(1, 0);
        await Still("lean-right", 400);
        island.DemoLean(0, 0);

        await Film("panel-open", island.DemoExpand, 700, rect);
        await Still("panel-music", 300);

        // Settings: the panel grows into the full page; switches answer on the press.
        await Film("settings-open", () => island.DemoCommand("settings"), 760, rect);
        await Still("settings", 300, tall);
        island.DemoScrollSettings();
        await Still("settings-end", 300, tall);
        await Film("settings-back", () => island.DemoCommand("back"), 700, rect);
        await Film("panel-close", island.DemoCollapse, 600, rect);

        // Screen-share hiding (F134): with it on, our own screen capture (the same mechanism screen
        // sharing uses) must not see the island at all.
        island.DemoFlag("hide-from-capture", true);
        await Still("hidden-from-capture", 900);
        island.DemoFlag("hide-from-capture", false);
        await Still("visible-again", 900);

        // Battery: the gauge sweeps from empty to the level when charging starts.
        island.DemoPower(new PowerSnapshot(71, true, true, false, 30_000, 35_700, 50_300, 54_000));
        q.Post(Activity.Event(PowerRules.ChargingKey, "power", Priority.Ambient, "Charging", null, Glyphs.BatteryCharging(71), "Charging, 71 percent", Accent.Done), Clock.Now);
        await Film("charging", () => { }, 700);
        await Still("charging", 200);

        q.Post(Activity.Live("approval.git", "agent", Priority.ActNow, "Approve git push?", "main", Glyphs.Info, "Needs your approval: git push to main", Accent.NeedsYou), Clock.Now);
        await Still("approval-with-music-satellite");
        var buds = new BluetoothState("demo-buds", "Galaxy Buds3 Pro", DeviceKind.Audio, false, null);
        q.Post(DeviceRules.Change(buds, buds with { Connected = true, Battery = 95 })!, Clock.Now);
        await Still("burst-waiting");
        island.DemoMode(Mode.Presenting);
        await Still("presenting-dot");
        island.DemoMode(Mode.Normal);
        q.Remove("approval.git", Clock.Now);
        q.Remove(DeviceRules.Key("demo-buds"), Clock.Now);
        q.Remove(PowerRules.ChargingKey, Clock.Now);

        // Paused music keeps its place for a while, equaliser at rest, glows still.
        island.DemoMusic(Song("Glass Tides", "Northbound Echo", false, TimeSpan.FromSeconds(60)), ocean, Lyrics);
        await Still("paused", 900);
        island.DemoMusic(null, null, null);
        await Film("music-leave", () => { }, 640);
        await Still("rest-again", 300);
        await Task.Delay(200);
        watchdog.Dispose();
        Application.Current.Exit();
    }

    /// <summary>Made-up lyric lines (not from any real song), timed for the demo.</summary>
    const string Lyrics = """
        [00:00.00]
        [00:03.80]Lights on the water
        [00:06.00]Every wave remembers the shape of the shore it left behind
        [00:09.40]Hold on
        [00:11.00]We drift through the evening with the city humming softly underneath our feet tonight
        [00:16.00]
        """;

    /// <summary>Draws a made-up album cover: a three-colour gradient with soft circles and a word.</summary>
    static async Task<byte[]> CoverAsync((int R, int G, int B)[] colours, string word)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, 300, 300, 96);
        using (var draw = target.CreateDrawingSession())
        {
            Windows.UI.Color C((int R, int G, int B) c, byte a = 255) => Windows.UI.Color.FromArgb(a, (byte)c.R, (byte)c.G, (byte)c.B);
            using var gradient = new CanvasLinearGradientBrush(draw, C(colours[0]), C(colours[2])) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(300, 300) };
            draw.FillRectangle(0, 0, 300, 300, gradient);
            draw.FillCircle(210, 90, 80, C(colours[1], 210));
            draw.FillCircle(70, 230, 55, C(colours[0], 180));
            draw.DrawText(word, new Vector2(24, 240), Windows.UI.Color.FromArgb(230, 255, 255, 255),
                new CanvasTextFormat { FontSize = 40, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontFamily = "Segoe UI Variable Display" });
        }
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    static GraphicsCaptureItem? Item(IslandWindow win) => GraphicsCaptureItem.TryCreateFromDisplayId(new DisplayId { Value = win.DisplayIdValue });

    /// <summary>Saves one rectangle of the monitor (pixels, monitor-relative) as a PNG, from a composed frame.</summary>
    static async Task<bool> SaveAsync(IslandWindow win, RectInt32 rect, string path)
    {
        var device = CanvasDevice.GetSharedDevice();
        if (Item(win) is not { } item) return false;
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        Quiet(session);
        var done = new TaskCompletionSource<CanvasRenderTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (p, _) =>
        {
            using var frame = p.TryGetNextFrame();
            if (frame == null || done.Task.IsCompleted) return;
            try { done.TrySetResult(Crop(device, frame, rect)); }
            catch (Exception ex) { done.TrySetException(ex); }
        };
        session.StartCapture();
        if (await Task.WhenAny(done.Task, Task.Delay(3000)) != done.Task) return false;
        using var image = await done.Task;
        if (image == null) return false;
        await image.SaveAsync(path, CanvasBitmapFileFormat.Png);
        return true;
    }

    /// <summary>
    /// Records every composed frame for <paramref name="ms"/> after doing <paramref name="act"/>, keeps
    /// about a dozen evenly spread, and lays them out top to bottom with each frame's time beside it.
    /// </summary>
    static async Task<bool> FilmAsync(IslandWindow win, RectInt32 rect, string path, Action act, int ms)
    {
        var device = CanvasDevice.GetSharedDevice();
        if (Item(win) is not { } item) return false;
        var frames = new List<(double T, CanvasRenderTarget Image)>();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
        using var session = pool.CreateCaptureSession(item);
        Quiet(session);
        double started = 0;
        bool recording = false;
        pool.FrameArrived += (p, _) =>
        {
            using var frame = p.TryGetNextFrame();
            if (frame == null || !recording) return;
            try { lock (frames) frames.Add((Clock.Now - started, Crop(device, frame, rect))); } catch { }
        };
        session.StartCapture();
        await Task.Delay(250);                  // let the capture start before the change
        started = Clock.Now;
        recording = true;
        act();
        await Task.Delay(ms);
        recording = false;
        await Task.Delay(50);

        List<(double T, CanvasRenderTarget Image)> kept;
        lock (frames) kept = frames.Count <= 12 ? frames.ToList() : Enumerable.Range(0, 12).Select(i => frames[(int)Math.Round(i * (frames.Count - 1) / 11.0)]).ToList();
        if (kept.Count == 0) return false;
        int label = 64;
        using var sheet = new CanvasRenderTarget(device, rect.Width + label, rect.Height * kept.Count, 96);
        using (var draw = sheet.CreateDrawingSession())
        {
            draw.Clear(Windows.UI.Color.FromArgb(255, 24, 24, 24));
            for (int i = 0; i < kept.Count; i++)
            {
                draw.DrawImage(kept[i].Image, label, i * rect.Height);
                draw.DrawText($"{kept[i].T * 1000:0} ms", new Vector2(6, i * rect.Height + rect.Height / 2f - 9), Windows.UI.Color.FromArgb(255, 200, 200, 200),
                    new CanvasTextFormat { FontSize = 14, FontFamily = "Segoe UI" });
            }
        }
        await sheet.SaveAsync(path, CanvasBitmapFileFormat.Png);
        foreach (var (_, image) in frames) image.Dispose();
        return true;
    }

    static CanvasRenderTarget Crop(CanvasDevice device, Direct3D11CaptureFrame frame, RectInt32 rect)
    {
        using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
        var target = new CanvasRenderTarget(device, rect.Width, rect.Height, 96f);
        using (var drawing = target.CreateDrawingSession())
            drawing.DrawImage(bitmap, new Rect(0, 0, rect.Width, rect.Height), new Rect(rect.X, rect.Y, rect.Width, rect.Height));
        return target;
    }

    static void Quiet(GraphicsCaptureSession session)
    {
        try { session.IsCursorCaptureEnabled = false; session.IsBorderRequired = false; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException) { }
    }
}

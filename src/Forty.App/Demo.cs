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
        var watchdog = new Timer(_ => Environment.Exit(3), null, 240_000, Timeout.Infinite);
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

        // A made-up key in the demo's own folder, so the settings page has a key to list (F121).
        new SecretStore(Path.Combine(App.Home, "keys.bin")).Set("SARVAM_API_KEY", "made-up-demo-value");

        // Settings: the panel grows into the full page; switches answer on the press.
        await Film("settings-open", () => island.DemoCommand("settings"), 760, rect);
        await Still("settings", 300, tall);
        island.DemoScrollSettings();
        await Still("settings-end", 300, tall);
        await Film("settings-back", () => island.DemoCommand("back"), 700, rect);
        await Film("panel-close", island.DemoCollapse, 600, rect);
        await Task.Delay(500);

        // F12: a finger pulls the island open (the body follows it, then the release carries on), and
        // a second pull is let go too early with a flick back up, so it springs back.
        await Film("pull-open", () => _ = island.DemoPull(30, Enumerable.Range(1, 12).Select(i => 30.0 + i * 16).ToList()), 900, rect);
        island.DemoCollapse();
        await Task.Delay(700);
        await Film("pull-cancel", () => _ = island.DemoPull(30, Enumerable.Range(1, 8).Select(i => 30.0 + i * 14).Concat(Enumerable.Range(1, 5).Select(i => 142.0 - i * 18)).ToList()), 900, rect);
        await Task.Delay(400);

        // F11: a contrast theme turning on and off with the panel open: colours ease, content dips and returns.
        island.DemoExpand();
        await Task.Delay(700);
        await Film("contrast-on", () => island.DemoContrast(true), 520, rect);
        await Still("contrast-panel", 200);
        await Film("contrast-off", () => island.DemoContrast(null), 520, rect);
        island.DemoCollapse();
        await Task.Delay(600);

        // F8: moving between displays, shown here moving onto the same display: sliver, fade, grow.
        await Film("display-move", island.DemoMove, 700, rect);
        await Task.Delay(400);

        // F7: a mirror of the island on another display, shown here lower on the same display.
        island.DemoMirror(true, (int)(260 * win.Scale));
        await Still("mirror", 900, tall);
        island.DemoMirror(false, 0);

        // Screen-share hiding (F134): with it on, our own screen capture (the same mechanism screen
        // sharing uses) must not see the island at all.
        island.DemoFlag("hide-from-capture", true);
        await Still("hidden-from-capture", 900);
        island.DemoFlag("hide-from-capture", false);
        await Still("visible-again", 900);

        // F7: a display change re-places the island in place (here on the same display).
        island.DemoDisplaysChanged();
        await Still("after-display-change", 600);

        // Battery: the gauge sweeps from empty to the level when charging starts.
        island.DemoPower(new PowerSnapshot(71, true, true, false, 30_000, 35_700, 50_300, 54_000));
        q.Post(Activity.Event(PowerRules.ChargingKey, "power", Priority.Ambient, "Charging", null, Glyphs.BatteryCharging(71), "Charging, 71 percent", Accent.Done), Clock.Now);
        await Film("charging", () => { }, 700);
        await Still("charging", 200);
        // F4: the capsule's gauge and percentage travel into the battery panel.
        await Film("battery-panel-open", island.DemoExpand, 700, rect);
        island.DemoCollapse();
        await Task.Delay(600);

        q.Post(Activity.Live("approval.git", "agent", Priority.ActNow, "Approve git push?", "main", Glyphs.Info, "Needs your approval: git push to main", Accent.NeedsYou), Clock.Now);
        await Still("approval-with-music-satellite");
        await Film("approval-panel-open", island.DemoExpand, 700, rect);   // its glyph and "main" travel into the header
        island.DemoCollapse();
        await Task.Delay(600);
        var buds = new BluetoothState("demo-buds", "Galaxy Buds3 Pro", DeviceKind.Audio, false, null);
        q.Post(DeviceRules.Change(buds, buds with { Connected = true, Battery = 95 })!, Clock.Now);
        await Still("burst-waiting");
        island.DemoMode(Mode.Presenting);
        await Still("presenting-dot");
        island.DemoMode(Mode.Normal);
        q.Remove("approval.git", Clock.Now);
        q.Remove(DeviceRules.Key("demo-buds"), Clock.Now);
        q.Remove(PowerRules.ChargingKey, Clock.Now);
        await Task.Delay(900);

        // Speech (F116): dictation shows Listening with the voice's level, then writing, then typed.
        // These lines are what the speech process sends; no microphone is opened.
        void Marks(string at) => App.Log(outDir, $"marks at {at}: [{string.Join(", ", q.View.Marks.Select(m => m.Title))}] primary: {q.View.Primary?.Title} satellite: {q.View.Satellite?.Title} dots: {q.View.Dots.Count}; {island.DemoSpeechState}");
        await Film("dictation", () => island.DemoSpeech("listening"), 520);
        for (int i = 0; i < 16; i++)
        {
            island.DemoSpeech(FormattableString.Invariant($"level {0.3 + 0.6 * Math.Abs(Math.Sin(i * 0.9)):0.00}"));
            await Task.Delay(50);
        }
        await Still("dictation-listening", 0);
        Marks("listening");
        island.DemoSpeech("working");
        await Still("dictation-working", 400);
        island.DemoSpeech("done");
        await Still("dictation-typed", 300);
        await Task.Delay(1800);

        // Call notes: the question with its buttons; on Yes the recording mark pulls out of the music
        // capsule as the satellite (the liquid bridge), and is drawn back in when recording stops.
        await Film("bridge-out", () => island.DemoSpeech("recording Teams"), 760);
        await Still("call-recording-beside-music", 300);
        Marks("recording");
        island.DemoExpand();
        await Still("call-recording-panel", 1300);
        island.DemoCollapse();
        await Task.Delay(700);
        await Film("bridge-in", () => island.DemoSpeech("capture-stopped"), 600);
        await Task.Delay(500);
        island.DemoSpeech("call-offer Teams");
        await Still("call-offer", 600);
        island.DemoCommand("call-decline");
        await Task.Delay(500);
        island.DemoSpeech("call-done 2 to-dos");
        await Still("call-notes-ready", 600);
        q.Remove(SpeechSession.CallKey, Clock.Now);
        await Task.Delay(700);

        // The real pipe: a stand-in for the speech process (a hidden PowerShell) connects, reports a
        // recording, drops the connection while still running, then exits. The mark must stay until it has exited.
        if (island.DemoStartSpeech())
        {
            using var standIn = StandIn();
            await Task.Delay(2600);
            await Still("pipe-recording", 0);
            Marks("pipe connected");
            await Task.Delay(2600);
            await Still("pipe-dropped-still-running", 0);
            Marks("pipe dropped, process running");
            await Task.Delay(3600);
            await Still("pipe-process-exited", 0);
            Marks("process exited");
        }

        // Paused music keeps its place for a while, equaliser at rest, glows still.
        island.DemoMusic(Song("Glass Tides", "Northbound Echo", false, TimeSpan.FromSeconds(60)), ocean, Lyrics);
        await Still("paused", 900);
        island.DemoMusic(null, null, null);
        await Film("music-leave", () => { }, 640);
        await Still("rest-again", 300);
        await Task.Delay(200);
        watchdog.Dispose();
        Ambient.StopAll();
        Application.Current.Exit();
    }

    /// <summary>The nine states of the F2 review, in the order of the table in docs/transitions.md.</summary>
    public static readonly string[] States = { "rest", "notification", "approval", "music", "lyrics", "listening", "charging", "panel", "dot" };

    /// <summary>
    /// FORTY_DEMO=transitions: every change between the nine states, filmed (72 films, F2). Each film
    /// starts from a settled state and records every composed frame of the change; the review sheets
    /// and the table in docs/transitions.md are made from them.
    /// </summary>
    public static async Task TransitionsAsync(IslandWindow win, IslandController island, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var watchdog = new Timer(_ => Environment.Exit(3), null, 600_000, Timeout.Infinite);
        var rect = new RectInt32(win.WindowPx.X - win.MonitorPx.X, 0, win.WindowPx.Width, (int)(360 * win.Scale));
        var q = island.Queue;
        await SaveAsync(win, rect, Path.Combine(outDir, "warmup.png"));
        byte[] ocean = await CoverAsync(new[] { (28, 92, 214), (40, 200, 190), (88, 40, 170) }, "SEA");
        var song = new NowPlaying("demo.player", "Demo Player", "Glass Tides", "Northbound Echo", "Made-Up Album", true, true, true, false, RepeatMode.Off,
            new PlaybackClock(TimeSpan.FromSeconds(4.2), DateTimeOffset.Now, true, TimeSpan.FromSeconds(214)));
        var buds = new BluetoothState("demo-buds", "Galaxy Buds3 Pro", DeviceKind.Audio, false, null);
        island.DemoFlag(MusicDirector.ColoursFlag, true);
        island.DemoPower(new PowerSnapshot(71, true, true, false, 30_000, 35_700, 50_300, 54_000));

        // Puts the island in a state: whatever the last state put up is taken down in the same moment.
        string last = "rest";
        void Set(string state)
        {
            island.DemoCollapse();
            island.DemoMode(Mode.Normal);
            foreach (var key in new[] { "approval.git", DeviceRules.Key("demo-buds"), PowerRules.ChargingKey, SpeechSession.DictationKey, "news.dot" }) q.Remove(key, Clock.Now);
            island.DemoSpeech("idle");
            bool music = state is "music" or "lyrics" or "panel";
            // Opening the panel over lyrics keeps lyrics on, as it would for you; every other change sets it.
            if (!(state == "panel" && last == "lyrics")) island.DemoFlag(MusicDirector.LyricsFlag, state == "lyrics");
            last = state;
            if (music) island.DemoMusic(song with { Clock = new PlaybackClock(TimeSpan.FromSeconds(4.2), DateTimeOffset.Now, true, TimeSpan.FromSeconds(214)) }, ocean, Lyrics);
            else island.DemoMusic(null, null, null);
            switch (state)
            {
                case "notification": q.Post(DeviceRules.Change(buds, buds with { Connected = true, Battery = 95 })! with { Lifetime = TimeSpan.FromSeconds(30) }, Clock.Now); break;
                case "approval":
                    q.Post(Activity.Live("approval.git", "agent", Priority.ActNow, "Approve git push?", null, Glyphs.Info, "Needs your approval: git push to main", Accent.NeedsYou)
                        with { Actions = new ActivityActions("Approve", "noop", "Not now", "noop") }, Clock.Now);
                    break;
                case "listening": island.DemoSpeech("listening"); break;
                case "charging": q.Post(Activity.Event(PowerRules.ChargingKey, "power", Priority.Ambient, "Charging", null, Glyphs.BatteryCharging(71), "Charging, 71 percent", Accent.Done) with { Lifetime = TimeSpan.FromSeconds(30) }, Clock.Now); break;
                case "panel": island.DemoExpand(); break;
                case "dot":
                    island.DemoMode(Mode.Focus);
                    q.Post(Activity.Event("news.dot", "news", Priority.Ambient, "News", null, Glyphs.Info, "News") with { Lifetime = TimeSpan.FromSeconds(30) }, Clock.Now);
                    break;
            }
        }

        string? only = Environment.GetEnvironmentVariable("FORTY_TRANSITIONS_FROM");   // to film one row of the table
        foreach (string from in States)
            foreach (string to in States)
            {
                if (from == to || (only != null && from != only)) continue;
                Set(from);
                await Task.Delay(from is "music" or "lyrics" or "panel" ? 1300 : 900);
                await FilmAsync(win, rect, Path.Combine(outDir, $"t-{from}-{to}.png"), () => Set(to), 520);
            }
        watchdog.Dispose();
        Ambient.StopAll();
        Application.Current.Exit();
    }

    /// <summary>
    /// FORTY_DEMO=hold: a made-up song plays with its fluid colours and nothing else happens for 45 s,
    /// so the steady cost of music on screen can be measured from outside. Captures nothing.
    /// </summary>
    public static async Task HoldAsync(IslandController island)
    {
        byte[] ocean = await CoverAsync(new[] { (28, 92, 214), (40, 200, 190), (88, 40, 170) }, "SEA");
        island.DemoFlag(MusicDirector.ColoursFlag, Environment.GetEnvironmentVariable("FORTY_HOLD_COLOURS") != "0");
        island.DemoFlag(MusicDirector.LyricsFlag, false);
        bool playing = Environment.GetEnvironmentVariable("FORTY_HOLD_PAUSED") != "1";
        island.DemoMusic(new NowPlaying("demo.player", "Demo Player", "Glass Tides", "Northbound Echo", "Made-Up Album", playing, true, true, false, RepeatMode.Off,
            new PlaybackClock(TimeSpan.FromSeconds(30), DateTimeOffset.Now, playing, TimeSpan.FromSeconds(214))), ocean, null);
        await Task.Delay(10_000);
        App.Log(Environment.GetEnvironmentVariable("FORTY_OUT"), $"springs: {CompositionSpring.Created} created, {CompositionSpring.Running} running");
        await Task.Delay(35_000);
        Ambient.StopAll();
        Application.Current.Exit();
    }

    /// <summary>
    /// A stand-in speech process for the pipe check: connects, says it is recording, closes the
    /// connection after 3 s while staying alive, and exits 3 s later. It opens no audio.
    /// </summary>
    static System.Diagnostics.Process? StandIn()
    {
        const string script =
            "$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'forty-island', [System.IO.Pipes.PipeDirection]::InOut); " +
            "$p.Connect(3000); $w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true; " +
            "$w.WriteLine('recording Test'); Start-Sleep -Milliseconds 3000; $p.Dispose(); Start-Sleep -Milliseconds 3000";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script }) start.ArgumentList.Add(arg);
        return System.Diagnostics.Process.Start(start);
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
        string? error = null;
        int arrived = 0;
        pool.FrameArrived += (p, _) =>
        {
            using var frame = p.TryGetNextFrame();
            arrived++;
            if (frame == null || !recording) return;
            try { lock (frames) frames.Add((Clock.Now - started, Crop(device, frame, rect))); }
            catch (Exception ex) { error ??= $"{ex.GetType().Name} 0x{ex.HResult:X8}"; }
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
        App.Log(Path.GetDirectoryName(path), $"film {Path.GetFileName(path)}: {arrived} frames arrived, {frames.Count} kept{(error != null ? ", first error " + error : "")}");
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

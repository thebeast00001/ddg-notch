using Forty.Core;
using Forty.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Forty.App;

/// <summary>
/// FORTY_DEMO=layout: is everything centred where it should be? The island runs in a window placed
/// off every screen (nothing shows on your display, and nothing of yours is captured). Each element
/// that should sit centred in its box (an icon in its button, a label in its row, a lyric in the
/// island, the mark in the satellite) is rendered on its own, its ink is found, and the distance from
/// the ink's centre to the box's centre is written to layout.txt, in DIPs. The island's own window is
/// captured in each state (window-*.png), so the composed result can be checked as well.
/// </summary>
static class LayoutCheck
{
    static IslandWindow? _win;

    public static async Task RunAsync(IslandWindow win, IslandController island, string outDir)
    {
        _win = win;
        Directory.CreateDirectory(outDir);
        var report = new List<string> { "Ink centre minus box centre, in DIPs (positive: right, down). Within ±0.5 is centred.", "" };
        byte[] ocean = await Demo.CoverAsync(new[] { (28, 92, 214), (40, 200, 190), (88, 40, 170) }, "SEA");
        island.DemoFlag(MusicDirector.ColoursFlag, true);
        island.DemoFlag(MusicDirector.LyricsFlag, false);
        NowPlaying Song(bool playing, double at) => new("demo.player", "Demo Player", "Glass Tides", "Northbound Echo", "Made-Up Album", playing, true, true, false, RepeatMode.Off,
            new PlaybackClock(TimeSpan.FromSeconds(at), DateTimeOffset.Now, playing, TimeSpan.FromSeconds(214)));

        island.DemoMusic(Song(true, 30), ocean, Demo.Lyrics);
        await Measure("music capsule", island, report, outDir);

        island.DemoFlag(MusicDirector.LyricsFlag, true);
        island.DemoPosition(new PlaybackClock(TimeSpan.FromSeconds(4.2), DateTimeOffset.Now, false, TimeSpan.FromSeconds(214)));
        await Measure("lyric, one line", island, report, outDir);
        island.DemoPosition(new PlaybackClock(TimeSpan.FromSeconds(11.1), DateTimeOffset.Now, false, TimeSpan.FromSeconds(214)));
        await Measure("lyric, two lines", island, report, outDir);
        island.DemoFlag(MusicDirector.LyricsFlag, false);

        island.DemoSpeech("call-offer Teams");
        await Measure("question with buttons", island, report, outDir);
        island.DemoSpeech("recording Teams");
        await Measure("recording beside music", island, report, outDir);
        // F5: the drop held part-way out of the capsule, so the neck's colour can be checked frame by frame.
        foreach (double split in new[] { 0.2, 0.4, 0.6, 0.8 })
        {
            island.DemoPoseSplit(split);
            await Task.Delay(300);
            await Capture($"drop {split:0.0}", outDir, report);
        }
        island.DemoPoseSplit(1);

        island.DemoExpand();
        await Measure("music panel with recording", island, report, outDir);
        island.DemoCommand("settings");
        await Measure("settings", island, report, outDir);
        island.DemoCommand("back");
        island.DemoCollapse();

        island.DemoSpeech("capture-stopped");
        island.DemoMusic(null, null, null);
        island.DemoSpeech("listening");
        await Measure("listening", island, report, outDir);

        await File.WriteAllLinesAsync(Path.Combine(outDir, "layout.txt"), report);
        Ambient.StopAll();
        Application.Current.Exit();
    }

    static async Task Measure(string state, IslandController island, List<string> report, string outDir)
    {
        await Task.Delay(1600);   // everything settled
        report.Add($"── {state}");
        foreach (var probe in island.Surface.Probes())
        {
            if (probe.Ink.ActualWidth <= 0 || probe.Box.ActualWidth <= 0) continue;
            var ink = await InkBounds(probe.Ink);
            if (ink is not { } box) { report.Add($"  {probe.Name}: no ink"); continue; }
            var at = probe.Ink.TransformToVisual(probe.Box).TransformPoint(new Point(0, 0));
            double dx = at.X + box.X + box.Width / 2 - probe.Box.ActualWidth / 2;
            double dy = at.Y + box.Y + box.Height / 2 - probe.Box.ActualHeight / 2;
            string flag = Math.Abs(dy) > 0.5 || (probe.Both && Math.Abs(dx) > 0.5) ? "  <<" : "";
            // Text is centred on its letters (cap height to baseline), not on descenders like g and y.
            string body = "";
            if (probe.Ink is Microsoft.UI.Xaml.Controls.TextBlock text && text.BaselineOffset > 0)
            {
                int lines = Math.Max(1, (int)Math.Round(text.ActualHeight / Math.Max(1, text.LineHeight > 0 ? text.LineHeight : text.FontSize * 1.33)));
                double lastBaseline = text.BaselineOffset + (lines - 1) * (text.LineHeight > 0 ? text.LineHeight : text.FontSize * 1.33);
                double letters = at.Y + (box.Y + lastBaseline) / 2 - probe.Box.ActualHeight / 2;
                body = $"  letters dy {letters:+0.0;-0.0;0.0}";
                if (Math.Abs(letters) > 0.5) flag = "  <<"; else if (flag.Length > 0 && Math.Abs(dx) <= 0.5 || !probe.Both) flag = "";
            }
            var layoutAt = probe.Ink.ActualOffset;
            string where = $"layout y {layoutAt.Y:0.0}, moved {probe.Ink.Translation.Y:0.0}, size {probe.Ink.ActualWidth:0.0} × {probe.Ink.ActualHeight:0.0}";
            report.Add($"  {probe.Name}: {(probe.Both ? $"dx {dx,5:+0.0;-0.0;0.0}  " : "")}dy {dy,5:+0.0;-0.0;0.0}{body}   (ink {box.Width:0.0} × {box.Height:0.0} at {at.X + box.X:0.0},{at.Y + box.Y:0.0} in a {probe.Box.ActualWidth:0} × {probe.Box.ActualHeight:0} box; {where}){flag}");
        }
        await Capture(state, outDir, report);
    }

    /// <summary>The island as the compositor draws it (body, glows, blur and all), from its own window only.</summary>
    static async Task Capture(string state, string outDir, List<string> report)
    {
        string name = state.Replace(' ', '-').Replace(",", "");
        if (_win == null) return;
        var top = new Windows.Graphics.RectInt32(0, 0, _win.WindowPx.Width, (int)(Math.Min(_win.HeightDip, 420) * _win.Scale));
        if (!await Demo.SaveWindowAsync(_win, top, Path.Combine(outDir, $"window-{name}.png"))) report.Add($"  ({state}: window picture not captured)");
    }

    /// <summary>Where an element's visible pixels are, in its own DIPs, or null if it draws nothing.</summary>
    static async Task<Rect?> InkBounds(FrameworkElement element)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var buffer = await rtb.GetPixelsAsync();
        byte[] pixels = new byte[buffer.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        int w = rtb.PixelWidth, h = rtb.PixelHeight;
        if (w == 0 || h == 0) return null;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (pixels[(y * w + x) * 4 + 3] > 96)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) return null;
        double scale = w / element.ActualWidth;
        return new Rect(minX / scale, minY / scale, (maxX - minX + 1) / scale, (maxY - minY + 1) / scale);
    }
}

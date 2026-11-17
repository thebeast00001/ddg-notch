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

        // FORTY_LAYOUT=tools checks only the timer and the mirror; pages, only memory and the agents' pages.
        if (Environment.GetEnvironmentVariable("FORTY_LAYOUT") is "tools" or "pages")
        {
            await Task.Delay(1500);
            if (Environment.GetEnvironmentVariable("FORTY_LAYOUT") == "tools") await Tools(island, outDir, report);
            else await Pages(island, outDir, report);
            await File.WriteAllLinesAsync(Path.Combine(outDir, "layout.txt"), report);
            Ambient.StopAll();
            Application.Current.Exit();
            return;
        }

        // Forty at rest, in the expressions it uses, each held still for the picture.
        await Task.Delay(1500);
        foreach (var (name, expression, x, y, lid, asleep) in new (string, Expression, double, double, double, bool)[]
        {
            ("neutral", Expression.Neutral, 0, 0, 0, false), ("look left", Expression.Neutral, -1, 0.2, 0, false), ("look right", Expression.Neutral, 1, -0.3, 0, false),
            ("blinking", Expression.Neutral, 0, 0, 0.75, false), ("perk", Expression.Perk, 0.3, 0, 0, false), ("happy", Expression.Happy, 0, 0, 0, false),
            ("surprised", Expression.Surprised, 0, 0, 0, false), ("curious", Expression.Curious, 0, 0, 0, false), ("sad", Expression.Sad, 0, 0, 0, false),
            ("sleepy", Expression.Sleepy, 0, 0, 0, false), ("asleep", Expression.Asleep, 0, 0, 0, true),
        })
        {
            island.DemoHost(expression, x, y, lid, asleep);
            await Task.Delay(450);
            await CaptureRest($"forty {name}", outDir, report);
        }
        island.DemoHost(Expression.Neutral, 0, 0, 0);

        island.DemoMusic(Song(true, 30), ocean, Demo.Lyrics);
        await Measure("music capsule", island, report, outDir);

        island.DemoFlag(MusicDirector.LyricsFlag, true);
        island.DemoPosition(new PlaybackClock(TimeSpan.FromSeconds(4.2), DateTimeOffset.Now, false, TimeSpan.FromSeconds(214)));
        await Measure("lyric, one line", island, report, outDir);
        island.DemoPosition(new PlaybackClock(TimeSpan.FromSeconds(11.1), DateTimeOffset.Now, false, TimeSpan.FromSeconds(214)));
        await Measure("lyric, two lines", island, report, outDir);

        // The panel with lyrics beside the player, then the next line arriving (the column glides up).
        island.DemoExpand();
        await Task.Delay(1400);
        await Capture("music panel with lyrics", outDir, report);
        island.DemoPosition(new PlaybackClock(TimeSpan.FromSeconds(16.5), DateTimeOffset.Now, false, TimeSpan.FromSeconds(214)));
        await Task.Delay(900);
        await Capture("music panel with lyrics next line", outDir, report);
        island.DemoCollapse();
        await Task.Delay(900);
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
        // The play/pause glyph held at five points of its change, from the triangle to the bars.
        if (island.Surface.PlayGlyph is { } glyph)
        {
            foreach (double t in new[] { 0, 0.25, 0.5, 0.75, 1 })
            {
                glyph.Pose(t);
                await Task.Delay(200);
                await CaptureElement($"playpause {t:0.00}", glyph.Element.Parent as FrameworkElement ?? glyph.Element, 6, outDir, report);
            }
            glyph.Pose(1);
        }
        island.DemoCommand("settings");
        await Measure("settings", island, report, outDir);
        // A switch in the poses a hand puts it through: at rest, pressed (the knob stretches toward
        // where it will go), dragged half-way (the track's colour half-way too), pressed while on, on.
        if (island.Surface.Switches.FirstOrDefault() is { } toggle)
        {
            foreach (var (name, position, stretch) in new[] { ("off", 0.0, 0.0), ("off-pressed", 0, 1), ("half", 0.5, 1), ("on-pressed", 1, 1), ("on", 1, 0) })
            {
                toggle.Pose(position, stretch);
                await Task.Delay(200);
                await CaptureElement($"switch {name}", toggle.Button, 8, outDir, report);
            }
        }
        island.DemoCommand("back");
        await Task.Delay(900);

        // Phase 2: Echo takes the capsule while you dictate (through the real speech path): its face,
        // "Listening…", the microphone's mark and the dot matrix filling with a voice; then working; then done.
        island.DemoCollapse();
        await Task.Delay(900);
        island.DemoSpeech("listening");
        foreach (double level in new[] { 0.05, 0.2, 0.45, 0.8, 0.6, 0.3, 0.9, 0.5, 0.15 }) { island.DemoSpeech($"level {level.ToString(System.Globalization.CultureInfo.InvariantCulture)}"); await Task.Delay(60); }
        await Task.Delay(700);
        await Capture("echo listening", outDir, report);
        island.DemoSpeech("working");
        await Task.Delay(800);
        await Capture("echo working", outDir, report);
        island.DemoSpeech("done");
        await Task.Delay(600);
        await Capture("echo done", outDir, report);
        island.DemoSpeech("idle");
        await Task.Delay(2400);

        // Phase 2: Operator opening apps through the ask field, with a made-up Start menu and a launcher
        // that only records (nothing on this PC is opened), then the emergency pause.
        var opened = new List<string>();
        island.DemoApps(new[]
        {
            new InstalledApp("Notepad", "demo.notepad", AppKind.Packaged),
            new InstalledApp("Google Chrome", "demo.chrome", AppKind.Shortcut),
            new InstalledApp("Microsoft Edge", "demo.edge", AppKind.Shortcut),
            new InstalledApp("Spotify", "demo.spotify", AppKind.Packaged),
        }, opened);
        island.DemoExpand();
        await Task.Delay(900);
        await Capture("ask field", outDir, report);
        island.DemoAsk("open notepad");
        await Task.Delay(900);
        await Capture("ask open notepad", outDir, report);
        island.DemoCommandAnswer("open-once");
        await Task.Delay(900);
        await Capture("opened notepad once", outDir, report);
        island.DemoExpand();
        await Task.Delay(700);
        island.DemoAsk("open a browser");
        await Task.Delay(900);
        await Capture("ask which browser", outDir, report);
        island.DemoCommandAnswer("open-choice:1");
        await Task.Delay(700);
        island.DemoCommandAnswer("open-always");
        await Task.Delay(600);
        island.DemoExpand();
        await Task.Delay(700);
        island.DemoAsk("Scout, find the invoice from March");
        await Task.Delay(900);
        await Capture("ask scout", outDir, report);
        island.DemoCommandAnswer("pause-all");
        await Task.Delay(900);
        await Capture("paused", outDir, report);
        island.DemoAsk("open spotify");
        await Task.Delay(600);
        island.DemoCommandAnswer("resume-all");
        await Task.Delay(900);
        await Capture("resumed", outDir, report);
        report.Add($"── operator opened (made-up launcher): {string.Join(", ", opened)}; approved now: {string.Join(", ", island.ApprovedApps)}");

        // The brain, with a scripted model (nothing is sent anywhere) and a player that only records:
        // Forty thinking, Iris answering a question, a request nobody can do yet, and a spoken command.
        var mediaLog = new List<string>();
        island.DemoBrain(new ScriptedBrain(), mediaLog);
        island.DemoCollapse();
        await Task.Delay(800);
        island.DemoExpand();
        await Task.Delay(700);
        island.DemoAsk("what does HTTP stand for");
        await Task.Delay(500);
        await Capture("brain thinking", outDir, report);
        await Task.Delay(1600);
        await Capture("brain answer", outDir, report);
        island.DemoAsk("send an email to my manager");
        await Task.Delay(2200);
        await Capture("brain cannot", outDir, report);
        island.DemoCollapse();
        await Task.Delay(900);
        island.DemoSpeech("ask skip this song");
        await Task.Delay(200);
        await Capture("brain spoken command 0.2", outDir, report);
        await Task.Delay(400);
        await Capture("brain spoken command 0.6", outDir, report);
        await Task.Delay(900);
        await Capture("brain spoken command 1.5", outDir, report);
        report.Add($"── brain: the player was asked to {string.Join(", ", mediaLog)}");

        await Tools(island, outDir, report);
        await Pages(island, outDir, report);

        // Every agent in every state, captured three times 0.4 s apart so the working motions show at different points.
        island.DemoExpand();
        await Task.Delay(700);
        var sheet = island.Surface.DemoMarkSheet(28);
        await Task.Delay(600);
        for (int i = 0; i < 3; i++)
        {
            await CaptureElement($"agents {i}", sheet, 10, outDir, report);
            await Task.Delay(400);
        }
        island.DemoCollapse();

        island.DemoSpeech("capture-stopped");
        island.DemoMusic(null, null, null);
        island.DemoSpeech("listening");
        await Measure("listening", island, report, outDir);

        await File.WriteAllLinesAsync(Path.Combine(outDir, "layout.txt"), report);
        Ambient.StopAll();
        Application.Current.Exit();
    }

    /// <summary>
    /// The timer (setting it on the ruler, running, paused, in the capsule, ringing) and the mirror (with
    /// its stand-in picture: no camera is opened here, and no sound plays).
    /// </summary>
    static async Task Tools(IslandController island, string outDir, List<string> report)
    {
        island.DemoCollapse();
        await Task.Delay(800);
        island.DemoTimerPage(15);
        await Task.Delay(1200);
        await Capture("timer set 15", outDir, report);
        island.Surface.DemoRuler(25);
        await Task.Delay(600);
        await Capture("timer set 25", outDir, report);
        island.DemoCommand("timer-start:1500");
        await Task.Delay(1000);
        await Capture("timer running", outDir, report);
        island.DemoCommand("timer-pause");
        await Task.Delay(800);
        await Capture("timer paused", outDir, report);
        island.DemoCommand("timer-resume");
        island.DemoCollapse();
        await Task.Delay(1200);
        await Capture("timer capsule", outDir, report);
        await Task.Delay(1100);
        await Capture("timer capsule a second on", outDir, report);
        island.DemoExpand();
        await Task.Delay(1100);
        await Capture("panel with timer", outDir, report);
        island.DemoCollapse();
        await Task.Delay(800);
        island.DemoTimer("ring", TimeSpan.Zero);
        await Task.Delay(1000);
        await Capture("timer ringing capsule", outDir, report);
        island.DemoTimerPage(25);
        await Task.Delay(1300);
        await Capture("timer ringing page", outDir, report);
        island.DemoCommand("timer-stop");
        await Task.Delay(900);
        await Capture("timer after stop", outDir, report);
        island.DemoCollapse();
        await Task.Delay(800);

        island.DemoMirror();
        await Task.Delay(1300);
        await Capture("mirror", outDir, report);
        island.Surface.DemoMirror(MirrorState.Denied);
        await Task.Delay(500);
        await Capture("mirror camera off", outDir, report);
        island.DemoCollapse();
        await Task.Delay(800);

        island.DemoExpand();
        await Task.Delay(900);
        await Capture("ask field with tools", outDir, report);
        island.DemoAsk("set a timer for 10 minutes");
        await Task.Delay(900);
        await Capture("asked for a timer", outDir, report);
        island.DemoAsk("how long is left");
        await Task.Delay(700);
        await Capture("asked what is left", outDir, report);
        island.DemoCommand("timer-cancel");
        island.DemoCollapse();
        await Task.Delay(800);
        report.Add($"── tools: timer {island.TimerPhaseNow}, page {island.ToolNow ?? "none"}");
    }

    /// <summary>
    /// Memory and the agents' pages, with made-up apps (nothing opens) and made-up memories in the demo's
    /// own folder: remembering, a question asking permission, the answer, Bolt's suggestion card, a choice
    /// remembered, Settings with the agents, Bolt's and Iris's pages, the memory page (editing, forgetting),
    /// a skill switched off, and an agent set to keep its results for later.
    /// </summary>
    static async Task Pages(IslandController island, string outDir, List<string> report)
    {
        island.DemoResetAgents();
        var opened = new List<string>();
        island.DemoApps(new[]
        {
            new InstalledApp("Notepad", "demo.notepad", AppKind.Packaged),
            new InstalledApp("Google Chrome", "demo.chrome", AppKind.Shortcut),
            new InstalledApp("Microsoft Edge", "demo.edge", AppKind.Shortcut),
        }, opened);
        island.DemoCollapse();
        await Task.Delay(800);
        island.DemoExpand();
        await Task.Delay(900);
        island.DemoAsk("remember that my wife's name is Priya");
        await Task.Delay(800);
        await Capture("memory remembered", outDir, report);
        island.DemoAsk("remember that I prefer short answers");
        island.DemoAsk("remember I'm working from home for today");
        island.DemoAsk("what's my wife's name");
        await Task.Delay(800);
        await Capture("memory asks to use it", outDir, report);
        island.DemoCommand("data-on:scout:personal-memory");
        await Task.Delay(900);
        await Capture("memory answer", outDir, report);

        // Bolt: a choice, then the suggestion to remember it, then the same request needing no choice.
        island.DemoAsk("open a browser");
        await Task.Delay(700);
        island.DemoCommand("open-choice:1");
        await Task.Delay(500);
        island.DemoCommand("open-always");
        island.DemoCollapse();
        await Task.Delay(3200);
        await Capture("memory card", outDir, report);
        report.Add($"── pages: {island.Memory.Suggested(DateTimeOffset.Now).Count} suggestion(s) waiting at the card");
        if (island.Memory.Suggested(DateTimeOffset.Now).FirstOrDefault() is { } suggestion) island.DemoCommand("mem-yes:" + suggestion.Id);
        await Task.Delay(900);
        island.DemoExpand();
        await Task.Delay(700);
        island.DemoAsk("open a browser");
        await Task.Delay(900);
        report.Add($"── pages: opened {string.Join(", ", opened)}");

        island.DemoCommand("settings");
        await Task.Delay(1300);
        await Capture("settings with agents", outDir, report);
        island.DemoCommand("page:agent:operator");
        await Task.Delay(1000);
        await Capture("bolt page", outDir, report);
        island.DemoScrollSettings();
        await Task.Delay(400);
        await Capture("bolt page end", outDir, report);
        island.DemoCommand("back");
        await Task.Delay(700);
        island.DemoCommand("page:agent:scout");
        await Task.Delay(1000);
        await Capture("iris page", outDir, report);
        island.DemoCommand("page:memory");
        await Task.Delay(1000);
        await Capture("memory page", outDir, report);
        var priya = island.Memory.All(DateTimeOffset.Now).FirstOrDefault(i => i.Text.Contains("Priya", StringComparison.Ordinal));
        if (priya != null)
        {
            island.DemoCommand("mem-edit:" + priya.Id);
            await Task.Delay(700);
            await Capture("memory editing", outDir, report);
            island.DemoCommand("mem-cancel");
            island.DemoCommand("mem-forget:" + priya.Id);
            await Task.Delay(700);
            await Capture("memory forget asks", outDir, report);
            island.DemoCommand("mem-forget-yes:" + priya.Id);
            await Task.Delay(800);
            await Capture("memory forgot", outDir, report);
        }

        // A skill switched off is refused when asked for, with a way to turn it back on.
        island.DemoCommand("skill:open_app");
        island.DemoCollapse();
        await Task.Delay(800);
        island.DemoExpand();
        await Task.Delay(800);
        island.DemoAsk("open notepad");
        await Task.Delay(800);
        await Capture("skill off", outDir, report);
        island.DemoCommand("skill-on:open_app");
        await Task.Delay(600);
        await Capture("skill on again", outDir, report);
        island.DemoCommand("open-no");
        island.DemoCollapse();
        await Task.Delay(900);

        // Iris set to brief only: its answer doesn't take the capsule.
        island.DemoCommand("interrupt:scout:BriefOnly");
        island.DemoSpeech("ask what time is it");
        await Task.Delay(700);
        await Capture("brief only capsule", outDir, report);
        island.DemoResetAgents();
        report.Add($"── pages: opened {string.Join(", ", opened)}; remembered now {island.Memory.All(DateTimeOffset.Now).Count}");
    }

    static async Task Measure(string state, IslandController island, List<string> report, string outDir)
    {
        await Task.Delay(1600);   // everything settled
        report.Add($"── {state}{(island.Expanded ? " (panel open)" : "")}");
        foreach (var probe in island.Surface.Probes())
        {
            if (probe.Ink.ActualWidth <= 0 || probe.Box.ActualWidth <= 0) continue;
            Rect? ink;
            // A bitmap of an element that hosts compositor shapes (an agent's face) can't be taken; the
            // window captures show those instead.
            try { ink = await InkBounds(probe.Ink); }
            catch (ArgumentException) { report.Add($"  {probe.Name}: drawn by the compositor (see the window picture)"); continue; }
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
        var top = new Windows.Graphics.RectInt32(0, 0, _win.WindowPx.Width, _win.WindowPx.Height);
        if (!await Demo.SaveWindowAsync(_win, top, Path.Combine(outDir, $"window-{name}.png"))) report.Add($"  ({state}: window picture not captured)");
    }

    /// <summary>A model for the check: answers like the real router would, after a pause long enough to see Forty think.</summary>
    sealed class ScriptedBrain : IChatModel
    {
        public string Name => "Scripted";
        public bool OnThisPc => true;
        public async Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationToken cancel)
        {
            await Task.Delay(1500, cancel);
            string asked = messages[^1].Content;
            string json = asked.Contains("HTTP")
                ? "{\"agent\":\"iris\",\"skill\":\"answer\",\"args\":{\"text\":\"HTTP stands for Hypertext Transfer Protocol: the rules browsers and websites use to send pages to each other.\"}}"
                : "{\"agent\":\"none\",\"skill\":\"cannot\",\"args\":{\"text\":\"I can't send email yet. That comes with the calendar and mail agents.\"}}";
            return new ChatReply(json, 1.5, Name);
        }
    }

    /// <summary>The resting island alone (the pill and a little around it).</summary>
    static async Task CaptureRest(string state, string outDir, List<string> report)
    {
        if (_win == null) return;
        double s = _win.Scale, w = BodyShape.Rest.Width + 24, h = BodyShape.Rest.Height + 16;
        var rect = new Windows.Graphics.RectInt32((int)((_win.WidthDip - w) / 2 * s), 0, (int)(w * s), (int)((BodyShape.Rest.Top + h) * s));
        string name = state.Replace(' ', '-');
        if (!await Demo.SaveWindowAsync(_win, rect, Path.Combine(outDir, $"rest-{name}.png"))) report.Add($"  ({state}: picture not captured)");
    }

    /// <summary>One element as the compositor draws it, with <paramref name="pad"/> DIPs around it, from the island's own window.</summary>
    static async Task CaptureElement(string state, FrameworkElement element, double pad, string outDir, List<string> report)
    {
        if (_win == null) return;
        var at = element.TransformToVisual(null).TransformPoint(new Point(0, 0));
        double s = _win.Scale;
        var rect = new Windows.Graphics.RectInt32((int)Math.Floor((at.X - pad) * s), (int)Math.Floor((at.Y - pad) * s),
                                                  (int)Math.Ceiling((element.ActualWidth + 2 * pad) * s), (int)Math.Ceiling((element.ActualHeight + 2 * pad) * s));
        string name = state.Replace(' ', '-').Replace(",", "");
        if (!await Demo.SaveWindowAsync(_win, rect, Path.Combine(outDir, $"element-{name}.png"))) report.Add($"  ({state}: picture not captured)");
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

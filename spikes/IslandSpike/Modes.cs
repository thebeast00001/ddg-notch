using System.Globalization;
using System.Text;
using IslandSpike.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace IslandSpike;

/// <summary>
/// What the spike can be asked to do (set FORTY_SPIKE):
///   run        the island you can hover and click; right-click quits
///   selfcheck  S1: window styles, focus, click-through, screenshots
///   torture    S2: 200 scripted interruptions plus drag releases, measured from the screen
///   calibrate  S2: what Composition's built-in spring actually does
/// </summary>
static class Modes
{
    public static string OutDir = "";
    static readonly List<object> s_keepAlive = new();

    static readonly Windows.UI.Color Ink = Windows.UI.Color.FromArgb(255, 0, 0, 0);
    static readonly Windows.UI.Color Marker = Windows.UI.Color.FromArgb(255, 255, 0, 255);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Calibration constant for the built-in spring: Period = K / omega.</summary>
    static double K => double.TryParse(Environment.GetEnvironmentVariable("FORTY_K"), NumberStyles.Float, Inv, out double k) ? k : 1.0;

    static Func<Compositor, double, MotionValue> Engine(string name) => name switch
    {
        "a" => (c, x) => new NaturalMotionValue(c, x, K, carryVelocity: true),
        "b" => (c, x) => new ExprMotionValue(c, x),
        _ => (c, x) => new AdditiveMotionValue(c, x),
    };

    static string EngineName(string name) => name switch
    {
        "a" => "A (built-in spring, velocity from the mirror)",
        "b" => "B (closed-form expression, replaced on each change)",
        _ => "C (additive springs)",
    };

    public static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(OutDir, "spike.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
        catch { }
    }

    // ───────────────────────────── run ─────────────────────────────

    public static void Interactive(string engine) => StartIsland(engine);

    static Interaction StartIsland(string engine)
    {
        var win = new IslandWindow(Island.WindowWidth, Island.WindowHeight);

        // Last resort. The window checks and repairs its own always-on-top flag (see
        // IslandWindow.EnsureTopmost), and that has not been seen to fail. If it ever does, a fresh
        // process is the remaining option, a few times at most.
        if (!win.IsTopmost)
        {
            int attempt = int.TryParse(Environment.GetEnvironmentVariable("FORTY_RELAUNCH"), out int n) ? n : 0;
            Log($"started without always-on-top (attempt {attempt})");
            if (attempt < 3 && Environment.ProcessPath is { } path)
            {
                var again = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false };
                again.Environment["FORTY_RELAUNCH"] = (attempt + 1).ToString(Inv);
                System.Diagnostics.Process.Start(again);
                Environment.Exit(0);
            }
        }

        var island = new Island(win, Engine(engine), Ink, hairline: true);
        island.SnapRegionTo(Shape.Rest);
        var watcher = new ForegroundWatcher(win.MonitorHandle);
        var interaction = new Interaction(island, watcher);
        var pipe = new IslandPipe(DispatcherQueue.GetForCurrentThread());
        pipe.Line += interaction.OnLine;
        interaction.Send = pipe.Send;
        s_keepAlive.AddRange(new object[] { win, island, watcher, interaction, pipe });
        win.Show();
        interaction.ApplyFront(watcher.State);
        Log($"run: engine {engine}, scale {win.Scale.ToString(Inv)}, front {watcher.FrontClass} = {watcher.State}");
        return interaction;
    }

    /// <summary>
    /// For checking the dictation display without a microphone: runs the island, saves a picture a
    /// moment after each state arrives over the pipe, and quits when the other side says "bye".
    /// </summary>
    public static async Task DictationDemo()
    {
        var interaction = StartIsland("c");
        var win = interaction.Island.Win;
        var shot = new RectInt32(win.WindowPx.X - win.MonitorPx.X, 0, win.WindowPx.Width, (int)(70 * win.Scale));
        var finished = new TaskCompletionSource();
        var pending = new List<Task>();
        var counts = new Dictionary<string, int>();

        interaction.Shown += state =>
        {
            if (state == "bye") { finished.TrySetResult(); return; }
            int delay = state switch { "listening" => 1300, "working" => 450, "done" => 520, "failed" => 400, _ => 900 };
            counts[state] = counts.GetValueOrDefault(state) + 1;
            string path = Path.Combine(OutDir, $"s7-{state}-{counts[state]}.png");
            pending.Add(Task.Delay(delay).ContinueWith(_ => ScreenProbe.SaveRegionAsync(win, shot, path)).Unwrap());
        };

        await Task.WhenAny(finished.Task, Task.Delay(40_000));
        await Task.WhenAll(pending);
        await Task.Delay(300);
        win.Hide();
    }

    /// <summary>
    /// For checking call notes in the island without touching the mouse: runs the island, answers
    /// "Yes" by itself a moment after the question appears, presses "Stop" by itself nine seconds
    /// into the recording, and saves a picture of each state.
    /// </summary>
    public static async Task CallDemo()
    {
        var interaction = StartIsland("c");
        var win = interaction.Island.Win;
        var shot = new RectInt32(win.WindowPx.X - win.MonitorPx.X, 0, win.WindowPx.Width, (int)(70 * win.Scale));
        var finished = new TaskCompletionSource();
        var pending = new List<Task>();
        Task Picture(string name, int afterMs) =>
            Task.Delay(afterMs).ContinueWith(_ => ScreenProbe.SaveRegionAsync(win, shot, Path.Combine(OutDir, $"s8-{name}.png"))).Unwrap();

        // The first picture a process takes is slow (the capture machinery starts up), so take one
        // that does not matter before any that do.
        await ScreenProbe.SaveRegionAsync(win, shot, Path.Combine(OutDir, "s8-warmup.png"));

        interaction.Shown += async state =>
        {
            switch (state)
            {
                case "call-offer":
                    await Picture("ask", 900);   // the question is photographed before it is answered
                    await Task.Delay(500);
                    interaction.SimulateAccept();
                    pending.Add(Picture("recording", 3300));
                    await Task.Delay(9000);
                    interaction.SimulateStop();
                    pending.Add(Picture("working", 450));
                    break;
                case "call-done":
                    pending.Add(Picture("done", 800));
                    await Task.Delay(2600);
                    finished.TrySetResult();
                    break;
                case "call-failed":
                    pending.Add(Picture("failed", 400));
                    await Task.Delay(1500);
                    finished.TrySetResult();
                    break;
            }
        };

        await Task.WhenAny(finished.Task, Task.Delay(70_000));
        await Task.WhenAll(pending);
        await Task.Delay(300);
        win.Hide();
    }

    /// <summary>Hover, dwell, click and leave, as MOTION.md describes them, plus the dictation and call displays.</summary>
    sealed class Interaction
    {
        enum CallMode { None, Asking, Recording, Finishing }

        readonly Island _island;
        readonly ForegroundWatcher _watcher;
        readonly DictationView _view;
        readonly CallView _call;
        readonly DispatcherQueueTimer _dwell, _leave;
        bool _inside, _expanded;
        bool _busy;          // an activity (dictation or a call) owns the island; hover and click wait
        int _activity;
        bool _resting;       // a final state (done, failed) is on screen and will return to rest by itself
        CallMode _callMode;

        /// <summary>Sends a line to the speech process. Set by whoever owns the pipe.</summary>
        public Action<string> Send { get; set; } = _ => { };
        Windows.Foundation.Point _anchor;   // where the pointer was when it entered, or when an activity ended
        bool _armed;                        // the pointer has moved on purpose since then

        public Island Island => _island;

        /// <summary>Raised after a state from the speech process has been put on screen.</summary>
        public event Action<string>? Shown;

        Shape RestShape => _watcher.State == FrontState.Maximized ? Shape.Sliver : Shape.Rest;

        public Interaction(Island island, ForegroundWatcher watcher)
        {
            _island = island;
            _watcher = watcher;
            _view = new DictationView(island);
            _call = new CallView(island);
            _call.Accepted += Accept;
            _call.Declined += Decline;
            _call.StopRequested += StopRecording;
            _call.OpenRequested += OpenNotes;
            var queue = DispatcherQueue.GetForCurrentThread();

            _dwell = queue.CreateTimer();
            _dwell.Interval = TimeSpan.FromMilliseconds(80);
            _dwell.IsRepeating = false;
            _dwell.Tick += (_, _) => { if (_inside && !_expanded) Expand(); };

            _leave = queue.CreateTimer();
            _leave.Interval = TimeSpan.FromMilliseconds(300);
            _leave.IsRepeating = false;
            _leave.Tick += (_, _) => { if (!_inside && _expanded) Collapse(); };

            var root = island.Win.Root;
            root.PointerEntered += OnEntered;
            root.PointerMoved += OnMoved;
            root.PointerExited += OnExited;
            root.PointerPressed += OnPressed;
            watcher.Changed += ApplyFront;
        }

        void OnEntered(object sender, PointerRoutedEventArgs e)
        {
            _inside = true;
            _leave.Stop();
            _anchor = e.GetCurrentPoint(_island.Win.Root).Position;
            _armed = false;
            if (_expanded || _busy) return;
            _island.GoTo(Shape.Peek, Spring.Press, Clock.Now);   // the instant answer
        }

        /// <summary>
        /// The expand waits for intent, and intent means the pointer actually moved. If the island
        /// grew or changed under a pointer that was simply resting there, nothing opens.
        /// </summary>
        void OnMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_inside || _armed || _expanded || _busy) return;
            var at = e.GetCurrentPoint(_island.Win.Root).Position;
            double dx = at.X - _anchor.X, dy = at.Y - _anchor.Y;
            if (dx * dx + dy * dy < 9) return;   // less than 3 DIPs: not a deliberate move
            _armed = true;
            _dwell.Start();
        }

        void OnExited(object sender, PointerRoutedEventArgs e)
        {
            _inside = false;
            _armed = false;
            _dwell.Stop();
            if (_busy) return;
            if (_expanded) _leave.Start();
            else _island.GoTo(RestShape, Spring.Quick, Clock.Now);
        }

        void OnPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(_island.Win.Root).Properties.IsRightButtonPressed)
            {
                Application.Current.Exit();
                return;
            }
            if (_busy) return;
            _dwell.Stop();
            if (_expanded) Collapse(); else Expand();   // a click never waits
        }

        /// <summary>One line from the speech process: a state, or the current voice level.</summary>
        public void OnLine(string line)
        {
            string[] parts = line.Split(' ', 2);
            if (parts[0].StartsWith("call-", StringComparison.Ordinal) || parts[0] is "recording" or "gone")
            {
                OnCallLine(parts[0], parts.Length == 2 ? parts[1] : "");
                Shown?.Invoke(parts[0]);
                return;
            }
            if (_callMode != CallMode.None) return;   // a call owns the island; dictation is not drawn over it
            switch (parts[0])
            {
                case "level":
                    if (_busy && parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, Inv, out double level)) _view.SetLevel(level);
                    return;
                case "listening":
                    _busy = true;
                    _activity++;
                    _expanded = false;
                    _dwell.Stop();
                    _leave.Stop();
                    if (!_island.Win.IsVisible) _island.Win.Show();   // listening is always shown
                    _call.Hide();   // "Notes ready" may still be up from a call that just ended
                    _island.GoTo(Shape.Listen, Spring.Quick, Clock.Now);
                    _view.ShowListening();
                    break;
                case "working":
                    if (_busy) _view.ShowWorking();
                    break;
                case "done":
                    if (_busy) { _view.ShowDone(); _ = RestAfter(800); }
                    break;
                case "failed":
                    if (_busy) { _view.ShowFailed(); _ = RestAfter(900); }
                    break;
                case "idle":
                    if (_busy) _ = RestAfter(0);
                    break;
            }
            Shown?.Invoke(parts[0]);
        }

        // ── Call notes ──

        void OnCallLine(string kind, string argument)
        {
            switch (kind)
            {
                case "call-offer":
                    if (_callMode != CallMode.None) return;
                    Claim();
                    _callMode = CallMode.Asking;
                    _view.Hide();
                    _island.GoTo(Shape.Ask, Spring.Move, Clock.Now);
                    _call.ShowAsk(argument);
                    _ = DeclineIfUnanswered(_activity);
                    break;
                case "call-gone":
                    if (_callMode == CallMode.Asking) EndCall();
                    break;
                case "recording":
                    ShowRecording();
                    break;
                case "call-working":
                    ShowFinishing();
                    break;
                case "call-done":
                    if (_callMode == CallMode.None) return;
                    _callMode = CallMode.None;
                    if (argument.Length == 0)
                    {
                        _call.Hide();
                        _view.ShowDone();
                        _ = RestAfter(1200);
                    }
                    else
                    {
                        // "Notes ready  3 to-dos  [Open]": long enough to read and reach, then it goes by itself.
                        _view.Hide();
                        _island.GoTo(Shape.Ready, Spring.Quick, Clock.Now);
                        _call.ShowReady(argument);
                        _ = RestAfter(7000);
                    }
                    break;
                case "call-failed":
                    if (_callMode == CallMode.None) return;
                    _callMode = CallMode.None;
                    _call.Hide();
                    _island.GoTo(Shape.Listen, Spring.Quick, Clock.Now);
                    _view.ShowFailed();
                    _ = RestAfter(1500);
                    break;
                case "gone":   // the speech process went away
                    if (_callMode != CallMode.None) EndCall();
                    else if (_busy && !_resting) _ = RestAfter(0);   // a "done" already on screen is left to finish
                    break;
            }
        }

        /// <summary>Takes the island for an activity: no hover, no click-to-expand until it ends.</summary>
        void Claim()
        {
            _busy = true;
            _resting = false;
            _activity++;
            _expanded = false;
            _dwell.Stop();
            _leave.Stop();
            if (!_island.Win.IsVisible) _island.Win.Show();
        }

        void ShowRecording()
        {
            if (_callMode == CallMode.Recording) return;
            if (!_busy) Claim();
            _callMode = CallMode.Recording;
            _view.Hide();
            _island.GoTo(Shape.Recording, Spring.Quick, Clock.Now);
            _call.ShowRecording();
        }

        void ShowFinishing()
        {
            if (_callMode is CallMode.None or CallMode.Finishing) return;
            _callMode = CallMode.Finishing;
            _call.Hide();
            _island.GoTo(Shape.Listen, Spring.Quick, Clock.Now);
            _view.ShowWorking();
        }

        void EndCall()
        {
            _callMode = CallMode.None;
            _call.Hide();
            _ = RestAfter(0);
        }

        // The three answers. Each shows its result at once and tells the speech process.
        void Accept()
        {
            if (_callMode != CallMode.Asking) return;
            Send("accept");
            ShowRecording();
        }

        void Decline()
        {
            if (_callMode != CallMode.Asking) return;
            Send("decline");
            EndCall();
        }

        void StopRecording()
        {
            if (_callMode != CallMode.Recording) return;
            Send("stop");
            ShowFinishing();
        }

        /// <summary>Asks the speech process to open the notes it just saved, and puts the island back to rest.</summary>
        void OpenNotes()
        {
            if (!_call.ReadyVisible) return;
            Send("open-notes");
            _resting = false;
            _ = RestAfter(0);
        }

        /// <summary>A question nobody answers is a no. It goes away by itself and nothing is recorded.</summary>
        async Task DeclineIfUnanswered(int activity)
        {
            await Task.Delay(15_000);
            if (activity == _activity && _callMode == CallMode.Asking) Decline();
        }

        public void SimulateAccept() => Accept();
        public void SimulateStop() => StopRecording();

        async Task RestAfter(int milliseconds)
        {
            int activity = _activity;
            if (milliseconds > 0)
            {
                _resting = true;
                await Task.Delay(milliseconds);
                if (activity == _activity) _resting = false;
            }
            if (activity != _activity || !_busy) return;   // a new utterance began meanwhile
            _busy = false;
            _armed = false;   // a pointer resting here must move again before anything opens
            _view.Hide();
            _call.Hide();
            _island.GoTo(_inside ? Shape.Peek : RestShape, Spring.Quick, Clock.Now);
            if (_watcher.State == FrontState.FullScreen) _island.Win.Hide();
        }

        void Expand()
        {
            _expanded = true;
            _island.GoTo(Shape.Panel, Spring.Move, Clock.Now);
        }

        void Collapse()
        {
            _expanded = false;
            _island.GoTo(_inside ? Shape.Peek : RestShape, new Spring(1.0, 0.32), Clock.Now);
        }

        public void ApplyFront(FrontState state)
        {
            var win = _island.Win;
            win.EnsureTopmost();   // cheap: reads one flag, and acts only if it has gone missing
            if (_busy) return;   // an activity is showing; it settles the shape when it ends
            if (state == FrontState.FullScreen) { win.Hide(); return; }
            if (!win.IsVisible) win.Show();
            if (!_expanded && !_inside) _island.GoTo(RestShape, Spring.Quick, Clock.Now);
        }
    }

    // ─────────────────────── always-on-top investigation ───────────────────────

    /// <summary>
    /// Creates the island's window exactly as the island does, never shows it, and writes one line
    /// saying whether the first request for always-on-top took, whether the repair was needed, and
    /// how it ended. Run a few hundred times, this measures both the problem and the fix.
    /// </summary>
    public static void TopmostProbe()
    {
        uint idle = Native.IdleMilliseconds();
        var win = new IslandWindow(Island.WindowWidth, Island.WindowHeight);
        string line = $"{win.TopmostMode} first {(win.TopmostFirstTry ? "took" : "FAILED")} repairs {win.TopmostRepairs} final {(win.IsTopmost ? "ON" : "OFF")} idle {idle}";
        if (!win.TopmostFirstTry) line += " | " + win.ZOrderAtFailure;
        File.AppendAllText(Path.Combine(OutDir, "topmost-probe.txt"), line + "\n");
    }

    // ─────────────────────────── selfcheck (S1) ───────────────────────────

    public static async Task SelfCheck()
    {
        var report = new StringBuilder("S1 overlay window self-check\n");
        void Line(string name, bool pass, string detail = "") =>
            report.AppendLine($"  [{(pass ? "pass" : "FAIL")}] {name}{(detail.Length > 0 ? "  — " + detail : "")}");

        IntPtr frontBefore = Native.GetForegroundWindow();
        string frontClass = Native.ClassName(frontBefore);
        uint idleAtStart = Native.IdleMilliseconds();

        var win = new IslandWindow(Island.WindowWidth, Island.WindowHeight);
        var island = new Island(win, Engine("c"), Ink, hairline: true);
        island.SnapRegionTo(Shape.Rest);

        win.Show();
        await Task.Delay(1200);
        report.AppendLine($"  always-on-top: first request {(win.TopmostFirstTry ? "took" : "DID NOT TAKE")}, repairs needed {win.TopmostRepairs}, input idle at start {idleAtStart} ms");

        double s = win.Scale;
        report.AppendLine($"  monitor {win.MonitorPx.Width}x{win.MonitorPx.Height} px at {s.ToString("P0", Inv)}; window {win.WindowPx.Width}x{win.WindowPx.Height} px at ({win.WindowPx.X},{win.WindowPx.Y})");

        Line("window is visible", win.IsVisible);
        Line("showing it did not take focus", Native.GetForegroundWindow() == frontBefore, $"front window class stayed {frontClass}");

        long ex = win.ExStyle;
        Line("never activated by the pointer (WS_EX_NOACTIVATE)", (ex & Native.WS_EX_NOACTIVATE) != 0);
        Line("absent from Alt-Tab and the taskbar (tool window, not app window)",
             (ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0 && !win.Window.AppWindow.IsShownInSwitchers);
        Line("always on top (WS_EX_TOPMOST)", (ex & Native.WS_EX_TOPMOST) != 0);

        int cx = win.WindowPx.X + win.WindowPx.Width / 2;
        bool Ours(double dxDip, double yDip)
        {
            var point = new Native.POINT { X = cx + (int)Math.Round(dxDip * s), Y = win.MonitorPx.Y + (int)Math.Round(yDip * s) };
            return Native.GetAncestor(Native.WindowFromPoint(point), Native.GA_ROOT) == win.Hwnd;
        }

        // At rest: the pill is 148 x 32, hanging 6 below the top edge.
        Line("rest: the pill itself receives the pointer", Ours(0, 22));
        Line("rest: 60 DIPs beside the pill belongs to the app underneath", !Ours(74 + 60, 22) && !Ours(-(74 + 60), 22));
        Line("rest: 100 DIPs below the pill belongs to the app underneath", !Ours(0, 140));

        // See-through test: a pixel inside the window's region but outside the pill should show the
        // same thing as its neighbour just outside the region.
        {
            int row = (int)Math.Round(22 * s);
            int regionLeft = win.WindowPx.X - win.MonitorPx.X + win.RegionPx.X;
            int regionRight = regionLeft + win.RegionPx.Width;
            var colours = await ScreenProbe.ReadPixelsAsync(win, new[]
            {
                (regionLeft + 3, row), (regionLeft - 4, row), (regionRight - 4, row), (regionRight + 3, row),
            });
            bool Close((byte B, byte G, byte R) a, (byte B, byte G, byte R) b) =>
                Math.Abs(a.B - b.B) <= 6 && Math.Abs(a.G - b.G) <= 6 && Math.Abs(a.R - b.R) <= 6;
            bool clear = colours != null && Close(colours[0], colours[1]) && Close(colours[2], colours[3]);
            string seen = colours == null ? "no frame" : $"inside {colours[0].R},{colours[0].G},{colours[0].B} vs outside {colours[1].R},{colours[1].G},{colours[1].B}";
            Line($"the window is see-through around the pill (recipe: {win.TransparencyRecipe})", clear, seen);
        }

        var shot = new RectInt32(win.WindowPx.X - win.MonitorPx.X, 0, win.WindowPx.Width, (int)(260 * s));
        bool shotRest = await ScreenProbe.SaveRegionAsync(win, shot, Path.Combine(OutDir, "s1-rest.png"));

        island.GoTo(Shape.Panel, Spring.Move, Clock.Now);
        await Task.Delay(1100);
        Line("panel: its middle receives the pointer", Ours(0, 110));
        Line("panel: 40 DIPs beside it belongs to the app underneath", !Ours(210 + 40, 110));
        Line("morphing did not take focus", Native.GetForegroundWindow() == frontBefore);
        bool shotPanel = await ScreenProbe.SaveRegionAsync(win, shot, Path.Combine(OutDir, "s1-panel.png"));

        island.GoTo(Shape.Sliver, Spring.Quick, Clock.Now);
        await Task.Delay(1100);
        Line("sliver: 30 DIPs below the top edge belongs to the app underneath", !Ours(0, 30));
        bool shotSliver = await ScreenProbe.SaveRegionAsync(win, shot, Path.Combine(OutDir, "s1-sliver.png"));

        Line("screenshots saved", shotRest && shotPanel && shotSliver, "s1-rest.png, s1-panel.png, s1-sliver.png");

        var front = ForegroundWatcher.Classify(frontBefore, win.MonitorHandle, out string cls);
        report.AppendLine($"  front window right now: class {cls} is {front} -> the island would rest as {(front == FrontState.FullScreen ? "hidden" : front == FrontState.Maximized ? "a sliver" : "the pill")}");
        report.AppendLine("  not checked here: a second monitor, other scaling factors, virtual desktops, a real full-screen app");

        File.WriteAllText(Path.Combine(OutDir, "s1-selfcheck.txt"), report.ToString());
        win.Hide();
    }

    // ─────────────────────────── torture (S2) ───────────────────────────

    enum StepKind { Morph, DragStart, DragEnd }
    readonly record struct Step(double At, StepKind Kind, Shape Shape, Spring Spring);

    public static async Task Torture(string engine, bool aligned)
    {
        const double dragSpeed = 900;   // DIPs per second, a brisk pull
        string name = $"s2-torture-{engine}-{(aligned ? "aligned" : "free")}";

        IntPtr frontBefore = Native.GetForegroundWindow();
        var win = new IslandWindow(Island.WindowWidth, Island.WindowHeight);
        var island = new Island(win, Engine(engine), Marker, hairline: false);
        win.ClearRegion();
        win.Show();
        await Task.Delay(900);

        try { await Windows.Graphics.Capture.GraphicsCaptureAccess.RequestAccessAsync(Windows.Graphics.Capture.GraphicsCaptureAccessKind.Borderless); }
        catch { }
        using var probe = new ScreenProbe(win, Shape.Rest.Top + 16);
        await Task.Delay(400);

        // 200 interruptions at random moments, 30 to 300 ms apart, between random shapes with random springs.
        var random = new Random(40);
        var shapes = new[] { Shape.Rest, Shape.Capsule, Shape.Panel, Shape.Full };
        var springs = new[] { Spring.Move, Spring.Quick, Spring.Thrown, Spring.Sheet };
        var script = new List<Step>();
        double at = 0.5;
        int lastShape = 0;
        for (int i = 0; i < 200; i++)
        {
            at += 0.03 + random.NextDouble() * 0.27;
            int next;
            do { next = random.Next(shapes.Length); } while (next == lastShape);
            lastShape = next;
            script.Add(new Step(at, StepKind.Morph, shapes[next], springs[random.Next(springs.Length)]));
        }
        // Five drags: the height follows a "pointer" 1:1, then is let go at speed.
        for (int i = 0; i < 5; i++)
        {
            at += 1.0; script.Add(new Step(at, StepKind.Morph, Shape.Rest, Spring.Move));
            at += 1.0; script.Add(new Step(at, StepKind.DragStart, Shape.Rest, default));
            at += 0.30; script.Add(new Step(at, StepKind.DragEnd, Shape.Panel, Spring.Sheet));
        }
        double endAt = at + 1.5;

        var releases = new HashSet<int>();
        int index = 0;
        bool dragging = false;
        double dragStart = 0;
        var finished = new TaskCompletionSource();
        double start = Clock.Now;
        probe.Recording = true;

        void Tick()
        {
            double now = Clock.Now, elapsed = now - start;
            while (index < script.Count && script[index].At <= elapsed)
            {
                var step = script[index++];
                switch (step.Kind)
                {
                    case StepKind.Morph:
                        island.GoTo(step.Shape, step.Spring, now, manageRegion: false);
                        break;
                    case StepKind.DragStart:
                        dragging = true;
                        dragStart = now;
                        break;
                    case StepKind.DragEnd:
                        dragging = false;
                        releases.Add(island.CommitTimes.Count);
                        island.GoTo(step.Shape, step.Spring, now, manageRegion: false, heightVelocity: dragSpeed);
                        break;
                }
            }
            if (dragging) island.H.SetDirect(Shape.Rest.H + dragSpeed * (now - dragStart), now);
            if (elapsed >= endAt) finished.TrySetResult();
        }

        EventHandler<object> onRendering = (_, _) => Tick();
        DispatcherQueueTimer? timer = null;
        if (aligned)
        {
            CompositionTarget.Rendering += onRendering;   // act on the frame tick
        }
        else
        {
            timer = DispatcherQueue.GetForCurrentThread().CreateTimer();   // act whenever, like real input
            timer.Interval = TimeSpan.FromMilliseconds(4);
            timer.Tick += (_, _) => Tick();
            timer.Start();
        }

        await finished.Task;
        if (aligned) CompositionTarget.Rendering -= onRendering; else timer!.Stop();
        await Task.Delay(300);
        probe.Recording = false;

        var frames = probe.Snapshot();
        var title = $"S2 motion torture test — engine {EngineName(engine)}, {(aligned ? "changes made on the frame tick" : "changes made at arbitrary moments")}";
        var all = Analysis.Run(title, frames, island.W, island.H, win.Scale, island.CommitTimes, releases, Path.Combine(OutDir, name + ".csv"));

        var text = new StringBuilder(all.Text);
        text.AppendLine($"  focus stayed with the app in front: {Native.GetForegroundWindow() == frontBefore}");
        text.AppendLine($"  capture errors: {probe.Errors}{(probe.FirstError != null ? " — " + probe.FirstError.Split('\n')[0] : "")}");
        File.WriteAllText(Path.Combine(OutDir, name + ".txt"), text.ToString());
        win.Hide();
    }

    // ─────────────────────────── calibrate (S2) ───────────────────────────

    public static async Task Calibrate()
    {
        var win = new IslandWindow(Island.WindowWidth, Island.WindowHeight);
        var island = new Island(win, (c, x) => new NaturalMotionValue(c, x, 1.0, carryVelocity: false), Marker, hairline: false);
        win.ClearRegion();
        win.Show();
        await Task.Delay(900);
        try { await Windows.Graphics.Capture.GraphicsCaptureAccess.RequestAccessAsync(Windows.Graphics.Capture.GraphicsCaptureAccessKind.Borderless); }
        catch { }
        using var probe = new ScreenProbe(win, Shape.Rest.Top + 16);
        await Task.Delay(400);
        probe.Recording = true;

        var props = island.W.Props;
        var compositor = island.Compositor;
        const double lo = 148, hi = 448;

        double Start(double target, double zeta, double period, float? initialVelocity)
        {
            var spring = compositor.CreateSpringScalarAnimation();
            spring.DampingRatio = (float)zeta;
            spring.Period = TimeSpan.FromSeconds(period);
            if (initialVelocity is float v) spring.InitialVelocity = v;
            spring.FinalValue = (float)target;
            double now = Clock.Now;
            props.StartAnimation("v", spring);
            return now;
        }

        double e1 = Start(hi, 1.0, 0.060, null); await Task.Delay(2200);
        Start(lo, 1.0, 0.060, null); await Task.Delay(2200);
        double e2 = Start(hi, 0.5, 0.060, null); await Task.Delay(3000);
        Start(lo, 0.5, 0.060, null); await Task.Delay(3000);
        double e3 = Start(hi, 1.0, 0.030, null); await Task.Delay(1800);
        Start(lo, 1.0, 0.030, null); await Task.Delay(1800);
        double e4 = Start(hi, 1.0, 0.060, null); await Task.Delay(120);
        double e4r = Start(lo, 1.0, 0.060, null); await Task.Delay(2200);
        double e5 = Start(lo, 1.0, 0.060, 800f); await Task.Delay(2200);
        probe.Recording = false;

        var frames = probe.Snapshot().Where(f => f.WidthPx > 0).OrderBy(f => f.T).ToList();
        var report = new StringBuilder("S2 calibration: what Composition's built-in spring actually does\n");
        if (frames.Count < 100)
        {
            report.AppendLine($"  not enough frames ({frames.Count}); capture errors {probe.Errors} {probe.FirstError}");
            File.WriteAllText(Path.Combine(OutDir, "s2-calibrate.txt"), report.ToString());
            win.Hide();
            return;
        }
        var lags = frames.Select(f => f.Arrival - f.T).OrderBy(x => x).ToList();
        double shift = Math.Abs(lags[lags.Count / 2]) > 0.5 ? lags[lags.Count / 2] : 0;
        double scale = win.Scale;

        List<(double T, double X)> Window(double from, double to) =>
            frames.Where(f => f.T + shift >= from && f.T + shift <= to).Select(f => (f.T + shift, f.WidthPx / scale)).ToList();

        // Fit the natural frequency (and the pipeline delay) of one step from rest.
        (double Omega, double Delay, double Rms) FitStep(double t0, double zeta, double seconds, double delayFrom, double delayTo)
        {
            var points = Window(t0 - 0.05, t0 + seconds);
            double bestOmega = 0, bestDelay = 0, best = double.MaxValue;
            for (int i = 0; i < 400; i++)
            {
                double omega = 3 * Math.Pow(200, i / 399.0);
                var segment = new SpringSegment(0, lo, 0, hi, new Spring(zeta, 2 * Math.PI / omega));
                for (double delay = delayFrom; delay <= delayTo; delay += 0.001)
                {
                    double sse = 0;
                    foreach (var (t, x) in points)
                    {
                        double tau = t - delay - t0;
                        double model = tau <= 0 ? lo : segment.Eval(tau).X;
                        sse += (x - model) * (x - model);
                    }
                    if (sse < best) { best = sse; bestOmega = omega; bestDelay = delay; }
                }
            }
            return (bestOmega, bestDelay, Math.Sqrt(best / Math.Max(1, points.Count)));
        }

        var f1 = FitStep(e1, 1.0, 1.8, -0.02, 0.12);
        report.AppendLine(string.Create(Inv, $"  E1 damping 1.0, Period 60 ms: omega = {f1.Omega:F2} rad/s, delay {f1.Delay * 1000:F0} ms, fit rms {f1.Rms:F2} DIP"));
        report.AppendLine(string.Create(Inv, $"     omega x Period = {f1.Omega * 0.060:F3}   (1.0 would mean Period is 1/omega; 6.283 would mean Period is a full cycle)"));

        var f3 = FitStep(e3, 1.0, 1.4, -0.02, 0.12);
        report.AppendLine(string.Create(Inv, $"  E3 damping 1.0, Period 30 ms: omega = {f3.Omega:F2} rad/s, delay {f3.Delay * 1000:F0} ms, fit rms {f3.Rms:F2} DIP"));
        report.AppendLine(string.Create(Inv, $"     omega x Period = {f3.Omega * 0.030:F3}"));

        // E2: is DampingRatio the textbook damping ratio? Fit it freely.
        {
            var points = Window(e2 - 0.05, e2 + 2.6);
            double bestZeta = 0, bestOmega = 0, bestDelay = 0, best = double.MaxValue;
            for (double zeta = 0.20; zeta <= 0.999; zeta += 0.02)
                for (int i = 0; i < 240; i++)
                {
                    double omega = 3 * Math.Pow(200, i / 239.0);
                    var segment = new SpringSegment(0, lo, 0, hi, new Spring(zeta, 2 * Math.PI / omega));
                    for (double delay = f1.Delay - 0.012; delay <= f1.Delay + 0.012; delay += 0.003)
                    {
                        double sse = 0;
                        foreach (var (t, x) in points)
                        {
                            double tau = t - delay - e2;
                            double model = tau <= 0 ? lo : segment.Eval(tau).X;
                            sse += (x - model) * (x - model);
                        }
                        if (sse < best) { best = sse; bestZeta = zeta; bestOmega = omega; bestDelay = delay; }
                    }
                }
            double peak = points.Count > 0 ? points.Max(p => p.X) : 0;
            report.AppendLine(string.Create(Inv, $"  E2 damping 0.5, Period 60 ms: best fit damping {bestZeta:F2}, omega {bestOmega:F2} rad/s, fit rms {Math.Sqrt(best / Math.Max(1, points.Count)):F2} DIP"));
            report.AppendLine(string.Create(Inv, $"     omega x Period = {bestOmega * 0.060:F3}; peak {peak:F1} DIP (target {hi}, so overshoot {100 * (peak - hi) / (hi - lo):F1}%; textbook for 0.5 is 16.3%)"));
        }

        // E4: when a running spring is replaced without an InitialVelocity, does the new one inherit the speed?
        {
            var first = new SpringSegment(e4, lo, 0, hi, new Spring(1.0, 2 * Math.PI / f1.Omega));
            var (xr, vr) = first.Eval(e4r);
            var points = Window(e4r, e4r + 1.5);
            double Fit(double v0)
            {
                double best = double.MaxValue;
                var segment = new SpringSegment(0, xr, v0, lo, new Spring(1.0, 2 * Math.PI / f1.Omega));
                for (double delay = f1.Delay - 0.012; delay <= f1.Delay + 0.012; delay += 0.001)
                {
                    double sse = 0;
                    foreach (var (t, x) in points)
                    {
                        double tau = t - delay - e4r;
                        double model = tau <= 0 ? xr : segment.Eval(tau).X;
                        sse += (x - model) * (x - model);
                    }
                    best = Math.Min(best, sse);
                }
                return Math.Sqrt(best / Math.Max(1, points.Count));
            }
            double peak = points.Count > 0 ? points.Max(p => p.X) : 0;
            report.AppendLine(string.Create(Inv, $"  E4 replaced mid-flight at {xr:F0} DIP moving {vr:F0} DIP/s, no InitialVelocity given:"));
            report.AppendLine(string.Create(Inv, $"     fit if the speed is dropped to zero  rms {Fit(0):F2} DIP"));
            report.AppendLine(string.Create(Inv, $"     fit if the speed is carried over     rms {Fit(vr):F2} DIP"));
            report.AppendLine(string.Create(Inv, $"     highest width seen after the replacement {peak:F1} DIP (it was at {xr:F1} when replaced)"));
        }

        // E5: units of InitialVelocity. Target equals the current value, so all motion comes from the kick.
        {
            var points = Window(e5 + f1.Delay, e5 + f1.Delay + 1.5);
            double num = 0, den = 0, peak = 0;
            foreach (var (t, x) in points)
            {
                double tau = t - f1.Delay - e5;
                double basis = 800 * tau * Math.Exp(-f1.Omega * tau);
                num += (x - lo) * basis; den += basis * basis;
                peak = Math.Max(peak, x - lo);
            }
            double expectedPeak = 800 / (Math.E * f1.Omega);
            report.AppendLine(string.Create(Inv, $"  E5 InitialVelocity 800 with nowhere to go: peak displacement {peak:F1} DIP (units-per-second would give {expectedPeak:F1}); scale factor {(den > 0 ? num / den : 0):F3}"));
        }

        report.AppendLine($"  frames {frames.Count}, capture errors {probe.Errors}");
        File.WriteAllText(Path.Combine(OutDir, "s2-calibrate.txt"), report.ToString());
        win.Hide();
    }
}

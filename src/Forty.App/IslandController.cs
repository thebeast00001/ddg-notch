using Forty.Core;
using Forty.Motion;
using Forty.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Forty.App;

/// <summary>
/// Joins everything: Windows adapters post activities to the queue; the queue decides what shows;
/// this class turns each decision into a morph of the body and a layered change of its content, and
/// turns your pointer and keyboard into expand, collapse, hold and lean (MOTION.md 5 and 6).
/// </summary>
sealed class IslandController : IDisposable
{
    readonly IslandWindow _win;
    readonly Body _body;
    readonly IslandSurface _surface;
    readonly MusicDirector _music;
    readonly DispatcherQueue _ui;
    readonly DispatcherQueueTimer _dwell, _leave, _deadline;

    public ActivityQueue Queue { get; } = new();
    public IslandSurface Surface => _surface;
    public PowerAdapter Power { get; } = new();
    public MediaAdapter Media { get; } = new();
    public FocusAdapter Focus { get; } = new();
    public ScreenAdapter Screen { get; }
    public BluetoothAdapter Bluetooth { get; } = new();
    public AudioDeviceAdapter Audio { get; } = new();
    public SpeechLink Speech { get; } = new();
    public IReadOnlyList<Adapter> Adapters => new Adapter[] { Power, Media, Focus, Screen, Bluetooth, Audio, Speech };
    public Meter Meter { get; }

    readonly FeatureFlags _flags;
    readonly Settings _settings;
    bool _settingsOpen;
    readonly ChargeEstimator _estimator = new();
    readonly DeviceNewsFilter _deviceNews = new();
    readonly SpeechSession _speech = new();
    string? _lastLookWhy;
    readonly SecretStore _keys = new(Path.Combine(App.Home, "keys.bin"));
    readonly DisplayFollower _follower;
    readonly Dictionary<string, MirrorIsland> _mirrors = new();
    readonly DispatcherQueueTimer _followCheck;
    bool _moving;
    bool? _helloReady;
    readonly HashSet<string> _marksShown = new();
    string? _policySent;
    PowerSnapshot? _lastPower;
    double _lastDeviceConnect = double.NegativeInfinity;
    Timer? _resourceSampler, _keyScanner;
    KeyCheck? _keyCheck;
    sealed record KeyCheck(KeyScan.Result Result, DateTime At, int Keys);
    TimeSpan _lastCpu;
    double _lastSampleAt;
    long _lastBluetoothEvents;
    string? _lookApplied;
    bool _flowing;

    bool _inside, _armed, _expanded, _touch;
    Windows.Foundation.Point _anchor;
    IntPtr _focusOwner;          // who had keyboard focus before the shortcut opened the panel
    PanelModel? _lastPanel;

    /// <summary>Paused music stays in the island this long, then it rests.</summary>
    static readonly TimeSpan PausedStay = TimeSpan.FromSeconds(20);

    /// <summary>Raised after a view has been put on screen; the demo photographs it.</summary>
    public event Action<string>? Shown;

    public IslandController(IslandWindow win, FeatureFlags flags, Settings settings, Meter meter)
    {
        _win = win;
        _flags = flags;
        _settings = settings;
        Meter = meter;
        _ui = DispatcherQueue.GetForCurrentThread();
        _body = new Body(win);
        _surface = new IslandSurface(win, _body);
        _body.Moved += () => FramePacing.Moving(Meter, 0.6);
        _music = new MusicDirector(flags, new LyricsClient(Path.Combine(App.Home, "lyrics")));
        Screen = new ScreenAdapter(win.MonitorHandle);

        _dwell = Timer(80, () => { if (_inside && !_expanded) Expand(takeFocus: false); });
        _leave = Timer(300, () => { if (!_inside && _expanded && _focusOwner == IntPtr.Zero) Collapse(); });
        _deadline = Timer(0, () => Queue.Tick(Clock.Now));
        _follower = new DisplayFollower(win.DisplayIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _followCheck = Timer(0, () => OnFrontWindow(Screen.FrontWindow));

        Queue.Changed += (_, view) => Render(view);
        _music.Changed += () => { SyncMusicActivity(); Render(Queue.View); };
        _music.LineChanged += () => { if (!_expanded && _music.LyricsActive && Queue.View.Primary?.Key == MediaAdapter.ActivityKey) Render(Queue.View); };
        _music.PositionChanged += clock => { if (_expanded) _surface.UpdateProgress(clock); };
        _surface.Command += OnCommand;

        var root = win.Root;
        root.PointerEntered += OnEntered;
        root.PointerMoved += OnMoved;
        root.PointerExited += OnExited;
        root.PointerPressed += OnPressed;
        root.PointerReleased += (_, e) => { if (_pull != null) { EndPull(); e.Handled = true; } };
        root.PointerCanceled += (_, _) => EndPull(cancelled: true);
        root.PointerCaptureLost += (_, _) => EndPull(cancelled: true);
        root.KeyDown += OnKey;
        win.Hotkey += () => { if (_expanded) Collapse(); else Expand(takeFocus: true); };
    }

    /// <summary>Runs work on the UI thread, guarded (see <see cref="Guard"/>).</summary>
    bool Ui(Action work) => _ui.TryEnqueue(() => Guard.Run(work, work.Method.Name));

    DispatcherQueueTimer Timer(int milliseconds, Action tick)
    {
        var timer = _ui.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Guard.Run(tick, "timer");
        return timer;
    }

    /// <summary>Starts the adapters. Their events arrive on Windows' threads and are moved to the UI thread here.</summary>
    public void Start(bool adapters = true)
    {
        foreach (var agent in Cast.All) { var a = agent; _roster[a].Changed += () => Ui(() => UpdateAgent(a)); }
        ApplyLook();
        _flags.Changed += () => Ui(() => { _music.OnFlagsChanged(); ApplyLook(); });
        _win.SettingsChanged += () => Ui(ApplyLook);
        _settings.Changed += () => Ui(() => Render(Queue.View));
        _win.DisplaysChanged += () => Ui(PlaceOnDisplay);
        _flags.Changed += () => Ui(() => { if (!_flags.IsOn("follow-display")) PlaceOnDisplay(); else SyncMirrors(); });
        _win.Show();
        SyncMirrors();   // after the island shows, so the mirrors show with it
        if (adapters) _win.RegisterHotkey();   // a scripted demo never takes your global shortcut (a press anywhere would toggle its panel)
        if (_flags.IsOn("diagnostics")) StartResourceSampler();
        // F121: half a minute after starting, and once a day after that, every file Forty wrote is
        // checked for key text, which is cut out if found. Off the UI thread; only counts are kept.
        _keyScanner = new Timer(_ => Guard.Run(ScanForKeys, "key scan"), null, TimeSpan.FromSeconds(30), TimeSpan.FromDays(1));
        // Warm the surface once, out of sight, while nothing is showing.
        bool warmUp = Environment.GetEnvironmentVariable("FORTY_WARM") != "0";   // FORTY_WARM=0 is for measuring what the warm-up costs
        var warm = Timer(1200, () => { if (warmUp && Queue.View.Primary == null && !_expanded) _surface.Warm(); _ = HelloReadyAsync(); });
        warm.Start();
        if (!adapters) return;

        Power.Changed += snapshot => Ui(() => OnPower(snapshot));
        Power.ModeChanged += _ => Ui(() => { UpdateMode(); ApplyLook(); SendPolicy(); });
        Media.Changed += playing => Ui(() => OnMedia(playing));
        Media.PositionChanged += clock => Ui(() => _music.OnPosition(clock));
        Focus.Changed += _ => Ui(UpdateMode);
        Screen.Changed += () => Ui(UpdateMode);
        Screen.FrontWindowChanged += hwnd => Ui(() => OnFrontWindow(hwnd));
        Bluetooth.Changed += (before, after) => Ui(() => OnBluetooth(before, after));
        Audio.OutputChanged += (name, worn) => Ui(() => OnOutput(name, worn));
        ListenToSpeech();
        foreach (var adapter in Adapters) adapter.Start();

        _lastPower = Power.Snapshot;
        OnMedia(Media.Current);
        UpdateMode();
    }

    // ── Displays (F7, F8) ──

    static string Id(Microsoft.UI.Windowing.DisplayArea area) => area.DisplayId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Displays were added, removed or rescaled, or a taskbar moved: the island goes where it belongs
    /// (the main display, or the one you're using when following is on) at that display's scaling,
    /// in place, with no animation, because the screen itself just changed.
    /// </summary>
    void PlaceOnDisplay()
    {
        var all = Microsoft.UI.Windowing.DisplayArea.FindAll();
        var present = new List<Microsoft.UI.Windowing.DisplayArea>();
        for (int i = 0; i < all.Count; i++) present.Add(all[i]);   // indexed: enumerating this list fails in some Windows App SDK versions
        var main = Microsoft.UI.Windowing.DisplayArea.Primary;
        var active = IslandWindow.DisplayOf(Screen.FrontWindow);
        string target = DisplayRules.Choose(_flags.IsOn("follow-display"), Id(main), active == null ? null : Id(active),
                                            _win.DisplayIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture), present.Select(Id).ToList());
        var area = present.FirstOrDefault(a => Id(a) == target) ?? main;
        _win.Place(area);
        _body.Relayout();
        Screen.WatchMonitor(_win.MonitorHandle);
        _follower.PlacedOn(Id(area));
        _lastPanel = null;
        SyncMirrors();
        Render(Queue.View);
    }

    /// <summary>
    /// "Show on every display" (F7): a mirror of the island on each display other than the one the
    /// island is on. Following the display you're using (F8) and showing everywhere exclude each other.
    /// </summary>
    void SyncMirrors(Microsoft.UI.Windowing.DisplayArea? demoArea = null, int demoOffsetPx = 0)
    {
        bool every = _flags.IsOn("every-display") && !_flags.IsOn("follow-display");
        var all = Microsoft.UI.Windowing.DisplayArea.FindAll();
        var present = new List<Microsoft.UI.Windowing.DisplayArea>();
        for (int i = 0; i < all.Count; i++) present.Add(all[i]);
        string main = _win.DisplayIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var wanted = every ? present.Where(a => Id(a) != main).ToList() : new();
        if (demoArea != null) wanted.Add(demoArea);
        foreach (var (id, mirror) in _mirrors.ToList())
            if (!wanted.Any(a => Id(a) == id)) { mirror.Dispose(); _mirrors.Remove(id); }
        foreach (var area in wanted)
        {
            if (_mirrors.ContainsKey(Id(area))) continue;
            var mirror = new MirrorIsland(_win, area, App.WindowWidth, App.WindowHeight, demoArea == area ? demoOffsetPx : 0);
            mirror.Pressed += m => Ui(() => BringHere(m.DisplayId));
            _mirrors[Id(area)] = mirror;
            if (_win.IsVisible) mirror.Show();
        }
    }

    /// <summary>A mirror was pressed: the island itself comes to that display, and a mirror takes its old place.</summary>
    void BringHere(string displayId)
    {
        var all = Microsoft.UI.Windowing.DisplayArea.FindAll();
        for (int i = 0; i < all.Count; i++)
            if (Id(all[i]) == displayId) { _ = MoveTo(all[i]); return; }
    }

    /// <summary>
    /// Follow the display you're using (F8, off by default): once the window you're working in has
    /// been on another display for 300 ms, the island shrinks to the sliver and fades here, and grows
    /// from the sliver there. It never slides across the gap between screens, and never moves while
    /// you are using it.
    /// </summary>
    void OnFrontWindow(IntPtr front)
    {
        _followCheck.Stop();
        if (!_flags.IsOn("follow-display") || _moving) return;
        var area = IslandWindow.DisplayOf(front);
        string? active = area == null ? null : Id(area);
        if (_follower.Observe(active, Clock.Now, busy: _expanded || _inside) != null && area != null)
        {
            _ = MoveTo(area);
            return;
        }
        if (_follower.NextCheck is { } due)
        {
            _followCheck.Interval = TimeSpan.FromSeconds(Math.Max(0.01, due - Clock.Now + 0.01));
            _followCheck.Start();
        }
    }

    async Task MoveTo(Microsoft.UI.Windowing.DisplayArea area)
    {
        _moving = true;
        try
        {
            var root = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_win.Root);
            _contentVersion++;
            _surface.HideCapsule();
            _surface.HideLyrics();
            _body.ShowSatellite(false);
            _body.GoTo(BodyShape.Sliver, Springs.Quick);
            Fade(root, 0, 160);
            await Task.Delay(170);
            _win.Place(area);
            _body.Relayout();
            _body.Jump(BodyShape.Sliver);
            Screen.WatchMonitor(_win.MonitorHandle);
            _follower.PlacedOn(Id(area));
            SyncMirrors();
            Fade(root, 1, 120);
            _lastPanel = null;
            Render(Queue.View);   // grows from the sliver into whatever it should show
        }
        finally { _moving = false; }
    }

    void Fade(Microsoft.UI.Composition.Visual visual, float to, int ms)
    {
        var c = visual.Compositor;
        var animation = c.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, to);
        animation.Duration = TimeSpan.FromMilliseconds(MotionPolicy.Reduced ? 1 : ms);
        visual.StartAnimation("Opacity", animation);
    }

    PowerPolicy Policy => PowerPolicy.For(Power.Snapshot, Power.Mode is EffectivePowerMode.BatterySaver or EffectivePowerMode.BetterBattery);

    /// <summary>The look that depends on settings: contrast theme (F11), Glass (S6), screen-share hiding (F134).</summary>
    void ApplyLook()
    {
        bool contrast = _demoContrast ?? (Native.HighContrastOn() || Environment.GetEnvironmentVariable("FORTY_CONTRAST") == "1");
        bool themeChanged = contrast != Palette.Contrast;
        _body.SetContrast(contrast);   // the body's colours ease over 300 ms
        bool wantGlass = (_flags.IsOn("glass") || Environment.GetEnvironmentVariable("FORTY_GLASS") == "1") && Policy.Glass && !contrast;
        _body.SetGlass(wantGlass);
        _win.SetExcludedFromCapture(_flags.IsOn("hide-from-capture"));
        _lookApplied = null;
        // A theme change (F11): the content dips out for the first 120 ms, is rebuilt in the new
        // colours, and is back by the end, an open panel included. Otherwise it is redrawn in place.
        if (themeChanged) _surface.Retint(() => { _lastPanel = null; Render(Queue.View); });
        else Render(Queue.View);
    }

    // ── Sources ──

    void OnBluetooth(BluetoothState? before, BluetoothState after)
    {
        if (_expanded) Render(Queue.View);   // the panel's device chips follow the change
        if (!_flags.IsOn("bluetooth") || _settings.IsQuiet(after.Id) || !_deviceNews.Allows(before, after, Clock.Now)) return;
        if (DeviceRules.Change(before, after) is not { } activity) return;
        if (before is { Connected: false } && after.Connected) _lastDeviceConnect = Clock.Now;
        Queue.Post(activity, Clock.Now);
    }

    /// <summary>"Now using …" is skipped right after a Bluetooth connect, which already said the same thing.</summary>
    void OnOutput(string name, bool worn)
    {
        if (!_flags.IsOn("audio-output") || Clock.Now - _lastDeviceConnect < 6) return;
        Queue.Post(DeviceRules.OutputChanged(name, worn), Clock.Now);
    }

    void OnPower(PowerSnapshot snapshot)
    {
        _estimator.Add(Clock.Now, snapshot);
        foreach (var activity in PowerRules.Changes(_lastPower, snapshot, _settings.Alerts, _estimator.TimeToFull(Clock.Now)))
            Queue.Post(activity, Clock.Now);
        _lastPower = snapshot;
        // Reports arrive every few seconds on battery. Only a change of mode (low battery) or an open
        // panel (its battery section) needs drawing; anything else the queue already redraws.
        bool low = snapshot is { Percent: < ModeResolver.LowBatteryPercent, PluggedIn: false };
        if (low != _lowBattery) { _lowBattery = low; UpdateMode(); }
        else if (_expanded) Render(Queue.View);
        SendPolicy();
    }

    bool _lowBattery;

    /// <summary>
    /// The speech process (F116, F128): what it reports becomes activities and marks; it is told the
    /// power policy and asked for its state whenever it connects.
    /// </summary>
    void ListenToSpeech()
    {
        Speech.Line += line => Ui(() => OnSpeechLine(line));
        Speech.Connected += () => Ui(() => { _policySent = null; SendPolicy(); Speech.Send("state?"); });
        Speech.Lost += running => Ui(() => { OnSpeech(running ? _speech.LinkLost(Clock.Now) : _speech.ProcessGone()); if (!running) _roster.ApplySpeech("bye", Clock.Now); });
        Speech.ProcessExited += () => Ui(() => { OnSpeech(_speech.ProcessGone()); _roster.ApplySpeech("bye", Clock.Now); });
    }

    /// <summary>One line from the speech process: the marks and activities (F116), and Scribe's run, whose jobs these are.</summary>
    void OnSpeechLine(string line)
    {
        var changes = _speech.Apply(line, Clock.Now);
        OnSpeech(changes);
        _roster.ApplySpeech(line, Clock.Now);
        if (changes.Level is { } level) _surface.SetAgentLevel(Cast.Scribe, level);
    }

    // ── The agents (phase 2) ──

    readonly Roster _roster = new();
    public Roster Roster => _roster;

    /// <summary>The roster strip is on unless the flags file turns it off.</summary>
    bool AgentsOn => !_flags.Has("agents") || _flags.IsOn("agents");

    AgentView ViewOf(Agent agent)
    {
        var run = _roster[agent];
        return new AgentView(agent, run.StateAt(Clock.Now), run.Detail, run.Progress);
    }

    /// <summary>
    /// A run changed: its mark and line change in place. Done reads as Idle two seconds later (the
    /// spec's one timed step), so the mark is looked at again then.
    /// </summary>
    void UpdateAgent(Agent agent)
    {
        var view = ViewOf(agent);
        _surface.ShowAgent(view);
        if (view.State == AgentState.Done) Timer((int)(AgentRun.DoneSeconds * 1000) + 50, () => _surface.ShowAgent(ViewOf(agent))).Start();
    }

    /// <summary>Applies what the speech process reported: the level goes straight to the meter, the rest through the queue.</summary>
    void OnSpeech(SpeechChanges changes)
    {
        if (changes.Level is { } level) _surface.SetLevel(level);
        foreach (var key in changes.Remove) Queue.Remove(key, Clock.Now);
        foreach (var activity in changes.Post) Queue.Post(activity, Clock.Now);
    }

    /// <summary>
    /// Tells the speech process how much work it may do (F128): in battery saver it unloads its models
    /// sooner, stops preloading and checks for calls half as often.
    /// </summary>
    void SendPolicy()
    {
        string line = Policy.PreloadModels ? "policy full" : "policy saver";
        if (line == _policySent || !Speech.IsConnected) return;
        _policySent = line;
        Speech.Send(line);
    }

    void OnMedia(NowPlaying? playing) => ShowMusic(playing, null, null);

    /// <summary>
    /// Music in the island: playing music is a live activity; paused music stays for 20 seconds with
    /// its equaliser at rest, then the island returns to rest.
    /// </summary>
    void ShowMusic(NowPlaying? playing, byte[]? demoArt, string? demoLyrics) =>
        _music.OnMedia(playing, demoArt, demoLyrics, demoArt == null ? Media.ReadArtAsync : null);

    /// <summary>The queue follows the song the music director has committed, never a half-loaded one.</summary>
    void SyncMusicActivity()
    {
        var playing = _music.Playing;
        if (playing is { Playing: true }) Queue.Post(MediaAdapter.ToActivity(playing)!, Clock.Now);
        else if (playing != null && Queue.Contains(MediaAdapter.ActivityKey))
            Queue.Post(MediaAdapter.ToActivity(playing with { Playing = true })! with { Lifetime = PausedStay }, Clock.Now);
        else if (playing == null) Queue.Remove(MediaAdapter.ActivityKey, Clock.Now);
    }

    int _contentVersion;

    /// <summary>
    /// Body first, content a moment later: the body's move is committed on this frame, and the content,
    /// which takes longer to build, is built just after at low priority. Its entrance is delayed by
    /// design anyway, so nothing is lost, and the island answers on the frame of the event.
    /// </summary>
    void Defer(Action show)
    {
        int version = ++_contentVersion;
        _ui.TryEnqueue(DispatcherQueuePriority.Low, () => Guard.Run(() => { if (version == _contentVersion) show(); }, "content"));
    }

    void UpdateMode()
    {
        Queue.SetMode(ModeResolver.Resolve(SystemNow(), null), Clock.Now);
        Render(Queue.View);   // the rest shape depends on the window in front even when the view is unchanged
    }

    SystemState SystemNow() => new(
        FullScreenApp: Screen.Front == FrontState.FullScreen,
        Presentation: Screen.Presentation,
        ExclusiveGame: Screen.ExclusiveGame || Power.Mode == EffectivePowerMode.GameMode,
        WindowsFocus: Focus.Active,
        LowBatteryUnplugged: Power.Snapshot is { Percent: < ModeResolver.LowBatteryPercent, PluggedIn: false });

    /// <summary>Where the body rests: a sliver over a maximised window, the pill otherwise (PLAN.md 5.2).</summary>
    BodyShape RestShape => Screen.Front == FrontState.Maximized ? BodyShape.Sliver : BodyShape.Rest;

    // ── Rendering ──

    void Render(IslandView view)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { RenderNow(view); }
        finally { Meter.Record("render.ms", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }

    void RenderNow(IslandView view)
    {
        ScheduleDeadline();

        // A full-screen app hides the island unless a dot must show (F133).
        // Nothing hides a mark (F116): while something listens or records, the island stays visible.
        bool chosenApp = _settings.IsHidden(Screen.FrontProgram) && !_expanded;
        bool hide = view.Marks.Count == 0 &&
                    ((Screen.Front == FrontState.FullScreen && view.Primary == null && view.Dots.Count == 0) ||
                     (chosenApp && view.Primary?.Priority != Priority.ActNow));
        if (hide) { if (_win.IsVisible) { _win.Hide(); foreach (var m in _mirrors.Values) m.Hide(); } MusicLook(false); return; }
        if (!_win.IsVisible) { _win.Show(); foreach (var m in _mirrors.Values) m.Show(); }

        bool music = view.Primary?.Key == MediaAdapter.ActivityKey && _music.View != null;
        MusicLook(music);

        if (_expanded)
        {
            Expand(takeFocus: false);   // refresh the panel's content in place
            return;
        }

        if (view.Primary is { } primary)
        {
            bool fromRest = _body.Shape.Name is "rest" or "sliver" or "peek";
            if (music && _music.LyricsActive)
            {
                var (key, text, instrumental) = _music.Line;
                var shape = IslandSurface.LyricShape(instrumental ? "" : text);
                _body.GoTo(shape, fromRest ? Feel.Lively : Feel.LyricWidth);
                Defer(() => _surface.ShowLyric(key, text, instrumental, shape));
            }
            else
            {
                var content = ContentFor(primary);
                var shape = IslandSurface.ShapeFor(content, _touch);
                _body.GoTo(shape, fromRest ? Feel.Lively : Springs.Quick);
                Defer(() => _surface.ShowCapsule(content, shape, fromRest));
            }
        }
        else
        {
            _contentVersion++;   // cancels any content still waiting to be shown
            _surface.HideCapsule();
            _surface.HideLyrics();
            _body.GoTo(_inside ? BodyShape.Peek : RestShape, Springs.Move);
        }

        _surface.ShowSatellite(view.Satellite, _music.View, view.Waiting);
        _body.ShowSatellite(view.Satellite != null);
        _body.ShowDot(view.Primary == null && view.Dots.Count > 0
            ? view.Dots[0].Mark != null ? view.Dots[0].Accent
            : view.Dots[0].Accent is Accent.None && view.Dots[0].Priority == Priority.ActNow ? Accent.NeedsYou : view.Dots[0].Accent
            : null);
        AnnounceMarks(view);
        Shown?.Invoke(view.Primary?.Key ?? (view.Dots.Count > 0 ? "dot" : "rest"));
    }

    /// <summary>Narrator hears each new mark once, even when it shows only as the dot (F116, F12).</summary>
    void AnnounceMarks(IslandView view)
    {
        foreach (var mark in view.Marks)
            if (_marksShown.Add(mark.Key + "|" + mark.Title)) _surface.AnnounceMark(mark);
        _marksShown.RemoveWhere(k => !view.Marks.Any(m => k == m.Key + "|" + m.Title));
    }

    /// <summary>
    /// The song's look: the body takes the album's dark tint and the fluid glows fade in behind the
    /// content. The glows drift only while music plays, the island is visible, and power allows it.
    /// </summary>
    void MusicLook(bool show)
    {
        Ambient.Allowed = Policy.Visualiser;   // battery saver: the equaliser rests too, as its row refills in this render
        var view = _music.View;
        bool coloured = show && view != null && view.ColoursOn && !Palette.Contrast;
        string state = coloured ? $"{view!.TrackKey}|{view.Colours.Base}" : "ink";
        if (state != _lookApplied)
        {
            _lookApplied = state;
            _body.SetTint(coloured ? Colours.Of(view!.Colours.Base) : null);
            if (coloured) _surface.Backdrop.SetColours(view!.Colours);
            _surface.Backdrop.Show(coloured);
        }
        bool flow = coloured && view!.Playing && _win.IsVisible && Policy.Visualiser;
        string why = $"look: show {show}, song {view != null}, colours {view?.ColoursOn}, playing {view?.Playing}, visible {_win.IsVisible}, visualiser {Policy.Visualiser}";
        if (why != _lastLookWhy) { _lastLookWhy = why; App.Log(Environment.GetEnvironmentVariable("FORTY_OUT"), why); }
        if (flow != _flowing)
        {
            _flowing = flow;
            _surface.Backdrop.Flow(flow);
            App.Log(Environment.GetEnvironmentVariable("FORTY_OUT"), flow ? "music colours flowing" : "music colours still");
        }
    }

    CapsuleContent ContentFor(Activity primary) =>
        primary.Key == MediaAdapter.ActivityKey && _music.View is { } song ? new CapsuleContent(primary with { Title = song.Title }, song, null)
        : primary.Source == "power" && BatteryNow() is { } battery ? new CapsuleContent(primary, null, battery)
        : new CapsuleContent(primary, null, null);

    /// <summary>The battery section's facts (F47 sub-features): level, state, time to full, health.</summary>
    BatteryView? BatteryNow()
    {
        var p = _demoPower ?? Power.Snapshot;
        if (p.Percent is not { } percent) return null;
        string? time = _estimator.TimeToFull(Clock.Now) is { } t ? $"full in {PowerRules.Describe(t)}" : null;
        int? health = p.FullMilliwattHours is > 0 && p.DesignMilliwattHours is > 0
            ? (int)Math.Round(Math.Min(100, 100.0 * p.FullMilliwattHours.Value / p.DesignMilliwattHours.Value)) : null;
        return new BatteryView(percent, p.Charging, p.PluggedIn, p.Saver, time, health);
    }

    /// <summary>Nothing runs on a repeating timer: one shot at the next moment something expires.</summary>
    void ScheduleDeadline()
    {
        _deadline.Stop();
        if (Queue.NextDeadline is not { } due) return;
        _deadline.Interval = TimeSpan.FromSeconds(Math.Max(0.01, due - Clock.Now + 0.005));
        _deadline.Start();
    }

    // ── Expand and collapse ──

    void Expand(bool takeFocus)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        ExpandNow(takeFocus);
        Meter.Record("expand.ms", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    void ExpandNow(bool takeFocus)
    {
        if (_settingsOpen) { ShowSettingsPage(takeFocus); return; }
        var view = Queue.View;
        var primary = view.Primary ?? (_music.View != null ? MediaAdapter.ToActivity(_music.Playing! with { Playing = true }) : null);
        var others = new[] { view.Satellite }.Where(a => a != null).Select(a => a!).ToList();
        foreach (var mark in view.Marks)   // every mark is listed in the panel, with its Stop button
            if (mark.Key != primary?.Key && others.All(o => o.Key != mark.Key)) others.Insert(0, mark);
        DateTimeOffset? recordingSince = _speech.CallSince is { } since ? DateTimeOffset.Now - TimeSpan.FromSeconds(Clock.Now - since) : null;
        var model = new PanelModel(primary, others, _music.View, BatteryNow(), Bluetooth.Devices, ModeLine(view.Mode), recordingSince,
                                   AgentsOn ? Cast.Working.Select(ViewOf).ToList() : null);
        // Power and position reports arrive often. Rebuilding an open panel each time would waste work
        // and take keyboard focus away from you, so it is rebuilt only when what it shows changes.
        if (_expanded && _lastPanel is { } shown && Same(shown, model) && _body.Shape.Name != "full") return;
        bool opening = !_expanded || _body.Shape.Name == "full";
        double capsuleWidth = _body.W.Target;
        _lastPanel = model;
        _expanded = true;
        MusicLook(primary?.Key == MediaAdapter.ActivityKey);
        string kind = model.Music != null && primary?.Key == MediaAdapter.ActivityKey ? "music" : "other";
        if (opening)
        {
            // The body starts growing on this frame, to the height this kind of panel had last time;
            // the content is built just after, and the height is corrected (smoothly) if it differs.
            double guess = _panelHeights.TryGetValue(kind, out double known) ? known : kind == "music" ? 300 : 190;
            _body.GoTo(BodyShape.Panel with { Height = guess }, Springs.Move);
            Defer(() =>
            {
                if (!_expanded) return;
                double height = _surface.ShowPanel(model, opening: true, capsuleWidth);
                _panelHeights[kind] = height;
                if (Math.Abs(height - guess) > 0.5) _body.GoTo(BodyShape.Panel with { Height = height }, Springs.Move);
                if (takeFocus) _surface.FocusFirst();
            });
        }
        else
        {
            double height = _surface.ShowPanel(model, opening: false, capsuleWidth);
            _panelHeights[kind] = height;
            _body.GoTo(BodyShape.Panel with { Height = height }, Springs.Quick);
        }
        _body.ShowSatellite(false);
        _body.ShowDot(null);
        if (view.Primary != null) Queue.Hold(view.Primary.Key, Clock.Now);
        if (takeFocus && _focusOwner == IntPtr.Zero) _focusOwner = _win.TakeFocus();   // focus lands inside once the content is built (F12)
        if (opening) Shown?.Invoke("panel");
    }

    readonly Dictionary<string, double> _panelHeights = new();

    /// <summary>
    /// The settings page: the panel grows into the full page. The body starts growing on this frame;
    /// the page is built just after. Switches update themselves, so the page is rebuilt only when a
    /// list changes (an app added to "hide in front of", a device paired).
    /// </summary>
    void ShowSettingsPage(bool takeFocus)
    {
        bool opening = _body.Shape.Name != "full";
        double maxHeight = Math.Min(BodyShape.Full.Height, _win.MonitorPx.Height / _win.Scale * 0.7);
        var model = SettingsNow();
        if (opening)
        {
            _body.GoTo(BodyShape.Full with { Height = _settingsHeight ?? maxHeight }, Springs.Move);
            Defer(() =>
            {
                if (!_settingsOpen) return;
                double height = _surface.ShowSettings(model, opening: true, maxHeight);
                _settingsHeight = height;
                _body.GoTo(BodyShape.Full with { Height = height }, Springs.Move);
                if (takeFocus) _surface.FocusFirst();
            });
        }
        else
        {
            double height = _surface.ShowSettings(model, opening: false, maxHeight);
            _body.GoTo(BodyShape.Full with { Height = height }, Springs.Quick);
        }
        _body.ShowSatellite(false);
        _body.ShowDot(null);
    }

    double? _settingsHeight;

    SettingsModel SettingsNow()
    {
        string Line(string name, string label, string unit, string format = "0")
        {
            var s = Meter.Summary(name);
            return s == null ? $"{label}: not measured yet" : $"{label}: typically {s.Value.P50.ToString(format)} {unit}, slowest {s.Value.P95.ToString(format)} {unit}";
        }
        var self = System.Diagnostics.Process.GetCurrentProcess();
        string folder = Path.Combine(App.Home, "diagnostics");
        int crashes = Directory.Exists(folder) ? Directory.GetFiles(folder, "crash-*.json").Length : 0;
        var adapters = string.Join(" · ", Adapters.Select(a => $"{a.Name} {(a.Status.Availability == Availability.Available ? "ok" : a.Status.Availability.ToString().ToLowerInvariant())}"));
        var diagnostics = new List<string>
        {
            Meter.Summary("start.ms") is { } start ? $"Started in {start.P50:0} ms" : "Start time not measured yet",
            $"Memory {self.PrivateMemorySize64 / 1048576.0:0} MB" + (Meter.Summary("cpu.percent") is { } cpu ? $" · CPU {cpu.P50:0.0}% of a core over a minute" : ""),
            Line("render.ms", "Drawing a change", "ms", "0.0"),
            Line("expand.ms", "Building the panel", "ms"),
            PacingLine(),
            $"Sources: {adapters}",
            crashes == 0 ? "No crash reports on this PC" : $"{crashes} crash report{(crashes == 1 ? "" : "s")} on this PC (no personal data in them)",
            KeyCheckLine(),
        };
        return new SettingsModel(
            _music.LyricsOn, _music.ColoursOn, _flags.IsOn("bluetooth"), _flags.IsOn("audio-output"), _flags.IsOn("hide-from-capture"), _flags.IsOn("glass"),
            _settings.Alerts.LowLevels, _settings.Alerts.FullLevel, _settings.Hidden, Screen.FrontProgram,
            Bluetooth.Devices.Select(d => (d, _settings.IsQuiet(d.Id))).ToList(), diagnostics,
            typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            _flags.IsOn("follow-display"), Microsoft.UI.Windowing.DisplayArea.FindAll().Count, _keys.Names, _helloReady == true,
            _flags.IsOn("every-display"));
    }

    /// <summary>F9 in words: the display's rate, and how many refreshes motion missed.</summary>
    string PacingLine()
    {
        string rate = FramePacing.Hz is { } hz ? $"Display {hz:0} Hz" : "Display rate unknown";
        return Meter.Summary("motion.missed") is { } missed
            ? $"{rate} · motion typically missed {missed.P50:0} frames per move, at most {missed.P95:0}"
            : $"{rate} · motion not measured yet";
    }

    /// <summary>The mode and why, in plain words, for the panel's footer (F132–F134 sub-feature).</summary>
    string ModeLine(Mode mode)
    {
        var s = SystemNow();
        string why = mode switch
        {
            Mode.Gaming => "a game is running, so only recording marks show",
            Mode.Presenting => "you're presenting, so only urgent dots show",
            Mode.Focus when s.WindowsFocus => "Windows Focus is on, so news waits as a dot",
            Mode.Focus => "battery is low, so news waits as a dot",
            _ => "",
        };
        return why.Length == 0 ? "Normal" : $"{mode} · {why}";
    }

    static bool Same(PanelModel a, PanelModel b) =>
        Equals(a.Primary, b.Primary) && a.Others.SequenceEqual(b.Others) && a.ModeLine == b.ModeLine && a.Battery == b.Battery &&
        (a.RecordingSince == null) == (b.RecordingSince == null) &&
        (a.Agents == null) == (b.Agents == null) &&   // agents' states change in place, not by rebuilding
        a.Devices.SequenceEqual(b.Devices) &&
        Equals(a.Music is { } m ? m with { Clock = default } : null, b.Music is { } n ? n with { Clock = default } : null);

    void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _settingsOpen = false;
        _lastPanel = null;
        _contentVersion++;   // a panel still waiting to be built is not built
        _surface.HidePanel();
        if (_focusOwner != IntPtr.Zero)
        {
            IslandWindow.GiveFocusBack(_focusOwner);
            _focusOwner = IntPtr.Zero;
        }
        Queue.Hold(_inside ? Queue.View.Primary?.Key : null, Clock.Now);
        _ = CollapseBodyLater();
    }

    /// <summary>Content leaves first, then the body follows on the shorter collapse spring.</summary>
    async Task CollapseBodyLater()
    {
        await Task.Delay(40);
        if (_expanded) return;
        Render(Queue.View);
    }

    // ── Your presses on the panel ──

    void OnCommand(string command)
    {
        var playing = _music.Playing;
        switch (command)
        {
            case "previous": _ = Media.PreviousAsync(); break;
            case "next": _ = Media.NextAsync(); break;
            case "playpause": _ = Media.PlayPauseAsync(); break;
            case "shuffle": _ = Media.SetShuffleAsync(!(playing?.Shuffle ?? false)); break;
            case "repeat":
                _ = Media.SetRepeatAsync(playing?.Repeat switch { RepeatMode.Off => RepeatMode.List, RepeatMode.List => RepeatMode.Track, _ => RepeatMode.Off });
                break;
            case "quit": Ambient.StopAll(); Application.Current.Exit(); break;
            // Answers for the speech process, in its own words.
            case "call-accept": Speech.Send("accept"); break;
            case "call-decline":
                Speech.Send("decline");
                Queue.Remove(SpeechSession.CallKey, Clock.Now);   // the question goes at once; nothing is recorded
                break;
            case "call-stop": Speech.Send("stop"); break;
            case "open-notes":
                Speech.Send("open-notes");
                Queue.Remove(SpeechSession.CallKey, Clock.Now);
                break;
            case "settings":
                if (_helloReady == null) _ = HelloReadyAsync().ContinueWith(_ => Ui(() => { if (_settingsOpen) Expand(takeFocus: false); }));
                _settingsOpen = true;
                _lastPanel = null;
                Expand(takeFocus: false);
                break;
            case "back":
                _settingsOpen = false;
                _lastPanel = null;
                Expand(takeFocus: false);
                break;
            case var alert when alert.StartsWith("alert:", StringComparison.Ordinal):
                _settings.ToggleAlert(int.Parse(alert[6..], System.Globalization.CultureInfo.InvariantCulture));
                break;
            case "full:80":
                _settings.SetFullAlert(_settings.Alerts.FullLevel == 80 ? null : 80);
                break;
            case var hide when hide.StartsWith("hide:", StringComparison.Ordinal):
                _settings.ToggleIn(Settings.HiddenApps, hide[5..]);
                break;
            case var quiet when quiet.StartsWith("quiet:", StringComparison.Ordinal):
                _settings.ToggleIn(Settings.QuietDevices, quiet[6..]);
                break;
            case var key when key.StartsWith("remove-key:", StringComparison.Ordinal):
                _ = RemoveKeyAsync(key[11..]);
                break;
            case "open-diagnostics":
                string diagnosticsFolder = Path.Combine(App.Home, "diagnostics");
                Directory.CreateDirectory(diagnosticsFolder);
                var open = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                open.ArgumentList.Add(diagnosticsFolder);
                System.Diagnostics.Process.Start(open)?.Dispose();
                break;
            case var seek when seek.StartsWith("seek:", StringComparison.Ordinal) && playing != null:
                double fraction = double.Parse(seek[5..], System.Globalization.CultureInfo.InvariantCulture);
                _ = Media.SeekAsync(TimeSpan.FromTicks((long)(playing.Clock.Duration.Ticks * fraction)));
                break;
            case var toggle when toggle.StartsWith("toggle:", StringComparison.Ordinal):
                string flag = toggle[7..];
                bool on = flag == MusicDirector.ColoursFlag ? _music.ColoursOn : _flags.IsOn(flag);
                _flags.Set(flag, on ? FlagState.Off : FlagState.On);   // the flags' Changed event re-renders
                break;
        }
    }

    // ── Sensitive actions (F121) ──

    /// <summary>
    /// Removes a stored service key after Windows Hello confirms it is you (face, fingerprint or PIN).
    /// Where Windows Hello isn't set up, the Windows sign-in that protects the key file is the guard.
    /// </summary>
    async Task RemoveKeyAsync(string name)
    {
        if (!_keys.Names.Contains(name)) return;
        bool ready = await HelloReadyAsync();
        if (ready)
        {
            var result = await Windows.Security.Credentials.UI.UserConsentVerifierInterop.RequestVerificationForWindowAsync(
                _win.Hwnd, $"Remove the {SettingsModel.KeyLabel(name)} key from this PC?");
            if (result != Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified) return;
        }
        _keys.Set(name, null);
        if (_settingsOpen) Expand(takeFocus: false);   // the list updates in place
    }

    async Task<bool> HelloReadyAsync()
    {
        try
        {
            _helloReady ??= await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync() ==
                            Windows.Security.Credentials.UI.UserConsentVerifierAvailability.Available;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { _helloReady = false; }
        return _helloReady.Value;
    }

    // ── Pointer and keyboard (MOTION.md 5 and 6) ──

    void OnEntered(object sender, PointerRoutedEventArgs e)
    {
        _inside = true;
        _leave.Stop();
        _touch = e.Pointer.PointerDeviceType == PointerDeviceType.Touch;
        _anchor = e.GetCurrentPoint(_win.Root).Position;
        _armed = false;
        if (_expanded) return;
        if (Queue.View.Primary is { } primary) Queue.Hold(primary.Key, Clock.Now);
        else _body.GoTo(BodyShape.Peek, Springs.Press);   // the instant answer
    }

    /// <summary>
    /// The expand waits for intent, and intent means the pointer actually moved: 3 DIPs, then 80 ms of
    /// dwell. Meanwhile the content leans a little toward the pointer, over glows that stay still.
    /// </summary>
    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var at = e.GetCurrentPoint(_win.Root).Position;
        if (_pull != null) { MovePull(at.Y); return; }
        if (_inside && !_expanded && !MotionPolicy.Reduced)
        {
            double cx = _win.WidthDip / 2, cy = _body.Shape.Top + _win.TopInsetDip + _body.Shape.Height / 2;
            _surface.Lean(Math.Clamp((at.X - cx) / (_body.Shape.Width / 2), -1, 1), Math.Clamp((at.Y - cy) / (_body.Shape.Height / 2), -1, 1));
        }
        if (!_inside || _armed || _expanded) return;
        double dx = at.X - _anchor.X, dy = at.Y - _anchor.Y;
        if (dx * dx + dy * dy < 9) return;
        _armed = true;
        _dwell.Start();
    }

    void OnExited(object sender, PointerRoutedEventArgs e)
    {
        _inside = false;
        _armed = false;
        _dwell.Stop();
        _surface.Lean(0, 0);
        if (_expanded) { _leave.Start(); return; }
        Queue.Hold(null, Clock.Now);
        if (Queue.View.Primary == null) _body.GoTo(RestShape, Springs.Quick);
    }

    /// <summary>
    /// A click never waits: toggle at once. A finger or pen may be starting a drag, so it waits for 10
    /// DIPs of movement: a tap toggles on release, a drag pulls the island open or pushes it closed
    /// (F12). Presses on the panel's own buttons are theirs.
    /// </summary>
    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;
        _dwell.Stop();
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            if (_expanded) Collapse(); else Expand(takeFocus: false);
            return;
        }
        _touch = true;
        _win.Root.CapturePointer(e.Pointer);
        BeginPull(e.GetCurrentPoint(_win.Root).Position.Y);
        e.Handled = true;
    }

    // ── Pulling with a finger or pen (F12, MOTION.md 6) ──

    PullGesture? _pull;
    BodyShape _pullFrom, _pullTo;

    void BeginPull(double y)
    {
        if (_settingsOpen) return;
        _pullFrom = _body.Shape;
        _pullTo = _expanded ? ClosedShape() : BodyShape.Panel with { Height = PanelGuess() };
        _pull = new PullGesture(y, _body.H.Current, _pullTo.Height, Clock.Now);
    }

    void MovePull(double y)
    {
        if (_pull?.Move(y, Clock.Now) is not { } height) return;
        if (_body.Shape.Name != "drag") _body.PrepareFollow(_pullFrom, _pullTo);
        _body.Follow(_pullFrom, _pullTo, height, _pull.Progress(height));
    }

    void EndPull(bool cancelled = false)
    {
        if (_pull is not { } pull) return;
        _pull = null;
        var (tap, commit, velocity) = pull.Release(Clock.Now);
        if (cancelled && tap) return;
        if (tap) { if (_expanded) Collapse(); else Expand(takeFocus: false); return; }
        _body.Kick = velocity;   // whichever way it goes, it carries the finger's speed
        commit &= !cancelled;
        if (_expanded && commit) Collapse();                              // pushed closed
        else if (_expanded) _body.GoTo(_pullFrom, Springs.Thrown);       // let go too early: back to the panel it was
        else if (commit) Expand(takeFocus: false);                        // pulled open
        else Render(Queue.View);                                          // let go too early: back to the capsule
    }

    /// <summary>The shape the island has when the panel is closed: the capsule for what shows, or rest.</summary>
    BodyShape ClosedShape()
    {
        var view = Queue.View;
        if (view.Primary is not { } primary) return RestShape;
        if (primary.Key == MediaAdapter.ActivityKey && _music.LyricsActive)
            return IslandSurface.LyricShape(_music.Line.Instrumental ? "" : _music.Line.Text);
        return IslandSurface.ShapeFor(ContentFor(primary), _touch);
    }

    double PanelGuess()
    {
        string kind = _music.View != null && (Queue.View.Primary == null || Queue.View.Primary.Key == MediaAdapter.ActivityKey) ? "music" : "other";
        return _panelHeights.TryGetValue(kind, out double known) ? known : kind == "music" ? 300 : 190;
    }

    // ── For the scripted demo: a finger pulling down (or up) along these points, 16 ms apart, then letting go ──

    public async Task DemoPull(double startY, IReadOnlyList<double> ys)
    {
        BeginPull(startY);
        foreach (double y in ys) { MovePull(y); await Task.Delay(16); }
        EndPull();
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && _settingsOpen)
        {
            OnCommand("back");
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape && _expanded)
        {
            Collapse();
            e.Handled = true;
        }
    }

    // ── Diagnostics (F130, F150) ──

    void StartResourceSampler()
    {
        var self = System.Diagnostics.Process.GetCurrentProcess();
        _lastCpu = self.TotalProcessorTime;
        _lastSampleAt = Clock.Now;
        _resourceSampler = new Timer(_ =>
        {
            self.Refresh();
            double now = Clock.Now, cpu = (self.TotalProcessorTime - _lastCpu).TotalSeconds;
            Meter.Record("cpu.percent", cpu / Math.Max(1e-3, now - _lastSampleAt) * 100);
            Meter.Record("memory.mb", self.PrivateMemorySize64 / 1048576.0);
            long events = Bluetooth.Events;
            Meter.Record("bluetooth.events", events - _lastBluetoothEvents);
            _lastBluetoothEvents = events;
            _lastCpu = self.TotalProcessorTime;
            _lastSampleAt = now;
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>F121: searches Forty's folder for the stored keys' text (never the encrypted key file itself).</summary>
    void ScanForKeys()
    {
        var keys = _keys.Names.Select(_keys.Get).OfType<string>().ToList();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = KeyScan.Run(App.Home, keys, new[] { "keys.bin" });
        _keyCheck = new KeyCheck(result, DateTime.Now, keys.Count);
        Meter.Record("keys.scan.ms", System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        Meter.Record("keys.found", result.Cleaned.Count);
    }

    /// <summary>The key check's last result in words, for Settings › Diagnostics.</summary>
    string KeyCheckLine() => _keyCheck switch
    {
        null => "Key check runs half a minute after start",
        { Keys: 0 } => "Key check: no keys stored, so nothing to look for",
        { Result.Cleaned.Count: 0 } c => $"Key check at {c.At:HH:mm}: no key text in the {c.Result.Files} files Forty wrote",
        var c => $"Key check at {c.At:HH:mm}: key text found and removed from {string.Join(", ", c.Result.Cleaned)}",
    };

    /// <summary>Turns the Glass look on or off; returns whether Windows allows it right now.</summary>
    public bool SetGlass(bool on) => _body.SetGlass(on);

    // ── For the scripted demo ──

    public void DemoExpand() => Expand(takeFocus: false);
    public bool Expanded => _expanded;
    public void DemoCollapse() => Collapse();
    public void DemoMusic(NowPlaying? playing, byte[]? art, string? lyrics) => ShowMusic(playing, art, lyrics);
    public void DemoPosition(PlaybackClock clock) => _music.OnPosition(clock);
    public void DemoFlag(string flag, bool on) => _flags.Set(flag, on ? FlagState.On : FlagState.Off);
    public void DemoLean(double x, double y) => _surface.Lean(x, y);
    public void DemoPower(PowerSnapshot snapshot) => _demoPower = snapshot;
    public void DemoCommand(string command) => OnCommand(command);
    public void DemoScrollSettings() => _surface.ScrollSettingsToEnd();
    public void DemoSpeech(string line) => OnSpeechLine(line);
    /// <summary>For the layout check: holds the satellite part-way out of the capsule (F5); 1 puts it at rest.</summary>
    public void DemoPoseSplit(double split) { _body.Satellite.Set(Body.SatelliteSize); _body.Split.Set(split); }
    public void DemoDisplaysChanged() => PlaceOnDisplay();
    public void DemoContrast(bool? on) { _demoContrast = on; ApplyLook(); }
    public void DemoMove() => _ = MoveTo(Microsoft.UI.Windowing.DisplayArea.Primary);
    bool? _demoContrast;

    /// <summary>For the scripted demo: a mirror on this same display, lower down, to see the copy work without a second monitor.</summary>
    public void DemoMirror(bool on, int offsetPx)
    {
        if (on) SyncMirrors(Microsoft.UI.Windowing.DisplayArea.Primary, offsetPx);
        else SyncMirrors();
    }

    public string DemoSpeechState => $"link: {Speech.Status.Reason}; connected {_speech.Connected}; unconfirmed {_speech.Unconfirmed}";

    /// <summary>Starts only the speech link, for the demo's pipe check; false if it could not start.</summary>
    public bool DemoStartSpeech()
    {
        ListenToSpeech();
        Speech.Start();
        return Speech.Status.Availability == Availability.Available;
    }
    PowerSnapshot? _demoPower;

    public void DemoMode(Mode mode)
    {
        Queue.SetMode(mode, Clock.Now);
        Render(Queue.View);
    }

    public void Dispose()
    {
        _resourceSampler?.Dispose();
        _keyScanner?.Dispose();
        foreach (var adapter in Adapters) adapter.Dispose();
        _flags.Dispose();
        _win.Dispose();
    }
}

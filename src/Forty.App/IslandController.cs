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
    public PowerAdapter Power { get; } = new();
    public MediaAdapter Media { get; } = new();
    public FocusAdapter Focus { get; } = new();
    public ScreenAdapter Screen { get; }
    public BluetoothAdapter Bluetooth { get; } = new();
    public AudioDeviceAdapter Audio { get; } = new();
    public IReadOnlyList<Adapter> Adapters => new Adapter[] { Power, Media, Focus, Screen, Bluetooth, Audio };
    public Meter Meter { get; }

    readonly FeatureFlags _flags;
    readonly Settings _settings;
    bool _settingsOpen;
    readonly ChargeEstimator _estimator = new();
    readonly DeviceNewsFilter _deviceNews = new();
    PowerSnapshot? _lastPower;
    double _lastDeviceConnect = double.NegativeInfinity;
    Timer? _resourceSampler;
    TimeSpan _lastCpu;
    double _lastSampleAt;
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
        _music = new MusicDirector(flags, new LyricsClient(Path.Combine(App.Home, "lyrics")));
        Screen = new ScreenAdapter(win.MonitorHandle);

        _dwell = Timer(80, () => { if (_inside && !_expanded) Expand(takeFocus: false); });
        _leave = Timer(300, () => { if (!_inside && _expanded && _focusOwner == IntPtr.Zero) Collapse(); });
        _deadline = Timer(0, () => Queue.Tick(Clock.Now));

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
        root.KeyDown += OnKey;
        win.Hotkey += () => { if (_expanded) Collapse(); else Expand(takeFocus: true); };
    }

    DispatcherQueueTimer Timer(int milliseconds, Action tick)
    {
        var timer = _ui.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    /// <summary>Starts the adapters. Their events arrive on Windows' threads and are moved to the UI thread here.</summary>
    public void Start(bool adapters = true)
    {
        ApplyLook();
        _flags.Changed += () => _ui.TryEnqueue(() => { _music.OnFlagsChanged(); ApplyLook(); });
        _win.SettingsChanged += () => _ui.TryEnqueue(ApplyLook);
        _settings.Changed += () => _ui.TryEnqueue(() => Render(Queue.View));
        _win.Show();
        _win.RegisterHotkey();
        if (_flags.IsOn("diagnostics")) StartResourceSampler();
        // Warm the surface once, out of sight, while nothing is showing.
        var warm = Timer(1200, () => { if (Queue.View.Primary == null && !_expanded) _surface.Warm(); });
        warm.Start();
        if (!adapters) return;

        Power.Changed += snapshot => _ui.TryEnqueue(() => OnPower(snapshot));
        Power.ModeChanged += _ => _ui.TryEnqueue(() => { UpdateMode(); ApplyLook(); });
        Media.Changed += playing => _ui.TryEnqueue(() => OnMedia(playing));
        Media.PositionChanged += clock => _ui.TryEnqueue(() => _music.OnPosition(clock));
        Focus.Changed += _ => _ui.TryEnqueue(UpdateMode);
        Screen.Changed += () => _ui.TryEnqueue(UpdateMode);
        Bluetooth.Changed += (before, after) => _ui.TryEnqueue(() => OnBluetooth(before, after));
        Audio.OutputChanged += (name, worn) => _ui.TryEnqueue(() => OnOutput(name, worn));
        foreach (var adapter in Adapters) adapter.Start();

        _lastPower = Power.Snapshot;
        OnMedia(Media.Current);
        UpdateMode();
    }

    PowerPolicy Policy => PowerPolicy.For(Power.Snapshot, Power.Mode is EffectivePowerMode.BatterySaver or EffectivePowerMode.BetterBattery);

    /// <summary>The look that depends on settings: contrast theme (F11), Glass (S6), screen-share hiding (F134).</summary>
    void ApplyLook()
    {
        bool contrast = Native.HighContrastOn() || Environment.GetEnvironmentVariable("FORTY_CONTRAST") == "1";
        _body.SetContrast(contrast);
        bool wantGlass = (_flags.IsOn("glass") || Environment.GetEnvironmentVariable("FORTY_GLASS") == "1") && Policy.Glass && !contrast;
        _body.SetGlass(wantGlass);
        _win.SetExcludedFromCapture(_flags.IsOn("hide-from-capture"));
        _lookApplied = null;
        Render(Queue.View);
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
        UpdateMode();
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
        _ui.TryEnqueue(DispatcherQueuePriority.Low, () => { if (version == _contentVersion) show(); });
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
        bool chosenApp = _settings.IsHidden(Screen.FrontProgram) && !_expanded;
        bool hide = (Screen.Front == FrontState.FullScreen && view.Primary == null && view.Dots.Count == 0) ||
                    (chosenApp && view.Primary?.Priority != Priority.ActNow);
        if (hide) { if (_win.IsVisible) _win.Hide(); MusicLook(false); return; }
        if (!_win.IsVisible) _win.Show();

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
        _body.ShowDot(view.Primary == null && view.Dots.Count > 0 ? view.Dots[0].Accent is Accent.None && view.Dots[0].Priority == Priority.ActNow ? Accent.NeedsYou : view.Dots[0].Accent : null);
        Shown?.Invoke(view.Primary?.Key ?? (view.Dots.Count > 0 ? "dot" : "rest"));
    }

    /// <summary>
    /// The song's look: the body takes the album's dark tint and the fluid glows fade in behind the
    /// content. The glows drift only while music plays, the island is visible, and power allows it.
    /// </summary>
    void MusicLook(bool show)
    {
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
        if (flow != _flowing) { _flowing = flow; _surface.Backdrop.Flow(flow); }
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
        var model = new PanelModel(primary, others, _music.View, BatteryNow(), Bluetooth.Devices, ModeLine(view.Mode));
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
            $"Sources: {adapters}",
            crashes == 0 ? "No crash reports on this PC" : $"{crashes} crash report{(crashes == 1 ? "" : "s")} on this PC (no personal data in them)",
        };
        return new SettingsModel(
            _music.LyricsOn, _music.ColoursOn, _flags.IsOn("bluetooth"), _flags.IsOn("audio-output"), _flags.IsOn("hide-from-capture"), _flags.IsOn("glass"),
            _settings.Alerts.LowLevels, _settings.Alerts.FullLevel, _settings.Hidden, Screen.FrontProgram,
            Bluetooth.Devices.Select(d => (d, _settings.IsQuiet(d.Id))).ToList(), diagnostics,
            typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
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
            case "quit": Application.Current.Exit(); break;
            case "settings":
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

    /// <summary>A click never waits: toggle at once. Presses on the panel's own buttons are theirs.</summary>
    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;
        _dwell.Stop();
        if (_expanded) Collapse(); else Expand(takeFocus: false);
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
            _lastCpu = self.TotalProcessorTime;
            _lastSampleAt = now;
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Turns the Glass look on or off; returns whether Windows allows it right now.</summary>
    public bool SetGlass(bool on) => _body.SetGlass(on);

    // ── For the scripted demo ──

    public void DemoExpand() => Expand(takeFocus: false);
    public void DemoCollapse() => Collapse();
    public void DemoMusic(NowPlaying? playing, byte[]? art, string? lyrics) => ShowMusic(playing, art, lyrics);
    public void DemoPosition(PlaybackClock clock) => _music.OnPosition(clock);
    public void DemoFlag(string flag, bool on) => _flags.Set(flag, on ? FlagState.On : FlagState.Off);
    public void DemoLean(double x, double y) => _surface.Lean(x, y);
    public void DemoPower(PowerSnapshot snapshot) => _demoPower = snapshot;
    public void DemoCommand(string command) => OnCommand(command);
    public void DemoScrollSettings() => _surface.ScrollSettingsToEnd();
    PowerSnapshot? _demoPower;

    public void DemoMode(Mode mode)
    {
        Queue.SetMode(mode, Clock.Now);
        Render(Queue.View);
    }

    public void Dispose()
    {
        _resourceSampler?.Dispose();
        foreach (var adapter in Adapters) adapter.Dispose();
        _flags.Dispose();
        _win.Dispose();
    }
}

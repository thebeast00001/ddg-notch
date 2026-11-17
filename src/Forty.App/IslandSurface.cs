using System.Numerics;
using Forty.Core;
using Forty.Motion;
using Forty.Platform;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Glyphs = Forty.Core.Glyphs;

namespace Forty.App;

/// <summary>Everything the music capsule and panel show about the song.</summary>
sealed record MusicView(
    string TrackKey, string Title, string Artist, string AppName, bool Playing,
    ImageSource? ArtSmall, ImageSource? ArtLarge, TrackColours Colours,
    PlaybackClock Clock, bool CanSeek, bool? Shuffle, RepeatMode Repeat,
    bool LyricsOn, bool ColoursOn, string LyricsNote);

/// <summary>What the battery section shows (F47 and its sub-features).</summary>
sealed record BatteryView(int Percent, bool Charging, bool PluggedIn, bool Saver, string? TimeLine, int? HealthPercent);

/// <summary>What the capsule shows: an activity, plus the music or battery detail that belongs to it.</summary>
sealed record CapsuleContent(Activity Activity, MusicView? Music, BatteryView? Battery, TimerView? Timer = null)
{
    /// <summary>Content with the same key updates in place; a new key swaps the content over.</summary>
    public string Key => Activity.Key + "|" + Music?.TrackKey + "|" + (Meter ? "meter" : Activity.Actions?.Command);

    /// <summary>A live microphone level shows while you are being listened to.</summary>
    public bool Meter => Activity.Mark == Sense.Microphone && Activity.Priority == Priority.ActNow && Music == null;
}

/// <summary>What the expanded panel shows.</summary>
sealed record PanelModel(Activity? Primary, IReadOnlyList<Activity> Others, MusicView? Music, BatteryView? Battery,
                         IReadOnlyList<BluetoothState> Devices, string ModeLine, DateTimeOffset? RecordingSince = null,
                         IReadOnlyList<AgentView>? Agents = null, IReadOnlyList<RunRecord>? Results = null, bool Paused = false,
                         Activity? Reply = null, string? AskHint = null, TimerView? Timer = null);

/// <summary>One agent as the panel shows it: who, in which state, and the tool, question or reason that goes with it.</summary>
sealed record AgentView(Agent Agent, AgentState State, string? Detail, double? Progress);

/// <summary>
/// The content inside the body, built in layers so every change is one gesture with depth:
/// the body moves (it carries everything), the leading art or glyph pops in just behind it, the title
/// settles a beat later, and decoration (the equaliser, values) only fades. Exits all go at once and
/// faster. Things that change often (lyric lines, the panel) never stagger: they move as one piece.
/// All content is ordinary XAML (crisp text, readable by Narrator) and clipped to the body.
/// </summary>
sealed partial class IslandSurface
{
    static readonly FontFamily TextFace = new("Segoe UI Variable Text");
    static readonly FontFamily DisplayFace = new("Segoe UI Variable Display");
    static readonly FontFamily Icons = new("Segoe Fluent Icons");

    readonly IslandWindow _win;
    readonly Body _body;
    readonly Grid _layer;
    readonly ContentBlur _blur;
    readonly CapsuleRow[] _rows;
    readonly LyricRow[] _lyrics;
    int _activeRow, _activeLyric;
    readonly Grid _satellite;
    readonly FontIcon _satelliteGlyph;
    readonly FrameworkElement _satelliteRecord;
    readonly TextBlock _satelliteBadge;
    readonly Border _satelliteArt;
    readonly Grid _panel;
    readonly StackPanel _panelContent;
    readonly Part _panelPart;
    Part? _panelArt;
    (double X, double Y, double Scale)? _travelFrom;
    readonly List<(FrameworkElement From, FrameworkElement To)> _travels = new();   // shared elements for this opening (F4)
    Border? _progressFill;
    TextBlock? _elapsed, _remaining, _recording;
    DateTimeOffset? _recordingSince;
    PlaybackClock _progressClock;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clockTimer;

    public Backdrop Backdrop { get; }

    /// <summary>Your presses on the panel: media commands, toggles, quit. Nothing presses them for you.</summary>
    public event Action<string>? Command;

    void Send(string command) => Command?.Invoke(command);

    public IslandSurface(IslandWindow win, Body body)
    {
        _win = win;
        _body = body;
        Backdrop = new Backdrop(win.Compositor, body);
        _layer = new Grid();
        win.Overlay.Children.Add(_layer);
        body.ClipToBody(_layer);
        _blur = new ContentBlur(win.Compositor, _layer);

        _rows = new[] { new CapsuleRow(win, Send, a => AgentNow?.Invoke(a)), new CapsuleRow(win, Send, a => AgentNow?.Invoke(a)) };
        foreach (var row in _rows) _layer.Children.Add(row.Root);
        _lyrics = new[] { new LyricRow(win), new LyricRow(win) };
        foreach (var row in _lyrics) _layer.Children.Add(row.Root);

        // Forty, at rest: its face in the middle of the pill, and a small "z" beside it when it sleeps.
        _host = new Face(FaceStyle.Host, win.Compositor, HostSize, Windows.UI.Color.FromArgb(238, 255, 255, 255));
        _zzz = new TextBlock { Text = "z", FontFamily = TextFace, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Brush(Palette.Tertiary), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        var host = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Spacing = 0 };
        host.Children.Add(_host.Element);
        host.Children.Add(_zzz);
        _zzz.Margin = new Thickness(-2, 1, -7, 0);   // beside the face, without pushing it off centre
        host.Margin = new Thickness(0, BodyShape.Rest.Top + win.TopInsetDip + (BodyShape.Rest.Height - HostSize) / 2, 0, 0);
        _layer.Children.Add(host);
        _hostPart = new Part(host, win.Compositor);
        _zzzPart = new Part(_zzz, win.Compositor);

        // The satellite: a tiny cover when music waits beside something else, otherwise a glyph.
        _satelliteGlyph = IconArt.Glyph("", 15, Palette.Primary);
        _satelliteRecord = IconArt.Record(15, Palette.Recording);
        _satelliteArt = Art.Frame(26, 13);
        _satelliteArt.HorizontalAlignment = HorizontalAlignment.Center;
        _satelliteBadge = new TextBlock { FontFamily = TextFace, FontSize = 9, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 4, 2) };
        _satellite = new Grid { Width = Body.SatelliteSize, Height = Body.SatelliteSize, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        _satellite.Children.Add(_satelliteArt);
        _satellite.Children.Add(_satelliteGlyph);
        _satellite.Children.Add(_satelliteRecord);
        _satellite.Children.Add(_satelliteBadge);
        win.Overlay.Children.Add(_satellite);
        body.PinToSatellite(_satellite);
        // The satellite's glyph or cover appears only once the drop has pulled clear of the capsule.
        var satelliteFade = win.Compositor.CreateExpressionAnimation("Clamp(s.v, 0, 1) * Clamp((split.v - 0.6) * 3, 0, 1)");
        satelliteFade.Target = "Opacity";
        satelliteFade.SetReferenceParameter("s", body.SatelliteOpacity.Props);
        satelliteFade.SetReferenceParameter("split", body.Split.Props);
        _satellite.StartAnimation(satelliteFade);

        _panelContent = new StackPanel { Spacing = 14, Padding = PanelPadding };
        _panel = new Grid
        {
            Width = BodyShape.Panel.Width, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, BodyShape.Panel.Top + win.TopInsetDip, 0, 0), IsHitTestVisible = false,
            TabFocusNavigation = KeyboardNavigationMode.Cycle, XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled,
        };
        _panel.Children.Add(_panelContent);
        _layer.Children.Add(_panel);
        _panelPart = new Part(_panelContent, win.Compositor);
        _panelContent.CenterPoint = new Vector3((float)BodyShape.Panel.Width / 2, 0, 0);

        _clockTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => Guard.Run(UpdateTimes, "clock");
    }

    // ── Measuring (F1): content is measured once, at its final size, before the body moves ──

    public static double Measure(string text, double size, Windows.UI.Text.FontWeight weight, double maxWidth = double.PositiveInfinity)
    {
        var probe = new TextBlock { Text = text, FontFamily = TextFace, FontSize = size, FontWeight = weight, TextWrapping = double.IsInfinity(maxWidth) ? TextWrapping.NoWrap : TextWrapping.Wrap };
        probe.Measure(new Windows.Foundation.Size(maxWidth, double.PositiveInfinity));
        return double.IsInfinity(maxWidth) ? probe.DesiredSize.Width : probe.DesiredSize.Height;
    }

    /// <summary>The capsule's shape for some content: label plus whatever the trailing slot holds.</summary>
    public static BodyShape ShapeFor(CapsuleContent content, bool touch)
    {
        double trailing = content.Music != null || content.Meter ? 20
            : content.Activity.Actions is { } actions ? ActionsWidth(actions)
            : content.Battery != null ? 28 + 6 + Measure($"{content.Battery.Percent}%", 13, FontWeights.SemiBold)
            : content.Timer is { Phase: TimerPhase.Running or TimerPhase.Paused } timer ? Measure(timer.Widest, 13, FontWeights.SemiBold) + 2
            : string.IsNullOrEmpty(content.Activity.Detail) ? 0 : Measure(content.Activity.Detail, 13, FontWeights.SemiBold);
        return CapsuleLayout.Measure(Measure(content.Activity.Title, 13, FontWeights.SemiBold), trailing, touch);
    }

    /// <summary>The width of the capsule's buttons: each is its label plus 12 DIPs a side, 6 apart.</summary>
    static double ActionsWidth(ActivityActions actions) =>
        Measure(actions.Label, 12, FontWeights.SemiBold) + 24 +
        (actions.SecondLabel is { } second ? 6 + Measure(second, 12, FontWeights.SemiBold) + 24 : 0) +
        (actions.ThirdLabel is { } third ? 6 + Measure(third, 12, FontWeights.SemiBold) + 24 : 0);

    /// <summary>The live microphone level, while the capsule shows Listening.</summary>
    public void SetLevel(double level) => _rows[_activeRow].SetLevel(level);

    /// <summary>
    /// The island's shape for a lyric line: as wide as the line plus breathing room, between 168 and
    /// 460 DIPs; a line too long for that wraps to two lines and the island grows taller instead.
    /// </summary>
    public static BodyShape LyricShape(string text)
    {
        const double MaxText = 416, Pad = 22;
        double width = Measure(text, LyricRow.Size, FontWeights.SemiBold);
        if (width <= MaxText) return new BodyShape("lyric", Math.Clamp(Math.Ceiling(width + 2 * Pad), 168, 460), 36, 18, 6);
        double height = Measure(text, LyricRow.Size, FontWeights.SemiBold, MaxText);
        double h = Math.Clamp(Math.Ceiling(height + 18), 52, 60);
        return new BodyShape("lyric2", MaxText + 2 * Pad, h, h / 2, 6);
    }

    // ── The capsule ──

    /// <summary>Shows content in the capsule. Same key: updates in place. New key: the old leaves and the new arrives in layers.</summary>
    public void ShowCapsule(CapsuleContent content, BodyShape shape, bool fromRest)
    {
        HideLyrics();
        HidePanel();
        var current = _rows[_activeRow];
        // "Showing" includes a row whose entrance is scheduled but hasn't started: two updates a few
        // milliseconds apart must not both bring in a row (both would stay, one going stale).
        if (current.Key == content.Key && current.Showing)
        {
            current.Fill(content, shape, _win.TopInsetDip, refresh: true);
            return;
        }
        bool swapping = current.Showing;
        if (swapping)
        {
            current.Leave(clearing: true);
            _blur.Pass(current.Root, 0, 6, 0.12, holdMs: 200);   // out: blurs as it shrinks and clears (F2)
        }
        _activeRow = 1 - _activeRow;
        var next = _rows[_activeRow];
        next.Fill(content, shape, _win.TopInsetDip, refresh: false);
        int delay = AfterPanel(swapping ? 95 : 40);
        next.Enter(delay);   // a swap waits for the old content to clear: two rich states never overlap
        _blur.Pass(next.Root, 8, 0, 0.26, delay);   // in: arrives blurred and sharpens as it settles
        Announce(next.Root, content.Activity);
    }

    public void HideCapsule()
    {
        foreach (var row in _rows)
        {
            if (!row.Showing) continue;
            row.Leave();
            _blur.Pass(row.Root, 0, 6, 0.12, holdMs: 380);   // the quick spring takes this long to fade it
        }
    }

    /// <summary>Hover depth: the content leans a little toward the pointer while the glows behind it stay put.</summary>
    public void Lean(double dx, double dy)
    {
        _rows[_activeRow].Lean(dx * 1.6, dy * 1.0);
        _lyrics[_activeLyric].Lean(dx * 1.6, dy * 1.0);
    }

    // ── Lyrics (F43 sub-feature) ──

    /// <summary>
    /// Shows a lyric line. The old line rises away as the new one rises into place: one gesture, no
    /// per-word stagger, because it happens every few seconds.
    /// </summary>
    public void ShowLyric(string key, string text, bool dim, BodyShape shape)
    {
        HideCapsule();
        HidePanel();
        var current = _lyrics[_activeLyric];
        if (current.Key == key && current.Visible) return;
        bool replacing = current.Visible;
        if (replacing)
        {
            current.Leave();
            _blur.Pass(current.Root, 0, 4, 0.1, holdMs: 200);   // lighter than a content swap: lines change every few seconds
        }
        _activeLyric = 1 - _activeLyric;
        var next = _lyrics[_activeLyric];
        next.Fill(key, text, dim, shape, _win.TopInsetDip);
        int delay = AfterPanel(replacing ? 95 : 30);
        next.Enter(delay);   // the old line is gone before the new one rises in
        _blur.Pass(next.Root, 6, 0, 0.22, delay);
        AutomationProperties.SetName(next.Root, dim ? "Instrumental" : text);
    }

    public void HideLyrics()
    {
        foreach (var row in _lyrics) if (row.Visible) row.Leave();
    }

    // ── The satellite ──

    public void ShowSatellite(Activity? activity, MusicView? music, int waiting)
    {
        if (activity == null) return;   // the content stays while the circle fades out
        bool art = music?.ArtSmall != null && activity.Key == MediaAdapter.ActivityKey;
        Art.Fill(_satelliteArt, art ? music!.ArtSmall : null);
        _satelliteArt.Visibility = art ? Visibility.Visible : Visibility.Collapsed;
        bool record = !art && activity.Mark == Sense.Call;
        _satelliteRecord.Visibility = record ? Visibility.Visible : Visibility.Collapsed;
        _satelliteGlyph.Visibility = art || record ? Visibility.Collapsed : Visibility.Visible;
        _satelliteGlyph.Glyph = activity.Glyph;
        _satelliteGlyph.Foreground = Brush(Palette.Colour(activity.Accent, Palette.Primary));
        _satelliteBadge.Text = waiting > 0 ? $"+{waiting}" : "";
        _satelliteBadge.Foreground = Brush(Palette.Secondary);
        AutomationProperties.SetName(_satellite, waiting > 0 ? $"{activity.Narration}, and {waiting} more" : activity.Narration);
    }

    // ── The panel ──

    /// <summary>
    /// Fills and shows the panel; returns the height it needs. The panel's content arrives as one piece
    /// (the panel is opened many times a day, so nothing inside it staggers). The album cover is the
    /// exception that makes the opening feel continuous: it travels from its place in the capsule to
    /// its place in the panel and grows on the way.
    /// </summary>
    public double ShowPanel(PanelModel model, bool opening, double capsuleWidth)
    {
        bool fromSettings = _panel.Width != BodyShape.Panel.Width;
        _capsuleWasShown = _rows[_activeRow].Visible;   // only a capsule on screen has anything to travel
        bool fromLyrics = _lyrics.Any(l => l.Visible);
        bool fromTool = _toolKey != null;
        LeaveTool();
        if (fromTool) opening = true;
        ResetPanelWidth();
        if (fromSettings) opening = true;
        foreach (var row in _rows) if (row.Showing) row.Leave(toScale: 0.96);
        HideLyrics();
        _panelContent.Children.Clear();
        _panelArt = null;
        _travelFrom = null;
        _travels.Clear();
        _progressFill = null;
        _recording = null;
        _recordingSince = model.RecordingSince;
        _clockTimer.Stop();

        _ask = null;
        if (model.Agents is { Count: > 0 }) AddAsk(model.Results ?? Array.Empty<RunRecord>(), model.Reply, model.AskHint);   // agents show their faces only when they have something to say
        if (model.Timer is { } timer) AddTimerCard(timer);   // a running timer leads: it's what you set
        if (model.Music is { } music && model.Primary?.Key == MediaAdapter.ActivityKey) AddMusic(music);
        else if (model.Primary is { } p) AddHeader(p, capsuleWidth);
        else if (model.Timer == null) _panelContent.Children.Add(Text("Nothing happening", 15, FontWeights.SemiBold, Palette.Primary));

        if (model.Battery is { } battery && (model.Music == null || model.Primary?.Source == "power")) AddBattery(battery, travel: model.Primary?.Source == "power");
        if (model.Music is { } other && model.Primary?.Key != MediaAdapter.ActivityKey) AddMiniPlayer(other);
        foreach (var activity in model.Others.Where(a => a.Key != MediaAdapter.ActivityKey).Take(3)) AddLine(activity);
        AddDevices(model.Devices);
        AddFooter(model.ModeLine, model.Agents != null ? model.Paused : null);
        if (_recording != null) { UpdateTimes(); _clockTimer.Start(); }   // the call's elapsed time, while the panel is open

        _panel.IsHitTestVisible = true;
        _panelContent.Measure(new Windows.Foundation.Size(_panel.Width, double.PositiveInfinity));
        // Room for everything at once: the roster, the ask field with an answer and results, the full
        // music panel and a recording line; never taller than the window.
        double height = Math.Clamp(Math.Ceiling(_panelContent.DesiredSize.Height), 120, Math.Min(620, _win.HeightDip - 16));

        if (fromSettings)
        {
            // Back from settings: the panel's content returns as one piece; nothing travels.
            _panelPart.Enter(70, fromScale: 0.97, fromY: 4);
            _panelArt?.Enter(0, fromScale: 1);
        }
        else if (opening)
        {
            _panelPart.Enter(fromLyrics ? 115 : 70, fromScale: 0.97, fromY: -4);   // a lyric line (and its blur) clears first
            if (_panelArt != null)
            {
                // The cover starts where the capsule's cover was (13 DIPs in from the capsule's left edge,
                // 7 down) and travels to its panel place (22 in from the panel's edge, 20 down), growing on the way.
                double capsuleX = -capsuleWidth / 2 + 13, panelX = -BodyShape.Panel.Width / 2 + 22;
                var (fx, fy, fs) = _travelFrom ?? (capsuleX - panelX, 7 - 20, 22.0 / 76);
                _panelArt.Place(fx, fy, fs);
                _panelArt.Enter(0, fromScale: fs, spring: Springs.Move);
                _panelArt.Nudge(0, 0, Springs.Move);
            }
            if (!MotionPolicy.Reduced && _capsuleWasShown && (_panelArt != null || _travels.Count > 0))
            {
                // The panel's content (and with it the travelling copies) starts in at 70 ms: the capsule's
                // copies go at that same moment, so there is always exactly one of each.
                var row = _rows[_activeRow];
                _ = HideLater(row, 70);
            }
            Travel();
        }
        else
        {
            _panelPart.Enter(0, fromScale: 1);
            _panelArt?.Enter(0, fromScale: 1);
        }
        return height;
    }

    static async Task HideLater(CapsuleRow row, int ms)
    {
        await Task.Delay(ms);
        if (!row.Visible) row.HideShared();   // the row is still leaving, not showing something new
    }

    /// <summary>
    /// The rest of the shared elements (F4): each value the capsule showed (a detail, the battery gauge
    /// and percentage) travels from where it was in the capsule to its place in the panel, growing or
    /// shrinking on the way, on the same spring as the body. Positions are read from the laid-out
    /// panel, so nothing is guessed; a shared element is never shown twice, because the capsule's
    /// copy is already clearing as this one arrives.
    /// </summary>
    bool _capsuleWasShown;

    void Travel()
    {
        if (_travels.Count == 0 || MotionPolicy.Reduced || !_capsuleWasShown) { _travels.Clear(); return; }
        _panel.UpdateLayout();
        foreach (var (from, to) in _travels)
        {
            if (from.ActualWidth <= 0 || to.ActualWidth <= 0 || from.Visibility != Visibility.Visible) continue;
            var a = from.TransformToVisual(_layer).TransformBounds(new Windows.Foundation.Rect(0, 0, from.ActualWidth, from.ActualHeight));
            var b = to.TransformToVisual(_layer).TransformBounds(new Windows.Foundation.Rect(0, 0, to.ActualWidth, to.ActualHeight));
            double scale = Math.Clamp(a.Height / Math.Max(1, b.Height), 0.3, 3);
            to.CenterPoint = new Vector3((float)(to.ActualWidth / 2), (float)(to.ActualHeight / 2), 0);
            double dx = (a.X + a.Width / 2) - (b.X + b.Width / 2), dy = (a.Y + a.Height / 2) - (b.Y + b.Height / 2);
            var part = new Part(to, _win.Compositor, 1);
            part.Place(dx, dy, scale);
            part.Enter(0, fromScale: scale, spring: Springs.Move);
            part.Nudge(0, 0, Springs.Move);
        }
        _travels.Clear();
    }

    /// <summary>
    /// Builds the capsule and panel once, out of sight, shortly after start-up, so the first real
    /// music event or panel opening doesn't pay one-time costs (code being compiled, controls being
    /// templated) on the frame you are waiting for.
    /// </summary>
    public void Warm()
    {
        var song = new MusicView("warm", "Warm-up", "Warm-up", "Warm-up", true, null, null, TrackColours.Ink,
            new PlaybackClock(TimeSpan.Zero, DateTimeOffset.Now, false, TimeSpan.FromMinutes(3)), true, false, RepeatMode.Off, false, true, "");
        var activity = Activity.Live("warm", "warm", Priority.Ambient, "Warm-up", "1%", Glyphs.Music, "");
        var shape = ShapeFor(new CapsuleContent(activity, song, null), false);
        foreach (var row in _rows) row.Fill(new CapsuleContent(activity, song, null), shape, _win.TopInsetDip, refresh: true);
        _rows[0].Fill(new CapsuleContent(activity, null, new BatteryView(50, true, true, false, null, 90)), shape, _win.TopInsetDip, refresh: true);
        _panelContent.Children.Clear();
        AddMusic(song);
        AddBattery(new BatteryView(50, true, true, false, "full in 1 h", 90));
        AddDevices(new[] { new BluetoothState("warm", "Warm-up", DeviceKind.Audio, true, 50) });
        AddFooter("Normal");
        _panelContent.Measure(new Windows.Foundation.Size(BodyShape.Panel.Width, double.PositiveInfinity));
        _panelContent.Children.Clear();
        _panelArt = null;
        _progressFill = null;
        _clockTimer.Stop();
        foreach (var row in _rows) row.Forget();
    }

    double _panelLeftAt = double.NegativeInfinity;

    /// <summary>Content arriving just after the panel closed waits until the panel's content has cleared (F2: two rich states never overlap).</summary>
    int AfterPanel(int delayMs) => (int)Math.Max(delayMs, 170 - (Clock.Now - _panelLeftAt) * 1000);

    public void HidePanel()
    {
        _clockTimer.Stop();
        LeaveTool();
        _tempoMark?.Face.AutoBlink(false);   // the timer card's face leaves with the panel
        if (!_panelPart.Shown && !_panel.IsHitTestVisible) return;
        _panel.IsHitTestVisible = false;
        _panelLeftAt = Clock.Now;
        _panelPart.Leave(toScale: 0.97, toY: -4, spring: Part.Clear);
        _panelArt?.Leave(toScale: 22.0 / 76);
    }

    /// <summary>Moves keyboard focus to the panel's first control, for when the shortcut opened it (F12).</summary>
    public void FocusFirst()
    {
        static Control? First(UIElement element) => element switch
        {
            Control { IsTabStop: true, IsEnabled: true } control => control,
            Panel panel => panel.Children.Select(First).FirstOrDefault(c => c != null),
            Border { Child: { } child } => First(child),
            _ => null,
        };
        First(_panelContent)?.Focus(FocusState.Keyboard);
    }

    /// <summary>The song's synced lyrics and the line being sung (the controller answers from the music director).</summary>
    public Func<(IReadOnlyList<LyricLine> Lines, int Index)>? LyricsNow { get; set; }

    public const double WideWidth = 520;
    const double LyricViewport = 214;
    readonly List<TextBlock> _lyricLines = new();
    StackPanel? _lyricStack;
    Part? _lyricScroll;
    int _lyricShown = -2;

    /// <summary>The panel's width now: wider while lyrics sit beside the player.</summary>
    public double PanelWidth => _panel.Width;

    /// <summary>
    /// Music with its lyrics beside it, like the reference: a compact player on the left (cover, title,
    /// progress, transport) and the song's synced lyrics on the right, over the album's own colours.
    /// The line being sung is full white; the others fade with distance, and the column glides on the
    /// stream spring as each new line arrives, so the current line always sits a third of the way down.
    /// </summary>
    void AddMusicWithLyrics(MusicView music, IReadOnlyList<LyricLine> lines, int index)
    {
        _panel.Width = WideWidth;
        _panelContent.CenterPoint = new Vector3((float)WideWidth / 2, 0, 0);
        var highlight = Colours.Of(music.Colours.Highlight);
        var layout = new Grid { ColumnSpacing = 0 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(222) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(33) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The player.
        var player = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cover = Art.Frame(64, 12);
        Art.Fill(cover, music.ArtLarge);
        _panelArt = new Part(cover, _win.Compositor, 1);
        top.Children.Add(cover);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var songTitle = Text(music.Title, 15, FontWeights.SemiBold, Palette.Primary, display: true, trim: true);
        songTitle.HorizontalAlignment = HorizontalAlignment.Left;
        words.Children.Add(songTitle);
        _travels.Add((_rows[_activeRow].LabelElement, songTitle));
        words.Children.Add(Text(music.Artist, 12, FontWeights.Normal, Palette.Secondary, trim: true));
        Grid.SetColumn(words, 1);
        top.Children.Add(words);
        player.Children.Add(top);

        var progress = new Grid { ColumnSpacing = 8 };
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _elapsed = Text("", 11, FontWeights.SemiBold, Palette.Secondary);
        _remaining = Text("", 11, FontWeights.SemiBold, Palette.Secondary);
        Typography.SetNumeralAlignment(_elapsed, FontNumeralAlignment.Tabular);
        Typography.SetNumeralAlignment(_remaining, FontNumeralAlignment.Tabular);
        _elapsed.VerticalAlignment = _remaining.VerticalAlignment = VerticalAlignment.Center;
        var track = new Grid { Height = 14, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        track.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Brush(Windows.UI.Color.FromArgb(46, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center });
        _progressFill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Brush(highlight), VerticalAlignment = VerticalAlignment.Center, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        _progressFill.CenterPoint = new Vector3(0, 2, 0);
        track.SizeChanged += (_, e) => { if (_progressFill != null) _progressFill.Width = e.NewSize.Width; };
        track.Children.Add(_progressFill);
        if (music.CanSeek)
            track.PointerPressed += (s, e) =>
            {
                double fraction = Math.Clamp(e.GetCurrentPoint(track).Position.X / track.ActualWidth, 0, 1);
                Command?.Invoke($"seek:{fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                e.Handled = true;
            };
        AutomationProperties.SetName(track, "Song position");
        progress.Children.Add(_elapsed);
        Grid.SetColumn(track, 1);
        progress.Children.Add(track);
        Grid.SetColumn(_remaining, 2);
        progress.Children.Add(_remaining);
        player.Children.Add(progress);
        StartProgress(music.Clock);

        var controls = new Grid();
        for (int i = 0; i < 5; i++) controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Place(FrameworkElement element, int column) { Grid.SetColumn(element, column); controls.Children.Add(element); }
        Place(Round(IconArt.Glyph("", 14, highlight), "Lyrics, on", 32, ButtonLook.Tinted(highlight), "toggle:lyrics"), 0);
        Place(Round(IconArt.Drawing(IconArt.Previous, 18, Palette.Primary), "Previous track", 38, ButtonLook.Quiet, "previous"), 1);
        Place(PlayPause(ref _playGlyph, 22, Palette.OnPrimary, music.Playing, 46, ButtonLook.Filled), 2);
        Place(Round(IconArt.Drawing(IconArt.Next, 18, Palette.Primary), "Next track", 38, ButtonLook.Quiet, "next"), 3);
        Place(Round(IconArt.Glyph("", 14, music.ColoursOn ? highlight : Windows.UI.Color.FromArgb(190, 255, 255, 255)), music.ColoursOn ? "Colours, on" : "Colours, off",
                    32, music.ColoursOn ? ButtonLook.Tinted(highlight) : ButtonLook.Quiet, "toggle:music-colours"), 4);
        player.Children.Add(controls);
        layout.Children.Add(player);

        // A hairline between the two, like the reference.
        var divider = new Border { Width = 1, Background = Brush(Windows.UI.Color.FromArgb(34, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
        Grid.SetColumn(divider, 1);
        layout.Children.Add(divider);

        // The lyrics.
        var viewport = new Grid { Height = LyricViewport };
        viewport.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(-4, 0, 400, LyricViewport) };
        _lyricStack = new StackPanel { Spacing = 9, VerticalAlignment = VerticalAlignment.Top };
        _lyricLines.Clear();
        foreach (var line in lines)
        {
            var text = new TextBlock
            {
                Text = line.IsInstrumental ? "♪" : line.Text, FontFamily = TextFace, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = Brush(Palette.Primary), TextWrapping = TextWrapping.Wrap, Opacity = 0.2,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(280) },
            };
            _lyricLines.Add(text);
            _lyricStack.Children.Add(text);
        }
        viewport.Children.Add(_lyricStack);
        _lyricScroll = new Part(_lyricStack, _win.Compositor, 1);
        AutomationProperties.SetName(viewport, "Lyrics");
        Grid.SetColumn(viewport, 2);
        layout.Children.Add(viewport);
        _panelContent.Children.Add(layout);

        _lyricShown = -2;
        // Lines have their heights once laid out: place the current one, without motion, then.
        _lyricStack.SizeChanged += (_, _) => { if (_lyricShown == -2) ShowPanelLyric(index, animate: false); };
    }

    /// <summary>The line being sung changed: it brightens, the others fade by distance, and the column glides so it sits a third of the way down.</summary>
    public void ShowPanelLyric(int index, bool animate = true)
    {
        if (_lyricStack == null || _lyricLines.Count == 0 || _lyricScroll == null) return;
        _lyricShown = index;
        int current = Math.Clamp(index, 0, _lyricLines.Count - 1);
        double y = 0;
        for (int i = 0; i < current; i++) y += _lyricLines[i].ActualHeight + _lyricStack.Spacing;
        double target = -(y + _lyricLines[current].ActualHeight / 2 - LyricViewport * 0.34);
        target = Math.Min(target, 0);   // the first lines never float below the top

        // Brightness by distance from the line being sung, and a soft fade where lines meet the column's
        // top and bottom edges (an edge that fades, never one that cuts a line in half).
        const double Fade = 30;
        double top = target;
        for (int i = 0; i < _lyricLines.Count; i++)
        {
            int distance = Math.Abs(i - current);
            double byDistance = index < 0 ? (i == 0 ? 0.5 : 0.2) : distance switch { 0 => 1.0, 1 => 0.42, 2 => 0.26, _ => 0.16 };
            double height = _lyricLines[i].ActualHeight;
            double middle = top + height / 2;
            double edge = Math.Clamp(Math.Min(middle / Fade, (LyricViewport - middle) / Fade), 0, 1);
            _lyricLines[i].Opacity = byDistance * (i == current ? Math.Max(edge, 0.85) : edge);
            top += height + _lyricStack.Spacing;
        }
        if (animate && !MotionPolicy.Reduced) _lyricScroll.Nudge(0, target, Springs.Stream);
        else _lyricScroll.Place(0, target, 1);
    }

    void AddMusic(MusicView music)
    {
        _lyricStack = null;
        if (music.LyricsOn && LyricsNow?.Invoke() is { Lines.Count: > 0 } lyrics) { AddMusicWithLyrics(music, lyrics.Lines, lyrics.Index); return; }
        var top = new Grid { ColumnSpacing = 16 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cover = Art.Frame(76, 14);
        Art.Fill(cover, music.ArtLarge);
        cover.CenterPoint = new Vector3(0, 0, 0);
        _panelArt = new Part(cover, _win.Compositor, 1);
        top.Children.Add(cover);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var songTitle = Text(music.Title, 17, FontWeights.SemiBold, Palette.Primary, display: true, trim: true);
        songTitle.HorizontalAlignment = HorizontalAlignment.Left;
        words.Children.Add(songTitle);
        _travels.Add((_rows[_activeRow].LabelElement, songTitle));
        words.Children.Add(Text(music.Artist, 13, FontWeights.Normal, Palette.Secondary, trim: true));
        if (music.AppName.Length > 0) words.Children.Add(Text(music.AppName, 11, FontWeights.Normal, Palette.Tertiary, trim: true));
        Grid.SetColumn(words, 1);
        top.Children.Add(words);
        _panelContent.Children.Add(top);

        // Progress: a thin track that fills on the compositor, with elapsed and remaining time under it.
        var highlight = Colours.Of(music.Colours.Highlight);
        var track = new Grid { Height = 14, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var line = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Brush(Windows.UI.Color.FromArgb(46, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
        _progressFill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Brush(highlight), VerticalAlignment = VerticalAlignment.Center, Width = BodyShape.Panel.Width - 44, HorizontalAlignment = HorizontalAlignment.Left };
        _progressFill.CenterPoint = new Vector3(0, 2, 0);
        track.Children.Add(line);
        track.Children.Add(_progressFill);
        if (music.CanSeek)
        {
            track.PointerPressed += (s, e) =>
            {
                double fraction = Math.Clamp(e.GetCurrentPoint(track).Position.X / track.ActualWidth, 0, 1);
                Command?.Invoke($"seek:{fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                e.Handled = true;
            };
        }
        AutomationProperties.SetName(track, "Song position");
        var times = new Grid();
        _elapsed = Text("", 11, FontWeights.SemiBold, Palette.Secondary);
        _remaining = Text("", 11, FontWeights.SemiBold, Palette.Secondary);
        _remaining.HorizontalAlignment = HorizontalAlignment.Right;
        Typography.SetNumeralAlignment(_elapsed, FontNumeralAlignment.Tabular);
        Typography.SetNumeralAlignment(_remaining, FontNumeralAlignment.Tabular);
        times.Children.Add(_elapsed);
        times.Children.Add(_remaining);
        var progress = new StackPanel { Spacing = 2 };
        progress.Children.Add(track);
        progress.Children.Add(times);
        _panelContent.Children.Add(progress);
        StartProgress(music.Clock);

        // Controls: shuffle and repeat flank the three transport buttons; play is the largest.
        var controls = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int i = 0; i < 5; i++) controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Place(FrameworkElement element, int column) { Grid.SetColumn(element, column); controls.Children.Add(element); }
        // Filled, centred drawings for the transport; the side buttons are quieter and say "on" with a tinted plate.
        bool shuffle = music.Shuffle == true, repeat = music.Repeat != RepeatMode.Off;
        var dim = Windows.UI.Color.FromArgb(190, 255, 255, 255);
        Place(Round(IconArt.Glyph("\uE8B1", 15, shuffle ? highlight : dim), "Shuffle", 36, shuffle ? ButtonLook.Tinted(highlight) : ButtonLook.Quiet, "shuffle", music.Shuffle != null), 0);
        Place(Round(IconArt.Drawing(IconArt.Previous, 22, Palette.Primary), "Previous track", 44, ButtonLook.Quiet, "previous"), 1);
        Place(PlayPause(ref _playGlyph, 26, Palette.OnPrimary, music.Playing, 56, ButtonLook.Filled), 2);
        Place(Round(IconArt.Drawing(IconArt.Next, 22, Palette.Primary), "Next track", 44, ButtonLook.Quiet, "next"), 3);
        Place(Round(IconArt.Glyph(music.Repeat == RepeatMode.Track ? "\uE8ED" : "\uE8EE", 15, repeat ? highlight : dim),
                    music.Repeat switch { RepeatMode.Track => "Repeat one", RepeatMode.List => "Repeat all", _ => "Repeat off" },
                    36, repeat ? ButtonLook.Tinted(highlight) : ButtonLook.Quiet, "repeat"), 4);
        _panelContent.Children.Add(controls);

        // Sub-feature toggles, right where they apply.
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toggles.Children.Add(Toggle("\uE8C1", "Lyrics", music.LyricsOn, "toggle:lyrics"));
        toggles.Children.Add(Toggle("\uE790", "Colours", music.ColoursOn, "toggle:music-colours"));
        var togglesRow = new Grid();
        togglesRow.Children.Add(toggles);
        if (music.LyricsNote.Length > 0)
        {
            var note = Text(music.LyricsNote, 11, FontWeights.Normal, Palette.Tertiary, trim: true);
            note.HorizontalAlignment = HorizontalAlignment.Right;
            note.VerticalAlignment = VerticalAlignment.Center;
            note.MaxWidth = 170;
            togglesRow.Children.Add(note);
        }
        _panelContent.Children.Add(togglesRow);
    }

    /// <summary>The progress fill runs on the compositor from where the song is to its end, so nothing ticks per frame.</summary>
    void StartProgress(PlaybackClock clock)
    {
        _progressClock = clock;
        if (_progressFill == null) return;
        double duration = clock.Duration.TotalSeconds;
        double at = clock.At(DateTimeOffset.Now).TotalSeconds;
        float from = duration > 0 ? (float)Math.Clamp(at / duration, 0, 1) : 0;
        var c = _win.Compositor;
        var animation = c.CreateVector3KeyFrameAnimation();
        animation.Target = "Scale";
        animation.InsertKeyFrame(0f, new Vector3(Math.Max(from, 0.001f), 1, 1), c.CreateLinearEasingFunction());
        if (clock.Playing && duration > 0 && at < duration)
        {
            animation.InsertKeyFrame(1f, new Vector3(1, 1, 1), c.CreateLinearEasingFunction());
            animation.Duration = TimeSpan.FromSeconds((duration - at) / Math.Max(0.1, clock.Rate));
        }
        else animation.Duration = TimeSpan.FromMilliseconds(1);
        _progressFill.StartAnimation(animation);
        UpdateTimes();
        if (clock.Playing) _clockTimer.Start();   // only while the panel is open and music plays
    }

    public void UpdateProgress(PlaybackClock clock) { if (_progressFill != null) StartProgress(clock); }

    void UpdateTimes()
    {
        if (_recording != null && _recordingSince is { } since) _recording.Text = Time(DateTimeOffset.Now - since);
        if (_elapsed == null || _remaining == null) return;
        var at = _progressClock.At(DateTimeOffset.Now);
        _elapsed.Text = Time(at);
        _remaining.Text = _progressClock.Duration > TimeSpan.Zero ? "-" + Time(_progressClock.Duration - at) : "";
    }

    static string Time(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    void AddMiniPlayer(MusicView music)
    {
        var line = new Grid { ColumnSpacing = 10 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cover = Art.Frame(28, 7);
        Art.Fill(cover, music.ArtSmall);
        line.Children.Add(cover);
        var title = Text($"{music.Title} · {music.Artist}", 13, FontWeights.Normal, Palette.Secondary, trim: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        line.Children.Add(title);
        var play = PlayPause(ref _miniPlayGlyph, 16, Palette.Primary, music.Playing, 32, ButtonLook.Quiet);
        Grid.SetColumn(play, 2);
        line.Children.Add(play);
        _panelContent.Children.Add(line);
    }

    void AddHeader(Activity p, double capsuleWidth)
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement glyph = p.Mark == Sense.Call ? IconArt.Record(22, Palette.Recording) : IconArt.Glyph(p.Glyph, 22, Palette.Colour(p.Accent, Palette.Primary));
        glyph.CenterPoint = new Vector3(0, 0, 0);
        _panelArt = new Part(glyph, _win.Compositor, 1);
        _travelFrom = (-capsuleWidth / 2 + 12 - (-BodyShape.Panel.Width / 2 + 22), 6 - 20, 16.0 / 22);
        var title = Text(p.Title, 16, FontWeights.SemiBold, Palette.Primary, display: true, trim: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.HorizontalAlignment = HorizontalAlignment.Left;   // its box is the text, so it travels to the right spot
        _travels.Add((_rows[_activeRow].LabelElement, title));
        var detail = Text(p.Detail ?? "", 16, FontWeights.SemiBold, Palette.Secondary);
        detail.VerticalAlignment = VerticalAlignment.Center;
        if (!string.IsNullOrEmpty(p.Detail)) _travels.Add((_rows[_activeRow].DetailElement, detail));
        Typography.SetNumeralAlignment(detail, FontNumeralAlignment.Tabular);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(detail, 2);
        header.Children.Add(glyph);
        header.Children.Add(title);
        header.Children.Add(detail);
        _panelContent.Children.Add(header);
        var said = Text(p.Narration, 13, FontWeights.Normal, Palette.Secondary);
        said.TextWrapping = TextWrapping.Wrap;
        _panelContent.Children.Add(said);
        if (p.Actions is { } actions)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(ActionButton(actions.Label, true, actions.Command));
            if (actions.SecondLabel is { } second && actions.SecondCommand is { } secondCommand) buttons.Children.Add(ActionButton(second, false, secondCommand));
            if (actions.ThirdLabel is { } third && actions.ThirdCommand is { } thirdCommand) buttons.Children.Add(ActionButton(third, false, thirdCommand));
            _panelContent.Children.Add(buttons);
        }
    }

    /// <summary>A panel button for an activity's answer: the first one filled, the second quiet.</summary>
    Button ActionButton(string label, bool primary, string command, double height = 32)
    {
        double side = height < 30 ? 13 : 18;   // the smaller answers sit three to a row under the ask field
        var button = new IslandButton(IconArt.Label(label, height < 30 ? 12.5 : 13, primary ? Palette.OnPrimary : Palette.Primary, TextFace), label,
                                      primary ? ButtonLook.Filled : ButtonLook.Soft, double.NaN, height, height / 2, _win.Compositor, new Thickness(side, 0, side, 0));
        button.Button.Click += (_, _) => Command?.Invoke(command);
        return button;
    }

    void AddBattery(BatteryView b, bool travel = false)
    {
        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gauge = new BatteryGauge(_win.Compositor, 46, 22);
        gauge.Show(b.Percent, b.Charging, sweep: false);
        row.Children.Add(gauge.Root);
        if (travel) _travels.Add((_rows[_activeRow].GaugeElement, gauge.Root));
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var big = Text($"{b.Percent}%", 22, FontWeights.SemiBold, Palette.Primary, display: true);
        Typography.SetNumeralAlignment(big, FontNumeralAlignment.Tabular);
        big.HorizontalAlignment = HorizontalAlignment.Left;
        words.Children.Add(big);
        if (travel) _travels.Add((_rows[_activeRow].PercentElement, big));
        string state = b.Charging ? "Charging" : b.PluggedIn ? "Plugged in, not charging" : "On battery";
        words.Children.Add(Text(state + (b.TimeLine is { } t ? $" · {t}" : "") + (b.Saver ? " · Battery saver on" : ""), 12, FontWeights.Normal, Palette.Secondary, trim: true));
        if (b.HealthPercent is { } health) words.Children.Add(Text($"Battery health {health}% of its original capacity", 11, FontWeights.Normal, Palette.Tertiary, trim: true));
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        AutomationProperties.SetName(row, $"Battery {b.Percent} percent, {state}");
        _panelContent.Children.Add(row);
    }

    void AddLine(Activity other)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(other.Mark == Sense.Call ? IconArt.Record(14, Palette.Recording) : IconArt.Glyph(other.Glyph, 14, Palette.Colour(other.Accent, Palette.Secondary)));
        line.Children.Add(Text(other.Title + (other.Detail is { } d ? $"  ·  {d}" : ""), 13, FontWeights.Normal, other.Mark != null ? Palette.Primary : Palette.Secondary));
        if (other.Mark == Sense.Call && _recordingSince != null)
        {
            // A recording shows how long it has been running (F116).
            _recording = Text("", 13, FontWeights.SemiBold, Palette.Recording);
            Typography.SetNumeralAlignment(_recording, FontNumeralAlignment.Tabular);
            line.Children.Add(_recording);
        }
        row.Children.Add(line);
        if (other.Actions is { } actions)
        {
            var button = ActionButton(actions.Label, false, actions.Command, height: 28);
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
        }
        AutomationProperties.SetName(row, other.Narration);
        _panelContent.Children.Add(row);
    }

    /// <summary>Connected Bluetooth devices with their battery, as chips (F45, F46 sub-feature).</summary>
    void AddDevices(IReadOnlyList<BluetoothState> devices)
    {
        var connected = devices.Where(d => d.Connected).ToList();
        if (connected.Count == 0) return;
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var device in connected.Take(3))
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            chip.Children.Add(new TextBlock { Text = DeviceRules.Glyph(device.Kind), FontFamily = Icons, FontSize = 13, Foreground = Brush(Palette.Secondary), VerticalAlignment = VerticalAlignment.Center });
            chip.Children.Add(Text(device.Name.Length > 18 ? device.Name[..17] + "…" : device.Name, 12, FontWeights.Normal, Palette.Secondary));
            if (device.Battery is { } level)
                chip.Children.Add(Text($"{level}%", 12, FontWeights.SemiBold, level <= 20 ? Palette.Error : Palette.Primary));
            var pill = new Border { Child = chip, Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(12), Background = Brush(Windows.UI.Color.FromArgb(22, 255, 255, 255)) };
            AutomationProperties.SetName(pill, device.Battery is { } b ? $"{device.Name}, battery {b} percent" : device.Name);
            chips.Children.Add(pill);
        }
        _panelContent.Children.Add(chips);
    }

    void AddFooter(string modeLine, bool? paused = null)
    {
        var footer = new Grid();
        var mode = Text(modeLine, 11, FontWeights.Normal, Palette.Tertiary, trim: true);
        mode.TextLineBounds = TextLineBounds.Tight;   // centred on its letters, like the links beside it
        mode.VerticalAlignment = VerticalAlignment.Center;
        mode.Margin = new Thickness(0, 0, paused == null ? 110 : 180, 0);   // clear of the links beside it
        footer.Children.Add(mode);
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (paused is { } p) links.Children.Add(p ? TextLink("Resume", "Resume everything", "resume-all") : TextLink("Pause all", "Pause everything (Ctrl+Alt+Shift+Space)", "pause-all"));
        links.Children.Add(TextLink("Settings", "Open settings", "settings"));
        links.Children.Add(TextLink("Quit", "Quit Forty", "quit"));
        footer.Children.Add(links);
        _panelContent.Children.Add(footer);
    }

    PlayPauseGlyph? _playGlyph, _miniPlayGlyph;
    public PlayPauseGlyph? PlayGlyph => _playGlyph;

    /// <summary>
    /// The play/pause button. Its glyph outlives the panel, so a rebuilt transport changes shape from
    /// wherever the last one was; pressing it changes shape at once, before the player has answered
    /// (if the player then disagrees, the rebuilt panel turns it back).
    /// </summary>
    Button PlayPause(ref PlayPauseGlyph? glyph, double size, Windows.UI.Color colour, bool playing, double buttonSize, ButtonLook look)
    {
        glyph ??= new PlayPauseGlyph(_win.Compositor, size, colour, playing);
        var g = glyph;
        g.SetColour(colour);
        g.Set(playing);
        var button = new IslandButton(g.Detach(), playing ? "Pause" : "Play", look, buttonSize, buttonSize, buttonSize / 2, _win.Compositor);
        g.Holder = button.Plate;
        button.Button.HorizontalAlignment = HorizontalAlignment.Center;
        button.Button.Click += (_, _) =>
        {
            g.Set(!g.Playing);
            AutomationProperties.SetName(button.Button, g.Playing ? "Pause" : "Play");
            Command?.Invoke("playpause");
        };
        return button;
    }

    /// <summary>A round button: a centred icon on a plate of the given look (see <see cref="IslandButton"/>).</summary>
    Button Round(FrameworkElement icon, string name, double size, ButtonLook look, string command, bool enabled = true)
    {
        var button = new IslandButton(icon, name, look, size, size, size / 2, _win.Compositor);
        button.Button.HorizontalAlignment = HorizontalAlignment.Center;
        button.Button.IsEnabled = enabled;
        button.Button.Click += (_, _) => Command?.Invoke(command);
        return button;
    }

    /// <summary>A sub-feature switch as a pill: an icon and a word, filled white when on.</summary>
    Button Toggle(string glyph, string label, bool on, string command)
    {
        var ink = on ? Palette.OnPrimary : Palette.Primary;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(IconArt.Glyph(glyph, 13, ink));
        content.Children.Add(IconArt.Label(label, 12.5, ink, TextFace));
        var button = new IslandButton(content, $"{label}, {(on ? "on" : "off")}", on ? ButtonLook.Filled : ButtonLook.Soft, double.NaN, 30, 15, _win.Compositor, new Thickness(12, 0, 14, 0));
        button.Button.Click += (_, _) => Command?.Invoke(command);
        return button;
    }

    /// <summary>A quiet text button (the panel's footer): secondary text, a soft plate under the pointer, no underline.</summary>
    Button TextLink(string label, string name, string command)
    {
        var button = new IslandButton(IconArt.Label(label, 11.5, Palette.Secondary, TextFace), name, ButtonLook.Quiet, double.NaN, 24, 12, _win.Compositor, new Thickness(9, 0, 9, 0));
        button.Button.Click += (_, _) => Command?.Invoke(command);
        return button;
    }

    static TextBlock Text(string text, double size, Windows.UI.Text.FontWeight weight, Windows.UI.Color colour, bool display = false, bool trim = false) => new()
    {
        Text = text, FontFamily = display ? DisplayFace : TextFace, FontSize = size, FontWeight = weight, Foreground = Brush(colour),
        TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
        // Tracking tightens as text grows (MOTION.md 8).
        CharacterSpacing = size >= 16 ? -10 : size <= 12 ? 8 : 0,
    };

    static SolidColorBrush Brush(Windows.UI.Color colour) => new(colour);

    /// <summary>
    /// A theme change (F11): the content dips out over 120 ms, <paramref name="rebuild"/> redraws it in
    /// the new colours, and it returns over 180 ms, while the body's own colours ease across underneath.
    /// </summary>
    public void Retint(Action rebuild)
    {
        var layer = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_layer);
        if (MotionPolicy.Reduced) { rebuild(); return; }
        var c = layer.Compositor;
        var dip = c.CreateScalarKeyFrameAnimation();
        dip.InsertKeyFrame(1f, 0f);
        dip.Duration = TimeSpan.FromMilliseconds(120);
        layer.StartAnimation("Opacity", dip);
        _ = Later();
        async Task Later()
        {
            await Task.Delay(120);
            Guard.Run(rebuild, "retint");
            var back = c.CreateScalarKeyFrameAnimation();
            back.InsertKeyFrame(1f, 1f);
            back.Duration = TimeSpan.FromMilliseconds(180);
            layer.StartAnimation("Opacity", back);
        }
    }

    // ── For the layout check (FORTY_DEMO=layout): what should sit centred in what ──

    /// <param name="Both">True to check both axes; false for vertical only (a label sits at the left of its row).</param>
    public sealed record Probe(string Name, FrameworkElement Ink, FrameworkElement Box, bool Both);

    public IEnumerable<Probe> Probes()
    {
        var row = _rows[_activeRow];
        if (row.Visible) foreach (var probe in row.Probes()) yield return probe;
        var lyric = _lyrics[_activeLyric];
        if (lyric.Visible) yield return new Probe("lyric line", lyric.Text, lyric.Root, true);
        if (_body.SatelliteShown)
            yield return new Probe("satellite mark", _satelliteRecord.Visibility == Visibility.Visible ? _satelliteRecord
                                                   : _satelliteGlyph.Visibility == Visibility.Visible ? _satelliteGlyph : _satelliteArt, _satellite, true);
        if (_panel.IsHitTestVisible)
            foreach (var button in Descendants<Button>(_panelContent))
                if (button.Content is FrameworkElement content && button.ActualWidth > 0 && button.Tag as string != "switch")   // a switch's knob sits at one end by design
                    yield return new Probe("button " + (AutomationProperties.GetName(button) is { Length: > 0 } n ? n : "?"), content is Border { Child: FrameworkElement inside } ? inside : content, button, true);
    }

    public FrameworkElement Layer => _layer;

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            else foreach (var inner in Descendants<T>(child)) yield return inner;
        }
    }

    /// <summary>Narrator hears a new mark assertively, even when it shows only as the dot or the satellite (F116).</summary>
    public void AnnounceMark(Activity mark)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(_satellite) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_satellite);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, mark.Narration, mark.Key);
    }

    // ── The agents (phase 2) ──

    // ── Forty, at rest ──

    const double HostSize = 34;
    readonly Face _host;
    readonly TextBlock _zzz;
    readonly Part _hostPart, _zzzPart;
    bool _hostBlinks = true;

    /// <summary>Forty's face on the resting island.</summary>
    public Face Host => _host;

    /// <summary>
    /// Shows Forty's face while the island rests, or lets it go as content arrives. It pops in on the
    /// same small spring as a capsule's glyph and leaves quicker, scaling a little as it goes, so the face
    /// and the content it gives way to read as one thing changing.
    /// </summary>
    public void ShowHost(bool show, bool blink)
    {
        if (show && !_hostPart.Shown) { _hostPart.Enter(fromScale: 0.7, spring: Feel.Pop); _host.Blink(); }
        else if (!show && _hostPart.Shown) _hostPart.Leave(toScale: 0.75, spring: Part.Clear);
        _hostBlinks = blink;
        _host.AutoBlink(show && blink);
    }

    public bool HostShown => _hostPart.Shown;

    /// <summary>Asleep: the eyes close to lines and a "z" drifts in beside them. Nothing moves until it wakes.</summary>
    public void HostSleep(bool asleep)
    {
        if (asleep) { _host.Express(Expression.Asleep); _host.AutoBlink(false); _zzzPart.Enter(fromScale: 0.6, fromY: 3, spring: Springs.Unfold); }
        else { _zzzPart.Leave(toScale: 0.6, toY: -3); _host.AutoBlink(_hostPart.Shown && _hostBlinks); }
    }

    // Agents are not on show all the time: a face appears only where an agent is doing something, in
    // the capsule while it works and beside its answer under the ask field. Each place keeps its own
    // face for each agent, so a face never has to be in two places at once.
    readonly Dictionary<string, AgentMark> _marks = new();
    public IReadOnlyDictionary<string, AgentMark> Marks => _marks;

    /// <summary>The agent's state now (the controller answers from the runs, F15).</summary>
    public Func<Agent, AgentView>? AgentNow { get; set; }

    AgentMark MarkFor(Agent agent) => _marks.TryGetValue(agent.Id, out var mark) ? mark : _marks[agent.Id] = new AgentMark(agent, _win.Compositor, 22);

    /// <summary>An agent's state changed: every face of it on show moves to the new expression, and Narrator hears it.</summary>
    public void ShowAgent(AgentView view)
    {
        if (_marks.TryGetValue(view.Agent.Id, out var mark) && mark.State != view.State) mark.Show(view.State, ToolGlyph(view.Detail));
        foreach (var row in _rows) row.ShowAgent(view);
        if (view.State == AgentState.Idle) return;
        string words = AgentWords.Announcement(view.Agent, view.State, view.Detail);
        var peer = FrameworkElementAutomationPeer.FromElement(_layer) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_layer);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,
            AgentWords.Assertive(view.State) ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
            words, "agent." + view.Agent.Id);
    }

    public void SetAgentLevel(Agent agent, double level)
    {
        if (_marks.TryGetValue(agent.Id, out var mark)) mark.SetLevel(level);
        foreach (var row in _rows) row.SetAgentLevel(agent, level);
    }

    /// <summary>The key of the "Thinking…" reply, shown while a request is being understood.</summary>
    public const string ThinkingKey = "agents.think";
    Face? _thinker;
    Panel? _thinkerHolder;

    /// <summary>A line was asked in the panel's field (Enter).</summary>
    public event Action<string>? Asked;

    /// <summary>The field was clicked: the island needs keyboard focus for typing to reach it.</summary>
    public event Action? WantsFocus;

    TextBox? _ask;

    /// <summary>
    /// The ask field (F24's start, and the way to reach the agents without a voice): a quiet plate under
    /// the roster that brightens when focused. The shortcut opens the panel with the cursor in it, so
    /// Ctrl+Alt+Space, "open Notepad", Enter is the whole gesture. WinUI's own text-box chrome (its
    /// background, border and accent underline) is made transparent; the plate is the only chrome.
    /// </summary>
    void AddAsk(IReadOnlyList<RunRecord> results, Activity? reply, string? hint = null)
    {
        var plate = new Border
        {
            CornerRadius = new CornerRadius(17), Height = 34, Padding = new Thickness(12, 0, 6, 0),
            Background = Brush(Windows.UI.Color.FromArgb(22, 255, 255, 255)),
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(120) },
        };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(IconArt.Glyph("", 13, Palette.Tertiary));
        var box = _ask = new TextBox
        {
            PlaceholderText = hint ?? "Ask Forty. Try “open Notepad”", FontFamily = TextFace, FontSize = 13,
            BorderThickness = new Thickness(0), Padding = new Thickness(0, 7, 0, 0), MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center, Background = Brush(Microsoft.UI.Colors.Transparent),
            Foreground = Brush(Palette.Primary), IsSpellCheckEnabled = false,
        };
        var clear = Brush(Microsoft.UI.Colors.Transparent);
        foreach (string key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                                       "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
                                       "TextControlElevationBorderBrush", "TextControlElevationBorderFocusedBrush", "TextControlButtonForeground", "TextControlButtonForegroundPointerOver" })
            box.Resources[key] = clear;
        foreach (string key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            box.Resources[key] = Brush(Palette.Primary);
        foreach (string key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" })
            box.Resources[key] = Brush(Palette.Tertiary);
        AutomationProperties.SetName(box, "Ask Forty");
        box.GotFocus += (_, _) => plate.Background = Brush(Windows.UI.Color.FromArgb(36, 255, 255, 255));
        box.LostFocus += (_, _) => plate.Background = Brush(Windows.UI.Color.FromArgb(22, 255, 255, 255));
        box.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => WantsFocus?.Invoke()), true);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && box.Text.Trim().Length > 0)
            {
                string line = box.Text.Trim();
                box.Text = "";
                e.Handled = true;
                Asked?.Invoke(line);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape && box.Text.Length > 0)
            {
                box.Text = "";   // the first Esc clears what was typed; the next closes the panel
                e.Handled = true;
            }
        };
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        // The quick tools, at the end of the field: one press to the timer or the mirror.
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(Round(IconArt.Glyph("", 13, Palette.Secondary), "Timer", 26, ButtonLook.Quiet, "tool:timer"));
        tools.Children.Add(Round(IconArt.Glyph("", 13, Palette.Secondary), "Mirror", 26, ButtonLook.Quiet, "tool:mirror"));
        Grid.SetColumn(tools, 2);
        row.Children.Add(tools);
        plate.Child = row;
        _panelContent.Children.Add(plate);

        // The agents' answer to what was asked, right under the question: a question with its buttons
        // ("Open Spotify?" Always, Once, No), a result, or a plain "can't do that yet".
        if (reply != null)
        {
            var answer = new Grid { ColumnSpacing = 10, Margin = new Thickness(4, 0, 0, 0) };
            answer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            answer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // While Forty works out what you asked, its own face thinks; then the agent who answers shows
            // its face; anything else, its glyph.
            if (reply.Key == ThinkingKey)
            {
                _thinker ??= new Face(FaceStyle.Host, _win.Compositor, 22, Windows.UI.Color.FromArgb(238, 255, 255, 255));
                _thinkerHolder?.Children.Remove(_thinker.Element);
                answer.Children.Add(_thinker.Element);
                _thinkerHolder = answer;
                _thinker.Element.VerticalAlignment = VerticalAlignment.Top;
                _thinker.Element.Margin = new Thickness(-2, -2, 0, 0);
                _thinker.Express(Expression.Thinking);
            }
            else if (reply.Agent is { } speaker && AgentNow?.Invoke(speaker) is { } now)
            {
                // The face is a button: it opens the agent's page (what it can do and use, what it remembers).
                var face = MarkFor(speaker);
                var holder = new Grid();
                face.PlaceIn(holder);
                face.Element.Margin = new Thickness(0);
                face.Show(now.State, ToolGlyph(now.Detail));
                var open = new IslandButton(holder, $"{speaker.Name}'s page", ButtonLook.Quiet, 28, 28, 14, _win.Compositor);
                open.Button.VerticalAlignment = VerticalAlignment.Top;
                open.Button.Margin = new Thickness(-5, -5, -3, 0);
                open.Button.Click += (_, _) => Send("page:agent:" + speaker.Id);
                answer.Children.Add(open.Button);
            }
            else
            {
                var glyph = IconArt.Glyph(reply.Glyph, 15, Palette.Colour(reply.Accent, Palette.Primary));
                glyph.VerticalAlignment = VerticalAlignment.Top;
                glyph.Margin = new Thickness(0, 2, 0, 0);
                answer.Children.Add(glyph);
            }
            var words = new StackPanel { Spacing = 6 };
            words.Children.Add(Text(reply.Title, 13.5, FontWeights.SemiBold, Palette.Primary, trim: true));
            if (reply.Narration != reply.Title && reply.Key != ThinkingKey)
            {
                // An agent's own words (an answer) read as text, a little larger and brighter than a note.
                bool words_ = reply.Agent != null && reply.Detail != null;
                var said = Text(reply.Narration, words_ ? 13 : 12, FontWeights.Normal, words_ ? Windows.UI.Color.FromArgb(225, 255, 255, 255) : Palette.Secondary);
                said.TextWrapping = TextWrapping.Wrap;
                if (words_) said.LineHeight = 18;
                words.Children.Add(said);
            }
            // Where it was understood (F118): the provider, cloud or this PC, and how long it took.
            if (reply.Detail is { Length: > 0 } via && reply.Key != ThinkingKey)
                words.Children.Add(Text(via, 10.5, FontWeights.Normal, Palette.Tertiary, trim: true));
            if (reply.Actions is { } actions)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
                buttons.Children.Add(ActionButton(actions.Label, true, actions.Command, height: 28));
                if (actions.SecondLabel is { } second && actions.SecondCommand is { } secondCommand) buttons.Children.Add(ActionButton(second, false, secondCommand, height: 28));
                if (actions.ThirdLabel is { } third && actions.ThirdCommand is { } thirdCommand) buttons.Children.Add(ActionButton(third, false, thirdCommand, height: 28));
                words.Children.Add(buttons);
            }
            Grid.SetColumn(words, 1);
            answer.Children.Add(words);
            AutomationProperties.SetName(answer, reply.Narration);
            _panelContent.Children.Add(answer);
            Announce(answer, reply);
        }

        // The last results from the past hour (F24: "the last three results", two fit here), newest first.
        foreach (var run in results.Where(r => DateTimeOffset.Now - r.Ended < TimeSpan.FromHours(1)).Take(2))
        {
            string ago = DateTimeOffset.Now - run.Ended is var span && span.TotalMinutes < 1 ? "just now" : $"{(int)span.TotalMinutes} min ago";
            var line = Text($"{run.Agent} · {run.Outcome} · {ago}", 11.5, FontWeights.Normal, Palette.Tertiary, trim: true);
            line.Margin = new Thickness(12, -4, 0, 0);
            _panelContent.Children.Add(line);
        }
    }

    /// <summary>For the layout check: types a line into the ask field and presses Enter.</summary>
    public void DemoAsk(string line) { if (_ask != null) Asked?.Invoke(line); }

    readonly List<AgentMark> _sheet = new();

    /// <summary>For the layout check: every agent (rows) in every state (columns), at a size that can be read, in place of the panel's content.</summary>
    public FrameworkElement DemoMarkSheet(double size)
    {
        _panelContent.Children.Clear();
        _sheet.Clear();
        var states = Enum.GetValues<AgentState>();
        var grid = new Grid { RowSpacing = 14, ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 0, 8, 10) };
        foreach (var _ in states) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cast = Cast.All;
        for (int r = 0; r < cast.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < states.Length; c++)
            {
                var mark = new AgentMark(cast[r], _win.Compositor, size);
                _sheet.Add(mark);   // the mark owns the springs its shapes follow, so it must live as long as they show
                mark.Pose(states[c], 0.6, states[c] == AgentState.UsingTool ? "" : null);
                Grid.SetRow(mark.Element, r);
                Grid.SetColumn(mark.Element, c);
                grid.Children.Add(mark.Element);
            }
        }
        _panelContent.Children.Add(grid);
        return grid;
    }

    /// <summary>The Segoe Fluent glyph for a tool an agent names, or the general tool glyph.</summary>
    static string? ToolGlyph(string? tool) => tool?.ToLowerInvariant() switch
    {
        null => null,
        "files" => "",
        "calendar" => "",
        "web" or "search" => "",
        "apps" => "",
        "media" => "",
        "mail" => "",
        _ => "",
    };

    /// <summary>Narrator hears ambient changes politely and things that need you assertively (MOTION.md 11).</summary>
    static void Announce(UIElement element, Activity activity)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,
            activity.Priority == Priority.ActNow ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
            activity.Narration, activity.Key);
    }

    /// <summary>One capsule's content in three parts: leading (art or glyph), label, trailing (value, equaliser or battery).</summary>
    sealed class CapsuleRow
    {
        public Grid Root { get; }
        readonly Grid _leading, _trailing;
        readonly Border _art;
        readonly FontIcon _glyph;
        readonly FrameworkElement _record;
        readonly StackPanel _meterRow;
        readonly TextBlock _label, _detail, _percent;
        readonly Equaliser _equaliser;
        readonly DotMatrix _meter;
        readonly FontIcon _mic;
        readonly Func<Agent, AgentView?> _agentNow;
        readonly Dictionary<string, AgentMark> _faces = new();
        AgentMark? _face;
        readonly BatteryGauge _gauge;
        readonly StackPanel _battery, _actions;
        readonly Action<string> _command;
        readonly Part _leadPart, _labelPart, _trailPart;
        public string? Key { get; private set; }
        public bool Visible => _labelPart.Shown;

        /// <summary>On screen, or about to be: its entrance is scheduled (it waits for the old content to clear).</summary>
        public bool Showing => Visible || _entering;
        bool _entering;

        public IEnumerable<Probe> Probes()
        {
            yield return new Probe("capsule leading", _art.Visibility == Visibility.Visible ? _art : _record.Visibility == Visibility.Visible ? _record : _glyph, _leading, true);
            yield return new Probe("capsule label (vertical)", _label, Root, false);
            if (_trailing.Children.OfType<FrameworkElement>().FirstOrDefault(e => e.Visibility == Visibility.Visible) is { } trail)
                yield return new Probe("capsule trailing (vertical)", trail, Root, false);
            foreach (var pill in _actions.Children.OfType<Button>())
                if (pill.Content is FrameworkElement content && _actions.Visibility == Visibility.Visible)
                    yield return new Probe("capsule button " + AutomationProperties.GetName(pill), content is Border { Child: FrameworkElement inside } ? inside : content, pill, true);
            yield return new Probe("capsule leading slot (vertical)", _leading, Root, false);
            yield return new Probe("capsule trailing slot (vertical)", _trailing, Root, false);
        }

        /// <summary>The capsule's values that have a place in the panel too (F4).</summary>
        public FrameworkElement DetailElement => _detail;
        public FrameworkElement LabelElement => _label;
        public FrameworkElement GaugeElement => _gauge.Root;
        public FrameworkElement PercentElement => _percent;

        /// <summary>
        /// The panel's copies have taken off: the capsule's copies vanish on this frame, so a shared
        /// element is never on screen twice (F4). The next fill shows them again.
        /// </summary>
        public void HideShared()
        {
            foreach (var element in new UIElement[] { _art, _glyph, _record, _detail, _gauge.Root, _percent }) element.Opacity = 0;
            _labelPart.Vanish();   // the label's opacity belongs to its part
        }

        readonly Microsoft.UI.Composition.Compositor _compositor;

        public CapsuleRow(IslandWindow win, Action<string> command, Func<Agent, AgentView?> agentNow)
        {
            var c = win.Compositor;
            _compositor = c;
            _command = command;
            _agentNow = agentNow;
            _art = Art.Frame(22, 6);
            _glyph = IconArt.Glyph("", 16, Palette.Primary);
            _record = IconArt.Record(16, Palette.Recording);
            _leading = new Grid { Width = CapsuleLayout.LeadingSlot, Height = CapsuleLayout.LeadingSlot, VerticalAlignment = VerticalAlignment.Center };
            _leading.Children.Add(_art);
            _leading.Children.Add(_glyph);
            _leading.Children.Add(_record);
            _leading.CenterPoint = new Vector3(12, 12, 0);

            // Text is centred on its letters (cap height to baseline): centring the line box leaves it riding high.
            _label = new TextBlock { FontFamily = TextFace, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, TextLineBounds = TextLineBounds.Tight };
            _detail = new TextBlock { FontFamily = TextFace, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
            Typography.SetNumeralAlignment(_detail, FontNumeralAlignment.Tabular);
            _equaliser = new Equaliser(c);
            _gauge = new BatteryGauge(c);
            _percent = new TextBlock { FontFamily = TextFace, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
            Typography.SetNumeralAlignment(_percent, FontNumeralAlignment.Tabular);
            _battery = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            _battery.Children.Add(_gauge.Root);
            _battery.Children.Add(_percent);
            _meter = new DotMatrix(c);
            // The microphone's mark stays beside the meter whenever the microphone is on (F116), even when
            // the leading slot shows the face of the agent who is listening.
            _mic = IconArt.Glyph(SpeechSession.MicGlyph, 15, Palette.Recording);
            var meter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
            meter.Children.Add(_mic);
            meter.Children.Add(_meter.Root);
            _meterRow = meter;
            _actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            _trailing = new Grid { VerticalAlignment = VerticalAlignment.Center };
            _trailing.Children.Add(_detail);
            _trailing.Children.Add(_equaliser.Root);
            _trailing.Children.Add(_meterRow);
            _trailing.Children.Add(_battery);
            _trailing.Children.Add(_actions);

            Root = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(CapsuleLayout.Inset + 2, 0, CapsuleLayout.Inset + 2, 0), ColumnSpacing = CapsuleLayout.Gap, IsHitTestVisible = false,
            };
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CapsuleLayout.LeadingSlot) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_label, 1);
            Grid.SetColumn(_trailing, 2);
            Root.Children.Add(_leading);
            Root.Children.Add(_label);
            Root.Children.Add(_trailing);

            _leadPart = new Part(_leading, c);
            _labelPart = new Part(_label, c);
            _trailPart = new Part(_trailing, c);
        }

        /// <summary>Lays the row out once, at its final size (MOTION.md 12.1).</summary>
        public void Fill(CapsuleContent content, BodyShape shape, double insetDip, bool refresh)
        {
            var a = content.Activity;
            Key = content.Key;
            foreach (var element in new UIElement[] { _art, _glyph, _record, _detail, _gauge.Root, _percent }) element.Opacity = 1;
            bool art = content.Music?.ArtSmall != null;
            Art.Fill(_art, content.Music?.ArtSmall);
            _art.Visibility = art ? Visibility.Visible : Visibility.Collapsed;
            bool record = !art && a.Mark == Sense.Call;
            _record.Visibility = record ? Visibility.Visible : Visibility.Collapsed;
            // An agent at work shows its face where a glyph would be (it appears only while it is needed).
            var agent = !art && !record ? a.Agent : null;
            if (agent != null && _agentNow(agent) is { } now)
            {
                if (_face?.Agent.Id != agent.Id) { _face?.Remove(); _face = FaceFor(agent); _face.PlaceIn(_leading); }
                _face.Show(now.State);
                if (content.Timer is { } t) _face.Face.Express(TimerFace(t, TimerClock.Now));
            }
            else if (_face != null) { _face.Remove(); _face = null; }
            _glyph.Visibility = art || record || _face != null ? Visibility.Collapsed : Visibility.Visible;
            _glyph.Glyph = a.Glyph;
            _glyph.Foreground = Brush(Palette.Colour(a.Accent, Palette.Primary));
            _label.Text = a.Title;
            _label.Foreground = Brush(Palette.Primary);

            _detail.Visibility = _equaliser.Root.Visibility = _battery.Visibility = _meterRow.Visibility = _actions.Visibility = Visibility.Collapsed;
            Root.IsHitTestVisible = false;
            if (content.Meter)
            {
                _meterRow.Visibility = Visibility.Visible;
                _mic.Visibility = a.Mark == Sense.Microphone ? Visibility.Visible : Visibility.Collapsed;
                _meter.SetColour(Palette.Colour(a.Accent, Palette.Primary));
                if (!refresh) _meter.Clear();
            }
            else if (a.Actions is { } actions)
            {
                // Buttons right in the capsule, so a question can be answered without opening anything.
                _actions.Visibility = Visibility.Visible;
                _actions.Children.Clear();
                _actions.Children.Add(Pill(actions.Label, true, actions.Command));
                if (actions.SecondLabel is { } second && actions.SecondCommand is { } secondCommand) _actions.Children.Add(Pill(second, false, secondCommand));
                if (actions.ThirdLabel is { } third && actions.ThirdCommand is { } thirdCommand) _actions.Children.Add(Pill(third, false, thirdCommand));
                Root.IsHitTestVisible = true;   // only the buttons take the press; elsewhere it opens the panel as usual
            }
            else if (content.Music is { } music)
            {
                _equaliser.Root.Visibility = Visibility.Visible;
                _equaliser.SetColour(music.ColoursOn ? Colours.Of(music.Colours.Highlight) : Palette.Primary);
                _equaliser.SetPlaying(music.Playing);
            }
            else if (content.Battery is { } battery)
            {
                _battery.Visibility = Visibility.Visible;
                _percent.Text = $"{battery.Percent}%";
                _percent.Foreground = Brush(Palette.Secondary);
                _gauge.Show(battery.Percent, battery.Charging, sweep: !refresh && battery.Charging);
            }
            else if (content.Timer is { Phase: TimerPhase.Running or TimerPhase.Paused } timer)
            {
                // The countdown, live: it changes on the second, in place, with nothing animating in between.
                _detail.Visibility = Visibility.Visible;
                _detail.Text = CountdownTimer.Format(timer.Remaining(TimerClock.Now));
                _detail.Foreground = Brush(timer.Phase == TimerPhase.Paused ? Palette.Secondary : Tempo);
            }
            else if (!string.IsNullOrEmpty(a.Detail))
            {
                _detail.Visibility = Visibility.Visible;
                _detail.Text = a.Detail;
                _detail.Foreground = Brush(Palette.Secondary);
            }
            _timer = content.Timer;
            if (content.Music == null) _equaliser.SetPlaying(false);

            Root.Width = shape.Width;
            Root.Height = shape.Height;
            Root.Margin = new Thickness(0, shape.Top + insetDip, 0, 0);
            AutomationProperties.SetName(Root, a.Narration);
            AutomationProperties.SetLiveSetting(Root, a.Priority == Priority.ActNow ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        }

        /// <summary>
        /// The layered entrance. Lead: the art or glyph pops in (a small element, so a little overshoot).
        /// Support: the title settles from 5 DIPs below, 40 ms later. Decoration: the trailing value only fades.
        /// </summary>
        Button Pill(string label, bool primary, string command)
        {
            var island = new IslandButton(IconArt.Label(label, 12, primary ? Palette.OnPrimary : Palette.Primary, TextFace), label,
                                          primary ? ButtonLook.Filled : ButtonLook.Soft, double.NaN, 26, 13, _compositor, new Thickness(12, 0, 12, 0));
            island.Button.Click += (_, _) => _command(command);
            return island;
        }

        TimerView? _timer;
        Expression? _timerFace;

        /// <summary>Tempo's face while its timer runs: calm, alert for the last ten seconds, drowsy when paused, wide-eyed when time is up.</summary>
        Expression TimerFace(TimerView t, double now)
        {
            var face = t.Phase switch
            {
                TimerPhase.Paused => Expression.Sleepy,
                TimerPhase.Ringing => Expression.Surprised,
                _ when t.Remaining(now).TotalSeconds <= 10 => Expression.Perk,
                _ => Expression.Neutral,
            };
            _timerFace = face;
            return face;
        }

        /// <summary>The timer moved on: the digits change in place (and Tempo's face, when its mood changes).</summary>
        public void TimerTime(TimerView view, double now)
        {
            if (_timer == null || !Showing) return;
            _timer = view;
            if (view.Phase is TimerPhase.Running or TimerPhase.Paused && _detail.Visibility == Visibility.Visible)
            {
                _detail.Text = CountdownTimer.Format(view.Remaining(now));
                if (_detail.Foreground is SolidColorBrush b) b.Color = view.Phase == TimerPhase.Paused ? Palette.Secondary : Tempo;
            }
            var before = _timerFace;
            if (_face?.Agent.Id == Cast.Tempo.Id && TimerFace(view, now) is var face && face != before) _face.Face.Express(face);
        }

        public void SetLevel(double level)
        {
            if (!Visible) return;
            if (_meterRow.Visibility == Visibility.Visible) _meter.Set(level);
            _face?.SetLevel(level);
        }

        AgentMark FaceFor(Agent agent) => _faces.TryGetValue(agent.Id, out var face) ? face : _faces[agent.Id] = new AgentMark(agent, _compositor, CapsuleLayout.LeadingSlot, badges: false);

        /// <summary>The agent's state changed while its face is in this row.</summary>
        public void ShowAgent(AgentView view) { if (_face?.Agent.Id == view.Agent.Id) _face.Show(view.State); }

        public void SetAgentLevel(Agent agent, double level) { if (_face?.Agent.Id == agent.Id && Visible) _face.SetLevel(level); }

        public void Enter(int delayMs)
        {
            _entering = true;
            _leadPart.Enter(delayMs, fromScale: 0.55, spring: Feel.Pop);
            _labelPart.Enter(delayMs + 40, fromScale: 0.97, fromY: 5);
            _trailPart.Enter(delayMs + 60, fromScale: 1);
        }

        /// <summary>Everything leaves together, quicker than it came, drifting up slightly.</summary>
        public void Leave(double toScale = 0.9, bool clearing = false)
        {
            _entering = false;
            var spring = clearing ? Part.Clear : Springs.Quick;
            _leadPart.Leave(toScale * 0.85, toY: -2, spring: spring);
            _labelPart.Leave(toScale, toY: -5, spring: spring);
            _trailPart.Leave(1, toY: -2, spring: spring);
            // A hidden row must not keep the compositor drawing: the bars come to rest as they fade.
            _equaliser.SetPlaying(false);
            _face?.Face.AutoBlink(false);   // a face that has left the island stops blinking
            Root.IsHitTestVisible = false;   // a leaving row's buttons no longer take presses
        }

        /// <summary>Forgets what it showed (after the warm-up), so the next content always enters fresh.</summary>
        public void Forget()
        {
            Key = null;
            _equaliser.SetPlaying(false);
        }

        public void Lean(double dx, double dy)
        {
            if (!Visible) return;
            _leadPart.Nudge(dx * 1.4, dy, Springs.Quick);
            _labelPart.Nudge(dx, dy * 0.6, Springs.Quick);
            _trailPart.Nudge(dx * 0.7, dy * 0.4, Springs.Quick);
        }
    }

    /// <summary>One lyric line. Two exist, so the old line can rise away while the new one rises in.</summary>
    sealed class LyricRow
    {
        public const double Size = 13.5;
        public Grid Root { get; }
        public TextBlock Text => _text;
        readonly TextBlock _text;
        readonly Part _part;
        public string? Key { get; private set; }
        public bool Visible => _part.Shown;

        public LyricRow(IslandWindow win)
        {
            _text = new TextBlock
            {
                FontFamily = TextFace, FontSize = Size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                // Centred on its letters: the block runs from the first line's cap height to the last
                // line's baseline, so a line never rides high in the island; lines are exactly 18 apart.
                TextLineBounds = TextLineBounds.Tight,
            };
            Root = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Padding = new Thickness(18, 0, 18, 0) };
            Root.Children.Add(_text);
            _part = new Part(_text, win.Compositor);
            _text.SizeChanged += (_, e) => _text.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);   // it grows and shrinks about its own middle
        }

        public void Fill(string key, string text, bool dim, BodyShape shape, double insetDip)
        {
            Key = key;
            _text.Text = text;
            _text.FontFamily = dim ? Icons : TextFace;
            _text.Foreground = Brush(dim ? Palette.Secondary : Palette.Primary);
            // One line is centred on its letters. Two lines keep their natural spacing (letter-tight
            // bounds would pack them on top of each other) on an 18 DIP rhythm.
            bool two = shape.Name == "lyric2";
            _text.TextLineBounds = two ? TextLineBounds.Full : TextLineBounds.Tight;
            _text.LineHeight = two ? 18 : 0;
            _text.LineStackingStrategy = two ? LineStackingStrategy.BlockLineHeight : LineStackingStrategy.MaxHeight;
            Root.Width = shape.Width;
            Root.Height = shape.Height;
            Root.Margin = new Thickness(0, shape.Top + insetDip, 0, 0);
        }

        public void Enter(int delayMs) => _part.Enter(delayMs, fromScale: 0.96, fromY: 9, spring: Springs.Stream);
        public void Leave() => _part.Leave(0.96, toY: -8, spring: Part.Clear);
        public void Lean(double dx, double dy) { if (Visible) _part.Nudge(dx, dy * 0.5, Springs.Quick); }
    }
}

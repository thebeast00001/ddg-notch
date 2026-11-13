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
sealed record CapsuleContent(Activity Activity, MusicView? Music, BatteryView? Battery)
{
    /// <summary>Content with the same key updates in place; a new key swaps the content over.</summary>
    public string Key => Activity.Key + "|" + Music?.TrackKey + "|" + (Meter ? "meter" : Activity.Actions?.Command);

    /// <summary>A live microphone level shows while you are being listened to.</summary>
    public bool Meter => Activity.Mark == Sense.Microphone && Activity.Priority == Priority.ActNow && Music == null;
}

/// <summary>What the expanded panel shows.</summary>
sealed record PanelModel(Activity? Primary, IReadOnlyList<Activity> Others, MusicView? Music, BatteryView? Battery,
                         IReadOnlyList<BluetoothState> Devices, string ModeLine, DateTimeOffset? RecordingSince = null);

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

        _rows = new[] { new CapsuleRow(win, Send), new CapsuleRow(win, Send) };
        foreach (var row in _rows) _layer.Children.Add(row.Root);
        _lyrics = new[] { new LyricRow(win), new LyricRow(win) };
        foreach (var row in _lyrics) _layer.Children.Add(row.Root);

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
            : string.IsNullOrEmpty(content.Activity.Detail) ? 0 : Measure(content.Activity.Detail, 13, FontWeights.SemiBold);
        return CapsuleLayout.Measure(Measure(content.Activity.Title, 13, FontWeights.SemiBold), trailing, touch);
    }

    /// <summary>The width of the capsule's buttons: each is its label plus 12 DIPs a side, 6 apart.</summary>
    static double ActionsWidth(ActivityActions actions) =>
        Measure(actions.Label, 12, FontWeights.SemiBold) + 24 +
        (actions.SecondLabel is { } second ? 6 + Measure(second, 12, FontWeights.SemiBold) + 24 : 0);

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
        if (current.Key == content.Key && current.Visible)
        {
            current.Fill(content, shape, _win.TopInsetDip, refresh: true);
            return;
        }
        bool swapping = current.Visible;
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
            if (!row.Visible) continue;
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
        ResetPanelWidth();
        if (fromSettings) opening = true;
        foreach (var row in _rows) if (row.Visible) row.Leave(toScale: 0.96);
        HideLyrics();
        _panelContent.Children.Clear();
        _panelArt = null;
        _travelFrom = null;
        _travels.Clear();
        _progressFill = null;
        _recording = null;
        _recordingSince = model.RecordingSince;
        _clockTimer.Stop();

        if (model.Music is { } music && model.Primary?.Key == MediaAdapter.ActivityKey) AddMusic(music);
        else if (model.Primary is { } p) AddHeader(p, capsuleWidth);
        else _panelContent.Children.Add(Text("Nothing happening", 15, FontWeights.SemiBold, Palette.Primary));

        if (model.Battery is { } battery && (model.Music == null || model.Primary?.Source == "power")) AddBattery(battery, travel: model.Primary?.Source == "power");
        if (model.Music is { } other && model.Primary?.Key != MediaAdapter.ActivityKey) AddMiniPlayer(other);
        foreach (var activity in model.Others.Where(a => a.Key != MediaAdapter.ActivityKey).Take(3)) AddLine(activity);
        AddDevices(model.Devices);
        AddFooter(model.ModeLine);
        if (_recording != null) { UpdateTimes(); _clockTimer.Start(); }   // the call's elapsed time, while the panel is open

        _panel.IsHitTestVisible = true;
        _panelContent.Measure(new Windows.Foundation.Size(BodyShape.Panel.Width, double.PositiveInfinity));
        double height = Math.Clamp(Math.Ceiling(_panelContent.DesiredSize.Height), 120, 400);   // room for the full music panel with a recording line

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

    void AddMusic(MusicView music)
    {
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
        Place(Round(IconArt.Drawing(music.Playing ? IconArt.Pause : IconArt.Play, 26, Palette.OnPrimary), music.Playing ? "Pause" : "Play", 56, ButtonLook.Filled, "playpause"), 2);
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
        var play = Round(IconArt.Drawing(music.Playing ? IconArt.Pause : IconArt.Play, 16, Palette.Primary), music.Playing ? "Pause" : "Play", 32, ButtonLook.Quiet, "playpause");
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
            _panelContent.Children.Add(buttons);
        }
    }

    /// <summary>A panel button for an activity's answer: the first one filled, the second quiet.</summary>
    Button ActionButton(string label, bool primary, string command, double height = 32)
    {
        var button = new IslandButton(IconArt.Label(label, 13, primary ? Palette.OnPrimary : Palette.Primary, TextFace), label,
                                      primary ? ButtonLook.Filled : ButtonLook.Soft, double.NaN, height, height / 2, _win.Compositor, new Thickness(18, 0, 18, 0));
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

    void AddFooter(string modeLine)
    {
        var footer = new Grid();
        var mode = Text(modeLine, 11, FontWeights.Normal, Palette.Tertiary, trim: true);
        mode.TextLineBounds = TextLineBounds.Tight;   // centred on its letters, like the links beside it
        mode.VerticalAlignment = VerticalAlignment.Center;
        mode.Margin = new Thickness(0, 0, 110, 0);
        footer.Children.Add(mode);
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        links.Children.Add(TextLink("Settings", "Open settings", "settings"));
        links.Children.Add(TextLink("Quit", "Quit Forty", "quit"));
        footer.Children.Add(links);
        _panelContent.Children.Add(footer);
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
        readonly TextBlock _label, _detail, _percent;
        readonly Equaliser _equaliser;
        readonly LevelMeter _meter;
        readonly BatteryGauge _gauge;
        readonly StackPanel _battery, _actions;
        readonly Action<string> _command;
        readonly Part _leadPart, _labelPart, _trailPart;
        public string? Key { get; private set; }
        public bool Visible => _labelPart.Shown;

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

        public CapsuleRow(IslandWindow win, Action<string> command)
        {
            var c = win.Compositor;
            _compositor = c;
            _command = command;
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
            _meter = new LevelMeter(c);
            _actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            _trailing = new Grid { VerticalAlignment = VerticalAlignment.Center };
            _trailing.Children.Add(_detail);
            _trailing.Children.Add(_equaliser.Root);
            _trailing.Children.Add(_meter.Root);
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
            _glyph.Visibility = art || record ? Visibility.Collapsed : Visibility.Visible;
            _glyph.Glyph = a.Glyph;
            _glyph.Foreground = Brush(Palette.Colour(a.Accent, Palette.Primary));
            _label.Text = a.Title;
            _label.Foreground = Brush(Palette.Primary);

            _detail.Visibility = _equaliser.Root.Visibility = _battery.Visibility = _meter.Root.Visibility = _actions.Visibility = Visibility.Collapsed;
            Root.IsHitTestVisible = false;
            if (content.Meter)
            {
                _meter.Root.Visibility = Visibility.Visible;
                _meter.SetColour(Palette.Colour(a.Accent, Palette.Primary));
                if (!refresh) _meter.Set(0);
            }
            else if (a.Actions is { } actions)
            {
                // Buttons right in the capsule, so a question can be answered without opening anything.
                _actions.Visibility = Visibility.Visible;
                _actions.Children.Clear();
                _actions.Children.Add(Pill(actions.Label, true, actions.Command));
                if (actions.SecondLabel is { } second && actions.SecondCommand is { } secondCommand) _actions.Children.Add(Pill(second, false, secondCommand));
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
            else if (!string.IsNullOrEmpty(a.Detail))
            {
                _detail.Visibility = Visibility.Visible;
                _detail.Text = a.Detail;
                _detail.Foreground = Brush(Palette.Secondary);
            }
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

        public void SetLevel(double level) { if (_meter.Root.Visibility == Visibility.Visible && Visible) _meter.Set(level); }

        public void Enter(int delayMs)
        {
            _leadPart.Enter(delayMs, fromScale: 0.55, spring: Feel.Pop);
            _labelPart.Enter(delayMs + 40, fromScale: 0.97, fromY: 5);
            _trailPart.Enter(delayMs + 60, fromScale: 1);
        }

        /// <summary>Everything leaves together, quicker than it came, drifting up slightly.</summary>
        public void Leave(double toScale = 0.9, bool clearing = false)
        {
            var spring = clearing ? Part.Clear : Springs.Quick;
            _leadPart.Leave(toScale * 0.85, toY: -2, spring: spring);
            _labelPart.Leave(toScale, toY: -5, spring: spring);
            _trailPart.Leave(1, toY: -2, spring: spring);
            // A hidden row must not keep the compositor drawing: the bars come to rest as they fade.
            _equaliser.SetPlaying(false);
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

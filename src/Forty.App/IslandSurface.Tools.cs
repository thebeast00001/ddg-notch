using System.Numerics;
using Forty.Core;
using Forty.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Forty.App;

/// <summary>The timer as the island shows it. Times are wall-clock seconds (<see cref="TimerClock"/>), so a timer keeps time through sleep.</summary>
sealed record TimerView(TimerPhase Phase, TimeSpan Duration, TimeSpan Left, double At)
{
    public TimeSpan Remaining(double now) => Phase == TimerPhase.Running ? TimeSpan.FromSeconds(Math.Max(0, Left.TotalSeconds - (now - At))) : Left;
    public double Fraction(double now) => Duration.TotalSeconds <= 0 ? 0 : Remaining(now).TotalSeconds / Duration.TotalSeconds;
    public DateTime EndsAt(double now) => DateTime.Now + Remaining(now);

    /// <summary>The widest the countdown will be, with every digit as wide as a zero (the capsule is sized once, for this).</summary>
    public string Widest => System.Text.RegularExpressions.Regex.Replace(CountdownTimer.Format(Duration), @"\d", "0");
}

/// <summary>Seconds on the wall clock: a timer set before the PC sleeps still ends when it should.</summary>
static class TimerClock
{
    public static double Now => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
}

/// <summary>
/// The tool pages: the timer and the mirror. Each takes the whole panel, like Settings, and is rebuilt
/// only when what it shows changes kind (setting, running, ringing), never for a tick of the clock: the
/// digits and the progress bar change in place.
/// </summary>
sealed partial class IslandSurface
{
    public const string TimerKey = "timer";
    static readonly Color TempoInk = Colours.Of(Cast.Tempo.Hue);
    static Color Tempo => Palette.Contrast ? Palette.NeedsYou : TempoInk;   // the highlight colour in a contrast theme
    static readonly Color OnTempo = Color.FromArgb(255, 22, 14, 38);

    string? _toolKey;
    double _toolHeight;
    TimerRuler? _ruler;
    RollingDigits? _digits;
    TextBlock? _toolLine;
    Border? _timerFill;
    AgentMark? _tempoMark;
    CameraMirror? _mirror;
    Grid? _mirrorHolder;
    StackPanel? _mirrorNote;
    TimerView? _timerShown;

    /// <summary>Ticks while the ruler turns, and the start chime.</summary>
    public Sounds Sounds { get; } = new();

    /// <summary>True while the ruler is held: the island stays open even if the pointer strays outside it.</summary>
    public bool Holding => _ruler?.Dragging == true;

    /// <summary>A tool page is on screen.</summary>
    public string? Tool => _toolKey?.Split(':')[0];

    /// <summary>Leaves whatever tool page is showing: the camera goes off at once and the ruler stops.</summary>
    void LeaveTool()
    {
        ClearPageFaces();
        if (_toolKey == null) return;
        _toolKey = null;
        _mirror?.Stop();
        _ruler?.Stop();
        _ruler = null;
        _digits = null;
        _toolLine = null;
        _timerFill = null;
        _timerShown = null;
        _tempoMark?.Remove();
        _ringFace?.Remove();
        _ringFace = null;
        if (_mirrorHolder != null) { _mirrorHolder.Children.Clear(); _mirrorHolder = null; }
        ResetPanelWidth();
    }

    /// <summary>Clears the panel for a tool page, the way Settings does.</summary>
    void BeginTool(string key)
    {
        LeaveTool();
        _toolKey = key;
        foreach (var row in _rows) if (row.Showing) row.Leave(toScale: 0.96);
        HideLyrics();
        _panelContent.Children.Clear();
        _panelArt = null;
        _progressFill = null;
        _clockTimer.Stop();
        _panel.Width = BodyShape.Panel.Width;
        _panelContent.CenterPoint = new Vector3((float)BodyShape.Panel.Width / 2, 0, 0);
    }

    double EndTool(bool opening)
    {
        _panel.IsHitTestVisible = true;
        _panelContent.Measure(new Windows.Foundation.Size(_panel.Width, double.PositiveInfinity));
        _toolHeight = Math.Clamp(Math.Ceiling(_panelContent.DesiredSize.Height), 120, Math.Min(560, _win.HeightDip - 16));
        // One entrance for the page: it arrives as a piece, from the panel it replaces or from the capsule.
        _panelPart.Enter(opening ? 70 : 0, fromScale: opening ? 0.97 : 0.985, fromY: opening ? -4 : 3);
        return _toolHeight;
    }

    /// <summary>The page's header: the face of whoever's tool it is (or none), its name, and a close button that goes back to the panel.</summary>
    Grid ToolHeader(string title, Agent? agent, FrameworkElement? trailing = null)
    {
        var header = new Grid();   // no column spacing: an empty face column must not indent the title
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (agent != null)
        {
            _tempoMark ??= new AgentMark(agent, _win.Compositor, 22, badges: false);
            var holder = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            _tempoMark.PlaceIn(holder);
            _tempoMark.Show(AgentState.Idle);
            header.Children.Add(holder);
        }
        var name = Text(title, 15, FontWeights.SemiBold, Palette.Primary, display: true);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        if (trailing != null) { trailing.VerticalAlignment = VerticalAlignment.Center; trailing.Margin = new Thickness(8, 0, 6, 0); Grid.SetColumn(trailing, 2); header.Children.Add(trailing); }
        var close = Round(IconArt.Glyph("", 11, Palette.Secondary), "Close", 28, ButtonLook.Quiet, "tool-close");
        Grid.SetColumn(close, 3);
        header.Children.Add(close);
        return header;
    }

    // ── The timer (Tempo's) ──

    /// <summary>
    /// The timer page, in one of three kinds: setting it (the ruler), running or paused (the countdown),
    /// or ringing. Returns the height it needs. <paramref name="pickMinutes"/> is where the ruler starts.
    /// </summary>
    public double ShowTimer(TimerView view, int pickMinutes, bool opening)
    {
        string kind = view.Phase switch { TimerPhase.Off => "set", TimerPhase.Ringing => "ring", TimerPhase.Paused => "pause", _ => "run" };
        string key = "timer:" + kind;
        if (!opening && _toolKey == key && _panel.Width == BodyShape.Panel.Width) { TimerTime(view); return _toolHeight; }
        BeginTool(key);
        _timerShown = view;
        double now = TimerClock.Now;

        if (kind == "ring")
        {
            // Time's up: Tempo's face, wide-eyed, and the two answers. Nothing else competes.
            var top = new Grid { ColumnSpacing = 14 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _tempoMark ??= new AgentMark(Cast.Tempo, _win.Compositor, 22, badges: false);
            var big = new AgentMark(Cast.Tempo, _win.Compositor, 44, badges: false);
            _ringFace = big;
            var faceHolder = new Grid { VerticalAlignment = VerticalAlignment.Center };
            big.PlaceIn(faceHolder);
            big.Show(AgentState.Idle);
            big.Face.Express(Expression.Surprised);
            _ = HappyLater(big);
            top.Children.Add(faceHolder);
            var words = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Text("Time's up", 22, FontWeights.SemiBold, Palette.Primary, display: true));
            words.Children.Add(Text($"{CountdownTimer.Words(view.Duration)} timer", 12.5, FontWeights.Normal, Palette.Secondary));
            Grid.SetColumn(words, 1);
            top.Children.Add(words);
            var close = Round(IconArt.Glyph("", 11, Palette.Secondary), "Close", 28, ButtonLook.Quiet, "tool-close");
            close.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(close, 2);
            top.Children.Add(close);
            _panelContent.Children.Add(top);
            var buttons = EvenRow(
                WideButton("+1 min", "Add a minute", ButtonLook.Soft, Palette.Primary, "timer-more"),
                WideButton("Stop", "Stop the alarm", TempoLook, OnTempo, "timer-stop"));
            _panelContent.Children.Add(buttons);
            AutomationProperties.SetName(top, $"Time's up. {CountdownTimer.Words(view.Duration)} timer.");
            Announce(top, Activity.Live(TimerKey, "planner", Priority.ActNow, "Time's up", null, "", $"Time's up. Your {CountdownTimer.Words(view.Duration)} timer has finished."));
            return EndTool(opening);
        }

        _panelContent.Children.Add(ToolHeader("Timer", Cast.Tempo));

        // The digits: large, light and tabular, rolling as they change (up when you add time, down as it counts).
        _digits = new RollingDigits(_win.Compositor, 58, DisplayFace, FontWeights.Light, Palette.Primary);
        _digits.Root.Margin = new Thickness(0, 6, 0, 0);
        _panelContent.Children.Add(_digits.Root);
        _toolLine = Text("", 12, FontWeights.Normal, Palette.Tertiary);
        _toolLine.HorizontalAlignment = HorizontalAlignment.Center;
        _toolLine.Margin = new Thickness(0, -6, 0, 2);
        Typography.SetNumeralAlignment(_toolLine, FontNumeralAlignment.Tabular);
        _panelContent.Children.Add(_toolLine);

        if (kind == "set")
        {
            int start = Math.Clamp(pickMinutes, TimerRuler.Min, TimerRuler.Max);
            _digits.Set(CountdownTimer.Format(TimeSpan.FromMinutes(start)), 0);
            _toolLine.Text = EndsLine(TimeSpan.FromMinutes(start));
            var ruler = _ruler = new TimerRuler(_win.Compositor, BodyShape.Panel.Width - PanelPadding.Left - PanelPadding.Right, start, Tempo);
            ruler.Changed += (minutes, direction) =>
            {
                Sounds.Tick();
                _digits?.Set(CountdownTimer.Format(TimeSpan.FromMinutes(minutes)), direction);
                if (_toolLine != null) _toolLine.Text = EndsLine(TimeSpan.FromMinutes(minutes));
            };
            ruler.Settled += minutes => AutomationProperties.SetName(ruler.Root, $"Timer length, {CountdownTimer.Words(TimeSpan.FromMinutes(minutes))}");
            ruler.Commit += () => Send("timer-start:" + (int)TimeSpan.FromMinutes(ruler.Value).TotalSeconds);
            _panelContent.Children.Add(ruler.Root);
            var startButton = WideButton("Start", "Start the timer", TempoLook, OnTempo, null);
            startButton.Click += (_, _) => Send("timer-start:" + (int)TimeSpan.FromMinutes(ruler.Value).TotalSeconds);
            startButton.Margin = new Thickness(0, 4, 0, 0);
            _panelContent.Children.Add(startButton);
            return EndTool(opening);
        }

        // Running or paused: the countdown, a bar that drains as it goes, and the controls.
        var left = view.Remaining(now);
        _digits.Set(CountdownTimer.Format(left), 0);
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Brush(Color.FromArgb(30, 255, 255, 255)), Margin = new Thickness(0, 4, 0, 6) };
        _timerFill = new Border { CornerRadius = new CornerRadius(2), Background = Brush(Tempo), HorizontalAlignment = HorizontalAlignment.Stretch };
        track.Child = _timerFill;
        _panelContent.Children.Add(track);
        bool paused = view.Phase == TimerPhase.Paused;
        _panelContent.Children.Add(EvenRow(
            WideButton("Cancel", "Cancel the timer", ButtonLook.Soft, Palette.Primary, "timer-cancel"),
            WideButton("+1 min", "Add a minute", ButtonLook.Soft, Palette.Primary, "timer-more"),
            WideButton(paused ? "Resume" : "Pause", paused ? "Resume the timer" : "Pause the timer", TempoLook, OnTempo, paused ? "timer-resume" : "timer-pause")));
        TimerTime(view);
        double height = EndTool(opening);
        _panel.UpdateLayout();
        Drain(view);
        return height;
    }

    AgentMark? _ringFace;

    static async Task HappyLater(AgentMark face)
    {
        await Task.Delay(1100);
        if (face.Element.Parent != null) face.Face.Express(Expression.Happy);
    }

    static ButtonLook TempoLook => new(Tempo, Shade(Tempo, 0.9), Shade(Tempo, 0.8));
    static Color Shade(Color c, double f) => Color.FromArgb(c.A, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    static string EndsLine(TimeSpan left) => $"Ends at {DateTime.Now + left:t}";

    /// <summary>A full-width pill (the timer's Start, Stop and the controls under the countdown).</summary>
    Button WideButton(string label, string name, ButtonLook look, Color ink, string? command)
    {
        var button = new IslandButton(IconArt.Label(label, 14, ink, TextFace), name, look, double.NaN, 40, 20, _win.Compositor);
        button.Button.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (command != null) button.Button.Click += (_, _) => Send(command);
        return button;
    }

    static Grid EvenRow(params Button[] buttons)
    {
        var row = new Grid { ColumnSpacing = 10 };
        for (int i = 0; i < buttons.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(buttons[i], i);
            row.Children.Add(buttons[i]);
        }
        return row;
    }

    /// <summary>
    /// The bar drains on the compositor: one linear animation from where it is to empty over the time
    /// left, so nothing on this side runs while it moves. Paused, it holds still.
    /// </summary>
    void Drain(TimerView view)
    {
        if (_timerFill is not { } fill) return;
        double now = TimerClock.Now;
        float fraction = (float)Math.Clamp(view.Fraction(now), 0, 1);
        var visual = ElementCompositionPreview.GetElementVisual(fill);
        visual.StopAnimation("Scale.X");
        visual.CenterPoint = new Vector3(0, 2, 0);
        visual.Scale = new Vector3(fraction, 1, 1);
        if (view.Phase != TimerPhase.Running || MotionPolicy.Reduced && view.Remaining(now).TotalSeconds > 1) return;
        var c = _win.Compositor;
        var drain = c.CreateScalarKeyFrameAnimation();
        drain.InsertKeyFrame(0, fraction);
        drain.InsertKeyFrame(1, 0, c.CreateLinearEasingFunction());
        drain.Duration = TimeSpan.FromSeconds(Math.Max(0.05, view.Remaining(now).TotalSeconds));
        visual.StartAnimation("Scale.X", drain);
    }

    /// <summary>The timer moved on (once a second, on the second): the capsule's digits and the page's, in place.</summary>
    public void TimerTime(TimerView view)
    {
        double now = TimerClock.Now;
        var left = view.Remaining(now);
        foreach (var row in _rows) row.TimerTime(view, now);
        if (_card != null && _panelContent.Children.Contains(_card) && _cardDigits != null)
        {
            _cardDigits.Text = CountdownTimer.Format(left);
            if (view.Phase == TimerPhase.Running && _cardLine != null) _cardLine.Text = EndsLine(left);
        }
        if (_toolKey is not ("timer:run" or "timer:pause")) return;   // the ruler's digits are yours, not the clock's
        bool phaseChanged = _timerShown?.Phase != view.Phase || _timerShown?.Duration != view.Duration;
        _timerShown = view;
        _digits?.Set(CountdownTimer.Format(left), -1);
        _digits?.SetColour(view.Phase == TimerPhase.Paused ? Palette.Secondary : Palette.Primary);
        if (_toolLine != null) _toolLine.Text = view.Phase == TimerPhase.Paused ? $"Paused · {CountdownTimer.Words(view.Duration)}" : $"{EndsLine(left)} · {CountdownTimer.Words(view.Duration)}";
        if (phaseChanged) Drain(view);
        // Tempo's face counts along: calm while it runs, alert for the last ten seconds, drowsy when paused.
        _tempoMark?.Face.Express(view.Phase == TimerPhase.Paused ? Expression.Sleepy : left.TotalSeconds <= 10 ? Expression.Perk : Expression.Neutral);
    }

    /// <summary>Keyboard focus for a tool page: the ruler (so the arrow keys set the minutes), or the page's first button.</summary>
    public void FocusTool()
    {
        if (_ruler != null) _ruler.Root.Focus(FocusState.Keyboard);
        else FocusFirst();
    }

    Grid? _card;
    TextBlock? _cardDigits, _cardLine;

    /// <summary>
    /// The timer in the panel, when the island opens while one runs (hovering its capsule opens into its
    /// controls, as music opens into its player): Tempo, the countdown ticking in place, and pause and
    /// cancel. Pressing Tempo's side opens the timer's own page.
    /// </summary>
    void AddTimerCard(TimerView view)
    {
        double now = TimerClock.Now;
        bool ringing = view.Phase == TimerPhase.Ringing, paused = view.Phase == TimerPhase.Paused;
        var card = _card = new Grid { ColumnSpacing = 10, Margin = new Thickness(-6, 0, 0, 0) };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _tempoMark ??= new AgentMark(Cast.Tempo, _win.Compositor, 22, badges: false);
        var holder = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _tempoMark.PlaceIn(holder);
        _tempoMark.Show(AgentState.Idle);
        _tempoMark.Face.Express(ringing ? Expression.Surprised : paused ? Expression.Sleepy : Expression.Neutral);
        who.Children.Add(holder);
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Text(ringing ? "Time's up" : paused ? "Timer paused" : "Timer", 13.5, FontWeights.SemiBold, Palette.Primary));
        _cardLine = Text(ringing ? $"{CountdownTimer.Words(view.Duration)} timer" : paused ? CountdownTimer.Words(view.Duration) : EndsLine(view.Remaining(now)), 11.5, FontWeights.Normal, Palette.Tertiary);
        Typography.SetNumeralAlignment(_cardLine, FontNumeralAlignment.Tabular);
        words.Children.Add(_cardLine);
        who.Children.Add(words);
        var open = new IslandButton(who, "Open the timer", ButtonLook.Quiet, double.NaN, 44, 14, _win.Compositor, new Thickness(6, 0, 12, 0));
        open.Button.HorizontalAlignment = HorizontalAlignment.Left;
        open.Button.Click += (_, _) => Send("tool:timer");
        card.Children.Add(open);

        _cardDigits = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (ringing)
        {
            buttons.Children.Add(ActionButton("+1 min", false, "timer-more", height: 30));
            var stop = new IslandButton(IconArt.Label("Stop", 12.5, OnTempo, TextFace), "Stop the alarm", TempoLook, double.NaN, 30, 15, _win.Compositor, new Thickness(16, 0, 16, 0));
            stop.Button.Click += (_, _) => Send("timer-stop");
            buttons.Children.Add(stop);
        }
        else
        {
            _cardDigits = Text(CountdownTimer.Format(view.Remaining(now)), 20, FontWeights.SemiBold, paused ? Palette.Secondary : Tempo, display: true);
            _cardDigits.VerticalAlignment = VerticalAlignment.Center;
            Typography.SetNumeralAlignment(_cardDigits, FontNumeralAlignment.Tabular);
            Grid.SetColumn(_cardDigits, 1);
            card.Children.Add(_cardDigits);
            buttons.Children.Add(Round(IconArt.Glyph(paused ? "\uE768" : "\uE769", 12, Palette.Primary), paused ? "Resume the timer" : "Pause the timer", 30, ButtonLook.Soft, paused ? "timer-resume" : "timer-pause"));
            buttons.Children.Add(Round(IconArt.Glyph("\uE711", 11, Palette.Primary), "Cancel the timer", 30, ButtonLook.Soft, "timer-cancel"));
        }
        Grid.SetColumn(buttons, 2);
        card.Children.Add(buttons);
        AutomationProperties.SetName(card, ringing ? "Time's up" : $"Timer, {CountdownTimer.Words(TimeSpan.FromSeconds(Math.Ceiling(view.Remaining(now).TotalSeconds)))} left{(paused ? ", paused" : "")}");
        _panelContent.Children.Add(card);
    }

    /// <summary>For the layout check: the ruler moved to a value, as if dragged there.</summary>
    public void DemoRuler(int minutes) => _ruler?.Jump(minutes);

    // ── The mirror ──

    /// <summary>The mirror page: the camera (on only while this page shows), how to get it back if it's off, and what happens to the picture (nothing).</summary>
    public double ShowMirror(bool opening)
    {
        if (!opening && _toolKey == "mirror" && _panel.Width == BodyShape.Panel.Width) return _toolHeight;
        BeginTool("mirror");
        var dot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        dot.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = Brush(Palette.Recording), VerticalAlignment = VerticalAlignment.Center });
        var on = Text("Camera on", 11.5, FontWeights.Normal, Palette.Secondary);
        on.VerticalAlignment = VerticalAlignment.Center;
        dot.Children.Add(on);
        dot.Margin = new Thickness(0, 0, 4, 0);
        _panelContent.Children.Add(ToolHeader("Mirror", null, dot));

        _mirror ??= new CameraMirror(_win.Compositor);
        _mirrorHolder = new Grid { Width = CameraMirror.Width, Height = CameraMirror.Height, HorizontalAlignment = HorizontalAlignment.Center };
        _mirrorHolder.Children.Add(_mirror.Frame);
        _mirrorNote = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _mirrorHolder.Children.Add(_mirrorNote);
        _panelContent.Children.Add(_mirrorHolder);
        var privacy = Text("Only you can see this. Nothing is saved or sent, and the camera turns off when you close it, or after 2 minutes.", 11.5, FontWeights.Normal, Palette.Tertiary);
        privacy.TextWrapping = TextWrapping.Wrap;
        privacy.Margin = new Thickness(2, -4, 2, 0);
        _panelContent.Children.Add(privacy);

        _mirror.Changed -= MirrorChanged;
        _mirror.Changed += MirrorChanged;
        void Shown(MirrorState state)
        {
            dot.Visibility = state == MirrorState.On ? Visibility.Visible : Visibility.Collapsed;
        }
        _mirrorDot = Shown;
        Shown(MirrorState.Starting);
        double height = EndTool(opening);
        _ = _mirror.StartAsync();
        return height;
    }

    Action<MirrorState>? _mirrorDot;

    void MirrorChanged(MirrorState state)
    {
        if (_toolKey != "mirror" || _mirrorNote is not { } note) return;
        _mirrorDot?.Invoke(state);
        note.Children.Clear();
        string? message = state switch
        {
            MirrorState.Starting => null,
            MirrorState.Denied => "Camera access is off for desktop apps",
            MirrorState.NoCamera => "No camera found on this PC",
            MirrorState.Busy => "Another app is using the camera",
            MirrorState.Failed => "The camera didn't start",
            _ => null,
        };
        note.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        if (message == null) return;
        var glyph = IconArt.Glyph("", 22, Palette.Tertiary);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        note.Children.Add(glyph);
        var words = Text(message, 13, FontWeights.SemiBold, Palette.Secondary);
        words.HorizontalAlignment = HorizontalAlignment.Center;
        note.Children.Add(words);
        if (state == MirrorState.Denied)
        {
            var open = ActionButton("Open camera settings", false, "mirror-settings", height: 30);
            open.HorizontalAlignment = HorizontalAlignment.Center;
            note.Children.Add(open);
        }
        else if (state is MirrorState.Busy or MirrorState.Failed)
        {
            var again = ActionButton("Try again", false, "tool:mirror-retry", height: 30);
            again.HorizontalAlignment = HorizontalAlignment.Center;
            note.Children.Add(again);
        }
        AutomationProperties.SetName(note, message);
    }

    /// <summary>The mirror's Try again: the camera is asked once more, on the page that is already open.</summary>
    public void RetryMirror() { if (_toolKey == "mirror" && _mirror != null) _ = _mirror.StartAsync(); }

    /// <summary>For the layout check: the mirror page showing how it looks when the camera can't be used.</summary>
    public void DemoMirror(MirrorState state) => MirrorChanged(state);
}

/// <summary>
/// Digits that roll as they change, like Apple's numeric text: each digit that changes slides out and
/// its replacement slides in from the side the number is moving to (up when it grows, down as it counts
/// down), with a fade, in 320 ms on a decelerating curve. Digits that stay the same don't move. Every
/// digit cell is as wide as the widest digit, so nothing shifts sideways as numbers change.
/// </summary>
sealed class RollingDigits
{
    public StackPanel Root { get; }
    readonly Compositor _c;
    readonly double _size, _digitW, _colonW, _cellH;
    readonly FontFamily _face;
    readonly Windows.UI.Text.FontWeight _weight;
    readonly List<(Grid Cell, TextBlock Text)> _cells = new();
    string _text = "";
    Color _colour;

    public RollingDigits(Compositor c, double size, FontFamily face, Windows.UI.Text.FontWeight weight, Color colour)
    {
        _c = c; _size = size; _face = face; _weight = weight; _colour = colour;
        var probe = Make("0");
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _digitW = Math.Ceiling(probe.DesiredSize.Width);
        _cellH = Math.Ceiling(probe.DesiredSize.Height);
        var colon = Make(":");
        colon.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _colonW = Math.Ceiling(colon.DesiredSize.Width);
        Root = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = _cellH };
        AutomationProperties.SetLiveSetting(Root, AutomationLiveSetting.Off);
    }

    TextBlock Make(string ch)
    {
        var t = new TextBlock
        {
            Text = ch, FontFamily = _face, FontSize = _size, FontWeight = _weight, Foreground = new SolidColorBrush(_colour),
            TextLineBounds = TextLineBounds.Tight, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    public void SetColour(Color colour)
    {
        if (_colour == colour) return;
        _colour = colour;
        foreach (var (_, text) in _cells) if (text.Foreground is SolidColorBrush b) Ease.Colour(b, colour, 200);
    }

    /// <param name="direction">+1 when the number grew, -1 when it shrank, 0 to change without motion.</param>
    public void Set(string text, int direction)
    {
        if (text == _text) return;
        bool rebuild = text.Length != _text.Length || MotionPolicy.Reduced;
        string old = _text;
        _text = text;
        AutomationProperties.SetName(Root, text);
        if (rebuild)
        {
            Root.Children.Clear();
            _cells.Clear();
            foreach (char ch in text)
            {
                var cell = new Grid { Width = ch == ':' ? _colonW : _digitW, Height = _cellH };
                var t = Make(ch.ToString());
                cell.Children.Add(t);
                Root.Children.Add(cell);
                _cells.Add((cell, t));
                if (direction != 0 && old.Length > 0 && !MotionPolicy.Reduced) Slide(t, (float)(direction * _cellH * 0.5), 0, 0, 1);
            }
            return;
        }
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == old[i]) continue;
            var (cell, current) = _cells[i];
            var next = Make(text[i].ToString());
            cell.Children.Add(next);
            _cells[i] = (cell, next);
            if (direction == 0) { cell.Children.Remove(current); continue; }
            float travel = (float)(_cellH * 0.55) * direction;
            // Digits further right change more often: they move a touch less, so the eye reads the larger digits first.
            Slide(current, 0, -travel, 1, 0);
            Slide(next, travel, 0, 0, 1);
            _ = RemoveLater(cell, current);
        }
    }

    void Slide(UIElement element, float fromY, float toY, float fromOpacity, float toOpacity)
    {
        var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));
        var move = _c.CreateVector3KeyFrameAnimation();
        move.Target = "Translation";
        move.InsertKeyFrame(0, new Vector3(0, fromY, 0));
        move.InsertKeyFrame(1, new Vector3(0, toY, 0), ease);
        move.Duration = TimeSpan.FromMilliseconds(320);
        var fade = _c.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, fromOpacity);
        fade.InsertKeyFrame(1, toOpacity, toOpacity > fromOpacity ? _c.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0), new Vector2(0.6f, 1)) : ease);
        fade.Duration = TimeSpan.FromMilliseconds(toOpacity > fromOpacity ? 320 : 220);
        element.StartAnimation(move);
        element.StartAnimation(fade);
    }

    static async Task RemoveLater(Grid cell, UIElement element)
    {
        await Task.Delay(340);
        cell.Children.Remove(element);
    }
}

/// <summary>
/// The timer's ruler: a strip of minute ticks under a fixed line in Tempo's colour. Drag it and it
/// follows the pointer 1:1; throw it and it coasts, snapping onto a minute; past either end it resists.
/// Ticks near the line grow taller, like a lens, and fade toward the edges. Each minute that crosses the
/// line ticks (a sound and a small pulse of the line). A wheel notch or an arrow key moves one minute;
/// Page Up and Page Down move five; a click on a tick goes to it. Everything that moves is drawn by the
/// compositor from one number (where the strip is), so a frame costs one property write.
/// </summary>
sealed class TimerRuler
{
    public const double Step = 10, Height = 62;
    public const int Min = 1, Max = 180;
    const double TickY = 22, LabelY = 40;

    readonly Compositor _c;
    readonly Grid _host;
    readonly Border _needle;
    readonly CompositionPropertySet _p;
    readonly RulerMotion _motion;
    readonly double _width;
    readonly List<object> _keep = new();
    double _lastX, _travel;
    bool _looping;
    int _shown;

    public ContentControl Root { get; }
    public event Action<int, int>? Changed;
    public event Action<int>? Settled;
    public event Action? Commit;

    public int Value => _motion.Target;
    public bool Dragging => _motion.Dragging;

    public TimerRuler(Compositor c, double width, int value, Color ink)
    {
        _c = c;
        _width = width;
        _motion = new RulerMotion(Step, Min, Max, value);
        _shown = value;
        _p = c.CreatePropertySet();
        _p.InsertScalar("x", (float)_motion.Position);

        _host = new Grid { Width = width, Height = Height, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _host.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, Height) };

        // The ticks: one sprite each, placed and lit by expressions of the strip's position.
        var strip = c.CreateContainerVisual();
        var minor = c.CreateRoundedRectangleGeometry(); minor.Size = new Vector2(2, 11); minor.CornerRadius = new Vector2(1, 1);
        var major = c.CreateRoundedRectangleGeometry(); major.Size = new Vector2(2, 20); major.CornerRadius = new Vector2(1, 1);
        var minorClip = c.CreateGeometricClip(minor);
        var majorClip = c.CreateGeometricClip(major);
        var white = c.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
        float centre = (float)(width / 2), half = (float)(width / 2);
        for (int m = Min - 1; m <= Max; m++)
        {
            bool big = m % 5 == 0;
            float h = big ? 20 : 11, x = (float)((m - Min) * Step);
            var tick = c.CreateSpriteVisual();
            tick.Size = new Vector2(2, h);
            tick.Brush = white;
            tick.Clip = big ? majorClip : minorClip;
            tick.CenterPoint = new Vector3(1, h / 2, 0);
            var place = c.CreateExpressionAnimation("Vector3(bx - p.x + c - 1, y, 0)");
            place.SetReferenceParameter("p", _p);
            place.SetScalarParameter("bx", x); place.SetScalarParameter("c", centre); place.SetScalarParameter("y", (float)(TickY - h / 2));
            tick.StartAnimation("Offset", place);
            var light = c.CreateExpressionAnimation("Clamp(1 - Pow(Abs(bx - p.x) / h, 2), 0, 1) * o");
            light.SetReferenceParameter("p", _p);
            light.SetScalarParameter("bx", x); light.SetScalarParameter("h", half); light.SetScalarParameter("o", big ? 0.92f : 0.5f);
            tick.StartAnimation("Opacity", light);
            var lens = c.CreateExpressionAnimation("Vector3(1, 1 + 0.5 * Clamp(1 - Abs(bx - p.x) / 36, 0, 1), 1)");
            lens.SetReferenceParameter("p", _p);
            lens.SetScalarParameter("bx", x);
            tick.StartAnimation("Scale", lens);
            strip.Children.InsertAtTop(tick);
        }
        var ticks = new Grid { IsHitTestVisible = false };
        ElementCompositionPreview.SetElementChildVisual(ticks, strip);
        _host.Children.Add(ticks);

        // The labels: every five minutes, under the long ticks, moving and fading with them.
        var labels = new Canvas { IsHitTestVisible = false };
        for (int m = 5; m <= Max; m += 5)
        {
            double x = (m - Min) * Step;
            var label = new TextBlock
            {
                Text = m.ToString(System.Globalization.CultureInfo.InvariantCulture), FontFamily = new FontFamily("Segoe UI Variable Text"), FontSize = 10.5,
                FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), Width = 32, TextAlignment = TextAlignment.Center,
            };
            Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
            Canvas.SetLeft(label, x - 16);
            Canvas.SetTop(label, LabelY);
            var fade = c.CreateExpressionAnimation("Clamp(1 - Pow(Abs(bx - p.x) / h, 2), 0, 1)");
            fade.Target = "Opacity";
            fade.SetReferenceParameter("p", _p);
            fade.SetScalarParameter("bx", (float)x); fade.SetScalarParameter("h", half);
            label.StartAnimation(fade);
            labels.Children.Add(label);
        }
        var slide = c.CreateExpressionAnimation("Vector3(c - p.x, 0, 0)");
        slide.Target = "Translation";
        slide.SetReferenceParameter("p", _p);
        slide.SetScalarParameter("c", centre);
        labels.StartAnimation(slide);
        _host.Children.Add(labels);

        // The line: fixed in the middle, in Tempo's colour; it pulses a little as each minute crosses it.
        _needle = new Border
        {
            Width = 3, Height = 32, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(ink),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, TickY - 16, 0, 0), IsHitTestVisible = false,
        };
        _needle.CenterPoint = new Vector3(1.5f, 16, 0);
        _host.Children.Add(_needle);
        _keep.Add(strip);

        Root = new ContentControl
        {
            Content = _host, IsTabStop = true, UseSystemFocusVisuals = true, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FocusVisualMargin = new Thickness(-2), FocusVisualPrimaryThickness = new Thickness(2), FocusVisualSecondaryThickness = new Thickness(0),
            FocusVisualPrimaryBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), CornerRadius = new CornerRadius(10),
        };
        AutomationProperties.SetName(Root, $"Timer length, {CountdownTimer.Words(TimeSpan.FromMinutes(value))}");
        AutomationProperties.SetHelpText(Root, "Drag, scroll, or use the arrow keys to choose the minutes. Enter starts the timer.");

        _host.PointerPressed += (_, e) =>
        {
            _host.CapturePointer(e.Pointer);
            _lastX = e.GetCurrentPoint(_host).Position.X;
            _travel = 0;
            _motion.Grab(Clock.Now);
            Root.Focus(FocusState.Pointer);
            Loop();
            e.Handled = true;   // a press on the ruler is for the ruler, not the island
        };
        _host.PointerMoved += (_, e) =>
        {
            if (!_motion.Dragging) return;
            double x = e.GetCurrentPoint(_host).Position.X, dx = x - _lastX;
            _lastX = x;
            _travel += Math.Abs(dx);
            _motion.Drag(dx, Clock.Now);
            Apply();
            e.Handled = true;
        };
        void Let(PointerRoutedEventArgs e)
        {
            if (!_motion.Dragging) return;
            double x = e.GetCurrentPoint(_host).Position.X;
            if (_travel < 4)
            {
                // A click, not a drag: go to the tick that was clicked.
                int tapped = Min + (int)Math.Round((_motion.Position + (x - _width / 2)) / Step);
                _motion.GoTo(tapped, Clock.Now);
            }
            else _motion.Release(Clock.Now);
            _host.ReleasePointerCaptures();
            Loop();
            e.Handled = true;
        }
        _host.PointerReleased += (_, e) => Let(e);
        _host.PointerCaptureLost += (_, e) => { if (_motion.Dragging) { _motion.Release(Clock.Now); Loop(); } };
        _host.PointerWheelChanged += (_, e) =>
        {
            var point = e.GetCurrentPoint(_host).Properties;
            int delta = point.MouseWheelDelta;
            if (point.IsHorizontalMouseWheel) delta = -delta;   // a touchpad's sideways swipe moves it the way the fingers go
            _wheel += delta;
            int steps = _wheel / 120;
            if (steps != 0) { _wheel -= steps * 120; _motion.Nudge(steps, Clock.Now); Loop(); }
            e.Handled = true;
        };
        Root.KeyDown += (_, e) =>
        {
            int? steps = e.Key switch
            {
                Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Up => 1,
                Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Down => -1,
                Windows.System.VirtualKey.PageUp => 5,
                Windows.System.VirtualKey.PageDown => -5,
                Windows.System.VirtualKey.Home => Min - _motion.Target,
                Windows.System.VirtualKey.End => Max - _motion.Target,
                _ => null,
            };
            if (steps is { } s) { _motion.Nudge(s, Clock.Now); Loop(); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.Enter) { Commit?.Invoke(); e.Handled = true; }
        };
    }

    int _wheel;

    /// <summary>Runs the strip on every frame until it comes to rest, then stops drawing.</summary>
    void Loop()
    {
        if (_looping) return;
        _looping = true;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!_looping) return;
        _looping = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, object e)
    {
        bool moving = _motion.Advance(Clock.Now);
        Apply();
        if (moving) return;
        Stop();
        Settled?.Invoke(_motion.Value);
    }

    void Apply()
    {
        _p.InsertScalar("x", (float)_motion.Position);
        int value = _motion.Value;
        if (value == _shown) return;
        int direction = Math.Sign(value - _shown);
        _shown = value;
        Pulse();
        Changed?.Invoke(value, direction);
    }

    /// <summary>The line's small give as a minute crosses it: 12% taller, back in 160 ms.</summary>
    void Pulse()
    {
        if (MotionPolicy.Reduced) return;
        var bump = _c.CreateVector3KeyFrameAnimation();
        bump.Target = "Scale";
        bump.InsertKeyFrame(0, new Vector3(1.25f, 1.12f, 1));
        bump.InsertKeyFrame(1, Vector3.One, _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f)));
        bump.Duration = TimeSpan.FromMilliseconds(160);
        _needle.StartAnimation(bump);
    }

    /// <summary>For the layout check: straight to a value, with no motion.</summary>
    public void Jump(int minutes)
    {
        var motion = _motion;
        motion.GoTo(minutes, 0);
        motion.Advance(1e6);
        Apply();
    }
}

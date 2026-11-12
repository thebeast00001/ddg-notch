using IslandSpike.Motion;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace IslandSpike;

/// <summary>
/// The two things the island says about a call. First a question, "Take notes on Chrome?", with Yes
/// and Not now. Then, if the answer was yes, a recording capsule that stays for the whole capture:
/// a steady red dot, the elapsed time, and Stop. Nothing is captured before Yes.
/// </summary>
sealed class CallView
{
    static readonly FontFamily Face = new("Segoe UI Variable");
    static readonly Windows.UI.Color White = Windows.UI.Color.FromArgb(255, 255, 255, 255);

    readonly Compositor _c;
    readonly Grid _ask, _recording, _ready;
    readonly TextBlock _question, _elapsed, _result;
    readonly MotionValue _askOpacity, _recordingOpacity, _readyOpacity;
    readonly DispatcherQueueTimer _clock;
    DateTime _since;
    int _version;

    public event Action? Accepted, Declined, StopRequested, OpenRequested;

    public CallView(Island island)
    {
        _c = island.Compositor;
        double top = Shape.Ask.Top + island.Win.TopInsetDip;

        // ── "Take notes on Chrome?"  [Yes] [Not now] ──
        _question = Text("", 13, FontWeights.SemiBold, 1.0);
        _question.TextTrimming = TextTrimming.CharacterEllipsis;
        _question.Margin = new Thickness(18, 0, 10, 0);
        var yes = Pill("Yes", filled: true, () => Accepted?.Invoke());
        var notNow = Pill("Not now", filled: false, () => Declined?.Invoke());
        notNow.Margin = new Thickness(6, 0, 8, 0);
        _ask = Row(Shape.Ask, top, _question, yes, notNow);

        // ── ● Notes 2:14  [Stop] ──
        var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x45, 0x3A)), Margin = new Thickness(16, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = Text("Taking notes", 13, FontWeights.SemiBold, 1.0);
        _elapsed = Text("0:00", 13, FontWeights.SemiBold, 0.6);
        _elapsed.Margin = new Thickness(8, 0, 8, 0);
        Typography.SetNumeralAlignment(_elapsed, FontNumeralAlignment.Tabular);   // digits that do not jiggle
        var stop = Pill("Stop", filled: false, () => StopRequested?.Invoke());
        stop.Margin = new Thickness(0, 0, 7, 0);
        _recording = new Grid
        {
            Width = Shape.Recording.W, Height = Shape.Recording.H, Margin = new Thickness(0, top, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
        };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            _recording.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        Place(_recording, dot, 0);
        Place(_recording, label, 1);
        Place(_recording, _elapsed, 2);
        Place(_recording, stop, 3);

        // ── Notes ready  3 to-dos  [Open] ──
        var readyLabel = Text("Notes ready", 13, FontWeights.SemiBold, 1.0);
        readyLabel.Margin = new Thickness(18, 0, 0, 0);
        _result = Text("", 13, FontWeights.SemiBold, 0.6);
        _result.Margin = new Thickness(8, 0, 8, 0);
        _result.TextTrimming = TextTrimming.CharacterEllipsis;
        var open = Pill("Open", filled: true, () => OpenRequested?.Invoke());
        open.Margin = new Thickness(0, 0, 7, 0);
        _ready = new Grid
        {
            Width = Shape.Ready.W, Height = Shape.Ready.H, Margin = new Thickness(0, top, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
        };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            _ready.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        Place(_ready, readyLabel, 0);
        Place(_ready, _result, 1);
        Place(_ready, open, 2);

        island.Win.Overlay.Children.Add(_ask);
        island.Win.Overlay.Children.Add(_recording);
        island.Win.Overlay.Children.Add(_ready);
        _askOpacity = Fade(_ask);
        _recordingOpacity = Fade(_recording);
        _readyOpacity = Fade(_ready);

        _clock = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => ShowElapsed();
    }

    public bool AskVisible => _ask.Visibility == Visibility.Visible;
    public bool RecordingVisible => _recording.Visibility == Visibility.Visible;
    public bool ReadyVisible => _readyOpacity.Target > 0.5;

    public void ShowAsk(string app)
    {
        _question.Text = $"Take notes on {app}?";
        Swap(_ask);
    }

    public void ShowRecording()
    {
        _since = DateTime.UtcNow;
        ShowElapsed();
        _clock.Start();
        Swap(_recording);
    }

    /// <summary>The notes are saved. <paramref name="result"/> is what they hold, such as "3 to-dos".</summary>
    public void ShowReady(string result)
    {
        _result.Text = result;
        Swap(_ready);
    }

    public void Hide() => Swap(null);

    void ShowElapsed()
    {
        var span = DateTime.UtcNow - _since;
        _elapsed.Text = $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    void Swap(Grid? show)
    {
        int version = ++_version;
        double now = Clock.Now;
        foreach (var (panel, opacity) in new[] { (_ask, _askOpacity), (_recording, _recordingOpacity), (_ready, _readyOpacity) })
        {
            if (panel == show)
            {
                panel.Visibility = Visibility.Visible;
                opacity.Retarget(1, Spring.Quick, now);
            }
            else if (panel.Visibility == Visibility.Visible)
            {
                opacity.Retarget(0, Spring.Quick, now);
                _ = CollapseLater(panel, version);
            }
        }
        if (show != _recording) _clock.Stop();
    }

    async Task CollapseLater(Grid panel, int version)
    {
        await Task.Delay(260);
        if (panel != ShownPanel()) panel.Visibility = Visibility.Collapsed;   // out of the way of clicks once faded
    }

    Grid? ShownPanel() => _askOpacity.Target > 0.5 ? _ask : _recordingOpacity.Target > 0.5 ? _recording : _readyOpacity.Target > 0.5 ? _ready : null;

    MotionValue Fade(UIElement element)
    {
        var opacity = new AdditiveMotionValue(_c, 0);
        var bind = _c.CreateExpressionAnimation("Clamp(o.v, 0, 1)");
        bind.SetReferenceParameter("o", opacity.Props);
        ElementCompositionPreview.GetElementVisual(element).StartAnimation("Opacity", bind);
        return opacity;
    }

    static Grid Row(Shape shape, double top, TextBlock text, FrameworkElement first, FrameworkElement second)
    {
        var row = new Grid
        {
            Width = shape.W, Height = shape.H, Margin = new Thickness(0, top, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(row, text, 0);
        Place(row, first, 1);
        Place(row, second, 2);
        return row;
    }

    static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    static TextBlock Text(string text, double size, Windows.UI.Text.FontWeight weight, double opacity) => new()
    {
        Text = text, FontFamily = Face, FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(255 * opacity), 255, 255, 255)),
    };

    /// <summary>A small rounded button. It answers on press, and marks the press handled so the island behind it does not also react.</summary>
    static Border Pill(string text, bool filled, Action pressed)
    {
        var rest = new SolidColorBrush(filled ? White : Windows.UI.Color.FromArgb(36, 255, 255, 255));
        var hover = new SolidColorBrush(filled ? Windows.UI.Color.FromArgb(255, 225, 225, 225) : Windows.UI.Color.FromArgb(64, 255, 255, 255));
        var pill = new Border
        {
            Height = 26, CornerRadius = new CornerRadius(13), Padding = new Thickness(12, 0, 12, 0), Background = rest,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text, FontFamily = Face, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(filled ? Colors.Black : White),
            },
        };
        pill.PointerEntered += (_, _) => pill.Background = hover;
        pill.PointerExited += (_, _) => pill.Background = rest;
        pill.PointerPressed += (_, e) => { e.Handled = true; pressed(); };
        return pill;
    }
}
